// SPDX-License-Identifier: GPL-3.0-or-later
// 
// PCCCComm - PCCC Communication Library for .NET
// Copyright (c) 2026 Ketut Kumajaya
// 
// Based on original DF1Comm.vb by Archie Jacobs (Manufacturing Automation LLC)
// which was released under GPLv2-or-later.
// 
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Buffers;
using PCCCComm.Core;
using PCCCComm.Pccc;

namespace PCCCComm.Handlers;

/// <summary>
/// PCCC protocol handler for SLC 500 and MicroLogix families.
/// Implements Protected Typed Logical Read/Write (FNC 0xA1/0xA2, 0xAA) and
/// SLC-specific upload/download procedures.
/// 
/// Reference: Allen‑Bradley Publication 1770‑6.5.16 (DF1 Protocol and Command Set)
/// </summary>
public class SlcHandler : IPlcHandler
{
    // ─── Fields ─────────────────────────────────────────────────────────────
    private readonly IHandlerContext _context;
    private readonly PCCCProtocol _protocol;
    private int _processorType;
    private readonly ConcurrentDictionary<string, DataAddress> _addressCache = new ConcurrentDictionary<string, DataAddress>();
    private int _lastFileProgressPercent = -1;

    // ─── Constructor ───────────────────────────────────────────────────────
    public SlcHandler(IHandlerContext context, PCCCProtocol protocol, int initialProcessorType)
    {
        _context = context;
        _protocol = protocol;
        _processorType = initialProcessorType;
    }

    // ─── Helper Properties (expose parent settings) ────────────────────────
    private int MyNode => _context.MyNode;
    private int TargetNode => _context.TargetNode;
    private bool AsyncMode => _context.AsyncMode;
    
    private bool DisableEventFlag
    {
        get => _context.DisableEvent;
        set => _context.DisableEvent = value;
    }
    
    private void OnFileProgress(PCCCComm.FileProgressEventArgs e)
    {
        // Check for cancellation at each file boundary — natural checkpoint
        // during long upload/download operations.
        _context.CancellationToken.ThrowIfCancellationRequested();

        // When TotalFiles is known, raise every file completion — file count is the
        // natural progress unit for ML1400 where byte totals span unknown-size phases.
        // When GrandTotalBytes is unknown (=0), also raise every event without throttling.
        // Otherwise throttle to every 5% change to avoid flooding callers.
        if (e.TotalFiles > 0 || e.GrandTotalBytes == 0)
        {
            _context.RaiseFileProgress(e);
            return;
        }

        int percent = (int)((double)e.TotalBytesTransferred / e.GrandTotalBytes * 100);
        if (percent != _lastFileProgressPercent && (percent % 5 == 0 || percent == 100))
        {
            _lastFileProgressPercent = percent;
            _context.RaiseFileProgress(e);
        }
    }

    private DataAddress ParseAddress(string address) => _addressCache.GetOrAdd(address, PCCCParser.Parse);

    // ─── Private Helper Methods ────────────────────────────────────────────

    private bool IsMicroLogixFamily => _processorType switch
    {
        (byte)PCCCConstants.ProcessorTypeCode.ML1000 or
        (byte)PCCCConstants.ProcessorTypeCode.ML1100 or
        (byte)PCCCConstants.ProcessorTypeCode.ML1200 or
        (byte)PCCCConstants.ProcessorTypeCode.ML1500LSP or
        (byte)PCCCConstants.ProcessorTypeCode.ML1500LRP or
        (byte)PCCCConstants.ProcessorTypeCode.ML1400 => true,
        _ => false
    };

    private bool IsMicroLogix1000 => _processorType == (byte)PCCCConstants.ProcessorTypeCode.ML1000;
    private bool IsMicroLogix1400 => _processorType == (byte)PCCCConstants.ProcessorTypeCode.ML1400;
    
    /// <summary>
    /// Reads raw data from the PLC with automatic chunking.
    /// Handles DF1 protocol's maximum payload limits and processor-specific restrictions.
    /// Reference: AB 1770-6.5.16, Chapter 7 (Protected Typed Logical Read)
    /// </summary>
    private byte[] ReadRawDataWithChunking(ref DataAddress addr, int numberOfBytes, out int finalStatus)
    {
        finalStatus = 0;
        int filePosition = 0;
        byte[] result = ArrayPool<byte>.Shared.Rent(numberOfBytes);
        try
        {
            // Determine processor-specific maximum read chunk size (no magic numbers)
            int maxReadChunk;
            switch (_processorType)
            {
                case (byte)PCCCConstants.ProcessorTypeCode.SLC502:
                case (byte)PCCCConstants.ProcessorTypeCode.SLC501:
                case (byte)PCCCConstants.ProcessorTypeCode.FixedSLC500:
                    maxReadChunk = PCCCConstants.Df1Limits.MaxReadPayloadSlc501_502; // 95 bytes
                    break;

                case (byte)PCCCConstants.ProcessorTypeCode.SLC503:
                case (byte)PCCCConstants.ProcessorTypeCode.SLC504:
                case (byte)PCCCConstants.ProcessorTypeCode.SLC505:
                    maxReadChunk = PCCCConstants.Df1Limits.MaxReadPayloadSlc503_504; // 236 bytes
                    break;

                case (byte)PCCCConstants.ProcessorTypeCode.ML1000:
                    maxReadChunk = PCCCConstants.Df1Limits.MaxReadPayloadMl1000; // 95 bytes
                    break;

                default:
                    // For other processors (ML1100, ML1200, ML1500, etc.) use default 236
                    maxReadChunk = PCCCConstants.Df1Limits.MaxReadPayloadBytes;
                    break;
            }

            while (filePosition < numberOfBytes && finalStatus == 0)
            {
                int toRead = Math.Min(numberOfBytes - filePosition, maxReadChunk);
                
                // String file (ST) restriction: max 168 bytes (two elements) per read
                if (toRead > PCCCConstants.Df1Limits.MaxStringReadBytes && addr.FileType == (byte)PCCCConstants.SlcFileTypeCode.String)
                    toRead = PCCCConstants.Df1Limits.MaxStringReadBytes;
                
                // Timer/Counter/Control file restriction: read in multiples of 6 bytes,
                // max 234 bytes. All three share the same 3-word (6-byte) element layout
                // (control/status word + 2 sub-elements: PRE/ACC or LEN/POS) — Control
                // was missing here even though it needs identical treatment: the generic
                // chunk limit (236 bytes) is NOT a multiple of 6, so a large multi-element
                // Control read (more than 39 elements merged into one physical read) could
                // have its chunk boundary fall mid-element, silently misaligning every
                // subsequent element's word offset for the rest of the read.
                if (toRead > PCCCConstants.Df1Limits.MaxTimerCounterReadBytes &&
                    (addr.FileType == (byte)PCCCConstants.SlcFileTypeCode.Timer ||
                     addr.FileType == (byte)PCCCConstants.SlcFileTypeCode.Counter ||
                     addr.FileType == (byte)PCCCConstants.SlcFileTypeCode.Control))
                    toRead = PCCCConstants.Df1Limits.MaxTimerCounterReadBytes;
                
                // SLC 5/02 additional limitation: max 0x50 (80) bytes per read
                if (toRead > PCCCConstants.Df1Limits.MaxSlc502ReadBytes && _processorType == (byte)PCCCConstants.ProcessorTypeCode.SLC502)
                    toRead = PCCCConstants.Df1Limits.MaxSlc502ReadBytes;
                
                // Data Monitor File (type 0xA4) limitation: max 0x78 (120) bytes per read
                if (addr.FileType == (byte)PCCCConstants.SlcFileTypeCode.DataMonitor && toRead > PCCCConstants.Df1Limits.MaxDataMonitorReadBytes)
                    toRead = PCCCConstants.Df1Limits.MaxDataMonitorReadBytes;
                
                if (toRead <= 0) break;

                var req = PCCCMessage.CreateReadRequest(addr, toRead, 0, (byte)MyNode, (byte)TargetNode);
                var reply = _protocol.SendRequest(req, out int sts);
                if (sts != PCCCConstants.Sts.Success || reply?.Data == null)
                {
                    finalStatus = sts;
                    break;
                }

                int bytesRead = Math.Min(toRead, reply.Data.Length);
                Array.Copy(reply.Data, 0, result, filePosition, bytesRead);
                filePosition += bytesRead;

                // Advance address pointer for next chunk
                const byte stringFileType = (byte)PCCCConstants.SlcFileTypeCode.String;
                const int stringElementBytes = PCCCConstants.Df1Limits.SlcStringElementBytes;

                if (addr.FileType == stringFileType)
                    addr.Element += toRead / stringElementBytes;
                else if (addr.FileType == (byte)PCCCConstants.SlcFileTypeCode.DataMonitor)
                    addr.Element += toRead / PCCCConstants.Df1Limits.DataMonitorElementBytes;
                else
                    // For word-based files, each word is 2 bytes
                    addr.SubElement += toRead / PCCCConstants.Df1Limits.BytesPerWord;
            }

            // Return a properly sized copy
            byte[] finalResult = new byte[filePosition];
            Array.Copy(result, 0, finalResult, 0, filePosition);
            return finalResult;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(result);
        }
    }

    /// <summary>
    /// Writes raw data to the PLC with automatic chunking.
    /// Handles DF1 protocol's maximum write payload limits based on processor type.
    /// Reference: AB 1770-6.5.16, Chapter 7 (Protected Typed Logical Write)
    /// </summary>
    private int WriteRawDataWithChunking(DataAddress addr, byte[] dataToWrite)
    {
        if (addr.FileType == 0) return -5;
        int filePosition = 0;
        int reply = 0;

        // Determine maximum write chunk size based on processor type (no magic numbers)
        int maxWriteChunk;
        switch (_processorType)
        {
            case (byte)PCCCConstants.ProcessorTypeCode.SLC502:
            case (byte)PCCCConstants.ProcessorTypeCode.SLC501:
            case (byte)PCCCConstants.ProcessorTypeCode.FixedSLC500:
                // SLC 5/01 and 5/02: max 82 bytes (41 words)
                maxWriteChunk = PCCCConstants.Df1Limits.MaxWritePayloadSlc501_502;
                break;

            case (byte)PCCCConstants.ProcessorTypeCode.SLC503:
            case (byte)PCCCConstants.ProcessorTypeCode.SLC504:
            case (byte)PCCCConstants.ProcessorTypeCode.SLC505:
                // SLC 5/03, 5/04, 5/05: max 234 bytes (without internet protocol)
                maxWriteChunk = PCCCConstants.Df1Limits.MaxWritePayloadSlc503_504;
                break;

            case (byte)PCCCConstants.ProcessorTypeCode.ML1000:
                // MicroLogix 1000: max 89 bytes
                maxWriteChunk = PCCCConstants.Df1Limits.MaxWritePayloadMl1000;
                break;

            default:
                // For other processors (ML1100, ML1200, ML1500, etc.) use default 164
                maxWriteChunk = PCCCConstants.Df1Limits.MaxWritePayloadBytes;
                break;
        }

        while (filePosition < dataToWrite.Length && reply == 0)
        {
            int toWrite = Math.Min(dataToWrite.Length - filePosition, maxWriteChunk);
            
            // Special case for file types >= 0xA1 (including Data Monitor File 0xA4) – limit to 120 bytes
            if (addr.FileType >= PCCCConstants.Df1Limits.MinFileTypeForExtendedLimit && 
                toWrite > PCCCConstants.Df1Limits.MaxDataMonitorWriteBytes) 
                toWrite = PCCCConstants.Df1Limits.MaxDataMonitorWriteBytes;
            
            var req = PCCCMessage.CreateWriteRequest(addr, dataToWrite, filePosition, toWrite, 0, (byte)MyNode, (byte)TargetNode);
            
            if (AsyncMode)
            {
                _protocol.SendRequestAsync(req);
                reply = 0;
                filePosition += toWrite;
            }
            else
            {
                var resp = _protocol.SendRequest(req, out int sts);
                reply = sts;
                filePosition += toWrite;
            }

            // Advance address pointer for next chunk
            const byte stringFileType = (byte)PCCCConstants.SlcFileTypeCode.String;
            const int stringElementBytes = PCCCConstants.Df1Limits.SlcStringElementBytes;

            if (addr.FileType == stringFileType)
                addr.Element += toWrite / stringElementBytes;
            else if (addr.FileType == (byte)PCCCConstants.SlcFileTypeCode.DataMonitor)
                addr.Element += toWrite / PCCCConstants.Df1Limits.DataMonitorElementBytes;
            else
                // For word-based files, each word is 2 bytes
                addr.SubElement += toWrite / PCCCConstants.Df1Limits.BytesPerWord;
        }
        if (reply == 0) return 0;
        throw new PCCCException(PCCCErrors.DecodeStatus(reply));
    }

    /// <summary>
    /// Reads the file directory (File 0) from the processor.
    /// This directory contains metadata about all program and data files.
    /// 
    /// Original method from DF1Comm.vb (ReadFileDirectory).
    /// The offset and file type vary by processor type:
    ///   - SLC 5/02 and ML1000: file type 0, element 0x23
    ///   - ML1100/1200/1500: file type 2, element 0x2F
    ///   - Others (SLC 5/03+): file type 1, element 0x23
    /// 
    /// Reference: AB Publication 1770-6.5.16, Chapter 10 (File Directory structure)
    /// </summary>
    /// <returns>Raw directory data bytes</returns>
    private byte[] ReadFileDirectory()
    {
        // Ensure processor type is known
        if (_processorType == 0)
            _processorType = GetProcessorType();
        
        var pAddr = new DataAddress();
        switch (_processorType)
        {
            case (byte)PCCCConstants.ProcessorTypeCode.SLC502:
            case (byte)PCCCConstants.ProcessorTypeCode.ML1000:
                pAddr.FileType = 0;
                pAddr.Element = 0x23;
                break;
            case (byte)PCCCConstants.ProcessorTypeCode.ML1200:
            case (byte)PCCCConstants.ProcessorTypeCode.ML1500LSP:
            case (byte)PCCCConstants.ProcessorTypeCode.ML1500LRP:
            case (byte)PCCCConstants.ProcessorTypeCode.ML1100:
                pAddr.FileType = 2;
                pAddr.Element = 0x2F;
                break;
            default:
                pAddr.FileType = 1;
                pAddr.Element = 0x23;
                break;
        }

        // Step 1: Read the directory size (2 bytes at the specified offset)
        byte[] data = ReadRawDataWithChunking(ref pAddr, 2, out int reply);
        if (reply != 0) throw new PCCCException("Failed to Get Program Directory Size - " + PCCCErrors.DecodeStatus(reply));

        // Step 2: Read the entire directory using the size obtained above
        pAddr.Element = 0;
        pAddr.SubElement = 0;
        int size = data[0] + data[1] * 256;
        byte[] fzd = ReadRawDataWithChunking(ref pAddr, size, out reply);
        if (reply != 0) throw new PCCCException("Failed to Get Program Directory - " + PCCCErrors.DecodeStatus(reply));
        return fzd;
    }

    /// <summary>
    /// Converts a file type code to bytes per element and human-readable type name.
    /// </summary>
    private static int FileTypeToBytesPerElement(byte code, out string fileTypeStr)
    {
        var type = (PCCCConstants.SlcFileTypeCode)code;
        fileTypeStr = PCCCConstants.SlcFileTypeInfo.GetTypeName(type);
        return PCCCConstants.SlcFileTypeInfo.GetBytesPerElement(type);
    }

    /// <summary>
    /// Determines whether the processor supports file-based transfer (OpenFile/FileRead/FileWrite).
    /// Supported processors: SLC 5/03, 5/04, 5/05, MicroLogix 1100, 1200, 1500.
    /// 
    /// For unsupported processors (SLC 5/01, 5/02, ML1000), physical-based upload/download is used.
    /// 
    /// Reference: AB Publication 1770-6.5.16, Chapter 12 (Upload/Download procedures)
    /// </summary>
    private bool SupportsFileBasedTransfer()
    {
        return _processorType == (byte)PCCCConstants.ProcessorTypeCode.SLC503 ||
               _processorType == (byte)PCCCConstants.ProcessorTypeCode.SLC504 ||
               _processorType == (byte)PCCCConstants.ProcessorTypeCode.SLC505 ||
               _processorType == (byte)PCCCConstants.ProcessorTypeCode.ML1100 ||
               _processorType == (byte)PCCCConstants.ProcessorTypeCode.ML1200 ||
               _processorType == (byte)PCCCConstants.ProcessorTypeCode.ML1500LSP ||
               _processorType == (byte)PCCCConstants.ProcessorTypeCode.ML1500LRP;
    }

    // ─── Public API Implementation ─────────────────────────────────────────
    
    /// <summary>
    /// Gets the processor type code.
    /// Returns the processor type code (e.g., 0x49 for SLC 5/03, 0x5B for SLC 5/04, 0x58 for ML1000).
    /// 
    /// Original method from DF1Comm.vb (GetProcessorType).
    /// </summary>
    public int GetProcessorType()
    {
        _processorType = _protocol.GetProcessorType((byte)MyNode, (byte)TargetNode);
        return _processorType;
    }

    /// <summary>
    /// Returns raw diagnostic status data (24 bytes) from the processor.
    /// This data includes processor type, mode, catalog string, and RAM size.
    /// 
    /// Reference: AB Publication 1770-6.5.16, Chapter 10
    /// </summary>
    public byte[]? GetDiagnosticStatusRaw()
    {
        var req = PCCCMessage.CreateDiagnosticStatusRequest(0, (byte)MyNode, (byte)TargetNode);
        var reply = _protocol.SendRequest(req, out int sts);
        if (sts != PCCCConstants.Sts.Success || reply?.Data == null)
            return null;
        return reply.Data;
    }

    /// <summary>
    /// Places the processor in Run mode.
    /// For MicroLogix 1000: uses FNC 0x3A with mode value 2.
    /// For other SLC/ML processors: uses FNC 0x80 with mode value 6 (Remote Run).
    /// 
    /// Original method from DF1Comm.vb (SetRunMode).
    /// 
    /// Reference: AB Publication 1770-6.5.16, page 7-5 (Change Mode) and page 7-26 (Set CPU Mode)
    /// </summary>
    public void SetRunMode()
    {
        byte modeValue;
        bool useFnc3A; // true = FNC 0x3A, false = FNC 0x80

        if (IsMicroLogixFamily)
        {
            useFnc3A = true;
            modeValue = 0x02; // Remote Run for all MicroLogix
        }
        else
        {
            useFnc3A = false;
            modeValue = 0x06; // Remote Run for SLC
        }

        var req = PCCCMessage.CreateChangeModeRequest(modeValue, useFnc3A, 0, (byte)MyNode, (byte)TargetNode);
        _protocol.SendRequest(req, out int sts);
        if (sts != PCCCConstants.Sts.Success)
            throw new PCCCException($"SetRunMode failed: {PCCCErrors.DecodeStatus(sts)}");
    }

    /// <summary>
    /// Places the processor in Program mode.
    /// For MicroLogix 1000: uses FNC 0x3A with mode value 0.
    /// For other SLC/ML processors: uses FNC 0x80 with mode value 1 (Remote Program).
    /// 
    /// Original method from DF1Comm.vb (SetProgramMode).
    /// </summary>
    public void SetProgramMode()
    {
        byte modeValue;
        bool useFnc3A;

        if (IsMicroLogix1000 || IsMicroLogix1400)
        {
            useFnc3A = true;
            modeValue = 0x00; // Program/Load for ML1000 (local program)
        }
        else if (IsMicroLogixFamily)
        {
            useFnc3A = true;
            modeValue = 0x01; // Remote Program for ML1100/1200/1500
        }
        else
        {
            useFnc3A = false;
            modeValue = 0x01; // Remote Program for SLC
        }

        var req = PCCCMessage.CreateChangeModeRequest(modeValue, useFnc3A, 0, (byte)MyNode, (byte)TargetNode);
        _protocol.SendRequest(req, out int sts);
        if (sts != PCCCConstants.Sts.Success)
            throw new PCCCException($"SetProgramMode failed: {PCCCErrors.DecodeStatus(sts)}");
    }

    /// <summary>
    /// Sets the CPU mode using a raw mode value via FNC 0x80 (Change Mode).
    /// </summary>
    public int SetCpuMode(byte modeValue)
    {
        var req = PCCCMessage.CreateChangeModeRequest(modeValue, false, 0, (byte)MyNode, (byte)TargetNode);
        _protocol.SendRequest(req, out int sts);
        return sts;
    }

    /// <summary>
    /// Returns 1 if the processor is in Run mode, 0 otherwise.
    /// Reads diagnostic status and checks byte 18 (mode code).
    /// Run mode codes: SLC 0x06 (Remote Run) or 0x1E (Local Run);
    /// MicroLogix 0x02 (Run).
    /// 
    /// Reference: AB Publication 1770-6.5.16, Chapter 10 (Status Bytes)
    /// </summary>
    public int GetRunMode()
    {
        var req = PCCCMessage.CreateDiagnosticStatusRequest(0, (byte)MyNode, (byte)TargetNode);
        var reply = _protocol.SendRequest(req, out int sts);
        if (sts != PCCCConstants.Sts.Success || reply?.Data == null ||
            reply.Data.Length <= PCCCConstants.ResponseOffsets.DiagnosticStatus.ModeCode)
            return -1;

        byte modeCode = reply.Data[PCCCConstants.ResponseOffsets.DiagnosticStatus.ModeCode];

        int result;
        if (IsMicroLogix1400)
            result = (modeCode & 0x01) == 0 ? 1 : 0;
        else if (IsMicroLogixFamily)
            result = modeCode == 0x02 ? 1 : 0;
        else
            result = (modeCode == 0x06 || modeCode == 0x1E) ? 1 : 0;

        return result;
    }

    /// <summary>Disables forces on the processor (CMD=0x0F, FNC=0x41).</summary>
    public int DisableForces()
    {
        var req = PCCCMessage.CreateDisableForcesRequest(0, (byte)MyNode, (byte)TargetNode);
        _protocol.SendRequest(req, out int sts);
        return sts;
    }

    /// <summary>Enables forces on the processor (CMD=0x0F, FNC=0x42).</summary>
    public void EnableForces()
    {
        _protocol.EnableForces((byte)MyNode, (byte)TargetNode);
    }

    /// <summary>Clears all forces on the processor (CMD=0x0F, FNC=0x43).</summary>
    public void ClearForces()
    {
        _protocol.ClearForces((byte)MyNode, (byte)TargetNode);
    }

    // ─── Raw Word API (Modbus-style) ───────────────────────────────────────

    /// <summary>
    /// Reads raw 16-bit words from the specified PCCC address.
    /// This is the primary read API. No interpretation is performed.
    /// For String (ST) files, throws NotSupportedException.
    /// </summary>
    public ushort[] ReadWords(string startAddress, int numberOfWords)
    {
        DataAddress p = ParseAddress(startAddress);
        if (p.FileType == 0) throw new PCCCException("Invalid Address");
        if (p.FileType == (byte)PCCCConstants.SlcFileTypeCode.String)
            throw new NotSupportedException("ReadWords does not support String (ST) files. Use ReadAny instead.");

        int bytesPerElem = p.BytesPerElements;
        if (p.SubElement > 0 && (p.FileType == (byte)PCCCConstants.SlcFileTypeCode.Timer || p.FileType == (byte)PCCCConstants.SlcFileTypeCode.Counter))
            bytesPerElem = PCCCConstants.Df1Limits.BytesPerWord;

        int totalBytesNeeded = numberOfWords * 2;
        int numberOfElements = (totalBytesNeeded + bytesPerElem - 1) / bytesPerElem;
        int numberOfBytesToRead = numberOfElements * bytesPerElem;

        if (p.SubElement > 0 && (p.FileType == (byte)PCCCConstants.SlcFileTypeCode.Timer || p.FileType == (byte)PCCCConstants.SlcFileTypeCode.Counter))
            numberOfBytesToRead = (numberOfBytesToRead * 3) - 4;

        byte[] returnedData = ReadRawDataWithChunking(ref p, numberOfBytesToRead, out int reply);
        if (reply != 0)
            throw new PCCCException(PCCCErrors.DecodeStatus(reply));

        int wordCount = Math.Min(numberOfWords, returnedData.Length / 2);
        ushort[] result = new ushort[wordCount];
        // Use MemoryMarshal for fast conversion without extra allocation
        Span<byte> byteSpan = returnedData.AsSpan(0, wordCount * 2);
        Span<ushort> wordSpan = MemoryMarshal.Cast<byte, ushort>(byteSpan);
        wordSpan.CopyTo(result);
        return result;
    }

    /// <summary>
    /// Writes raw 16-bit words to the specified PCCC address.
    /// This is the primary write API. No interpretation is performed.
    /// </summary>
    public void WriteWords(string startAddress, ushort[] data)
    {
        if (data == null || data.Length == 0) return;
        DataAddress p = ParseAddress(startAddress);
        if (p.FileType == 0) throw new PCCCException("Invalid Address");
        if (p.FileType == (byte)PCCCConstants.SlcFileTypeCode.String)
            throw new PCCCException("Use WriteData(string, string) for ST files.");

        byte[] byteData = new byte[data.Length * 2];
        for (int i = 0; i < data.Length; i++)
        {
            byteData[i * 2] = (byte)(data[i] & 0xFF);
            byteData[i * 2 + 1] = (byte)((data[i] >> 8) & 0xFF);
        }
        int status = WriteRawDataWithChunking(p, byteData);
        if (status != 0)
            throw new PCCCException(PCCCErrors.DecodeStatus(status));
    }

    /// <summary>
    /// Reads String (ST) files and returns the decoded strings.
    /// </summary>
    private string[] ReadAnyString(string startAddress, int numberOfElements)
    {
        DataAddress p = ParseAddress(startAddress);
        if (p.FileType == 0) throw new PCCCException("Invalid Address");

        short arrayElements = (short)(numberOfElements - 1);
        if (arrayElements < 0) arrayElements = 0;

        int bytesPerElem = PCCCConstants.Df1Limits.SlcStringElementBytes;
        int numberOfBytes = (arrayElements + 1) * bytesPerElem;

        byte[] returnedData = ReadRawDataWithChunking(ref p, numberOfBytes, out int reply);
        if (reply != 0)
            throw new PCCCException(PCCCErrors.DecodeStatus(reply));

        string[] result = new string[arrayElements + 1];
        for (int i = 0; i <= arrayElements; i++)
        {
            int baseOffset = i * bytesPerElem;
            int strLen = BitConverter.ToInt16(returnedData, baseOffset);
            if (strLen > PCCCConstants.Df1Limits.MaxStringLength)
                strLen = PCCCConstants.Df1Limits.MaxStringLength;
            var sb = new StringBuilder();
            for (int j = 0; j < strLen; j++)
            {
                int wordOffset = baseOffset + 2 + (j / 2) * 2;
                char c = (j % 2 == 0)
                    ? (char)returnedData[wordOffset + 1]  // even index = high byte
                    : (char)returnedData[wordOffset];     // odd index = low byte
                if (c == 0) break;
                sb.Append(c);
            }
            result[i] = sb.ToString();
        }
        return result;
    }

    // ─── Read/Write Operations ─────────────────────────────────────────────

    /// <summary>
    /// Reads data from the specified address and returns it as strings.
    /// Supports integer, float, string, timer/counter, long, message, and bit-level addressing.
    /// 
    /// Original method from DF1Comm.vb (ReadAny).
    /// 
    /// Example addresses:
    ///   "N7:0"        – read integer at N7:0
    ///   "F8:0"        – read float at F8:0
    ///   "ST9:0"       – read string at ST9:0
    ///   "T4:0.ACC"    – read timer accumulator
    ///   "B3:0/5"      – read bit 5 of B3:0
    /// </summary>
    public string[] ReadAny(string startAddress, int numberOfElements)
    {
        DataAddress p = ParseAddress(startAddress);
        if (p.FileType == 0) throw new PCCCException("Invalid Address");

        if (p.FileType == (byte)PCCCConstants.SlcFileTypeCode.String)
            return ReadAnyString(startAddress, numberOfElements);

        int bytesPerElem = p.BytesPerElements;
        if (p.SubElement > 0 && (p.FileType == (byte)PCCCConstants.SlcFileTypeCode.Timer || p.FileType == (byte)PCCCConstants.SlcFileTypeCode.Counter))
            bytesPerElem = PCCCConstants.Df1Limits.BytesPerWord;

        int wordsPerElem = bytesPerElem / 2;
        int totalWords = numberOfElements * wordsPerElem;
        ushort[] rawWords = ReadWords(startAddress, totalWords);

        string[] result = new string[numberOfElements];
        for (int i = 0; i < numberOfElements; i++)
        {
            int offset = i * wordsPerElem;
            switch (p.FileType)
            {
                case (byte)PCCCConstants.SlcFileTypeCode.Float:
                    result[i] = WordConverter.WordsToFloat(rawWords[offset], rawWords[offset + 1])
                        .ToString(CultureInfo.InvariantCulture);
                    break;
                case (byte)PCCCConstants.SlcFileTypeCode.Long:
                    result[i] = WordConverter.WordsToInt32(rawWords[offset], rawWords[offset + 1])
                        .ToString(CultureInfo.InvariantCulture);
                    break;
                case (byte)PCCCConstants.SlcFileTypeCode.Timer:
                case (byte)PCCCConstants.SlcFileTypeCode.Counter:
                    result[i] = ((short)rawWords[offset]).ToString(CultureInfo.InvariantCulture);
                    break;
                default:
                    result[i] = ((short)rawWords[offset]).ToString(CultureInfo.InvariantCulture);
                    break;
            }
        }

        if (p.BitNumber >= 0 && p.BitNumber < 16)
        {
            string[] bitResult = new string[numberOfElements];
            int bitPos = p.BitNumber, wordPos = 0;
            for (int i = 0; i < numberOfElements; i++)
            {
                int wordVal = int.Parse(result[wordPos], CultureInfo.InvariantCulture);
                bitResult[i] = ((wordVal & (1 << bitPos)) != 0).ToString(CultureInfo.InvariantCulture);
                if (++bitPos > 15) { bitPos = 0; wordPos++; }
            }
            return bitResult;
        }
        return result;
    }

    /// <summary>Reads a single element from the specified address.</summary>
    public string ReadAny(string startAddress) => ReadAny(startAddress, 1)[0];

    /// <summary>Reads integer values from the specified address.</summary>
    public int[] ReadInt(string startAddress, int numberOfElements)
    {
        string[] result = ReadAny(startAddress, numberOfElements);
        int[] ints = new int[result.Length];
        for (int i = 0; i < result.Length; i++) ints[i] = int.Parse(result[i], CultureInfo.InvariantCulture);
        return ints;
    }

    /// <summary>
    /// Reads numeric data from the specified address and returns raw values as doubles.
    /// This method is an exact replication of <see cref="ReadAny(string, int)"/> logic,
    /// but it converts raw PLC data directly to double without intermediate string allocation.
    /// It supports integer, float, long, timer/counter, and bit-level addresses.
    /// </summary>
    /// <param name="startAddress">PCCC address (e.g., "N7:0", "F8:0", "T4:0.ACC", "B3:0/5").</param>
    /// <param name="numberOfElements">Number of elements to read.</param>
    /// <returns>Array of double values.</returns>
    /// <exception cref="PCCCException">Thrown on invalid address or communication error.</exception>
    public double[] ReadAnyValues(string startAddress, int numberOfElements)
    {
        DataAddress p = ParseAddress(startAddress);
        if (p.FileType == 0) throw new PCCCException("Invalid Address");
        if (p.FileType == (byte)PCCCConstants.SlcFileTypeCode.String)
            throw new NotSupportedException("ReadAnyValues does not support String (ST) files. Use ReadAny instead.");

        int bytesPerElem = p.BytesPerElements;
        if (p.SubElement > 0 && (p.FileType == (byte)PCCCConstants.SlcFileTypeCode.Timer || p.FileType == (byte)PCCCConstants.SlcFileTypeCode.Counter))
            bytesPerElem = PCCCConstants.Df1Limits.BytesPerWord;

        int wordsPerElem = bytesPerElem / 2;
        int totalWords = numberOfElements * wordsPerElem;
        ushort[] rawWords = ReadWords(startAddress, totalWords);

        double[] result = new double[numberOfElements];
        for (int i = 0; i < numberOfElements; i++)
        {
            int offset = i * wordsPerElem;
            switch (p.FileType)
            {
                case (byte)PCCCConstants.SlcFileTypeCode.Float:
                    result[i] = WordConverter.WordsToFloat(rawWords[offset], rawWords[offset + 1]);
                    break;
                case (byte)PCCCConstants.SlcFileTypeCode.Long:
                    result[i] = WordConverter.WordsToInt32(rawWords[offset], rawWords[offset + 1]);
                    break;
                case (byte)PCCCConstants.SlcFileTypeCode.Timer:
                case (byte)PCCCConstants.SlcFileTypeCode.Counter:
                    result[i] = (short)rawWords[offset];
                    break;
                default:
                    result[i] = (short)rawWords[offset];
                    break;
            }
        }

        if (p.BitNumber >= 0 && p.BitNumber < 16)
        {
            double[] bitResult = new double[numberOfElements];
            int bitPos = p.BitNumber, wordPos = 0;
            for (int i = 0; i < numberOfElements; i++)
            {
                int wordVal = (int)result[wordPos];
                bitResult[i] = ((wordVal & (1 << bitPos)) != 0) ? 1.0 : 0.0;
                if (++bitPos > 15) { bitPos = 0; wordPos++; }
            }
            return bitResult;
        }
        return result;
    }

    /// <summary>Reads a single numeric element from the specified address.</summary>
    public double ReadAnyValues(string startAddress) => ReadAnyValues(startAddress, 1)[0];

    /// <summary>
    /// Performs a read-modify-write operation on multiple addresses.
    /// Reads each specified word, applies AND mask (resets bits where mask bit = 0),
    /// then OR mask (sets bits where mask bit = 1), and writes back.
    /// 
    /// Supported only on SLC processors. Not implemented for PLC-5 due to different address format.
    /// 
    /// Reference: AB Publication 1770-6.5.16, page 7-20
    /// </summary>
    public int ReadModifyWrite(string[] addresses, ushort[] andMasks, ushort[] orMasks)
    {
        if (addresses == null || addresses.Length == 0)
            throw new PCCCException("ReadModifyWrite: number of sets must be non-zero.");
        if (andMasks == null || orMasks == null)
            throw new PCCCException("ReadModifyWrite: andMasks and orMasks cannot be null.");
        if (addresses.Length != andMasks.Length || addresses.Length != orMasks.Length)
            throw new PCCCException("ReadModifyWrite: addresses, andMasks, and orMasks must have the same length.");

        DataAddress[] parsed = new DataAddress[addresses.Length];
        for (int i = 0; i < addresses.Length; i++)
        {
            parsed[i] = ParseAddress(addresses[i]);
            if (parsed[i].FileType == 0)
                throw new PCCCException($"ReadModifyWrite: invalid address '{addresses[i]}'.");
        }

        var req = PCCCMessage.CreateReadModifyWriteRequest(parsed, andMasks, orMasks, 0, (byte)MyNode, (byte)TargetNode);
        _protocol.SendRequest(req, out int sts);
        return sts;
    }

    /// <summary>
    /// Writes a single integer value to the specified address.
    /// </summary>
    /// <returns>Empty string on success, or an error description on failure.</returns>
    public string WriteData(string startAddress, int dataToWrite)
    {
        int status = WriteData(startAddress, 1, new int[] { dataToWrite });
        return status == 0 ? string.Empty : PCCCErrors.DecodeStatus(status);
    }

    /// <summary>
    /// Writes multiple integer values to the specified address.
    /// Supports both standard integer (16-bit) and long integer (32-bit) files.
    /// </summary>
    public int WriteData(string startAddress, int numberOfElements, int[] dataToWrite)
    {
        DataAddress p = ParseAddress(startAddress);
        if (p.FileType == (byte)PCCCConstants.SlcFileTypeCode.String)
            throw new PCCCException("Use WriteData(string, string) for ST files.");

        ushort[] words;
        if (p.FileType == (byte)PCCCConstants.SlcFileTypeCode.Long)
        {
            words = new ushort[numberOfElements * 2];
            for (int i = 0; i < numberOfElements; i++)
            {
                WordConverter.Int32ToWords(dataToWrite[i], out ushort low, out ushort high);
                words[i * 2] = low;
                words[i * 2 + 1] = high;
            }
        }
        else
        {
            words = new ushort[numberOfElements];
            for (int i = 0; i < numberOfElements; i++)
            {
                if (dataToWrite[i] > 32767 || dataToWrite[i] < -32768)
                    throw new PCCCException("Integer data out of range, must be between -32768 and 32767");
                words[i] = (ushort)dataToWrite[i];
            }
        }
        WriteWords(startAddress, words);
        return 0;
    }

    /// <summary>Writes a single float value to the specified address.</summary>
    public int WriteData(string startAddress, float dataToWrite)
        => WriteData(startAddress, 1, new float[] { dataToWrite });

    /// <summary>Writes multiple float values to the specified address.</summary>
    public int WriteData(string startAddress, int numberOfElements, float[] dataToWrite)
    {
        DataAddress p = ParseAddress(startAddress);
        if (p.FileType == 0) throw new PCCCException("Invalid Address");
        if (p.FileType == (byte)PCCCConstants.SlcFileTypeCode.String)
            throw new PCCCException("Use WriteData(string, string) for ST files.");

        if (p.BitNumber >= 0 && p.BitNumber < 16 && numberOfElements == 1)
        {
            string wordAddress = startAddress.Split('/')[0];
            string[] current = ReadAny(wordAddress, 1);
            int word = int.Parse(current[0], CultureInfo.InvariantCulture);
            if (dataToWrite[0] != 0)
                word |= (1 << p.BitNumber);
            else
                word &= ~(1 << p.BitNumber);
            return WriteData(wordAddress, 1, new int[] { word });
        }

        ushort[] words;
        if (p.FileType == (byte)PCCCConstants.SlcFileTypeCode.Float)
        {
            words = new ushort[numberOfElements * 2];
            for (int i = 0; i < numberOfElements; i++)
            {
                WordConverter.FloatToWords(dataToWrite[i], out ushort low, out ushort high);
                words[i * 2] = low;
                words[i * 2 + 1] = high;
            }
        }
        else if (p.FileType == (byte)PCCCConstants.SlcFileTypeCode.Long)
        {
            words = new ushort[numberOfElements * 2];
            for (int i = 0; i < numberOfElements; i++)
            {
                WordConverter.Int32ToWords((int)dataToWrite[i], out ushort low, out ushort high);
                words[i * 2] = low;
                words[i * 2 + 1] = high;
            }
        }
        else
        {
            words = new ushort[numberOfElements];
            for (int i = 0; i < numberOfElements; i++)
            {
                if (dataToWrite[i] > 32767 || dataToWrite[i] < -32768)
                    throw new PCCCException("Integer data out of range, must be between -32768 and 32767");
                words[i] = (ushort)dataToWrite[i];
            }
        }
        WriteWords(startAddress, words);
        return 0;
    }

    /// <summary>
    /// Writes a string to an ST file (type 0x8D) or word-packed integer file.
    /// For ST files: writes length word (LE) followed by character data (max 82 chars).
    /// For integer files: packs characters into 16-bit words (high byte, low byte).
    /// 
    /// Original method from DF1Comm.vb (WriteData for string).
    /// </summary>
    public int WriteData(string startAddress, string dataToWrite)
    {
        if (string.IsNullOrEmpty(dataToWrite)) return 0;
        if (dataToWrite.Length > PCCCConstants.Df1Limits.MaxStringLength) 
            dataToWrite = dataToWrite.Substring(0, PCCCConstants.Df1Limits.MaxStringLength);

        DataAddress p = ParseAddress(startAddress);
        if (p.FileType == (byte)PCCCConstants.SlcFileTypeCode.String)
        {
            byte[] stElement = new byte[PCCCConstants.Df1Limits.SlcStringElementBytes];
            int len = dataToWrite.Length;
            stElement[0] = (byte)(len & 0xFF);
            stElement[1] = (byte)((len >> 8) & 0xFF);
            for (int i = 0; i < len; i++)
            {
                int wordOffset = i / 2;
                int byteOffset = 2 + wordOffset * 2;
                if (i % 2 == 0)
                    stElement[byteOffset + 1] = (byte)dataToWrite[i]; // even → high byte
                else
                    stElement[byteOffset] = (byte)dataToWrite[i];     // odd  → low byte
            }
            return WriteRawDataWithChunking(p, stElement);
        }
        else
        {
            int[]? words = StringConverter.StringToWords(dataToWrite);
            if (words == null) return -1;
            ushort[] ushortWords = new ushort[words.Length];
            for (int i = 0; i < words.Length; i++)
                ushortWords[i] = (ushort)words[i];
            WriteWords(startAddress, ushortWords);
            return 0;
        }
    }

    // ─── Data Memory Enumeration ─────────────────────────────────────────

    /// <summary>
    /// Returns a list of data files present in the processor.
    /// Uses file-based read (OpenFile/FileRead) when supported by the processor,
    /// otherwise falls back to physical (Protected Typed Logical Read) method.
    /// 
    /// Original method from DF1Comm.vb (GetDataMemory).
    /// </summary>
    public DataFileDetails[] GetDataMemory()
    {
        if (IsMicroLogix1400)
        {
            // Use file list from context (loaded by PCCCComm)
            var list = _context.GetMl1400FileList();
            return list ?? Array.Empty<DataFileDetails>();
        }

        if (SupportsFileBasedTransfer())
            return GetDataMemoryFileBased();
        else
            return GetDataMemoryPhysicalBased();
    }

    /// <summary>
    /// Returns data file information specific to MicroLogix 1500.
    /// This method reads File 0, Type 2 (different directory structure than standard SLC).
    /// 
    /// Original method from DF1Comm.vb (GetML1500DataMemory).
    /// </summary>
    public DataFileDetails[] GetML1500DataMemory()
    {
        var pAddr = new DataAddress { FileNumber = 0, FileType = 2, Element = 0x2F };
        byte[] data = ReadRawDataWithChunking(ref pAddr, 2, out int reply);
        if (reply != 0) throw new PCCCException(PCCCErrors.DecodeStatus(reply) + " - Failed to get data table list");

        int fzSize = data[0] + data[1] * 256;
        pAddr.Element = 0; pAddr.SubElement = 0;
        byte[] fzd = ReadRawDataWithChunking(ref pAddr, fzSize, out reply);
        if (reply != 0) throw new PCCCException(PCCCErrors.DecodeStatus(reply) + " - Failed to get data table list");

        var list = new List<DataFileDetails>();
        int filePosition = 143, idx = 0;
        while (filePosition + 2 < fzd.Length)
        {
            int bpe = FileTypeToBytesPerElement(fzd[filePosition], out string ftStr);
            var df = new DataFileDetails
            {
                FileType = ftStr,
                NumberOfElements = (fzd[filePosition + 1] + fzd[filePosition + 2] * 256) / bpe,
                FileNumber = idx
            };
            if (fzd[filePosition] > 0x81 && fzd[filePosition] < 0x95) { list.Add(df); idx++; }
            filePosition += 10;
        }
        return list.ToArray();
    }

    /// <summary>
    /// Enumerates data files on MicroLogix 1400 by probing all file numbers 0..255
    /// for each known file type. Uses chunked element counting for speed.
    /// </summary>
    private DataFileDetails[] GetDataMemoryMl1400()
    {
        var list = new List<DataFileDetails>();

        foreach (PCCCConstants.SlcFileTypeCode fileType in Enum.GetValues(typeof(PCCCConstants.SlcFileTypeCode)))
        {
            int bytesPerElement = PCCCConstants.SlcFileTypeInfo.GetBytesPerElement(fileType);
            int fileTypeCode = (int)fileType;

            // Only file 0 for O and I
            int maxFileNumber = (fileTypeCode == 0x82 || fileTypeCode == 0x83) ? 0 : 255;

            for (int fileNumber = 0; fileNumber <= maxFileNumber; fileNumber++)
            {
                var addr = new DataAddress
                {
                    FileNumber = fileNumber,
                    FileType = fileTypeCode
                };

                // Probe: read first element
                byte[] probe = ReadRawDataWithChunking(ref addr, bytesPerElement, out int probeSts);
                if (probeSts != 0 || probe == null || probe.Length < bytesPerElement)
                    continue; // File doesn't exist — continue to next number

                // File exists — count elements in chunks (16 elements at a time)
                int elementCount = 0;
                const int chunkElements = 16;
                while (true)
                {
                    int chunkBytes = chunkElements * bytesPerElement;
                    addr.Element = elementCount;
                    byte[] chunk = ReadRawDataWithChunking(ref addr, chunkBytes, out int sts);
                    if (sts != 0) break;
                    elementCount += chunk.Length / bytesPerElement;
                    if (elementCount > 1000) break; // safety limit
                }

                if (elementCount > 0)
                {
                    string typeName = PCCCConstants.SlcFileTypeInfo.GetTypeName(fileType);
                    list.Add(new DataFileDetails
                    {
                        FileNumber = fileNumber,
                        FileType = typeName,
                        NumberOfElements = elementCount
                    });
                }
            }
        }

        return list.ToArray();
    }

    /// <summary>
    /// Reads the data file directory using traditional Protected Typed Logical Read (FNC 0xA1).
    /// This method works on all SLC/MicroLogix processors, including older ones that do not
    /// support file-based transfer (SLC 5/01, 5/02, ML1000).
    /// 
    /// Original method from DF1Comm.vb (GetDataMemory physical-based fallback).
    /// </summary>
    public DataFileDetails[] GetDataMemoryPhysicalBased()
    {
        byte[] fzd = ReadFileDirectory();
        int numberOfDataTables = fzd[PCCCConstants.ResponseOffsets.FileDirectory.NumberOfDataFilesLo]
                               + fzd[PCCCConstants.ResponseOffsets.FileDirectory.NumberOfDataFilesHi] * 256;
        var dataFiles = new Collection<DataFileDetails>();

        int filePosition, bytesPerRow;
        switch (_processorType)
        {
            case (byte)PCCCConstants.ProcessorTypeCode.SLC502:
            case (byte)PCCCConstants.ProcessorTypeCode.ML1000:
                filePosition = PCCCConstants.ResponseOffsets.FileDirectory.StartOffsetSlc502Ml1000;
                bytesPerRow = PCCCConstants.ResponseOffsets.FileDirectory.BytesPerEntrySlc502;
                break;
            case (byte)PCCCConstants.ProcessorTypeCode.ML1200:
            case (byte)PCCCConstants.ProcessorTypeCode.ML1500LSP:
            case (byte)PCCCConstants.ProcessorTypeCode.ML1500LRP:
            case (byte)PCCCConstants.ProcessorTypeCode.ML1100:
                filePosition = PCCCConstants.ResponseOffsets.FileDirectory.StartOffsetMl1100Ml1500;
                bytesPerRow = PCCCConstants.ResponseOffsets.FileDirectory.BytesPerEntryDefault;
                break;
            default:
                filePosition = PCCCConstants.ResponseOffsets.FileDirectory.StartOffsetDefault;
                bytesPerRow = PCCCConstants.ResponseOffsets.FileDirectory.BytesPerEntryDefault;
                break;
        }

        int i = 0, k = 0;
        while (k < numberOfDataTables && filePosition + 2 < fzd.Length)
        {
            int bpe = FileTypeToBytesPerElement(fzd[filePosition], out string ftStr);
            var df = new DataFileDetails
            {
                FileType = ftStr,
                NumberOfElements = (fzd[filePosition + 1] + fzd[filePosition + 2] * 256) / bpe,
                FileNumber = i
            };
            if (fzd[filePosition] > 0x81 && fzd[filePosition] < 0x9F) { dataFiles.Add(df); k++; }
            if (k > 0) i++;
            filePosition += bytesPerRow;
        }

        var result = new DataFileDetails[dataFiles.Count];
        dataFiles.CopyTo(result, 0);
        return result;
    }

    /// <summary>
    /// Reads the data file directory using file-based read (OpenFile/FileRead).
    /// This method is only available on processors that support file-based transfer
    /// (SLC 5/03, 5/04, 5/05, MicroLogix 1100, 1200, 1500). It is more reliable
    /// and consistent with the upload/download mechanism.
    /// </summary>
    private DataFileDetails[] GetDataMemoryFileBased()
    {
        ushort dirTag = OpenFile(0, 0x24);
        byte[] sizeData = FileReadWithChunking(dirTag, 70, 2);
        if (sizeData.Length < 2)
            throw new PCCCException("Failed to read directory size via file-based read.");

        int dirSize = sizeData[0] + (sizeData[1] << 8);
        if (dirSize <= 0 || dirSize > 65535)
            throw new PCCCException($"Invalid directory size from file-based read: {dirSize}");

        byte[] directory = FileReadWithChunking(dirTag, 0, dirSize);
        CloseFile(dirTag);
        return ParseDirectory(directory);
    }

    /// <summary>
    /// Parses raw directory bytes into a list of data file details.
    /// Common parsing logic used by both physical-based and file-based directory reads.
    /// 
    /// Directory format per AB Publication 1770-6.5.16, Chapter 10.
    /// </summary>
    private DataFileDetails[] ParseDirectory(byte[] fzd)
    {
        int numberOfDataTables = fzd[PCCCConstants.ResponseOffsets.FileDirectory.NumberOfDataFilesLo]
                            + fzd[PCCCConstants.ResponseOffsets.FileDirectory.NumberOfDataFilesHi] * 256;
        var dataFiles = new List<DataFileDetails>();

        // Determine starting offset and bytes per row based on processor type
        int filePosition, bytesPerRow;
        switch (_processorType)
        {
            case (byte)PCCCConstants.ProcessorTypeCode.SLC502:
            case (byte)PCCCConstants.ProcessorTypeCode.ML1000:
                filePosition = PCCCConstants.ResponseOffsets.FileDirectory.StartOffsetSlc502Ml1000;
                bytesPerRow = PCCCConstants.ResponseOffsets.FileDirectory.BytesPerEntrySlc502;
                break;
            case (byte)PCCCConstants.ProcessorTypeCode.ML1200:
            case (byte)PCCCConstants.ProcessorTypeCode.ML1500LSP:
            case (byte)PCCCConstants.ProcessorTypeCode.ML1500LRP:
            case (byte)PCCCConstants.ProcessorTypeCode.ML1100:
                filePosition = PCCCConstants.ResponseOffsets.FileDirectory.StartOffsetMl1100Ml1500;
                bytesPerRow = PCCCConstants.ResponseOffsets.FileDirectory.BytesPerEntryDefault;
                break;
            default:
                filePosition = PCCCConstants.ResponseOffsets.FileDirectory.StartOffsetDefault;
                bytesPerRow = PCCCConstants.ResponseOffsets.FileDirectory.BytesPerEntryDefault;
                break;
        }

        int entriesParsed = 0;
        int maxEntries = (fzd.Length - filePosition) / bytesPerRow;

        // Iterate through directory entries
        while (entriesParsed < numberOfDataTables && entriesParsed < maxEntries)
        {
            // Guard against buffer overflow
            if (filePosition + bytesPerRow > fzd.Length)
                break;

            byte fileTypeByte = fzd[filePosition];
            // Valid data file types are 0x82–0x9F (SLC/MicroLogix data files)
            if (fileTypeByte > 0x81 && fileTypeByte < 0x9F)
            {
                int bpe = FileTypeToBytesPerElement(fileTypeByte, out string ftStr);
                int elementCount = (fzd[filePosition + 1] + (fzd[filePosition + 2] << 8)) / bpe;
                dataFiles.Add(new DataFileDetails
                {
                    FileType = ftStr,
                    NumberOfElements = elementCount,
                    FileNumber = entriesParsed
                });
            }
            entriesParsed++;
            filePosition += bytesPerRow;
        }
        return dataFiles.ToArray();
    }

    // ─── I/O Configuration ─────────────────────────────────────────────────

    /// <summary>
    /// Returns the number of slots in the chassis.
    /// For MicroLogix processors (no chassis), returns 0.
    /// 
    /// Original method from DF1Comm.vb (GetSlotCount).
    /// </summary>
    public int GetSlotCount()
    {
        byte[] body = { 4, 0, 0x60, 0, 0 };
        var req = new PCCCMessage((byte)TargetNode, (byte)MyNode, PCCCConstants.Cmd.ProtectedWrite, 0, 0, PCCCConstants.Fnc.GetSlotCount, body);
        var reply = _protocol.SendRequest(req, out int sts);
        if (sts != PCCCConstants.Sts.Success || reply?.Data == null || reply.Data.Length == 0)
            throw new PCCCException("Failed to get Slot Count - " + PCCCErrors.DecodeStatus(sts));
        return reply.Data[0] > 0 ? reply.Data[0] - 1 : 0;
    }

    /// <summary>
    /// Returns I/O configuration for all slots.
    /// For ML1500, calls GetML1500IOConfig(); otherwise GetSLCIOConfig().
    /// 
    /// Original method from DF1Comm.vb (GetIOConfig).
    /// </summary>
    public IOConfig[] GetIOConfig()
    {
        int pt = GetProcessorType();
        return (pt == (byte)PCCCConstants.ProcessorTypeCode.ML1500LSP || pt == (byte)PCCCConstants.ProcessorTypeCode.ML1500LRP)
            ? GetML1500IOConfig() : GetSLCIOConfig();
    }

    /// <summary>
    /// Gets I/O configuration for standard SLC chassis.
    /// </summary>
    private IOConfig[] GetSLCIOConfig()
    {
        int slots = GetSlotCount();
        if (slots <= 0) throw new PCCCException("Failed to get Slot Count");
        const int bytesPerSlot = PCCCConstants.Df1Limits.SlcIoConfigBytesPerSlot;
        byte[] body = { (byte)(4 + (slots + 1) * bytesPerSlot + 2), 0, 0x60, 0, 0 };
        var req = new PCCCMessage((byte)TargetNode, (byte)MyNode, PCCCConstants.Cmd.ProtectedWrite, 0, 0, PCCCConstants.Fnc.GetSlotCount, body);
        var reply = _protocol.SendRequest(req, out int sts);
        if (sts != PCCCConstants.Sts.Success || reply?.Data == null)
            throw new PCCCException("Failed to get IO Config - " + PCCCErrors.DecodeStatus(sts));

        var result = new IOConfig[slots + 1];
        for (int i = 0; i <= slots; i++)
        {
            int baseOffset = i * bytesPerSlot;
            if (baseOffset + 9 >= reply.Data.Length)
                throw new PCCCException($"IO Config packet too short for slot {i}.");
            result[i].InputBytes  = reply.Data[baseOffset + 4];
            result[i].OutputBytes = reply.Data[baseOffset + bytesPerSlot];
            result[i].CardCode    = BitConverter.ToInt16(new byte[] { reply.Data[baseOffset + 8], reply.Data[baseOffset + 9] }, 0);
        }
        return result;
    }

    /// <summary>
    /// Gets I/O configuration for MicroLogix 1500.
    /// This processor has a different I/O configuration file structure (type 0x62).
    /// 
    /// Original method from DF1Comm.vb (GetML1500IOConfig).
    /// </summary>
    private IOConfig[] GetML1500IOConfig()
    {
        byte[] body = { 4, 0, 0x62, 0, 0 };
        var req = new PCCCMessage((byte)TargetNode, (byte)MyNode, PCCCConstants.Cmd.ProtectedWrite, 0, 0, PCCCConstants.Fnc.GetSlotCount, body);
        var reply = _protocol.SendRequest(req, out int sts);
        if (sts != PCCCConstants.Sts.Success || reply?.Data == null || reply.Data.Length == 0)
            throw new PCCCException("Failed to get IO Config for ML1500 - " + PCCCErrors.DecodeStatus(sts));

        int fzSize = reply.Data[0] * 2;
        byte[] fzd = new byte[fzSize + 1];
        int filePosition = 0, subElement = 0;
        int chunkSize = fzSize > 0x50 ? 0x50 : fzSize;

        while (filePosition < fzSize && sts == PCCCConstants.Sts.Success)
        {
            byte[] chunkBody = { (byte)chunkSize, 0, 0x62, 0, 0 };
            if (subElement >= 255)
            {
                chunkBody = new byte[6];
                chunkBody[0] = (byte)chunkSize;
                chunkBody[1] = 0;
                chunkBody[2] = 0x62;
                chunkBody[3] = 255;
                chunkBody[4] = (byte)(subElement & 0xFF);
                chunkBody[5] = (byte)((subElement >> 8) & 0xFF);
            }
            else
            {
                chunkBody[3] = (byte)subElement;
            }

            var chunkReq = new PCCCMessage((byte)TargetNode, (byte)MyNode, PCCCConstants.Cmd.ProtectedWrite, 0, 0, PCCCConstants.Fnc.GetSlotCount, chunkBody);
            var chunkReply = _protocol.SendRequest(chunkReq, out sts);
            if (sts != PCCCConstants.Sts.Success || chunkReply?.Data == null)
                break;

            int bytesToCopy = Math.Min(chunkSize, fzSize - filePosition);
            Array.Copy(chunkReply.Data, 0, fzd, filePosition, bytesToCopy);
            filePosition += bytesToCopy;
            subElement += chunkSize / 2;
            chunkSize = Math.Min(80, fzSize - filePosition);
        }

        if (sts != PCCCConstants.Sts.Success)
            throw new PCCCException("Failed to read ML1500 IO Config - " + PCCCErrors.DecodeStatus(sts));

        int slotCount = fzd[2] - 2;
        if (slotCount < 0) slotCount = 0;
        var result = new IOConfig[slotCount + 1];
        int idx = 32 + slotCount * 4;
        for (int s = 1; s <= slotCount; s++)
        {
            if (idx + 19 >= fzd.Length)
                throw new PCCCException($"ML1500 IO Config data too short for slot {s}.");
            result[s].InputBytes = fzd[idx + 2] * 2;
            result[s].OutputBytes = fzd[idx + 8] * 2;
            result[s].CardCode = BitConverter.ToInt16(new byte[] { fzd[idx + 18], fzd[idx + 19] }, 0);
            idx += 26;
        }

        // Get base unit IO
        byte[] baseBody = { 8, 0, 0x60, 0, 0 };
        var baseReq = new PCCCMessage((byte)TargetNode, (byte)MyNode, PCCCConstants.Cmd.ProtectedWrite, 0, 0, PCCCConstants.Fnc.GetSlotCount, baseBody);
        var baseReply = _protocol.SendRequest(baseReq, out int baseSts);
        if (baseSts != PCCCConstants.Sts.Success || baseReply?.Data == null || baseReply.Data.Length <= 6)
            throw new PCCCException("Failed to get Base IO Config for ML1500 - " + PCCCErrors.DecodeStatus(baseSts));
        result[0].InputBytes = baseReply.Data[4];
        result[0].OutputBytes = baseReply.Data[6];

        return result;
    }

    // ─── Upload / Download ─────────────────────────────────────────────────

    /// <summary>
    /// Uploads the entire program and data from the PLC.
    /// Automatically selects file-based transfer (SLC 5/03+ / ML1100/1200/1500)
    /// or physical-based transfer (SLC 5/01, 5/02, ML1000).
    /// </summary>
    public Collection<PLCFileDetails> UploadProgramData()
    {
        if (IsMicroLogix1400)
            return UploadProgramDataMl1400();

        DisableEventFlag = true;
        try
        {
            if (SupportsFileBasedTransfer())
                return UploadProgramDataFileBased();
            else
                return UploadProgramDataPhysicalBased();
        }
        finally
        {
            DisableEventFlag = false;
        }
    }

    /// <summary>
    /// Downloads a program to the PLC.
    /// Automatically selects file-based transfer or physical-based transfer.
    /// </summary>
    public void DownloadProgramData(Collection<PLCFileDetails> plcFiles)
    {
        if (IsMicroLogix1400)
        {
            DownloadProgramDataMl1400(plcFiles);
            return;
        }

        DisableEventFlag = true;
        try
        {
            if (SupportsFileBasedTransfer())
                DownloadProgramDataFileBased(plcFiles);
            else
                DownloadProgramDataPhysicalBased(plcFiles);
        }
        finally
        {
            DisableEventFlag = false;
        }
    }

    // ─── Implementation: Physical-based (legacy) ───────────────────────────
    // These methods are direct ports from DF1Comm.vb and work with SLC 5/01, 5/02, ML1000.
    // They use Protected Typed Logical Read/Write instead of file-based commands.

    private Collection<PLCFileDetails> UploadProgramDataPhysicalBased()
    {
        // Step 1: Read the file directory (File 0)
        byte[] fzd = ReadFileDirectory();
        var programFiles = new Collection<PLCFileDetails>();
        
        // Step 2: Add directory as the first file
        programFiles.Add(new PLCFileDetails { FileNumber = 0, Data = fzd, FileType = 0, NumberOfBytes = fzd.Length });

        // Step 3: Raise initial progress event
        OnFileProgress(new PCCCComm.FileProgressEventArgs
        {
            FileNumber = 0,
            FileType = 0,
            FileSizeBytes = fzd.Length,
            FilesCompleted = 1,
            TotalFiles = 1,
            TotalBytesTransferred = fzd.Length,
            GrandTotalBytes = fzd.Length
        });

        // Step 4: Parse directory header to get file counts
        int numberOfProgramFiles = fzd[PCCCConstants.ResponseOffsets.FileDirectory.NumberOfProgramFilesLo]
                                 + fzd[PCCCConstants.ResponseOffsets.FileDirectory.NumberOfProgramFilesHi] * 256;
        int numberOfDataFiles = fzd[PCCCConstants.ResponseOffsets.FileDirectory.NumberOfDataFilesLo]
                              + fzd[PCCCConstants.ResponseOffsets.FileDirectory.NumberOfDataFilesHi] * 256;
        int totalEntries = numberOfProgramFiles + numberOfDataFiles;

        // Step 5: Determine starting offset and bytes per entry based on processor type
        int filePosition = (_processorType == (byte)PCCCConstants.ProcessorTypeCode.SLC502 || _processorType == (byte)PCCCConstants.ProcessorTypeCode.ML1000)
            ? PCCCConstants.ResponseOffsets.FileDirectory.StartOffsetSlc502Ml1000
            : (_processorType == (byte)PCCCConstants.ProcessorTypeCode.ML1200 || _processorType == (byte)PCCCConstants.ProcessorTypeCode.ML1500LSP || _processorType == (byte)PCCCConstants.ProcessorTypeCode.ML1500LRP || _processorType == (byte)PCCCConstants.ProcessorTypeCode.ML1100)
                ? PCCCConstants.ResponseOffsets.FileDirectory.StartOffsetMl1100Ml1500
                : PCCCConstants.ResponseOffsets.FileDirectory.StartOffsetDefault;

        // Step 6: Calculate grand total bytes for progress reporting
        long grandTotalBytes = 0;
        int tempPos = filePosition;
        for (int j = 0; j < totalEntries && tempPos < fzd.Length; j++)
        {
            int sizeBytes = fzd[tempPos + 1] + fzd[tempPos + 2] * 256;
            grandTotalBytes += sizeBytes;
            tempPos += (_processorType == (byte)PCCCConstants.ProcessorTypeCode.SLC502 || _processorType == (byte)PCCCConstants.ProcessorTypeCode.ML1000) ? 8 : 10;
        }
        grandTotalBytes += fzd.Length;

        // Step 7: Iterate through directory entries and read each file
        int i = 0;
        long totalBytesTransferred = fzd.Length;
        int filesCompleted = 1;
        _lastFileProgressPercent = -1; // reset

        while (filePosition < fzd.Length && i < totalEntries)
        {
            var pf = new PLCFileDetails
            {
                FileType = fzd[filePosition],
                NumberOfBytes = fzd[filePosition + 1] + fzd[filePosition + 2] * 256
            };
            pf.FileNumber = fzd[filePosition + 3];

            var addr = new DataAddress { FileType = pf.FileType, FileNumber = pf.FileNumber };
            if (pf.NumberOfBytes > 0)
            {
                pf.Data = ReadRawDataWithChunking(ref addr, pf.NumberOfBytes, out int reply);
                if (reply != 0 && reply != 0x50)
                    throw new PCCCException("Failed to Read Program File " + addr.FileNumber +
                                        ", Type " + addr.FileType + " - " + PCCCErrors.DecodeStatus(reply));
                if (reply == 0x50)
                    pf.Data = Array.Empty<byte>();
            }
            else
                pf.Data = Array.Empty<byte>();

            programFiles.Add(pf);

            totalBytesTransferred += pf.NumberOfBytes;
            filesCompleted++;

            OnFileProgress(new PCCCComm.FileProgressEventArgs
            {
                FileNumber = pf.FileNumber,
                FileType = pf.FileType,
                FileSizeBytes = pf.NumberOfBytes,
                FilesCompleted = filesCompleted,
                TotalFiles = totalEntries + 1,
                TotalBytesTransferred = totalBytesTransferred,
                GrandTotalBytes = grandTotalBytes
            });

            i++;
            filePosition += (_processorType == (byte)PCCCConstants.ProcessorTypeCode.SLC502 || _processorType == (byte)PCCCConstants.ProcessorTypeCode.ML1000) ? 8 : 10;
        }
        return programFiles;
    }

    private void DownloadProgramDataPhysicalBased(Collection<PLCFileDetails> plcFiles)
    {
        SetProgramMode();

        long grandTotalBytes = plcFiles.Sum(f => f.Data?.Length ?? 0);
        long totalBytesTransferred = 0;
        int filesCompleted = 0;
        int totalFiles = plcFiles.Count;
        _lastFileProgressPercent = -1;

        // Step 1: Initialize download
        int dataLength = (_processorType == 0x5B || _processorType == 0x78) ? 13 : 15;
        byte[] initData = new byte[dataLength + 1];
        initData[0] = 0x02; initData[1] = 0x0A; initData[2] = 0xAA;
        initData[3] = 4; initData[4] = 0; initData[5] = 0x63;

        int idx = 0;
        while (idx < plcFiles.Count && (plcFiles[idx].FileNumber != 0 || plcFiles[idx].FileType != 0x24)) idx++;
        if (idx < plcFiles.Count && plcFiles[idx].Data?.Length >= 8)
        {
            initData[8] = plcFiles[idx].Data[2]; initData[9] = plcFiles[idx].Data[3];
            initData[10] = plcFiles[idx].Data[4]; initData[11] = plcFiles[idx].Data[5];
            if (dataLength > 14) { initData[12] = plcFiles[idx].Data[6]; initData[13] = plcFiles[idx].Data[7]; }
        }

        var pAddr = new DataAddress();
        switch (_processorType)
        {
            case 0x78: case 0x5B: case 0x49:
                pAddr.FileType = 0x63; pAddr.Element = 0;
                byte[] four = ReadRawDataWithChunking(ref pAddr, 4, out int r4);
                if (r4 != 0) throw new PCCCException("Failed to Read File 0, Type 63h - " + PCCCErrors.DecodeStatus(r4));
                Array.Copy(four, 0, initData, 8, 4);
                pAddr.FileType = 1; pAddr.Element = 0x23;
                initData[1] = 0x0A; initData[3] = 4;
                break;
            case 0x88: case 0x89: case 0x8C: case 0x9C:
                initData[1] = 0x0C; initData[3] = 6;
                pAddr.FileType = 2; pAddr.Element = 0x23;
                break;
            default:
                initData[1] = 0x0A; initData[3] = 4;
                pAddr.FileType = 1; pAddr.Element = 0x23;
                break;
        }
        initData[initData.Length - 2] = 1;
        initData[initData.Length - 1] = 0x56;

        var initReq = new PCCCMessage((byte)TargetNode, (byte)MyNode, PCCCConstants.Cmd.ProtectedWrite, 0, 0, PCCCConstants.Fnc.DownloadInit, initData);
        var initReply = _protocol.SendRequest(initReq, out int initSts);
        if (initSts != 0) throw new PCCCException("Failed to Initialize for Download - " + PCCCErrors.DecodeStatus(initSts));

        // Step 2: Secure sole access
        var secureReq = new PCCCMessage((byte)TargetNode, (byte)MyNode, PCCCConstants.Cmd.ProtectedWrite, 0, 0, PCCCConstants.Fnc.SecureAccess, Array.Empty<byte>());
        var secureReply = _protocol.SendRequest(secureReq, out int secureSts);
        if (secureSts != 0) throw new PCCCException("Failed to Secure Sole Access - " + PCCCErrors.DecodeStatus(secureSts));

        // Step 3: Write directory length
        pAddr.BitNumber = 16;
        byte[] dirLen = { (byte)(plcFiles[0].Data.Length & 0xFF), (byte)((plcFiles[0].Data.Length >> 8) & 0xFF) };
        int writeLenSts = WriteRawDataWithChunking(pAddr, dirLen);
        if (writeLenSts != 0) throw new PCCCException("Failed to Write Directory Length - " + PCCCErrors.DecodeStatus(writeLenSts));

        totalBytesTransferred += 2;
        filesCompleted = 1;
        OnFileProgress(new PCCCComm.FileProgressEventArgs
        {
            FileNumber = 0,
            FileType = 0,
            FileSizeBytes = 2,
            FilesCompleted = filesCompleted,
            TotalFiles = totalFiles,
            TotalBytesTransferred = totalBytesTransferred,
            GrandTotalBytes = grandTotalBytes
        });

        // Step 4: Write program directory
        pAddr.Element = 0;
        pAddr.SubElement = 0;
        int writeDirSts = WriteRawDataWithChunking(pAddr, plcFiles[0].Data);
        if (writeDirSts != 0) throw new PCCCException("Failed to Write New Program Directory - " + PCCCErrors.DecodeStatus(writeDirSts));

        totalBytesTransferred += plcFiles[0].Data.Length;
        filesCompleted++;
        OnFileProgress(new PCCCComm.FileProgressEventArgs
        {
            FileNumber = 0,
            FileType = 0,
            FileSizeBytes = plcFiles[0].Data.Length,
            FilesCompleted = filesCompleted,
            TotalFiles = totalFiles,
            TotalBytesTransferred = totalBytesTransferred,
            GrandTotalBytes = grandTotalBytes
        });

        // Step 5: Write each program/data file
        for (int i = 1; i < plcFiles.Count; i++)
        {
            pAddr.FileNumber = plcFiles[i].FileNumber;
            pAddr.FileType = plcFiles[i].FileType;
            pAddr.Element = 0;
            pAddr.SubElement = 0;
            pAddr.BitNumber = 16;
            int writeFileSts = WriteRawDataWithChunking(pAddr, plcFiles[i].Data);
            if (writeFileSts != 0) throw new PCCCException("Failed when writing files to PLC - " + PCCCErrors.DecodeStatus(writeFileSts));

            totalBytesTransferred += plcFiles[i].Data?.Length ?? 0;
            filesCompleted++;
            OnFileProgress(new PCCCComm.FileProgressEventArgs
            {
                FileNumber = plcFiles[i].FileNumber,
                FileType = plcFiles[i].FileType,
                FileSizeBytes = plcFiles[i].Data?.Length ?? 0,
                FilesCompleted = filesCompleted,
                TotalFiles = totalFiles,
                TotalBytesTransferred = totalBytesTransferred,
                GrandTotalBytes = grandTotalBytes
            });
        }

        // Step 6: Indicate download complete
        var completeReq = new PCCCMessage((byte)TargetNode, (byte)MyNode, PCCCConstants.Cmd.ProtectedWrite, 0, 0, PCCCConstants.Fnc.DownloadCompleted, Array.Empty<byte>());
        var completeReply = _protocol.SendRequest(completeReq, out int completeSts);
        if (completeSts != 0) throw new PCCCException("Failed to Indicate to PLC that Download is complete - " + PCCCErrors.DecodeStatus(completeSts));

        // Step 7: Release sole access
        var releaseReq = new PCCCMessage((byte)TargetNode, (byte)MyNode, PCCCConstants.Cmd.ProtectedWrite, 0, 0, PCCCConstants.Fnc.ReleaseAccess, Array.Empty<byte>());
        var releaseReply = _protocol.SendRequest(releaseReq, out int releaseSts);
        if (releaseSts != 0) throw new PCCCException("Failed to Release Sole Access - " + PCCCErrors.DecodeStatus(releaseSts));
    }

    // ─── Implementation: File-based upload/download ────────────────────────

    /// <summary>
    /// Uploads program and data files from the PLC using file-based transfer (SLC 5/03+ and ML1100/1200/1500).
    /// Reference: AB Publication 1770-6.5.16, Chapter 12 (Upload/Download procedures)
    /// </summary>
    private Collection<PLCFileDetails> UploadProgramDataFileBased()
    {
        // Step 1: Enter upload mode and get segment info (required by some PLCs)
        byte[] segmentInfo = _protocol.UploadAllRequest((byte)MyNode, (byte)TargetNode);
        
        // Step 2: Secure edit resource (sole access)
        GetEditResource();
        var files = new Collection<PLCFileDetails>();
        
        // Step 3: Open program directory (file number 0, type 0x24)
        ushort dirTag = OpenFile(0, 0x24);
        
        // Step 4: Read directory size from byte offset 70 (word offset 0x23)
        // The directory length is stored as a 16-bit little-endian value at offset 70
        byte[] dirSizeData = FileReadWithChunking(dirTag, 70, 2);
        int dirSize = dirSizeData[0] + (dirSizeData[1] << 8);
        
        // Step 5: Read entire directory using chunked reads
        byte[] directory = FileReadWithChunking(dirTag, 0, dirSize);
        files.Add(new PLCFileDetails { FileNumber = 0, FileType = 0, NumberOfBytes = dirSize, Data = directory });
        CloseFile(dirTag);

        // Step 6: Parse directory and read each program/data file
        // Directory structure (per AB Publication 1770-6.5.16, Chapter 10):
        //   Offset 46/47: number of program files (little-endian)
        //   Offset 52/53: number of data files (little-endian)
        //   Then file table entries start at processor-specific offset.
        int numberOfProgramFiles = directory[PCCCConstants.ResponseOffsets.FileDirectory.NumberOfProgramFilesLo]
                                + directory[PCCCConstants.ResponseOffsets.FileDirectory.NumberOfProgramFilesHi] * 256;
        int numberOfDataFiles = directory[PCCCConstants.ResponseOffsets.FileDirectory.NumberOfDataFilesLo]
                            + directory[PCCCConstants.ResponseOffsets.FileDirectory.NumberOfDataFilesHi] * 256;
        int totalEntries = numberOfProgramFiles + numberOfDataFiles;

        // Determine starting offset and bytes per entry based on processor type
        int filePosition;
        int bytesPerEntry;
        if (_processorType == (byte)PCCCConstants.ProcessorTypeCode.SLC502 || 
            _processorType == (byte)PCCCConstants.ProcessorTypeCode.ML1000)
        {
            filePosition = PCCCConstants.ResponseOffsets.FileDirectory.StartOffsetSlc502Ml1000;
            bytesPerEntry = PCCCConstants.ResponseOffsets.FileDirectory.BytesPerEntrySlc502;
        }
        else if (_processorType == (byte)PCCCConstants.ProcessorTypeCode.ML1200 ||
                _processorType == (byte)PCCCConstants.ProcessorTypeCode.ML1500LSP ||
                _processorType == (byte)PCCCConstants.ProcessorTypeCode.ML1500LRP ||
                _processorType == (byte)PCCCConstants.ProcessorTypeCode.ML1100)
        {
            filePosition = PCCCConstants.ResponseOffsets.FileDirectory.StartOffsetMl1100Ml1500;
            bytesPerEntry = PCCCConstants.ResponseOffsets.FileDirectory.BytesPerEntryDefault;
        }
        else
        {
            filePosition = PCCCConstants.ResponseOffsets.FileDirectory.StartOffsetDefault;
            bytesPerEntry = PCCCConstants.ResponseOffsets.FileDirectory.BytesPerEntryDefault;
        }

        // Calculate grand total bytes for progress reporting
        long grandTotalBytes = dirSize;
        int tempPos = filePosition;
        for (int j = 0; j < totalEntries && tempPos < directory.Length; j++)
        {
            int sizeBytes = directory[tempPos + 1] + directory[tempPos + 2] * 256;
            grandTotalBytes += sizeBytes;
            tempPos += bytesPerEntry;
        }

        int i = 0;
        long totalBytesTransferred = dirSize;
        int filesCompleted = 1;
        _lastFileProgressPercent = -1;

        while (filePosition < directory.Length && i < totalEntries)
        {
            int fileType = directory[filePosition];
            int fileNumber = directory[filePosition + 3];
            int fileSizeBytes = directory[filePosition + 1] + directory[filePosition + 2] * 256;

            // Some PLCs and emulators include zero-filled empty slots in the
            // directory. Attempting to OpenFile on these slots results in
            // STS 0x50 (Addressing problem). We skip them here.
            //
            // Valid ranges per AB 1770-6.5.16:
            //   - Data files:   0x82–0x9F (SLC/MicroLogix)
            //   - Program files: 0x20–0x3F (LAD/SYS)
            bool isValidDataFile = (fileType >= 0x82 && fileType <= 0x9F);
            bool isValidProgramFile = (fileType >= 0x20 && fileType <= 0x3F);
            if (!isValidDataFile && !isValidProgramFile)
            {
                i++;
                filePosition += bytesPerEntry;
                continue;
            }

            // Open the file (program or data) using its number and type
            ushort fileTag = OpenFile(fileNumber, fileType);
            byte[] fileData = fileSizeBytes > 0 ? FileReadWithChunking(fileTag, 0, fileSizeBytes) : Array.Empty<byte>();
            CloseFile(fileTag);

            var pf = new PLCFileDetails
            {
                FileNumber = fileNumber,
                FileType = fileType,
                NumberOfBytes = fileSizeBytes,
                Data = fileData
            };
            files.Add(pf);

            totalBytesTransferred += fileSizeBytes;
            filesCompleted++;

            // Trigger progress event
            OnFileProgress(new PCCCComm.FileProgressEventArgs
            {
                FileNumber = fileNumber,
                FileType = fileType,
                FileSizeBytes = fileSizeBytes,
                FilesCompleted = filesCompleted,
                TotalFiles = totalEntries + 1,
                TotalBytesTransferred = totalBytesTransferred,
                GrandTotalBytes = grandTotalBytes
            });

            i++;
            filePosition += bytesPerEntry;
        }

        // Step 7: Exit upload mode
        UploadCompleted();
        
        // Step 8: Release edit resource
        ReturnEditResource();
        return files;
    }

    /// <summary>
    /// Downloads a program and data files to the PLC using file-based transfer (SLC 5/03+ and ML1100/1200/1500).
    /// Reference: AB Publication 1770-6.5.16, Chapter 12 (Download/Upload procedures)
    /// </summary>
    private void DownloadProgramDataFileBased(Collection<PLCFileDetails> plcFiles)
    {
        // Step 1: Set Program mode (required for download)
        SetProgramMode();
        // Step 2: Disable forces (if any)
        try { DisableForces(); } catch (PCCCException) { /* ignore */ }

        // Step 3: Enter download mode and get segment info (optional)
        byte[] segmentInfo = _protocol.DownloadAllRequest((byte)MyNode, (byte)TargetNode);
        
        // Step 4: Secure edit resource
        GetEditResource();
        
        // Step 5: Open program directory file (file number 0, type 0x24)
        ushort dirTag = OpenFile(0, 0x24);
        
        // Step 6: Write entire directory data (starting at offset 0)
        FileWriteWithChunking(dirTag, 0, plcFiles[0].Data);
        
        // Step 7: Close directory file
        CloseFile(dirTag);

        // Progress calculation for download
        long grandTotalBytes = plcFiles.Sum(f => f.Data?.Length ?? 0);
        long totalBytesTransferred = plcFiles[0].Data?.Length ?? 0;
        int filesCompleted = 1;
        int totalFiles = plcFiles.Count;
        _lastFileProgressPercent = -1;

        // Trigger initial progress event for directory
        OnFileProgress(new PCCCComm.FileProgressEventArgs
        {
            FileNumber = 0,
            FileType = 0,
            FileSizeBytes = plcFiles[0].Data?.Length ?? 0,
            FilesCompleted = filesCompleted,
            TotalFiles = totalFiles,
            TotalBytesTransferred = totalBytesTransferred,
            GrandTotalBytes = grandTotalBytes
        });

        // Step 8: Write each program/data file (skip index 0, which is the directory)
        for (int i = 1; i < plcFiles.Count; i++)
        {
            ushort fileTag = OpenFile(plcFiles[i].FileNumber, plcFiles[i].FileType);
            FileWriteWithChunking(fileTag, 0, plcFiles[i].Data);
            CloseFile(fileTag);

            // Update progress
            totalBytesTransferred += plcFiles[i].Data?.Length ?? 0;
            filesCompleted++;

            OnFileProgress(new PCCCComm.FileProgressEventArgs
            {
                FileNumber = plcFiles[i].FileNumber,
                FileType = plcFiles[i].FileType,
                FileSizeBytes = plcFiles[i].Data?.Length ?? 0,
                FilesCompleted = filesCompleted,
                TotalFiles = totalFiles,
                TotalBytesTransferred = totalBytesTransferred,
                GrandTotalBytes = grandTotalBytes
            });
        }

        // Step 9: Exit download mode
        DownloadCompleted();
        
        // Step 10: Apply port configuration (required after download)
        ApplyPortConfiguration();
        
        // Step 11: Release edit resource
        ReturnEditResource();
    }

    // ─── ML1400 Upload & Download ────────────────────────────────────────────

    private const int FileTypeLadder       = 0x03;
    private const int FileTypeProgramDir   = 0x24;
    private const int FileTypeSystemConfig = 0x22;
    private const int FileTypeMetadata     = 0x00;
    private const int FileTypeSysHeader    = 0x64;

    private long _ml1400UploadBytesTransferred;
    private int _ml1400UploadFilesCompleted;

    /// <summary>
    /// Uploads ML1400 program. Uses file list from context (filelist.xml or default).
    /// Reads exact byte counts per file — does not read beyond the mapped size.
    /// </summary>
    private Collection<PLCFileDetails> UploadProgramDataMl1400()
    {
        var progressFiles = new List<PLCFileDetails>();
        _lastFileProgressPercent = -1;
        _ml1400UploadBytesTransferred = 0;
        _ml1400UploadFilesCompleted = 0;

        // ── Pre-compute totals for progress ──────────────────────────────────
        // GrandTotalBytes from data files (known size). TotalFiles covers all phases
        // so the progress bar moves smoothly from the start.
        // Phase counts: 1 (ft=0x63) + 1 (LAD) + 1 (ProgramDir) + 49 (SYS fn=2..50) + 24 (system files).
        DataFileDetails[] fileList = _context.GetMl1400FileList() ?? Array.Empty<DataFileDetails>();
        int dataFileCount = 0;
        long grandTotalBytes = 0;
        foreach (var fi in fileList)
        {
            int ftc = SlcFileTypeStringToCode(fi.FileType);
            if (ftc == 0) continue;
            grandTotalBytes += fi.NumberOfElements * PCCCConstants.SlcFileTypeInfo.GetBytesPerElement((PCCCConstants.SlcFileTypeCode)ftc);
            dataFileCount++;
        }
        int totalFiles = 1 + 1 + 1 + 49 + 24 + dataFileCount;

        // ── Phases 1–4: Handshake, LAD, Program Directory, SYS Config ──────
        GetProcessorType();
        DoHandshakeReads();
        ReadAndAdd(progressFiles, 0x63, 0, 4, totalFiles: totalFiles, grandTotalBytes: grandTotalBytes);
        ReadChunkedSubElement(progressFiles, FileTypeLadder, 0, totalFiles, grandTotalBytes);
        ReadAndAdd(progressFiles, FileTypeProgramDir, 0, 64, totalFiles: totalFiles, grandTotalBytes: grandTotalBytes);
        for (int fn = 2; fn <= 50; fn++)
            ReadChunkedSubElement(progressFiles, FileTypeSystemConfig, fn, totalFiles, grandTotalBytes);

        // ── Phase 5: Read system files (ft=0x4x and others) ────────
        // These files are not in filelist.xml but are present on all ML1400 units.
        // ReadChunkedSubElement reads until EOF so no element count needed.
        // Order matches upload capture.
        int[] systemFts = { 0x47, 0x49, 0x47, 0x4D, 0x60, 0x69, 0x61, 0x61, 0x61, 0x61, 0x61, 0x61, 0x6C,
                            0xA1, 0xA2, 0xE0, 0xED, 0xEE, 0xE2, 0xE3, 0xEC, 0x48, 0x4A, 0x48 };
        int[] systemFns = {    0,    1,    2,    3,    0,    1,    2,    3,    4,    5,    6,    7,    8,
                               0,    1,    0,    1,    2,    3,    4,    5,    2,    1,    0 };
        for (int h = 0; h < systemFts.Length; h++)
            ReadChunkedSubElement(progressFiles, systemFts[h], systemFns[h], totalFiles, grandTotalBytes);

        // ── Phase 6: Get file list ────────────────────────────────────────────
        // (file list already loaded above for progress calculation)

        // ── Phase 7: Read each data file exactly per mapping ─────────────────
        foreach (var fileInfo in fileList)
        {
            int fileTypeCode = SlcFileTypeStringToCode(fileInfo.FileType);
            if (fileTypeCode == 0) continue;

            int bytesPerElement = PCCCConstants.SlcFileTypeInfo.GetBytesPerElement((PCCCConstants.SlcFileTypeCode)fileTypeCode);
            int totalBytes = fileInfo.NumberOfElements * bytesPerElement;

            var addr = new DataAddress
            {
                FileType = (byte)fileTypeCode,
                FileNumber = fileInfo.FileNumber,
                Element = 0,
                SubElement = 0
            };

            byte[] data = ReadRawDataWithChunking(ref addr, totalBytes, out int sts);
            if (sts != 0 || data == null || data.Length == 0)
                continue;

            progressFiles.Add(new PLCFileDetails
            {
                FileNumber = fileInfo.FileNumber,
                FileType = fileTypeCode,
                Data = data,
                NumberOfBytes = data.Length
            });

            _ml1400UploadBytesTransferred += data.Length;
            _ml1400UploadFilesCompleted++;
            OnFileProgress(new PCCCComm.FileProgressEventArgs
            {
                FileNumber = fileInfo.FileNumber,
                FileType = fileTypeCode,
                FileSizeBytes = totalBytes,
                FilesCompleted = _ml1400UploadFilesCompleted,
                TotalFiles = totalFiles,
                TotalBytesTransferred = _ml1400UploadBytesTransferred,
                GrandTotalBytes = grandTotalBytes
            });
        }

        // ── Build result ──────────────────────────────────────────────────────
        var result = new Collection<PLCFileDetails>();
        foreach (var pf in progressFiles)
        {
            pf.NumberOfBytes = pf.Data?.Length ?? 0;
            result.Add(pf);
        }
        return result;
    }

    /// <summary>
    /// Reads a file in 80-byte chunks (sub+=40) until EOF. Used for LAD, SYS Config.
    /// </summary>
    private void ReadChunkedSubElement(List<PLCFileDetails> list, int fileType, int fileNumber,
        int totalFiles = 0, long grandTotalBytes = 0)
    {
        var data = new List<byte>();
        int sub = 0;
        while (true)
        {
            var addr = new DataAddress
            {
                FileType = (byte)fileType,
                FileNumber = fileNumber,
                Element = 0,
                SubElement = sub
            };
            byte[] chunk = ReadRawDataWithChunking(ref addr, 0x50, out int sts);
            if (sts != 0 || chunk == null || chunk.Length == 0) break;
            data.AddRange(chunk);
            sub += 40;
        }
        if (data.Count == 0) return;

        var fullData = data.ToArray();
        list.Add(new PLCFileDetails
        {
            FileNumber = fileNumber,
            FileType = fileType,
            Data = fullData,
            NumberOfBytes = fullData.Length
        });
        _ml1400UploadBytesTransferred += fullData.Length;
        _ml1400UploadFilesCompleted++;
        OnFileProgress(new PCCCComm.FileProgressEventArgs
        {
            FileNumber = fileNumber,
            FileType = fileType,
            FileSizeBytes = fullData.Length,
            FilesCompleted = _ml1400UploadFilesCompleted,
            TotalFiles = totalFiles,
            TotalBytesTransferred = _ml1400UploadBytesTransferred,
            GrandTotalBytes = grandTotalBytes
        });
    }

    /// <summary>Reads fixed-size file and adds to list.</summary>
    private void ReadAndAdd(List<PLCFileDetails> list, int fileType, int fileNumber,
        int byteCount, int element = 0, int subElement = 0,
        int totalFiles = 0, long grandTotalBytes = 0)
    {
        var addr = new DataAddress
        {
            FileType = (byte)fileType,
            FileNumber = fileNumber,
            Element = element,
            SubElement = subElement
        };
        byte[] data = ReadRawDataWithChunking(ref addr, byteCount, out int sts);
        if (sts != 0 || data == null || data.Length == 0) return;

        list.Add(new PLCFileDetails
        {
            FileNumber = fileNumber,
            FileType = fileType,
            Data = data,
            NumberOfBytes = data.Length
        });
        _ml1400UploadBytesTransferred += data.Length;
        _ml1400UploadFilesCompleted++;
        OnFileProgress(new PCCCComm.FileProgressEventArgs
        {
            FileNumber = fileNumber,
            FileType = fileType,
            FileSizeBytes = data.Length,
            FilesCompleted = _ml1400UploadFilesCompleted,
            TotalFiles = totalFiles,
            TotalBytesTransferred = _ml1400UploadBytesTransferred,
            GrandTotalBytes = grandTotalBytes
        });
    }

    private static int SlcFileTypeStringToCode(string typeName)
    {
        return typeName switch
        {
            "O"  => 0x82, "I"  => 0x83, "S"  => 0x84, "B"  => 0x85,
            "T"  => 0x86, "C"  => 0x87, "R"  => 0x88, "N"  => 0x89,
            "F"  => 0x8A, "ST" => 0x8D, "L"  => 0x91, "MG" => 0x92,
            "PD" => 0x93, "PLS"=> 0x94, "A"  => 0x8E,
            _    => 0
        };
    }

    /// <summary>Full handshake probes (9 reads).</summary>
    private void DoHandshakeReads()
    {
        var a = new DataAddress { FileType = FileTypeMetadata, FileNumber = 0, Element = 0 };
        ReadRawDataWithChunking(ref a, 44, out _);
        a = new DataAddress { FileType = 0x63, FileNumber = 0, Element = 0 };
        ReadRawDataWithChunking(ref a, 4, out _);
        a = new DataAddress { FileType = FileTypeMetadata, FileNumber = 0, Element = 11 };
        ReadRawDataWithChunking(ref a, 20, out _);
        a = new DataAddress { FileType = FileTypeLadder, FileNumber = 0, Element = 21 };
        ReadRawDataWithChunking(ref a, 10, out _);
        a = new DataAddress { FileType = FileTypeSysHeader, FileNumber = 0, Element = 0 };
        ReadRawDataWithChunking(ref a, 24, out _);
        a = new DataAddress { FileType = FileTypeMetadata, FileNumber = 0, Element = 9 };
        ReadRawDataWithChunking(ref a, 4, out _);
    }

    /// <summary>Writes LAD data: first 78 bytes at el=0, then el=39 sub=0,40,80,...</summary>
    private void WriteLadderData(byte[] ladData)
    {
        if (ladData == null || ladData.Length == 0) return;
        int first = Math.Min(78, ladData.Length);
        byte[] firstChunk = new byte[first];
        Array.Copy(ladData, 0, firstChunk, 0, first);
        WriteRawFixed(FileTypeLadder, 0, firstChunk, element: 0);
        int pos = first, sub = 0;
        while (pos < ladData.Length)
        {
            int chunk = Math.Min(80, ladData.Length - pos);
            byte[] chunkData = new byte[chunk];
            Array.Copy(ladData, pos, chunkData, 0, chunk);
            WriteRawFixed(FileTypeLadder, 0, chunkData, element: 39, subElement: sub);
            pos += chunk;
            sub += 40;
        }
    }

    /// <summary>Reads I/O config files for verification after DownloadCompleted (REQ 1465–1473).</summary>
    private void ReadIoConfigForVerify()
    {
        var a = new DataAddress { FileType = 0x47, FileNumber = 0, Element = 0, SubElement = 0 };
        ReadRawDataWithChunking(ref a, 80, out _);
        a.SubElement = 40; ReadRawDataWithChunking(ref a, 8, out _);
        a = new DataAddress { FileType = 0x49, FileNumber = 1, Element = 0 };
        foreach (var (sub, bc) in new[] { (0, 80), (40, 80), (80, 80), (120, 24) })
        { a.SubElement = sub; ReadRawDataWithChunking(ref a, bc, out _); }
        a = new DataAddress { FileType = 0x47, FileNumber = 2, Element = 0, SubElement = 0 };
        ReadRawDataWithChunking(ref a, 80, out _);
        a.SubElement = 40; ReadRawDataWithChunking(ref a, 8, out _);
        a = new DataAddress { FileType = 0x48, FileNumber = 0, Element = 4, SubElement = 0 };
        ReadRawDataWithChunking(ref a, 2, out _);
    }

    /// <summary>Writes a fixed-size raw block (no chunking).</summary>
    private void WriteRawFixed(int fileType, int fileNumber, byte[] data, int element = 0, int subElement = 0)
    {
        if (data == null || data.Length == 0) return;
        var addr = new DataAddress
        {
            FileType = (byte)fileType,
            FileNumber = fileNumber,
            Element = element,
            SubElement = subElement,
            BitNumber = -1
        };
        WriteRawDataWithChunking(addr, data);
    }

    /// <summary>Writes a whole file as one block (for small files like Program Directory).</summary>
    private void WriteRawFile(Dictionary<(int, int), byte[]> fileMap, int fileType, int fileNumber,
        ref long bytesTransferred, ref int filesCompleted, long totalBytes, int totalFiles)
    {
        if (!fileMap.TryGetValue((fileType, fileNumber), out byte[] data) || data.Length == 0) return;
        var addr = new DataAddress { FileType = (byte)fileType, FileNumber = fileNumber, BitNumber = -1 };
        if (WriteRawDataWithChunking(addr, data) != 0) return;
        bytesTransferred += data.Length;
        filesCompleted++;
        OnFileProgress(new PCCCComm.FileProgressEventArgs
        {
            FileNumber = fileNumber,
            FileType = fileType,
            FileSizeBytes = data.Length,
            FilesCompleted = filesCompleted,
            TotalFiles = totalFiles,
            TotalBytesTransferred = bytesTransferred,
            GrandTotalBytes = totalBytes
        });
    }

    /// <summary>
    /// Writes a file using element addressing (not sub-element). Writes in chunks of up to 16 elements.
    /// Ensures proper alignment for Timer (6 bytes), Long (4 bytes), etc.
    /// </summary>
    /// <summary>
    /// Writes a file from fileMap using sub-element addressing (el=0, sub advances per chunk).
    /// All ML1400 file types use el=0 with sub-element byte addressing — verified from capture.
    /// </summary>
    private void WriteFileByElement(Dictionary<(int, int), byte[]> fileMap, int fileType, int fileNumber,
        ref long bytesTransferred, ref int filesCompleted, long totalBytes, int totalFiles)
    {
        if (!fileMap.TryGetValue((fileType, fileNumber), out byte[] data) || data.Length == 0)
            return;

        var addr = new DataAddress
        {
            FileType = (byte)fileType,
            FileNumber = fileNumber,
            Element = 0,
            SubElement = 0,
            BitNumber = -1
        };
        WriteRawDataWithChunking(addr, data);

        bytesTransferred += data.Length;
        filesCompleted++;
        OnFileProgress(new PCCCComm.FileProgressEventArgs
        {
            FileNumber = fileNumber,
            FileType = fileType,
            FileSizeBytes = data.Length,
            FilesCompleted = filesCompleted,
            TotalFiles = totalFiles,
            TotalBytesTransferred = bytesTransferred,
            GrandTotalBytes = totalBytes
        });
    }

    /// <summary>
    /// Downloads program to ML1400. Sequence verified from RSLogix 500 v12 capture.
    /// Exact order is critical.
    /// </summary>
    private void DownloadProgramDataMl1400(Collection<PLCFileDetails> plcFiles)
    {
        if (plcFiles == null || plcFiles.Count == 0)
            throw new ArgumentException("No data to download.", nameof(plcFiles));

        DisableEventFlag = true;
        _lastFileProgressPercent = -1;
        long totalBytes = plcFiles.Sum(f => f.Data?.Length ?? 0);
        long bytesTransferred = 0;
        int filesCompleted = 0;
        int totalFiles = plcFiles.Count;

        try
        {
            var fileMap = new Dictionary<(int, int), byte[]>();
            foreach (var f in plcFiles)
                fileMap[(f.FileType, f.FileNumber)] = f.Data ?? Array.Empty<byte>();

            // ── Phase 1: Handshake probes (short) ──────────────────────────
            GetProcessorType();
            DoHandshakeReads();

            // ── Phase 2: Init download, get edit resource, set program mode ─
            GetProcessorType();

            byte[] cmd1 = new byte[12] { 0xAA, 0x06, 0x00, 0x63, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
            if (fileMap.TryGetValue((0x63, 0), out var p) && p.Length >= 4)
                Array.Copy(p, 0, cmd1, 6, 4);
            else
            {
                cmd1[6] = 0x08; cmd1[7] = 0x91; cmd1[8] = 0x00; cmd1[9] = 0x00;
            }
            cmd1[10] = 0x68;  // trailing bytes observed in captures — purpose unknown
            cmd1[11] = 0x9C;  // (not processor type; present regardless of ML1400 variant)
            ExecuteCommandList(new byte[][] { cmd1, new byte[] { 0x56 } });
            GetEditResource();
            SetProgramMode();

            // ── Phase 3: Write LAD size header (2 bytes at el=40) ──────────
            if (fileMap.TryGetValue((FileTypeLadder, 0), out byte[] ladData) && ladData.Length >= 82)
            {
                byte[] header = new byte[2];
                Array.Copy(ladData, 80, header, 0, 2);
                WriteRawFixed(FileTypeLadder, 0, header, element: 40);
            }

            // ── Phase 4: Write LAD body ─────────────────────────────────────
            if (fileMap.TryGetValue((FileTypeLadder, 0), out ladData) && ladData.Length > 0)
                WriteLadderData(ladData);

            // ── Phase 5: Program Directory (64 bytes) ──────────────────────
            WriteRawFile(fileMap, FileTypeProgramDir, 0, ref bytesTransferred, ref filesCompleted, totalBytes, totalFiles);

            // ── Phase 6: SYS Config (ft=0x22, fn=2..50) ────────────────────
            for (int fn = 2; fn <= 50; fn++)
                WriteFileByElement(fileMap, FileTypeSystemConfig, fn, ref bytesTransferred, ref filesCompleted, totalBytes, totalFiles);

            // ── Phase 7: Write all data files from fileMap in upload order ──
            // All files backed up during upload are written here, in the same order
            // they were uploaded. Capture shows system files (ft=0x4x, 0x6x, 0xEx)
            // are written BEFORE DnldComplete alongside regular data files.
            // Only ft=0x63 (ExecCmdList only) and ft=0x48/0x4A (read-verify only) are skipped.
            foreach (var pf in plcFiles)
            {
                int ft = pf.FileType;
                if (ft == 0x63) continue;               // written via ExecCmdList in Phase 2
                if (ft == 0x48 || ft == 0x4A) continue; // read-verify only, never written
                if (ft == FileTypeLadder && pf.FileNumber == 0) continue;
                if (ft == FileTypeProgramDir && pf.FileNumber == 0) continue;
                if (ft == FileTypeSystemConfig && pf.FileNumber >= 2 && pf.FileNumber <= 50) continue;
                WriteFileByElement(fileMap, ft, pf.FileNumber, ref bytesTransferred, ref filesCompleted, totalBytes, totalFiles);
            }

            // ── Phase 8: DownloadCompleted ─────────────────────────────────
            DownloadCompleted();

            // ── Phase 9: Read I/O config for verification (REQ 1464–1473) ──
            GetProcessorType();
            ReadIoConfigForVerify();

            // ── Phase 10: Poll DiagStatus (11 times) ──────────────────────
            GetProcessorType();
            for (int i = 0; i < 10; i++) { Thread.Sleep(1000); GetProcessorType(); }

            // ── Phase 11: Re-write I/O config (REQ 1485–1494) ───────────
            // These files were backed up during upload (ReadChunkedSubElement reads all
            // file types including system files). Exact order from capture.
            foreach (var ioKey in new[] { (0x47, 0), (0x47, 2), (0x4D, 3), (0x49, 1) })
                WriteFileByElement(fileMap, ioKey.Item1, ioKey.Item2, ref bytesTransferred, ref filesCompleted, totalBytes, totalFiles);

            // ── Phase 12: Apply port configuration ────────────────────────
            ApplyPortConfiguration();

            // ── Phase 13: DiagStatus ───────────────────────────────────────
            GetProcessorType();

            // ── Phase 14: Return edit resource ────────────────────────────
            ReturnEditResource();

            // ── Phase 15: Verify port config (read ft=0x4A fn=1 el=4) ─────
            { var a = new DataAddress { FileType = 0x4A, FileNumber = 1, Element = 4 }; ReadRawDataWithChunking(ref a, 4, out _); }

            // ── Phase 16: Final DiagStatus ─────────────────────────────────
            GetProcessorType();

            // ── Phase 17: Full handshake reads (verification) ─────────────
            DoHandshakeReads();
        }
        finally
        {
            DisableEventFlag = false;
        }
    }

    // ─── File chunking helpers for file-based transfer ────────────────────

    private byte[] FileReadWithChunking(ushort tag, int byteOffset, int totalBytes)
    {
        if (byteOffset % PCCCConstants.Df1Limits.BytesPerWord != 0)
            throw new PCCCException("FileRead: byte offset must be even (word‑aligned).");

        using var ms = new MemoryStream(totalBytes);
        int bytesRemaining = totalBytes;
        int currentByteOffset = byteOffset;
        int maxChunkBytes = PCCCConstants.Df1Limits.MaxReadPayloadBytes;

        while (bytesRemaining > 0)
        {
            int toReadBytes = Math.Min(bytesRemaining, maxChunkBytes);
            int wordOffset = currentByteOffset / PCCCConstants.Df1Limits.BytesPerWord;
            byte[] chunk = _protocol.FileRead(tag, wordOffset, toReadBytes, (byte)MyNode, (byte)TargetNode);
            if (chunk == null || chunk.Length == 0)
                throw new PCCCException($"Empty chunk received at byte offset {currentByteOffset} (word offset {wordOffset}).");
            ms.Write(chunk, 0, chunk.Length);
            bytesRemaining -= chunk.Length;
            currentByteOffset += chunk.Length;
        }
        return ms.ToArray();
    }

    private void FileWriteWithChunking(ushort tag, int byteOffset, byte[] data)
    {
        if (byteOffset % PCCCConstants.Df1Limits.BytesPerWord != 0)
            throw new PCCCException("FileWrite: byte offset must be even (word‑aligned).");

        int bytesRemaining = data.Length;
        int currentByteOffset = byteOffset;
        int maxChunkBytes = PCCCConstants.Df1Limits.MaxWritePayloadBytes;

        while (bytesRemaining > 0)
        {
            int toWriteBytes = Math.Min(bytesRemaining, maxChunkBytes);
            int wordOffset = currentByteOffset / PCCCConstants.Df1Limits.BytesPerWord;
            int srcOffset = currentByteOffset - byteOffset;
            byte[] chunk = new byte[toWriteBytes];
            Array.Copy(data, srcOffset, chunk, 0, toWriteBytes);
            int sts = _protocol.FileWrite(tag, wordOffset, chunk, (byte)MyNode, (byte)TargetNode);
            if (sts != 0)
                throw new PCCCException($"FileWrite failed at byte offset {currentByteOffset}: {PCCCErrors.DecodeStatus(sts)}");
            bytesRemaining -= toWriteBytes;
            currentByteOffset += toWriteBytes;
        }
    }

    // ─── Public methods for file-based and diagnostic commands ────────────
    // These delegate directly to PCCCProtocol.

    public ushort OpenFile(int fileNumber, int fileType)
        => _protocol.OpenFile((byte)fileNumber, (byte)fileType, (byte)MyNode, (byte)TargetNode);

    public void CloseFile(ushort tag)
        => _protocol.CloseFile(tag, (byte)MyNode, (byte)TargetNode);

    public byte[] FileRead(ushort tag, int offset, int length)
        => _protocol.FileRead(tag, offset, length, (byte)MyNode, (byte)TargetNode);

    public int FileWrite(ushort tag, int offset, byte[] data)
        => _protocol.FileWrite(tag, offset, data, (byte)MyNode, (byte)TargetNode);

    public void GetEditResource()
        => _protocol.GetEditResource((byte)MyNode, (byte)TargetNode);

    public void ReturnEditResource()
        => _protocol.ReturnEditResource((byte)MyNode, (byte)TargetNode);

    public void UploadAllRequest()
        => _protocol.UploadAllRequest((byte)MyNode, (byte)TargetNode);

    public void UploadCompleted()
        => _protocol.UploadCompleted((byte)MyNode, (byte)TargetNode);

    public void DownloadAllRequest()
        => _protocol.DownloadAllRequest((byte)MyNode, (byte)TargetNode);

    public void DownloadCompleted()
        => _protocol.DownloadCompleted((byte)MyNode, (byte)TargetNode);

    public void ExecuteCommandList(byte[][] commands)
        => _protocol.ExecuteCommandList(commands, (byte)MyNode, (byte)TargetNode);

    public void ApplyPortConfiguration()
        => _protocol.ApplyPortConfiguration((byte)MyNode, (byte)TargetNode);

    public void InitializeMemory()
        => _protocol.InitializeMemory((byte)MyNode, (byte)TargetNode);

    public byte[] ReadDiagnosticCounters()
        => _protocol.ReadDiagnosticCounters((byte)MyNode, (byte)TargetNode);

    public void ResetDiagnosticCounters()
        => _protocol.ResetDiagnosticCounters((byte)MyNode, (byte)TargetNode);

    public byte ReadLinkParameters()
        => _protocol.ReadLinkParameters((byte)MyNode, (byte)TargetNode);

    public void SetLinkParameters(byte maxAddress)
        => _protocol.SetLinkParameters(maxAddress, (byte)MyNode, (byte)TargetNode);

    public byte[] Echo(byte[] data)
        => _protocol.Echo(data, (byte)MyNode, (byte)TargetNode);
}
