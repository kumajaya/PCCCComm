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

        /// <summary>Creates an Enable Forces request (0x0F/0x42).</summary>
        public static PCCCMessage CreateEnableForcesRequest(ushort tns, byte myNode, byte targetNode)
        {
            return new PCCCMessage(targetNode, myNode, PCCCConstants.Cmd.ProtectedWrite, 0, tns,
                PCCCConstants.Fnc.EnableForces, Array.Empty<byte>());
        }

        /// <summary>Creates a Clear Forces request (0x0F/0x43).</summary>
        public static PCCCMessage CreateClearForcesRequest(ushort tns, byte myNode, byte targetNode)
        {
            return new PCCCMessage(targetNode, myNode, PCCCConstants.Cmd.ProtectedWrite, 0, tns,
                PCCCConstants.Fnc.ClearForces, Array.Empty<byte>());
        }

        // ========================================================================
        // Factory methods for new commands
        // ========================================================================

        /// <summary>Creates an Open File request (0x0F/0x81).</summary>
        public static PCCCMessage CreateOpenFileRequest(byte fileNumber, byte fileType, ushort tns, byte myNode, byte targetNode)
        {
            return new PCCCMessage(targetNode, myNode, PCCCConstants.Cmd.ProtectedWrite, 0, tns,
                PCCCConstants.Fnc.OpenFile, new byte[] { fileNumber, fileType });
        }

        /// <summary>Creates a Close File request (0x0F/0x82).</summary>
        public static PCCCMessage CreateCloseFileRequest(ushort tag, ushort tns, byte myNode, byte targetNode)
        {
            return new PCCCMessage(targetNode, myNode, PCCCConstants.Cmd.ProtectedWrite, 0, tns,
                PCCCConstants.Fnc.CloseFile, new byte[] { (byte)(tag & 0xFF), (byte)((tag >> 8) & 0xFF) });
        }

        /// <summary>Creates a File Read request (0x0F/0xA7).</summary>
        public static PCCCMessage CreateFileReadRequest(ushort tag, int offset, int bytesToRead, ushort tns, byte myNode, byte targetNode)
        {
            byte[] body = new byte[5];
            body[0] = (byte)(tag & 0xFF);
            body[1] = (byte)((tag >> 8) & 0xFF);
            body[2] = (byte)(offset & 0xFF);
            body[3] = (byte)((offset >> 8) & 0xFF);
            body[4] = (byte)bytesToRead;
            return new PCCCMessage(targetNode, myNode, PCCCConstants.Cmd.ProtectedWrite, 0, tns,
                PCCCConstants.Fnc.FileRead, body);
        }

        /// <summary>Creates a File Write request (0x0F/0xAF).</summary>
        public static PCCCMessage CreateFileWriteRequest(ushort tag, int offset, byte[] data, ushort tns, byte myNode, byte targetNode)
        {
            byte[] body = new byte[5 + data.Length];
            body[0] = (byte)(tag & 0xFF);
            body[1] = (byte)((tag >> 8) & 0xFF);
            body[2] = (byte)(offset & 0xFF);
            body[3] = (byte)((offset >> 8) & 0xFF);
            body[4] = (byte)data.Length;
            Array.Copy(data, 0, body, 5, data.Length);
            return new PCCCMessage(targetNode, myNode, PCCCConstants.Cmd.ProtectedWrite, 0, tns,
                PCCCConstants.Fnc.FileWrite, body);
        }

        /// <summary>Creates an Upload All Request (0x0F/0x53).</summary>
        public static PCCCMessage CreateUploadAllRequest(ushort tns, byte myNode, byte targetNode)
        {
            return new PCCCMessage(targetNode, myNode, PCCCConstants.Cmd.ProtectedWrite, 0, tns,
                PCCCConstants.Fnc.UploadAllRequest, Array.Empty<byte>());
        }

        /// <summary>Creates an Upload Completed request (0x0F/0x55).</summary>
        public static PCCCMessage CreateUploadCompletedRequest(ushort tns, byte myNode, byte targetNode)
        {
            return new PCCCMessage(targetNode, myNode, PCCCConstants.Cmd.ProtectedWrite, 0, tns,
                PCCCConstants.Fnc.UploadCompleted, Array.Empty<byte>());
        }

        /// <summary>Creates a Download All Request (0x0F/0x50).</summary>
        public static PCCCMessage CreateDownloadAllRequest(ushort tns, byte myNode, byte targetNode)
        {
            return new PCCCMessage(targetNode, myNode, PCCCConstants.Cmd.ProtectedWrite, 0, tns,
                PCCCConstants.Fnc.DownloadAllRequest, Array.Empty<byte>());
        }

        /// <summary>Creates a Download Completed request (0x0F/0x52).</summary>
        public static PCCCMessage CreateDownloadCompletedRequest(ushort tns, byte myNode, byte targetNode)
        {
            return new PCCCMessage(targetNode, myNode, PCCCConstants.Cmd.ProtectedWrite, 0, tns,
                PCCCConstants.Fnc.DownloadCompleted, Array.Empty<byte>());
        }

        /// <summary>Creates a Get Edit Resource request (0x0F/0x11).</summary>
        public static PCCCMessage CreateGetEditResourceRequest(ushort tns, byte myNode, byte targetNode)
        {
            return new PCCCMessage(targetNode, myNode, PCCCConstants.Cmd.ProtectedWrite, 0, tns,
                PCCCConstants.Fnc.SecureAccess, Array.Empty<byte>());
        }

        /// <summary>Creates a Return Edit Resource request (0x0F/0x12).</summary>
        public static PCCCMessage CreateReturnEditResourceRequest(ushort tns, byte myNode, byte targetNode)
        {
            return new PCCCMessage(targetNode, myNode, PCCCConstants.Cmd.ProtectedWrite, 0, tns,
                PCCCConstants.Fnc.ReleaseAccess, Array.Empty<byte>());
        }

        /// <summary>Creates an Apply Port Configuration request (0x0F/0x8F).</summary>
        public static PCCCMessage CreateApplyPortConfigRequest(ushort tns, byte myNode, byte targetNode)
        {
            // Four unused bytes as placeholder
            return new PCCCMessage(targetNode, myNode, PCCCConstants.Cmd.ProtectedWrite, 0, tns,
                PCCCConstants.Fnc.ApplyPortConfig, new byte[] { 0, 0, 0, 0 });
        }

        /// <summary>Creates an Initialize Memory request (0x0F/0x57).</summary>
        public static PCCCMessage CreateInitializeMemoryRequest(ushort tns, byte myNode, byte targetNode)
        {
            return new PCCCMessage(targetNode, myNode, PCCCConstants.Cmd.ProtectedWrite, 0, tns,
                PCCCConstants.Fnc.InitializeMemory, Array.Empty<byte>());
        }

        /// <summary>Creates a Read Diagnostic Counters request (0x06/0x01).</summary>
        public static PCCCMessage CreateReadDiagnosticCountersRequest(ushort tns, byte myNode, byte targetNode)
        {
            return new PCCCMessage(targetNode, myNode, PCCCConstants.Cmd.DiagnosticStatus, 0, tns,
                PCCCConstants.DiagnosticFnc.ReadCounters, new byte[] { 0, 0 });
        }

        /// <summary>Creates a Reset Diagnostic Counters request (0x06/0x07).</summary>
        public static PCCCMessage CreateResetDiagnosticCountersRequest(ushort tns, byte myNode, byte targetNode)
        {
            return new PCCCMessage(targetNode, myNode, PCCCConstants.Cmd.DiagnosticStatus, 0, tns,
                PCCCConstants.DiagnosticFnc.ResetCounters, new byte[] { 0, 0 });
        }

        /// <summary>Creates a Read Link Parameters request (0x06/0x09).</summary>
        public static PCCCMessage CreateReadLinkParamsRequest(ushort tns, byte myNode, byte targetNode)
        {
            return new PCCCMessage(targetNode, myNode, PCCCConstants.Cmd.DiagnosticStatus, 0, tns,
                PCCCConstants.DiagnosticFnc.ReadLinkParams, Array.Empty<byte>());
        }

        /// <summary>Creates a Set Link Parameters request (0x06/0x0A).</summary>
        public static PCCCMessage CreateSetLinkParamsRequest(byte maxAddress, ushort tns, byte myNode, byte targetNode)
        {
            return new PCCCMessage(targetNode, myNode, PCCCConstants.Cmd.DiagnosticStatus, 0, tns,
                PCCCConstants.DiagnosticFnc.SetLinkParams, new byte[] { maxAddress });
        }

        /// <summary>
        /// Creates an Echo request using CMD=0x06 (Diagnostic Status) with FNC=0x00.
        /// </summary>
        public static PCCCMessage CreateEchoRequest(byte[] data, ushort tns, byte myNode, byte targetNode)
        {
            return new PCCCMessage(targetNode, myNode, PCCCConstants.Cmd.DiagnosticStatus, 0, tns,
                PCCCConstants.Fnc.Echo, data ?? Array.Empty<byte>());
        }

        /// <summary>
        /// Creates a Typed Read request for PLC-5 (CMD=0x0F, FNC=0x68).
        /// </summary>
        /// <param name="logicalAddress">Encoded logical binary address (mask + levels).</param>
        /// <param name="elementCount">Number of elements to read (each element = 1 byte for this type parameter).</param>
        /// <param name="tns">Transaction number (0 to auto-assign).</param>
        /// <param name="myNode">Source node address.</param>
        /// <param name="targetNode">Destination node address.</param>
        /// <returns>PCCCMessage ready to send.</returns>
        public static PCCCMessage CreateTypedReadRequest(byte[] logicalAddress, int elementCount,
            ushort tns, byte myNode, byte targetNode)
        {
            // Format per 1770-6.5.16 §7-28:
            // [PktOff 2B][TotTrans 2B][address var][Size(elements) 2B]
            List<byte> body = new List<byte>();
            // Packet Offset = 0 (2 bytes, little‑endian)
            body.Add((byte)(PCCCConstants.Df1Limits.TypedPacketOffsetZero & 0xFF));
            body.Add((byte)((PCCCConstants.Df1Limits.TypedPacketOffsetZero >> 8) & 0xFF));
            // Total Transaction = elementCount (used by emulator/PLC to allocate buffers)
            body.Add((byte)(elementCount & 0xFF));
            body.Add((byte)((elementCount >> 8) & 0xFF));
            // Logical binary address
            body.AddRange(logicalAddress);
            // Size = number of elements to read (again)
            body.Add((byte)(elementCount & 0xFF));
            body.Add((byte)((elementCount >> 8) & 0xFF));
            
            return new PCCCMessage(targetNode, myNode, PCCCConstants.Cmd.ProtectedWrite, 0, tns,
                PCCCConstants.Fnc.TypedRead, body.ToArray());
        }

        /// <summary>
        /// Creates a Typed Write request for PLC-5 (CMD=0x0F, FNC=0x67).
        /// </summary>
        /// <param name="logicalAddress">Encoded logical binary address (mask + levels).</param>
        /// <param name="data">Raw data bytes to write (must be aligned to element boundaries).</param>
        /// <param name="elementCount">Number of elements to write (data.Length / bytesPerElement).</param>
        /// <param name="tns">Transaction number (0 to auto-assign).</param>
        /// <param name="myNode">Source node address.</param>
        /// <param name="targetNode">Destination node address.</param>
        /// <returns>PCCCMessage ready to send.</returns>
        public static PCCCMessage CreateTypedWriteRequest(byte[] logicalAddress, byte[] data,
            int elementCount, ushort tns, byte myNode, byte targetNode)
        {
            // Format per 1770-6.5.16 §7-30:
            // [PktOff 2B][TotTrans 2B][address var][typeDataParam][data]
            List<byte> body = new List<byte>();
            // Packet Offset = 0
            body.Add((byte)(PCCCConstants.Df1Limits.TypedPacketOffsetZero & 0xFF));
            body.Add((byte)((PCCCConstants.Df1Limits.TypedPacketOffsetZero >> 8) & 0xFF));
            // Total Transaction = elementCount (used for buffer allocation)
            body.Add((byte)(elementCount & 0xFF));
            body.Add((byte)((elementCount >> 8) & 0xFF));
            // Logical binary address
            body.AddRange(logicalAddress);
            // Type/Data Parameter (0x31 = byte array, 1 byte per element)
            body.Add(PCCCConstants.Df1Limits.TypedTypeDataParamByteArray);
            // Data bytes (must be aligned to element boundaries)
            body.AddRange(data);
            
            return new PCCCMessage(targetNode, myNode, PCCCConstants.Cmd.ProtectedWrite, 0, tns,
                PCCCConstants.Fnc.TypedWrite, body.ToArray());
        }

        /// <summary>
        /// Creates a Word Range Read request for PLC-5 (CMD=0x0F, FNC=0x01).
        /// </summary>
        public static PCCCMessage CreateWordRangeReadRequest(byte[] logicalAddress, int wordOffset, int sizeWords,
            ushort tns, byte myNode, byte targetNode)
        {
            var body = new List<byte>();
            body.Add((byte)(wordOffset & 0xFF));
            body.Add((byte)((wordOffset >> 8) & 0xFF));
            body.Add(0x00); body.Add(0x00); // totalTrans – ignored
            body.AddRange(logicalAddress);
            body.Add((byte)(sizeWords & 0xFF));
            body.Add((byte)((sizeWords >> 8) & 0xFF));

            return new PCCCMessage(targetNode, myNode, PCCCConstants.Cmd.ProtectedWrite, 0, tns,
                PCCCConstants.Fnc.WordRangeRead, body.ToArray());
        }

        /// <summary>
        /// Creates a Word Range Write request for PLC-5 (CMD=0x0F, FNC=0x00).
        /// </summary>
        public static PCCCMessage CreateWordRangeWriteRequest(byte[] logicalAddress, int wordOffset, byte[] data,
            ushort tns, byte myNode, byte targetNode)
        {
            int sizeWords = data.Length / 2;
            var body = new List<byte>();
            body.Add((byte)(wordOffset & 0xFF));
            body.Add((byte)((wordOffset >> 8) & 0xFF));
            body.Add(0x00); body.Add(0x00);
            body.AddRange(logicalAddress);
            body.Add((byte)(sizeWords & 0xFF));
            body.Add((byte)((sizeWords >> 8) & 0xFF));
            body.AddRange(data);

            return new PCCCMessage(targetNode, myNode, PCCCConstants.Cmd.ProtectedWrite, 0, tns,
                PCCCConstants.Fnc.WordRangeWrite, body.ToArray());
        }
    }
}
