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
using System.Collections.Generic;
using System.IO.Ports;
using System.Threading;
using System.Threading.Channels;
using System.Runtime.InteropServices;

/// <summary>
/// DF1 Full-Duplex transport implementation for PCCC emulator.
/// Handles DLE stuffing, ACK/NAK, CRC/BCC checksums, and ENQ polling.
/// 
/// This class implements the ILinkTransport interface and provides the
/// low-level DF1 framing over RS-232 serial communication.
/// 
/// FRAME FORMAT (both directions):
///   DLE STX (0x10 0x02) | DLE-stuffed inner payload | DLE ETX (0x10 0x03) | Checksum (1 or 2 bytes)
/// 
/// INNER PAYLOAD FORMAT:
///   DST (1 byte) | SRC (1 byte) | CMD (1 byte) | STS (1 byte) | TNS_LO (1 byte) | TNS_HI (1 byte) | [FUNC (1 byte)] | [DATA...]
/// 
/// TANSPORT BEHAVIOR:
///   - Every valid frame must be acknowledged with ACK (DLE 0x06) before processing
///   - Invalid frames (checksum mismatch, malformed) trigger NAK (DLE 0x15)
///   - Standalone ENQ (DLE 0x05) is used for node presence detection (auto-configure)
///   - DLE byte (0x10) in payload must be doubled (0x10 0x10) for transparency
/// 
/// HIGH-PERFORMANCE OPTIMIZATIONS:
///   - Producer-Consumer pattern with System.Threading.Channels (decouples I/O from processing)
///   - Circular buffer with head/tail pointers (reduces memory copying)
///   - stackalloc for small frame operations (ACK, NAK, small unstuffing)
///   - Conditional logging to eliminate string allocations in hot path
///   - Span-based frame building (zero allocation in TX path)
///   - Static readonly frames for ACK/NAK (zero allocation per call)
///   - Direct buffer allocation (no List/ToArray overhead)
///   - Health monitoring with periodic statistics logging
/// 
/// CIRCULAR BUFFER DESIGN:
///   - _rxBuffer: Power-of-two sized buffer for efficient modulo operations
///   - _rxHead: Read position (where parsing starts)
///   - _rxTail: Write position (where new data is added)
///   - _rxCount: Number of available bytes (avoids head/tail comparison)
///   - Grows dynamically when capacity is exceeded
/// 
/// ERROR HANDLING:
///   - Port error events trigger buffer reset and recovery
///   - Oversized frames (>512 bytes payload) are rejected with NAK
///   - Checksum mismatches increment diagnostic counters and send NAK
///   - Frame parsing errors skip single bytes to maintain synchronization
/// </summary>
public class DF1Transport : ILinkTransport
{
    private readonly PCCCEmulator _emulator;
    private readonly SerialPort _port;

    // Transport configuration (mirrored from PCCCEngine for performance)
    private CheckSumOptions _checkSum;
    private int _myNode;

    // ─── HIGH-PERFORMANCE: Producer-Consumer Channel ─────────────────────────
    // Decouples serial port DataReceived event from frame processing.
    // Benefits:
    //   - Serial port thread does minimal work (only reads bytes)
    //   - Processing runs on dedicated background thread
    //   - Backpressure handled automatically by channel
    private readonly Channel<byte[]> _receiveChannel;
    private readonly CancellationTokenSource _processingCts;
    private Task _processingTask = Task.CompletedTask;

    // ─── Circular Buffer Management ─────────────────────────────────────────
    // Power of two size enables mask optimization: index & (_bufferSize - 1)
    // Initial size 8192 bytes (typical DF1 frame < 512 bytes, holds ~16 frames)
    private byte[] _rxBuffer = new byte[8192];
    private int _rxHead = 0;          // Read position (start of valid data)
    private int _rxTail = 0;          // Write position (end of valid data)
    private int _rxCount = 0;         // Number of bytes available (_rxTail - _rxHead with wrap)
    private volatile int _rxResetRequested = 0;  // Signal from error handler to reset buffer

    // Lock ordering: always acquire _txLock before _rxLock to avoid deadlock.
    private readonly object _txLock = new object();

    // ─── Conditional Logging ────────────────────────────────────────────────
    // Eliminates string allocations when logging is disabled (high performance mode)
    private bool _isLoggingEnabled = true;
    private DateTime _lastLog = DateTime.Now;
    private readonly object _logLock = new object();

    // ─── Health Monitoring ──────────────────────────────────────────────────
    private Timer? _healthTimer;
    private long _lastFrameCount = 0;
    private long _framesProcessed = 0;
    private long _lastFrameLog = 0;

    // ─── Edge Case Handling ─────────────────────────────────────────────────
    // Prevents log spam during sustained error conditions
    private DateTime _lastErrorTime = DateTime.MinValue;
    private readonly TimeSpan _errorThrottle = TimeSpan.FromSeconds(5);

    private volatile int _isDisposing = 0; // Atomic flag for shutdown (0 = running, 1 = disposing)
    private int _activeCallbacks = 0;      // Tracks active DataReceived callbacks for graceful shutdown

    // Static readonly frames for ACK/NAK (zero allocation per call)
    // These are small, constant, and never modified - perfect for static readonly
    private static readonly byte[] ACK_FRAME = new byte[] { 0x10, 0x06 };
    private static readonly byte[] NAK_FRAME = new byte[] { 0x10, 0x15 };

    // ─── Events (ILinkTransport implementation) ──────────────────────────────
    /// <summary>
    /// Raised when a complete PDU (inner frame) has been received and parsed.
    /// The PDU is the unstuffed inner payload without DLE framing.
    /// Format: DST, SRC, CMD, STS, TNS_LO, TNS_HI, [FUNC], [DATA...]
    /// </summary>
    public event EventHandler<(byte[] pdu, object ClientContext)>? PduReceived;
    
    /// <summary>
    /// Human-readable name of this transport for logging.
    /// </summary>
    public string Name => "DF1";

    // ─── Properties ─────────────────────────────────────────────────────────
    public CheckSumOptions CheckSum
    {
        get => _checkSum;
        set => _checkSum = value;
    }

    public int MyNode
    {
        get => _myNode;
        set => _myNode = value;
    }

    // ─── Internal Methods for PCCCEngine to Access Serial Port Status ─────
    // These allow the emulator to read modem line states for diagnostic counters
    // without exposing the entire SerialPort object.
    internal bool GetCtsHolding() => _port.IsOpen && _port.CtsHolding;
    internal bool GetRtsEnable() => _port.IsOpen && _port.RtsEnable;
    internal bool GetDsrHolding() => _port.IsOpen && _port.DsrHolding;
    internal bool GetCdHolding() => _port.IsOpen && _port.CDHolding;
    internal bool GetDtrEnable() => _port.IsOpen && _port.DtrEnable;

    // ─── Constructor ────────────────────────────────────────────────────────
    /// <summary>
    /// Initializes the DF1 transport handler.
    /// </summary>
    /// <param name="emulator">Parent emulator instance (provides counters and logging)</param>
    /// <param name="portName">Serial port name (e.g., "COM2" or "/dev/ttyUSB0")</param>
    /// <param name="baudRate">Baud rate (e.g., 19200, 9600, 38400)</param>
    /// <param name="parity">Parity mode (None, Odd, Even)</param>
    public DF1Transport(PCCCEmulator emulator, string portName, int baudRate, Parity parity)
    {
        _emulator = emulator ?? throw new ArgumentNullException(nameof(emulator));
        _checkSum = _emulator.CheckSum;
        _myNode   = _emulator.MyNode;

        // Configure SerialPort with conservative timeouts (DF1 is half-duplex with ACK)
        _port = new SerialPort(portName, baudRate, parity, 8, StopBits.One)
        {
            ReadTimeout = 500,      // 500ms timeout for reads
            WriteTimeout = 500,     // 500ms timeout for writes
            ReadBufferSize = 65536,  // Large buffer for burst traffic
            WriteBufferSize = 65536
        };
        
        // Subscribe to serial port events
        _port.DataReceived += Port_DataReceived;
        _port.ErrorReceived += Port_ErrorReceived;
        _port.PinChanged += Port_PinChanged;

        // Initialize high-performance producer-consumer channel
        _receiveChannel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
        {
            SingleReader = true,   // Only one consumer task (no locking needed)
            SingleWriter = false,  // DataReceived may be called on multiple threads
            AllowSynchronousContinuations = false  // Avoid thread pool starvation
        });
        _processingCts = new CancellationTokenSource();
    }

    // ─── ILinkTransport Implementation ──────────────────────────────────────
    /// <summary>
    /// Starts the DF1 transport handler.
    /// Opens the serial port, discards any stale data, and starts the
    /// background processing task.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when port is not found or access denied</exception>
    public void Start()
    {
        // Validate port exists (platform-specific handling)
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Windows: case-insensitive check against available COM ports
            if (!SerialPort.GetPortNames()
                    .Contains(_port.PortName, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Port '{_port.PortName}' not found. Available ports: " +
                    $"{string.Join(", ", SerialPort.GetPortNames())}");
            }
        }
        else
        {
            // Linux: normalize port name (add /dev/ prefix if needed)
            string baseName = _port.PortName.Replace("/dev/", "");
            string fullPath = $"/dev/{baseName}";
            var ports = Directory.GetFiles("/dev", "tty*")
                .Concat(Directory.Exists("/dev/pts") 
                    ? Directory.GetFiles("/dev/pts") 
                    : Array.Empty<string>())
                .ToArray();
            if (ports.Contains(fullPath))
            {
                _port.PortName = fullPath;
            }
            else if (!ports.Contains(_port.PortName))
            {
                var likelyPorts = ports.Where(p => p.StartsWith("/dev/ttyUSB")
                        || p.StartsWith("/dev/ttyS") || p.StartsWith("/dev/ttyACM"));
                throw new InvalidOperationException(
                    $"Port '{_port.PortName}' not found. Available tty devices: " +
                    $"{string.Join(", ", likelyPorts)}");
            }
        }

        try
        {
            _port.Open();
            _port.DiscardInBuffer();  // Clear any stale data from previous connections

            // Start background processing task (dedicated thread for frame parsing)
            _processingTask = Task.Run(ProcessReceiveChannelAsync);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new Exception($"Port '{_port.PortName}' is busy. Details: {ex.Message}");
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to open port {_port.PortName}: {ex.Message}");
        }
    }

    /// <summary>
    /// Stops the DF1 transport handler gracefully.
    /// Waits for pending operations to complete before closing the port.
    /// Thread-safe and prevents data loss during shutdown.
    /// </summary>
    public void Stop()
    {
        if (Interlocked.CompareExchange(ref _isDisposing, 1, 0) != 0) return;

        // Step 1: Stop accepting new data from serial port
        _port.DataReceived -= Port_DataReceived;

        // Step 2: Stop health monitoring timer
        _healthTimer?.Dispose();

        // Step 3: Cancel the consumer task
        _processingCts?.Cancel();

        // Step 4: Complete the channel (no more writes allowed)
        _receiveChannel.Writer.TryComplete();

        // Step 5: Wait for consumer task to finish processing pending items
        try
        {
            if (_processingTask != null && !_processingTask.IsCompleted)
            {
                _processingTask.Wait(TimeSpan.FromSeconds(3));
            }
        }
        catch (AggregateException ex)
        {
            Console.WriteLine($"[STOP] Consumer task shutdown warning: {ex.InnerException?.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[STOP] Error during task shutdown: {ex.Message}");
        }

        // Step 6: Wait for any active DataReceived callbacks to complete
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref _activeCallbacks) > 0 && sw.ElapsedMilliseconds < 2000)
        {
            Thread.Sleep(10);
        }

        // Step 7: Close serial port
        try
        {
            if (_port.IsOpen)
            {
                _port.DiscardInBuffer();
                _port.DiscardOutBuffer();
                _port.Close();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[STOP] Error closing port: {ex.Message}");
        }
    }

    /// <summary>
    /// Sends a response PDU back to the client using DF1 framing.
    /// The PDU is the inner frame (DST, SRC, CMD, STS, TNS, FUNC?, DATA...)
    /// This method adds DLE STX/ETX framing, DLE stuffing, and checksum.
    /// </summary>
    /// <param name="pdu">Inner frame PDU to send (without DLE framing)</param>
    public void SendResponse(byte[] pdu, object clientContext)
    {
        SendRawFrame(pdu);
    }

    /// <summary>
    /// Enables or disables verbose logging for this transport instance.
    /// When logging is enabled, the health monitor is disabled to reduce overhead.
    /// When logging is disabled, the health monitor is activated for visibility.
    /// </summary>
    /// <param name="enabled">True to enable logging, false for maximum performance</param>
    public void SetLoggingEnabled(bool enabled)
    {
        _isLoggingEnabled = enabled;

        if (enabled)
        {
            // Logging ON → health monitor OFF (reduce overhead)
            _healthTimer?.Dispose();
            _healthTimer = null;
        }
        else
        {
            // Logging OFF → health monitor ON (provide visibility)
            _healthTimer ??= new Timer(_ => LogHealthStats(), null, 15000, 15000);
            Console.WriteLine("[PERF] DF1 logging disabled — health monitor active");
        }
    }

    // ─── Health Monitoring ─────────────────────────────────────────────────
    /// <summary>
    /// Logs health statistics every 15 seconds for monitoring purposes.
    /// Shows frames per second, total frame count, bad packet count, and memory usage.
    /// Alerts when no communication is detected (potential connection issue).
    /// </summary>
    private void LogHealthStats()
    {
        if (_isDisposing != 0) return;

        long currentFrames = Interlocked.Read(ref _framesProcessed);
        long delta = currentFrames - _lastFrameCount;
        _lastFrameCount = currentFrames;

        Console.WriteLine($"[MONI] DF1 Rate: {delta / 15,6}/s | " +
            $"Total: {currentFrames,10:N0} | " +
            $"Bad: {_emulator.GetBadPacketsDetected(),4:N0} | " +
            $"Memory: {GC.GetTotalMemory(false) / 1024,6:N0} KB");

        if (delta == 0 && currentFrames > 0)
        {
            Console.WriteLine($"[WARN] No DF1 communication detected in last 15 seconds. Check client connection.");
        }
    }

    // ─── Serial Receive (PRODUCER: minimal work, only reads bytes) ─────────
    /// <summary>
    /// Handles DataReceived event from SerialPort.
    /// Does minimal work: reads bytes and writes to channel.
    /// Heavy processing is done in background task to avoid blocking the serial port.
    /// </summary>
    private void Port_DataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (_isDisposing != 0) return;
        Interlocked.Increment(ref _activeCallbacks);

        try
        {
            int bytesToRead = _port.BytesToRead;
            if (bytesToRead <= 0) return;

            // Read directly into exact-sized buffer - one allocation per receive
            // This is necessary because SerialPort.Read returns the actual bytes read
            byte[] buffer = new byte[bytesToRead];
            int bytesRead = _port.Read(buffer, 0, bytesToRead);

            if (bytesRead > 0)
            {
                // Trim if Read() returned fewer bytes than BytesToRead (rare but possible)
                byte[] exactBuffer = (bytesRead == bytesToRead)
                    ? buffer
                    : buffer[..bytesRead];  // Slice creates new array, no extra copy

                // Non-blocking write to channel (never throws)
                if (!_receiveChannel.Writer.TryWrite(exactBuffer))
                {
                    _emulator.IncrementBadPacketsDetected();
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // Ignore during shutdown (port is being closed)
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Port_DataReceived error: {ex.Message}");
        }
        finally
        {
            Interlocked.Decrement(ref _activeCallbacks);
        }
    }

    /// <summary>
    /// Handles serial port errors (buffer overflows, frame errors, etc.)
    /// Attempts to recover by discarding buffers and resetting the circular buffer.
    /// Throttles error logging to avoid console spam.
    /// </summary>
    private void Port_ErrorReceived(object sender, SerialErrorReceivedEventArgs e)
    {
        if (_isDisposing != 0) return;

        // Throttle error logging to avoid spam during sustained errors
        if (DateTime.Now - _lastErrorTime < _errorThrottle)
            return;
        _lastErrorTime = DateTime.Now;

        Console.WriteLine($"[ERR] Serial port error: {e.EventType}");

        // Signal consumer to reset circular buffer (lock-free)
        Interlocked.Exchange(ref _rxResetRequested, 1);

        try
        {
            if (_port.IsOpen)
            {
                _port.DiscardInBuffer();
                _port.DiscardOutBuffer();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERR] Discard buffer failed: {ex.Message}");
        }

        _emulator.IncrementBadPacketsDetected();
    }

    /// <summary>
    /// Handles serial pin changes (DCD, CTS, DSR, etc.)
    /// Updates modem status for diagnostic counters.
    /// </summary>
    private void Port_PinChanged(object sender, SerialPinChangedEventArgs e)
    {
        if (_isDisposing != 0) return;

        if (_isLoggingEnabled)
        {
            Console.WriteLine($"[PIN] {e.EventType} - DCD: {_port.CDHolding}, CTS: {_port.CtsHolding}, DSR: {_port.DsrHolding}");
        }

        _emulator.UpdateModemStatus();
    }

    // ─── Background Processing Task (CONSUMER) ─────────────────────────────
    /// <summary>
    /// Background task that processes received data from the channel.
    /// Runs on a dedicated thread with AboveNormal priority for real-time performance.
    /// This is where all heavy processing (frame parsing, checksum validation) happens.
    /// </summary>
    private async Task ProcessReceiveChannelAsync()
    {
        // Boost thread priority for real-time performance (Windows only)
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
        }

        try
        {
            await foreach (var buffer in _receiveChannel.Reader.ReadAllAsync(_processingCts.Token))
            {
                // Check for reset request from error handler
                if (Interlocked.CompareExchange(ref _rxResetRequested, 0, 1) == 1)
                {
                    _rxHead = 0;
                    _rxTail = 0;
                    _rxCount = 0;
                    if (_isLoggingEnabled)
                        Console.WriteLine("[INFO] Circular buffer reset due to error");
                }

                // Optional periodic performance logging (every 10000 frames)
                long frames = _framesProcessed;
                if (_isLoggingEnabled && frames - _lastFrameLog >= 10000)
                {
                    _lastFrameLog = frames;
                    Console.WriteLine($"[PERF] Processed {frames} DF1 frames, GC.GetTotalMemory: {GC.GetTotalMemory(false) / 1024:N0} KB");
                }

                // Add data to circular buffer
                int bytesToAdd = buffer.Length;

                // Grow buffer if needed (doubling strategy amortizes reallocation cost)
                if (_rxCount + bytesToAdd > _rxBuffer.Length)
                {
                    int newSize = Math.Max(_rxBuffer.Length * 2, _rxCount + bytesToAdd);
                    byte[] newBuffer = new byte[newSize];

                    // Linearize circular buffer into new buffer
                    if (_rxHead <= _rxTail)
                    {
                        Array.Copy(_rxBuffer, _rxHead, newBuffer, 0, _rxCount);
                    }
                    else
                    {
                        int firstPart = _rxBuffer.Length - _rxHead;
                        Array.Copy(_rxBuffer, _rxHead, newBuffer, 0, firstPart);
                        Array.Copy(_rxBuffer, 0, newBuffer, firstPart, _rxTail);
                    }

                    _rxBuffer = newBuffer;
                    _rxHead = 0;
                    _rxTail = _rxCount;
                }

                // Copy new data to circular buffer (handles wrap-around)
                if (_rxTail + bytesToAdd <= _rxBuffer.Length)
                {
                    Array.Copy(buffer, 0, _rxBuffer, _rxTail, bytesToAdd);
                }
                else
                {
                    int firstPart = _rxBuffer.Length - _rxTail;
                    Array.Copy(buffer, 0, _rxBuffer, _rxTail, firstPart);
                    Array.Copy(buffer, firstPart, _rxBuffer, 0, bytesToAdd - firstPart);
                }

                _rxTail = (_rxTail + bytesToAdd) % _rxBuffer.Length;
                _rxCount += bytesToAdd;

                // Parse all complete frames from the buffer
                ParseBuffer();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown - expected during Stop()
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ProcessReceiveChannelAsync error: {ex.Message}");
        }
    }

    // ─── Frame Parser ──────────────────────────────────────────────────────
    /// <summary>
    /// Parses complete DF1 frames from the circular buffer.
    /// Scans for DLE STX (0x10 0x02) and finds matching DLE ETX (0x10 0x03)
    /// while correctly handling stuffed DLE pairs (0x10 0x10) in the payload.
    /// 
    /// When a complete frame is found, it is extracted and passed to ProcessFrame().
    /// Invalid bytes that cannot start a frame are skipped to maintain synchronization.
    /// </summary>
    private void ParseBuffer()
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
                // Remove ENQ from buffer
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
                        // Stuffed DLE - skip both bytes as they are part of payload
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

                // Frame structure: DLE STX (2) + stuffed_payload (payloadLen) + DLE ETX (2) + checksum (chkBytes)
                int frameLen = 2 + payloadLen + 2 + chkBytes;

                if (frameLen > remaining) break; // Checksum bytes not yet received

                // Extract frame bytes (linearize to contiguous array)
                byte[] frame = new byte[frameLen];
                int spaceToEnd = _rxBuffer.Length - scanPos;
                if (spaceToEnd >= frameLen)
                {
                    // Frame does not wrap — single copy
                    Array.Copy(_rxBuffer, scanPos, frame, 0, frameLen);
                }
                else
                {
                    // Frame wraps around buffer end — two copies
                    Array.Copy(_rxBuffer, scanPos, frame, 0, spaceToEnd);
                    Array.Copy(_rxBuffer, 0, frame, spaceToEnd, frameLen - spaceToEnd);
                }

                // Remove frame from buffer
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

    // ─── ENQ Handler ────────────────────────────────────────────────────────
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

    // ─── Frame Processor ────────────────────────────────────────────────────
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
                if (_isLoggingEnabled)
                    Console.WriteLine($"[WARN] Oversized DF1 frame rejected: payloadLen={payloadLen}");
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
                if (_isLoggingEnabled)
                    Console.WriteLine($"Checksum mismatch: calc=0x{calc:X4} recv=0x{receivedChk:X4} ({_checkSum})");
                _emulator.IncrementBadPacketsDetected();
                _emulator.IncrementUndeliveredPackets();
                _emulator.IncrementNoBufferNakd();
                SendNak();
                return;
            }

            // Valid packet received
            _emulator.IncrementTotalPacketsReceived();

            int dst = unstuffed[0];
            int src = unstuffed[1];
            int cmd = unstuffed[2];
            int tns = unstuffed[4] | (unstuffed[5] << 8);
            int func = unstuffedLen >= 7 ? unstuffed[6] : 0;

            if (_isLoggingEnabled)
            {
                int dataLen = Math.Max(0, unstuffedLen - 7);
                LogDelta($"\n    RX: ");
                Console.WriteLine(BitConverter.ToString(rawFrame).Replace("-", " "));
                Console.WriteLine($"    dst={dst} src={src} cmd=0x{cmd:X2} tns={tns} func=0x{func:X2} dataLen={dataLen}");
            }

            // Only respond if this frame is addressed to us (or broadcast)
            if (dst != _myNode && dst != 0xFF) return;

            // ACK before responding — required by DF1 full-duplex transport
            SendAck();

            // Build PDU and raise event for emulator to dispatch
            byte[] pdu = new byte[unstuffedLen];
            unstuffed.CopyTo(pdu);
            // DF1 is a single-client transport, pass 'this' as client context (ignored by emulator)
            PduReceived?.Invoke(this, (pdu, this));
        }
        catch (Exception ex) { if (_isLoggingEnabled) Console.WriteLine("ProcessFrame error: " + ex.Message); }
    }

    // ─── Optimized ACK/NAK (zero allocation per call) ──────────────────────
    /// <summary>
    /// Sends DLE ACK (0x10 0x06) to acknowledge receipt of a valid frame.
    /// Uses static readonly array for zero allocation per call.
    /// </summary>
    private void SendAck()
    {
        try
        {
            lock (_txLock)
            {
                _port.Write(ACK_FRAME, 0, ACK_FRAME.Length);
                if (_isLoggingEnabled)
                    LogDelta("type=ACK → \n    TX: 10 06\n");
            }
        }
        catch (Exception ex) 
        { 
            // Client may have disconnected - just log if debugging needed
            // No need to increment counters as this is a normal disconnect scenario
            if (_isLoggingEnabled)
                Console.WriteLine($"[WARN] Failed to send ACK: {ex.Message}");
        }
    }

    /// <summary>
    /// Sends DLE NAK (0x10 0x15) to indicate a frame was invalid.
    /// Increments NAK received counter for diagnostic reporting.
    /// Uses static readonly array for zero allocation per call.
    /// </summary>
    private void SendNak()
    {
        try
        {
            _emulator.IncrementNakReceived();
            lock (_txLock)
            {
                _port.Write(NAK_FRAME, 0, NAK_FRAME.Length);
                if (_isLoggingEnabled)
                    LogDelta("type=NAK → \n    TX: 10 15\n");
            }
        }
        catch (Exception ex)
        {
            if (_isLoggingEnabled)
                Console.WriteLine($"[WARN] Failed to send NAK: {ex.Message}");
        }
    }

    // ─── Frame Builders (Optimized, Zero List/ToArray Overhead) ────────────

    /// <summary>
    /// Core frame transmission method.
    /// Builds complete DF1 frame: DLE STX | DLE-stuffed inner | DLE ETX | checksum
    /// 
    /// OPTIMIZATION NOTES:
    ///   - Single buffer allocation (max frame size pre-calculated)
    ///   - DLE stuffing writes directly to frame buffer (no intermediate list)
    ///   - Checksum calculated using MessageDecoder (table-driven CRC)
    ///   - Lock ensures thread-safe serial port access
    /// </summary>
    /// <param name="innerArray">Inner frame to send (without DLE framing)</param>
    private void SendRawFrame(byte[] innerArray)
    {
        _emulator.IncrementTotalPacketsSent();

        // Max frame size: DLE STX (2) + stuffed inner (worst case ×2) + DLE ETX (2) + CRC (2)
        int maxSize = 2 + innerArray.Length * 2 + 4;
        byte[] frameBuf = new byte[maxSize];
        int pos = 0;

        // DLE STX
        frameBuf[pos++] = 0x10;
        frameBuf[pos++] = 0x02;

        // DLE-stuffed inner — write directly to frameBuf
        int stuffedLen = MessageDecoder.ApplyDleStuffing(innerArray.AsSpan(), frameBuf.AsSpan(pos));
        pos += stuffedLen;

        // DLE ETX
        frameBuf[pos++] = 0x10;
        frameBuf[pos++] = 0x03;

        // Checksum (ETX appended internally by CalculateChecksum)
        ushort chk = MessageDecoder.CalculateChecksum(innerArray.AsSpan(), _checkSum);
        frameBuf[pos++] = (byte)(chk & 0xFF);
        if (_checkSum == CheckSumOptions.Crc)
            frameBuf[pos++] = (byte)((chk >> 8) & 0xFF);

        try
        {
            lock (_txLock)
            {
                _port.Write(frameBuf, 0, pos);
                if (_isLoggingEnabled && innerArray.Length > 2)
                {
                    LogDelta($"cmd=0x{innerArray[2]:X2} → \n    TX: ");
                    Console.WriteLine(BitConverter.ToString(frameBuf, 0, pos).Replace("-", " "));
                }
                else if (_isLoggingEnabled)
                {
                    LogDelta($"cmd=0x?? → \n    TX: ");
                    Console.WriteLine(BitConverter.ToString(frameBuf, 0, pos).Replace("-", " "));
                }
            }
        }
        catch (Exception ex)
        {
            if (_isLoggingEnabled) Console.WriteLine("Write error: " + ex.Message);
        }
    }

    // ─── Conditional Logging ───────────────────────────────────────────────
    /// <summary>
    /// Logs a message with timestamp and delta from previous log.
    /// Only allocates strings when logging is enabled (conditional).
    /// Thread-safe with lock to prevent interleaved log lines.
    /// </summary>
    /// <param name="msg">Message to log (prefixed with timestamp and delta)</param>
    private void LogDelta(string msg)
    {
        if (!_isLoggingEnabled) return;

        lock (_logLock)
        {
            var now = DateTime.Now;
            var dt = (now - _lastLog).TotalMilliseconds;
            _lastLog = now;
            Console.Write($"{now:HH:mm:ss.fff} (+{dt:0000} ms) {msg}");
        }
    }
}
