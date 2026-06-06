// SPDX-License-Identifier: GPL-3.0-or-later
// 
// PCCCEmulator - PCCC Engine and Transports for .NET
// Copyright (c) 2026 Ketut Kumajaya
// 
// Initial reference: DF1Comm.vb (Archie Jacobs); implementation substantially modified.
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
using System.IO.Ports;

/// <summary>
/// DF1 Full-Duplex transport implementation for PCCC emulator.
/// Handles DLE stuffing, ACK/NAK, CRC/BCC checksums, and ENQ polling.
/// 
/// This class implements the ILinkTransport interface and provides the
/// low-level DF1 framing over RS-232 serial communication (point-to-point).
/// 
/// FRAME FORMAT (both directions):
///   DLE STX (0x10 0x02) | DLE-stuffed inner payload | DLE ETX (0x10 0x03) | Checksum (1 or 2 bytes)
/// 
/// INNER PAYLOAD FORMAT:
///   DST (1 byte) | SRC (1 byte) | CMD (1 byte) | STS (1 byte) | TNS_LO (1 byte) | TNS_HI (1 byte) | [FUNC (1 byte)] | [DATA...]
/// 
/// TRANSPORT BEHAVIOR:
///   - Every valid frame must be acknowledged with ACK (DLE 0x06) before processing
///   - Invalid frames (checksum mismatch, malformed) trigger NAK (DLE 0x15)
///   - Standalone ENQ (DLE 0x05) is used for node presence detection (auto-configure)
///   - DLE byte (0x10) in payload must be doubled (0x10 0x10) for transparency
/// 
/// HIGH-PERFORMANCE OPTIMIZATIONS (inherited from base):
///   - Producer-Consumer pattern with System.Threading.Channels
///   - Circular buffer with head/tail pointers
///   - stackalloc for small frame operations
///   - Span-based frame building
///   - Static readonly frames for ACK/NAK
/// 
/// This class is the direct replacement for the original DF1Transport,
/// maintaining identical behavior.
/// </summary>
public class DF1FullDuplexTransport : DF1BaseTransport
{
    /// <summary>
    /// Initializes the DF1 full-duplex transport handler.
    /// </summary>
    /// <param name="emulator">Parent emulator instance (provides counters and logging)</param>
    /// <param name="portName">Serial port name (e.g., "COM2" or "/dev/ttyUSB0")</param>
    /// <param name="baudRate">Baud rate (e.g., 19200, 9600, 38400)</param>
    /// <param name="parity">Parity mode (None, Odd, Even)</param>
    public DF1FullDuplexTransport(PCCCEmulator emulator, string portName, int baudRate, Parity parity)
        : base(emulator, portName, baudRate, parity)
    {
    }

    /// <summary>
    /// Sends a response PDU back to the client using DF1 framing.
    /// In full-duplex mode, the response is sent immediately.
    /// </summary>
    /// <param name="pdu">Inner frame PDU to send (without DLE framing)</param>
    /// <param name="clientContext">Unused in DF1 full-duplex (kept for interface compatibility)</param>
    public override void SendResponse(byte[] pdu, object clientContext)
    {
        byte[] frame = BuildRawFrame(pdu);
        LogRawFrame(pdu, frame, frame.Length);
        SendRawFrame(frame);
    }

    /// <summary>
    /// Parses complete DF1 frames from the circular buffer.
    /// Scans for DLE STX (0x10 0x02) and finds matching DLE ETX (0x10 0x03)
    /// while correctly handling stuffed DLE pairs (0x10 0x10) in the payload.
    /// 
    /// When a complete frame is found, it is extracted and passed to ProcessFrame().
    /// Invalid bytes that cannot start a frame are skipped to maintain synchronization.
    /// </summary>
    protected override void ParseBuffer()
    {
        int chkBytes = _checkSum == CheckSumOptions.Bcc ? 1 : 2;
        int scanPos = _rxHead;
        int remaining = _rxCount;

        while (remaining > 0)
        {
            // Need at least 2 bytes for any valid frame start (DLE STX or ENQ)
            if (remaining < 2) break;

            byte b1 = _rxBuffer[scanPos];
            byte b2 = _rxBuffer[(scanPos + 1) % _rxBuffer.Length];

            // Handle standalone ENQ (DLE 0x05) — RSLinx auto-configure node probe
            // ENQ is not a full frame, just a 2-byte sequence
            if (b1 == 0x10 && b2 == 0x05)
            {
                _rxHead = (_rxHead + 2) % _rxBuffer.Length;
                _rxCount -= 2;
                scanPos = _rxHead;
                remaining = _rxCount;
                HandleEnq();
                continue;
            }

            // Look for DLE STX frame start
            if (b1 == 0x10 && b2 == 0x02)
            {
                // Scan for DLE ETX, skipping over stuffed 0x10 0x10 pairs in the payload
                int tempPos = (scanPos + 2) % _rxBuffer.Length;
                int bytesScanned = 0;
                int payloadLen = -1;

                while (bytesScanned + 1 < remaining - 2) // -2 for DLE STX already consumed
                {
                    byte current = _rxBuffer[tempPos];
                    byte next = _rxBuffer[(tempPos + 1) % _rxBuffer.Length];

                    if (current == 0x10 && next == 0x10)
                    {
                        tempPos = (tempPos + 2) % _rxBuffer.Length;
                        bytesScanned += 2;
                        continue;
                    }
                    if (current == 0x10 && next == 0x03)
                    {
                        payloadLen = bytesScanned;
                        break;
                    }
                    tempPos = (tempPos + 1) % _rxBuffer.Length;
                    bytesScanned++;
                }

                if (payloadLen == -1) break; // Incomplete frame — wait for more bytes

                int frameLen = 2 + payloadLen + 2 + chkBytes;
                if (frameLen > remaining) break; // Checksum bytes not yet received

                // Extract frame bytes (linearize to contiguous array)
                byte[] frame = new byte[frameLen];
                int spaceToEnd = _rxBuffer.Length - scanPos;
                if (spaceToEnd >= frameLen)
                {
                    Array.Copy(_rxBuffer, scanPos, frame, 0, frameLen);
                }
                else
                {
                    Array.Copy(_rxBuffer, scanPos, frame, 0, spaceToEnd);
                    Array.Copy(_rxBuffer, 0, frame, spaceToEnd, frameLen - spaceToEnd);
                }

                _rxHead = (_rxHead + frameLen) % _rxBuffer.Length;
                _rxCount -= frameLen;
                scanPos = _rxHead;
                remaining = _rxCount;

                ProcessFrame(frame);
            }
            else
            {
                // Skip single byte that cannot start a valid frame
                _rxHead = (_rxHead + 1) % _rxBuffer.Length;
                _rxCount--;
                scanPos = _rxHead;
                remaining = _rxCount;
            }
        }
    }

    /// <summary>
    /// Handles standalone ENQ (DLE 0x05) packets.
    /// ENQ is a node-presence probe sent by RSLinx during auto-configure.
    /// Reply with DLE ACK (0x10 0x06) to confirm this node is alive.
    /// </summary>
    private void HandleEnq()
    {
        _emulator.IncrementEnqReceived();
        SendAck();
    }

    /// <summary>
    /// Processes a complete DF1 frame.
    /// Steps:
    ///   1. Extract and validate checksum
    ///   2. Locate DLE ETX while handling stuffed DLE pairs
    ///   3. Unstuff the payload (remove duplicate 0x10 bytes)
    ///   4. Verify checksum matches calculated value
    ///   5. Send ACK before processing (required by DF1 full-duplex transport)
    ///   6. Raise PduReceived event for emulator to dispatch the command
    /// </summary>
    /// <param name="rawFrame">Complete DF1 frame including DLE STX, stuffed payload, DLE ETX, and checksum</param>
    private void ProcessFrame(byte[] rawFrame)
    {
        try
        {
            int chkBytes = _checkSum == CheckSumOptions.Bcc ? 1 : 2;
            if (rawFrame.Length < 6 + chkBytes) return;

            // Extract received checksum (little-endian for CRC)
            ushort receivedChk = chkBytes == 1
                ? rawFrame[rawFrame.Length - 1]
                : (ushort)(rawFrame[rawFrame.Length - 2] | (rawFrame[rawFrame.Length - 1] << 8));

            // Locate DLE ETX while skipping stuffed 0x10 0x10 pairs
            int etxPos = -1;
            for (int i = 2; i < rawFrame.Length - 1; i++)
            {
                if (rawFrame[i] == 0x10 && rawFrame[i + 1] == 0x10) { i++; continue; }
                if (rawFrame[i] == 0x10 && rawFrame[i + 1] == 0x03) { etxPos = i; break; }
            }
            if (etxPos == -1) return;

            int payloadLen = etxPos - 2;
            if (payloadLen <= 0) return;

            // Oversized frame protection (SLC 5/03 max payload is 244 bytes)
            // DF1 spec: max payload 244 bytes, stuffed worst case ×2 = 488
            // Using 512 as safe upper bound
            if (payloadLen > 512)
            {
                Logger.Info(this, $"Oversized DF1 frame rejected: payloadLen={payloadLen}");
                _emulator.IncrementBadPacketsDetected();
                _emulator.IncrementUndeliveredPackets();
                _emulator.IncrementNoBufferNakd();
                SendNak();
                return;
            }

            // Unstuff directly from rawFrame to stackalloc — zero heap allocation
            Span<byte> unstuffed = stackalloc byte[payloadLen];
            int unstuffedLen = MessageDecoder.RemoveDleStuffing(rawFrame.AsSpan(2, payloadLen), unstuffed);
            unstuffed = unstuffed[..unstuffedLen];

            if (unstuffedLen < 6) return;

            // Verify checksum over the unstuffed payload only
            ushort calc = MessageDecoder.CalculateChecksum(unstuffed, _checkSum);
            if (calc != receivedChk)
            {
                Logger.Info(this, $"Checksum mismatch: calc=0x{calc:X4} recv=0x{receivedChk:X4} ({_checkSum})");
                _emulator.IncrementBadPacketsDetected();
                _emulator.IncrementUndeliveredPackets();
                _emulator.IncrementNoBufferNakd();
                SendNak();
                return;
            }

            // Valid packet received
            _emulator.IncrementTotalPacketsReceived();

            int dst = unstuffed[0];
            if (dst != _myNode && dst != 0xFF) return;

            // ACK before responding — required by DF1 full-duplex transport
            SendAck();

            // Build PDU and raise event for emulator to dispatch
            byte[] pdu = new byte[unstuffedLen];
            unstuffed.CopyTo(pdu);
            LogReceivedFrame(rawFrame, pdu);
            RaisePduReceived(pdu, this);
        }
        catch (Exception ex) 
        { 
            Logger.Warn(this, "ProcessFrame error: " + ex.Message); 
        }
    }
}
