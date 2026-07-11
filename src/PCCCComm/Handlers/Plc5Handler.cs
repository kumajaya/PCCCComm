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
///   - Word Range Read (FNC 0x01) and Word Range Write (FNC 0x00), page 14-6/14-7
/// 
/// This implementation provides full data read/write access using Word Range Read/Write
/// with logical binary addressing, supporting all PLC-5 file types (N, B, F, T, C, R, ST, etc.)
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
    /// Encodes a PLC-5 logical binary address with 2 levels (file number + element).
    /// Format: [mask=0x06][fileNumber][element].
    /// Reference: AB Publication 1770-6.5.16, Chapter 13, page 13-12.
    /// </summary>
    public static byte[] EncodePlc5LogicalAddress(int fileNumber, int element)
    {
        var result = new List<byte>();
        result.Add(0x06); // 2 levels, last level = element

        // Encode file number
        // Threshold is deliberately "< 255", not "<= 255": 0xFF is reserved as the
        // extended-encoding marker byte itself, so a fileNumber of exactly 255 must
        // also go through the extended (3-byte) form below, not the 1-byte form.
        if (fileNumber < 255)
            result.Add((byte)fileNumber);
        else
        {
            result.Add(0xFF);
            result.Add((byte)(fileNumber & 0xFF));
            result.Add((byte)((fileNumber >> 8) & 0xFF));
        }

        // Encode element (same "< 255" reasoning as file number above: 0xFF is the
        // extended-form marker, so element == 255 must use the 3-byte form too).
        if (element < 255)
            result.Add((byte)element);
        else
        {
            result.Add(0xFF);
            result.Add((byte)(element & 0xFF));
            result.Add((byte)((element >> 8) & 0xFF));
        }

        return result.ToArray();
    }

    // ---------------------------------------------------------------------
    // RSLinx-exact logical binary encoders (defensive: stricter PLC-5 firmware)
    // ---------------------------------------------------------------------
    //
    // The compact EncodePlc5LogicalAddress above (mask 0x06 = file+element, section
    // and sub-element levels omitted) is accepted by the PLC-5 we tested against and
    // by libplctag's target — the PLC-5 fills absent levels with their defaults, which
    // is consistent with the "insignificant zero fields may be omitted" rule in
    // AB 1770-6.5.16 §7-36. The encoders below instead reproduce, byte-for-byte, what
    // RSLinx itself sends (verified against live RSLinx->PLC-5 captures):
    //   READ  (word range read  0x01): mask 0x0F, levels [section=0, file, elem, sub=0]
    //   WRITE (word range write 0x00): mask 0x07, levels [section=0, file, elem]
    // RSLinx's form is the maximally-compatible superset every PLC-5 must accept, so it
    // is the safer default for meeting firmware whose default-level leniency is unknown.

    /// <summary>
    /// When true (default) the read/write data paths emit the RSLinx-exact logical
    /// binary address (mask 0x0F / 0x07 with the section level present). When false they
    /// emit the compact form (mask 0x06, section/sub-element omitted). Both address the
    /// same location on PLC-5 firmware that applies level defaults; RSLinx form is chosen
    /// as default for widest firmware compatibility.
    /// </summary>
    public bool UseRSLinxAddressForm { get; set; } = true;

    private static void AppendLevel(List<byte> b, int value)
    {
        // 0xFF is the extended-form marker, so a value of exactly 255 must also use the
        // 3-byte form (same reasoning as EncodePlc5LogicalAddress).
        if (value < 255)
            b.Add((byte)value);
        else
        {
            b.Add(0xFF);
            b.Add((byte)(value & 0xFF));
            b.Add((byte)((value >> 8) & 0xFF));
        }
    }

    /// <summary>RSLinx-exact READ address: mask 0x0F, [section=0, file, element, sub=0].</summary>
    public static byte[] EncodePlc5ReadAddress(int fileNumber, int element)
    {
        var b = new List<byte> { 0x0F, 0x00 };
        AppendLevel(b, fileNumber);
        AppendLevel(b, element);
        b.Add(0x00); // sub-element level 0
        return b.ToArray();
    }

    /// <summary>RSLinx-exact WRITE address: mask 0x07, [section=0, file, element].</summary>
    public static byte[] EncodePlc5WriteAddress(int fileNumber, int element)
    {
        var b = new List<byte> { 0x07, 0x00 };
        AppendLevel(b, fileNumber);
        AppendLevel(b, element);
        return b.ToArray();
    }

    private byte[] EncodeReadAddr(int fileNumber, int element)
        => UseRSLinxAddressForm ? EncodePlc5ReadAddress(fileNumber, element)
                                : EncodePlc5LogicalAddress(fileNumber, element);

    private byte[] EncodeWriteAddr(int fileNumber, int element)
        => UseRSLinxAddressForm ? EncodePlc5WriteAddress(fileNumber, element)
                                : EncodePlc5LogicalAddress(fileNumber, element);

    // ---------------------------------------------------------------------
    // Typed Read / Typed Write (FNC 0x68 / 0x67)
    // ---------------------------------------------------------------------
    // The primary data path uses Word Range Read/Write; these typed methods exist so the
    // typed command path is correct and testable. Ground truth covers integer (type ID 4,
    // size 2 — 1770-6.5.16 §7-36) and float (type ID 8, size 4 — live RSLinx typed-write).
    // Unlike Word Range, the typed path uses standard little-endian for float/long (no
    // high-word-first swap).

    /// <summary>Advance idx past a variable-length type/data parameter (§7-36/7-37).</summary>
    public static bool SkipTypeDataParam(byte[] p, ref int idx)
    {
        if (p == null || idx >= p.Length) return false;
        byte flag = p[idx++];
        int typeId;
        if ((flag & 0x80) != 0)
        {
            int n = (flag >> 4) & 0x07;
            if (idx + n > p.Length) return false;
            typeId = 0;
            for (int i = 0; i < n; i++) typeId |= p[idx++] << (8 * i);
        }
        else typeId = (flag >> 4) & 0x07;
        if ((flag & 0x08) != 0)
        {
            int m = flag & 0x07;
            if (idx + m > p.Length) return false;
            idx += m;
        }
        if (typeId == 9) return SkipTypeDataParam(p, ref idx);
        return true;
    }

    /// <summary>Typed Read (FNC 0x68). Returns DATA bytes with the reply descriptor stripped.</summary>
    public byte[] TypedReadRaw(byte[] logicalAddress, int elementCount)
    {
        var req = PCCCMessage.CreateTypedReadRequest(logicalAddress, elementCount, 0, (byte)MyNode, (byte)TargetNode);
        var reply = _protocol.SendRequest(req, out int sts);
        if (sts != PCCCConstants.Sts.Success || reply?.Data == null)
            throw new PCCCException($"TypedRead failed: {PCCCErrors.DecodeStatus(sts)}");
        int idx = 0;
        if (!SkipTypeDataParam(reply.Data, ref idx))
            throw new PCCCException("TypedRead: malformed type/data parameter in reply.");
        return reply.Data[idx..];
    }

    /// <summary>Typed Write (FNC 0x67). Caller supplies the type/data parameter for the type.</summary>
    public void TypedWriteRaw(byte[] logicalAddress, byte[] typeDataParam, byte[] data, int elementCount)
    {
        var req = PCCCMessage.CreateTypedWriteRequest(logicalAddress, typeDataParam, data,
            elementCount, 0, (byte)MyNode, (byte)TargetNode);
        _protocol.SendRequest(req, out int sts);
        if (sts != PCCCConstants.Sts.Success)
            throw new PCCCException($"TypedWrite failed: {PCCCErrors.DecodeStatus(sts)}");
    }

    // ---------------------------------------------------------------------
    // Chunked read/write helpers using Word Range Read/Write
    // ---------------------------------------------------------------------

    /// <summary>
    /// Reads raw data from PLC-5 using Word Range Read (FNC 0x01) with automatic chunking.
    /// </summary>
    private byte[] ReadRawDataWithChunking(ref DataAddress addr, int numberOfBytes, out int finalStatus)
    {
        finalStatus = 0;
        int filePosition = 0;
        byte[] result = ArrayPool<byte>.Shared.Rent(numberOfBytes);
        try
        {
            int bytesPerElem = addr.BytesPerElements;

            while (filePosition < numberOfBytes && finalStatus == 0)
            {
                int maxChunkBytes = PCCCConstants.Df1Limits.MaxReadPayloadPlc5;
                int remainingBytes = numberOfBytes - filePosition;
                int chunkBytes = Math.Min(remainingBytes, maxChunkBytes);
                
                // Align chunk to a whole number of elements, not just a word boundary.
                // For 2-byte element types (Integer, Binary, ...) this is the same thing,
                // but for Float/Long (4 bytes), Timer/Counter/Control (6 bytes), String, etc.
                // a plain word-boundary trim can still leave a partial element at the end
                // of a chunk. That previously caused two bugs:
                //   1. "sizeWords" was actually an element count, so byteCount = sizeWords*2
                //      under-requested data for any element size != 2 bytes.
                //   2. On the following chunk, chunkBytes / bytesPerElem could truncate to 0
                //      (integer division), producing a zero-size Word Range Read that the
                //      PLC rejects (STS 0xF0 - "size to read equals zero" per AB 1770-6.5.16).
                chunkBytes -= chunkBytes % bytesPerElem;
                if (chunkBytes == 0) chunkBytes = bytesPerElem; // guard: always make progress
                
                int chunkWords = chunkBytes / 2;
                int currentElement = addr.Element + (filePosition / bytesPerElem);
                
                // Encode logical address (RSLinx-exact 4-level read form by default)
                byte[] logicalAddress = EncodeReadAddr(addr.FileNumber, currentElement);
                
                // Use WordRangeRead (FNC 0x01)
                var req = PCCCMessage.CreateWordRangeReadRequest(
                    logicalAddress,
                    0,                // wordOffset
                    chunkWords,       // sizeWords (word count, not element count)
                    0,
                    (byte)MyNode,
                    (byte)TargetNode);

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
    /// Writes raw data to PLC-5 using Word Range Write (FNC 0x00) with automatic chunking.
    /// </summary>
    private int WriteRawDataWithChunking(DataAddress addr, byte[] dataToWrite)
    {
        if (addr.FileType == 0) return -5;
        int filePosition = 0;
        int reply = 0;
        int bytesPerElem = addr.BytesPerElements;

        while (filePosition < dataToWrite.Length && reply == 0)
        {
            int maxChunkBytes = PCCCConstants.Df1Limits.MaxWritePayloadPlc5;
            int remainingBytes = dataToWrite.Length - filePosition;
            int chunkBytes = Math.Min(remainingBytes, maxChunkBytes);
            
            // Align chunk to a whole number of elements (not just a word boundary).
            // Otherwise a chunk can end mid-element for element sizes != 2 bytes
            // (Float/Long = 4, Timer/Counter/Control = 6, String = 84, ...), which
            // shifts the following chunk's data relative to its computed element
            // address and corrupts the write.
            chunkBytes -= chunkBytes % bytesPerElem;
            if (chunkBytes == 0) chunkBytes = bytesPerElem; // guard: always make progress
            
            int currentElement = addr.Element + (filePosition / bytesPerElem);
            
            byte[] logicalAddress = EncodeWriteAddr(addr.FileNumber, currentElement);
            
            byte[] chunkData = new byte[chunkBytes];
            Array.Copy(dataToWrite, filePosition, chunkData, 0, chunkBytes);
            
            // Use WordRangeWrite (FNC 0x00)
            var req = PCCCMessage.CreateWordRangeWriteRequest(
                logicalAddress,
                0,
                chunkData,
                0,
                (byte)MyNode,
                (byte)TargetNode);

            if (AsyncMode)
            {
                try
                {
                    _protocol.SendRequestAsync(req);
                    reply = 0;
                }
                catch (PCCCException)
                {
                    // SendRequestAsync now throws when the circuit breaker is open (see
                    // PCCCProtocol.SendRequestAsync), so an unreachable PLC no longer silently
                    // fires writes into the void. Convert that into the same -23 status code
                    // the sync path returns via SendRequest, so callers of WriteWords/WriteData
                    // see consistent behavior regardless of AsyncMode.
                    reply = -23;
                }
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

        // Timer/Counter/Control are always 3 words (6 bytes: control, preset/PRE or LEN,
        // accumulated/ACC or POS) on the wire, regardless of SubElement — but PCCCParser
        // leaves BytesPerElements at its generic default (2) for these types since it doesn't
        // special-case them, so that can't be relied on here. The caller (ReadAny/
        // ReadAnyValues) requests a whole number of 3-word elements and picks out the right
        // word client-side, since PLC-5 Word Range Read can't target a sub-element directly on
        // the wire the way this 2-level EncodePlc5LogicalAddress works. (A previous version
        // forced bytesPerElem down to 2 and over-fetched via an undocumented "(N*2*3)-4"
        // formula, but ReadWords then truncated its result to just the first `numberOfWords`
        // words — so a ".ACC" and a ".PRE" read of the same element ended up reading the exact
        // same raw words with no sub-element offset ever applied.)
        // NOTE: ReadRawDataWithChunking reads addr.BytesPerElements directly (it needs
        // the true per-element size to align chunk boundaries AND to compute each
        // chunk's logical element address as filePosition/bytesPerElem — see its body).
        // A local-only "bytesPerElem = 6" here without also updating p.BytesPerElements
        // was a bug: single-packet reads worked, but any read needing >1 packet advanced
        // to the wrong element on the 2nd+ packet (using /2 instead of /6), eventually
        // requesting an element past the real end of the file and getting back PCCC's
        // "File is wrong size" (STS) from the PLC.
        if (p.FileType == (byte)PCCCConstants.SlcFileTypeCode.Timer ||
            p.FileType == (byte)PCCCConstants.SlcFileTypeCode.Counter ||
            p.FileType == (byte)PCCCConstants.SlcFileTypeCode.Control)
            p.BytesPerElements = 6;

        int bytesPerElem = p.BytesPerElements;

        int totalBytesNeeded = numberOfWords * 2;
        int numberOfElements = (totalBytesNeeded + bytesPerElem - 1) / bytesPerElem;
        int numberOfBytesToRead = numberOfElements * bytesPerElem;

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

        // Timer/Counter/Control always fetch full 3-word elements (control, PRE/LEN,
        // ACC/POS), whether or not a sub-element was requested — see ReadWords for why
        // BytesPerElements (generic default 2) can't be used for these types here.
        int bytesPerElem = (p.FileType == (byte)PCCCConstants.SlcFileTypeCode.Timer ||
                             p.FileType == (byte)PCCCConstants.SlcFileTypeCode.Counter ||
                             p.FileType == (byte)PCCCConstants.SlcFileTypeCode.Control)
            ? 6
            : p.BytesPerElements;

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
                    // Word Range Read transmits Float/Long as HIGH word then LOW word
                    // (AB Pub. 1770-6.5.16 p.13-17 "PLC5 word range read" example) —
                    // the opposite order from Typed Read. Swap accordingly.
                    result[i] = WordConverter.WordsToFloat(rawWords[offset + 1], rawWords[offset])
                        .ToString(CultureInfo.InvariantCulture);
                    break;
                case (byte)PCCCConstants.SlcFileTypeCode.Long:
                    result[i] = WordConverter.WordsToInt32(rawWords[offset + 1], rawWords[offset])
                        .ToString(CultureInfo.InvariantCulture);
                    break;
                case (byte)PCCCConstants.SlcFileTypeCode.Timer:
                case (byte)PCCCConstants.SlcFileTypeCode.Counter:
                    // p.SubElement selects control(0, default)/PRE-LEN(1)/ACC-POS(2) within
                    // this element's 3-word block — see ReadWords for why this can't be
                    // done on the wire and has to be picked out of the raw stream here.
                    result[i] = ((short)rawWords[offset + p.SubElement]).ToString(CultureInfo.InvariantCulture);
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

        // Timer/Counter/Control always fetch full 3-word elements — see ReadWords for why
        // BytesPerElements (generic default 2) can't be used for these types here.
        int bytesPerElem = (p.FileType == (byte)PCCCConstants.SlcFileTypeCode.Timer ||
                             p.FileType == (byte)PCCCConstants.SlcFileTypeCode.Counter ||
                             p.FileType == (byte)PCCCConstants.SlcFileTypeCode.Control)
            ? 6
            : p.BytesPerElements;

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
                    // Word Range Read transmits Float/Long as HIGH word then LOW word
                    // (AB Pub. 1770-6.5.16 p.13-17) — swapped from Typed Read order.
                    result[i] = WordConverter.WordsToFloat(rawWords[offset + 1], rawWords[offset]);
                    break;
                case (byte)PCCCConstants.SlcFileTypeCode.Long:
                    result[i] = WordConverter.WordsToInt32(rawWords[offset + 1], rawWords[offset]);
                    break;
                case (byte)PCCCConstants.SlcFileTypeCode.Timer:
                case (byte)PCCCConstants.SlcFileTypeCode.Counter:
                    // p.SubElement selects control(0)/PRE(1)/ACC(2) — see ReadAny above.
                    result[i] = (short)rawWords[offset + p.SubElement];
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

        // Note: unlike ReadWords/ReadAny, this intentionally does NOT correct
        // BytesPerElements to 6 for Timer/Counter/Control. A bit-level write to
        // T/C/R only ever targets a named status-bit mnemonic (EN/DN/TT/...),
        // which PCCCParser collapses onto the control word (word 0) of the
        // element. The masked write below addresses p.Element directly, so a
        // 2-byte mask covering just that control word is correct here — a
        // 6-byte mask would incorrectly extend the AND/OR mask over PRE/ACC too.
        if (p.BitNumber >= 0 && p.BitNumber < p.BytesPerElements * 8)
        {
            int elemSize = p.BytesPerElements;
            byte[] andMask = new byte[elemSize];
            byte[] orMask = new byte[elemSize];

            for (int i = 0; i < elemSize; i++)
            {
                andMask[i] = 0xFF;
                orMask[i] = 0x00;
            }

            int byteIndex = p.BitNumber / 8;
            int bitIndex = p.BitNumber % 8;

            andMask[byteIndex] = (byte)~(1 << bitIndex);
            if (dataToWrite != 0)
                orMask[byteIndex] = (byte)(1 << bitIndex);

            // No live PLC-5 RMW (FNC 0x26) capture yet; use the write-form (3-level)
            // address by analogy to word range write. Revisit if a capture shows otherwise.
            byte[] logicalAddress = EncodeWriteAddr(p.FileNumber, p.Element);
            _protocol.ReadModifyWritePlc5(logicalAddress, andMask, orMask, out int status,
                (byte)MyNode, (byte)TargetNode);

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
                // Word Range Write expects HIGH word then LOW word for 32-bit types
                // (mirrors Word Range Read order, AB Pub. 1770-6.5.16 p.13-17).
                words[i * 2] = high;
                words[i * 2 + 1] = low;
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
                // Word Range Write expects HIGH word then LOW word (see ReadAny/ReadAnyValues).
                words[i * 2] = high;
                words[i * 2 + 1] = low;
            }
        }
        else if (p.FileType == (byte)PCCCConstants.SlcFileTypeCode.Long)
        {
            words = new ushort[numberOfElements * 2];
            for (int i = 0; i < numberOfElements; i++)
            {
                WordConverter.Int32ToWords((int)dataToWrite[i], out ushort low, out ushort high);
                // Word Range Write expects HIGH word then LOW word (see ReadAny/ReadAnyValues).
                words[i * 2] = high;
                words[i * 2 + 1] = low;
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

    /// <summary>
    /// Reads <paramref name="sizeWords"/> words starting at <paramref name="wordOffset"/>
    /// from the given logical address, automatically splitting the request into multiple
    /// Word Range Read packets if it exceeds the AB protocol's per-packet limit (244 bytes /
    /// 122 words per AB Pub. 1770-6.5.16 p.7-34). Successive packets reuse the same logical
    /// address and advance the PACKET OFFSET field, exactly as shown in the multi-packet
    /// "word range read" example on p.14-6/14-7 of that spec — so results are byte-for-byte
    /// identical to what a single (hypothetically unlimited) request would return.
    /// </summary>
    public byte[] WordRangeRead(byte[] logicalAddress, int wordOffset, int sizeWords)
    {
        if (logicalAddress == null || logicalAddress.Length == 0)
            throw new ArgumentException("Logical address cannot be null or empty.", nameof(logicalAddress));
        if (sizeWords <= 0)
            throw new ArgumentOutOfRangeException(nameof(sizeWords), "Size must be positive.");

        int maxWordsPerChunk = PCCCConstants.Df1Limits.MaxReadPayloadPlc5 / 2;
        byte[] result = new byte[sizeWords * 2];
        int wordsDone = 0;

        while (wordsDone < sizeWords)
        {
            int chunkWords = Math.Min(sizeWords - wordsDone, maxWordsPerChunk);

            // TOTAL TRANS must be >= (this packet's absolute PACKET OFFSET + its size), or the
            // PLC rejects it (STS 0xF0 / EXT STS 0x12 "Invalid parameter"). It's not enough to
            // use just sizeWords: if the caller's own wordOffset is already non-zero (e.g.
            // GetDataMemory reading at word offset 35), sizeWords alone undercounts the required
            // total. Using the absolute end-of-range (wordOffset + sizeWords) satisfies the rule
            // both for a single call and for every sub-packet of an auto-chunked one.
            byte[] chunk = _protocol.WordRangeRead(logicalAddress, wordOffset + wordsDone, chunkWords,
                (byte)MyNode, (byte)TargetNode, totalTransWords: wordOffset + sizeWords);

            int bytesToCopy = Math.Min(chunk.Length, chunkWords * 2);
            Array.Copy(chunk, 0, result, wordsDone * 2, bytesToCopy);

            // If the PLC returned fewer bytes than requested, stop rather than looping forever.
            if (bytesToCopy < chunkWords * 2)
            {
                Array.Resize(ref result, wordsDone * 2 + bytesToCopy);
                return result;
            }

            wordsDone += chunkWords;
        }

        return result;
    }

    /// <summary>
    /// Writes <paramref name="data"/> starting at <paramref name="wordOffset"/> from the given
    /// logical address, automatically splitting the write into multiple Word Range Write
    /// packets if it exceeds the AB protocol's per-packet limit (240 bytes per AB Pub.
    /// 1770-6.5.16 p.7-35). Successive packets reuse the same logical address and advance
    /// the PACKET OFFSET field, mirroring the chunking used for <see cref="WordRangeRead"/>.
    /// </summary>
    public void WordRangeWrite(byte[] logicalAddress, int wordOffset, byte[] data)
    {
        if (logicalAddress == null || logicalAddress.Length == 0)
            throw new ArgumentException("Logical address cannot be null or empty.", nameof(logicalAddress));
        if (data == null || data.Length == 0 || data.Length % 2 != 0)
            throw new ArgumentException("Data must be non‑empty and have even number of bytes.", nameof(data));

        int maxBytesPerChunk = PCCCConstants.Df1Limits.MaxWritePayloadPlc5;
        // Absolute end-of-range in words — see the matching comment in WordRangeRead for why
        // this must include the caller's own wordOffset, not just data.Length/2.
        int totalTransWords = wordOffset + data.Length / 2;
        int bytesDone = 0;

        while (bytesDone < data.Length)
        {
            int chunkBytes = Math.Min(data.Length - bytesDone, maxBytesPerChunk);
            if (chunkBytes % 2 != 0) chunkBytes--; // keep whole words per chunk

            byte[] chunkData = new byte[chunkBytes];
            Array.Copy(data, bytesDone, chunkData, 0, chunkBytes);

            _protocol.WordRangeWrite(logicalAddress, wordOffset + bytesDone / 2, chunkData,
                (byte)MyNode, (byte)TargetNode, totalTransWords: totalTransWords);

            bytesDone += chunkBytes;
        }
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
    /// Enumerates PLC-5 data files by probing Read Section Size (FNC 0x29) for each candidate
    /// file number with a file-level (no element) logical address. Reverse-engineered from a
    /// live RSLinx "Data Monitor" capture: RSLinx itself builds its file list this way — one
    /// Read Section Size request per file number, 0 through <paramref name="maxFileNumber"/>
    /// (RSLinx used 0-200) — rather than reading any kind of directory structure.
    ///
    /// Reply layout (empirically confirmed against two independently-verified live files —
    /// F13: 254 elements/508 words, F14: 446 elements/892 words — matching exactly):
    ///   bytes[0-1] = size, in words (LE)
    ///   bytes[2-3] = count, in elements (LE)
    ///   bytes[4...] = 1-2 trailing bytes (type/privilege?) — not yet decoded, so FileType
    ///                 in the result is left as "?" rather than guessed.
    /// A file that doesn't exist replies with size=0/count=0 (or a PCCCException, which is
    /// treated as "does not exist" here too) and is skipped.
    ///
    /// Ref: AB Publication 1770-6.5.16 p.7-22 ("read section size").
    /// </summary>
    public DataFileDetails[] GetDataMemory() => GetDataMemory(200);

    /// <summary>
    /// Overload allowing a narrower/wider file-number scan range than the
    /// IPlcHandler.GetDataMemory() default of 0-200 (the range RSLinx itself used).
    ///
    /// Requests are fired concurrently (bounded by PCCCProtocol.MaxConcurrentRequests,
    /// default 10) rather than one-at-a-time. A live capture comparison showed RSLinx does
    /// the same 201-file scan in ~1.9s by pipelining requests instead of waiting for each
    /// reply before sending the next; a naive sequential loop here took ~10.1s (201 requests
    /// x ~50ms round trip each). PCCCProtocol.SendRequest is already safe to call
    /// concurrently — every call gets its own TNS and its own wait handle — so this just
    /// fans the scan out across worker tasks instead of adding new synchronization.
    /// </summary>
    public DataFileDetails[] GetDataMemory(int maxFileNumber)
    {
        // Genuine single-threaded pipelining, matching what a live capture showed RSLinx
        // itself doing: fire every request back to back without waiting for replies (RSLinx's
        // ~200 requests were sent microseconds apart), THEN collect replies as they arrive,
        // matched by TNS. This replaces an earlier Parallel.For attempt: spreading the sends
        // across multiple threads/the MaxConcurrentRequests throttle (built for one request
        // at a time, not a 200-request burst) caused non-deterministic drops — some requests
        // waited past the 50ms queue timeout for a free throttle slot and were wrongly treated
        // as "file doesn't exist". Sending everything from one thread up front sidesteps that
        // machinery entirely and mirrors the access pattern that's actually proven to work
        // against this PLC.
        var pending = new System.Collections.Generic.List<(int fileNumber, ushort tns)>(maxFileNumber + 1);
        for (int fileNumber = 0; fileNumber <= maxFileNumber; fileNumber++)
        {
            var req = PCCCMessage.CreateReadSectionSizeRequest(fileNumber, 0, (byte)MyNode, (byte)TargetNode);
            ushort tns;
            try
            {
                tns = _protocol.BeginRequest(req);
            }
            catch (PCCCException)
            {
                // Circuit breaker tripped mid-scan (e.g. by a concurrent SendRequest call on
                // another thread). Stop sending further probes, but still fall through to
                // collect/clean up the ones already sent below — otherwise their TNS entries
                // in PCCCProtocol's response tables would never be removed or disposed.
                break;
            }
            pending.Add((fileNumber, tns));
        }

        var dataFiles = new System.Collections.Generic.List<DataFileDetails>();
        foreach (var (fileNumber, tns) in pending)
        {
            var reply = _protocol.EndRequest(tns, 5000, out int sts);
            if (sts != PCCCConstants.Sts.Success || reply?.Data == null || reply.Data.Length < 4)
                continue; // definitive PLC-side error, or no reply within 5s — file doesn't exist

            int sizeWords = reply.Data[0] | (reply.Data[1] << 8);
            int countElements = reply.Data[2] | (reply.Data[3] << 8);
            if (countElements <= 0)
                continue; // empty/non-existent file

            byte[] trailing = reply.Data.Length > 4
                ? reply.Data[4..]
                : Array.Empty<byte>();

            dataFiles.Add(new DataFileDetails
            {
                FileType = InferFileType(fileNumber, trailing),
                NumberOfElements = countElements,
                FileNumber = fileNumber
            });
        }

        return dataFiles.ToArray();
    }

    /// <summary>
    /// Maps the Read Section Size reply's trailing byte(s) (after size/count) to a PLC-5 file
    /// type letter. AB Pub. 1770-6.5.16 doesn't document this byte's bit layout, so this is
    /// reverse-engineered from a live RSLinx capture cross-referenced with independently
    /// verified files (F13/F14 -> Float, N20 -> Integer) and AB's default file-number
    /// convention (0=Output, 1=Input, 2=Status are always reserved regardless of what the
    /// trailing byte says, since it can't distinguish O/I/S/N from each other on its own).
    /// Unrecognized patterns fall back to "?" rather than guessing.
    /// </summary>
    private static string InferFileType(int fileNumber, byte[] trailing)
    {
        if (fileNumber == 0) return "O";
        if (fileNumber == 1) return "I";
        if (fileNumber == 2) return "S";

        string key = Convert.ToHexString(trailing);
        return key switch
        {
            "10" => "B",   // Binary/Bit
            "40" => "N",   // Integer
            "50" => "T",   // Timer
            "60" => "C",   // Counter
            "70" => "R",   // Control
            "9008" => "F", // Float
            "9015" => "PD", // PID (164 bytes/element)
            "9020" => "BT", // Block Transfer (12 bytes/element)
            _ => "?"
        };
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
