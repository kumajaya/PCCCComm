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

using System;
using System.Collections.Generic;

namespace PCCCComm.Pccc
{
    /// <summary>
    /// Represents a PCCC message (application layer PDU).
    /// </summary>
    public class PCCCMessage
    {
        public byte Dst { get; set; }
        public byte Src { get; set; }
        public byte Cmd { get; set; }
        public byte Sts { get; set; }
        public ushort Tns { get; set; }
        public byte? Fnc { get; set; }
        public byte[] Data { get; set; }

        public PCCCMessage()
        {
            Data = Array.Empty<byte>();
        }

        public PCCCMessage(byte dst, byte src, byte cmd, byte sts, ushort tns, byte? fnc, byte[] data)
        {
            Dst = dst;
            Src = src;
            Cmd = cmd;
            Sts = sts;
            Tns = tns;
            Fnc = fnc;
            Data = data ?? Array.Empty<byte>();
        }

        public byte[] ToBytes()
        {
            int dataLen = Data.Length;
            int totalLen = 6 + (Fnc.HasValue ? 1 : 0) + dataLen;
            var bytes = new byte[totalLen];
            int idx = 0;

            bytes[idx++] = Dst;
            bytes[idx++] = Src;
            bytes[idx++] = Cmd;
            bytes[idx++] = Sts;
            bytes[idx++] = (byte)(Tns & 0xFF);
            bytes[idx++] = (byte)((Tns >> 8) & 0xFF);
            if (Fnc.HasValue)
                bytes[idx++] = Fnc.Value;
            if (dataLen > 0)
                Array.Copy(Data, 0, bytes, idx, dataLen);

            return bytes;
        }

        public static PCCCMessage FromBytes(byte[] rawFrame, bool hasFnc = false)
        {
            if (rawFrame == null || rawFrame.Length < 6)
                throw new ArgumentException("Raw frame too short", nameof(rawFrame));

            int idx = 0;
            byte dst = rawFrame[idx++];
            byte src = rawFrame[idx++];
            byte cmd = rawFrame[idx++];
            byte sts = rawFrame[idx++];
            ushort tns = (ushort)(rawFrame[idx++] | (rawFrame[idx++] << 8));
            byte? fnc = null;
            if (hasFnc && idx < rawFrame.Length)
                fnc = rawFrame[idx++];
            byte[] data = new byte[rawFrame.Length - idx];
            Array.Copy(rawFrame, idx, data, 0, data.Length);

            return new PCCCMessage(dst, src, cmd, sts, tns, fnc, data);
        }

        // ========================================================================
        // Address encoding helpers (formerly PCCCAddressCodec)
        // ========================================================================

        private static byte[] EncodeReadBody(DataAddress addr, int numberOfBytesToRead, out int function)
        {
            function = (addr.SubElement == 0) ? PCCCConstants.Fnc.ReadWordRange : PCCCConstants.Fnc.ReadSubElement;
            int dataSize = (addr.SubElement == 0) ? 3 : 4;
            if (addr.Element >= 255) dataSize += 2;
            if (addr.SubElement >= 255) dataSize += 2;

            byte[] body = new byte[dataSize + 1];
            int idx = 0;

            body[idx++] = (byte)numberOfBytesToRead;
            body[idx++] = (byte)addr.FileNumber;
            body[idx++] = (byte)addr.FileType;

            if (addr.Element < 255)
                body[idx++] = (byte)addr.Element;
            else
            {
                body[idx++] = 255;
                body[idx++] = (byte)(addr.Element & 0xFF);
                body[idx++] = (byte)((addr.Element >> 8) & 0xFF);
            }

            if (function == PCCCConstants.Fnc.ReadSubElement)
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

        private static byte[] EncodeWriteBody(DataAddress addr, byte[] dataToWrite, int writeOffset, int bytesToWrite, out int function)
        {
            if (addr.BitNumber >= 0 && addr.BitNumber < 16)
            {
                function = PCCCConstants.Fnc.WriteBit;
                int bodySize = 8;
                if (addr.Element >= 255) bodySize += 2;
                if (addr.SubElement >= 255) bodySize += 2;

                byte[] body = new byte[bodySize + 1];
                int idx = 0;

                body[idx++] = (byte)bytesToWrite;
                body[idx++] = (byte)addr.FileNumber;
                body[idx++] = (byte)addr.FileType;

                if (addr.Element < 255)
                    body[idx++] = (byte)addr.Element;
                else
                {
                    body[idx++] = 255;
                    body[idx++] = (byte)(addr.Element & 0xFF);
                    body[idx++] = (byte)((addr.Element >> 8) & 0xFF);
                }

                if (addr.SubElement < 255)
                    body[idx++] = (byte)addr.SubElement;
                else
                {
                    body[idx++] = 255;
                    body[idx++] = (byte)(addr.SubElement & 0xFF);
                    body[idx++] = (byte)((addr.SubElement >> 8) & 0xFF);
                }

                int bitMask = 1 << addr.BitNumber;
                body[idx++] = (byte)(bitMask & 0xFF);
                body[idx++] = (byte)((bitMask >> 8) & 0xFF);

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
                function = PCCCConstants.Fnc.WriteWordRange;
                int bodySize = 5 + bytesToWrite;
                if (addr.Element >= 255) bodySize += 2;
                if (addr.SubElement >= 255) bodySize += 2;

                byte[] body = new byte[bodySize + 1];
                int idx = 0;

                body[idx++] = (byte)bytesToWrite;
                body[idx++] = (byte)addr.FileNumber;
                body[idx++] = (byte)addr.FileType;

                if (addr.Element < 255)
                    body[idx++] = (byte)addr.Element;
                else
                {
                    body[idx++] = 255;
                    body[idx++] = (byte)(addr.Element & 0xFF);
                    body[idx++] = (byte)((addr.Element >> 8) & 0xFF);
                }

                if (addr.SubElement < 255)
                    body[idx++] = (byte)addr.SubElement;
                else
                {
                    body[idx++] = 255;
                    body[idx++] = (byte)(addr.SubElement & 0xFF);
                    body[idx++] = (byte)((addr.SubElement >> 8) & 0xFF);
                }

                int copyLen = Math.Min(bytesToWrite, dataToWrite.Length - writeOffset);
                Array.Copy(dataToWrite, writeOffset, body, idx, copyLen);

                return body;
            }
        }

        // (Optional) Keep PLC-5 and SLC specific encoders if needed elsewhere, but currently unused.
        // They can remain private static or be removed. For now, we'll keep them commented.
        /*
        private static byte[] EncodeForPlc5(DataAddress addr) { ... }
        private static byte[] EncodeForSlc(DataAddress addr) { ... }
        */

        // ========================================================================
        // Factory methods (using the encoding helpers)
        // ========================================================================

        public static PCCCMessage CreateReadRequest(DataAddress addr, int bytesToRead, ushort tns, byte myNode, byte targetNode)
        {
            byte[] body = EncodeReadBody(addr, bytesToRead, out int fnc);
            return new PCCCMessage(targetNode, myNode, PCCCConstants.Cmd.ProtectedWrite, 0, tns, (byte)fnc, body);
        }

        public static PCCCMessage CreateWriteRequest(DataAddress addr, byte[] dataToWrite, int writeOffset, int bytesToWrite, ushort tns, byte myNode, byte targetNode)
        {
            byte[] body = EncodeWriteBody(addr, dataToWrite, writeOffset, bytesToWrite, out int fnc);
            return new PCCCMessage(targetNode, myNode, PCCCConstants.Cmd.ProtectedWrite, 0, tns, (byte)fnc, body);
        }

        public static PCCCMessage CreateReadModifyWriteRequest(DataAddress[] addrs, ushort[] andMasks, ushort[] orMasks, ushort tns, byte myNode, byte targetNode)
        {
            var body = new List<byte>();
            for (int i = 0; i < addrs.Length; i++)
            {
                var addr = addrs[i];
                body.Add((byte)addr.FileNumber);
                body.Add((byte)addr.FileType);

                // Element
                if (addr.Element < 255)
                    body.Add((byte)addr.Element);
                else
                {
                    body.Add(0xFF);
                    body.Add((byte)(addr.Element & 0xFF));
                    body.Add((byte)((addr.Element >> 8) & 0xFF));
                }

                // Sub-element
                int sub = addr.SubElement >= 0 ? addr.SubElement : 0;
                if (sub < 255)
                    body.Add((byte)sub);
                else
                {
                    body.Add(0xFF);
                    body.Add((byte)(sub & 0xFF));
                    body.Add((byte)((sub >> 8) & 0xFF));
                }

                // AND mask (little-endian)
                body.Add((byte)(andMasks[i] & 0xFF));
                body.Add((byte)((andMasks[i] >> 8) & 0xFF));
                // OR mask (little-endian)
                body.Add((byte)(orMasks[i] & 0xFF));
                body.Add((byte)((orMasks[i] >> 8) & 0xFF));

                // --- VALIDATION (moved from original PacketBuilder) ---
                // Maximum total data size for Read-Modify-Write is 243 bytes (AB spec, 1770-6.5.16)
                if (body.Count > PCCCConstants.Df1Limits.MaxReadModifyWriteBodyBytes)
                    throw new PCCCException($"ReadModifyWrite: set {i + 1} exceeded maximum command size of {PCCCConstants.Df1Limits.MaxReadModifyWriteBodyBytes} bytes.");
            }
            return new PCCCMessage(targetNode, myNode, PCCCConstants.Cmd.ProtectedWrite, 0, tns, PCCCConstants.Fnc.ReadModifyWrite, body.ToArray());
        }

        public static PCCCMessage CreateDiagnosticStatusRequest(ushort tns, byte myNode, byte targetNode)
        {
            return new PCCCMessage(targetNode, myNode, PCCCConstants.Cmd.DiagnosticStatus, 0, tns, PCCCConstants.Fnc.GetRunMode, Array.Empty<byte>());
        }

        public static PCCCMessage CreateChangeModeRequest(byte modeValue, bool isMicroLogix, ushort tns, byte myNode, byte targetNode)
        {
            byte fnc = isMicroLogix ? PCCCConstants.Fnc.SetRunModeML : PCCCConstants.Fnc.SetRunModeSLC;
            return new PCCCMessage(targetNode, myNode, PCCCConstants.Cmd.ProtectedWrite, 0, tns, fnc, new byte[] { modeValue });
        }

        public static PCCCMessage CreateDisableForcesRequest(ushort tns, byte myNode, byte targetNode)
        {
            return new PCCCMessage(targetNode, myNode, PCCCConstants.Cmd.ProtectedWrite, 0, tns, PCCCConstants.Fnc.DisableForces, Array.Empty<byte>());
        }
    }
}
