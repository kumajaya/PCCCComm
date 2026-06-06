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

namespace PCCCComm.Core;

/// <summary>
/// DF1 half‑duplex master transport for RS‑485 multi‑drop networks.
/// Implements the correct 5‑step transaction sequence per Allen‑Bradley
/// Publication 1770-6.5.16, Chapter 6.
/// 
/// Correct transaction sequence:
///   1. Master sends command frame (DLE STX ... DLE ETX + checksum).
///   2. Slave responds with DLE ACK (link‑layer acknowledgment).
///   3. Master enters polling loop: sends DLE ENQ + SlaveAddress repeatedly.
///   4. Slave responds:
///      - DLE NAK if not ready → master continues polling.
///      - Data frame (DLE STX ...) when ready → master proceeds.
///   5. Master sends final DLE ACK and raises FrameReceived event.
/// 
/// This transport is synchronous and thread‑safe. Only one transaction
/// can be active at a time.
/// </summary>
public class DF1HalfDuplexTransport : DF1BaseTransport
{
    // --- RS-485 Direction Control ---
    /// <summary>
    /// RS-485 direction control mode.
    /// </summary>
    public enum Rs485ControlMode
    {
        /// <summary>Auto‑direction (hardware handles RTS).</summary>
        Auto,
        /// <summary>Manual control using RTS pin.</summary>
        Rts,
        /// <summary>Manual control using DTR pin.</summary>
        Dtr
    }

    private Rs485ControlMode _rs485Mode = Rs485ControlMode.Auto;
    private int _rtsAssertDelayMs = 1;
    private int _rtsDeassertDelayMs = 5;   // Increased for safety margin

    /// <summary>
    /// Gets or sets the RS-485 direction control mode. Default is Auto.
    /// </summary>
    public Rs485ControlMode Rs485Mode
    {
        get => _rs485Mode;
        set => _rs485Mode = value;
    }

    /// <summary>
    /// Delay in milliseconds after asserting RTS/DTR before writing data.
    /// Typical value 1-5 ms. Used only when Rs485Mode is not Auto.
    /// </summary>
    public int RtsAssertDelayMs
    {
        get => _rtsAssertDelayMs;
        set => _rtsAssertDelayMs = Math.Max(0, value);
    }

    /// <summary>
    /// Delay in milliseconds after writing data before deasserting RTS/DTR.
    /// Typical value 2-10 ms. Used only when Rs485Mode is not Auto.
    /// </summary>
    public int RtsDeassertDelayMs
    {
        get => _rtsDeassertDelayMs;
        set => _rtsDeassertDelayMs = Math.Max(0, value);
    }

    // --- Slave addressing ---
    private int _slaveAddress = 1;
    private readonly object _txLock = new object();

    /// <summary>
    /// Gets or sets the slave node address (1-254). Default is 1.
    /// </summary>
    public int SlaveAddress
    {
        get => _slaveAddress;
        set
        {
            if (value < 1 || value > 254)
                throw new ArgumentOutOfRangeException(nameof(SlaveAddress), "Address must be 1-254.");
            _slaveAddress = value;
        }
    }

    // --- Timeout Configuration ---
    /// <summary>
    /// Timeout in milliseconds waiting for the initial ACK after sending a command frame.
    /// Default is 500 ms.
    /// </summary>
    public int CommandAckTimeoutMs { get; set; } = 500;

    /// <summary>
    /// Timeout in milliseconds waiting for a response (NAK or data frame) to each poll.
    /// Default is 200 ms.
    /// </summary>
    public int PollResponseTimeoutMs { get; set; } = 200;

    /// <summary>
    /// Maximum number of poll attempts after receiving the initial ACK.
    /// Default is 20.
    /// </summary>
    public int MaxPollAttempts { get; set; } = 20;

    /// <summary>
    /// Delay in milliseconds between poll attempts when slave responds with NAK.
    /// Default is 20 ms.
    /// </summary>
    public int PollRetryDelayMs { get; set; } = 20;

    /// <summary>
    /// When true, bytes transmitted by this master are expected to echo back
    /// on the RX line (common on RS-485 half-duplex without hardware echo cancellation).
    /// The transport will discard echoed bytes automatically.
    /// Default is false (assumes hardware or adapter handles echo suppression).
    /// </summary>
    public bool EchoSuppression { get; set; } = false;

    // --- Explicit state machine for receive processing ---
    private enum MasterState
    {
        Idle,
        WaitingForCommandAck,   // After sending command frame, expecting ACK
        WaitingForPollResponse  // After sending poll, expecting NAK or data frame
    }
    private volatile MasterState _currentState = MasterState.Idle;
    private readonly object _stateLock = new object();

    // --- Receive buffers and flags ---
    private readonly object _rxLock = new object();
    private readonly List<byte> _rxBuffer = new List<byte>();
    private DateTime _frameStartTime = DateTime.MinValue;
    private const int FrameTimeoutMs = 500;
    private const int MaxBufferBytes = 4096;

    // Echo suppression (using Interlocked for atomic updates)
    private int _echoSuppressBytes = 0;
    private volatile bool _suppressEcho = false;

    // Transaction flags (volatile for cross-thread visibility)
    private volatile bool _commandAckReceived;
    private volatile bool _commandNakReceived;
    private volatile bool _pollNakReceived;
    private volatile bool _responseDataReceived;
    private volatile byte[]? _responseDataFrame;

    /// <summary>
    /// Initialises the half‑duplex master transport with a custom <see cref="ISerialPort"/>.
    /// </summary>
    public DF1HalfDuplexTransport(ISerialPort port) : base(port)
    {
        _port.BytesReceived += OnBytesReceived;
    }

    /// <summary>
    /// Initialises the half‑duplex master transport with standard serial port parameters.
    /// </summary>
    public DF1HalfDuplexTransport(string portName, int baudRate, Parity parity)
        : base(portName, baudRate, parity)
    {
        _port.BytesReceived += OnBytesReceived;
    }

    /// <inheritdoc/>
    public override void Open()
    {
        base.Open();
        // Set initial RTS/DTR state to receive mode
        if (_rs485Mode != Rs485ControlMode.Auto)
        {
            try
            {
                if (_rs485Mode == Rs485ControlMode.Rts)
                    _port.RtsEnable = false;
                else if (_rs485Mode == Rs485ControlMode.Dtr)
                    _port.DtrEnable = false;
            }
            catch { /* Ignore */ }
        }
    }

    /// <inheritdoc/>
    /// <summary>
    /// Sends a PCCC command frame to the slave and waits for the response.
    /// Implements the correct 5‑step half‑duplex master transaction with retry on NAK or timeout.
    /// </summary>
    public override void SendFrame(byte[] innerFrame)
    {
        if (innerFrame == null || innerFrame.Length == 0)
            throw new ArgumentException("Inner frame cannot be null or empty.", nameof(innerFrame));

        lock (_txLock)   // Only one transaction at a time
        {
            // Build the complete wire frame (includes DLE STX/ETX, DLE stuffing, checksum)
            byte[] commandFrame = BuildWireFrame(innerFrame);
            const int maxCmdRetries = 3;
            bool commandAcknowledged = false;

            // Step 1 & 2: Send command frame, retry on NAK or timeout up to 3 times (spec §6.3)
            for (int attempt = 0; attempt < maxCmdRetries; attempt++)
            {
                ResetTransactionFlags();
                _currentState = MasterState.WaitingForCommandAck;
                SendDataFrame(commandFrame);

                if (WaitForCommandAck(out bool wasNak))
                {
                    commandAcknowledged = true;
                    break;
                }

                // If this was the last attempt, throw timeout exception
                if (attempt == maxCmdRetries - 1)
                {
                    _currentState = MasterState.Idle;
                    throw new TimeoutException(
                        $"Slave did not respond to command frame after {maxCmdRetries} attempts.");
                }

                // Backoff: increase delay if NAK, otherwise use current SleepDelay
                if (wasNak && SleepDelay < 400)
                    SleepDelay += 50;
                Thread.Sleep(SleepDelay > 0 ? SleepDelay : 20);
            }

            if (!commandAcknowledged)
            {
                _currentState = MasterState.Idle;
                throw new TimeoutException(
                    $"Slave NAK'd command frame {maxCmdRetries} times. Communication failed.");
            }

            // Step 3 & 4: Poll for response
            _currentState = MasterState.WaitingForPollResponse;
            byte[]? responseInnerFrame = PollForResponse();

            _currentState = MasterState.Idle;

            if (responseInnerFrame != null)
            {
                // Step 5: Final ACK already sent inside PollForResponse
                OnFrameReceived(responseInnerFrame);
            }
            else
            {
                throw new TimeoutException("No response data received from slave after polling.");
            }
        }
    }

    // --- Private transaction helpers ---
    private void ResetTransactionFlags()
    {
        _commandAckReceived = false;
        _commandNakReceived = false;
        _pollNakReceived = false;
        _responseDataReceived = false;
        _responseDataFrame = null;
    }

    private bool WaitForCommandAck(out bool wasNak)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < CommandAckTimeoutMs)
        {
            if (_commandAckReceived) { wasNak = false; return true; }
            if (_commandNakReceived) { wasNak = true; return false; }
            Thread.Sleep(1);
        }
        wasNak = false;
        return false;
    }

    private byte[]? PollForResponse()
    {
        for (int attempt = 0; attempt < MaxPollAttempts; attempt++)
        {
            _pollNakReceived = false;
            _responseDataReceived = false;
            _responseDataFrame = null;

            SendPoll();

            // Wait for either NAK or data frame (inline, no lambda allocation)
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!_pollNakReceived && !_responseDataReceived)
            {
                if (sw.ElapsedMilliseconds >= PollResponseTimeoutMs)
                    break;
                Thread.Sleep(1);
            }

            if (_responseDataReceived && _responseDataFrame != null)
            {
                // Step 5: Final ACK must go through direction control
                SendWithDirectionControl(new byte[] { DLE, ACK });
                OnRawFrameSent(new byte[] { DLE, ACK });
                return _responseDataFrame;
            }

            if (_pollNakReceived)
            {
                // Slave not ready – wait and continue polling
                if (PollRetryDelayMs > 0)
                    Thread.Sleep(PollRetryDelayMs);
                continue;
            }

            // Timeout – no response to poll
            break;
        }
        return null;
    }

    // --- Transmission Methods (using direction control + echo suppression) ---
    private void SendPoll()
    {
        // Selective polling: DLE ENQ + SlaveAddress (3-byte, multi‑drop)
        byte[] poll = new byte[] { DLE, ENQ, (byte)_slaveAddress };
        SendWithDirectionControl(poll);
        OnRawFrameSent(poll);
    }

    private void SendDataFrame(byte[] frame)
    {
        SendWithDirectionControl(frame);
        OnRawFrameSent(frame);
    }

    /// <summary>
    /// Sends raw bytes with RS‑485 direction control.
    /// Also manages echo suppression if enabled.
    /// IMPORTANT: This method must only be called while holding _txLock.
    /// </summary>
    private void SendWithDirectionControl(byte[] data)
    {
        // Enable echo suppression BEFORE write (if configured)
        if (EchoSuppression)
        {
            Interlocked.Exchange(ref _echoSuppressBytes, data.Length);
            _suppressEcho = true;
        }

        if (_rs485Mode == Rs485ControlMode.Auto)
        {
            _port.Write(data, 0, data.Length);
            // No direction control, echo suppression may still work if needed
            return;
        }

        try
        {
            if (_rs485Mode == Rs485ControlMode.Rts)
                _port.RtsEnable = true;
            else if (_rs485Mode == Rs485ControlMode.Dtr)
                _port.DtrEnable = true;

            if (_rtsAssertDelayMs > 0)
                Thread.Sleep(_rtsAssertDelayMs);

            _port.Write(data, 0, data.Length);

            int transmitTimeMs = (data.Length * 10 * 1000) / _port.BaudRate;
            int totalDelay = Math.Max(1, transmitTimeMs + _rtsDeassertDelayMs);
            Thread.Sleep(totalDelay);

            if (_rs485Mode == Rs485ControlMode.Rts)
                _port.RtsEnable = false;
            else if (_rs485Mode == Rs485ControlMode.Dtr)
                _port.DtrEnable = false;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("RS-485 direction control failed.", ex);
        }
    }

    // --- Receive Handler (State Machine + Echo Suppression) ---
    private void OnBytesReceived(object? sender, byte[] chunk)
    {
        // Apply echo suppression if enabled
        byte[] filtered = chunk;
        if (EchoSuppression && _suppressEcho && _echoSuppressBytes > 0)
        {
            int discard = Math.Min(chunk.Length, _echoSuppressBytes);
            int remaining = Interlocked.Add(ref _echoSuppressBytes, -discard);
            if (remaining <= 0)
                _suppressEcho = false;
            if (discard >= chunk.Length)
                return; // entire chunk was echo, ignore
            filtered = chunk[discard..];
        }

        lock (_rxLock)
        {
            _rxBuffer.AddRange(filtered);
            if (_rxBuffer.Count > MaxBufferBytes)
            {
                _rxBuffer.Clear();
                _frameStartTime = DateTime.MinValue;
                return;
            }

            bool consumed = true;
            while (consumed && _rxBuffer.Count >= 2)
            {
                consumed = false;

                // Synchronisation: find DLE
                if (_rxBuffer[0] != DLE)
                {
                    _rxBuffer.RemoveAt(0);
                    consumed = true;
                    continue;
                }

                byte ctrl = _rxBuffer[1];

                // --- ACK / NAK processing (with explicit state) ---
                if (ctrl == ACK || ctrl == NAK)
                {
                    _rxBuffer.RemoveRange(0, 2);
                    _frameStartTime = DateTime.MinValue;

                    lock (_stateLock)
                    {
                        if (_currentState == MasterState.WaitingForCommandAck)
                        {
                            if (ctrl == ACK)
                                _commandAckReceived = true;
                            else if (ctrl == NAK)
                                _commandNakReceived = true;
                        }
                        else if (_currentState == MasterState.WaitingForPollResponse && ctrl == NAK)
                        {
                            _pollNakReceived = true;
                        }
                        // ACK during polling is ignored (should not happen)
                    }
                    consumed = true;
                    continue;
                }

                // --- Data frame: DLE STX ... DLE ETX ---
                if (ctrl == STX)
                {
                    if (_frameStartTime == DateTime.MinValue)
                        _frameStartTime = DateTime.UtcNow;

                    if ((DateTime.UtcNow - _frameStartTime).TotalMilliseconds > FrameTimeoutMs)
                    {
                        _rxBuffer.RemoveRange(0, 2);
                        _frameStartTime = DateTime.MinValue;
                        consumed = true;
                        continue;
                    }

                    // Find DLE ETX, skipping over stuffed DLE pairs
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
                    int totalLen = etxIndex + 2 + csLen;
                    if (_rxBuffer.Count < totalLen)
                        break; // checksum bytes not yet received

                    byte[] rawFrame = new byte[totalLen];
                    _rxBuffer.CopyTo(0, rawFrame, 0, totalLen);
                    OnRawFrameReceived(rawFrame);
                    _rxBuffer.RemoveRange(0, totalLen);
                    _frameStartTime = DateTime.MinValue;

                    // Extract inner frame (unstuffed)
                    int payloadLen = etxIndex - 2;
                    byte[] stuffed = new byte[payloadLen];
                    Array.Copy(rawFrame, 2, stuffed, 0, payloadLen);
                    byte[] innerFrame = RemoveDleStuffing(stuffed);

                    // Validate checksum
                    bool valid;
                    if (ChecksumType == CheckSumOptions.Crc)
                    {
                        ushort calc = MessageDecoder.CalculateChecksum(innerFrame, CheckSumOptions.Crc);
                        ushort recv = (ushort)(rawFrame[etxIndex + 2] | (rawFrame[etxIndex + 3] << 8));
                        valid = calc == recv;
                    }
                    else
                    {
                        byte calc = (byte)MessageDecoder.CalculateChecksum(innerFrame, CheckSumOptions.Bcc);
                        byte recv = rawFrame[etxIndex + 2];
                        valid = calc == recv;
                    }

                    if (valid)
                    {
                        lock (_stateLock)
                        {
                            if (_currentState == MasterState.WaitingForPollResponse)
                            {
                                _responseDataReceived = true;
                                _responseDataFrame = innerFrame;
                            }
                            // Unsolicited data frames (not during polling) are ignored.
                        }
                    }
                    // Invalid frame – ignore (master will timeout)
                    consumed = true;
                    continue;
                }

                // Unexpected byte after DLE – discard DLE
                _rxBuffer.RemoveAt(0);
                consumed = true;
            }
        }
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        _port.BytesReceived -= OnBytesReceived;
        base.Dispose();
    }
}
