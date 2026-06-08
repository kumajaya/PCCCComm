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
/// PCCC protocol handler for SLC 500 and MicroLogix families.
/// Implements Protected Typed Logical Read/Write (FNC 0xA1/0xA2, 0xAA) and
/// SLC-specific upload/download procedures.
/// </summary>
public class SlcHandler : IPlcHandler
{
    // ─── Fields ─────────────────────────────────────────────────────────────
    private readonly IHandlerContext _context;
    private readonly PCCCProtocol _protocol;
    private int _processorType;

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
    
    private void OnFileProgress(PCCCComm.FileProgressEventArgs e) => _context.RaiseFileProgress(e);

    // ─── Private Helper Methods (copied from PCCCComm) ────────────────────
    
    /// <summary>
    /// Reads raw data from the PLC with automatic chunking.
    /// Original method from PCCCComm.
    /// </summary>
    private byte[] ReadRawDataWithChunking(ref DataAddress addr, int numberOfBytes, out int finalStatus)
    {
        // [COPY EXACT IMPLEMENTATION FROM PCCCComm.ReadRawDataWithChunking]
        // Replace:
        //   _protocol! -> _protocol
        //   MyNode/TargetNode -> properties
        //   _processorType -> _processorType
        //   _disableEvent -> not used here
        finalStatus = 0;
        int filePosition = 0;
        byte[] result = new byte[numberOfBytes];

        while (filePosition < numberOfBytes && finalStatus == 0)
        {
            int toRead = Math.Min(numberOfBytes - filePosition, PCCCConstants.Df1Limits.MaxReadPayloadBytes);
            if (toRead > PCCCConstants.Df1Limits.MaxStringReadBytes && addr.FileType == (byte)PCCCConstants.SlcFileTypeCode.String)
                toRead = PCCCConstants.Df1Limits.MaxStringReadBytes;
            if (toRead > PCCCConstants.Df1Limits.MaxTimerCounterReadBytes && (addr.FileType == (byte)PCCCConstants.SlcFileTypeCode.Timer || addr.FileType == (byte)PCCCConstants.SlcFileTypeCode.Counter))
                toRead = PCCCConstants.Df1Limits.MaxTimerCounterReadBytes;
            if (toRead > PCCCConstants.Df1Limits.MaxSlc502ReadBytes && _processorType == (byte)PCCCConstants.ProcessorTypeCode.SLC502)
                toRead = PCCCConstants.Df1Limits.MaxSlc502ReadBytes;
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

            const byte stringFileType = (byte)PCCCConstants.SlcFileTypeCode.String; // 0x8D, 84 bytes per element
            const int stringElementBytes = 84; // size of one ST file element

            if (addr.FileType == stringFileType)
                addr.Element += toRead / stringElementBytes; // move to next string element
            else
                addr.SubElement += toRead / 2;               // move by words (2 bytes each)
        }
        return result;
    }

    /// <summary>
    /// Writes raw data to the PLC with automatic chunking.
    /// Original method from PCCCComm.
    /// </summary>
    private int WriteRawDataWithChunking(DataAddress addr, byte[] dataToWrite)
    {
        if (addr.FileType == 0) return -5;
        int filePosition = 0;
        int reply = 0;

        while (filePosition < dataToWrite.Length && reply == 0)
        {
            int toWrite = Math.Min(dataToWrite.Length - filePosition, PCCCConstants.Df1Limits.MaxWritePayloadBytes);
            if (addr.FileType >= 0xA1 && toWrite > 0x78) toWrite = 0x78;

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

            const byte stringFileType = (byte)PCCCConstants.SlcFileTypeCode.String;
            const int stringElementBytes = 84;

            if (addr.FileType == stringFileType)
                addr.Element += toWrite / stringElementBytes;
            else
                addr.SubElement += toWrite / 2;
        }
        if (reply == 0) return 0;
        throw new PCCCException(PCCCErrors.DecodeStatus(reply));
    }

    /// <summary>
    /// Reads the file directory (file zero data) from the processor.
    /// Original method from PCCCComm.
    /// </summary>
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

        byte[] data = ReadRawDataWithChunking(ref pAddr, 2, out int reply);
        if (reply != 0) throw new PCCCException("Failed to Get Program Directory Size - " + PCCCErrors.DecodeStatus(reply));

        pAddr.Element = 0;
        pAddr.SubElement = 0;
        int size = data[0] + data[1] * 256;
        byte[] fzd = ReadRawDataWithChunking(ref pAddr, size, out reply);
        if (reply != 0) throw new PCCCException("Failed to Get Program Directory - " + PCCCErrors.DecodeStatus(reply));
        return fzd;
    }

    private static int FileTypeToBytesPerElement(byte code, out string fileTypeStr)
    {
        var type = (PCCCConstants.SlcFileTypeCode)code;
        fileTypeStr = PCCCConstants.SlcFileTypeInfo.GetTypeName(type);
        return PCCCConstants.SlcFileTypeInfo.GetBytesPerElement(type);
    }

    // ─── Public API Implementation ─────────────────────────────────────────
    // (All methods below are copied verbatim from PCCCComm, with field adjustments)

    /// <summary>
    /// Gets the processor type code.
    /// Original documentation: Returns the processor type code (e.g., 0x49 for SLC 5/03).
    /// </summary>
    public int GetProcessorType()
    {
        _processorType = _protocol.GetProcessorType((byte)MyNode, (byte)TargetNode);
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
        if (_processorType == 0)
            _processorType = GetProcessorType();
        bool isMl = (_processorType == (byte)PCCCConstants.ProcessorTypeCode.ML1000);
        _protocol.SetRunMode(isMl, (byte)MyNode, (byte)TargetNode);
    }

    public void SetProgramMode()
    {
        if (_processorType == 0)
            _processorType = GetProcessorType();
        bool isMl = (_processorType == (byte)PCCCConstants.ProcessorTypeCode.ML1000);
        _protocol.SetProgramMode(isMl, (byte)MyNode, (byte)TargetNode);
    }

    public int SetCpuMode(byte modeValue)
    {
        var req = PCCCMessage.CreateChangeModeRequest(modeValue, false, 0, (byte)MyNode, (byte)TargetNode);
        _protocol.SendRequest(req, out int sts);
        return sts;
    }

    public int GetRunMode()
    {
        var req = PCCCMessage.CreateDiagnosticStatusRequest(0, (byte)MyNode, (byte)TargetNode);
        var reply = _protocol.SendRequest(req, out int sts);
        if (sts != PCCCConstants.Sts.Success || reply?.Data == null || reply.Data.Length <= PCCCConstants.ResponseOffsets.DiagnosticStatus.ModeCode)
            return -1;
        byte modeCode = reply.Data[PCCCConstants.ResponseOffsets.DiagnosticStatus.ModeCode];
        return (modeCode == 0x06 || modeCode == 0x1E) ? 1 : 0;
    }

    public int DisableForces()
    {
        var req = PCCCMessage.CreateDisableForcesRequest(0, (byte)MyNode, (byte)TargetNode);
        _protocol.SendRequest(req, out int sts);
        return sts;
    }

    /// <summary>
    /// Reads data from the specified address and returns it as strings.
    /// Supports integer, float, string, timer/counter, and bit-level addressing.
    /// </summary>
    public string[] ReadAny(string startAddress, int numberOfElements)
    {
        DataAddress p = PCCCParser.Parse(startAddress);
        if (p.FileType == 0) throw new PCCCException("Invalid Address");

        short arrayElements = (short)(numberOfElements - 1);
        if (arrayElements < 0) arrayElements = 0;
        if (p.BitNumber < 16)
            arrayElements = (short)Math.Floor(numberOfElements / 16.0);

        // Calculate total bytes needed based on file type
        int bytesPerElem = p.BytesPerElements;
        if (p.SubElement > 0 && (p.FileType == (byte)PCCCConstants.SlcFileTypeCode.Timer || p.FileType == (byte)PCCCConstants.SlcFileTypeCode.Counter))
            bytesPerElem = 2;
        int numberOfBytes = (arrayElements + 1) * bytesPerElem;

        // Special adjustment for timer/counter sub-element reads
        if (p.SubElement > 0 && (p.FileType == (byte)PCCCConstants.SlcFileTypeCode.Timer || p.FileType == (byte)PCCCConstants.SlcFileTypeCode.Counter))
            numberOfBytes = (numberOfBytes * 3) - 4;

        // Read raw data with chunking
        byte[] returnedData = ReadRawDataWithChunking(ref p, numberOfBytes, out int reply);
        if (reply != 0)
            throw new PCCCException(PCCCErrors.DecodeStatus(reply));

        // Convert bytes to string based on data type
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
            case (byte)PCCCConstants.SlcFileTypeCode.Message:
                for (int i = 0; i <= arrayElements; i++)
                    result[i] = BitConverter.ToString(returnedData, i * 50, 50);
                break;
            default:
                for (int i = 0; i <= arrayElements; i++)
                    result[i] = BitConverter.ToInt16(returnedData, i * 2).ToString();
                break;
        }

        // Bit-level extraction
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
            parsed[i] = PCCCParser.Parse(addresses[i]);
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
            {
                if (dataToWrite[i] > int.MaxValue || dataToWrite[i] < int.MinValue)
                    throw new PCCCException("Integer data out of range, must be between -2147483648 and 2147483647");
                BitConverter.GetBytes((int)dataToWrite[i]).CopyTo(converted, i * 4);
            }
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
        
        // ST file (SLC 500 String file, type 0x8D)
        if (p.FileType == (byte)PCCCConstants.SlcFileTypeCode.String)
        {
            byte[] stElement = new byte[84];
            int len = dataToWrite.Length;
            stElement[0] = (byte)(len & 0xFF);
            stElement[1] = (byte)((len >> 8) & 0xFF);
            for (int i = 0; i < len; i++)
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

    // ─── Data Memory ───────────────────────────────────────────────────────
    public DataFileDetails[] GetDataMemory()
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

    // ─── I/O Configuration ─────────────────────────────────────────────────
    public int GetSlotCount()
    {
        byte[] body = { 4, 0, 0x60, 0, 0 };
        var req = new PCCCMessage((byte)TargetNode, (byte)MyNode, PCCCConstants.Cmd.ProtectedWrite, 0, 0, PCCCConstants.Fnc.GetSlotCount, body);
        var reply = _protocol.SendRequest(req, out int sts);
        if (sts != PCCCConstants.Sts.Success || reply?.Data == null || reply.Data.Length == 0)
            throw new PCCCException("Failed to get Slot Count - " + PCCCErrors.DecodeStatus(sts));
        return reply.Data[0] > 0 ? reply.Data[0] - 1 : 0;
    }

    public IOConfig[] GetIOConfig()
    {
        int pt = GetProcessorType();
        return (pt == (byte)PCCCConstants.ProcessorTypeCode.ML1500LSP || pt == (byte)PCCCConstants.ProcessorTypeCode.ML1500LRP)
            ? GetML1500IOConfig() : GetSLCIOConfig();
    }

    private IOConfig[] GetSLCIOConfig()
    {
        int slots = GetSlotCount();
        if (slots <= 0) throw new PCCCException("Failed to get Slot Count");
        byte[] body = { (byte)(4 + (slots + 1) * 6 + 2), 0, 0x60, 0, 0 };
        var req = new PCCCMessage((byte)TargetNode, (byte)MyNode, PCCCConstants.Cmd.ProtectedWrite, 0, 0, PCCCConstants.Fnc.GetIOConfig, body);
        var reply = _protocol.SendRequest(req, out int sts);
        if (sts != PCCCConstants.Sts.Success || reply?.Data == null)
            throw new PCCCException("Failed to get IO Config - " + PCCCErrors.DecodeStatus(sts));

        var result = new IOConfig[slots + 1];
        for (int i = 0; i <= slots; i++)
        {
            int baseOffset = i * 6;
            if (baseOffset + 9 >= reply.Data.Length)
                throw new PCCCException($"IO Config packet too short for slot {i}.");
            result[i].InputBytes = reply.Data[baseOffset + 4];
            result[i].OutputBytes = reply.Data[baseOffset + 6];
            result[i].CardCode = BitConverter.ToInt16(new byte[] { reply.Data[baseOffset + 8], reply.Data[baseOffset + 9] }, 0);
        }
        return result;
    }

    private IOConfig[] GetML1500IOConfig()
    {
        // First read: get size
        byte[] body = { 4, 0, 0x62, 0, 0 };
        var req = new PCCCMessage((byte)TargetNode, (byte)MyNode, PCCCConstants.Cmd.ProtectedWrite, 0, 0, PCCCConstants.Fnc.GetIOConfig, body);
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

            var chunkReq = new PCCCMessage((byte)TargetNode, (byte)MyNode, PCCCConstants.Cmd.ProtectedWrite, 0, 0, PCCCConstants.Fnc.GetIOConfig, chunkBody);
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
        var baseReq = new PCCCMessage((byte)TargetNode, (byte)MyNode, PCCCConstants.Cmd.ProtectedWrite, 0, 0, PCCCConstants.Fnc.GetIOConfig, baseBody);
        var baseReply = _protocol.SendRequest(baseReq, out int baseSts);
        if (baseSts != PCCCConstants.Sts.Success || baseReply?.Data == null || baseReply.Data.Length <= 6)
            throw new PCCCException("Failed to get Base IO Config for ML1500 - " + PCCCErrors.DecodeStatus(baseSts));
        result[0].InputBytes = baseReply.Data[4];
        result[0].OutputBytes = baseReply.Data[6];

        return result;
    }

    // ─── Upload / Download ─────────────────────────────────────────────────
    public Collection<PLCFileDetails> UploadProgramData()
    {
        DisableEventFlag = true;
        try
        {
            byte[] fzd = ReadFileDirectory();
            var programFiles = new Collection<PLCFileDetails>();
            programFiles.Add(new PLCFileDetails { FileNumber = 0, Data = fzd, FileType = 0, NumberOfBytes = fzd.Length });

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

            int numberOfProgramFiles = fzd[PCCCConstants.ResponseOffsets.FileDirectory.NumberOfProgramFilesLo]
                                     + fzd[PCCCConstants.ResponseOffsets.FileDirectory.NumberOfProgramFilesHi] * 256;
            int numberOfDataFiles = fzd[PCCCConstants.ResponseOffsets.FileDirectory.NumberOfDataFilesLo]
                                  + fzd[PCCCConstants.ResponseOffsets.FileDirectory.NumberOfDataFilesHi] * 256;
            int totalEntries = numberOfProgramFiles + numberOfDataFiles;

            int filePosition = (_processorType == (byte)PCCCConstants.ProcessorTypeCode.SLC502 || _processorType == (byte)PCCCConstants.ProcessorTypeCode.ML1000)
                ? PCCCConstants.ResponseOffsets.FileDirectory.StartOffsetSlc502Ml1000
                : (_processorType == (byte)PCCCConstants.ProcessorTypeCode.ML1200 || _processorType == (byte)PCCCConstants.ProcessorTypeCode.ML1500LSP || _processorType == (byte)PCCCConstants.ProcessorTypeCode.ML1500LRP || _processorType == (byte)PCCCConstants.ProcessorTypeCode.ML1100)
                    ? PCCCConstants.ResponseOffsets.FileDirectory.StartOffsetMl1100Ml1500
                    : PCCCConstants.ResponseOffsets.FileDirectory.StartOffsetDefault;

            long grandTotalBytes = 0;
            int tempPos = filePosition;
            for (int j = 0; j < totalEntries && tempPos < fzd.Length; j++)
            {
                int sizeBytes = fzd[tempPos + 1] + fzd[tempPos + 2] * 256;
                grandTotalBytes += sizeBytes;
                tempPos += (_processorType == (byte)PCCCConstants.ProcessorTypeCode.SLC502 || _processorType == (byte)PCCCConstants.ProcessorTypeCode.ML1000) ? 8 : 10;
            }
            grandTotalBytes += fzd.Length;

            int i = 0;
            long totalBytesTransferred = fzd.Length;
            int filesCompleted = 1;

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
        finally
        {
            DisableEventFlag = false;
        }
    }

    public void DownloadProgramData(Collection<PLCFileDetails> plcFiles)
    {
        DisableEventFlag = true;
        try
        {
            SetProgramMode();

            long grandTotalBytes = plcFiles.Sum(f => f.Data?.Length ?? 0);
            long totalBytesTransferred = 0;
            int filesCompleted = 0;
            int totalFiles = plcFiles.Count;

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
            var completeReq = new PCCCMessage((byte)TargetNode, (byte)MyNode, PCCCConstants.Cmd.ProtectedWrite, 0, 0, PCCCConstants.Fnc.DownloadComplete, Array.Empty<byte>());
            var completeReply = _protocol.SendRequest(completeReq, out int completeSts);
            if (completeSts != 0) throw new PCCCException("Failed to Indicate to PLC that Download is complete - " + PCCCErrors.DecodeStatus(completeSts));

            // Step 7: Release sole access
            var releaseReq = new PCCCMessage((byte)TargetNode, (byte)MyNode, PCCCConstants.Cmd.ProtectedWrite, 0, 0, PCCCConstants.Fnc.ReleaseAccess, Array.Empty<byte>());
            var releaseReply = _protocol.SendRequest(releaseReq, out int releaseSts);
            if (releaseSts != 0) throw new PCCCException("Failed to Release Sole Access - " + PCCCErrors.DecodeStatus(releaseSts));
        }
        finally
        {
            DisableEventFlag = false;
        }
    }
}
