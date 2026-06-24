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
using System.IO.Ports;
using System.Threading;
using PCCCComm.Pccc;

namespace PCCCComm.Core;

/// <inheritdoc cref="ITransport"/>
/// <summary>
/// DF1 full‑duplex transport over RS‑232 (point‑to‑point master).
/// Handles DLE stuffing, CRC‑16/BCC, ACK/NAK, ENQ, and automatic backoff.
/// Reference: Allen Bradley Publication 1770-6.5.16, Chapters 5-7.
/// </summary>
public class DF1FullDuplexTransport : DF1BaseTransport
{
    // --- State for receive frame assembly ---
    private readonly object _rxLock = new object();
    private readonly RingBuffer _rxBuffer;
    private DateTime _frameStartTime = DateTime.MinValue;
    private const int FrameTimeoutMs = 500;      // Max time between DLE STX and DLE ETX
    private const int MaxBufferBytes = 4096;     // Safety limit

    // --- ACK/NAK signalling (SendFrame waits on this event) ---
    // ManualResetEventSlim replaces the previous 20 ms tick-polling loop:
    // the receive thread sets the event as soon as ACK or NAK arrives,
    // so SendFrame wakes up immediately rather than up to 20 ms late.
    private readonly ManualResetEventSlim _ackNakEvent = new ManualResetEventSlim(false);
    private volatile bool _ackReceived;
    private volatile bool _nakReceived;

    // --- ENQ signalling (SendEnqAndWaitForAck uses this event) ---
    private readonly ManualResetEventSlim _enqEvent = new ManualResetEventSlim(false);
    private volatile bool _ackFlagForEnq;
    private volatile bool _nakFlagForEnq;

    private bool _lastResponseWasNAK = false;

    /// <summary>
    /// Initialises the DF1 full‑duplex transport with a custom <see cref="ISerialPort"/>.
    /// </summary>
    public DF1FullDuplexTransport(ISerialPort port) : base(port)
    {
        _rxBuffer = new RingBuffer(MaxBufferBytes);
        _port.BytesReceived += OnBytesReceived;
    }

    /// <summary>
    /// Initialises the DF1 full‑duplex transport with standard serial port parameters.
    /// </summary>
    public DF1FullDuplexTransport(string portName, int baudRate, Parity parity)
        : base(portName, baudRate, parity)
    {
        _rxBuffer = new RingBuffer(MaxBufferBytes);
        _port.BytesReceived += OnBytesReceived;
    }

    /// <inheritdoc/>
    public override void SendFrame(byte[] innerFrame)
    {
        if (innerFrame == null || innerFrame.Length == 0)
            throw new ArgumentException("Inner frame cannot be null or empty.", nameof(innerFrame));

        // Build the complete wire frame using base helper
        byte[] frame = BuildWireFrame(innerFrame);
        OnRawFrameSent(frame);

        // Send with retry (max 2 attempts, as in original VB code)
        int retries = 0;
        const int maxRetries = 2;

        // Timeout in ms derived from MaxTicks (each tick was 20 ms in the old loop).
        int timeoutMs = MaxTicks * 20;

        while (retries < maxRetries)
        {
            _ackReceived = false;
            _nakReceived = false;
            _ackNakEvent.Reset();

            // SleepDelay is applied before writing (not after receiving ACK).
            // The original code slept inside the receive-thread lock after ACK — that
            // blocked the serial port reader. Sleeping here on the send thread is equivalent
            // for flow control purposes and does not block incoming bytes.
            if (SleepDelay > 0)
                Thread.Sleep(SleepDelay);

            _port.Write(frame, 0, frame.Length);

            // Wait for ACK or NAK — wakes immediately when the receive thread signals.
            _ackNakEvent.Wait(timeoutMs);

            if (_ackReceived)
                return;                     // Success

            if (_nakReceived)
            {
                // Backoff: increase sleep delay for the next retry
                if (SleepDelay < 400) SleepDelay += 50;
                retries++;
                continue;
            }

            // Timeout – no ACK/NAK received
            throw new TimeoutException("No ACK or NAK received within the specified timeout.");
        }

        throw new TimeoutException($"Failed to send frame after {maxRetries} retries.");
    }

    /// <summary>
    /// Sends a standalone ENQ (DLE 0x05) and waits for an ACK or NAK response.
    /// Used by the auto‑detect routine to test communication settings.
    /// </summary>
    /// <returns>0 if ACK received, -2 if NAK received, -3 if timeout.</returns>
    public int SendEnqAndWaitForAck()
    {
        if (!IsOpen)
            Open();

        _ackFlagForEnq = false;
        _nakFlagForEnq = false;
        _enqEvent.Reset();

        SendControl(ENQ);

        int timeoutMs = MaxTicks * 20;
        _enqEvent.Wait(timeoutMs);

        if (_ackFlagForEnq) return 0;
        if (_nakFlagForEnq) return -2;
        return -3;
    }

    /// <summary>
    /// Resets the ACK/NAK flags used by <see cref="SendEnqAndWaitForAck"/>.
    /// </summary>
    public void ResetAckNakFlags()
    {
        _ackFlagForEnq = false;
        _nakFlagForEnq = false;
    }

    // --- Serial receive handler (state machine) ---
    private void OnBytesReceived(object? sender, byte[] chunk)
    {
        if (chunk == null || chunk.Length == 0) return;

        byte[]? pduToDeliver = null;
        bool enqReceived = false;
        bool respondWithNak = false;

        lock (_rxLock)
        {
            _rxBuffer.AddRange(chunk, 0, chunk.Length);
            if (_rxBuffer.Count > MaxBufferBytes)
            {
                _rxBuffer.Clear();
                _frameStartTime = DateTime.MinValue;
                return;
            }

            bool consumed = true;
            while (consumed)
            {
                consumed = false;
                if (_rxBuffer.Count < 2) break;

                // Synchronisation: find a DLE byte
                if (_rxBuffer[0] != DLE)
                {
                    _rxBuffer.Advance(1);
                    consumed = true;
                    continue;
                }

                byte ctrl = _rxBuffer[1];

                // --- 2‑byte link control: ACK, NAK, ENQ ---
                if (ctrl == ACK || ctrl == NAK || ctrl == ENQ)
                {
                    _rxBuffer.Advance(2);
                    _frameStartTime = DateTime.MinValue;

                    if (ctrl == ACK)
                    {
                        // Set flag first, then signal — SendFrame checks the flag
                        // after Wait() returns, so order matters.
                        _ackReceived = true;
                        _ackFlagForEnq = true;
                        _ackNakEvent.Set();
                        _enqEvent.Set();
                    }
                    else if (ctrl == NAK)
                    {
                        _nakReceived = true;
                        _nakFlagForEnq = true;
                        _lastResponseWasNAK = true;
                        _ackNakEvent.Set();
                        _enqEvent.Set();
                    }
                    else if (ctrl == ENQ)
                    {
                        enqReceived = true;
                    }
                    consumed = true;
                    continue;
                }

                // --- Data frame: DLE STX ... DLE ETX ---
                if (ctrl == STX)
                {
                    if (_frameStartTime == DateTime.MinValue)
                        _frameStartTime = DateTime.UtcNow;

                    // Frame timeout check (prevents hanging on partial frames)
                    if ((DateTime.UtcNow - _frameStartTime).TotalMilliseconds > FrameTimeoutMs)
                    {
                        _rxBuffer.Advance(2);
                        _frameStartTime = DateTime.MinValue;
                        consumed = true;
                        continue;
                    }

                    // Find DLE ETX, skipping over stuffed DLE pairs (0x10 0x10)
                    int etxIndex = -1;
                    for (int i = 2; i < _rxBuffer.Count - 1; i++)
                    {
                        if (_rxBuffer[i] == DLE)
                        {
                            if (_rxBuffer[i + 1] == DLE)
                            {
                                i++; // skip the stuffed pair
                                continue;
                            }
                            if (_rxBuffer[i + 1] == ETX)
                            {
                                etxIndex = i;
                                break;
                            }
                        }
                    }
                    if (etxIndex == -1)
                        break; // need more bytes

                    int csLen = (ChecksumType == CheckSumOptions.Crc) ? 2 : 1;
                    int totalFrameLen = etxIndex + 2 + csLen;
                    if (_rxBuffer.Count < totalFrameLen)
                        break; // checksum bytes not yet fully received

                    // Extract the complete frame using ArrayPool to reduce allocations
                    byte[] frame = System.Buffers.ArrayPool<byte>.Shared.Rent(totalFrameLen);
                    try
                    {
                        Array.Clear(frame, 0, totalFrameLen);
                        _rxBuffer.Peek(frame, 0, totalFrameLen);
                        // Create a copy for the RawFrameReceived event (since we must not pass the rented buffer out)
                        byte[] rawFrameCopy = new byte[totalFrameLen];
                        Array.Copy(frame, 0, rawFrameCopy, 0, totalFrameLen);
                        OnRawFrameReceived(rawFrameCopy);
                        _rxBuffer.Advance(totalFrameLen);
                        _frameStartTime = DateTime.MinValue;

                        // Unstuff the payload between DLE STX and DLE ETX
                        int payloadLen = etxIndex - 2;
                        byte[] stuffedPayload = new byte[payloadLen];
                        Array.Copy(frame, 2, stuffedPayload, 0, payloadLen);
                        byte[] innerFrame = RemoveDleStuffing(stuffedPayload);

                        // Validate checksum
                        bool valid;
                        if (ChecksumType == CheckSumOptions.Crc)
                        {
                            ushort calc = CalculateChecksum(innerFrame, CheckSumOptions.Crc);
                            ushort recv = (ushort)(frame[etxIndex + 2] | (frame[etxIndex + 3] << 8));
                            valid = calc == recv;
                        }
                        else // BCC
                        {
                            byte calc = (byte)CalculateChecksum(innerFrame, CheckSumOptions.Bcc);
                            byte recv = frame[etxIndex + 2];
                            valid = calc == recv;
                        }

                        // Send ACK or NAK immediately
                        if (valid)
                        {
                            SendControl(ACK);
                            _lastResponseWasNAK = false;
                            pduToDeliver = innerFrame;
                        }
                        else
                        {
                            SendControl(NAK);
                            _lastResponseWasNAK = true;
                            if (SleepDelay < 400) SleepDelay += 50;
                        }
                    }
                    finally
                    {
                        System.Buffers.ArrayPool<byte>.Shared.Return(frame);
                    }
                    consumed = true;
                    continue;
                }

                // DLE followed by an unexpected byte – discard the DLE and resync
                _rxBuffer.Advance(1);
                consumed = true;
            }
            respondWithNak = _lastResponseWasNAK;   // capture inside lock
        }

        // Raise events outside the lock to avoid blocking the serial receive thread
        if (enqReceived)
        {
            // The DF1 specification requires responding to ENQ with the status
            // of the last received frame. We send ACK if the last frame was valid,
            // otherwise NAK. This matches the original VB behavior.
            SendControl(respondWithNak ? NAK : ACK);
        }
        if (pduToDeliver != null)
        {
            OnFrameReceived(pduToDeliver);
        }
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        _port.BytesReceived -= OnBytesReceived;
        _ackNakEvent.Dispose();
        _enqEvent.Dispose();
        base.Dispose();
    }
}
