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

using System.Collections.ObjectModel;
using System.Text;
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

    public Plc5Handler(IHandlerContext context, PCCCProtocol protocol, int initialProcessorType)
    {
        _context = context;
        _protocol = protocol;
        _processorType = initialProcessorType;
    }

    private int MyNode => _context.MyNode;
    private int TargetNode => _context.TargetNode;
    private bool AsyncMode => _context.AsyncMode;

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
    private byte[] EncodePlc5LogicalAddress(int fileNumber, int fileType, int element, int subElement)
    {
        // Determine number of levels and their values
        bool hasSubElement = subElement != 0 && subElement != 99;
        int levelCount = hasSubElement ? 4 : 3;
        
        // Prepare levels (4 max)
        byte[] levels = new byte[4];
        levels[0] = (byte)fileNumber;
        levels[1] = (byte)fileType;
        levels[2] = (byte)element;
        levels[3] = hasSubElement ? (byte)subElement : (byte)0;
        
        // Build extended levels if any level >= 255
        List<byte> result = new List<byte>();
        int maskHigh = (levelCount << 4); // bits 7-4
        int maskLow = hasSubElement ? 0x08 : 0x00; // bit 3 = 1 for sub-element
        result.Add((byte)(maskHigh | maskLow));
        
        for (int i = 0; i < levelCount; i++)
        {
            int val = (i == 3) ? subElement : (i == 2) ? element : (i == 1) ? fileType : fileNumber;
            if (val < 255)
            {
                result.Add((byte)val);
            }
            else
            {
                result.Add(0xFF);
                result.Add((byte)(val & 0xFF));
                result.Add((byte)((val >> 8) & 0xFF));
            }
        }
        return result.ToArray();
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
        byte[] result = new byte[numberOfBytes];
        int bytesPerElem = addr.BytesPerElements;

        while (filePosition < numberOfBytes && finalStatus == 0)
        {
            // Calculate number of elements to read in this chunk
            int maxChunkBytes = PCCCConstants.Df1Limits.MaxReadPayloadBytes;
            int remainingBytes = numberOfBytes - filePosition;
            int chunkBytes = Math.Min(remainingBytes, maxChunkBytes);
            
            // For structured types (timer/counter/string), ensure we read whole elements
            if (addr.FileType == (byte)PCCCConstants.SlcFileTypeCode.Timer ||
                addr.FileType == (byte)PCCCConstants.SlcFileTypeCode.Counter ||
                addr.FileType == (byte)PCCCConstants.SlcFileTypeCode.String)
            {
                int elemAlign = (chunkBytes + bytesPerElem - 1) / bytesPerElem * bytesPerElem;
                if (elemAlign > maxChunkBytes) elemAlign -= bytesPerElem;
                chunkBytes = Math.Max(bytesPerElem, elemAlign);
            }
            
            int currentElement = addr.Element + (filePosition / bytesPerElem);
            int subElementOffset = addr.SubElement + ((filePosition % bytesPerElem) / 2);
            
            byte[] logicalAddress = EncodePlc5LogicalAddress(
                addr.FileNumber, addr.FileType, currentElement, subElementOffset);
            
            var req = PCCCMessage.CreateTypedReadRequest(
                logicalAddress, chunkBytes, 0, (byte)MyNode, (byte)TargetNode);
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
        return result;
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

        while (filePosition < dataToWrite.Length && reply == 0)
        {
            int maxChunkBytes = PCCCConstants.Df1Limits.MaxWritePayloadBytes;
            int remainingBytes = dataToWrite.Length - filePosition;
            int chunkBytes = Math.Min(remainingBytes, maxChunkBytes);
            
            // Align to element boundary for structured types
            if (addr.FileType == (byte)PCCCConstants.SlcFileTypeCode.Timer ||
                addr.FileType == (byte)PCCCConstants.SlcFileTypeCode.Counter ||
                addr.FileType == (byte)PCCCConstants.SlcFileTypeCode.String)
            {
                int elemAlign = (chunkBytes + bytesPerElem - 1) / bytesPerElem * bytesPerElem;
                if (elemAlign > maxChunkBytes) elemAlign -= bytesPerElem;
                chunkBytes = Math.Max(bytesPerElem, elemAlign);
            }
            
            int currentElement = addr.Element + (filePosition / bytesPerElem);
            int subElementOffset = addr.SubElement + ((filePosition % bytesPerElem) / 2);
            
            byte[] logicalAddress = EncodePlc5LogicalAddress(
                addr.FileNumber, addr.FileType, currentElement, subElementOffset);
            
            byte[] chunkData = new byte[chunkBytes];
            Array.Copy(dataToWrite, filePosition, chunkData, 0, chunkBytes);
            
            var req = PCCCMessage.CreateTypedWriteRequest(
                logicalAddress, chunkData, 0, (byte)MyNode, (byte)TargetNode);
            
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

    public int GetProcessorType()
    {
        var req = PCCCMessage.CreateDiagnosticStatusRequest(0, (byte)MyNode, (byte)TargetNode);
        var reply = _protocol.SendRequest(req, out int sts);
        if (sts != PCCCConstants.Sts.Success || reply?.Data == null || reply.Data.Length < 4)
            return 0;

        byte typeByte = reply.Data[1];
        byte expansionByte = reply.Data[2];
        bool hasExpansion = (typeByte >> 4) == 0x0E;
        _processorType = hasExpansion ? expansionByte : typeByte;
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
        byte modeValue = 0x00;
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
    // Read/Write operations using Typed Read/Write
    // ---------------------------------------------------------------------

    public string[] ReadAny(string startAddress, int numberOfElements)
    {
        DataAddress p = PCCCParser.Parse(startAddress);
        if (p.FileType == 0) throw new PCCCException("Invalid Address");

        short arrayElements = (short)(numberOfElements - 1);
        if (arrayElements < 0) arrayElements = 0;
        if (p.BitNumber < 16)
            arrayElements = (short)Math.Floor(numberOfElements / 16.0);

        int bytesPerElem = p.BytesPerElements;
        int numberOfBytes = (arrayElements + 1) * bytesPerElem;
        
        // Adjust for timer/counter sub-element reads
        if (p.SubElement > 0 && (p.FileType == (byte)PCCCConstants.SlcFileTypeCode.Timer || 
                                 p.FileType == (byte)PCCCConstants.SlcFileTypeCode.Counter))
        {
            numberOfBytes = (numberOfBytes * 3) - 4;
        }
        
        byte[] returnedData = ReadRawDataWithChunking(ref p, numberOfBytes, out int reply);
        if (reply != 0)
            throw new PCCCException(PCCCErrors.DecodeStatus(reply));

        string[] result = new string[arrayElements + 1];
        switch (p.FileType)
        {
            case (byte)PCCCConstants.SlcFileTypeCode.Float:
                for (int i = 0; i <= arrayElements; i++)
                    result[i] = BitConverter.ToSingle(returnedData, i * 4).ToString();
                break;
            case (byte)PCCCConstants.SlcFileTypeCode.String:
                for (int i = 0; i <= arrayElements; i++)
                {
                    int strLen = BitConverter.ToInt16(returnedData, i * 84);
                    if (strLen > 82) strLen = 82;
                    var sb = new StringBuilder();
                    for (int j = 0; j < strLen; j++)
                    {
                        char c = (char)returnedData[(i * 84) + 2 + j];
                        if (c == 0) break;
                        sb.Append(c);
                    }
                    result[i] = sb.ToString();
                }
                break;
            case (byte)PCCCConstants.SlcFileTypeCode.Timer:
            case (byte)PCCCConstants.SlcFileTypeCode.Counter:
                for (int i = 0; i <= arrayElements; i++)
                {
                    int offset = (p.SubElement > 0) ? i * 6 : i * 2;
                    result[i] = BitConverter.ToInt16(returnedData, offset).ToString();
                }
                break;
            case (byte)PCCCConstants.SlcFileTypeCode.Long:
                for (int i = 0; i <= arrayElements; i++)
                    result[i] = BitConverter.ToInt32(returnedData, i * 4).ToString();
                break;
            default:
                for (int i = 0; i <= arrayElements; i++)
                    result[i] = BitConverter.ToInt16(returnedData, i * 2).ToString();
                break;
        }

        if (p.BitNumber >= 0 && p.BitNumber < 16)
        {
            string[] bitResult = new string[numberOfElements];
            int bitPos = p.BitNumber, wordPos = 0;
            for (int i = 0; i < numberOfElements; i++)
            {
                int wordVal = Convert.ToInt32(result[wordPos]);
                bitResult[i] = ((wordVal & (1 << bitPos)) != 0).ToString();
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

    public int ReadModifyWrite(string[] addresses, ushort[] andMasks, ushort[] orMasks)
        => throw new NotSupportedException(
            "ReadModifyWrite for PLC-5 requires PLC-5 logical binary addressing. " +
            "SLC-style addressing is not supported. See 1770-6.5.16 page 7-20.");

    public string WriteData(string startAddress, int dataToWrite)
    {
        int status = WriteData(startAddress, 1, new int[] { dataToWrite });
        return status == 0 ? string.Empty : PCCCErrors.DecodeStatus(status);
    }

    public int WriteData(string startAddress, int numberOfElements, int[] dataToWrite)
    {
        DataAddress p = PCCCParser.Parse(startAddress);
        byte[] converted = new byte[numberOfElements * p.BytesPerElements];
        if (p.FileType == (byte)PCCCConstants.SlcFileTypeCode.Long)
        {
            for (int i = 0; i < numberOfElements; i++)
                BitConverter.GetBytes(dataToWrite[i]).CopyTo(converted, i * 4);
        }
        else
        {
            for (int i = 0; i < numberOfElements; i++)
            {
                if (dataToWrite[i] > 32767 || dataToWrite[i] < -32768)
                    throw new PCCCException("Integer data out of range, must be between -32768 and 32767");
                converted[i * 2] = (byte)(dataToWrite[i] & 0xFF);
                converted[i * 2 + 1] = (byte)((dataToWrite[i] >> 8) & 0xFF);
            }
        }
        return WriteRawDataWithChunking(p, converted);
    }

    public int WriteData(string startAddress, float dataToWrite)
        => WriteData(startAddress, 1, new float[] { dataToWrite });

    public int WriteData(string startAddress, int numberOfElements, float[] dataToWrite)
    {
        DataAddress p = PCCCParser.Parse(startAddress);
        byte[] converted = new byte[numberOfElements * p.BytesPerElements];
        if (p.FileType == (byte)PCCCConstants.SlcFileTypeCode.Float)
        {
            for (int i = 0; i < numberOfElements; i++)
                BitConverter.GetBytes(dataToWrite[i]).CopyTo(converted, i * 4);
        }
        else if (p.FileType == (byte)PCCCConstants.SlcFileTypeCode.Long)
        {
            for (int i = 0; i < numberOfElements; i++)
                BitConverter.GetBytes((int)dataToWrite[i]).CopyTo(converted, i * 4);
        }
        else
        {
            for (int i = 0; i < numberOfElements; i++)
            {
                if (dataToWrite[i] > 32767 || dataToWrite[i] < -32768)
                    throw new PCCCException("Integer data out of range, must be between -32768 and 32767");
                converted[i * 2] = (byte)((int)dataToWrite[i] & 0xFF);
                converted[i * 2 + 1] = (byte)(((int)dataToWrite[i] >> 8) & 0xFF);
            }
        }
        return WriteRawDataWithChunking(p, converted);
    }

    public int WriteData(string startAddress, string dataToWrite)
    {
        if (string.IsNullOrEmpty(dataToWrite)) return 0;
        if (dataToWrite.Length > 82) dataToWrite = dataToWrite[..82];

        DataAddress p = PCCCParser.Parse(startAddress);
        if (p.FileType == (byte)PCCCConstants.SlcFileTypeCode.String)
        {
            byte[] stElement = new byte[84];
            stElement[0] = (byte)(dataToWrite.Length & 0xFF);
            stElement[1] = (byte)((dataToWrite.Length >> 8) & 0xFF);
            for (int i = 0; i < dataToWrite.Length; i++)
                stElement[2 + i] = (byte)dataToWrite[i];
            return WriteRawDataWithChunking(p, stElement);
        }
        else
        {
            int[]? words = StringConverter.StringToWords(dataToWrite);
            if (words == null) return -1;
            byte[] converted = new byte[words.Length * 2 + 2];
            converted[0] = (byte)dataToWrite.Length;
            for (int i = 0; i < words.Length; i++)
            {
                converted[i * 2 + 2] = (byte)((words[i] >> 8) & 0xFF);
                converted[i * 2 + 3] = (byte)(words[i] & 0xFF);
            }
            return WriteRawDataWithChunking(p, converted);
        }
    }

    // ---------------------------------------------------------------------
    // Unsupported methods
    // ---------------------------------------------------------------------

    public Collection<PLCFileDetails> UploadProgramData()
        => throw new NotSupportedException(
            "PLC-5 upload uses 'upload all request' (FNC 0x53) + 'read bytes physical' (FNC 0x17). " +
            "See 1770-6.5.16 Chapter 12.");

    public void DownloadProgramData(Collection<PLCFileDetails> plcFiles)
        => throw new NotSupportedException(
            "PLC-5 download uses 'download all request' (FNC 0x50) + 'write bytes physical' (FNC 0x18). " +
            "See 1770-6.5.16 Chapter 12.");

    public int GetSlotCount() => throw new NotSupportedException("I/O config not yet implemented.");
    public IOConfig[] GetIOConfig() => throw new NotSupportedException("I/O config not yet implemented.");
    public DataFileDetails[] GetDataMemory() => throw new NotSupportedException("Data memory enumeration not yet implemented.");
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
    public void ApplyPortConfiguration() => throw new NotSupportedException("Port config not yet implemented.");
    public void InitializeMemory() => throw new NotSupportedException("Initialize memory not yet implemented.");
    public byte[] ReadDiagnosticCounters() => throw new NotSupportedException("Diagnostic counters not yet implemented.");
    public void ResetDiagnosticCounters() => throw new NotSupportedException("Diagnostic counters not yet implemented.");
    public byte ReadLinkParameters() => throw new NotSupportedException("Link parameters not yet implemented.");
    public void SetLinkParameters(byte maxAddress) => throw new NotSupportedException("Link parameters not yet implemented.");
    public byte[] Echo(byte[] data) => throw new NotSupportedException("Echo not yet implemented.");
}
