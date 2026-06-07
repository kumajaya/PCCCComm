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
using System.Threading;

/// <summary>
/// DF1 Half-Duplex transport implementation for PCCC emulator (RS-485 multi-drop).
/// 
/// This class implements a passive slave that only responds when polled by the master.
/// It is designed for RS-485 networks where multiple slaves share a single pair of wires.
/// 
/// PROTOCOL DIFFERENCES FROM FULL-DUPLEX:
///   - Slave never initiates transmission; it waits for a poll (DLE ENQ + address).
///   - Responses are queued and sent only when the master polls this node's address.
///   - After receiving a valid command frame, the slave sends DLE ACK immediately.
///   - During polling, the slave sends DLE NAK if response not ready, or the response data frame when ready.
///   - Address filtering: polls not matching _myNode are silently ignored.
///   - RTS control may be required for RS-485 transceivers without auto-direction.
/// 
/// FRAME FORMAT (same as full-duplex, but transmission timing is different):
///   Master sends: DLE ENQ (0x10 0x05) <address>
///   Slave responds (if address matches): DLE STX ... DLE ETX ... checksum
/// 
/// HIGH-PERFORMANCE OPTIMIZATIONS (inherited from base):
///   - Producer-Consumer pattern with System.Threading.Channels
///   - Circular buffer with head/tail pointers
///   - stackalloc for small frame operations
///   - Span-based frame building
///   - Static readonly frames for ACK/NAK
/// </summary>
public class DF1HalfDuplexTransport : DF1BaseTransport
{
    // ─── RS-485 Direction Control ─────────────────────────────────────────
    /// <summary>
    /// Determines how the RS-485 driver is enabled before transmission.
    /// </summary>
    public enum Rs485ControlMode
    {
        /// <summary>No special control – assumes hardware auto-direction.</summary>
        Auto,
        /// <summary>Use RTS pin to enable driver (set high before write, low after).</summary>
        Rts,
        /// <summary>Use DTR pin to enable driver.</summary>
        Dtr
    }

    private Rs485ControlMode _rs485Mode = Rs485ControlMode.Auto;
    private int _rtsAssertDelayMs = 1;      // Delay after enabling driver
    private int _rtsDeassertDelayMs = 5;    // Increased for safety margin

    /// <summary>
    /// Gets or sets the RS-485 direction control mode.
    /// Default is Auto (assumes hardware auto-direction).
    /// </summary>
    public Rs485ControlMode Rs485Mode
    {
        get => _rs485Mode;
        set => _rs485Mode = value;
    }

    /// <summary>
    /// Delay in milliseconds after asserting RTS/DTR before writing data.
    /// Adjust for slow transceivers (typical 1-5 ms).
    /// </summary>
    public int RtsAssertDelayMs
    {
        get => _rtsAssertDelayMs;
        set => _rtsAssertDelayMs = Math.Max(0, value);
    }

    /// <summary>
    /// Delay in milliseconds after writing data before deasserting RTS/DTR.
    /// Ensures the last byte is fully transmitted.
    /// </summary>
    public int RtsDeassertDelayMs
    {
        get => _rtsDeassertDelayMs;
        set => _rtsDeassertDelayMs = Math.Max(0, value);
    }

    // ─── Pending Response Queue ───────────────────────────────────────────
    // DF1 half-duplex slave can hold at most one pending response per poll cycle.
    private byte[]? _pendingResponse;
    private readonly object _pendingLock = new object();

    // ─── Constructor ───────────────────────────────────────────────────────
    /// <summary>
    /// Initializes the DF1 half-duplex transport handler.
    /// </summary>
    /// <param name="emulator">Parent emulator instance.</param>
    /// <param name="portName">Serial port name.</param>
    /// <param name="baudRate">Baud rate (typically 9600, 19200, 38400).</param>
    /// <param name="parity">Parity (usually None for DF1).</param>
    public DF1HalfDuplexTransport(PCCCEmulator emulator, string portName, int baudRate, Parity parity)
        : base(emulator, portName, baudRate, parity)
    {
    }

    /// <summary>
    /// Human-readable name for this transport variant.
    /// </summary>
    public override string Name => "DF1HD";

    // ─── ILinkTransport Implementation ─────────────────────────────────────
    /// <summary>
    /// Stores a response PDU to be sent when the master polls this node.
    /// In half-duplex mode, the response is not sent immediately; it is queued.
    /// </summary>
    /// <param name="pdu">Inner frame PDU to send (without DLE framing).</param>
    /// <param name="clientContext">Unused.</param>
    public override void SendResponse(byte[] pdu, object clientContext)
    {
        byte[] frame = BuildRawFrame(pdu);
        // Do NOT log "TX" here because the frame is not yet sent.
        // Logging will happen in SendWithDirectionControl when actually transmitted.
        // Optionally log a "QUEUED" message for debugging:
        Logger.Info(this, "Response queued (pending poll)");

        lock (_pendingLock)
        {
            if (_pendingResponse != null)
            {
                Logger.Warn(this, "Pending response overwritten — previous frame dropped");
                _emulator.IncrementUndeliveredPackets();
            }
            _pendingResponse = frame;
        }
    }

    // ─── Parsing and Polling Detection ─────────────────────────────────────
    /// <summary>
    /// Parses the circular buffer, looking for:
    ///   1. Polling packets: DLE ENQ (0x10 0x05) followed by address byte.
    ///      If address matches _myNode, sends the pending response (or NAK if none).
    ///   2. Data frames: DLE STX ... DLE ETX ... (same as full-duplex),
    ///      but with immediate ACK after validation, and response queued for later poll.
    /// </summary>
    protected override void ParseBuffer()
    {
        int chkBytes = _checkSum == CheckSumOptions.Bcc ? 1 : 2;
        int scanPos = _rxHead;
        int remaining = _rxCount;

        while (remaining > 0)
        {
            if (remaining < 2) break;

            byte b1 = _rxBuffer[scanPos];
            byte b2 = _rxBuffer[(scanPos + 1) % _rxBuffer.Length];

            // ─── Polling Detection (DLE ENQ + address) ─────────────────────
            if (b1 == 0x10 && b2 == 0x05)
            {
                if (remaining < 3) break;  // wait address byte
                byte addr = _rxBuffer[(scanPos + 2) % _rxBuffer.Length];
                // Remove the 3-byte poll sequence from buffer
                _rxHead = (_rxHead + 3) % _rxBuffer.Length;
                _rxCount -= 3;
                scanPos = _rxHead;
                remaining = _rxCount;
                if (addr == _myNode) HandlePoll();
                // else: poll for another node – ignore silently
                continue;
            }

            // ─── Data Frame (DLE STX) ─────────────────────────────────────
            if (b1 == 0x10 && b2 == 0x02)
            {
                // Scan for DLE ETX (same logic as full-duplex)
                int tempPos = (scanPos + 2) % _rxBuffer.Length;
                int bytesScanned = 0;
                int payloadLen = -1;

                while (bytesScanned + 1 < remaining - 2)
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

                if (payloadLen == -1) break; // incomplete frame

                int frameLen = 2 + payloadLen + 2 + chkBytes;
                if (frameLen > remaining) break;

                // Extract frame (linearize)
                byte[] frame = new byte[frameLen];
                int spaceToEnd = _rxBuffer.Length - scanPos;
                if (spaceToEnd >= frameLen)
                    Array.Copy(_rxBuffer, scanPos, frame, 0, frameLen);
                else
                {
                    Array.Copy(_rxBuffer, scanPos, frame, 0, spaceToEnd);
                    Array.Copy(_rxBuffer, 0, frame, spaceToEnd, frameLen - spaceToEnd);
                }

                _rxHead = (_rxHead + frameLen) % _rxBuffer.Length;
                _rxCount -= frameLen;
                scanPos = _rxHead;
                remaining = _rxCount;

                Logger.Hex(this, "RX:", frame, frame.Length);
                ProcessDataFrame(frame);
                continue;
            }

            // ─── Unknown / out-of-sync byte ────────────────────────────────
            // Skip one byte to recover synchronization.
            _rxHead = (_rxHead + 1) % _rxBuffer.Length;
            _rxCount--;
            scanPos = _rxHead;
            remaining = _rxCount;
        }
    }

    // ─── Poll Handling ─────────────────────────────────────────────────────
    /// <summary>
    /// Called when the master polls this node's address.
    /// Sends the pending response if available; otherwise sends a NAK
    /// (DLE 0x15) to indicate not ready. This matches DF1 half-duplex spec §6.4.
    /// </summary>
    private void HandlePoll()
    {
        byte[]? response = null;
        lock (_pendingLock)
        {
            if (_pendingResponse != null)
            {
                response = _pendingResponse;
                _pendingResponse = null;
            }
        }

        if (response != null)
        {
            SendWithDirectionControl(response);
            Logger.Info(this, "Poll response: sent pending frame");
        }
        else
        {
            // Not ready yet – send NAK so master will re-poll (per spec §6.4)
            SendWithDirectionControl(NAK_FRAME);
            Logger.Info(this, "Poll response: NAK (response not ready)");
        }
    }

    // ─── Data Frame Processing (immediate ACK, then queue response) ────────
    /// <summary>
    /// Processes a complete DF1 data frame (DLE STX ... DLE ETX ... checksum).
    /// After validating the frame, sends an ACK immediately (link-layer acknowledgment),
    /// then queues the application response for later polling.
    /// 
    /// IMPORTANT: RaisePduReceived is synchronous. PCCCEmulator.DispatchCommand
    /// is called inline here and will call SendResponse() before this returns.
    /// _pendingResponse will be set before the next poll arrives (single consumer thread).
    /// </summary>
    /// <param name="rawFrame">Complete raw DF1 frame.</param>
    private void ProcessDataFrame(byte[] rawFrame)
    {
        try
        {
            int chkBytes = _checkSum == CheckSumOptions.Bcc ? 1 : 2;
            if (rawFrame.Length < 6 + chkBytes) return;

            ushort receivedChk = chkBytes == 1
                ? rawFrame[rawFrame.Length - 1]
                : (ushort)(rawFrame[rawFrame.Length - 2] | (rawFrame[rawFrame.Length - 1] << 8));

            // Locate DLE ETX
            int etxPos = -1;
            for (int i = 2; i < rawFrame.Length - 1; i++)
            {
                if (rawFrame[i] == 0x10 && rawFrame[i + 1] == 0x10) { i++; continue; }
                if (rawFrame[i] == 0x10 && rawFrame[i + 1] == 0x03) { etxPos = i; break; }
            }
            if (etxPos == -1) return;

            int payloadLen = etxPos - 2;
            if (payloadLen <= 0 || payloadLen > 512)
            {
                if (payloadLen > 512)
                    Logger.Info(this, $"Oversized DF1 frame rejected: payloadLen={payloadLen}");
                _emulator.IncrementBadPacketsDetected();
                _emulator.IncrementUndeliveredPackets();
                _emulator.IncrementNoBufferNakd();
                // In half-duplex, we cannot send NAK immediately; we must queue a NAK response
                // for the next poll. However, the simplest is to just drop the frame.
                // For simplicity, we do not queue NAK; master will time out.
                return;
            }

            Span<byte> unstuffed = stackalloc byte[payloadLen];
            int unstuffedLen = MessageDecoder.RemoveDleStuffing(rawFrame.AsSpan(2, payloadLen), unstuffed);
            unstuffed = unstuffed[..unstuffedLen];

            if (unstuffedLen < 6) return;

            ushort calc = MessageDecoder.CalculateChecksum(unstuffed, _checkSum);
            if (calc != receivedChk)
            {
                Logger.Info(this, $"Checksum mismatch: calc=0x{calc:X4} recv=0x{receivedChk:X4}");
                _emulator.IncrementBadPacketsDetected();
                _emulator.IncrementUndeliveredPackets();
                _emulator.IncrementNoBufferNakd();
                return;
            }

            _emulator.IncrementTotalPacketsReceived();

            int dst = unstuffed[0];
            if (dst != _myNode && dst != 0xFF) return;

            // --- Send ACK immediately after valid command frame (link-layer ack) ---
            // Master is waiting for this before it will begin polling (per spec §6.2)
            SendWithDirectionControl(ACK_FRAME);

            // Log the received frame
            byte[] pdu = new byte[unstuffedLen];
            unstuffed.CopyTo(pdu);
            LogReceivedFrame(rawFrame, pdu);

            // Raise event to emulator; the emulator will eventually call SendResponse
            // which will queue the response (not send immediately).
            // NOTE: This call is synchronous – DispatchCommand runs inline.
            RaisePduReceived(pdu, this);
        }
        catch (Exception ex)
        {
            Logger.Warn(this, "ProcessDataFrame error: " + ex.Message);
        }
    }

    // ─── RS-485 Transmission Helper (Unified) ──────────────────────────────
    /// <summary>
    /// Sends raw bytes with proper RS-485 direction control (if enabled).
    /// This method handles RTS/DTR assertion, delays, and logging.
    /// </summary>
    /// <param name="data">Raw data to send (e.g., ACK frame or response frame).</param>
    private void SendWithDirectionControl(byte[] data)
    {
        if (_rs485Mode == Rs485ControlMode.Auto)
        {
            // No special handling – direct write (but log as TX)
            SendRawFrame(data);   // already does logging
            return;
        }

        try
        {
            lock (_txLock)
            {
                // Enable driver
                if (_rs485Mode == Rs485ControlMode.Rts)
                    _port.RtsEnable = true;
                else if (_rs485Mode == Rs485ControlMode.Dtr)
                    _port.DtrEnable = true;

                if (_rtsAssertDelayMs > 0)
                    Thread.Sleep(_rtsAssertDelayMs);

                // Write data
                _port.Write(data, 0, data.Length);

                // Wait for transmission to finish before releasing driver
                // Calculate approximate transmit time in ms:
                //   bits per byte = start(1) + data(8) + parity(0 or 1) + stop(1)
                int bitsPerByte = 1 + 8 + (_port.Parity == Parity.None ? 0 : 1) + 1;
                int transmitTimeMs = (data.Length * bitsPerByte * 1000) / _port.BaudRate;
                int totalDelay = Math.Max(1, transmitTimeMs + _rtsDeassertDelayMs);
                Thread.Sleep(totalDelay);

                // Disable driver
                if (_rs485Mode == Rs485ControlMode.Rts)
                    _port.RtsEnable = false;
                else if (_rs485Mode == Rs485ControlMode.Dtr)
                    _port.DtrEnable = false;

                // Log hex (if enabled)
                Logger.Hex(this, "TX:", data, data.Length);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(this, $"SendWithDirectionControl error: {ex.Message}");
        }
    }

    // ─── Override Start to set initial RTS/DTR state ───────────────────────
    /// <summary>
    /// Overrides Start to set initial RTS/DTR state to receive mode (disabled).
    /// </summary>
    protected override void OnStart()
    {
        base.OnStart();
        if (_rs485Mode != Rs485ControlMode.Auto)
        {
            try
            {
                if (_rs485Mode == Rs485ControlMode.Rts)
                    _port.RtsEnable = false;
                else if (_rs485Mode == Rs485ControlMode.Dtr)
                    _port.DtrEnable = false;
            }
            catch { /* ignore */ }
        }
    }
}
