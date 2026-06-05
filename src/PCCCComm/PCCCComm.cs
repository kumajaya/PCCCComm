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

// **********************************************************************************************
// * PCCC Application Layer (PCCCComm) – fully compatible with original DF1Comm VB code.
// * Uses ITransport for data link layer (framing, ACK/NAK, checksum)
// * Uses PacketBuilder for packet assembly
// * All behaviors (polling, SleepDelay, TNS, retries) are identical to the original.
// *
// * Original VB: Archie Jacobs, Manufacturing Automation LLC
// * C# Port: modularized, behavior‑preserving, zero‑behavioral‑gap
// * Reference: Allen Bradley Publication 1770-6.5.16
// *
// * Note: DH485 support has been temporarily removed. Only DF1 full‑duplex is supported.
// **********************************************************************************************

using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text;
using PCCCComm.Core;

namespace PCCCComm;

/// <summary>
/// PCCC application layer for communicating with Allen‑Bradley PLCs (DF1, EIP, DH485).
/// This class is transport‑agnostic; it uses an <see cref="ITransport"/> implementation
/// to send and receive inner PCCC frames.
/// </summary>
public class PCCCComm : IDisposable
{
    // ─── Fields (compatible with original) ─────────────────────────────────
    private readonly Random rnd = new Random();
    private ushort TNS;
    private int ProcessorType;

    // TNS 16‑bit response handling (replaces Responded(255) and DataPackets array)
    private readonly ConcurrentDictionary<ushort, ManualResetEventSlim> _responseEvents = new();
    private readonly ConcurrentDictionary<ushort, byte[]> _dataPackets = new();

    // Flag to suppress DataReceived events during bulk transfers
    private volatile bool DisableEvent;

    // Transport abstraction (DF1, EIP, etc.)
    private ITransport? _currentTransport;
    private readonly string? _eipHost = null;
    private readonly int    _eipPort  = 0;

    // Named methods for forwarding raw frame events (to allow unsubscribe)
    private void ForwardRawFrameSent(object? sender, byte[] data) => RawFrameSent?.Invoke(sender, data);
    private void ForwardRawFrameReceived(object? sender, byte[] data) => RawFrameReceived?.Invoke(sender, data);

    // ─── Properties (exactly as original) ──────────────────────────────────
    public int MyNode { get; set; }
    public int TargetNode { get; set; }

    private int m_BaudRate = 19200;
    public int BaudRate
    {
        get => m_BaudRate;
        set { if (value != m_BaudRate) CloseComms(); m_BaudRate = value; }
    }

    private string m_ComPort = "COM1";
    public string ComPort
    {
        get => m_ComPort;
        set { if (value != m_ComPort) CloseComms(); m_ComPort = value; }
    }

    private System.IO.Ports.Parity m_Parity = System.IO.Ports.Parity.None;
    public System.IO.Ports.Parity Parity
    {
        get => m_Parity;
        set { if (value != m_Parity) CloseComms(); m_Parity = value; }
    }

    // Protocol property kept for API compatibility; only "DF1" is supported.
    // Setting any other value throws NotSupportedException.
    private string m_Protocol = "DF1";
    public string Protocol
    {
        get => m_Protocol;
        set
        {
            if (value != "DF1")
                throw new NotSupportedException($"Protocol '{value}' is not supported. " + 
                        "Only 'DF1' is supported in this version.");
            m_Protocol = value;
        }
    }

    private CheckSumOptions m_CheckSum = CheckSumOptions.Crc;
    public CheckSumOptions CheckSum
    {
        get => m_CheckSum;
        set
        {
            m_CheckSum = value;
            if (_currentTransport is DF1Transport df1)
                df1.ChecksumType = value;
        }
    }

    public bool AsyncMode { get; set; }

    // ─── Events ──────────────────────────────────────────────────────────────
    public event EventHandler? DataReceived;
    public event EventHandler? UnsolicitedMessageRcvd;
    public event EventHandler? AutoDetectTry;
    public event EventHandler<FileProgressEventArgs>? FileProgress;

    // Events for logging support (kept for API compatibility, currently not implemented)
    public event EventHandler<byte[]>? RawFrameSent;
    public event EventHandler<byte[]>? RawFrameReceived;

    private int responseTimeoutMs = 2000;
    public int ResponseTimeoutMs
    {
        get => responseTimeoutMs;
        set
        {
            responseTimeoutMs = value > 0 ? value : 2000;
            if (_currentTransport is DF1Transport df1)
                df1.MaxTicks = responseTimeoutMs / 20;  // each tick = 20 ms
        }
    }

    /// <summary>Event arguments for file progress during upload/download.</summary>
    public class FileProgressEventArgs : EventArgs
    {
        public int FileNumber { get; set; }
        public int FileType { get; set; }
        public int FileSizeBytes { get; set; }
        public int FilesCompleted { get; set; }
        public int TotalFiles { get; set; }
        public long TotalBytesTransferred { get; set; }
        public long GrandTotalBytes { get; set; }
    }

    // ─── Constructors ───────────────────────────────────────────────────────
    /// <summary>
    /// Initialises the library using a DF1 serial transport with the given parameters.
    /// The transport is not opened until <see cref="OpenComms"/> is called.
    /// </summary>
    public PCCCComm(string? portName = null, int baud = 19200,
                    System.IO.Ports.Parity parity = System.IO.Ports.Parity.None)
    {
        TNS = (ushort)((rnd.Next() & 0x7F) + 1);
        if (!string.IsNullOrEmpty(portName))
        {
            m_ComPort = portName;
            m_BaudRate = baud;
            m_Parity = parity;
        }
        // The transport will be created in OpenComms() to allow property changes before opening.
    }

    /// <summary>
    /// Initialises the library with an EIP (EtherNet/IP) transport.
    /// </summary>
    /// <param name="host">PLC IP address or hostname.</param>
    /// <param name="port">EIP port (default 44818).</param>
    /// <param name="timeoutMs">Timeout for send/receive operations (default 5000 ms).</param>
    public PCCCComm(string host, int port, int timeoutMs = 5000)
    {
        TNS = (ushort)((rnd.Next() & 0x7F) + 1);
        responseTimeoutMs = timeoutMs;
        _eipHost = host;
        _eipPort = port;
        _currentTransport = new EIPTransport(host, port, timeoutMs);
        _currentTransport.FrameReceived += OnFrameReceived;
        // EIPTransport has no checksum type or raw frame diagnostic events.
    }

    /// <summary>
    /// Initialises the library with a custom transport implementation.
    /// The caller is responsible for opening and closing the transport.
    /// </summary>
    public PCCCComm(ITransport transport)
    {
        TNS = (ushort)((rnd.Next() & 0x7F) + 1);
        _currentTransport = transport ?? throw new ArgumentNullException(nameof(transport));
        _currentTransport.FrameReceived += OnFrameReceived;

        if (_currentTransport is DF1Transport df1)
        {
            df1.ChecksumType = m_CheckSum;
            df1.MaxTicks = responseTimeoutMs / 20; // Sync timeout
            // Forward raw frame events if the transport is DF1Transport
            df1.RawFrameSent += ForwardRawFrameSent;
            df1.RawFrameReceived += ForwardRawFrameReceived;
        }
    }

    // =========================================================================
    // PUBLIC METHODS (identical to original, but using ITransport)
    // =========================================================================

    /// <summary>Place the processor in Run mode.</summary>
    public void SetRunMode()
    {
        byte[] data = new byte[1];
        int func;
        if (GetProcessorType() == 0x58) { data[0] = 2; func = 0x3A; }
        else { data[0] = 6; func = 0x80; }
        int reply = PrefixAndSend(0xF, func, data, true, out _);
        if (reply != 0)
            throw new PCCCException("Failed to change to Run mode, Check PLC Key switch - " + MessageDecoder.DecodeMessage(reply));
    }

    /// <summary>Set CPU mode (PLC‑5 / MicroLogix).</summary>
    public int SetCPUMode(byte modeValue)
    {
        byte[] data = { modeValue };
        int reply = PrefixAndSend(0x0F, 0x3A, data, true, out _);
        return reply;
    }

    /// <summary>Get current processor mode (1 = RUN, 0 = PROGRAM, -1 = error).</summary>
    public int GetRunMode()
    {
        byte[] data = Array.Empty<byte>();
        int reply = PrefixAndSend(0x06, 0x03, data, true, out int rTNS);
        if (reply != 0)
            return -1;

        if (_dataPackets.TryGetValue((ushort)rTNS, out byte[]? pkt) && pkt.Length > 24)
        {
            byte modeCode = pkt[24];
            return (modeCode == 0x06 || modeCode == 0x1E) ? 1 : 0;
        }
        return -1;
    }

    /// <summary>Disable I/O forces.</summary>
    public int DisableForces()
    {
        int reply = PrefixAndSend(0x0F, 0x41, Array.Empty<byte>(), true, out _);
        return reply;
    }

    /// <summary>Place processor in Program mode.</summary>
    public void SetProgramMode()
    {
        byte[] data = new byte[1];
        int func;
        if (GetProcessorType() == 0x58) { data[0] = 0; func = 0x3A; }
        else { data[0] = 1; func = 0x80; }
        int reply = PrefixAndSend(0xF, func, data, true, out _);
        if (reply != 0)
            throw new PCCCException("Failed to change to Program mode, Check PLC Key switch - " + MessageDecoder.DecodeMessage(reply));
    }

    /// <summary>Read the processor type code (e.g., 0x49 for SLC 5/03).</summary>
    public int GetProcessorType()
    {
        byte[] data = Array.Empty<byte>();
        int reply = PrefixAndSend(6, 3, data, true, out int rTNS);
        if (reply != 0)
            throw new PCCCException("GetProcessorType failed: " + MessageDecoder.DecodeMessage(reply));

        if (_dataPackets.TryGetValue((ushort)rTNS, out byte[]? pkt) && pkt.Length > 9)
        {
            ProcessorType = pkt[9];
            return ProcessorType;
        }

        throw new PCCCException("GetProcessorType: response packet too short or missing");
    }

    /// <summary>Get raw Diagnostic Status payload (CMD 0x06 FNC 0x03).</summary>
    public byte[]? GetDiagnosticStatusRaw()
    {
        byte[] data = Array.Empty<byte>();
        int reply = PrefixAndSend(0x06, 0x03, data, true, out int rTNS);
        if (reply != 0)
            return null;

        if (_dataPackets.TryGetValue((ushort)rTNS, out byte[]? pkt) && pkt.Length > 6)
        {
            // Guard: if status byte (STS) is non-zero, the request failed
            if (pkt[3] != 0) return null;
            int dataLen = pkt.Length - 6;
            if (dataLen <= 0) return Array.Empty<byte>();
            var result = new byte[dataLen];
            Array.Copy(pkt, 6, result, 0, dataLen);
            return result;
        }
        return null;
    }

    // ─── Read ─────────────────────────────────────────────────────────────────
    public string[] ReadAny(string startAddress, int numberOfElements)
    {
        DataAddress p = AddressParser.Parse(startAddress);
        if (p.FileType == 0) throw new PCCCException("Invalid Address");

        short arrayElements = (short)(numberOfElements - 1);
        if (arrayElements < 0) arrayElements = 0;
        if (p.BitNumber < 16) arrayElements = (short)Math.Floor(numberOfElements / 16.0);

        int numberOfBytes;
        switch (p.FileType)
        {
            case 0x8D: numberOfBytes = (arrayElements + 1) * 84; break;
            case 0x8A: numberOfBytes = (arrayElements + 1) * 4; break;
            case 0x91: numberOfBytes = (arrayElements + 1) * 4; break;
            case 0x92: numberOfBytes = (arrayElements + 1) * 50; break;
            case 0x86:
            case 0x87: numberOfBytes = (arrayElements + 1) * 2; break;
            default: numberOfBytes = (arrayElements + 1) * 2; break;
        }

        if (p.SubElement > 0 && (p.FileType == 0x86 || p.FileType == 0x87))
            numberOfBytes = (numberOfBytes * 3) - 4;

        int reply = 0;
        byte[] returnedData = Array.Empty<byte>();
        int retries = 0;

        while (retries <= 2)
        {
            returnedData = ReadRawData(p, numberOfBytes, out reply);
            if (reply == 0)
                break;
            if (retries < 2)
            {
                retries++;
                continue;
            }
            throw new PCCCException(MessageDecoder.DecodeMessage(reply));
        }

        string[] result = new string[arrayElements + 1];
        switch (p.FileType)
        {
            case 0x8A:
                for (int i = 0; i <= arrayElements; i++)
                    result[i] = BitConverter.ToSingle(returnedData, i * 4).ToString();
                break;
            case 0x8D:
                for (int i = 0; i <= arrayElements; i++)
                {
                    int strLen = BitConverter.ToInt16(returnedData, i * 84);
                    if (strLen > 82) strLen = 82;
                    var sb = new StringBuilder();
                    int j = 2;
                    while (j < strLen + 2 && (i * 84) + j + 1 < returnedData.Length &&
                            returnedData[(i * 84) + j + 1] > 0)
                    {
                        sb.Append(Encoding.ASCII.GetString(new byte[] { returnedData[(i * 84) + j + 1] }));
                        if (j < strLen + 1 && returnedData[(i * 84) + j] > 0)
                            sb.Append(Encoding.ASCII.GetString(new byte[] { returnedData[(i * 84) + j] }));
                        j += 2;
                    }
                    result[i] = sb.ToString();
                }
                break;
            case 0x86:
            case 0x87:
                for (int i = 0; i <= arrayElements; i++)
                {
                    int j = p.SubElement > 0 ? i * 6 : i * 2;
                    result[i] = BitConverter.ToInt16(returnedData, j).ToString();
                }
                break;
            case 0x91:
                for (int i = 0; i <= arrayElements; i++)
                    result[i] = BitConverter.ToInt32(returnedData, i * 4).ToString();
                break;
            case 0x92:
                for (int i = 0; i <= arrayElements; i++)
                    result[i] = BitConverter.ToString(returnedData, i * 50, 50);
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
                bitResult[i] = ((Convert.ToInt32(result[wordPos]) & (int)Math.Pow(2, bitPos)) != 0).ToString();
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
            parsed[i] = AddressParser.Parse(addresses[i]);
            if (parsed[i].FileType == 0)
                throw new PCCCException($"ReadModifyWrite: invalid address '{addresses[i]}'.");
        }

        byte[] body = PacketBuilder.BuildReadModifyWriteBody(parsed, andMasks, orMasks);
        int reply = PrefixAndSend(0x0F, 0x26, body, true, out _);
        return reply;
    }

    // ─── Write ────────────────────────────────────────────────────────────────
    public string WriteData(string startAddress, int dataToWrite)
        => WriteData(startAddress, 1, new int[] { dataToWrite }).ToString();

    public int WriteData(string startAddress, int numberOfElements, int[] dataToWrite)
    {
        DataAddress p = AddressParser.Parse(startAddress);
        byte[] converted = new byte[numberOfElements * p.BytesPerElements + 1];
        if (p.FileType == 0x91)
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
        return WriteRawData(p, numberOfElements * p.BytesPerElements, converted);
    }

    public int WriteData(string startAddress, float dataToWrite)
        => WriteData(startAddress, 1, new float[] { dataToWrite });

    public int WriteData(string startAddress, int numberOfElements, float[] dataToWrite)
    {
        DataAddress p = AddressParser.Parse(startAddress);
        byte[] converted = new byte[numberOfElements * p.BytesPerElements + 1];
        if (p.FileType == 0x8A)
        {
            for (int i = 0; i < numberOfElements; i++)
                BitConverter.GetBytes(dataToWrite[i]).CopyTo(converted, i * 4);
        }
        else if (p.FileType == 0x91)
        {
            for (int i = 0; i < numberOfElements; i++)
            {
                if (dataToWrite[i] > 2147483647 || dataToWrite[i] < -2147483648)
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
        return WriteRawData(p, numberOfElements * p.BytesPerElements, converted);
    }

    public int WriteData(string startAddress, string dataToWrite)
    {
        if (string.IsNullOrEmpty(dataToWrite)) return 0;
        if (dataToWrite.Length > 82) dataToWrite = dataToWrite[..82];

        DataAddress p = AddressParser.Parse(startAddress);
        int[]? words = StringConverter.StringToWords(dataToWrite);
        if (words == null) return -1;

        byte[] converted = new byte[words.Length * 2 + 2];
        converted[0] = (byte)dataToWrite.Length;
        for (int i = 0; i < words.Length; i++)
        {
            converted[i * 2 + 2] = (byte)((words[i] >> 8) & 0xFF);
            converted[i * 2 + 3] = (byte)(words[i] & 0xFF);
        }
        return WriteRawData(p, dataToWrite.Length + 2, converted);
    }

    // ─── Data Memory ─────────────────────────────────────────────────────────
    public DataFileDetails[] GetDataMemory()
    {
        byte[] fzd = ReadFileDirectory();
        int numberOfDataTables = fzd[52] + fzd[53] * 256;
        var dataFiles = new Collection<DataFileDetails>();

        int filePosition, bytesPerRow;
        switch (ProcessorType)
        {
            case 0x25: case 0x58: filePosition = 93; bytesPerRow = 8; break;
            case 0x88: case 0x89: case 0x8C: case 0x9C: filePosition = 103; bytesPerRow = 10; break;
            default: filePosition = 79; bytesPerRow = 10; break;
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
        byte[] data = ReadRawData(pAddr, 2, out int reply);
        if (reply != 0) throw new PCCCException(MessageDecoder.DecodeMessage(reply) + " - Failed to get data table list");

        int fzSize = data[0] + data[1] * 256;
        pAddr.Element = 0; pAddr.SubElement = 0;
        byte[] fzd = ReadRawData(pAddr, fzSize, out reply);
        if (reply != 0) throw new PCCCException(MessageDecoder.DecodeMessage(reply) + " - Failed to get data table list");

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

    // ─── IO Config ────────────────────────────────────────────────────────────
    public int GetSlotCount()
    {
        byte[] data = { 4, 0, 0x60, 0, 0 };
        int reply = PrefixAndSend(0xF, 0xA2, data, true, out int rTNS);
        if (reply == 0 && _dataPackets.TryGetValue((ushort)rTNS, out byte[]? pkt) && pkt.Length > 6)
            return pkt[6] > 0 ? pkt[6] - 1 : 0;
        throw new PCCCException("Failed to get Slot Count - " + MessageDecoder.DecodeMessage(reply));
    }

    public IOConfig[] GetIOConfig()
    {
        int pt = GetProcessorType();
        return (pt == 0x89 || pt == 0x8C) ? GetML1500IOConfig() : GetSLCIOConfig();
    }

    public IOConfig[] GetSLCIOConfig()
    {
        int slots = GetSlotCount();
        if (slots <= 0) throw new PCCCException("Failed to get Slot Count");
        byte[] data = { (byte)(4 + (slots + 1) * 6 + 2), 0, 0x60, 0, 0 };
        int reply = PrefixAndSend(0xF, 0xA2, data, true, out int rTNS);
        if (reply != 0) throw new PCCCException("Failed to get IO Config - " + MessageDecoder.DecodeMessage(reply));

        var result = new IOConfig[slots + 1];
        if (!_dataPackets.TryGetValue((ushort)rTNS, out byte[]? pkt))
            throw new PCCCException("No IO Config data returned from PLC.");
        for (int i = 0; i <= slots; i++)
        {
            if (i * 6 + 15 >= pkt.Length)
                throw new PCCCException($"IO Config packet too short for slot {i}.");
            result[i].InputBytes  = pkt[i * 6 + 10];
            result[i].OutputBytes = pkt[i * 6 + 12];
            result[i].CardCode    = BitConverter.ToInt16(new byte[] { pkt[i * 6 + 14], pkt[i * 6 + 15] }, 0);
        }
        return result;
    }

    public IOConfig[] GetML1500IOConfig()
    {
        byte[] data = { 4, 0, 0x62, 0, 0 };
        int reply = PrefixAndSend(0xF, 0xA2, data, true, out int rTNS);
        if (reply != 0) throw new PCCCException("Failed to get IO Config for ML1500 - " + MessageDecoder.DecodeMessage(reply));

        if (!_dataPackets.TryGetValue((ushort)rTNS, out byte[]? pkt0) || pkt0.Length <= 6)
            throw new PCCCException("Failed to get IO Config for ML1500 - response too short");
        int fzSize = pkt0[6] * 2;
        byte[] fzd = new byte[fzSize + 1];
        int filePosition = 0, subElement = 0;
        data[0] = (byte)(fzSize > 0x50 ? 0x50 : fzSize);

        while (filePosition < fzSize && reply == 0)
        {
            reply = PrefixAndSend(0xF, 0xA2, data, true, out rTNS);
            if (_dataPackets.TryGetValue((ushort)rTNS, out byte[]? chunk))
            {
                int i = 0;
                while (i < data[0] && filePosition < fzSize) fzd[filePosition++] = chunk[i++ + 6];
            }

            subElement += data[0] / 2;
            if (subElement < 255) { data[3] = (byte)subElement; }
            else
            {
                if (data.Length < 6) Array.Resize(ref data, 6);
                data[3] = 255;
                data[4] = (byte)(subElement & 0xFF);
                data[5] = (byte)((subElement >> 8) & 0xFF);
            }
            data[0] = (byte)(fzSize - filePosition < 80 ? fzSize - filePosition : 80);
        }

        int slotCount = fzd[2] - 2; if (slotCount < 0) slotCount = 0;
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

        data = new byte[] { 8, 0, 0x60, 0, 0 };
        reply = PrefixAndSend(0xF, 0xA2, data, true, out rTNS);
        if (reply == 0 && _dataPackets.TryGetValue((ushort)rTNS, out byte[]? basePkt) && basePkt.Length > 12)
        {
            result[0].InputBytes  = basePkt[10];
            result[0].OutputBytes = basePkt[12];
        }
        else throw new PCCCException("Failed to get Base IO Config for ML1500 - " + MessageDecoder.DecodeMessage(reply));

        return result;
    }

    // ─── Upload / Download ────────────────────────────────────────────────────
    public Collection<PLCFileDetails> UploadProgramData()
    {
        DisableEvent = true;
        try
        {
            byte[] fzd = ReadFileDirectory();
            var programFiles = new Collection<PLCFileDetails>();
            programFiles.Add(new PLCFileDetails { FileNumber = 0, Data = fzd, FileType = 0, NumberOfBytes = fzd.Length });

            FileProgress?.Invoke(this, new FileProgressEventArgs
            {
                FileNumber = 0,
                FileType = 0,
                FileSizeBytes = fzd.Length,
                FilesCompleted = 1,
                TotalFiles = 1,
                TotalBytesTransferred = fzd.Length,
                GrandTotalBytes = fzd.Length
            });

            int numberOfProgramFiles = fzd[46] + fzd[47] * 256;
            int numberOfDataFiles = fzd[52] + fzd[53] * 256;
            int totalEntries = numberOfProgramFiles + numberOfDataFiles;

            int filePosition = ProcessorType == 0x25 || ProcessorType == 0x58 ? 93
                            : ProcessorType == 0x88 || ProcessorType == 0x89 || ProcessorType == 0x8C || ProcessorType == 0x9C ? 103
                            : 79;

            long grandTotalBytes = 0;
            int tempPos = filePosition;
            for (int j = 0; j < totalEntries && tempPos < fzd.Length; j++)
            {
                int sizeBytes = fzd[tempPos + 1] + fzd[tempPos + 2] * 256;
                grandTotalBytes += sizeBytes;
                tempPos += (ProcessorType == 0x25 || ProcessorType == 0x58) ? 8 : 10;
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
                    pf.Data = ReadRawData(addr, pf.NumberOfBytes, out int reply);
                    if (reply != 0 && reply != 0x50)
                        throw new PCCCException("Failed to Read Program File " + addr.FileNumber +
                                            ", Type " + addr.FileType + " - " + MessageDecoder.DecodeMessage(reply));
                    if (reply == 0x50)
                        pf.Data = Array.Empty<byte>();
                }
                else pf.Data = Array.Empty<byte>();

                programFiles.Add(pf);

                totalBytesTransferred += pf.NumberOfBytes;
                filesCompleted++;

                FileProgress?.Invoke(this, new FileProgressEventArgs
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
                filePosition += (ProcessorType == 0x25 || ProcessorType == 0x58) ? 8 : 10;
            }
            return programFiles;
        }
        finally
        {
            DisableEvent = false;
        }
    }

    public void DownloadProgramData(Collection<PLCFileDetails> plcFiles)
    {
        DisableEvent = true;
        try
        {
            SetProgramMode();

            long grandTotalBytes = plcFiles.Sum(f => f.Data?.Length ?? 0);
            long totalBytesTransferred = 0;
            int filesCompleted = 0;
            int totalFiles = plcFiles.Count;

            int dataLength = (ProcessorType == 0x5B || ProcessorType == 0x78) ? 13 : 15;
            byte[] data = new byte[dataLength + 1];
            data[0] = 0x02; data[1] = 0x0A; data[2] = 0xAA;
            data[3] = 4; data[4] = 0; data[5] = 0x63;

            int idx = 0;
            while (idx < plcFiles.Count && (plcFiles[idx].FileNumber != 0 || plcFiles[idx].FileType != 0x24)) idx++;
            if (idx < plcFiles.Count && plcFiles[idx].Data?.Length >= 8)
            {
                data[8] = plcFiles[idx].Data[2]; data[9] = plcFiles[idx].Data[3];
                data[10] = plcFiles[idx].Data[4]; data[11] = plcFiles[idx].Data[5];
                if (dataLength > 14) { data[12] = plcFiles[idx].Data[6]; data[13] = plcFiles[idx].Data[7]; }
            }

            var pAddr = new DataAddress();
            switch (ProcessorType)
            {
                case 0x78:
                case 0x5B:
                case 0x49:
                    pAddr.FileType = 0x63; pAddr.Element = 0;
                    byte[] four = ReadRawData(pAddr, 4, out int r4);
                    if (r4 != 0) throw new PCCCException("Failed to Read File 0, Type 63h - " + MessageDecoder.DecodeMessage(r4));
                    Array.Copy(four, 0, data, 8, 4);
                    pAddr.FileType = 1; pAddr.Element = 0x23;
                    data[1] = 0x0A; data[3] = 4;
                    break;
                case 0x88:
                case 0x89:
                case 0x8C:
                case 0x9C:
                    data[1] = 0x0C; data[3] = 6;
                    pAddr.FileType = 2; pAddr.Element = 0x23;
                    break;
                default:
                    data[1] = 0x0A; data[3] = 4;
                    pAddr.FileType = 1; pAddr.Element = 0x23;
                    break;
            }

            data[data.Length - 2] = 1;
            data[data.Length - 1] = 0x56;

            int reply = PrefixAndSend(0xF, 0x88, data, true, out _);
            if (reply != 0) throw new PCCCException("Failed to Initialize for Download - " + MessageDecoder.DecodeMessage(reply));

            byte[] empty = Array.Empty<byte>();

            reply = PrefixAndSend(0xF, 0x11, empty, true, out _);
            if (reply != 0) throw new PCCCException("Failed to Secure Sole Access - " + MessageDecoder.DecodeMessage(reply));

            pAddr.BitNumber = 16;
            byte[] data3 = { (byte)(plcFiles[0].Data.Length & 0xFF), (byte)((plcFiles[0].Data.Length >> 8) & 0xFF) };
            reply = WriteRawData(pAddr, 2, data3);
            if (reply != 0) throw new PCCCException("Failed to Write Directory Length - " + MessageDecoder.DecodeMessage(reply));

            totalBytesTransferred += 2;
            filesCompleted = 1;
            FileProgress?.Invoke(this, new FileProgressEventArgs
            {
                FileNumber = 0,
                FileType = 0,
                FileSizeBytes = 2,
                FilesCompleted = filesCompleted,
                TotalFiles = totalFiles,
                TotalBytesTransferred = totalBytesTransferred,
                GrandTotalBytes = grandTotalBytes
            });

            pAddr.Element = 0;
            reply = WriteRawData(pAddr, plcFiles[0].Data.Length, plcFiles[0].Data);
            if (reply != 0) throw new PCCCException("Failed to Write New Program Directory - " + MessageDecoder.DecodeMessage(reply));

            totalBytesTransferred += plcFiles[0].Data.Length;
            filesCompleted++;
            FileProgress?.Invoke(this, new FileProgressEventArgs
            {
                FileNumber = 0,
                FileType = 0,
                FileSizeBytes = plcFiles[0].Data.Length,
                FilesCompleted = filesCompleted,
                TotalFiles = totalFiles,
                TotalBytesTransferred = totalBytesTransferred,
                GrandTotalBytes = grandTotalBytes
            });

            for (int i = 1; i < plcFiles.Count; i++)
            {
                pAddr.FileNumber = plcFiles[i].FileNumber;
                pAddr.FileType = plcFiles[i].FileType;
                pAddr.Element = 0;
                pAddr.SubElement = 0;
                pAddr.BitNumber = 16;

                reply = WriteRawData(pAddr, plcFiles[i].Data.Length, plcFiles[i].Data);
                if (reply != 0) throw new PCCCException("Failed when writing files to PLC - " + MessageDecoder.DecodeMessage(reply));

                totalBytesTransferred += plcFiles[i].Data?.Length ?? 0;
                filesCompleted++;
                FileProgress?.Invoke(this, new FileProgressEventArgs
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

            reply = PrefixAndSend(0xF, 0x52, empty, true, out _);
            if (reply != 0) throw new PCCCException("Failed to Indicate to PLC that Download is complete - " + MessageDecoder.DecodeMessage(reply));

            reply = PrefixAndSend(0xF, 0x12, empty, true, out _);
            if (reply != 0) throw new PCCCException("Failed to Release Sole Access - " + MessageDecoder.DecodeMessage(reply));
        }
        finally
        {
            DisableEvent = false;
        }
    }

    // ─── Auto-detect (DF1 only) ─────────────────────────────────────────────────
    public int DetectCommSettings()
    {
        // Close any existing transport and create a temporary one for auto‑detect
        CloseComms();

        int[] baudRates = { 38400, 19200, 9600 };
        var parities = new System.IO.Ports.Parity[] { System.IO.Ports.Parity.None, System.IO.Ports.Parity.Even };
        var checksums = new CheckSumOptions[] { CheckSumOptions.Crc, CheckSumOptions.Bcc };

        int reply = -1;
        bool portError = false;

        // Save original settings to restore if detection fails
        string originalPort = m_ComPort;
        int originalBaud = m_BaudRate;
        var originalParity = m_Parity;
        var originalChecksum = m_CheckSum;

        foreach (int baud in baudRates)
        {
            if (reply == 0 || portError) break;
            foreach (var parity in parities)
            {
                if (reply == 0 || portError) break;
                foreach (var cs in checksums)
                {
                    // Set temporary parameters
                    m_BaudRate = baud;
                    m_Parity = parity;
                    m_CheckSum = cs;

                    AutoDetectTry?.Invoke(this, EventArgs.Empty);

                    // Create a temporary DF1 transport
                    try
                    {
                        var port = new SerialPortWrapper(m_ComPort, m_BaudRate, m_Parity);
                        var transport = new DF1Transport(port);
                        transport.ChecksumType = m_CheckSum;
                        transport.MaxTicks = 3; // short timeout for detection
                        transport.Open();

                        reply = transport.SendEnqAndWaitForAck();
                        transport.Close();
                        transport.Dispose();

                        if (reply == 0)
                        {
                            // Detection succeeded: keep these settings
                            // (do not update _currentTransport; caller must call OpenComms again)
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        if (ex.Message.Contains("Access") || ex.Message.Contains("port"))
                            portError = true;
                        reply = -6;
                    }

                    if (reply == -6) { portError = true; break; }
                }
            }
        }

        if (reply != 0)
        {
            // Restore original settings on failure
            m_ComPort = originalPort;
            m_BaudRate = originalBaud;
            m_Parity = originalParity;
            m_CheckSum = originalChecksum;
        }

        return reply;
    }

    // ─── Comms ────────────────────────────────────────────────────────────────
    /// <summary>
    /// Opens the communication channel. For DF1 serial, this creates a new
    /// <see cref="DF1Transport"/> using the current <see cref="ComPort"/>,
    /// <see cref="BaudRate"/>, <see cref="Parity"/>, and <see cref="CheckSum"/>.
    /// </summary>
    public int OpenComms()
    {
        // If a transport already exists, ensure it is open.
        if (_currentTransport != null)
        {
            if (!_currentTransport.IsOpen)
                _currentTransport.Open();
            return 0;
        }

        // No transport exists. Recreate based on connection type.
        if (_eipHost != null)
        {
            // Reconnect EIP transport (e.g. after CloseComms was called).
            try
            {
                var eip = new EIPTransport(_eipHost, _eipPort, responseTimeoutMs);
                eip.FrameReceived += OnFrameReceived;
                eip.Open();
                _currentTransport = eip;
                return 0;
            }
            catch (Exception ex)
            {
                throw new PCCCException($"Failed to connect to {_eipHost}:{_eipPort}. {ex.Message}");
            }
        }

        // Fall back to DF1 serial transport.
        try
        {
            var port = new SerialPortWrapper(m_ComPort, m_BaudRate, m_Parity);
            var transport = new DF1Transport(port);
            transport.ChecksumType = m_CheckSum;
            transport.FrameReceived += OnFrameReceived;
            transport.RawFrameSent += ForwardRawFrameSent;
            transport.RawFrameReceived += ForwardRawFrameReceived;
            transport.MaxTicks = responseTimeoutMs / 20;
            transport.Open();
            _currentTransport = transport;
            return 0;
        }
        catch (Exception ex)
        {
            throw new PCCCException("Failed To Open " + m_ComPort + ". " + ex.Message);
        }
    }

    /// <summary>
    /// Closes the communication channel if it is open.
    /// </summary>
    public void CloseComms()
    {
        if (_currentTransport != null)
        {
            _currentTransport.FrameReceived -= OnFrameReceived;
            // Unsubscribe raw frame events if applicable
            if (_currentTransport is DF1Transport df1)
            {
                df1.RawFrameSent -= ForwardRawFrameSent;
                df1.RawFrameReceived -= ForwardRawFrameReceived;
            }
            _currentTransport.Close();
            _currentTransport.Dispose();
            _currentTransport = null;
        }
    }

    // ─── String helpers ───────────────────────────────────────────────────────
    public static string WordsToString(int[] words) => StringConverter.WordsToString(words);
    public static string WordsToString(int[] words, int index) => StringConverter.WordsToString(words, index);
    public static string WordsToString(int[] words, int index, int count) => StringConverter.WordsToString(words, index, count);
    public static int[]? StringToWords(string source) => StringConverter.StringToWords(source);

    // =========================================================================
    // PRIVATE METHODS (updated to use ITransport and 16‑bit TNS)
    // =========================================================================

    private readonly object tnsLock = new object();

    private ushort IncrementAndGetTNS()
    {
        lock (tnsLock)
        {
            TNS = TNS < 65535 ? (ushort)(TNS + 1) : (ushort)1;
            _dataPackets.TryRemove(TNS, out _);
            _responseEvents.TryRemove(TNS, out _);
            return TNS;
        }
    }

    private int PrefixAndSend(int command, int func, byte[] data, bool wait, out int rTNS)
    {
        if (_currentTransport == null)
            throw new InvalidOperationException("Communications not open. Call OpenComms() first.");

        ushort currentTNS = IncrementAndGetTNS();
        byte[] pdu = PacketBuilder.BuildCommandWithData(command, func, data, currentTNS, MyNode, TargetNode, true);
        rTNS = currentTNS;

        ManualResetEventSlim? ev = null;
        if (wait)
        {
            ev = new ManualResetEventSlim(false);
            _responseEvents[currentTNS] = ev;
            _dataPackets.TryRemove(currentTNS, out _);
        }

        try
        {
            _currentTransport.SendFrame(pdu);
        }
        catch (TimeoutException)
        {
            if (wait && ev != null)
                _responseEvents.TryRemove(currentTNS, out _);
            return -3;
        }
        catch (Exception)
        {
            if (wait && ev != null)
                _responseEvents.TryRemove(currentTNS, out _);
            return -6;
        }

        if (!wait)
            return 0;

        if (!ev!.Wait(responseTimeoutMs))
            return -20;

        if (_dataPackets.TryGetValue(currentTNS, out byte[]? response))
        {
            if (response.Length > 3)
            {
                int sts = response[3];
                if (sts == 0xF0 && response.Length > 0)
                    sts = response[response.Length - 1] + 0x100;
                return sts;
            }
            return -8;
        }
        return -8;
    }

    // DF1 protocol limits (Publication 1770-6.5.16)
    private const int MaxReadPayloadBytes = 236;
    private const int MaxWritePayloadBytes = 164;
    private byte[] ReadRawData(DataAddress pAddr, int numberOfBytes, out int reply)
    {
        reply = 0;
        int filePosition = 0;
        byte[] result = new byte[numberOfBytes];

        while (filePosition < numberOfBytes && reply == 0)
        {
            int toRead = Math.Min(numberOfBytes - filePosition, MaxReadPayloadBytes);
            if (toRead > 168 && pAddr.FileType == 0x8D) toRead = 168;
            if (toRead > 234 && (pAddr.FileType == 0x86 || pAddr.FileType == 0x87)) toRead = 234;
            if (toRead > 0x78 && pAddr.FileType == 0xA4) toRead = 0x78;
            if (toRead > 0x50 && ProcessorType == 0x25) toRead = 0x50;
            if (toRead <= 0) break;

            byte[] body = PacketBuilder.BuildReadRequestBody(pAddr, toRead, out int func);
            reply = PrefixAndSend(0xF, func, body, true, out int rTNS);
            if (reply == 0 && _dataPackets.TryGetValue((ushort)rTNS, out byte[]? pkt))
            {
                for (int i = 0; i < toRead && (i + 6) < pkt.Length; i++)
                    result[filePosition + i] = pkt[i + 6];
            }

            filePosition += toRead;
            if (pAddr.FileType == 0xA4) pAddr.Element += toRead / 0x28;
            else pAddr.SubElement += toRead / 2;
        }
        return result;
    }

    private int WriteRawData(DataAddress p, int numberOfBytes, byte[] dataToWrite)
    {
        if (p.FileType == 0) return -5;
        int filePosition = 0, reply = 0;

        while (filePosition < numberOfBytes && reply == 0)
        {
            int toWrite = Math.Min(numberOfBytes - filePosition, MaxWritePayloadBytes);
            if (p.FileType >= 0xA1 && toWrite > 0x78) toWrite = 0x78;

            byte[] body = PacketBuilder.BuildWriteRequestBody(p, dataToWrite, filePosition, toWrite, out int func);
            reply = PrefixAndSend(0xF, func, body, !AsyncMode, out _);
            filePosition += toWrite;
            if (p.FileType != 0xA4) p.SubElement += toWrite / 2;
            else p.Element += toWrite / 0x28;
        }

        if (reply == 0) return 0;
        throw new PCCCException(MessageDecoder.DecodeMessage(reply));
    }

    private byte[] ReadFileDirectory()
    {
        GetProcessorType();
        var pAddr = new DataAddress();
        switch (ProcessorType)
        {
            case 0x25: case 0x58: pAddr.FileType = 0; pAddr.Element = 0x23; break;
            case 0x88: case 0x89: case 0x8C: case 0x9C: pAddr.FileType = 2; pAddr.Element = 0x2F; break;
            default: pAddr.FileType = 1; pAddr.Element = 0x23; break;
        }

        byte[] data = ReadRawData(pAddr, 2, out int reply);
        if (reply != 0) throw new PCCCException("Failed to Get Program Directory Size - " + MessageDecoder.DecodeMessage(reply));

        pAddr.Element = 0;
        int size = data[0] + data[1] * 256;
        byte[] fzd = ReadRawData(pAddr, size, out reply);
        if (reply != 0) throw new PCCCException("Failed to Get Program Directory - " + MessageDecoder.DecodeMessage(reply));
        return fzd;
    }

    private static int FileTypeToBytesPerElement(byte code, out string fileTypeStr)
    {
        switch (code)
        {
            case 0x82: case 0x8B: fileTypeStr = "O"; return 2;
            case 0x83: case 0x8C: fileTypeStr = "I"; return 2;
            case 0x84: fileTypeStr = "S"; return 2;
            case 0x85: fileTypeStr = "B"; return 2;
            case 0x86: fileTypeStr = "T"; return 6;
            case 0x87: fileTypeStr = "C"; return 6;
            case 0x88: fileTypeStr = "R"; return 6;
            case 0x89: fileTypeStr = "N"; return 2;
            case 0x8A: fileTypeStr = "F"; return 4;
            case 0x8D: fileTypeStr = "ST"; return 84;
            case 0x8E: fileTypeStr = "A"; return 2;
            case 0x91: fileTypeStr = "L"; return 4;
            case 0x92: fileTypeStr = "MG"; return 50;
            case 0x93: fileTypeStr = "PD"; return 46;
            case 0x94: fileTypeStr = "PLS"; return 12;
            default: fileTypeStr = "Undefined"; return 2;
        }
    }

    // =========================================================================
    // FRAME RECEIVED HANDLER
    // =========================================================================

    private void OnFrameReceived(object? sender, byte[] innerFrame)
    {
        if (innerFrame.Length < 6) return;

        ushort tns = (ushort)(innerFrame[4] | (innerFrame[5] << 8));
        _dataPackets[tns] = innerFrame;

        if (_responseEvents.TryGetValue(tns, out var ev))
            ev.Set();

        // Raise DataReceived if this is a reply (CMD > 31)
        if (innerFrame.Length > 2 && innerFrame[2] > 31)
        {
            // Truly unsolicited, not a reply to our own request
            if (!DisableEvent && !_responseEvents.ContainsKey(tns))
                DataReceived?.Invoke(this, EventArgs.Empty);
        }
        // Handle unsolicited message (CMD=0x0F, FUNC=0xAA)
        else if (innerFrame.Length > 6 && innerFrame[2] == 0x0F && innerFrame[6] == 0xAA)
        {
            // Unsolicited write: send a PCCC reply only for DF1 serial.
            // EtherNet/IP uses request-response; no explicit reply is needed.
            if (_currentTransport is DF1Transport)
            {
                int replyTns = innerFrame[5] * 256 + innerFrame[4];
                SendResponse(innerFrame[2] + 0x40, replyTns);
            }
            UnsolicitedMessageRcvd?.Invoke(this, EventArgs.Empty);
        }
    }

    private int SendResponse(int command, int rTNS)
    {
        if (_currentTransport == null) return -6;
        byte[] pdu = PacketBuilder.BuildCommandWithData(command, 0, Array.Empty<byte>(), (ushort)rTNS, MyNode, TargetNode, true);
        try
        {
            _currentTransport.SendFrame(pdu);
            return 0;
        }
        catch
        {
            return -6;
        }
    }

    // ─── IDisposable ─────────────────────────────────────────────────────────
    public void Dispose()
    {
        CloseComms();
    }
}
