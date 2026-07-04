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
/// PCCC protocol handler for PLC‑5 family (1785-Lxx, 6008-LTV, 5130-RM).
/// 
/// Per Allen‑Bradley Publication 1770‑6.5.16:
///   - Chapter 7: Communication commands
///   - Chapter 10: Diagnostic status information (PLC‑5 status bytes, pages 10-20 to 10-23)
///   - Chapter 12: Uploading and downloading with PLC‑5 (pages 12-3 to 12-5, 12-8 to 12-10)
///   - Chapter 13: PLC‑5 logical binary addressing (pages 13-11 to 13-14)
///   - Typed Read (FNC 0x68) and Typed Write (FNC 0x67) pages 7-28 and 7-30
/// 
/// This implementation provides full data read/write access using Typed Read/Write
/// with logical binary addressing, supporting all SLC file types (N, B, F, T, C, R, ST, etc.)
/// </summary>
public class Plc5Handler : IPlcHandler
{
    private readonly IHandlerContext _context;
    private readonly PCCCProtocol _protocol;
    private int _processorType;
    private readonly ConcurrentDictionary<string, DataAddress> _addressCache = new ConcurrentDictionary<string, DataAddress>();
    private int _lastFileProgressPercent = -1;

    public Plc5Handler(IHandlerContext context, PCCCProtocol protocol, int initialProcessorType)
    {
        _context = context;
        _protocol = protocol;
        _processorType = initialProcessorType;
    }

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
        int percent = (int)((double)e.TotalBytesTransferred / e.GrandTotalBytes * 100);
        if (percent != _lastFileProgressPercent && (percent % 5 == 0 || percent == 100))
        {
            _lastFileProgressPercent = percent;
            _context.RaiseFileProgress(e);
        }
    }

    private DataAddress ParseAddress(string address) => _addressCache.GetOrAdd(address, PCCCParser.Parse);

    // ---------------------------------------------------------------------
    // Helper: Encode logical binary address for PLC-5 Typed Read/Write
    // ---------------------------------------------------------------------

    /// <summary>
    /// Encodes a logical binary address for PLC-5 Typed Read/Write commands.
    /// Format per 1770-6.5.16 Chapter 13, page 13-11:
    ///   [mask byte] [level1] [level2] ... [levelN]
    /// where mask byte bits:
    ///   bits 7-4: number of levels (1-8)
    ///   bit 3: 0 = last level is element, 1 = last level is sub-element (for structured)
    ///   bit 2-0: reserved (0)
    /// Levels are 1-byte each, with 0xFF extended to 3 bytes for values >= 255.
    /// For SLC-style files: level1=file number, level2=file type, level3=element, level4=sub-element.
    /// </summary>
    /// <param name="fileNumber">File number (0-255)</param>
    /// <param name="fileType">PLC-5 file type code (0x00-0x0F)</param>
    /// <param name="element">Element number (0-65535)</param>
    /// <param name="subElement">Sub-element number (0-65535, typically for Timer/Counter members)</param>
    /// <param name="isStructured">True if the data type has sub-element (Timer, Counter, Control, String)</param>
    public static byte[] EncodePlc5LogicalAddress(int fileNumber, int fileType, int element, int subElement, bool isStructured)
    {
        // Number of levels depends on whether the address points to a structured type
        int levelCount = isStructured ? 4 : 3;
        
        Span<byte> buffer = stackalloc byte[14]; // max 4 levels + mask (1 + 4*3 + 1)
        int idx = 0;

        int maskHigh = (levelCount << 4);
        int maskLow = isStructured ? 0x08 : 0x00;
        buffer[idx++] = (byte)(maskHigh | maskLow);

        int[] values = { fileNumber, fileType, element, subElement };
        for (int i = 0; i < levelCount; i++)
        {
            int val = values[i];
            if (val < 255)
                buffer[idx++] = (byte)val;
            else
            {
                buffer[idx++] = 0xFF;
                buffer[idx++] = (byte)(val & 0xFF);
                buffer[idx++] = (byte)((val >> 8) & 0xFF);
            }
        }

        return buffer.Slice(0, idx).ToArray();
    }

    // ---------------------------------------------------------------------
    // Chunked read/write helpers using Typed Read/Write
    // ---------------------------------------------------------------------

    /// <summary>
    /// Reads raw data from PLC-5 using Typed Read (FNC 0x68) with automatic chunking.
    /// </summary>
    private byte[] ReadRawDataWithChunking(ref DataAddress addr, int numberOfBytes, out int finalStatus)
    {
        finalStatus = 0;
        int filePosition = 0;
        byte[] result = ArrayPool<byte>.Shared.Rent(numberOfBytes);
        try
        {
            int bytesPerElem = addr.BytesPerElements;

            bool isStructured = addr.FileType == (byte)PCCCConstants.SlcFileTypeCode.Timer ||
                                addr.FileType == (byte)PCCCConstants.SlcFileTypeCode.Counter ||
                                addr.FileType == (byte)PCCCConstants.SlcFileTypeCode.Control ||
                                addr.FileType == (byte)PCCCConstants.SlcFileTypeCode.String;

            while (filePosition < numberOfBytes && finalStatus == 0)
            {
                int maxChunkBytes = PCCCConstants.Df1Limits.MaxReadPayloadPlc5;
                int remainingBytes = numberOfBytes - filePosition;
                int chunkBytes = Math.Min(remainingBytes, maxChunkBytes);
                
                if (isStructured)
                {
                    int elemAlign = (chunkBytes + bytesPerElem - 1) / bytesPerElem * bytesPerElem;
                    if (elemAlign > maxChunkBytes) elemAlign -= bytesPerElem;
                    chunkBytes = Math.Max(bytesPerElem, elemAlign);
                }
                
                int chunkElements = chunkBytes / bytesPerElem;
                int currentElement = addr.Element + (filePosition / bytesPerElem);
                int subElementOffset = addr.SubElement + ((filePosition % bytesPerElem) / PCCCConstants.Df1Limits.BytesPerWord);
                
                byte[] logicalAddress = EncodePlc5LogicalAddress(
                    addr.FileNumber, addr.FileType, currentElement, subElementOffset, isStructured);
                
                var req = PCCCMessage.CreateTypedReadRequest(
                    logicalAddress, chunkElements, 0, (byte)MyNode, (byte)TargetNode);
                var reply = _protocol.SendRequest(req, out int sts);
                
                if (sts != PCCCConstants.Sts.Success || reply?.Data == null)
                {
                    finalStatus = sts;
                    break;
                }
                
                int bytesRead = Math.Min(chunkBytes, reply.Data.Length);
                Array.Copy(reply.Data, 0, result, filePosition, bytesRead);
                filePosition += bytesRead;
            }

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
    /// Writes raw data to PLC-5 using Typed Write (FNC 0x67) with automatic chunking.
    /// </summary>
    private int WriteRawDataWithChunking(DataAddress addr, byte[] dataToWrite)
    {
        if (addr.FileType == 0) return -5;
        int filePosition = 0;
        int reply = 0;
        int bytesPerElem = addr.BytesPerElements;

        bool isStructured = addr.FileType == (byte)PCCCConstants.SlcFileTypeCode.Timer ||
                            addr.FileType == (byte)PCCCConstants.SlcFileTypeCode.Counter ||
                            addr.FileType == (byte)PCCCConstants.SlcFileTypeCode.Control ||
                            addr.FileType == (byte)PCCCConstants.SlcFileTypeCode.String;

        while (filePosition < dataToWrite.Length && reply == 0)
        {
            int maxChunkBytes = PCCCConstants.Df1Limits.MaxWritePayloadPlc5;
            int remainingBytes = dataToWrite.Length - filePosition;
            int chunkBytes = Math.Min(remainingBytes, maxChunkBytes);
            
            if (isStructured)
            {
                int elemAlign = (chunkBytes + bytesPerElem - 1) / bytesPerElem * bytesPerElem;
                if (elemAlign > maxChunkBytes) elemAlign -= bytesPerElem;
                chunkBytes = Math.Max(bytesPerElem, elemAlign);
            }
            
            int currentElement = addr.Element + (filePosition / bytesPerElem);
            int subElementOffset = addr.SubElement + ((filePosition % bytesPerElem) / PCCCConstants.Df1Limits.BytesPerWord);
            
            byte[] logicalAddress = EncodePlc5LogicalAddress(
                addr.FileNumber, addr.FileType, currentElement, subElementOffset, isStructured);
            
            byte[] chunkData = new byte[chunkBytes];
            Array.Copy(dataToWrite, filePosition, chunkData, 0, chunkBytes);
            
            int chunkElements = chunkBytes / bytesPerElem;
            var req = PCCCMessage.CreateTypedWriteRequest(
                logicalAddress, chunkData, chunkElements, 0, (byte)MyNode, (byte)TargetNode);
            
            if (AsyncMode)
            {
                _protocol.SendRequestAsync(req);
                reply = 0;
            }
            else
            {
                var resp = _protocol.SendRequest(req, out int sts);
                reply = sts;
            }
            filePosition += chunkBytes;
        }

        if (reply == 0) return 0;
        throw new PCCCException(PCCCErrors.DecodeStatus(reply));
    }

    // ---------------------------------------------------------------------
    // IPlcHandler implementation (diagnostics, mode, forces)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Gets the processor type code from diagnostic status data.
    /// For PLC-5 processors, checks if the type extender has high nibble 0x0E,
    /// indicating that the actual processor type is in the expansion byte.
    /// </summary>
    /// <returns>Processor type code, or 0 if failed.</returns>
    public int GetProcessorType()
    {
        var req = PCCCMessage.CreateDiagnosticStatusRequest(0, (byte)MyNode, (byte)TargetNode);
        var reply = _protocol.SendRequest(req, out int sts);
        if (sts != PCCCConstants.Sts.Success || reply?.Data == null || reply.Data.Length < 4)
            return 0;

        // Use constants from PCCCConstants for offsets and masks (no magic numbers)
        byte typeExtender = reply.Data[PCCCConstants.ResponseOffsets.DiagnosticStatus.TypeExtenderOffset];
        byte expansionByte = reply.Data[PCCCConstants.ResponseOffsets.DiagnosticStatus.ExpansionByteOffset];
        
        // Check if high nibble of typeExtender equals 0x0E (indicates expansion byte follows)
        int highNibble = (typeExtender & PCCCConstants.ResponseOffsets.DiagnosticStatus.HighNibbleMask) 
                        >> PCCCConstants.ResponseOffsets.DiagnosticStatus.HighNibbleShift;
        bool hasExpansion = (highNibble == PCCCConstants.ResponseOffsets.DiagnosticStatus.ExpansionIndicatorHighNibble);

        _processorType = hasExpansion ? expansionByte : typeExtender;
        return _processorType;
    }

    public byte[]? GetDiagnosticStatusRaw()
    {
        var req = PCCCMessage.CreateDiagnosticStatusRequest(0, (byte)MyNode, (byte)TargetNode);
        var reply = _protocol.SendRequest(req, out int sts);
        if (sts != PCCCConstants.Sts.Success || reply?.Data == null)
            return null;
        return reply.Data;
    }

    public void SetRunMode()
    {
        byte modeValue = 0x02;
        var req = PCCCMessage.CreateChangeModeRequest(modeValue, true, 0, (byte)MyNode, (byte)TargetNode);
        _protocol.SendRequest(req, out int sts);
        if (sts != PCCCConstants.Sts.Success)
            throw new PCCCException($"SetRunMode failed: {PCCCErrors.DecodeStatus(sts)}");
    }

    public void SetProgramMode()
    {
        // PLC-5 Set CPU Mode (FNC 0x3A) with mode value 0x01 = Remote Program
        // Ref: 1770-6.5.16 page 7-26
        byte modeValue = 0x01;   // Remote Program
        var req = PCCCMessage.CreateChangeModeRequest(modeValue, true, 0, (byte)MyNode, (byte)TargetNode);
        _protocol.SendRequest(req, out int sts);
        if (sts != PCCCConstants.Sts.Success)
            throw new PCCCException($"SetProgramMode failed: {PCCCErrors.DecodeStatus(sts)}");
    }

    public int SetCpuMode(byte modeValue)
    {
        var req = PCCCMessage.CreateChangeModeRequest(modeValue, true, 0, (byte)MyNode, (byte)TargetNode);
        _protocol.SendRequest(req, out int sts);
        return sts;
    }

    public int GetRunMode()
    {
        var req = PCCCMessage.CreateDiagnosticStatusRequest(0, (byte)MyNode, (byte)TargetNode);
        var reply = _protocol.SendRequest(req, out int sts);
        if (sts != PCCCConstants.Sts.Success || reply?.Data == null || reply.Data.Length < 1)
            return -1;
        byte statusByte = reply.Data[0];
        int modeBits = statusByte & 0x07;
        return (modeBits == 2 || modeBits == 6) ? 1 : 0;
    }

    public int DisableForces()
    {
        var req = PCCCMessage.CreateDisableForcesRequest(0, (byte)MyNode, (byte)TargetNode);
        _protocol.SendRequest(req, out int sts);
        return sts;
    }

    public void EnableForces()
        => throw new NotSupportedException("EnableForces (FNC 0x42) is not supported by PLC-5.");

    public void ClearForces()
        => throw new NotSupportedException("ClearForces (FNC 0x43) is not supported by PLC-5.");

    // ---------------------------------------------------------------------
    // Raw Word API (Modbus-style) for PLC-5
    // ---------------------------------------------------------------------

    /// <summary>
    /// Reads raw 16-bit words from the specified PCCC address using PLC-5 Typed Read.
    /// This is the primary read API. No interpretation is performed.
    /// For String (ST) files, throws NotSupportedException.
    /// </summary>
    public ushort[] ReadWords(string startAddress, int numberOfWords)
    {
        DataAddress p = ParseAddress(startAddress);
        if (p.FileType == 0) throw new PCCCException("Invalid Address");
        if (p.FileType == (byte)PCCCConstants.SlcFileTypeCode.String)
            throw new NotSupportedException("ReadWords does not support String (ST) files. Use ReadAny instead.");

        // PLC-5 String file override
        if (p.FileType == (byte)PCCCConstants.SlcFileTypeCode.String)
            p.BytesPerElements = PCCCConstants.Df1Limits.Plc5StringElementBytes;

        int bytesPerElem = p.BytesPerElements;
        if (p.SubElement > 0 && (p.FileType == (byte)PCCCConstants.SlcFileTypeCode.Timer ||
                                  p.FileType == (byte)PCCCConstants.SlcFileTypeCode.Counter ||
                                  p.FileType == (byte)PCCCConstants.SlcFileTypeCode.Control))
            bytesPerElem = PCCCConstants.Df1Limits.BytesPerWord;

        int totalBytesNeeded = numberOfWords * 2;
        int numberOfElements = (totalBytesNeeded + bytesPerElem - 1) / bytesPerElem;
        int numberOfBytesToRead = numberOfElements * bytesPerElem;

        // Byte-count adjustment for Timer/Counter/Control sub-element reads
        // ("T4:0.PRE", "C5:0.ACC", "R6:0.LEN") on PLC-5.
        //
        // Origin (reconstructed — not documented elsewhere): an earlier
        // version indexed each element at a 6-byte (3-word) stride
        // (`offset = i*6`), matching a Timer/Counter/Control element's true
        // layout (status word + 2 sub-elements). That was simplified to the
        // same 1-word-per-index model SLC uses (bytesPerElem forced to
        // BytesPerWord above); this line makes the simplified path request
        // the same total bytes the old stride would have: for N words,
        // `(N*2*3)-4 = 6N-4` bytes — enough to span N full 3-word elements.
        if (p.SubElement > 0 && (p.FileType == (byte)PCCCConstants.SlcFileTypeCode.Timer ||
                                  p.FileType == (byte)PCCCConstants.SlcFileTypeCode.Counter ||
                                  p.FileType == (byte)PCCCConstants.SlcFileTypeCode.Control))
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
    /// Writes raw 16-bit words to the specified PCCC address using PLC-5 Typed Write.
    /// </summary>
    public void WriteWords(string startAddress, ushort[] data)
    {
        if (data == null || data.Length == 0) return;
        DataAddress p = ParseAddress(startAddress);
        if (p.FileType == 0) throw new PCCCException("Invalid Address");
        if (p.FileType == (byte)PCCCConstants.SlcFileTypeCode.String)
            throw new PCCCException("Use WriteData(string, string) for ST files.");

        // PLC-5 String override
        if (p.FileType == (byte)PCCCConstants.SlcFileTypeCode.String)
            p.BytesPerElements = PCCCConstants.Df1Limits.Plc5StringElementBytes;

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
    /// Reads String (ST) files for PLC-5 and returns the decoded strings.
    /// PLC-5 ST element: 88 bytes (44 words)
    /// word 0 (byte 0-1): max length = 82 (constant)
    /// word 1 (byte 2-3): current length
    /// word 2+ (byte 4+): chars packed 2/word, low byte = even index char, high byte = odd index char
    /// </summary>
    private string[] ReadAnyString(string startAddress, int numberOfElements)
    {
        DataAddress p = ParseAddress(startAddress);
        if (p.FileType == 0) throw new PCCCException("Invalid Address");

        short arrayElements = (short)(numberOfElements - 1);
        if (arrayElements < 0) arrayElements = 0;

        int bytesPerElem = PCCCConstants.Df1Limits.Plc5StringElementBytes; // 88 bytes
        // Override BytesPerElements so ReadRawDataWithChunking aligns chunks
        // to PLC-5 element boundaries (88 bytes) not SLC ones (84 bytes).
        p.BytesPerElements = bytesPerElem;
        int numberOfBytes = (arrayElements + 1) * bytesPerElem;

        byte[] returnedData = ReadRawDataWithChunking(ref p, numberOfBytes, out int reply);
        if (reply != 0)
            throw new PCCCException(PCCCErrors.DecodeStatus(reply));

        string[] result = new string[arrayElements + 1];
        for (int i = 0; i <= arrayElements; i++)
        {
            int baseOffset = i * bytesPerElem;
            // Guard: PLC may return fewer bytes than requested (e.g. empty string file).
            if (baseOffset + PCCCConstants.Df1Limits.BytesPerWord * 2 > returnedData.Length)
            {
                result[i] = "";
                continue;
            }
            int strLen = BitConverter.ToInt16(returnedData, baseOffset + PCCCConstants.Df1Limits.BytesPerWord);
            if (strLen < 0) strLen = 0;
            if (strLen > PCCCConstants.Df1Limits.MaxStringLength)
                strLen = PCCCConstants.Df1Limits.MaxStringLength;
            var sb = new StringBuilder();
            for (int j = 0; j < strLen; j++)
            {
                int wordOffset = baseOffset + 4 + (j / 2) * PCCCConstants.Df1Limits.BytesPerWord;
                if (wordOffset + 1 >= returnedData.Length) break;  // truncated response
                char c = (j % 2 == 0)
                    ? (char)returnedData[wordOffset]        // low byte = even index char
                    : (char)returnedData[wordOffset + 1];   // high byte = odd index char
                if (c == 0) break;
                sb.Append(c);
            }
            result[i] = sb.ToString();
        }
        return result;
    }

    // ---------------------------------------------------------------------
    // Read/Write operations using Typed Read/Write
    // ---------------------------------------------------------------------

    public string[] ReadAny(string startAddress, int numberOfElements)
    {
        DataAddress p = ParseAddress(startAddress);
        if (p.FileType == 0) throw new PCCCException("Invalid Address");

        // For String files, use original decoding logic (unchanged)
        if (p.FileType == (byte)PCCCConstants.SlcFileTypeCode.String)
            return ReadAnyString(startAddress, numberOfElements);

        // PLC-5 Float and Long file handling
        if (p.FileType == (byte)PCCCConstants.SlcFileTypeCode.String)
            p.BytesPerElements = PCCCConstants.Df1Limits.Plc5StringElementBytes;

        int bytesPerElem = p.BytesPerElements;
        if (p.SubElement > 0 && (p.FileType == (byte)PCCCConstants.SlcFileTypeCode.Timer ||
                                  p.FileType == (byte)PCCCConstants.SlcFileTypeCode.Counter ||
                                  p.FileType == (byte)PCCCConstants.SlcFileTypeCode.Control))
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
                case (byte)PCCCConstants.SlcFileTypeCode.Binary:
                case (byte)PCCCConstants.SlcFileTypeCode.Output:
                case (byte)PCCCConstants.SlcFileTypeCode.OutputAlt:
                case (byte)PCCCConstants.SlcFileTypeCode.Input:
                case (byte)PCCCConstants.SlcFileTypeCode.InputAlt:
                    // Bit/Binary, Output, and Input files are bit patterns, not
                    // signed quantities — bit 15 is data, not a sign bit.
                    // N (Integer) and other numeric files are genuinely signed
                    // 16-bit per AB spec, so they keep the (short) cast below.
                    result[i] = rawWords[offset].ToString(CultureInfo.InvariantCulture);
                    break;
                default:
                    result[i] = ((short)rawWords[offset]).ToString(CultureInfo.InvariantCulture);
                    break;
            }
        }

        if (p.BitNumber >= 0 && p.BitNumber < 16)
        {
            // Bit extraction logic (unchanged)
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

    public string ReadAny(string startAddress) => ReadAny(startAddress, 1)[0];

    public int[] ReadInt(string startAddress, int numberOfElements)
    {
        string[] result = ReadAny(startAddress, numberOfElements);
        int[] ints = new int[result.Length];
        for (int i = 0; i < result.Length; i++) ints[i] = Convert.ToInt32(result[i]);
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
    /// <exception cref="NotSupportedException">Thrown for String (ST) files.</exception>
    public double[] ReadAnyValues(string startAddress, int numberOfElements)
    {
        DataAddress p = ParseAddress(startAddress);
        if (p.FileType == 0) throw new PCCCException("Invalid Address");
        if (p.FileType == (byte)PCCCConstants.SlcFileTypeCode.String)
            throw new NotSupportedException("ReadAnyValues does not support String (ST) files. Use ReadAny instead.");

        // Determine words per element based on file type
        int bytesPerElem = p.BytesPerElements;
        if (p.SubElement > 0 && (p.FileType == (byte)PCCCConstants.SlcFileTypeCode.Timer ||
                                  p.FileType == (byte)PCCCConstants.SlcFileTypeCode.Counter ||
                                  p.FileType == (byte)PCCCConstants.SlcFileTypeCode.Control))
            bytesPerElem = PCCCConstants.Df1Limits.BytesPerWord; // 2 bytes per sub-element

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
                    // Two words -> float (little-endian)
                    result[i] = WordConverter.WordsToFloat(rawWords[offset], rawWords[offset + 1]);
                    break;
                case (byte)PCCCConstants.SlcFileTypeCode.Long:
                    // Two words -> int (little-endian)
                    result[i] = WordConverter.WordsToInt32(rawWords[offset], rawWords[offset + 1]);
                    break;
                case (byte)PCCCConstants.SlcFileTypeCode.Timer:
                case (byte)PCCCConstants.SlcFileTypeCode.Counter:
                    // One word -> short (signed 16-bit)
                    result[i] = (short)rawWords[offset];
                    break;
                case (byte)PCCCConstants.SlcFileTypeCode.Binary:
                case (byte)PCCCConstants.SlcFileTypeCode.Output:
                case (byte)PCCCConstants.SlcFileTypeCode.OutputAlt:
                case (byte)PCCCConstants.SlcFileTypeCode.Input:
                case (byte)PCCCConstants.SlcFileTypeCode.InputAlt:
                    // Bit pattern, not a signed quantity — see ReadAny above.
                    result[i] = rawWords[offset];
                    break;
                default:
                    // Default: one word -> short (signed 16-bit)
                    result[i] = (short)rawWords[offset];
                    break;
            }
        }

        // Bit-level extraction (for addresses like "B3:0/5")
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

    public int ReadModifyWrite(string[] addresses, ushort[] andMasks, ushort[] orMasks)
        => throw new NotSupportedException(
            "ReadModifyWrite for PLC-5 requires PLC-5 logical binary addressing. " +
            "SLC-style addressing is not supported. See 1770-6.5.16 page 7-20.");

    public string WriteData(string startAddress, int dataToWrite)
    {
        DataAddress p = ParseAddress(startAddress);
        if (p.FileType == 0) throw new PCCCException("Invalid Address");

        // Bit-level write
        if (p.BitNumber >= 0 && p.BitNumber < 16)
        {
            // Build address without bit to read the whole word
            string wordAddress = startAddress.Split('/')[0];
            string[] current = ReadAny(wordAddress, 1);
            int word = int.Parse(current[0], CultureInfo.InvariantCulture);
            // Modify the specified bit
            if (dataToWrite != 0)
                word |= (1 << p.BitNumber);
            else
                word &= ~(1 << p.BitNumber);
            // Write back using word write
            int status = WriteData(wordAddress, 1, new int[] { word });
            return status == 0 ? string.Empty : PCCCErrors.DecodeStatus(status);
        }

        // Normal word write
        int normalStatus = WriteData(startAddress, 1, new int[] { dataToWrite });
        return normalStatus == 0 ? string.Empty : PCCCErrors.DecodeStatus(normalStatus);
    }

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

    public int WriteData(string startAddress, float dataToWrite)
        => WriteData(startAddress, 1, new float[] { dataToWrite });

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

    public int WriteData(string startAddress, string dataToWrite)
    {
        if (string.IsNullOrEmpty(dataToWrite)) return 0;
        if (dataToWrite.Length > PCCCConstants.Df1Limits.MaxStringLength) 
            dataToWrite = dataToWrite.Substring(0, PCCCConstants.Df1Limits.MaxStringLength);

        DataAddress p = ParseAddress(startAddress);
        if (p.FileType == 0) throw new PCCCException("Invalid Address");

        // --- ST file (PLC-5 specific, 88 bytes per element) ---
        if (p.FileType == (byte)PCCCConstants.SlcFileTypeCode.String)
        {
            // PLC-5 ST element: 88 bytes (44 words)
            // word 0 (byte 0-1): max length = 82 (constant)
            // word 1 (byte 2-3): current length
            // word 2+ (byte 4+): chars packed 2/word, low byte = even index char, high byte = odd index char
            p.BytesPerElements = PCCCConstants.Df1Limits.Plc5StringElementBytes;
            byte[] stElement = new byte[PCCCConstants.Df1Limits.Plc5StringElementBytes];
            stElement[0] = PCCCConstants.Df1Limits.MaxStringLength;          // max length low byte
            stElement[1] = 0;                                                 // max length high byte
            stElement[2] = (byte)(dataToWrite.Length & 0xFF);                // current length low
            stElement[3] = (byte)((dataToWrite.Length >> 8) & 0xFF);         // current length high
            for (int i = 0; i < dataToWrite.Length; i++)
            {
                int wordOffset = 4 + (i / 2) * PCCCConstants.Df1Limits.BytesPerWord;
                if (i % 2 == 0)
                    stElement[wordOffset] = (byte)dataToWrite[i];     // low byte
                else
                    stElement[wordOffset + 1] = (byte)dataToWrite[i]; // high byte
            }
            return WriteRawDataWithChunking(p, stElement);
        }
        else
        {
            // --- Non-ST file: use WriteWords for consistency
            int[]? words = StringConverter.StringToWords(dataToWrite);
            if (words == null) return -1;
            ushort[] ushortWords = new ushort[words.Length];
            for (int i = 0; i < words.Length; i++)
                ushortWords[i] = (ushort)words[i];
            WriteWords(startAddress, ushortWords);
            return 0;
        }
    }

    public byte[] WordRangeRead(byte[] logicalAddress, int wordOffset, int sizeWords)
    {
        if (logicalAddress == null || logicalAddress.Length == 0)
            throw new ArgumentException("Logical address cannot be null or empty.", nameof(logicalAddress));
        if (sizeWords <= 0)
            throw new ArgumentOutOfRangeException(nameof(sizeWords), "Size must be positive.");

        return _protocol.WordRangeRead(logicalAddress, wordOffset, sizeWords,
            (byte)MyNode, (byte)TargetNode);
    }

    public void WordRangeWrite(byte[] logicalAddress, int wordOffset, byte[] data)
    {
        if (logicalAddress == null || logicalAddress.Length == 0)
            throw new ArgumentException("Logical address cannot be null or empty.", nameof(logicalAddress));
        if (data == null || data.Length == 0 || data.Length % 2 != 0)
            throw new ArgumentException("Data must be non‑empty and have even number of bytes.", nameof(data));

        _protocol.WordRangeWrite(logicalAddress, wordOffset, data,
            (byte)MyNode, (byte)TargetNode);
    }

    // ---------------------------------------------------------------------
    // Unsupported methods
    // ---------------------------------------------------------------------

    /// <summary>
    /// PLC-5 upload uses 'upload all request' (FNC 0x53) + 'read bytes physical' (FNC 0x17).
    /// See 1770-6.5.16 Chapter 12.
    /// </summary>
    public Collection<PLCFileDetails> UploadProgramData()
    {
        DisableEventFlag = true;
        try
        {
            // Step 1: UploadAllRequest — dapatkan segment list
            byte[] segReply = _protocol.UploadAllRequest((byte)MyNode, (byte)TargetNode);
            if (segReply == null || segReply.Length < 1)
                throw new PCCCException("UploadAllRequest: invalid or empty reply.");

            int idx = 0;
            int uploadCount = segReply[idx++];
            if (segReply.Length < 1 + uploadCount * 8 + 1)
                throw new PCCCException($"UploadAllRequest: reply too short for {uploadCount} segments.");

            var segments = new List<(int start, int end)>();
            for (int i = 0; i < uploadCount; i++)
            {
                int start = segReply[idx]     | (segReply[idx+1] << 8) |
                            (segReply[idx+2] << 16) | (segReply[idx+3] << 24);
                int end   = segReply[idx+4]   | (segReply[idx+5] << 8) |
                            (segReply[idx+6] << 16) | (segReply[idx+7] << 24);
                segments.Add((start, end));
                idx += 8;
            }
            // skip comparable segments (C dan D) — tidak digunakan untuk upload

            // Step 2: ReadBytesPhysical per segment, per chunk, with progress
            const int maxChunk = 128;  // 128 bytes per chunk (max allowed), must be even
            var files = new Collection<PLCFileDetails>();
            long grandTotalBytes = 0;
            foreach (var (segStart, segEnd) in segments)
            {
                int totalBytes = segEnd - segStart + 1;
                if (totalBytes % 2 != 0) totalBytes--;
                grandTotalBytes += totalBytes;
            }

            long totalBytesTransferred = 0;
            int filesCompleted = 0;
            int totalFiles = segments.Count;
            _lastFileProgressPercent = -1;

            foreach (var (segStart, segEnd) in segments)
            {
                int totalBytes = segEnd - segStart + 1;
                if (totalBytes % 2 != 0) totalBytes--;

                using var ms = new MemoryStream(totalBytes);
                int offset = 0;

                while (offset < totalBytes)
                {
                    int chunk = Math.Min(maxChunk, totalBytes - offset);
                    if (chunk % 2 != 0) chunk--;
                    if (chunk <= 0) break;

                    byte[] chunkData = _protocol.ReadBytesPhysical(
                        segStart + offset, chunk, (byte)MyNode, (byte)TargetNode);

                    if (chunkData == null || chunkData.Length == 0)
                        throw new PCCCException($"ReadBytesPhysical failed at offset 0x{segStart+offset:X}.");

                    ms.Write(chunkData, 0, chunkData.Length);
                    offset += chunkData.Length;
                    totalBytesTransferred += chunkData.Length;

                    // Report progress
                    OnFileProgress(new PCCCComm.FileProgressEventArgs
                    {
                        FileNumber = filesCompleted,
                        FileType = 0,
                        FileSizeBytes = totalBytes,
                        FilesCompleted = filesCompleted + 1,
                        TotalFiles = totalFiles,
                        TotalBytesTransferred = totalBytesTransferred,
                        GrandTotalBytes = grandTotalBytes
                    });
                }

                files.Add(new PLCFileDetails
                {
                    FileNumber      = filesCompleted,
                    FileType        = 0x00,
                    NumberOfBytes   = (int)ms.Length,
                    Data            = ms.ToArray(),
                    PhysicalAddress = segStart,   // per spec §12-5: store physical address
                });
                filesCompleted++;
            }

            // Step 3: UploadCompleted
            _protocol.UploadCompleted((byte)MyNode, (byte)TargetNode);
            return files;
        }
        finally
        {
            DisableEventFlag = false;
        }
    }

    /// <summary>
    /// PLC-5 download uses 'download all request' (FNC 0x50) + 'write bytes physical' (FNC 0x18).
    /// See 1770-6.5.16 Chapter 12.
    /// </summary>
    public void DownloadProgramData(Collection<PLCFileDetails> files)
    {
        if (files == null || files.Count == 0)
            throw new ArgumentException("No data to download.");

        DisableEventFlag = true;
        try
        {
            // Step 1: DownloadAllRequest
            // PLC-5 returns empty reply for DownloadAllRequest (FNC 0x50); return value not used.
            _protocol.DownloadAllRequest((byte)MyNode, (byte)TargetNode);

            // Step 2: WriteBytesPhysical per segment, per chunk, with progress
            const int maxChunk = 128; // Max 238 bytes per spec (must be even)
            long grandTotalBytes = files.Sum(f => f.Data?.Length ?? 0);
            long totalBytesTransferred = 0;
            int filesCompleted = 0;
            int totalFiles = files.Count;
            _lastFileProgressPercent = -1;

            foreach (var file in files)
            {
                if (file.Data == null || file.Data.Length == 0) continue;

                int totalBytes = file.Data.Length;
                // Per spec §12-5 Procedure 2: use the physical address stored during upload.
                // UploadProgramData stores segStart in PLCFileDetails.PhysicalAddress.
                int physBase = file.PhysicalAddress;

                int offset = 0;

                while (offset < totalBytes)
                {
                    int chunk = Math.Min(maxChunk, totalBytes - offset);
                    if (chunk % 2 != 0) chunk--; // must be even
                    if (chunk <= 0) break;

                    var chunkData = new byte[chunk];
                    Array.Copy(file.Data, offset, chunkData, 0, chunk);

                    bool ok = _protocol.WriteBytesPhysical(
                        physBase + offset, chunkData, (byte)MyNode, (byte)TargetNode);

                    if (!ok)
                        throw new PCCCException(
                            $"WriteBytesPhysical failed at address 0x{physBase + offset:X8}.");

                    offset += chunk;
                    totalBytesTransferred += chunk;

                    OnFileProgress(new PCCCComm.FileProgressEventArgs
                    {
                        FileNumber = filesCompleted,
                        FileType = 0,
                        FileSizeBytes = totalBytes,
                        FilesCompleted = filesCompleted + 1,
                        TotalFiles = totalFiles,
                        TotalBytesTransferred = totalBytesTransferred,
                        GrandTotalBytes = grandTotalBytes
                    });
                }
                filesCompleted++;
            }

            // Step 3: DownloadCompleted
            _protocol.DownloadCompleted((byte)MyNode, (byte)TargetNode);
        }
        finally
        {
            DisableEventFlag = false;
        }
    }

    public int GetSlotCount() => throw new NotSupportedException("I/O config not yet implemented.");
    public IOConfig[] GetIOConfig() => throw new NotSupportedException("I/O config not yet implemented.");
    /// <summary>
    /// Reads the data file directory from PLC-5 using Word Range Read (FNC 0x01).
    ///
    /// PLC-5 directory is accessed via flat physical memory addressing.
    /// Step 1: Read 1 word at flat offset 35 (byte 70) = directory size.
    /// Step 2: Read entire directory from flat offset 0.
    /// Step 3: Parse entries at offset 79, 10 bytes each:
    ///   [fileType(1)] [sizeBytes(2,LE)] [fileNum(1)] [addr(4)] [flags(2)]
    /// Offset 52-53 = number of data files.
    ///
    /// Ref: AB Publication 1770-6.5.16, Chapter 10 (PLC-5 directory layout).
    ///      Word Range Read: CMD=0x0F, FNC=0x01.
    /// </summary>
    public DataFileDetails[] GetDataMemory()
    {
        // Logical address for PLC-5 directory: file 0, type 0x24 (directory)
        // EncodePlc5LogicalAddress(fileNum=0, fileType=0x24, element=0, subElement=0)
        byte[] dirAddr = EncodePlc5LogicalAddress(0, 0x24, 0, 0, false);

        // Step 1: read 1 word at word offset 35 (byte offset 70) = dirSize field
        byte[] sizeData = WordRangeRead(dirAddr, 35, 1);
        if (sizeData == null || sizeData.Length < 2)
            throw new PCCCException("PLC-5 GetDataMemory: failed to read directory size");

        int dirSize = sizeData[0] | (sizeData[1] << 8);
        if (dirSize <= 0 || dirSize > 65535)
            throw new PCCCException($"PLC-5 GetDataMemory: invalid directory size {dirSize}");

        int dirWords = (dirSize + 1) / 2;

        // Step 2: read entire directory with chunking
        // WordRangeRead max = MaxReadPayloadPlc5 / 2 words per request
        const int maxWordsPerChunk = PCCCConstants.Df1Limits.MaxReadPayloadPlc5 / 2;  // 118
        byte[] fzd = new byte[dirWords * 2];
        int wordsRead = 0;
        while (wordsRead < dirWords)
        {
            int chunk = Math.Min(maxWordsPerChunk, dirWords - wordsRead);
            byte[] part = WordRangeRead(dirAddr, wordsRead, chunk);
            if (part == null || part.Length == 0)
                throw new PCCCException("PLC-5 GetDataMemory: failed to read directory chunk");
            Array.Copy(part, 0, fzd, wordsRead * 2, part.Length);
            wordsRead += chunk;
        }
        if (fzd.Length < PCCCConstants.ResponseOffsets.FileDirectory.StartOffsetDefault)
            throw new PCCCException("PLC-5 GetDataMemory: directory too small");

        // Step 3: parse — same layout as SLC default
        int numberOfDataFiles = fzd[PCCCConstants.ResponseOffsets.FileDirectory.NumberOfDataFilesLo]
                              | (fzd[PCCCConstants.ResponseOffsets.FileDirectory.NumberOfDataFilesHi] << 8);

        var dataFiles = new System.Collections.Generic.List<DataFileDetails>();
        int pos       = PCCCConstants.ResponseOffsets.FileDirectory.StartOffsetDefault;   // 79
        const int bpr = PCCCConstants.ResponseOffsets.FileDirectory.BytesPerEntryDefault; // 10
        int parsed    = 0;

        while (parsed < numberOfDataFiles && pos + bpr <= fzd.Length)
        {
            byte fileTypeByte = fzd[pos];
            // Valid PLC-5 data file types: 0x82–0x9F
            if (fileTypeByte > 0x81 && fileTypeByte < 0x9F)
            {
                int sizeBytes = fzd[pos + 1] | (fzd[pos + 2] << 8);
                int fileNumber = fzd[pos + 3];
                string ftStr = PCCCConstants.SlcFileTypeInfo.GetTypeName(
                    (PCCCConstants.SlcFileTypeCode)fileTypeByte);

                // Read element size from directory, not from static lookup
                int bpe = fzd[pos + 5];   // byte offset 5 is elemSize
                if (bpe == 0) bpe = 2;    // fallback safety

                dataFiles.Add(new DataFileDetails
                {
                    FileType = ftStr,
                    NumberOfElements = bpe > 0 ? sizeBytes / bpe : 0,
                    FileNumber = fileNumber
                });
            }
            parsed++;
            pos += bpr;
        }
        return dataFiles.ToArray();
    }
    public DataFileDetails[] GetML1500DataMemory() => throw new NotSupportedException("ML1500 specific method not applicable.");
    public ushort OpenFile(int fileNumber, int fileType) => throw new NotSupportedException("File-based operations not supported.");
    public void CloseFile(ushort tag) => throw new NotSupportedException("File-based operations not supported.");
    public byte[] FileRead(ushort tag, int offset, int length) => throw new NotSupportedException("File-based operations not supported.");
    public int FileWrite(ushort tag, int offset, byte[] data) => throw new NotSupportedException("File-based operations not supported.");
    public void GetEditResource() => throw new NotSupportedException("Edit resource not yet implemented.");
    public void ReturnEditResource() => throw new NotSupportedException("Edit resource not yet implemented.");
    public void UploadAllRequest() => throw new NotSupportedException("Use UploadProgramData().");
    public void UploadCompleted() => throw new NotSupportedException("Use UploadProgramData().");
    public void DownloadAllRequest() => throw new NotSupportedException("Use DownloadProgramData().");
    public void DownloadCompleted() => throw new NotSupportedException("Use DownloadProgramData().");
    public void ExecuteCommandList(byte[][] commands) => throw new NotSupportedException("SLC specific method not applicable.");
    public void ApplyPortConfiguration() => throw new NotSupportedException("Port config not yet implemented.");
    public void InitializeMemory() => throw new NotSupportedException("Initialize memory not yet implemented.");
    public byte[] ReadDiagnosticCounters() => throw new NotSupportedException("Diagnostic counters not yet implemented.");
    public void ResetDiagnosticCounters() => throw new NotSupportedException("Diagnostic counters not yet implemented.");
    public byte ReadLinkParameters() => throw new NotSupportedException("Link parameters not yet implemented.");
    public void SetLinkParameters(byte maxAddress) => throw new NotSupportedException("Link parameters not yet implemented.");
    /// <summary>
    /// Sends an Echo command (CMD 0x06 FNC 0x00) and returns the echoed data.
    /// Defined in AB Publication 1770-6.5.16 and supported by all PCCC-compatible PLCs.
    /// </summary>
    public byte[] Echo(byte[] data)
        => _protocol.Echo(data, (byte)MyNode, (byte)TargetNode);
}
