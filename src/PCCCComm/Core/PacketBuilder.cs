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

namespace PCCCComm.Core;

/// <summary>
/// Factory for building DF1 application-layer PDUs (Protocol Data Units).
/// All methods allocate byte arrays with +1 extra byte (compatible with original VB code).
/// Does not perform DLE stuffing or checksum calculation.
/// Reference: Allen Bradley Publication 1770-6.5.16
/// </summary>
public static class PacketBuilder
{
    /// <summary>
    /// Builds a generic command packet (CMD, STS, TNS, FNC, data).
    /// Buffer allocation = (headerLen + dataLen) + 1 extra byte (compatible with original).
    /// </summary>
    public static byte[] BuildCommandWithData(int command, int func, byte[] data, ushort tns,
                                                int myNode, int targetNode, bool useDf1Protocol)
    {
        int dataLen = data.Length;
        int headerLen = useDf1Protocol ? 6 : 10;
        int totalLen = headerLen + dataLen;
        byte[] pkt = new byte[totalLen + 1]; // +1 as in original
        int pos = 0;

        if (useDf1Protocol)
        {
            // DF1: DST, SRC, CMD, STS, TNS_LOW, TNS_HIGH, FNC, data...
            pkt[pos++] = (byte)targetNode;
            pkt[pos++] = (byte)myNode;
            pkt[pos++] = (byte)command;
            pkt[pos++] = 0x00; // STS
            pkt[pos++] = (byte)(tns & 0xFF);
            pkt[pos++] = (byte)((tns >> 8) & 0xFF);
            pkt[pos++] = (byte)func;
        }
        else
        {
            // DH485 format
            pkt[pos++] = (byte)(targetNode + 0x80);
            pkt[pos++] = 0x88;
            pkt[pos++] = (byte)(myNode + 0x80);
            pkt[pos++] = 0x01;
            pkt[pos++] = 0x01;
            pkt[pos++] = (byte)(dataLen + 5);
            pkt[pos++] = (byte)command;
            pkt[pos++] = 0x00; // STS
            pkt[pos++] = (byte)(tns & 0xFF);
            pkt[pos++] = (byte)((tns >> 8) & 0xFF);
            pkt[pos++] = (byte)func;
        }

        if (dataLen > 0)
            Array.Copy(data, 0, pkt, pos, dataLen);

        return pkt;
    }

    /// <summary>
    /// Builds a Protected Write packet (CMD=0x0F) with the specified function.
    /// </summary>
    public static byte[] BuildProtectedWrite(int func, byte[] data, ushort tns,
                                                int myNode, int targetNode, bool useDf1Protocol)
    {
        return BuildCommandWithData(0x0F, func, data, tns, myNode, targetNode, useDf1Protocol);
    }

    /// <summary>
    /// Builds the body for a read command (0xA1 / 0xA2).
    /// Buffer allocation = (dataSize) + 1 extra byte (compatible with original).
    /// </summary>
    public static byte[] BuildReadRequestBody(DataAddress addr, int numberOfBytesToRead, out int function)
    {
        function = (addr.SubElement == 0) ? 0xA1 : 0xA2;
        int dataSize = (addr.SubElement == 0) ? 3 : 4;
        if (addr.Element >= 255) dataSize += 2;
        if (addr.SubElement >= 255) dataSize += 2;

        byte[] body = new byte[dataSize + 1]; // +1 as in original
        int idx = 0;

        body[idx++] = (byte)numberOfBytesToRead;
        body[idx++] = (byte)addr.FileNumber;
        body[idx++] = (byte)addr.FileType;

        // Element
        if (addr.Element < 255)
            body[idx++] = (byte)addr.Element;
        else
        {
            body[idx++] = 255;
            body[idx++] = (byte)(addr.Element & 0xFF);
            body[idx++] = (byte)((addr.Element >> 8) & 0xFF);
        }

        // Sub-element (only if function == 0xA2)
        if (function == 0xA2)
        {
            if (addr.SubElement < 255)
                body[idx++] = (byte)addr.SubElement;
            else
            {
                body[idx++] = 255;
                body[idx++] = (byte)(addr.SubElement & 0xFF);
                body[idx++] = (byte)((addr.SubElement >> 8) & 0xFF);
            }
        }

        return body;
    }

    /// <summary>
    /// Builds the body for a write command (0xAA word write / 0xAB bit write).
    /// Buffer allocation is dynamic based on extended addressing requirements.
    /// </summary>
    public static byte[] BuildWriteRequestBody(DataAddress addr, byte[] dataToWrite,
                                                int writeOffset, int bytesToWrite, out int function)
    {
        // Bit write (0xAB)
        if (addr.BitNumber >= 0 && addr.BitNumber < 16)
        {
            function = 0xAB;

            // Calculate dynamic size: base 8 bytes + extended addressing overhead
            int bodySize = 8;
            if (addr.Element >= 255) bodySize += 2;
            if (addr.SubElement >= 255) bodySize += 2;

            byte[] body = new byte[bodySize + 1]; // +1 for original VB compatibility
            int idx = 0;

            body[idx++] = (byte)bytesToWrite;
            body[idx++] = (byte)addr.FileNumber;
            body[idx++] = (byte)addr.FileType;

            // Element field (with extended addressing if needed)
            if (addr.Element < 255)
                body[idx++] = (byte)addr.Element;
            else
            {
                body[idx++] = 255;
                body[idx++] = (byte)(addr.Element & 0xFF);
                body[idx++] = (byte)((addr.Element >> 8) & 0xFF);
            }

            // Sub-element field (with extended addressing if needed)
            if (addr.SubElement < 255)
                body[idx++] = (byte)addr.SubElement;
            else
            {
                body[idx++] = 255;
                body[idx++] = (byte)(addr.SubElement & 0xFF);
                body[idx++] = (byte)((addr.SubElement >> 8) & 0xFF);
            }

            // Bit mask
            int bitMask = 1 << addr.BitNumber;
            body[idx++] = (byte)(bitMask & 0xFF);
            body[idx++] = (byte)((bitMask >> 8) & 0xFF);

            // Value (set or clear)
            if (writeOffset < dataToWrite.Length && dataToWrite[writeOffset] != 0)
            {
                body[idx++] = (byte)(bitMask & 0xFF);
                body[idx++] = (byte)((bitMask >> 8) & 0xFF);
            }
            else
            {
                body[idx++] = 0;
                body[idx++] = 0;
            }

            return body;
        }
        else
        {
            // Word write (0xAA)
            function = 0xAA;

            // Calculate base size: 5 (size,file,type,element,subelement) + bytesToWrite
            int bodySize = 5 + bytesToWrite;
            if (addr.Element >= 255) bodySize += 2;
            if (addr.SubElement >= 255) bodySize += 2;

            byte[] body = new byte[bodySize + 1]; // +1 for original VB compatibility
            int idx = 0;

            body[idx++] = (byte)bytesToWrite;
            body[idx++] = (byte)addr.FileNumber;
            body[idx++] = (byte)addr.FileType;

            // Element field
            if (addr.Element < 255)
                body[idx++] = (byte)addr.Element;
            else
            {
                body[idx++] = 255;
                body[idx++] = (byte)(addr.Element & 0xFF);
                body[idx++] = (byte)((addr.Element >> 8) & 0xFF);
            }

            // Sub-element field
            if (addr.SubElement < 255)
                body[idx++] = (byte)addr.SubElement;
            else
            {
                body[idx++] = 255;
                body[idx++] = (byte)(addr.SubElement & 0xFF);
                body[idx++] = (byte)((addr.SubElement >> 8) & 0xFF);
            }

            // Data
            int copyLen = Math.Min(bytesToWrite, dataToWrite.Length - writeOffset);
            Array.Copy(dataToWrite, writeOffset, body, idx, copyLen);

            return body;
        }
    }

    /// <summary>
    /// Builds a complete read packet for DF1.
    /// </summary>
    public static byte[] BuildReadPacket(DataAddress addr, int numberOfBytesToRead, ushort tns,
                                            int myNode, int targetNode)
    {
        byte[] body = BuildReadRequestBody(addr, numberOfBytesToRead, out int func);
        return BuildCommandWithData(0x0F, func, body, tns, myNode, targetNode, true);
    }

    /// <summary>
    /// Builds a complete write packet for DF1.
    /// </summary>
    public static byte[] BuildWritePacket(DataAddress addr, byte[] dataToWrite,
                                            int writeOffset, int bytesToWrite, ushort tns,
                                            int myNode, int targetNode)
    {
        byte[] body = BuildWriteRequestBody(addr, dataToWrite, writeOffset, bytesToWrite, out int func);
        return BuildCommandWithData(0x0F, func, body, tns, myNode, targetNode, true);
    }

    /// <summary>
    /// Builds the body for a Read Modify Write command (CMD=0x0F, FNC=0x26).
    /// Each set contains: encoded address + AND mask (2 bytes LE) + OR mask (2 bytes LE).
    /// The PLC processes each set as: word = (word AND andMask) OR orMask.
    /// Maximum total data size is 243 bytes per AB spec (libpccc cmd_init_0f.c line 731).
    /// Compatibility: PLC-5, PLC-5/VME.
    /// </summary>
    public static byte[] BuildReadModifyWriteBody(DataAddress[] addresses,
                                                ushort[] andMasks,
                                                ushort[] orMasks)
    {
        if (addresses.Length == 0)
            throw new ArgumentException("Number of sets must be non-zero.");
        if (addresses.Length != andMasks.Length || addresses.Length != orMasks.Length)
            throw new ArgumentException("addresses, andMasks, and orMasks must have the same length.");

        var body = new List<byte>();
        for (int i = 0; i < addresses.Length; i++)
        {
            // Encode address: FileNumber, FileType, Element, SubElement
            // Uses same encoding as BuildReadRequestBody (SLC logical address format)
            body.Add((byte)addresses[i].FileNumber);
            body.Add((byte)addresses[i].FileType);

            if (addresses[i].Element < 255)
                body.Add((byte)addresses[i].Element);
            else
            {
                body.Add(0xFF);
                body.Add((byte)(addresses[i].Element & 0xFF));
                body.Add((byte)((addresses[i].Element >> 8) & 0xFF));
            }

            if (addresses[i].SubElement < 255)
                body.Add((byte)addresses[i].SubElement);
            else
            {
                body.Add(0xFF);
                body.Add((byte)(addresses[i].SubElement & 0xFF));
                body.Add((byte)((addresses[i].SubElement >> 8) & 0xFF));
            }

            // AND mask (little-endian)
            body.Add((byte)(andMasks[i] & 0xFF));
            body.Add((byte)((andMasks[i] >> 8) & 0xFF));

            // OR mask (little-endian)
            body.Add((byte)(orMasks[i] & 0xFF));
            body.Add((byte)((orMasks[i] >> 8) & 0xFF));

            // Per AB spec and libpccc: total data must not exceed 243 bytes
            if (body.Count > 243)
                throw new PCCCException($"ReadModifyWrite: set {i + 1} exceeded maximum command size of 243 bytes.");
        }

        return body.ToArray();
    }
}
