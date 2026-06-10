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
/// Abstract base class for DF1 transport implementations (Full-Duplex and Half-Duplex).
/// 
/// Provides common DF1 framing services:
///   - Serial port management (open/close, event handling)
///   - High-performance producer-consumer channel for receive processing
///   - Circular buffer with head/tail pointers (reduces memory copying)
///   - DLE stuffing/unstuffing via MessageDecoder
///   - CRC/BCC checksum calculation
///   - ACK/NAK frame helpers (static readonly for zero allocation)
///   - Health monitoring with periodic statistics logging
/// 
/// FRAME FORMAT (both directions):
///   DLE STX (0x10 0x02) | DLE-stuffed inner payload | DLE ETX (0x10 0x03) | Checksum (1 or 2 bytes)
/// 
/// INNER PAYLOAD FORMAT:
///   DST (1 byte) | SRC (1 byte) | CMD (1 byte) | STS (1 byte) | TNS_LO (1 byte) | TNS_HI (1 byte) | [FUNC (1 byte)] | [DATA...]
/// 
/// Derived classes must implement:
///   - ParseBuffer()   : protocol-specific frame detection (DLE STX for Full-Duplex, DLE ENQ+address for Half-Duplex)
///   - SendResponse()  : immediate (Full-Duplex) or queued (Half-Duplex) response transmission
/// </summary>
public abstract class DF1BaseTransport : ILinkTransport, IDisposable
{
    // ─── Shared Fields (copied from original DF1Transport) ─────────────────────
    protected readonly PCCCEmulator _emulator;
    protected readonly SerialPort _port;
    protected CheckSumOptions _checkSum;
    protected int _myNode;

    // High-performance producer-consumer channel (decouples I/O from processing)
    private readonly Channel<byte[]> _receiveChannel;
    private readonly CancellationTokenSource _processingCts;
    private Task _processingTask = Task.CompletedTask;

    // Circular buffer management (power-of-two size for mask optimization)
    protected byte[] _rxBuffer = new byte[8192];
    protected int _rxHead = 0;          // Read position (start of valid data)
    protected int _rxTail = 0;          // Write position (end of valid data)
    protected int _rxCount = 0;         // Number of bytes available
    protected volatile int _rxResetRequested = 0;  // Signal from error handler to reset buffer
    protected readonly object _txLock = new object();  // Lock ordering: always acquire _txLock before _rxLock

    // Health monitoring
    private Timer? _healthTimer;
    private long _lastFrameCount = 0;
    private DateTime _lastErrorTime = DateTime.MinValue;
    private readonly TimeSpan _errorThrottle = TimeSpan.FromSeconds(5);

    protected volatile int _isDisposing = 0;
    protected int _activeCallbacks = 0;

    // Static readonly frames for ACK/NAK (zero allocation per call)
    protected static readonly byte[] ACK_FRAME = new byte[] { 0x10, 0x06 };
    protected static readonly byte[] NAK_FRAME = new byte[] { 0x10, 0x15 };

    // ─── Events (ILinkTransport implementation) ──────────────────────────────
    /// <summary>
    /// Raised when a complete PDU (inner frame) has been received and parsed.
    /// The PDU is the unstuffed inner payload without DLE framing.
    /// Format: DST, SRC, CMD, STS, TNS_LO, TNS_HI, [FUNC], [DATA...]
    /// </summary>
    public event EventHandler<(byte[] pdu, object ClientContext)>? PduReceived;

    /// <summary>
    /// Human-readable name of this transport for logging.
    /// Derived classes may override to indicate specific variant.
    /// </summary>
    public virtual string Name => "DF1";

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

    // ─── Internal Methods for PCCCEmulator to Access Modem Status ───────────
    // These allow the emulator to read modem line states for diagnostic counters
    // without exposing the entire SerialPort object.
    internal bool GetCtsHolding() => _port.IsOpen && _port.CtsHolding;
    internal bool GetRtsEnable()  => _port.IsOpen && _port.RtsEnable;
    internal bool GetDsrHolding() => _port.IsOpen && _port.DsrHolding;
    internal bool GetCdHolding()  => _port.IsOpen && _port.CDHolding;
    internal bool GetDtrEnable()  => _port.IsOpen && _port.DtrEnable;

    // ─── Constructor ────────────────────────────────────────────────────────
    /// <summary>
    /// Initializes the base DF1 transport handler.
    /// </summary>
    /// <param name="emulator">Parent emulator instance (provides counters and logging)</param>
    /// <param name="portName">Serial port name (e.g., "COM2" or "/dev/ttyUSB0")</param>
    /// <param name="baudRate">Baud rate (e.g., 19200, 9600, 38400)</param>
    /// <param name="parity">Parity mode (None, Odd, Even)</param>
    protected DF1BaseTransport(PCCCEmulator emulator, string portName, int baudRate, Parity parity)
    {
        _emulator = emulator ?? throw new ArgumentNullException(nameof(emulator));
        _checkSum = _emulator.CheckSum;
        _myNode   = _emulator.MyNode;

        // Configure SerialPort with conservative timeouts (DF1 is half-duplex with ACK)
        _port = new SerialPort(portName, baudRate, parity, 8, StopBits.One)
        {
            ReadTimeout = 500,
            WriteTimeout = 500,
            ReadBufferSize = 65536,
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
    public virtual void Start()
    {
        // Port validation (Windows/Linux) – identical to original
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
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
            // Linux: normalize port name
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
            _port.DiscardInBuffer();

            // Start background processing task
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

        // Only after successful open, enable health monitor and call derived startup
        SetHealthStatsEnabled(!Logger.Enabled);
        OnStart();
    }

    /// <summary>
    /// Called after the port is opened and processing task started.
    /// Derived classes can override to add custom initialization.
    /// </summary>
    protected virtual void OnStart() { }

    /// <summary>
    /// Stops the DF1 transport handler gracefully.
    /// Waits for pending operations to complete before closing the port.
    /// Thread-safe and prevents data loss during shutdown.
    /// </summary>
    public virtual void Stop()
    {
        if (Interlocked.CompareExchange(ref _isDisposing, 1, 0) != 0) return;

        // Step 1: Stop accepting new data from serial port
        _port.DataReceived -= Port_DataReceived;

        // Step 2: Stop health monitoring timer
        SetHealthStatsEnabled(false);

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
            Logger.Warn(this, $"Consumer task shutdown warning: {ex.InnerException?.Message}");
        }
        catch (Exception ex)
        {
            Logger.Warn(this, $"[STOP] Error during task shutdown: {ex.Message}");
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
            Logger.Warn(this, $"[STOP] Error closing port: {ex.Message}");
        }
    }

    /// <summary>
    /// Sends a response PDU using DF1 framing.
    /// Derived classes must implement (immediate or queued).
    /// </summary>
    /// <param name="pdu">Inner frame PDU to send (without DLE framing)</param>
    /// <param name="clientContext">Client context (unused in DF1, kept for interface compatibility)</param>
    public abstract void SendResponse(byte[] pdu, object clientContext);

    // ─── Helpers for Derived Classes ──────────────────────────────────────────
    /// <summary>
    /// Sends DLE ACK (0x10 0x06) to acknowledge receipt of a valid frame.
    /// Uses static readonly array for zero allocation per call.
    /// </summary>
    protected void SendAck()
    {
        try
        {
            lock (_txLock)
            {
                _port.Write(ACK_FRAME, 0, ACK_FRAME.Length);
                Logger.Info(this, "type=ACK → TX: 10 06");
            }
        }
        catch (Exception ex) 
        { 
            Logger.Warn(this, $"Failed to send ACK: {ex.Message}");
        }
    }

    /// <summary>
    /// Sends DLE NAK (0x10 0x15) to indicate a frame was invalid.
    /// Increments NAK received counter for diagnostic reporting.
    /// Uses static readonly array for zero allocation per call.
    /// </summary>
    protected void SendNak()
    {
        try
        {
            _emulator.IncrementNakReceived();
            lock (_txLock)
            {
                _port.Write(NAK_FRAME, 0, NAK_FRAME.Length);
                Logger.Info(this, "type=NAK → TX: 10 15");
            }
        }
        catch (Exception ex) {
            Logger.Warn(this, $"Failed to send NAK: {ex.Message}");
        }
    }

    /// <summary>
    /// Builds a complete DF1 frame from an inner PDU.
    /// Includes DLE STX/ETX, DLE stuffing, and checksum.
    /// </summary>
    /// <param name="innerArray">Unstuffed inner payload (PDU)</param>
    /// <returns>Raw DF1 frame ready for transmission</returns>
    protected byte[] BuildRawFrame(byte[] innerArray)
    {
        _emulator.IncrementTotalPacketsSent();

        // Max frame size: DLE STX (2) + stuffed inner (worst case ×2) + DLE ETX (2) + CRC (2)
        int maxSize = 2 + innerArray.Length * 2 + 4;
        byte[] frameBuf = new byte[maxSize];
        int pos = 0;

        // DLE STX
        frameBuf[pos++] = 0x10;
        frameBuf[pos++] = 0x02;

        // DLE-stuffed inner
        int stuffedLen = MessageDecoder.ApplyDleStuffing(innerArray.AsSpan(), frameBuf.AsSpan(pos));
        pos += stuffedLen;

        // DLE ETX
        frameBuf[pos++] = 0x10;
        frameBuf[pos++] = 0x03;

        // Checksum
        ushort chk = MessageDecoder.CalculateChecksum(innerArray.AsSpan(), _checkSum);
        frameBuf[pos++] = (byte)(chk & 0xFF);
        if (_checkSum == CheckSumOptions.Crc)
            frameBuf[pos++] = (byte)((chk >> 8) & 0xFF);

        // Trim to actual length
        byte[] result = new byte[pos];
        Array.Copy(frameBuf, 0, result, 0, pos);
        return result;
    }

    /// <summary>
    /// Writes a raw frame to the serial port with basic hex logging.
    /// For detailed logging (DST, SRC, CMD, etc.), call LogRawFrame() before this.
    /// </summary>
    /// <param name="frame">Complete DF1 frame (including DLE STX/ETX, checksum)</param>
    protected void SendRawFrame(byte[] frame)
    {
        try
        {
            lock (_txLock)
            {
                _port.Write(frame, 0, frame.Length);
                Logger.Hex(this, "TX:", frame, frame.Length);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(this, "Write error: " + ex.Message);
        }
    }

    /// <summary>
    /// Logs detailed DF1 frame information (DST, SRC, CMD, TNS, FUNC, data length)
    /// as originally implemented in the monolithic DF1Transport.
    /// </summary>
    /// <param name="innerArray">Unstuffed inner payload (PDU)</param>
    /// <param name="rawFrame">Complete raw frame (for hex dump)</param>
    /// <param name="rawLength">Length of raw frame to log</param>
    protected void LogRawFrame(byte[] innerArray, byte[] rawFrame, int rawLength)
    {
        if (!Logger.Enabled || innerArray.Length < 6) return;

        int dst = innerArray[0];
        int src = innerArray[1];
        int cmd = innerArray[2];
        int tns = innerArray[4] | (innerArray[5] << 8);
        bool hasFunc = (innerArray.Length >= 7) && 
                       (cmd == 0x0F || cmd == 0x06 || cmd == 0x0A);
        int headerLen = hasFunc ? 7 : 6;
        int dataLen = Math.Max(0, innerArray.Length - headerLen);

        string funcStr = hasFunc ? $"0x{innerArray[6]:X2}" : "none";
        Logger.Info(this, $"dst={dst} src={src} cmd=0x{cmd:X2} tns={tns:X4} func={funcStr} dataLen={dataLen}");
    }

    /// <summary>
    /// Logs a received DF1 frame (hex dump + parsed fields) identical to the original monolithic implementation.
    /// </summary>
    /// <param name="rawFrame">Complete raw DF1 frame (including DLE STX/ETX and checksum)</param>
    /// <param name="pdu">Unstuffed inner payload (DST, SRC, CMD, STS, TNS, ...)</param>
    protected void LogReceivedFrame(byte[] rawFrame, byte[] pdu)
    {
        if (!Logger.Enabled || pdu.Length < 6) return;

        int cmd = pdu[2];
        int tns = pdu[4] | (pdu[5] << 8);
        bool hasFunc = pdu.Length >= 7 && (cmd == 0x0F || cmd == 0x06 || cmd == 0x0A);
        int headerLen = hasFunc ? 7 : 6;
        int dataLen = pdu.Length - headerLen;
        int func = hasFunc ? pdu[6] : 0;

        Logger.Info(this, $"dst={pdu[0]} src={pdu[1]} cmd=0x{cmd:X2} tns={tns:X4} func=0x{func:X2} dataLen={dataLen}");
    }

    /// <summary>
    /// Raises the PduReceived event.
    /// </summary>
    protected void RaisePduReceived(byte[] pdu, object context)
    {
        PduReceived?.Invoke(this, (pdu, context));
    }

    // ─── Health Monitoring ─────────────────────────────────────────────────
    /// <summary>
    /// Enables or disables the health monitor for this transport instance.
    /// When enabled, the health monitor is activated for visibility.
    /// When disabled, the health monitor is disabled to reduce overhead.
    /// </summary>
    /// <param name="enabled">True to enable logging, false for maximum performance</param>
    public void SetHealthStatsEnabled(bool enabled)
    {
        if (enabled)
        {
            _healthTimer ??= new Timer(_ => LogHealthStats(), null, 15000, 15000);
            Logger.Always(this, "Logging disabled — health monitor active");
        }
        else
        {
            _healthTimer?.Dispose();
            _healthTimer = null;
        }
    }

    private void LogHealthStats()
    {
        if (_isDisposing != 0) return;

        long currentFrames = _emulator.GetFramesProcessed();
        long delta = currentFrames - _lastFrameCount;
        _lastFrameCount = currentFrames;

        Logger.Always(this, $"DF1 Rate: {delta / 15,6}/s | " +
            $"Total: {currentFrames,10:N0} | " +
            $"Bad: {_emulator.GetBadPacketsDetected(),4:N0} | " +
            $"Memory: {GC.GetTotalMemory(false) / 1024,6:N0} KB");

        if (delta == 0 && currentFrames > 0)
        {
            Logger.Always(this, "No frames in last 15 s — check client connection");
        }
    }

    // ─── Serial Receive (PRODUCER) ─────────────────────────────────────────
    private void Port_DataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (_isDisposing != 0) return;
        Interlocked.Increment(ref _activeCallbacks);

        try
        {
            int bytesToRead = _port.BytesToRead;
            if (bytesToRead <= 0) return;

            byte[] buffer = new byte[bytesToRead];
            int bytesRead = _port.Read(buffer, 0, bytesToRead);

            if (bytesRead > 0)
            {
                byte[] exactBuffer = (bytesRead == bytesToRead)
                    ? buffer
                    : buffer[..bytesRead];

                if (!_receiveChannel.Writer.TryWrite(exactBuffer))
                {
                    _emulator.IncrementBadPacketsDetected();
                }
            }
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            Logger.Warn(this, $"Port_DataReceived error: {ex.Message}");
        }
        finally
        {
            Interlocked.Decrement(ref _activeCallbacks);
        }
    }

    private void Port_ErrorReceived(object sender, SerialErrorReceivedEventArgs e)
    {
        if (_isDisposing != 0) return;

        if (DateTime.Now - _lastErrorTime < _errorThrottle)
            return;
        _lastErrorTime = DateTime.Now;

        Logger.Always(this, $"Serial port error: {e.EventType}");
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
            Logger.Warn(this, $"Discard buffer failed: {ex.Message}");
        }

        _emulator.IncrementBadPacketsDetected();
    }

    private void Port_PinChanged(object sender, SerialPinChangedEventArgs e)
    {
        if (_isDisposing != 0) return;

        Logger.Info(this, $"[PIN] {e.EventType} - DCD: {_port.CDHolding}, CTS: {_port.CtsHolding}, DSR: {_port.DsrHolding}");
        _emulator.UpdateModemStatus();
    }

    // ─── Background Processing Task (CONSUMER) ─────────────────────────────
    private async Task ProcessReceiveChannelAsync()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
        }

        try
        {
            await foreach (var buffer in _receiveChannel.Reader.ReadAllAsync(_processingCts.Token))
            {
                if (Interlocked.CompareExchange(ref _rxResetRequested, 0, 1) == 1)
                {
                    _rxHead = 0;
                    _rxTail = 0;
                    _rxCount = 0;
                    Logger.Info(this, "Circular buffer reset due to error");
                }

                // Add data to circular buffer (grow if needed)
                int bytesToAdd = buffer.Length;
                if (_rxCount + bytesToAdd > _rxBuffer.Length)
                {
                    int newSize = Math.Max(_rxBuffer.Length * 2, _rxCount + bytesToAdd);
                    byte[] newBuffer = new byte[newSize];
                    if (_rxHead <= _rxTail)
                        Array.Copy(_rxBuffer, _rxHead, newBuffer, 0, _rxCount);
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

                if (_rxTail + bytesToAdd <= _rxBuffer.Length)
                    Array.Copy(buffer, 0, _rxBuffer, _rxTail, bytesToAdd);
                else
                {
                    int firstPart = _rxBuffer.Length - _rxTail;
                    Array.Copy(buffer, 0, _rxBuffer, _rxTail, firstPart);
                    Array.Copy(buffer, firstPart, _rxBuffer, 0, bytesToAdd - firstPart);
                }

                _rxTail = (_rxTail + bytesToAdd) % _rxBuffer.Length;
                _rxCount += bytesToAdd;

                ParseBuffer();  // Abstract method implemented by derived classes
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Warn(this, $"ProcessReceiveChannelAsync error: {ex.Message}");
        }
    }

    /// <summary>
    /// Parses complete DF1 frames from the circular buffer.
    /// Must be implemented by derived classes according to their protocol
    /// (Full-Duplex: DLE STX; Half-Duplex: DLE ENQ + address polling).
    /// </summary>
    protected abstract void ParseBuffer();

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _isDisposing, 1, 0) != 0) return;
        
        _processingCts?.Cancel();
        _processingCts?.Dispose();
        
        // Ensure port is stopped and closed
        Stop();
        
        GC.SuppressFinalize(this);
    }
}
