// SPDX-License-Identifier: GPL-3.0-or-later
// 
// DF1Comm - DF1 Protocol Library for .NET
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
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Runtime.InteropServices;

/// <summary>
/// DF1 Full-Duplex SLC 5/03 emulator, compatible with both RSLinx auto-browse
/// and the DF1Comm library client.
///
/// HIGH-PERFORMANCE OPTIMIZATIONS (without behavior change):
///   - Producer-Consumer pattern with System.Threading.Channels
///   - stackalloc for small frames (ACK, NAK)
///   - Conditional logging to eliminate string allocations in hot path
///   - Span-based frame building (zero allocation in TX path)
///   - Direct buffer allocation (no List/ToArray overhead)
///   - Thread-safe response cache for frequently accessed data
///
/// Frame format (both directions):
///   DLE STX | DST SRC CMD STS TNS_LO TNS_HI [FUNC] [DATA...] | DLE ETX | CHK
///
/// RSLinx auto-configure sequence:
///   1. ENQ (DLE 0x05)              → emulator replies ACK (DLE 0x06)
///   2. Get Diagnostic Status
///      (CMD=0x06, FNC=0x03)        → emulator replies with 24-byte status payload
///   3. Reset (CMD=0x01)            → emulator acknowledges the reset
///   4. Set Variables (CMD=0x0B)    → emulator acknowledges; no parameters changed
///
/// Response frame conventions:
///   - CMD 0x06 Get Status  : WITHOUT FUNC byte — DF1Comm reads ProcessorType
///                            from DataPackets[rTNS][9] = inner[9] = DATA[3] = 0x49.
///   - CMD 0x06 echo        : WITH FUNC byte (reflects data back unchanged).
///   - CMD 0x0F Read/Write  : WITHOUT FUNC byte — DF1Comm reads returned data
///                            starting at DataPackets[rTNS][6] = inner[6] = DATA[0].
///   - CMD 0x01 Reset       : WITH FUNC byte.
///   - Error responses      : WITH FUNC byte (cmd | 0x40, non-zero STS).
///
/// Checksum: BCC (1 byte) or CRC (2 bytes little-endian). Calculated over the
///           unstuffed payload only — CalculateChecksum appends ETX (0x03)
///           internally, so the caller must NOT pre-append DLE+ETX.
///
/// Key implementation notes:
///   - PlcMemory is thread-safe (ReaderWriterLockSlim inside).
///   - Frame parsing skips stuffed DLE pairs (0x10 0x10) to avoid false DLE ETX
///     detection when payload contains 0x10 bytes.
///   - Extended element addressing (element >= 255) is decoded correctly.
///   - _lastLog is an instance field; LogDelta is protected by _logLock to prevent
///     race between DataReceived and timer threads.
///   - Diagnostic counters are read with Interlocked.CompareExchange for consistency
///     with Interlocked.Increment writes.
///
/// SLC 5/03 compliance (Publication 1770-6.5.16):
///   - GetStatus byte 0 (mode/status flags) uses bits 6-7 only (edits active);
///     mode code is placed in bytes 18-19 as per Chapter 10 table.
///   - GetStatus catalog string is "5/03" per specification.
///   - GetStatus RAM size is 0x20 (16K words = 32 KB for 1747-L532E).
///   - GetStatus byte 24 added: directory corrupted flag + program owner.
///   - CMD 0x06 FNC 0x01 (read diagnostic counters) reply CMD is 0x46
///     (not 0x4A, which is the reply for CMD 0x0A).
///   - CMD 0x06 FNC 0x07 (reset diagnostic counters) resets all counters to zero
///     and replies with CMD 0x46 and no data.
///   - Change mode (CMD 0x0F FNC 0x80): modes 0x07/0x08/0x09 (TEST) are tracked
///     via _processorMode enum; status response reflects correct mode code per spec
///     (0x1E=RUN, 0x11=PROG, 0x17=TEST-Cont, etc.).
///
/// Additional SLC 5/03 compliance (AB Application Note, March 1995 + SLCCCD Rev 1.0):
///   - Diagnostic counters DF1 Full-Duplex layout follows the AB Application Note
///     (page 17) layout for "DF1 Full-Duplex size <=40 bytes":
///       Bytes 8-9   = ENQ packets sent (not "retried")
///       Bytes 10-11 = NAK packets received (not "normal poll last scan")
///       Bytes 12-13 = ENQ packets received (not "normal poll max scan")
///       Bytes 16-17 = no buffer space / NAK'd (not "unused 0x0000")
///       Bytes 20-27 = 00h unused × 8 (poll scan time fields removed — DF1 only)
///       Total response extended to 34 bytes (modem status + 32 counter bytes).
///   - CMD 0x0F FNC 0x94 (Read File Info) handler added.
///     Returns file size (4 bytes), element count (2 bytes), reserved, data type.
///   - CMD 0x0F FNC 0xAB (Bit Write): when target file type is I/O image
///     (0x8B output-by-slot, 0x8C input-by-slot), mask is ignored and data
///     is written directly per SLCCCD section 4.36 operation note.
/// </summary>
public class DF1Emulator : IDisposable
{
    private readonly SerialPort _port;

    // ─── HIGH-PERFORMANCE: Producer-Consumer Channel ─────────────────────────
    private readonly Channel<byte[]> _receiveChannel;
    private readonly CancellationTokenSource _processingCts;
    private Task _processingTask = Task.CompletedTask;

    // ─── Buffer Management ───────────────────────────────────────────────────
    private byte[] _rxBuffer = new byte[8192];  // Power of two for mask optimization
    private int _rxHead = 0;   // Read position (start of valid data)
    private int _rxTail = 0;   // Write position (end of valid data)
    private int _rxCount = 0;  // Number of bytes available (_rxTail - _rxHead with wrap)
    private volatile int _rxResetRequested = 0;  // Signal from error handler to reset circular buffer

    // Lock ordering: always acquire _rxLock before _txLock to avoid deadlock.
    private readonly object _txLock = new object();
    private readonly PlcMemory _memory;

    // ─── Response Cache (Thread-safe, reduces recomputation) ─────────────────
    private byte[] _cachedGetStatusPayload;
    private readonly object _cacheLock = new object();

    // ─── Conditional Logging (eliminates string allocations in hot path) ─────
    private bool _isLoggingEnabled = true;  // Can be set to false for max performance
    private DateTime _lastLog = DateTime.Now;
    private readonly object _logLock = new object();

    public CheckSumOptions CheckSum { get; set; } = CheckSumOptions.Crc;
    public int MyNode { get; set; } = 1;

    // Full processor mode tracking per Publication 1770-6.5.16 Chapter 10.
    // Mode code stored in bits 0-4 of byte 19 in GetStatus response.
    //   Local:  0x11=PROG, 0x1E=RUN
    //   Remote: 0x01=PROG, 0x06=RUN
    //   Test:   0x17=Cont, 0x18=Single, 0x19=Step
    private enum ProcessorMode : byte
    {
        LocalProg   = 0x11,
        RemoteProg  = 0x01,
        LocalRun    = 0x1E,
        RemoteRun   = 0x06,
        TestCont    = 0x17,
        TestSingle  = 0x18,
        TestStep    = 0x19,
    }
    private volatile int _processorModeRaw = (int)ProcessorMode.LocalRun;
    private ProcessorMode _processorMode
    {
        get => (ProcessorMode)_processorModeRaw;
        set => _processorModeRaw = (int)value;
    }
    private bool IsRunMode => _processorMode == ProcessorMode.LocalRun ||
                              _processorMode == ProcessorMode.RemoteRun;

    // ─── Diagnostic counters ─────────────────────────────────────────────────
    // Layout matches AB Application Note (1995) "DF1 Full-Duplex size <=40 bytes" table.
    // bytes 8-9 = ENQ sent, 10-11 = NAK received, 12-13 = ENQ received,
    // 16-17 = no buffer NAK'd. Bytes 20-27 = all 0x00 (DF1 full-duplex has no poll scan time fields).
    private int _totalPacketsSent         = 0;
    private int _totalPacketsReceived     = 0;
    private int _undeliveredPackets       = 0;
    private int _enqSent                  = 0;
    private int _nakReceived              = 0;
    private int _enqReceived              = 0;
    private int _badPacketsDetected       = 0;
    private int _noBufferNakd             = 0;
    private int _duplicatePacketsReceived = 0;
    private int _dcdRecoveryCount         = 0;
    private int _lostModemCount           = 0;
    private ushort _modemStatus = 0x001F;
    private Timer _timer;
    private Timer _waveformTimer;
    private int _isDisposing = 0;  // 0 = false, 1 = true (atomic flag for shutdown)
    private int _activeCallbacks = 0;

    // Performance metrics (optional, for debugging)
    private long _framesProcessed = 0;
    private long _lastFrameLog = 0;

    // Static readonly frames for ACK/NAK (zero allocation per call)
    private static readonly byte[] ACK_FRAME = new byte[] { 0x10, 0x06 };
    private static readonly byte[] NAK_FRAME = new byte[] { 0x10, 0x15 };

    // ─── Health Monitoring ───────────────────────────────────────────────────
    private Timer _healthTimer;
    private long _lastFrameCount = 0;

    // ─── Edge Case Handling ──────────────────────────────────────────────────
    private DateTime _lastErrorTime = DateTime.MinValue;
    private readonly TimeSpan _errorThrottle = TimeSpan.FromSeconds(5);

    // ─── Constructor ─────────────────────────────────────────────────────────
    /// <summary>
    /// Initializes the emulator but does not open the serial port.
    /// Timer is created but not started; it will be started in Start().
    /// </summary>
    public DF1Emulator(string portName, int baudRate, Parity parity)
    {
        _port = new SerialPort(portName, baudRate, parity, 8, StopBits.One)
        {
            ReadTimeout     = 500,
            WriteTimeout    = 500,
            ReadBufferSize  = 65536,
            WriteBufferSize = 65536
        };
        _port.DataReceived += Port_DataReceived;

        // Add error and pin change handlers for edge cases
        _port.ErrorReceived += Port_ErrorReceived;
        _port.PinChanged += Port_PinChanged;

        _memory = new PlcMemory();

        // Initialize high-performance channel
        _receiveChannel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
        {
            SingleReader = true,   // Only one consumer task
            SingleWriter = false,  // DataReceived may be called on multiple threads
            AllowSynchronousContinuations = false
        });
        _processingCts = new CancellationTokenSource();

        // Pre-cache GetStatus payload (mode changes will update it)
        _cachedGetStatusPayload = BuildGetStatusPayload();

        _timer = new Timer(_ => UpdateDateTime(), null, Timeout.Infinite, Timeout.Infinite);
        _waveformTimer = new Timer(_ => UpdateWaveform(), null, Timeout.Infinite, Timeout.Infinite);
        UpdateProcessorMode();

        // Start health monitoring (logs every 15 seconds)
        _healthTimer = new Timer(_ => LogHealthStats(), null, 15000, 15000);

        Console.WriteLine("[PERF] DF1Emulator initialized with optimized pipeline");
        Console.WriteLine($"       Channel mode: Producer-Consumer");
    }

    /// <summary>
    /// Handles serial port errors (buffer overflows, frame errors, etc.)
    /// Attempts to recover by discarding buffers when possible.
    /// </summary>
    private void Port_ErrorReceived(object sender, SerialErrorReceivedEventArgs e)
    {
        if (_isDisposing != 0) return;
        
        // Throttle error logging to avoid spam
        if (DateTime.Now - _lastErrorTime < _errorThrottle)
            return;
        _lastErrorTime = DateTime.Now;
        
        Console.WriteLine($"[ERR]  Serial port error: {e.EventType}");
        
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
            Console.WriteLine($"[ERR]  Discard buffer failed: {ex.Message}");
        }
        
        Interlocked.Increment(ref _badPacketsDetected);
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
            Console.WriteLine($"[PIN]  {e.EventType} - DCD: {_port.CDHolding}, CTS: {_port.CtsHolding}, DSR: {_port.DsrHolding}");
        }
        
        // Update modem status for diagnostic counters
        UpdateModemStatus();
    }
    
    /// <summary>
    /// Logs health statistics periodically for monitoring purposes.
    /// Shows frames per second, total frames, and memory usage.
    /// This method works even when logging is disabled.
    /// </summary>
    private void LogHealthStats()
    {
        if (_isDisposing != 0) return;

        long currentFrames = Interlocked.Read(ref _framesProcessed);
        long delta = currentFrames - _lastFrameCount;
        _lastFrameCount = currentFrames;
        
        // Timer interval 15 seconds - calculate rate per second
        long ratePerSec = delta / 15;
        
        // Always log health stats, regardless of logging setting
        // This ensures operators can monitor emulator health
        Console.WriteLine($"[MONI] Rate: {ratePerSec,4}/s | Total: {currentFrames,8:N0} | Memory: {GC.GetTotalMemory(false) / 1024,6:N0} KB");
        
        // Alert if no activity (potential stall)
        if (delta == 0 && currentFrames > 0)
        {
            Console.WriteLine($"[WARN] No communication detected in last 15 seconds. Check client connection.");
        }
    }

    /// <summary>
    /// Pre-build the GetStatus payload (without TNS and DST)
    ///
    /// Diagnostic Status response (CMD=0x06, FNC=0x03).
    /// Response CMD = 0x46 (0x06 | 0x40), sent WITHOUT FUNC byte.
    ///
    /// Inner frame layout (WithoutFunc):
    ///   [0]=DST [1]=SRC [2]=CMD [3]=STS [4]=TNS_LO [5]=TNS_HI [6]=DATA[0] ...
    ///
    /// DF1Comm reads ProcessorType from DataPackets[rTNS][9] = inner[9] = DATA[3].
    /// payload[3] = 0x49 (SLC 5/03) → ProcessorType = 0x49.
    ///
    /// Payload layout per Publication 1770-6.5.16 Chapter 10 (1747-L532):
    ///   Byte  0    : mode/status flags — bits 0-5 = 0, bit 6 = testing edits,
    ///                bit 7 = edits in processor. NOT the mode code.
    ///   Byte  1    : 0xEE — type extender
    ///   Byte  2    : 0x34 — extended interface type (DF1 full-duplex, port 0)
    ///   Byte  3    : 0x49 — extended processor type (1747-L534 rack, SLC 5/03)
    ///   Byte  4    : series/revision
    ///   Byte  5–15 : bulletin number "5/03" in ASCII, space-padded to 11 bytes
    ///   Byte 16–17 : major error word (0x0000 = no fault)
    ///   Byte 18    : processor mode status/control low byte — mode code bits 0-4
    ///                  0x11 = local PROG   0x1E = local RUN
    ///                  0x17 = TEST-cont    0x18 = TEST-single   0x19 = TEST-step
    ///   Byte 19    : processor mode status/control high byte — fault flags
    ///   Byte 20–21 : program ID
    ///   Byte 22    : RAM size in Kbytes — 0x10 for 1747-L532E (32K)
    ///   Byte 23    : flags (bits 2-7 = program owner node, 0x3F = no owner)
    ///                bit 0 = directory file corrupted
    /// </summary>
    private byte[] BuildGetStatusPayload()
    {
        byte[] payload = new byte[24];

        payload[0] = 0x00;
        payload[1] = 0xEE;
        payload[2] = 0x34;
        payload[3] = 0x49;
        payload[4] = 0x22;

        string catalog = "5/03";
        byte[] catBytes = System.Text.Encoding.ASCII.GetBytes(catalog);
        Array.Copy(catBytes, 0, payload, 5, catBytes.Length);
        for (int i = 5 + catBytes.Length; i < 16; i++) payload[i] = 0x20;

        payload[16] = 0x00;
        payload[17] = 0x00;
        payload[18] = (byte)_processorMode;
        payload[19] = 0x00;
        payload[20] = 0x00;
        payload[21] = 0x00;
        payload[22] = 0x20;
        payload[23] = 0x3F;

        return payload;
    }

    private void UpdateDateTime()
    {
        if (!IsRunMode) return;
        var now = DateTime.Now;

        // S2:37 = YYYY, S2:38 = MM, S2:39 = DD
        // S2:40 = HH,   S2:41 = MM, S2:42 = SS
        // PlcMemory.Write is thread-safe, so concurrent timer + receive is safe.
        // Only update time registers when in RUN mode — processor clock advances in RUN.
        _memory.Write(0x84, 2, 37, 0, 2, BitConverter.GetBytes((short)now.Year));
        _memory.Write(0x84, 2, 38, 0, 2, BitConverter.GetBytes((short)now.Month));
        _memory.Write(0x84, 2, 39, 0, 2, BitConverter.GetBytes((short)now.Day));
        _memory.Write(0x84, 2, 40, 0, 2, BitConverter.GetBytes((short)now.Hour));
        _memory.Write(0x84, 2, 41, 0, 2, BitConverter.GetBytes((short)now.Minute));
        _memory.Write(0x84, 2, 42, 0, 2, BitConverter.GetBytes((short)now.Second));
    }

    private void UpdateProcessorMode()
    {
        // S2:1 = file type 0x84, file number 2, element 1 (word offset 1), 2 bytes
        // Read current word to preserve high byte (bit flags)
        byte[] current = _memory.ReadRaw(0x84, 2, 2, 2, out int status);
        if (status == 0 && current.Length == 2)
        {
            // Low byte = mode code (bits 0-4), high byte unchanged
            current[0] = (byte)_processorMode;
            _memory.Write(0x84, 2, 1, 0, 2, current);
        }

        // Update cached response when mode changes
        lock (_cacheLock)
        {
            _cachedGetStatusPayload[18] = (byte)_processorMode;
        }
    }

    /// <summary>
    /// Periodically updates F8:0 (sine wave) and F8:1 (triangle wave) based on real-time.
    /// This ensures waveform continuity even after disconnection/reconnection.
    /// </summary>    
    private void UpdateWaveform()
    {
        if (!IsRunMode) return;

        // F8:0 - Sine wave (amplitude 100, period 2 seconds)
        double now = DateTime.UtcNow.TimeOfDay.TotalSeconds;
        double sinePhase = (now % 2.0) / 2.0 * (2.0 * Math.PI);
        float sineValue = (float)(100.0 * Math.Sin(sinePhase));
        _memory.Write(0x8A, 8, 0, 0, 4, BitConverter.GetBytes(sineValue));

        // F8:1 - Triangle wave (amplitude 100, period 4 seconds)
        double t = now % 4.0;
        float triValue;
        if (t < 2.0)
            triValue = (float)(-100.0 + (t / 2.0) * 200.0);
        else
            triValue = (float)(100.0 - ((t - 2.0) / 2.0) * 200.0);
        _memory.Write(0x8A, 8, 1, 0, 4, BitConverter.GetBytes(triValue));
    }

    /// <summary>
    /// Opens the serial port and starts the emulator.
    /// Starts background processing task for high throughput.
    /// </summary>
    public void Start()
    {
        // Port validation
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
            // Normalize port name for Linux (add /dev/ prefix if needed)
            string baseName = _port.PortName.Replace("/dev/", "");
            string fullPath = $"/dev/{baseName}";
            var ports = Directory.GetFiles("/dev", "tty*");
            if (ports.Contains(fullPath))
            {
                _port.PortName = fullPath;  // Normalize to full path
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
            _port.DiscardInBuffer();
            UpdateModemStatus();

            // Start background processing task
            _processingTask = Task.Run(ProcessReceiveChannelAsync);

            // Start the periodic timer for S2 date/time registers
            _timer.Change(0, 1000);
            // Start the waveform timer
            _waveformTimer.Change(0, 500);
            Console.WriteLine($"DF1 Emulator started on {_port.PortName}...");
            Console.WriteLine($"       Background processor: {_processingTask.Status}");
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
    /// Background task that processes received data from the channel.
    /// This decouples I/O from processing for better throughput.
    /// </summary>
    private async Task ProcessReceiveChannelAsync()
    {
        // Boost thread priority for real-time performance
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
                
                Interlocked.Increment(ref _framesProcessed);

                // Optional: Periodic performance logging
                long frames = _framesProcessed;
                if (_isLoggingEnabled && frames - _lastFrameLog >= 10000)
                {
                    _lastFrameLog = frames;
                    Console.WriteLine($"[PERF] Processed {frames} frames, GC.GetTotalMemory: {GC.GetTotalMemory(false) / 1024:N0} KB");
                }

                // Add data to circular buffer
                int bytesToAdd = buffer.Length;
                
                // Ensure we have enough space (resize if needed)
                if (_rxCount + bytesToAdd > _rxBuffer.Length)
                {
                    // Grow buffer by doubling
                    int newSize = Math.Max(_rxBuffer.Length * 2, _rxCount + bytesToAdd);
                    byte[] newBuffer = new byte[newSize];
                    
                    // Copy existing data to new buffer (linearize)
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
                
                // Copy new data to circular buffer
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
                
                ParseBuffer();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ProcessReceiveChannelAsync error: {ex.Message}");
        }
    }

    /// <summary>
    /// Stops the emulator gracefully, closes the serial port, and disposes resources.
    /// Waits for pending operations to complete before closing.
    /// Thread-safe and designed to prevent data loss during shutdown.
    /// </summary>
    public void Stop()
    {
        // Atomic check-and-set to prevent race condition on shutdown
        if (Interlocked.CompareExchange(ref _isDisposing, 1, 0) != 0) return;

        Console.WriteLine("[STOP] Stopping emulator gracefully...");

        // Step 1: Stop accepting new data from serial port
        _port.DataReceived -= Port_DataReceived;
        
        // Step 2: Stop health monitoring timer
        _healthTimer?.Dispose();
        
        // Step 3: Cancel the consumer task
        _processingCts?.Cancel();
        
        // Step 4: Stop timers
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        _waveformTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        
        // Step 5: Complete the channel (no more writes allowed)
        _receiveChannel.Writer.TryComplete();
        
        // Step 6: Wait for consumer task to finish processing pending items
        try
        {
            if (_processingTask != null && !_processingTask.IsCompleted)
            {
                Console.WriteLine("[STOP] Waiting for consumer task to complete...");
                _processingTask.Wait(TimeSpan.FromSeconds(3));
                Console.WriteLine($"[STOP] Consumer task status: {_processingTask.Status}");
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
        
        // Step 7: Wait for any active DataReceived callbacks
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Interlocked.CompareExchange(ref _activeCallbacks, 0, 0) > 0 && sw.ElapsedMilliseconds < 2000)
        {
            Thread.Sleep(10);
        }
        
        // Log if callbacks didn't complete
        if (Interlocked.CompareExchange(ref _activeCallbacks, 0, 0) > 0)
        {
            Console.WriteLine("[STOP] Warning: Some callbacks did not complete within timeout");
        }
        
        // Step 8: Close serial port
        try
        {
            if (_port.IsOpen)
            {
                _port.DiscardInBuffer();
                _port.DiscardOutBuffer();
                _port.Close();
                Console.WriteLine("[STOP] Serial port closed.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[STOP] Error closing port: {ex.Message}");
        }
        
        // Step 9: Report final statistics
        Console.WriteLine($"[STOP] Complete. Total frames processed: {_framesProcessed:N0}");
    }

    public void Dispose()
    {
        Stop();
        _processingCts?.Dispose();
        _timer?.Dispose();
        _waveformTimer?.Dispose();
        _port?.Dispose();
        GC.SuppressFinalize(this);
    }

    // ─── Serial receive (PRODUCER: minimal work, only reads bytes) ───────────
    private void Port_DataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (_isDisposing != 0) return;
        Interlocked.Increment(ref _activeCallbacks);

        try
        {
            int bytesToRead = _port.BytesToRead;
            if (bytesToRead <= 0) return;

            // Read directly into exact-sized buffer - one allocation, no pool overhead
            byte[] buffer = new byte[bytesToRead];
            int bytesRead = _port.Read(buffer, 0, bytesToRead);

            if (bytesRead > 0)
            {
                // Trim if Read() returned fewer bytes than BytesToRead
                byte[] exactBuffer = (bytesRead == bytesToRead)
                    ? buffer
                    : buffer[..bytesRead];  // slice, no extra copy

                // Non-blocking write to channel
                if (!_receiveChannel.Writer.TryWrite(exactBuffer))
                {
                    Interlocked.Increment(ref _badPacketsDetected);
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // Ignore during shutdown
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

    // ─── Frame parser ─────────────────────────────────────────────────────────
    private void ParseBuffer()
    {
        int chkBytes = CheckSum == CheckSumOptions.Bcc ? 1 : 2;
        int scanPos = _rxHead;
        int remaining = _rxCount;
        
        while (remaining > 0)
        {
            // Need at least 2 bytes for any valid frame start (DLE STX or ENQ)
            if (remaining < 2) break;
            
            byte b1 = _rxBuffer[scanPos];
            byte b2 = _rxBuffer[(scanPos + 1) % _rxBuffer.Length];
            
            // Handle standalone ENQ (DLE 0x05) — RSLinx auto-configure node probe
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
                // Scan for DLE ETX, skipping over stuffed 0x10 0x10 pairs in the payload.
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
                
                if (payloadLen == -1) break; // incomplete frame — wait for more bytes
                
                // Frame structure: DLE STX (2) + stuffed_payload (payloadLen) + DLE ETX (2) + checksum (chkBytes)
                int frameLen = 2 + payloadLen + 2 + chkBytes;
                
                if (frameLen > remaining) break; // checksum bytes not yet received
                
                // Extract frame bytes (linearize to contiguous array) - optimized with Array.Copy
                byte[] frame = new byte[frameLen];
                int spaceToEnd = _rxBuffer.Length - scanPos;
                if (spaceToEnd >= frameLen)
                {
                    // Frame does not wrap — single copy
                    Array.Copy(_rxBuffer, scanPos, frame, 0, frameLen);
                }
                else
                {
                    // Frame wraps — two copies
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

    // ─── ENQ handler ─────────────────────────────────────────────────────────
    private void HandleEnq()
    {
        // DLE ENQ (0x10 0x05): node-presence probe sent by RSLinx during auto-configure.
        // Reply with DLE ACK (0x10 0x06) to confirm this node is alive.
        Interlocked.Increment(ref _enqReceived); // track ENQ packets received
        SendAck();
    }

    // ─── Frame processor ─────────────────────────────────────────────────────
    private void ProcessFrame(byte[] rawFrame)
    {
        try
        {
            int chkBytes = CheckSum == CheckSumOptions.Bcc ? 1 : 2;
            if (rawFrame.Length < 6 + chkBytes) return;

            // Extract received checksum (little-endian for CRC)
            ushort receivedChk = chkBytes == 1
                ? rawFrame[rawFrame.Length - 1]
                : (ushort)(rawFrame[rawFrame.Length - 2] | (rawFrame[rawFrame.Length - 1] << 8));

            // Locate DLE ETX while skipping stuffed 0x10 0x10 pairs to avoid false detection.
            int etxPos = -1;
            for (int i = 2; i < rawFrame.Length - 1; i++)
            {
                if (rawFrame[i] == 0x10 && rawFrame[i + 1] == 0x10) { i++; continue; }
                if (rawFrame[i] == 0x10 && rawFrame[i + 1] == 0x03) { etxPos = i; break; }
            }
            if (etxPos == -1) return;

            int payloadLen = etxPos - 2;
            if (payloadLen <= 0) return;

            // Guard against oversized frames from corrupt data or line noise
            // DF1 spec: max payload 244 bytes (SLC 5/03), stuffed worst case × 2 = 488
            // Using 512 as safe upper bound
            if (payloadLen > 512)
            {
                if (_isLoggingEnabled)
                    Console.WriteLine($"[WARN] Oversized frame rejected: payloadLen={payloadLen}");
                Interlocked.Increment(ref _badPacketsDetected);
                Interlocked.Increment(ref _undeliveredPackets);
                SendNak();
                return;
            }

            // Unstuff directly from rawFrame to stackalloc — zero heap allocation
            Span<byte> unstuffed = stackalloc byte[payloadLen];
            int unstuffedLen = MessageDecoder.RemoveDleStuffing(rawFrame.AsSpan(2, payloadLen), unstuffed);
            unstuffed = unstuffed[..unstuffedLen];

            if (unstuffedLen < 6) return;

            // Verify checksum over the unstuffed payload only.
            ushort calc = MessageDecoder.CalculateChecksum(unstuffed, CheckSum);
            if (calc != receivedChk)
            {
                if (_isLoggingEnabled)
                    Console.WriteLine($"Checksum mismatch: calc=0x{calc:X4} recv=0x{receivedChk:X4} ({CheckSum})");
                Interlocked.Increment(ref _badPacketsDetected);
                Interlocked.Increment(ref _undeliveredPackets);
                SendNak();
                return;
            }

            // Valid packet received
            Interlocked.Increment(ref _totalPacketsReceived);

            int dst = unstuffed[0];
            int src = unstuffed[1];
            int cmd = unstuffed[2];
            // unstuffed[3] = STS (not used by emulator)
            int tns = unstuffed[4] | (unstuffed[5] << 8);

            // FUNC byte is optional – only present if unstuffed.Length >= 7
            int func = unstuffedLen >= 7 ? unstuffed[6] : 0;

            // ToArray only here because DispatchCommand and handlers expect byte[]
            // This is the only remaining allocation in ProcessFrame
            byte[] data = unstuffedLen > 7
                ? unstuffed[7..].ToArray()
                : Array.Empty<byte>();

            if (_isLoggingEnabled)
            {
                LogDelta($"\n    RX: ");
                Console.WriteLine(BitConverter.ToString(rawFrame).Replace("-", " "));
                Console.WriteLine($"    dst={dst} src={src} cmd=0x{cmd:X2} tns={tns} func=0x{func:X2} dataLen={data.Length}");
            }

            if (dst != MyNode && dst != 0xFF) return;

            // ACK before responding — required by DF1 full-duplex protocol.
            SendAck();

            // Dispatch on CMD (and FUNC where CMD alone is ambiguous)
            DispatchCommand(src, tns, cmd, func, data);
        }
        catch (Exception ex) { if (_isLoggingEnabled) Console.WriteLine("ProcessFrame error: " + ex.Message); }
    }

    // ─── Command dispatcher ──────────────────────────────────────────────────
    private void DispatchCommand(int src, int tns, int cmd, int func, byte[] data)
    {
        if (cmd == 0x06 && func == 0x00)
            // Diagnostic Loop – RSLinx auto‑detection echo
            SendFrameWithFunc(src, MyNode, 0x46, tns, func, 0x00, data);
        else if (cmd == 0x06 && func == 0x03)
            SendGetStatusResponse(src, tns);
        else if (cmd == 0x06 && func == 0x01)
            // Read diagnostic counters. Reply CMD = 0x46 (CMD 0x06 | 0x40).
            // (Not 0x4A, which is the reply for CMD 0x0A)
            SendDiagnosticCountersResponse(src, tns, replyCmd: 0x46);
        else if (cmd == 0x06 && func == 0x07)
        {
            // Reset diagnostic counters (Publication 1770-6.5.16, page 7-22).
            // Resets all counters to zero. Reply CMD = 0x46, no data.
            ResetDiagnosticCounters();
            SendFrameWithoutFunc(src, MyNode, 0x46, tns, func, 0x00, Array.Empty<byte>());
        }
        else if (cmd == 0x06 && func == 0x02)
        {
            // Echo/loopback — RSLinx diagnostic; reflect data back unchanged
            SendFrameWithFunc(src, MyNode, 0x46, tns, func, 0x00, data);
        }
        else if (cmd == 0x01)
        {
            // Reset — sent by RSLinx during auto-configure
            SendFrameWithFunc(src, MyNode, 0x41, tns, 0x00, 0x00, Array.Empty<byte>());
        }
        else if (cmd == 0x0B)
        {
            // Set Variables — RSLinx pushes communication parameters.
            // Acknowledge with success; no parameters are actually changed.
            SendFrameWithoutFunc(src, MyNode, 0x4B, tns, 0x00, 0x00, Array.Empty<byte>());
        }
        else if (cmd == 0x0F)
            DispatchFunctionCode(src, tns, func, data);
        else if (cmd == 0x0A)
        {
            // Diagnostic Counters request (DH+ style). Reply CMD = 0x4A.
            SendDiagnosticCountersResponse(src, tns, 0x4A);
        }
        else if (cmd == 0x67)
        {
            // Read Modified Data (treated as normal read for simplicity)
            HandleReadModifiedData(src, tns, data);
        }
        else
            SendErrorResponse(src, tns, cmd, func, 0x01);
    }

    private void DispatchFunctionCode(int src, int tns, int func, byte[] data)
    {
        switch (func)
        {
            case 0xA1: // Protected Typed Logical Read (2-address fields)
            case 0xA2: // Protected Typed Logical Read (3-address fields, with sub-element)
            case 0x68: // Protected Typed Logical Read (three address fields) – AB Application Note, page 6
                HandleReadRequest(src, tns, func, data);
                break;
            case 0xAA: // Protected Typed Logical Write (word)
            case 0xAB: // Protected Typed Logical Write (bit-masked)
                HandleWriteRequest(src, tns, func, data);
                break;
            case 0x94: // Read File Info — SLC 5/03 and 5/04 only
                HandleReadFileInfo(src, tns, data);
                break;
            case 0x80: // Change mode — SLC 500/5/03/5/04 (Publication 1770-6.5.16 page 7-5)
                // Mode codes per spec: 0x01=PROG (REM), 0x06=RUN (REM), 0x07=TEST continuous,
                // 0x08=TEST single scan, 0x09=TEST single step (SLC 5/03 supported)
                if (data != null && data.Length > 0)
                {
                    byte requestedMode = data[0];
                    _processorMode = requestedMode switch
                    {
                        0x01 => ProcessorMode.RemoteProg,
                        0x06 => ProcessorMode.RemoteRun,
                        0x07 => ProcessorMode.TestCont,
                        0x08 => ProcessorMode.TestSingle,
                        0x09 => ProcessorMode.TestStep,
                        _    => _processorMode // unknown mode: ignore
                    };
                    UpdateProcessorMode();
                    if (_isLoggingEnabled)
                        Console.WriteLine($"Mode changed to {_processorMode} (FNC=0x80, data=0x{requestedMode:X2})");
                }
                SendFrameWithoutFunc(src, MyNode, 0x4F, tns, func, 0x00, Array.Empty<byte>());
                break;
            case 0x3A: // Change mode — MicroLogix 1000 (data: 0x01=PROG, 0x02=RUN)
                if (data != null && data.Length > 0)
                {
                    byte requestedMode = data[0];
                    _processorMode = requestedMode switch
                    {
                        0x01 => ProcessorMode.RemoteProg,
                        0x02 => ProcessorMode.RemoteRun,
                        _    => _processorMode
                    };
                    UpdateProcessorMode();
                    if (_isLoggingEnabled)
                        Console.WriteLine($"Mode changed to {_processorMode} (FNC=0x3A, data=0x{requestedMode:X2})");
                }
                SendFrameWithoutFunc(src, MyNode, 0x4F, tns, func, 0x00, Array.Empty<byte>());
                break;
            case 0x11: // Get Edit Resource (secure sole access)
            case 0x12: // Return Edit Resource
            case 0x29: // Unrecognised function code sent by RSLinx during auto-configure
            case 0x52: // Download Completed
            case 0x88: // Execute Command List (download initialization)
                // DF1Comm sends data (16 bytes) as per DownloadProgramData.
                // No need to process; just acknowledge success.
                SendFrameWithoutFunc(src, MyNode, 0x4F, tns, func, 0x00, Array.Empty<byte>());
                break;
            default:
                SendErrorResponse(src, tns, 0x0F, func, 0x01);
                break;
        }
    }

    // ─── Optimized SendGetStatusResponse (uses cached payload) ───────────────
    private void SendGetStatusResponse(int dst, int tns)
    {
        byte[] payload;
        lock (_cacheLock)
        {
            // Clone cached payload (safe because TNS and DST change per request)
            payload = (byte[])_cachedGetStatusPayload.Clone();
        }
        SendFrameWithoutFunc(dst, MyNode, 0x46, tns, 0x03, 0x00, payload);
    }

    /// <summary>
    /// Diagnostic Counters response.
    /// - CMD 0x06 FNC 0x01 → replyCmd = 0x46
    /// - CMD 0x0A           → replyCmd = 0x4A
    ///
    /// Layout per AB Application Note (March 1995), page 17,
    /// "Reply for Data for DF1 Full-Duplex size <=40 bytes":
    ///   Bytes  0-1  : RS-232 modem line status (CTS/RTS/DSR/DCD/DTR bits)
    ///   Bytes  2-3  : total message packets sent
    ///   Bytes  4-5  : total message packets received
    ///   Bytes  6-7  : undelivered message packets
    ///   Bytes  8-9  : ENQuiry packets sent
    ///   Bytes 10-11 : NAK packets received
    ///   Bytes 12-13 : ENQ packets received
    ///   Bytes 14-15 : bad message packets received and NAK'd
    ///   Bytes 16-17 : no buffer space and NAK'd
    ///   Bytes 18-19 : duplicate message packets received
    ///   Bytes 20-21 : 00h (unused — poll scan times are DH485/half-duplex only)
    ///   Bytes 22-23 : DCD recover field
    ///   Bytes 24-25 : lost modem field
    ///   Bytes 26-33 : 00h (unused) × 8
    ///
    /// Total: 34 bytes (modem status word + 32 counter bytes).
    /// </summary>
    private void SendDiagnosticCountersResponse(int dst, int tns, int replyCmd)
    {
        UpdateModemStatus();

        // Read all counters atomically via Interlocked.
        int sent        = Interlocked.CompareExchange(ref _totalPacketsSent,         0, 0);
        int received    = Interlocked.CompareExchange(ref _totalPacketsReceived,     0, 0);
        int undelivered = Interlocked.CompareExchange(ref _undeliveredPackets,       0, 0);
        int enqSent     = Interlocked.CompareExchange(ref _enqSent,                 0, 0);
        int nakRecv     = Interlocked.CompareExchange(ref _nakReceived,              0, 0);
        int enqRecv     = Interlocked.CompareExchange(ref _enqReceived,             0, 0);
        int bad         = Interlocked.CompareExchange(ref _badPacketsDetected,       0, 0);
        int noBuf       = Interlocked.CompareExchange(ref _noBufferNakd,            0, 0);
        int dup         = Interlocked.CompareExchange(ref _duplicatePacketsReceived, 0, 0);
        int dcd         = Interlocked.CompareExchange(ref _dcdRecoveryCount,        0, 0);
        int lost        = Interlocked.CompareExchange(ref _lostModemCount,           0, 0);

        // 34 bytes total for DF1 full-duplex
        byte[] counters = new byte[34];

        void W(int idx, int val)
        {
            counters[idx]     = (byte)(val & 0xFF);
            counters[idx + 1] = (byte)((val >> 8) & 0xFF);
        }

        W(0,  _modemStatus); // Bytes 0-1:   RS-232 modem line status
        W(2,  sent);          // Bytes 2-3:   total packets sent
        W(4,  received);      // Bytes 4-5:   total packets received
        W(6,  undelivered);   // Bytes 6-7:   undelivered packets
        W(8,  enqSent);       // Bytes 8-9:   ENQ packets sent
        W(10, nakRecv);       // Bytes 10-11: NAK packets received
        W(12, enqRecv);       // Bytes 12-13: ENQ packets received
        W(14, bad);           // Bytes 14-15: bad packets / NAK'd
        W(16, noBuf);         // Bytes 16-17: no buffer space / NAK'd
        W(18, dup);           // Bytes 18-19: duplicate packets received
        // Bytes 20-21: 00h unused (DF1 has no poll scan time)
        W(22, dcd);           // Bytes 22-23: DCD recovery count
        W(24, lost);          // Bytes 24-25: lost modem count
                              // Bytes 26-33: 00h unused × 8

        SendFrameWithoutFunc(dst, MyNode, replyCmd, tns, 0x00, 0x00, counters);
    }

    /// <summary>
    /// Reset all diagnostic counters to zero (CMD=0x06, FNC=0x07).
    /// Publication 1770-6.5.16, page 7-22.
    /// </summary>
    private void ResetDiagnosticCounters()
    {
        Interlocked.Exchange(ref _totalPacketsSent,         0);
        Interlocked.Exchange(ref _totalPacketsReceived,     0);
        Interlocked.Exchange(ref _undeliveredPackets,       0);
        Interlocked.Exchange(ref _enqSent,                  0);
        Interlocked.Exchange(ref _nakReceived,               0);
        Interlocked.Exchange(ref _enqReceived,              0);
        Interlocked.Exchange(ref _badPacketsDetected,       0);
        Interlocked.Exchange(ref _noBufferNakd,             0);
        Interlocked.Exchange(ref _duplicatePacketsReceived, 0);
        Interlocked.Exchange(ref _dcdRecoveryCount,         0);
        Interlocked.Exchange(ref _lostModemCount,           0);
        Console.WriteLine("Diagnostic counters reset.");
    }

    /// <summary>
    /// Read Modified Data (CMD=0x67) – simplified as normal read.
    /// </summary>
    private void HandleReadModifiedData(int dst, int tns, byte[] payload)
    {
        if (payload == null || payload.Length < 5)
        {
            SendErrorResponse(dst, tns, 0x67, 0x00, 0x01);
            return;
        }
        int fileNumber   = payload[0];
        int fileType     = payload[1];
        int offsetWords  = payload[2] | (payload[3] << 8);
        int bytesToRead  = payload[4];
        int byteOffset   = offsetWords * 2;

        int fileSize = _memory.GetFileSize(fileType, fileNumber);
        if (byteOffset + bytesToRead > fileSize)
        {
            SendErrorResponse(dst, tns, 0x67, 0x00, 0x10);
            return;
        }

        byte[] data = _memory.ReadRaw(fileType, fileNumber, byteOffset, bytesToRead, out int status);
        if (status != 0)
        {
            SendErrorResponse(dst, tns, 0x67, 0x00, 0x10);
            return;
        }
        SendFrameWithoutFunc(dst, MyNode, 0xA7, tns, 0x00, 0x00, data);
    }

    /// <summary>
    /// Read File Info (CMD=0x0F, FNC=0x94).
    /// SLC 5/03 and SLC 5/04 only. Used by RSLinx to enumerate data files.
    ///
    /// Command format (AB Application Note, March 1995, section 6.5):
    ///   FNC = 0x94
    ///   mask = 0x06  (2 bytes follow: major file type + file number)
    ///   major file type = 0x80 (data table file)
    ///   file number = 0x00–0xFF
    ///
    /// Reply data (9 bytes on success):
    ///   Bytes 0-3 : file size in bytes (32-bit little-endian)
    ///   Bytes 4-5 : element count (16-bit little-endian)
    ///   Byte  6   : element count high byte (reserved — repeat of byte 5 per doc)
    ///   Byte  7   : reserved (0x00)
    ///   Byte  8   : data type byte (0x84=status, 0x85=bit, 0x86=timer, etc.)
    ///
    /// Error codes:
    ///   STS=0x00            success
    ///   STS=0x10            illegal format (wrong mask or major file type)
    ///   STS=0x50            bad address / file doesn't exist
    ///   STS=0xF0 EXT=0x1B   file protection error + owner node address byte
    /// </summary>
    private void HandleReadFileInfo(int dst, int tns, byte[] payload)
    {
        // Minimum: mask(1) + majorType(1) + fileNumber(1) = 3 bytes
        if (payload == null || payload.Length < 3)
        {
            SendErrorResponse(dst, tns, 0x0F, 0x94, 0x10);
            return;
        }

        byte mask         = payload[0];
        byte majorType    = payload[1];
        byte fileNumber   = payload[2];

        // Validate: mask must be 0x06, major type must be 0x80 (data table)
        if (mask != 0x06 || majorType != 0x80)
        {
            SendErrorResponse(dst, tns, 0x0F, 0x94, 0x10);
            return;
        }

        if (_isLoggingEnabled) Console.WriteLine($"    ReadFileInfo fileNumber={fileNumber}");

        // Ask PlcMemory for the file type code and size for this file number.
        if (!_memory.GetFileInfo(fileNumber, out int fileType, out int fileSize, out int elements))
        {
            // File number not found / not initialised
            SendErrorResponse(dst, tns, 0x0F, 0x94, 0x50);
            return;
        }

        byte[] reply = new byte[9];
        // Bytes 0-3: file size in bytes (32-bit little-endian)
        reply[0] = (byte)(fileSize & 0xFF);
        reply[1] = (byte)((fileSize >> 8) & 0xFF);
        reply[2] = (byte)((fileSize >> 16) & 0xFF);
        reply[3] = (byte)((fileSize >> 24) & 0xFF);
        // Bytes 4-5: element count
        reply[4] = (byte)(elements & 0xFF);
        reply[5] = (byte)((elements >> 8) & 0xFF);
        // Byte 6: reserved (high byte of count per doc)
        reply[6] = reply[5]; // repeat of byte 5 per AB Application Note
        // Byte 7: reserved
        reply[7] = 0x00;
        // Byte 8: data type code
        reply[8] = (byte)fileType;

        SendFrameWithoutFunc(dst, MyNode, 0x4F, tns, 0x94, 0x00, reply);
    }


    /// Response CMD = 0x4F, WITHOUT FUNC byte.
    /// DF1Comm reads returned data starting at DataPackets[rTNS][6] = inner[6] = DATA[0].
    ///
    /// Request payload layout (from DF1Comm ReadRawData):
    ///   [0]    = bytesToRead
    ///   [1]    = fileNumber
    ///   [2]    = fileType
    ///   [3]    = element  (255 = extended: element in [4..5], then subElement follows)
    ///            OR element (0–254) directly
    ///   [4]    = subElement (func 0xA2 only; or element low byte if [3]==255)
    ///   [5]    = element high byte (if [3]==255)
    ///
    /// Supports extended element addressing (element >= 255) by decoding 0xFF followed by two bytes.
    /// </summary>
    private void HandleReadRequest(int dst, int tns, int func, byte[] payload)
    {
        if (payload == null || payload.Length < 4)
        {
            SendErrorResponse(dst, tns, 0x0F, func, 0x01);
            return;
        }

        int bytesToRead = payload[0];
        int fileNumber  = payload[1];
        int fileType    = payload[2];

        // Decode element with extended addressing support
        int element     = payload[3];
        int payloadIdx  = 4;
        if (element == 0xFF && payload.Length > payloadIdx + 1)
        {
            element = payload[payloadIdx] | (payload[payloadIdx + 1] << 8);
            payloadIdx += 2;
        }

        // Decode subElement (0xA2 only)
        int subElement = 0;
        if (func == 0xA2 && payload.Length > payloadIdx)
        {
            if (payload[payloadIdx] == 0xFF && payload.Length > payloadIdx + 2)
            {
                subElement = payload[payloadIdx + 1] | (payload[payloadIdx + 2] << 8);
            }
            else
            {
                subElement = payload[payloadIdx];
            }
        }

        int bpe = _memory.GetBytesPerElement(fileType, fileNumber);
        int byteOffset = element * bpe + subElement * 2;

        if (_isLoggingEnabled)
        {
            string extNote = (payload[3] == 0xFF) ? " (ext)" : "";
            Console.WriteLine($"    Read func=0x{func:X2} fileType=0x{fileType:X2} file={fileNumber} elem={element}{extNote} sub={subElement} bytes={bytesToRead}");
        }

        byte[] data = _memory.ReadRaw(fileType, fileNumber, byteOffset, bytesToRead, out int status);
        if (status == 2) { SendErrorResponse(dst, tns, 0x0F, func, 0x50); return; }
        if (status == 3) { SendErrorResponse(dst, tns, 0x0F, func, 0x10); return; }
        if (status != 0) { SendErrorResponse(dst, tns, 0x0F, func, 0x10); return; }

        SendFrameWithoutFunc(dst, MyNode, 0x4F, tns, func, 0x00, data);
    }

    /// <summary>
    /// Handle Protected Typed Logical Write (0xAA) and Bit Write (0xAB).
    /// Response CMD = 0x4F with empty data on success, WITHOUT FUNC byte.
    ///
    /// Request payload layout (from DF1Comm WriteRawData):
    ///
    ///   func 0xAA — word write:
    ///     [0] = bytesToWrite
    ///     [1] = fileNumber
    ///     [2] = fileType
    ///     [3] = element  (255 = extended: [4..5] = element 16-bit, then subElement)
    ///     [4] = subElement (or element low byte if [3]==255)
    ///     [5..] = data bytes (word write) or mask + value (bit write)
    ///
    ///   func 0xAB — bit-masked write:
    ///     [0] = 2  (one word operation)
    ///     [1] = fileNumber
    ///     [2] = fileType
    ///     [3] = element  (255 = extended)
    ///     [4] = subElement (or element low byte if [3]==255)
    ///     [5] = mask low byte
    ///     [6] = mask high byte
    ///     [7] = value low byte   (bits to set where mask bit = 1)
    ///     [8] = value high byte
    ///
    /// Supports extended element addressing (element >= 255).
    /// For I/O image file types (0x8B output-by-slot, 0x8C input-by-slot), the mask is ignored
    /// and data is written directly (SLCCCD section 4.36).
    /// </summary>
    private void HandleWriteRequest(int dst, int tns, int func, byte[] payload)
    {
        if (payload == null || payload.Length < 5)
        {
            SendErrorResponse(dst, tns, 0x0F, func, 0x01);
            return;
        }

        int bytesToWrite = payload[0];
        int fileNumber   = payload[1];
        int fileType     = payload[2];

        // Decode element with extended addressing support
        int element    = payload[3];
        int payloadIdx = 4;
        if (element == 0xFF && payload.Length > payloadIdx + 1)
        {
            element = payload[payloadIdx] | (payload[payloadIdx + 1] << 8);
            payloadIdx += 2;
        }

        if (payload.Length <= payloadIdx)
        {
            SendErrorResponse(dst, tns, 0x0F, func, 0x01);
            return;
        }

        // Decode subElement with extended addressing support
        int subElement = payload[payloadIdx];
        payloadIdx++;
        if (subElement == 0xFF && payload.Length > payloadIdx + 1)
        {
            subElement = payload[payloadIdx] | (payload[payloadIdx + 1] << 8);
            payloadIdx += 2;
        }

        if (_isLoggingEnabled)
        {
            string extNote = (payload[3] == 0xFF) ? " (ext)" : "";
            Console.WriteLine($"    Write func=0x{func:X2} fileType=0x{fileType:X2} file={fileNumber} elem={element}{extNote} sub={subElement} bytes={bytesToWrite}");
        }

        if (func == 0xAB)
        {
            // Bit-masked write requires mask (2 bytes) and value (2 bytes) after header
            if (payload.Length < payloadIdx + 4)
            {
                SendErrorResponse(dst, tns, 0x0F, func, 0x01);
                return;
            }

            int mask  = payload[payloadIdx]     | (payload[payloadIdx + 1] << 8);
            int value = payload[payloadIdx + 2] | (payload[payloadIdx + 3] << 8);

            int bpe        = _memory.GetBytesPerElement(fileType, fileNumber);
            int byteOffset = element * bpe + subElement * 2;

            // For I/O image file types (0x8B output-by-slot, 0x8C input-by-slot), mask is ignored (direct write)
            bool isIoFile = fileType == 0x8B || fileType == 0x8C;
            if (isIoFile)
            {
                // Direct write — no read-modify-write, no mask applied
                byte[] directData = { (byte)(value & 0xFF), (byte)((value >> 8) & 0xFF) };
                bool okDirect = _memory.Write(fileType, fileNumber, element, subElement, 2, directData);
                if (!okDirect) { SendErrorResponse(dst, tns, 0x0F, func, 0x10); return; }
            }
            else
            {
                // Standard read-modify-write: only the bits set in mask are affected
                byte[] current = _memory.ReadRaw(fileType, fileNumber, byteOffset, 2, out int readStatus);
                if (readStatus != 0) { SendErrorResponse(dst, tns, 0x0F, func, 0x10); return; }

                int word = current[0] | (current[1] << 8);
                word = (word & ~mask) | (value & mask);
                byte[] newData = { (byte)(word & 0xFF), (byte)(word >> 8) };

                bool ok = _memory.Write(fileType, fileNumber, element, subElement, 2, newData);
                if (!ok) { SendErrorResponse(dst, tns, 0x0F, func, 0x10); return; }
            }
        }
        else
        {
            // Word write (0xAA): data bytes follow immediately after the address header
            if (payload.Length < payloadIdx + bytesToWrite)
            {
                SendErrorResponse(dst, tns, 0x0F, func, 0x01);
                return;
            }

            byte[] writeData = new byte[bytesToWrite];
            Array.Copy(payload, payloadIdx, writeData, 0, bytesToWrite);
            bool ok = _memory.Write(fileType, fileNumber, element, subElement, bytesToWrite, writeData);
            if (!ok) { SendErrorResponse(dst, tns, 0x0F, func, 0x10); return; }
        }

        SendFrameWithoutFunc(dst, MyNode, 0x4F, tns, func, 0x00, Array.Empty<byte>());
    }

    // ─── Frame builders (optimized, zero List/ToArray overhead) ───────────────

    /// <summary>
    /// Send a DF1 response frame WITH FUNC byte.
    /// Inner payload: DST SRC CMD STS TNS_LO TNS_HI FUNC [DATA...]
    /// Used for echo (CMD 0x06/0x02), Reset (CMD 0x01), and error responses.
    /// Optimized: direct array allocation, no List/ToArray overhead.
    /// </summary>
    private void SendFrameWithFunc(int dstNode, int srcNode, int cmd, int tns,
                                   int func, byte status, byte[] data)
    {
        int dataLen = data?.Length ?? 0;
        byte[] inner = new byte[7 + dataLen];
        inner[0] = (byte)dstNode;
        inner[1] = (byte)srcNode;
        inner[2] = (byte)cmd;
        inner[3] = status;
        inner[4] = (byte)(tns & 0xFF);
        inner[5] = (byte)((tns >> 8) & 0xFF);
        inner[6] = (byte)func;
        if (dataLen > 0) data!.CopyTo(inner, 7);
        SendRawFrame(inner, cmd, func, status, tns);
    }

    /// <summary>
    /// Send a DF1 response frame WITHOUT FUNC byte.
    /// Inner payload: DST SRC CMD STS TNS_LO TNS_HI [DATA...]
    /// Used for Get Status, Read, Write, mode-change, and Set Variables responses.
    /// DF1Comm reads returned data starting at DataPackets[rTNS][6] = inner[6] = DATA[0].
    /// Optimized: direct array allocation, no List/ToArray overhead.
    /// </summary>
    private void SendFrameWithoutFunc(int dstNode, int srcNode, int cmd, int tns,
                                      int func, byte status, byte[] data)
    {
        int dataLen = data?.Length ?? 0;
        byte[] inner = new byte[6 + dataLen];
        inner[0] = (byte)dstNode;
        inner[1] = (byte)srcNode;
        inner[2] = (byte)cmd;
        inner[3] = status;
        inner[4] = (byte)(tns & 0xFF);
        inner[5] = (byte)((tns >> 8) & 0xFF);
        if (dataLen > 0) data!.CopyTo(inner, 6);
        SendRawFrame(inner, cmd, func, status, tns);
    }

    /// <summary>
    /// Build and transmit the final serial frame:
    ///   DLE STX | DLE-stuffed(inner) | DLE ETX | checksum
    /// Checksum is computed over innerArray only; CalculateChecksum appends ETX internally.
    /// Optimized: single buffer allocation, no List/ToArray overhead.
    /// </summary>
    private void SendRawFrame(byte[] innerArray, int cmd, int func, byte status, int tns)
    {
        Interlocked.Increment(ref _totalPacketsSent);

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
        ushort chk = MessageDecoder.CalculateChecksum(innerArray.AsSpan(), CheckSum);
        frameBuf[pos++] = (byte)(chk & 0xFF);
        if (CheckSum == CheckSumOptions.Crc)
            frameBuf[pos++] = (byte)((chk >> 8) & 0xFF);

        try
        {
            lock (_txLock)
            {
                _port.Write(frameBuf, 0, pos);
                if (_isLoggingEnabled)
                {
                    LogDelta($"cmd=0x{cmd:X2} tns={tns} func=0x{func:X2} sts=0x{status:X2} → \n    TX: ");
                    Console.WriteLine(BitConverter.ToString(frameBuf, 0, pos).Replace("-", " "));
                }
            }
        }
        catch (Exception ex)
        {
            if (_isLoggingEnabled) Console.WriteLine("Write error: " + ex.Message);
        }
    }

    /// <summary>
    /// Optimized SendAck using static readonly array (zero allocation per call)
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
        catch { }
    }

    /// <summary>
    /// Optimized SendNak using static readonly array (zero allocation per call)
    /// </summary>
    private void SendNak()
    {
        try
        {
            lock (_txLock)
            {
                _port.Write(NAK_FRAME, 0, NAK_FRAME.Length);

                if (_isLoggingEnabled)
                    LogDelta("type=NAK → \n    TX: 10 15\n");
            }
        }
        catch { }
    }

    // Error response always includes FUNC byte so the client can identify which
    // command failed (cmd | 0x40 sets the reply bit; non-zero STS signals the error).
    private void SendErrorResponse(int dst, int tns, int cmd, int func, byte status)
        => SendFrameWithFunc(dst, MyNode, cmd | 0x40, tns, func, status, Array.Empty<byte>());

    /// <summary>
    /// Updates the modem status bits for diagnostic counters.
    /// Always reads actual hardware state for accurate diagnostic counters.
    /// </summary>
    private void UpdateModemStatus()
    {
        if (!_port.IsOpen) return;
        ushort status = 0;
        if (_port.CtsHolding) status |= 0x0001;
        if (_port.RtsEnable)  status |= 0x0002;
        if (_port.DsrHolding) status |= 0x0004;
        if (_port.CDHolding)  status |= 0x0008;
        if (_port.DtrEnable)  status |= 0x0010;
        _modemStatus = status;
    }

    /// <summary>
    /// Conditional logging - only allocates strings when logging is enabled
    /// </summary>
    private void LogDelta(string msg)
    {
        if (!_isLoggingEnabled) return;

        lock (_logLock)
        {
            var now = DateTime.Now;
            var dt  = (now - _lastLog).TotalMilliseconds;
            _lastLog = now;
            Console.Write($"{now:HH:mm:ss.fff} (+{dt:0000} ms) {msg}");
        }
    }

    /// <summary>
    /// Enable/disable logging at runtime for maximum performance
    /// </summary>
    public void SetLoggingEnabled(bool enabled)
    {
        _isLoggingEnabled = enabled;
        if (!enabled)
        {
            Console.WriteLine("[PERF] Logging disabled for maximum throughput");
        }
    }
}
