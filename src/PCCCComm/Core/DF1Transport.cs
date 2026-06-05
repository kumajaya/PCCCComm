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
//
// -----------------------------------------------------------------------------
// DF1 full‑duplex transport over RS‑232.
// Handles DLE stuffing, CRC‑16/BCC, ACK/NAK, ENQ, and automatic backoff.
// Reference: Allen Bradley Publication 1770-6.5.16, Chapters 5-7.

using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Threading;

namespace PCCCComm.Core;

/// <inheritdoc cref="ITransport"/>
/// <summary>
/// DF1 full‑duplex transport implementation using a serial port.
/// This class replaces the original <c>DataLink</c> and the serial‑related code
/// that was previously embedded inside <c>PCCCComm</c>. All timing, retry, and
/// backoff behaviours are preserved.
/// </summary>
public class DF1Transport : ITransport
{
    // --- DF1 control characters (Publication 1770-6.5.16, Chapter 5) ---
    private const byte DLE = 0x10;   // Data Link Escape
    private const byte STX = 0x02;   // Start of Text
    private const byte ETX = 0x03;   // End of Text
    private const byte ACK = 0x06;   // Acknowledge
    private const byte NAK = 0x15;   // Not Acknowledge
    private const byte ENQ = 0x05;   // Enquiry

    private readonly ISerialPort _port;
    private readonly object _rxLock = new object();
    private readonly List<byte> _rxBuffer = new List<byte>();
    private DateTime _frameStartTime = DateTime.MinValue;
    private const int FrameTimeoutMs = 500;      // Max time between DLE STX and DLE ETX
    private const int MaxBufferBytes = 4096;     // Safety limit

    private CheckSumOptions _checksumType = CheckSumOptions.Crc;
    private int _sleepDelay = 0;                // backoff after NAK (helps with USB converters)
    private bool _lastResponseWasNAK = false;

    // ACK/NAK polling flags (used during SendFrame)
    private volatile bool _ackReceived;
    private volatile bool _nakReceived;

    // ENQ polling flags (used by auto-detect)
    private volatile bool _ackFlagForEnq;
    private volatile bool _nakFlagForEnq;

    private int _ackWaitTicks = 0;
    private int _maxTicks = 100;                // 100 * 20ms = 2 seconds

    public event EventHandler<byte[]>? FrameReceived;
    public event EventHandler<byte[]>? RawFrameSent;
    public event EventHandler<byte[]>? RawFrameReceived;

    public bool IsOpen => _port.IsOpen;

    /// <summary>Gets or sets the checksum algorithm (CRC or BCC).</summary>
    public CheckSumOptions ChecksumType
    {
        get => _checksumType;
        set => _checksumType = value;
    }

    /// <summary>
    /// Sleep delay (ms) added after a NAK. Increases automatically on repeated NAKs.
    /// Helps stabilise communication with USB‑to‑serial converters.
    /// </summary>
    public int SleepDelay
    {
        get => _sleepDelay;
        set => _sleepDelay = value < 0 ? 0 : value;
    }

    /// <summary>
    /// Maximum number of polling ticks (each tick = 20 ms) to wait for ACK/NAK.
    /// Default is 100 (2 seconds). Used in auto‑detect and normal sends.
    /// </summary>
    public int MaxTicks
    {
        get => _maxTicks;
        set => _maxTicks = value > 0 ? value : 100;
    }

    /// <summary>
    /// Initialises the DF1 transport with a custom <see cref="ISerialPort"/>.
    /// </summary>
    public DF1Transport(ISerialPort port)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
        _port.BytesReceived += OnBytesReceived;
    }

    /// <summary>
    /// Initialises the DF1 transport with standard serial port parameters.
    /// </summary>
    public DF1Transport(string portName, int baudRate, Parity parity)
        : this(new SerialPortWrapper(portName, baudRate, parity))
    {
    }

    /// <inheritdoc/>
    public void Open() => _port.Open();

    /// <inheritdoc/>
    public void Close() => _port.Close();

    /// <inheritdoc/>
    public void SendFrame(byte[] innerFrame)
    {
        if (innerFrame == null || innerFrame.Length == 0)
            throw new ArgumentException("Inner frame cannot be null or empty.", nameof(innerFrame));

        // 1. DLE stuffing – duplicate any 0x10 byte in the payload
        byte[] stuffed = ApplyDleStuffing(innerFrame);

        // 2. Calculate checksum (CRC or BCC) over the UNSTUFFED inner frame
        ushort checksum = MessageDecoder.CalculateChecksum(innerFrame, _checksumType);
        int csLen = (_checksumType == CheckSumOptions.Crc) ? 2 : 1;

        // 3. Build the complete wire frame: DLE STX + stuffed + DLE ETX + checksum
        byte[] frame = new byte[2 + stuffed.Length + 2 + csLen];
        int idx = 0;
        frame[idx++] = DLE;
        frame[idx++] = STX;
        Array.Copy(stuffed, 0, frame, idx, stuffed.Length);
        idx += stuffed.Length;
        frame[idx++] = DLE;
        frame[idx++] = ETX;
        frame[idx++] = (byte)(checksum & 0xFF);
        if (csLen == 2)
            frame[idx++] = (byte)((checksum >> 8) & 0xFF);

        // Raise raw frame event for logging
        RawFrameSent?.Invoke(this, frame);

        // 4. Send with retry (max 2 attempts, as in original VB code)
        int retries = 0;
        const int maxRetries = 2;

        while (retries < maxRetries)
        {
            _ackReceived = false;
            _nakReceived = false;

            if (_sleepDelay > 0)
                Thread.Sleep(_sleepDelay);

            _port.Write(frame, 0, frame.Length);

            // Poll for ACK/NAK using 20 ms ticks
            _ackWaitTicks = 0;
            while (!_ackReceived && !_nakReceived && _ackWaitTicks < _maxTicks)
            {
                Thread.Sleep(20);
                _ackWaitTicks++;
            }

            if (_ackReceived)
                return;                     // Success

            if (_nakReceived)
            {
                // Backoff: increase sleep delay for the next retry
                if (_sleepDelay < 400) _sleepDelay += 50;
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
        if (!_port.IsOpen)
            Open();

        _ackFlagForEnq = false;
        _nakFlagForEnq = false;

        SendControl(ENQ);

        int waitTicks = 0;
        while (!_ackFlagForEnq && !_nakFlagForEnq && waitTicks < _maxTicks)
        {
            Thread.Sleep(20);
            waitTicks++;
        }

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

    // --- DLE stuffing helpers -------------------------------------------------

    private static byte[] ApplyDleStuffing(byte[] payload)
    {
        var result = new List<byte>(payload.Length * 2);
        foreach (byte b in payload)
        {
            result.Add(b);
            if (b == DLE)
                result.Add(DLE);
        }
        return result.ToArray();
    }

    private static byte[] RemoveDleStuffing(byte[] stuffed)
    {
        var result = new List<byte>(stuffed.Length);
        for (int i = 0; i < stuffed.Length; i++)
        {
            if (stuffed[i] == DLE && i + 1 < stuffed.Length && stuffed[i + 1] == DLE)
            {
                result.Add(DLE);
                i++; // skip the stuffed duplicate
            }
            else
                result.Add(stuffed[i]);
        }
        return result.ToArray();
    }

    // --- Send a single control byte (ACK, NAK, ENQ) with DLE prefix ----------

    private void SendControl(byte controlByte)
    {
        if (controlByte != ACK && controlByte != NAK && controlByte != ENQ)
            throw new ArgumentException("Invalid control byte.", nameof(controlByte));
        _port.Write(new byte[] { DLE, controlByte }, 0, 2);
    }

    // --- Serial receive handler (state machine) ------------------------------

    private void OnBytesReceived(object? sender, byte[] chunk)
    {
        if (chunk == null || chunk.Length == 0) return;

        byte[]? pduToDeliver = null;
        bool enqReceived = false;
        bool respondWithNak = false;

        lock (_rxLock)
        {
            _rxBuffer.AddRange(chunk);
            if (_rxBuffer.Count > MaxBufferBytes)
            {
                // Buffer overflow – reset everything
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
                    _rxBuffer.RemoveAt(0);
                    consumed = true;
                    continue;
                }

                byte ctrl = _rxBuffer[1];

                // --- 2‑byte link control: ACK, NAK, ENQ ---
                if (ctrl == ACK || ctrl == NAK || ctrl == ENQ)
                {
                    _rxBuffer.RemoveRange(0, 2);
                    _frameStartTime = DateTime.MinValue;

                    if (ctrl == ACK)
                    {
                        if (_sleepDelay > 0) Thread.Sleep(_sleepDelay);
                        _ackReceived = true;
                        _ackFlagForEnq = true;
                    }
                    else if (ctrl == NAK)
                    {
                        _nakReceived = true;
                        _nakFlagForEnq = true;
                        _lastResponseWasNAK = true;
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
                        _rxBuffer.RemoveRange(0, 2);
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

                    int csLen = (_checksumType == CheckSumOptions.Crc) ? 2 : 1;
                    int totalFrameLen = etxIndex + 2 + csLen;
                    if (_rxBuffer.Count < totalFrameLen)
                        break; // checksum bytes not yet fully received

                    // Extract the complete frame
                    byte[] frame = new byte[totalFrameLen];
                    _rxBuffer.CopyTo(0, frame, 0, totalFrameLen);
                    RawFrameReceived?.Invoke(this, frame);
                    _rxBuffer.RemoveRange(0, totalFrameLen);
                    _frameStartTime = DateTime.MinValue;

                    // Unstuff the payload between DLE STX and DLE ETX
                    int payloadLen = etxIndex - 2;
                    byte[] stuffedPayload = new byte[payloadLen];
                    Array.Copy(frame, 2, stuffedPayload, 0, payloadLen);
                    byte[] innerFrame = RemoveDleStuffing(stuffedPayload);

                    // Validate checksum
                    bool valid;
                    if (_checksumType == CheckSumOptions.Crc)
                    {
                        ushort calc = MessageDecoder.CalculateChecksum(innerFrame, CheckSumOptions.Crc);
                        ushort recv = (ushort)(frame[etxIndex + 2] | (frame[etxIndex + 3] << 8));
                        valid = calc == recv;
                    }
                    else // BCC
                    {
                        byte calc = (byte)MessageDecoder.CalculateChecksum(innerFrame, CheckSumOptions.Bcc);
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
                        if (_sleepDelay < 400) _sleepDelay += 50;
                    }
                    consumed = true;
                    continue;
                }

                // DLE followed by an unexpected byte – discard the DLE and resync
                _rxBuffer.RemoveAt(0);
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
            FrameReceived?.Invoke(this, pduToDeliver);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _port.BytesReceived -= OnBytesReceived;
        _port.Dispose();
    }
}