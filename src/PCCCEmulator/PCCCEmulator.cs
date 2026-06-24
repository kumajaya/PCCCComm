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
using System.Collections.Concurrent;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// PCCC emulator coordinator.
/// 
/// This class is the main coordinator that:
///   1. Manages shared resources (PlcMemory, mode, cache, timers)
///   2. Dispatches PCCC commands to PlcMemory
///   3. Delegates transport-specific I/O to ILinkTransport implementations
/// 
/// Supported transports:
///   - DF1 (serial) via DF1BaseTransport (default, fully implemented)
///   - DH485 via serial (partial tested)
///   - EtherNet/IP (EIP/PCCC) via TCP (fully implemented)
///   - CSP (Client Server Protocol) via TCP (implemented)
/// 
/// FRAME FORMAT (DF1 mode only - other transports use different framing):
///   DLE STX | DST SRC CMD STS TNS_LO TNS_HI [FUNC] [DATA...] | DLE ETX | CHK
/// 
/// RSLinx auto-configure sequence:
///   1. ENQ (DLE 0x05)              → emulator replies ACK (DLE 0x06)
///   2. Get Diagnostic Status (CMD=0x06, FNC=0x03) → 24-byte status payload
///   3. Reset (CMD=0x01)            → emulator acknowledges
///   4. Set Variables (CMD=0x0B)    → emulator acknowledges
/// 
/// RESPONSE FRAME CONVENTIONS:
///   - CMD 0x06 Get Status  : WITHOUT FUNC byte — DF1Comm reads ProcessorType
///                            from DataPackets[rTNS][9] = inner[9] = DATA[3] = 0x49.
///   - CMD 0x06 echo        : WITH FUNC byte (reflects data back unchanged).
///   - CMD 0x0F Read/Write  : WITHOUT FUNC byte — DF1Comm reads returned data
///                            starting at DataPackets[rTNS][6] = inner[6] = DATA[0].
///   - CMD 0x01 Reset       : WITH FUNC byte.
///   - Error responses      : WITH FUNC byte (cmd | 0x40, non-zero STS).
/// 
/// SLC 5/03 COMPLIANCE (Publication 1770-6.5.16):
///   - GetStatus byte 0 (mode/status flags) uses bits 6-7 only (edits active);
///     mode code is placed in bytes 18-19 as per Chapter 10 table.
///   - GetStatus catalog string is "5/03" per specification.
///   - GetStatus RAM size is 0x20 (32 KB for 1747-L532E).
///   - CMD 0x06 FNC 0x01 (read diagnostic counters) reply CMD is 0x46
///     (not 0x4A, which is the reply for CMD 0x0A).
///   - CMD 0x06 FNC 0x07 (reset diagnostic counters) resets all counters to zero
///     and replies with CMD 0x46 and no data.
///   - Change mode (CMD 0x0F FNC 0x80): modes 0x07/0x08/0x09 (TEST) are tracked
///     via _processorMode enum; status response reflects correct mode code.
/// 
/// ADDITIONAL SLC 5/03 FEATURES:
///   - CMD 0x0F FNC 0x94 (Read File Info) returns file size (4 bytes),
///     element count (2 bytes), reserved, data type.
///   - CMD 0x0F FNC 0xAB (Bit Write): when target file type is I/O image
///     (0x8B output-by-slot, 0x8C input-by-slot), mask is ignored and data
///     is written directly per SLCCCD section 4.36 operation note.
///   - Extended element addressing (element >= 255) is decoded correctly
///     using 0xFF followed by two-byte value.
/// </summary>
public class PCCCEmulator : IDisposable
{
    // ─── Core Components ──────────────────────────────────────────────────────
    private readonly PlcMemory _memory;
    private byte[]? _directoryBytes;
    private readonly ConcurrentDictionary<ushort, (int FileType, int FileNumber)> _openFiles = new();
    private ushort _nextTag = 1;  // start from 1, avoid 0 and 0xFFFF
    private readonly object _tagLock = new();
    private ILinkTransport? _transport;

    // ─── Transport Mode ───────────────────────────────────────────────────────
    public enum TransportMode
    {
        DF1,      // Serial DF1 full-duplex (default, fully implemented)
        UIC,      // DH485 via 1747-UIC (implemented)
        EIP,      // EtherNet/IP (EIP/PCCC) via TCP (fully implemented)
        DF1Slave, // DF1 half-duplex slave (RS-485 multi-drop)
        CSP       // CSP (Client Server Protocol)
    }

    private readonly TransportMode _mode;

    public enum EmulationFamily
    {
        SlcMicroLogix,   // default, type extender 0xEE, catalog "5/04"
        Plc5,            // processor type low nibble 0x?E (e.g., 0xBE), catalog "PLC-5"
        Ml1400           // MicroLogix 1400 (1766-LEC), processor type 0x9F
    }

    private EmulationFamily _family = EmulationFamily.SlcMicroLogix;

    public EmulationFamily Family
    {
        get => _family;
        set
        {
            _family = value;
            // Regenerate GetStatus payload cache when family changes
            lock (_cacheLock)
            {
                _cachedGetStatusPayload = BuildGetStatusPayload();
            }
            Logger.Always(this, $"Emulation family set to {_family}");
        }
    }

    // ─── Shared Configuration ─────────────────────────────────────────────────
    private CheckSumOptions _checkSum = CheckSumOptions.Crc;
    private int _myNode = 1;

    // ─── Response Cache (Thread-safe, reduces recomputation) ─────────────────
    // Rebuilt whenever processor mode changes via UpdateProcessorMode().
    private byte[] _cachedGetStatusPayload;
    private readonly object _cacheLock = new object();

    // ─── Processor Mode Tracking ──────────────────────────────────────────────
    // Full processor mode tracking per Publication 1770-6.5.16 Chapter 10.
    // Mode code is stored in byte 18 of the GetStatus response.
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

    // Stored as int so Interlocked operations can be used for thread-safe access.
    private volatile int _processorModeRaw = (int)ProcessorMode.LocalRun;

    private ProcessorMode ProcessorModeValue
    {
        get => (ProcessorMode)_processorModeRaw;
        set => Interlocked.Exchange(ref _processorModeRaw, (int)value);
    }

    private bool IsRunMode => ProcessorModeValue == ProcessorMode.LocalRun ||
                              ProcessorModeValue == ProcessorMode.RemoteRun;

    // ─── Diagnostic Counters ─────────────────────────────────────────────────
    // Layout matches AB Application Note (1995) "DF1 Full-Duplex size <=40 bytes" table.
    // Total 34 bytes: modem status word (2 bytes) + 32 counter bytes.
    // All fields are int to allow Interlocked.Increment / Interlocked.Exchange.
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
    private volatile ushort _modemStatus = 0x001F;

    // ─── Timers ──────────────────────────────────────────────────────────────
    private Timer? _timer;           // Updates S2 date/time registers (every 1 sec)
    private Timer? _waveformTimer;   // Updates F8:0 (sine) and F8:1 (triangle, every 500ms)

    // ─── Shutdown ────────────────────────────────────────────────────────────
    private int _isDisposing = 0;
    private long _framesProcessed = 0;  // Total frames processed across all transports

    // ─── Properties ──────────────────────────────────────────────────────────
    public CheckSumOptions CheckSum
    {
        get => _checkSum;
        set
        {
            _checkSum = value;
            if (_transport is DF1BaseTransport df1Transport)
                df1Transport.CheckSum = value;
        }
    }

    public int MyNode
    {
        get => _myNode;
        set
        {
            _myNode = value;
            if (_transport is DF1BaseTransport df1Transport)
                df1Transport.MyNode = value;
        }
    }

    // ─── RS-485 configuration for half-duplex slave ─────────────────────
    private DF1HalfDuplexTransport.Rs485ControlMode _rs485Mode = DF1HalfDuplexTransport.Rs485ControlMode.Auto;
    private int _rtsAssertDelayMs = 1;
    private int _rtsDeassertDelayMs = 5;

    /// <summary>
    /// Gets or sets the RS-485 direction control mode.
    /// Must be set before calling <see cref="Start()"/>.
    /// </summary>
    public DF1HalfDuplexTransport.Rs485ControlMode Rs485Mode
    {
        get => _rs485Mode;
        set
        {
            _rs485Mode = value;
            if (_transport is DF1HalfDuplexTransport slave)
                slave.Rs485Mode = value;
        }
    }

    public int RtsAssertDelayMs
    {
        get => _rtsAssertDelayMs;
        set
        {
            _rtsAssertDelayMs = Math.Max(0, value);
            if (_transport is DF1HalfDuplexTransport slave)
                slave.RtsAssertDelayMs = _rtsAssertDelayMs;
        }
    }

    public int RtsDeassertDelayMs
    {
        get => _rtsDeassertDelayMs;
        set
        {
            _rtsDeassertDelayMs = Math.Max(0, value);
            if (_transport is DF1HalfDuplexTransport slave)
                slave.RtsDeassertDelayMs = _rtsDeassertDelayMs;
        }
    }

    public TransportMode Mode => _mode;

    // ─── Constructor ─────────────────────────────────────────────────────────
    /// <summary>
    /// Initializes the emulator with the specified transport mode.
    /// </summary>
    /// <param name="portName">Serial port name (e.g., "COM2" or "/dev/ttyUSB0")</param>
    /// <param name="baudRate">Baud rate (e.g., 19200, 9600)</param>
    /// <param name="parity">Parity mode (None, Odd, Even)</param>
    /// <param name="mode">Transport mode (DF1, DH485, or EIP)</param>
    /// <param name="eipPort">EIP port number (default 44818, only used for EIP mode)</param>
    /// <param name="cspPort">CSP port number (default 2222, only used for CSP mode)</param>
    /// <exception cref="NotImplementedException">Thrown for DH485 mode (planned for future)</exception>
    public PCCCEmulator(string portName, int baudRate, Parity parity, 
                        TransportMode mode = TransportMode.DF1, 
                        int eipPort = 44818, int cspPort = 2222,
                        EmulationFamily family = EmulationFamily.SlcMicroLogix)
    {
        _family = family;   // set family before BuildGetStatusPayload is called
        _memory = new PlcMemory(_family);
        _directoryBytes = _memory.GetDirectory();
        Logger.Always(this, $"Directory loaded: {_directoryBytes?.Length ?? 0} bytes");

        _mode = mode;

        // Build initial GetStatus payload cache using the default processor mode.
        // The cache is dynamically updated via UpdateProcessorMode() whenever the
        // processor mode changes at runtime (CMD 0x0F FNC 0x80).
        _cachedGetStatusPayload = BuildGetStatusPayload();

        // Create the appropriate transport handler based on mode
        _transport = mode switch
        {
            TransportMode.DF1      => new DF1FullDuplexTransport(this, portName, baudRate, parity),
            TransportMode.UIC      => new DF1FullDuplexTransport(this, portName, 19200, Parity.None)
            {
                CheckSum = CheckSumOptions.Crc
            },
            TransportMode.EIP      => new EIPTransport(this, eipPort),
            TransportMode.DF1Slave => new DF1HalfDuplexTransport(this, portName, baudRate, parity),
            TransportMode.CSP      => new CSPTransport(this, cspPort),
            _                   => throw new ArgumentException($"Unknown emulator mode: {mode}")
        };

        // Subscribe to transport PDU events
        _transport.PduReceived += OnPduReceived;

        // Create timers in stopped state; they are armed when Start() is called
        _timer        = new Timer(_ => UpdateDateTime(), null, Timeout.Infinite, Timeout.Infinite);
        _waveformTimer = new Timer(_ => UpdateWaveform(), null, Timeout.Infinite, Timeout.Infinite);

        Logger.Always(this, $"PCCC emulator initialized in {mode} mode");
    }

    /// <summary>
    /// Convenience constructor for DF1 mode (default transport).
    /// </summary>
    public PCCCEmulator(string portName, int baudRate, Parity parity)
        : this(portName, baudRate, parity, TransportMode.DF1)
    {
    }

    // ─── Public API ──────────────────────────────────────────────────────────
    /// <summary>
    /// Starts the emulator. Opens the serial port (for DF1 mode) or starts
    /// the TCP listener (for EIP mode), and begins processing commands.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the port is not found or access is denied</exception>
    public void Start()
    {
        _transport?.Start();
        _timer?.Change(0, 1000);
        _waveformTimer?.Change(0, 500);
        Logger.Always(this, $"PCCC emulator started in {_mode} mode");
    }

    /// <summary>
    /// Stops the emulator gracefully. Closes the serial port or TCP connection,
    /// stops timers, and reports final statistics.
    /// </summary>
    public void Stop()
    {
        if (Interlocked.CompareExchange(ref _isDisposing, 1, 0) != 0) return;

        _transport?.Stop();
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        _waveformTimer?.Change(Timeout.Infinite, Timeout.Infinite);

        Logger.Always(this, $"PCCC emulator stopped. Total frames processed: {_framesProcessed:N0}");
    }

    public void Dispose()
    {
        Stop();
        _timer?.Dispose();
        _waveformTimer?.Dispose();
        _memory.Dispose();
        // EIPTransport does not implement IDisposable; Stop() above already drains
        // in-flight requests and closes all resources via its StopAsync() path.
        (_transport as IDisposable)?.Dispose();
        _transport = null;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Enables or disables verbose logging. Disabling logging eliminates string
    /// allocations and significantly improves throughput under high load.
    /// </summary>
    /// <param name="enabled">True to enable logging, false for maximum performance</param>
    public void SetLoggingEnabled(bool enabled)
    {
        if (!enabled)
            Logger.Info(this, "Logging disabled for maximum throughput");

        Logger.Enabled = enabled;
    }

    // ─── Internal Methods for DF1BaseTransport to Update Counters ─────────────────
    // These allow DF1BaseTransport to report transport-specific events without exposing
    // internal counters directly to external code.
    internal void IncrementTotalPacketsSent()       => Interlocked.Increment(ref _totalPacketsSent);
    internal void IncrementFramesProcessed()        => Interlocked.Increment(ref _framesProcessed);
    internal void IncrementTotalPacketsReceived()   => Interlocked.Increment(ref _totalPacketsReceived);
    internal void IncrementBadPacketsDetected()     => Interlocked.Increment(ref _badPacketsDetected);
    internal void IncrementUndeliveredPackets()     => Interlocked.Increment(ref _undeliveredPackets);
    internal void IncrementEnqReceived()            => Interlocked.Increment(ref _enqReceived);
    internal void IncrementNakReceived()            => Interlocked.Increment(ref _nakReceived);
    internal void IncrementNoBufferNakd()           => Interlocked.Increment(ref _noBufferNakd);
    internal void IncrementDuplicatePackets()       => Interlocked.Increment(ref _duplicatePacketsReceived);
    internal void IncrementDcdRecovery()            => Interlocked.Increment(ref _dcdRecoveryCount);
    internal void IncrementLostModem()              => Interlocked.Increment(ref _lostModemCount);
    internal void IncrementEnqSent()                => Interlocked.Increment(ref _enqSent);

    // ─── Internal Methods Called by DF1BaseTransport ──────────────────────────────
    // These provide DF1BaseTransport with read-only access to diagnostic counters
    // for health monitoring purposes.
    // Volatile.Read is used for a clean atomic read without the compare-exchange
    // idiom, which is semantically equivalent but more explicit in intent.
    public int GetBadPacketsDetected()    => Volatile.Read(ref _badPacketsDetected);
    public int GetUndeliveredPackets()    => Volatile.Read(ref _undeliveredPackets);
    public int GetEnqReceived()           => Volatile.Read(ref _enqReceived);
    public int GetNakReceived()           => Volatile.Read(ref _nakReceived);
    public int GetTotalPacketsReceived()  => Volatile.Read(ref _totalPacketsReceived);
    public long GetFramesProcessed()      => Volatile.Read(ref _framesProcessed);

    /// <summary>
    /// Updates modem status bits based on actual hardware line states.
    /// Called by DF1BaseTransport when pin change events occur.
    /// </summary>
    internal void UpdateModemStatus()
    {
        if (_transport is DF1BaseTransport df1Transport)
        {
            ushort status = 0;
            if (df1Transport.GetCtsHolding()) status |= 0x0001;  // CTS
            if (df1Transport.GetRtsEnable())  status |= 0x0002;  // RTS
            if (df1Transport.GetDsrHolding()) status |= 0x0004;  // DSR
            if (df1Transport.GetCdHolding())  status |= 0x0008;  // DCD
            if (df1Transport.GetDtrEnable())  status |= 0x0010;  // DTR
            _modemStatus = status;
        }
    }

    // ─── DH485 Link Parameters ─────────────────────────────────────────────
    private int _maxNodeAddress = 31;   // Default maximum solicit address for DH485

    // ─── PDU Event Handler (Called by transport layer) ────────────────────────
    /// <summary>
    /// Called when a complete PDU (Transport Data Unit) has been received.
    /// The PDU is the inner frame without transport-specific framing.
    /// Format: [DST, SRC, CMD, STS, TNS_LO, TNS_HI, FUNC?, DATA...]
    /// </summary>
    private void OnPduReceived(object? sender, (byte[] pdu, object ClientContext) args)
    {
        Interlocked.Increment(ref _framesProcessed);
        DispatchCommand(args.pdu, args.ClientContext);
    }

    // ─── Command Dispatcher (Shared Across All Transports) ────────────────────
    // All command processing routes through here. The PDU format is the same
    // for DF1 and EIP/PCCC (CIP encapsulated PCCC).
    //
    // DATA OFFSET RULE:
    //   Only CMD 0x0F always carries a FUNC byte in the request; all other
    //   commands place data immediately after the 6-byte header (no FUNC byte).
    //   Therefore dataOffset = 7 only when cmd == 0x0F; otherwise dataOffset = 6.
    //   This is distinct from the response convention: some response helpers
    //   (SendEmptyResponse, SendLoopbackResponse, SendErrorResponse) include
    //   a FUNC byte (withFunc: true) while data responses omit it (withFunc: false).
    private void DispatchCommand(byte[] pdu, object clientContext)
    {
        if (pdu.Length < 6) return;

        int dst  = pdu[0];
        int src  = pdu[1];
        int cmd  = pdu[2];
        int tns  = pdu[4] | (pdu[5] << 8);

        // Determine if command includes a FUNC byte per AB Publication 1770-6.5.16
        // Commands with FUNC byte: 0x06 (Get Status, Diagnostic Counters, etc.),
        //                          0x0F (Protected Logical Read/Write),
        //                          0x0A (Read Diagnostic Counters)
        bool hasFuncByte = (cmd == 0x06 || cmd == 0x0F || cmd == 0x0A) && pdu.Length >= 7;
        int func         = hasFuncByte ? pdu[6] : 0;
        int dataOffset   = hasFuncByte ? 7 : 6;

        // Extract data payload — everything after the header (+ FUNC byte for 0x0F)
        byte[] data = pdu.Length > dataOffset ? pdu[dataOffset..] : Array.Empty<byte>();

        // Dispatch based on command code
        if (cmd == 0x06 && func == 0x00)
            SendGetStatusLoopbackResponse(src, tns, data, clientContext);
        else if (cmd == 0x06 && func == 0x03)
            SendGetStatusResponse(src, tns, clientContext);
        else if (cmd == 0x06 && func == 0x01)
            SendDiagnosticCountersResponse(src, tns, replyCmd: 0x46, clientContext);
        else if (cmd == 0x06 && func == 0x07)
        {
            ResetDiagnosticCounters();
            SendEmptyResponse(src, tns, 0x46, func, clientContext);
        }
        else if (cmd == 0x06 && func == 0x02)
            SendLoopbackResponse(src, tns, data, clientContext);
        else if (cmd == 0x01)
            SendEmptyResponse(src, tns, 0x41, 0x00, clientContext);
        else if (cmd == 0x0B)
            SendEmptyResponse(src, tns, 0x4B, 0x00, clientContext);
        else if (cmd == 0x0F)
            DispatchFunctionCode(src, tns, func, data, clientContext);
        else if (cmd == 0x0A)
            SendDiagnosticCountersResponse(src, tns, 0x4A, clientContext);
        else if (cmd == 0x67)
            HandleReadModifiedData(src, tns, data, clientContext);
        else if (cmd == 0x06 && func == 0x09)
        {
            // Read Link Parameters – return one byte containing _maxNodeAddress
            byte[] linkParam = new byte[] { (byte)_maxNodeAddress };
            SendDataResponse(src, tns, 0x46, linkParam, clientContext);
        }
        else if (cmd == 0x06 && func == 0x0A)
        {
            // Set Link Parameters – expects one byte in data
            if (data != null && data.Length >= 1)
                _maxNodeAddress = data[0];
            SendEmptyResponse(src, tns, 0x46, func, clientContext);
        }
        else
            SendErrorResponse(src, tns, cmd, func, 0x01, clientContext);
    }

    // ─── PCCC Function Code Handlers ─────────────────────────────────────────
    private void DispatchFunctionCode(int src, int tns, int func, byte[] data, object clientContext)
    {
        switch (func)
        {
            // Protected Typed Logical Read operations
            case 0xA1:  // Two address fields
            case 0xA2:  // Three address fields (with sub-element)
                HandleReadRequest(src, tns, func, data, clientContext);
                break;

            // Protected Typed Logical Write operations
            case 0xAA:  // Word write
            case 0xAB:  // Bit-masked write
                HandleWriteRequest(src, tns, func, data, clientContext);
                break;

            // Read File Info (SLC 5/03 and 5/04 only)
            case 0x94:
                HandleReadFileInfo(src, tns, data, clientContext);
                break;

            // Change processor mode (FNC=0x80 for SLC 5/03)
            // Request data[0] carries the requested mode code:
            //   0x01 = RemoteProg, 0x06 = RemoteRun
            //   0x07 = TestCont,   0x08 = TestSingle, 0x09 = TestStep
            // LocalProg (0x11) and LocalRun (0x1E) are set by the keyswitch
            // on the physical PLC and are not settable via this command.
            case 0x80:
                if (data != null && data.Length > 0)
                {
                    byte requestedMode = data[0];
                    ProcessorModeValue = requestedMode switch
                    {
                        0x01 => ProcessorMode.RemoteProg,
                        0x06 => ProcessorMode.RemoteRun,
                        0x07 => ProcessorMode.TestCont,
                        0x08 => ProcessorMode.TestSingle,
                        0x09 => ProcessorMode.TestStep,
                        _    => ProcessorModeValue  // Unknown mode: ignore
                    };
                    UpdateProcessorMode();
                }
                SendEmptyResponse(src, tns, 0x4F, func, clientContext);
                break;

            // Change processor mode (FNC=0x3A for MicroLogix 1000)
            // Request data[0] carries the requested mode code:
            //   0x01 = RemoteProg, 0x02 = RemoteRun
            case 0x3A:
                if (data != null && data.Length > 0)
                {
                    byte requestedMode = data[0];
                    ProcessorModeValue = requestedMode switch
                    {
                        0x01 => ProcessorMode.RemoteProg,
                        0x02 => ProcessorMode.RemoteRun,
                        _    => ProcessorModeValue
                    };
                    UpdateProcessorMode();
                }
                SendEmptyResponse(src, tns, 0x4F, func, clientContext);
                break;

            // Acknowledged commands (no processing required)
            case 0x11:  // Get Edit Resource
            case 0x12:  // Return Edit Resource
            case 0x29:  // Unrecognized (sent by RSLinx during auto-configure)
            case 0x88:  // Execute Command List (download initialization)
                SendEmptyResponse(src, tns, 0x4F, func, clientContext);
                break;

            // =================================================================
            // File-based upload/download commands (SLC 5/03 and newer)
            // =================================================================
            case 0x53:  // Upload All Request
            {
                if (_family == EmulationFamily.Plc5)
                {
                    // PLC-5 Procedure 2 reply per spec §7-33: 
                    // A(1B) = number of uploadable segments 
                    // B(8B × A) = [startAddr 4B LE][endAddr 4B LE] 
                    // C(1B) = number of comparable segments (0)
                    int totalBytes = _memory.GetFlatMemorySize();
                    if (totalBytes == 0)
                    {
                        SendErrorResponse(src, tns, 0x0F, func, 0x10, clientContext);
                        break;
                    }
                    int endAddr = totalBytes - 1;
                    var resp = new byte[10];   // A(1) + B(8) + C(1)
                    resp[0] = 1;               // A: 1 uploadable segment
                    // B: start = 0x00000000
                    // B: end address (LE)
                    resp[5] = (byte)( endAddr        & 0xFF);
                    resp[6] = (byte)((endAddr >>  8) & 0xFF);
                    resp[7] = (byte)((endAddr >> 16) & 0xFF);
                    resp[8] = (byte)((endAddr >> 24) & 0xFF);
                    resp[9] = 0;               // C: 0 comparable segments
                    SendDataResponse(src, tns, 0x4F, resp, clientContext);
                }
                else
                {
                    // Segment info: max chunk size (2 bytes LE) + total memory size (2 bytes LE)
                    // SLC 5/03: max segment = 236 bytes, total memory = directory size
                    int totalMemory = _directoryBytes?.Length ?? 0;
                    byte[] segInfo = new byte[4];
                    segInfo[0] = (byte)(236 & 0xFF);
                    segInfo[1] = (byte)((236 >> 8) & 0xFF);
                    segInfo[2] = (byte)(totalMemory & 0xFF);
                    segInfo[3] = (byte)((totalMemory >> 8) & 0xFF);
                    SendDataResponse(src, tns, 0x4F, segInfo, clientContext);
                }
                break;
            }

            case 0x50:  // Download All Request
            {
                if (_family == EmulationFamily.Plc5)
                {
                    // PLC-5: empty reply per spec §7-7
                    SendEmptyResponse(src, tns, 0x4F, func, clientContext);
                }
                else
                {
                    int totalMemory = _directoryBytes?.Length ?? 0;
                    byte[] segInfo = new byte[4];
                    segInfo[0] = (byte)(164 & 0xFF);
                    segInfo[1] = (byte)((164 >> 8) & 0xFF);
                    segInfo[2] = (byte)(totalMemory & 0xFF);
                    segInfo[3] = (byte)((totalMemory >> 8) & 0xFF);
                    SendDataResponse(src, tns, 0x4F, segInfo, clientContext);
                }
                break;
            }

            case 0x55:  // Upload Completed
            case 0x52:  // Download Completed
                SendEmptyResponse(src, tns, 0x4F, func, clientContext);
                break;

            case 0x81:  // Open File
                if (data.Length >= 2)
                {
                    int fileNumber = data[0];
                    int fileType   = data[1];

                    // Program directory (type 0x24, number 0) → special tag 0xFFFF
                    if (fileType == 0x24 && fileNumber == 0)
                    {
                        _openFiles[0xFFFF] = (fileType, fileNumber);
                        SendDataResponse(src, tns, 0x4F, new byte[] { 0xFF, 0xFF }, clientContext);
                        break;
                    }

                    // Normal file: verify exists, assign unique tag
                    int size = _memory.GetFileSize(fileType, fileNumber);
                    if (size > 0)
                    {
                        ushort tag;
                        lock (_tagLock)
                        {
                            // Cari tag berikutnya yang belum dipakai (skip 0 dan 0xFFFF)
                            do {
                                if (_nextTag == 0xFFFE) _nextTag = 1;
                                else _nextTag++;
                            } while (_openFiles.ContainsKey(_nextTag));
                            tag = _nextTag;
                        }
                        _openFiles[tag] = (fileType, fileNumber);
                        byte[] tagBytes = new byte[] { (byte)(tag & 0xFF), (byte)((tag >> 8) & 0xFF) };
                        SendDataResponse(src, tns, 0x4F, tagBytes, clientContext);
                    }
                    else
                    {
                        SendErrorResponse(src, tns, 0x0F, func, 0x50, clientContext);
                    }
                }
                else
                {
                    SendErrorResponse(src, tns, 0x0F, func, 0x01, clientContext);
                }
                break;

            case 0x82:  // Close File
                if (data.Length >= 2)
                {
                    ushort tag = (ushort)(data[0] | (data[1] << 8));
                    _openFiles.TryRemove(tag, out _);
                }
                SendEmptyResponse(src, tns, 0x4F, func, clientContext);
                break;

            case 0xA7:  // Protected Typed File Read
                if (data.Length >= 5)
                {
                    ushort tag = (ushort)(data[0] | (data[1] << 8));
                    int offsetWords = data[2] | (data[3] << 8);
                    int bytesToRead = data[4];
                    int byteOffset  = offsetWords * 2;

                    if (tag == 0xFFFF)
                    {
                        // Directory read — sama seperti sebelumnya
                        if (_directoryBytes == null || byteOffset + bytesToRead > _directoryBytes.Length)
                        {
                            SendErrorResponse(src, tns, 0x0F, func, 0x10, clientContext);
                            break;
                        }
                        byte[] response = new byte[bytesToRead];
                        Array.Copy(_directoryBytes, byteOffset, response, 0, bytesToRead);
                        SendDataResponse(src, tns, 0x4F, response, clientContext);
                        break;
                    }

                    // Lookup tag → (fileType, fileNumber)
                    if (!_openFiles.TryGetValue(tag, out var fileInfo))
                    {
                        SendErrorResponse(src, tns, 0x0F, func, 0x50, clientContext);  // file not open
                        break;
                    }

                    byte[] fileData = _memory.ReadRaw(fileInfo.FileType, fileInfo.FileNumber,
                                                    byteOffset, bytesToRead, out int status);
                    if (status == 0)
                        SendDataResponse(src, tns, 0x4F, fileData, clientContext);
                    else
                        SendErrorResponse(src, tns, 0x0F, func, 0x10, clientContext);
                }
                else
                {
                    SendErrorResponse(src, tns, 0x0F, func, 0x01, clientContext);
                }
                break;

            case 0xAF: // Protected Typed File Write
                if (data.Length >= 5)
                {
                    int tag = data[0] | (data[1] << 8);
                    int offsetWords  = data[2] | (data[3] << 8);
                    int bytesToWrite = data[4];
                    int byteOffset   = offsetWords * 2;

                    if (tag == 0xFFFF)
                    {
                        // Directory write — allowed during download
                        if (data.Length < 5 + bytesToWrite)
                        {
                            SendErrorResponse(src, tns, 0x0F, func, 0x01, clientContext);
                            break;
                        }
                        if (_directoryBytes == null ||
                            byteOffset + bytesToWrite > _directoryBytes.Length)
                        {
                            SendErrorResponse(src, tns, 0x0F, func, 0x10, clientContext);
                            break;
                        }
                        byte[] writeData = new byte[bytesToWrite];
                        Array.Copy(data, 5, writeData, 0, bytesToWrite);
                        Array.Copy(writeData, 0, _directoryBytes, byteOffset, bytesToWrite);
                        SendEmptyResponse(src, tns, 0x4F, func, clientContext);
                        break;
                    }

                    // Normal file — pakai _openFiles lookup (kode yang sudah ada)
                    if (!_openFiles.TryGetValue((ushort)tag, out var fileInfo))
                    {
                        SendErrorResponse(src, tns, 0x0F, func, 0x50, clientContext);
                        break;
                    }
                    if (data.Length >= 5 + bytesToWrite)
                    {
                        byte[] writeData = new byte[bytesToWrite];
                        Array.Copy(data, 5, writeData, 0, bytesToWrite);
                        bool ok = _memory.WriteRaw(fileInfo.FileType, fileInfo.FileNumber,
                                                byteOffset, bytesToWrite, writeData);
                        if (ok)
                            SendEmptyResponse(src, tns, 0x4F, func, clientContext);
                        else
                            SendErrorResponse(src, tns, 0x0F, func, 0x10, clientContext);
                    }
                    else
                    {
                        SendErrorResponse(src, tns, 0x0F, func, 0x01, clientContext);
                    }
                }
                else
                {
                    SendErrorResponse(src, tns, 0x0F, func, 0x01, clientContext);
                }
                break;

            case 0x41:  // Disable Forces
                SendEmptyResponse(src, tns, 0x4F, func, clientContext);
                break;

            case 0x8F:  // Apply Port Configuration
                SendEmptyResponse(src, tns, 0x4F, func, clientContext);
                break;

            // ─── Initialize Memory (0x0F/0x57) ────────────────────────────────────
            case 0x57:
                _memory.ResetToDefault();
                SendEmptyResponse(src, tns, 0x4F, func, clientContext);
                break;

            // ─── Read-Modify-Write (0x0F/0x26) ────────────────────────────────────
            case 0x26:
                HandleReadModifyWrite(src, tns, data, clientContext);
                break;

            case 0x67:  // Typed Write for PLC-5
                HandleTypedWriteRequest(src, tns, data, clientContext);
                break;

            case 0x68:  // Typed Read for PLC-5
                HandleTypedReadRequest(src, tns, data, clientContext);
                break;

            case 0x00:  // Word Range Write for PLC-5
                HandleWordRangeWrite(src, tns, data, clientContext);
                break;

            case 0x01:  // Word Range Read for PLC-5
                HandleWordRangeRead(src, tns, data, clientContext);
                break;

            case 0x17:  // Read Bytes Physical — PLC-5 upload per spec §7-19
            {
                // Request payload: [physAddr 4B LE][size 1B]
                // size: max 240, must be even
                if (data == null || data.Length < 5)
                {
                    SendErrorResponse(src, tns, 0x0F, func, 0x01, clientContext);
                    break;
                }
                int physAddr = data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24);
                int size     = data[4];
                byte[]? result = _memory.ReadPhysical(physAddr, size);
                if (result == null)
                    SendErrorResponse(src, tns, 0x0F, func, 0x01, clientContext);
                else
                    SendDataResponse(src, tns, 0x4F, result, clientContext);
                break;
            }

            case 0x18:  // Write Bytes Physical — PLC-5 download per spec §7-35
            {
                // Request payload: [physAddr 4B LE][data... (max 238, must be even)]
                if (data == null || data.Length < 5)
                {
                    SendErrorResponse(src, tns, 0x0F, func, 0x01, clientContext);
                    break;
                }
                int physAddr  = data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24);
                int dataLen   = data.Length - 4;
                var writeData = new byte[dataLen];
                Array.Copy(data, 4, writeData, 0, dataLen);
                bool ok = _memory.WritePhysical(physAddr, writeData);
                if (ok) SendEmptyResponse(src, tns, 0x4F, func, clientContext);
                else    SendErrorResponse(src, tns, 0x0F, func, 0x01, clientContext);
                break;
            }

            default:
                SendErrorResponse(src, tns, 0x0F, func, 0x01, clientContext);
                break;
        }
    }

    /// <summary>
    /// Handles Protected Typed Logical Read requests (0xA1, 0xA2, 0x68).
    /// 
    /// Request payload layout (from DF1Comm ReadRawData):
    ///   [0]    = bytesToRead
    ///   [1]    = fileNumber
    ///   [2]    = fileType
    ///   [3]    = element (255 = extended: element in [4..5], then subElement follows)
    ///   [4]    = subElement (func 0xA2 only; or element low byte if [3]==255)
    ///   [5]    = element high byte (if [3]==255)
    /// 
    /// Supports extended element addressing (element >= 255) by decoding 0xFF
    /// followed by two bytes.
    /// </summary>
    private void HandleReadRequest(int src, int tns, int func, byte[] payload, object clientContext)
    {
        if (payload == null || payload.Length < 4)
        {
            SendErrorResponse(src, tns, 0x0F, func, 0x01, clientContext);
            return;
        }

        int bytesToRead = payload[0];
        int fileNumber  = payload[1];
        int fileType    = payload[2];
        int element     = payload[3];
        int payloadIdx  = 4;

        // Extended element addressing (element >= 255)
        if (element == 0xFF && payload.Length > payloadIdx + 1)
        {
            element = payload[payloadIdx] | (payload[payloadIdx + 1] << 8);
            payloadIdx += 2;
        }

        // Sub-element decoding (only for func 0xA2)
        int subElement = 0;
        if (func == 0xA2 && payload.Length > payloadIdx)
        {
            if (payload[payloadIdx] == 0xFF && payload.Length > payloadIdx + 2)
                subElement = payload[payloadIdx + 1] | (payload[payloadIdx + 2] << 8);
            else
                subElement = payload[payloadIdx];
        }

        int bpe = _memory.GetBytesPerElement(fileType, fileNumber);
        int byteOffset = element * bpe + subElement * 2;

        byte[] data = _memory.ReadRaw(fileType, fileNumber, byteOffset, bytesToRead, out int status);

        // Map status codes to DF1 error codes:
        //   status 2 = file not found → STS 0x50 (bad address)
        //   status 3 = out of range   → STS 0x10 (illegal format/address)
        if (status == 2) { SendErrorResponse(src, tns, 0x0F, func, 0x50, clientContext); return; }
        if (status == 3) { SendErrorResponse(src, tns, 0x0F, func, 0x10, clientContext); return; }
        if (status != 0) { SendErrorResponse(src, tns, 0x0F, func, 0x10, clientContext); return; }

        SendDataResponse(src, tns, 0x4F, data, clientContext);
    }

    /// <summary>
    /// Handles Protected Typed Logical Write (0xAA) and Bit Write (0xAB).
    /// 
    /// Request payload layout (from DF1Comm WriteRawData):
    /// 
    ///   func 0xAA — word write:
    ///     [0] = bytesToWrite
    ///     [1] = fileNumber
    ///     [2] = fileType
    ///     [3] = element (255 = extended: [4..5] = element 16-bit, then subElement)
    ///     [4] = subElement (or element low byte if [3]==255)
    ///     [5..] = data bytes
    /// 
    ///   func 0xAB — bit-masked write:
    ///     [0] = 2 (one word operation)
    ///     [1] = fileNumber
    ///     [2] = fileType
    ///     [3] = element (255 = extended)
    ///     [4] = subElement (or element low byte if [3]==255)
    ///     [5] = mask low byte
    ///     [6] = mask high byte
    ///     [7] = value low byte (bits to set where mask bit = 1)
    ///     [8] = value high byte
    /// 
    /// For I/O image file types (0x8B output-by-slot, 0x8C input-by-slot),
    /// the mask is ignored and data is written directly (SLCCCD section 4.36).
    /// </summary>
    private void HandleWriteRequest(int src, int tns, int func, byte[] payload, object clientContext)
    {
        if (payload == null || payload.Length < 5)
        {
            SendErrorResponse(src, tns, 0x0F, func, 0x01, clientContext);
            return;
        }

        int bytesToWrite = payload[0];
        int fileNumber   = payload[1];
        int fileType     = payload[2];
        int element      = payload[3];
        int payloadIdx   = 4;

        // Extended element addressing
        if (element == 0xFF && payload.Length > payloadIdx + 1)
        {
            element = payload[payloadIdx] | (payload[payloadIdx + 1] << 8);
            payloadIdx += 2;
        }

        if (payload.Length <= payloadIdx)
        {
            SendErrorResponse(src, tns, 0x0F, func, 0x01, clientContext);
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

        // Validate file exists and write operation is within bounds
        int fileSize = _memory.GetFileSize(fileType, fileNumber);
        if (fileSize == 0)
        {
            // File not found
            SendErrorResponse(src, tns, 0x0F, func, 0x50, clientContext);
            return;
        }

        int bpe = _memory.GetBytesPerElement(fileType, fileNumber);
        int byteOffset = element * bpe + subElement * 2;
        
        if (byteOffset < 0 || byteOffset + bytesToWrite > fileSize)
        {
            // Write operation would exceed file bounds
            SendErrorResponse(src, tns, 0x0F, func, 0x10, clientContext);
            return;
        }

        // Bit-masked write (0xAB)
        if (func == 0xAB)
        {
            // Bit write (masked write) - atomic operation required for multi-client safety
            if (payload.Length < payloadIdx + 4)
            {
                SendErrorResponse(src, tns, 0x0F, func, 0x01, clientContext);
                return;
            }

            int mask  = payload[payloadIdx]     | (payload[payloadIdx + 1] << 8);
            int value = payload[payloadIdx + 2] | (payload[payloadIdx + 3] << 8);

            // Atomic read-modify-write — safe for concurrent EIP clients
            bool ok = _memory.ReadModifyWrite(fileType, fileNumber, element, subElement, mask, value);
            if (!ok)
            {
                SendErrorResponse(src, tns, 0x0F, func, 0x10, clientContext);
                return;
            }
        }
        else  // func == 0xAA (word write)
        {
            if (payload.Length < payloadIdx + bytesToWrite)
            {
                SendErrorResponse(src, tns, 0x0F, func, 0x01, clientContext);
                return;
            }

            byte[] writeData = new byte[bytesToWrite];
            Array.Copy(payload, payloadIdx, writeData, 0, bytesToWrite);
            bool ok = _memory.Write(fileType, fileNumber, element, subElement, bytesToWrite, writeData);
            if (!ok)
            {
                SendErrorResponse(src, tns, 0x0F, func, 0x10, clientContext);
                return;
            }
        }

        SendEmptyResponse(src, tns, 0x4F, func, clientContext);
    }

    /// <summary>
    /// Read File Info (CMD=0x0F, FNC=0x94).
    /// SLC 5/03 and SLC 5/04 only. Used by RSLinx to enumerate data files.
    /// 
    /// Command format (AB Application Note, March 1995, section 6.5):
    ///   FNC = 0x94
    ///   mask = 0x06 (2 bytes follow: major file type + file number)
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
    ///   STS=0x00  success
    ///   STS=0x10  illegal format (wrong mask or major file type)
    ///   STS=0x50  bad address / file doesn't exist
    /// </summary>
    private void HandleReadFileInfo(int src, int tns, byte[] payload, object clientContext)
    {
        if (payload == null || payload.Length < 3)
        {
            SendErrorResponse(src, tns, 0x0F, 0x94, 0x10, clientContext);
            return;
        }

        byte mask       = payload[0];
        byte majorType  = payload[1];
        byte fileNumber = payload[2];

        // Validate: mask must be 0x06, major type must be 0x80 (data table)
        if (mask != 0x06 || majorType != 0x80)
        {
            SendErrorResponse(src, tns, 0x0F, 0x94, 0x10, clientContext);
            return;
        }

        if (!_memory.GetFileInfo(fileNumber, out int fileType, out int fileSize, out int elements))
        {
            SendErrorResponse(src, tns, 0x0F, 0x94, 0x50, clientContext);
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
        // Byte 6: reserved (high byte of count per AB Application Note)
        reply[6] = reply[5];
        // Byte 7: reserved
        reply[7] = 0x00;
        // Byte 8: data type code
        reply[8] = (byte)fileType;

        SendDataResponse(src, tns, 0x4F, reply, clientContext);
    }

    /// <summary>
    /// Read Modified Data (CMD=0x67) — simplified as normal read.
    /// Used by some Rockwell software for optimized data access.
    /// 
    /// Request payload layout (no FUNC byte — data starts immediately at offset 6):
    ///   [0]   = fileNumber
    ///   [1]   = fileType
    ///   [2-3] = word offset (little-endian)
    ///   [4]   = bytesToRead
    /// </summary>
    private void HandleReadModifiedData(int src, int tns, byte[] payload, object clientContext)
    {
        if (payload == null || payload.Length < 5)
        {
            SendErrorResponse(src, tns, 0x67, 0x00, 0x01, clientContext);
            return;
        }

        int fileNumber  = payload[0];
        int fileType    = payload[1];
        int offsetWords = payload[2] | (payload[3] << 8);
        int bytesToRead = payload[4];
        int byteOffset  = offsetWords * 2;

        int fileSize = _memory.GetFileSize(fileType, fileNumber);
        if (fileSize == 0)
        {
            // File not found
            SendErrorResponse(src, tns, 0x67, 0x00, 0x50, clientContext);
            return;
        }

        if (byteOffset + bytesToRead > fileSize)
        {
            // Offset out of range
            SendErrorResponse(src, tns, 0x67, 0x00, 0x10, clientContext);
            return;
        }

        byte[] data = _memory.ReadRaw(fileType, fileNumber, byteOffset, bytesToRead, out int status);
        if (status == 2)  // File not found
        {
            SendErrorResponse(src, tns, 0x67, 0x00, 0x50, clientContext);
            return;
        }
        if (status != 0)  // Other error (out of range, etc.)
        {
            SendErrorResponse(src, tns, 0x67, 0x00, 0x10, clientContext);
            return;
        }

        SendDataResponse(src, tns, 0xA7, data, clientContext);
    }

    /// <summary>
    /// Handles Read‑Modify‑Write (CMD=0x0F, FNC=0x26).
    /// Payload format: for each set:
    ///   fileNumber (1 byte)
    ///   fileType   (1 byte)
    ///   element    (1 or 3 bytes, 0xFF extended)
    ///   subElement (1 or 3 bytes, 0xFF extended)
    ///   andMask    (2 bytes LE)
    ///   orMask     (2 bytes LE)
    /// </summary>
    private void HandleReadModifyWrite(int src, int tns, byte[] payload, object clientContext)
    {
        if (payload == null || payload.Length < 8) // at least one full set
        {
            SendErrorResponse(src, tns, 0x0F, 0x26, 0x01, clientContext);
            return;
        }

        int idx = 0;
        bool success = true;

        while (idx < payload.Length && success)
        {
            // Decode fileNumber
            if (idx >= payload.Length) { success = false; break; }
            int fileNumber = payload[idx++];

            // Decode fileType
            if (idx >= payload.Length) { success = false; break; }
            int fileType = payload[idx++];

            // Decode element (extended if 0xFF)
            if (idx >= payload.Length) { success = false; break; }
            int element = payload[idx++];
            if (element == 0xFF)
            {
                if (idx + 2 > payload.Length) { success = false; break; }
                element = payload[idx] | (payload[idx + 1] << 8);
                idx += 2;
            }

            // Decode subElement (extended if 0xFF)
            if (idx >= payload.Length) { success = false; break; }
            int subElement = payload[idx++];
            if (subElement == 0xFF)
            {
                if (idx + 2 > payload.Length) { success = false; break; }
                subElement = payload[idx] | (payload[idx + 1] << 8);
                idx += 2;
            }

            // Decode AND mask (2 bytes LE)
            if (idx + 2 > payload.Length) { success = false; break; }
            int andMask = payload[idx] | (payload[idx + 1] << 8);
            idx += 2;

            // Decode OR mask (2 bytes LE)
            if (idx + 2 > payload.Length) { success = false; break; }
            int orMask = payload[idx] | (payload[idx + 1] << 8);
            idx += 2;

            // Perform atomic RMW using PlcMemory
            bool ok = _memory.ReadModifyWriteWithMasks(fileType, fileNumber, element, subElement, andMask, orMask);
            if (!ok)
            {
                success = false;
                break;
            }
        }

        if (success)
            SendEmptyResponse(src, tns, 0x4F, 0x26, clientContext);
        else
            SendErrorResponse(src, tns, 0x0F, 0x26, 0x10, clientContext);
    }

    // Typed Read/Write frame field sizes per 1770-6.5.16 §7-28 and §7-30.
    // Defined here independently of PCCCComm library to keep emulator self-contained.
    private const int TypedPacketOffsetBytes = 2;
    private const int TypedTotalTransBytes = 2;
    private const int TypedSizeBytes = 2;
    private const byte TypedSuccessReply = 0x4F; // CMD reply = 0x0F | 0x40

    /// <summary>
    /// Handles Typed Write (CMD=0x0F, FNC=0x67) for PLC-5.
    /// Request payload per 1770-6.5.16 §7-30:
    ///   [PktOff 2B LE] [TotTrans 2B LE] [logical binary address (variable)] [typeDataParam 1B] [data...]
    /// </summary>
    private void HandleTypedWriteRequest(int src, int tns, byte[] payload, object clientContext)
    {
        // Format: [PktOff 2B][TotTrans 2B][address...][typeDataParam][data]
        if (payload == null || payload.Length < 6)
        {
            SendErrorResponse(src, tns, 0x0F, 0x67, 0x01, clientContext);
            return;
        }

        int idx = 0;
        // Skip Packet Offset (2 bytes)
        idx += TypedPacketOffsetBytes;
        // Skip Total Transaction (2 bytes) – not used for write in emulator
        idx += TypedTotalTransBytes;

        if (!Plc5AddressDecoder.Decode(payload, ref idx,
                out int fileNumber, out int fileType, out int element, out int subElement))
        {
            SendErrorResponse(src, tns, 0x0F, 0x67, 0x01, clientContext);
            return;
        }
        fileType = Plc5AddressDecoder.Plc5ToSlcFileType(fileType);

        // Expect typeDataParam (0x31) but we don't need to validate unless desired
        if (idx >= payload.Length)
        {
            SendErrorResponse(src, tns, 0x0F, 0x67, 0x01, clientContext);
            return;
        }
        // Optionally check that the byte equals TypedTypeDataParamByteArray
        idx += 1;

        int bpe = _memory.GetBytesPerElement(fileType, fileNumber);
        // Align data length to element boundaries
        int dataBytes = ((payload.Length - idx) / bpe) * bpe;
        if (dataBytes <= 0)
        {
            SendErrorResponse(src, tns, 0x0F, 0x67, 0x01, clientContext);
            return;
        }

        int byteOffset = element * bpe + subElement * 2;
        byte[] data = new byte[dataBytes];
        Array.Copy(payload, idx, data, 0, dataBytes);

        bool ok = _memory.WriteRaw(fileType, fileNumber, byteOffset, dataBytes, data);
        if (ok) SendEmptyResponse(src, tns, TypedSuccessReply, 0x67, clientContext);
        else    SendErrorResponse(src, tns, 0x0F, 0x67, 0x10, clientContext);
    }

    /// <summary>
    /// Handles Typed Read (CMD=0x0F, FNC=0x68) for PLC-5.
    /// Request payload per 1770-6.5.16 §7-28:
    ///   [PktOff 2B LE] [TotTrans 2B LE] [logical binary address (variable)] [Size 2B LE (element count)]
    /// Reply: [typeDataParam var] [data]
    /// </summary>
    private void HandleTypedReadRequest(int src, int tns, byte[] payload, object clientContext)
    {
        // Format: [PktOff 2B][TotTrans 2B][address...][Size(elements) 2B]
        if (payload == null || payload.Length < 7)
        {
            SendErrorResponse(src, tns, 0x0F, 0x68, 0x01, clientContext);
            return;
        }

        int idx = 0;
        // Skip Packet Offset (2 bytes)
        idx += TypedPacketOffsetBytes;
        // Skip Total Transaction (2 bytes) – not used for read in emulator
        idx += TypedTotalTransBytes;

        if (!Plc5AddressDecoder.Decode(payload, ref idx,
                out int fileNumber, out int fileType, out int element, out int subElement))
        {
            SendErrorResponse(src, tns, 0x0F, 0x68, 0x01, clientContext);
            return;
        }
        fileType = Plc5AddressDecoder.Plc5ToSlcFileType(fileType);

        if (idx + TypedSizeBytes > payload.Length)
        {
            SendErrorResponse(src, tns, 0x0F, 0x68, 0x01, clientContext);
            return;
        }
        int elementCount = payload[idx] | (payload[idx + 1] << 8);

        int bpe = _memory.GetBytesPerElement(fileType, fileNumber);
        int bytesToRead = elementCount * bpe;
        int byteOffset = element * bpe + subElement * 2;

        byte[] data = _memory.ReadRaw(fileType, fileNumber, byteOffset, bytesToRead, out int status);
        if      (status == 2) SendErrorResponse(src, tns, 0x0F, 0x68, 0x50, clientContext);
        else if (status != 0) SendErrorResponse(src, tns, 0x0F, 0x68, 0x10, clientContext);
        else                  SendDataResponse(src, tns, TypedSuccessReply, data, clientContext);
    }

    /// <summary>
    /// Handles Word Range Read (CMD=0x0F, FNC=0x01).
    /// Supports PLC-5 standard (logical binary/ASCII) and RSLinx fixed 10-byte format.
    /// </summary>
    private void HandleWordRangeRead(int src, int tns, byte[] payload, object clientContext)
    {
        if (payload == null || payload.Length < 8)
        {
            SendErrorResponse(src, tns, 0x0F, 0x01, 0x10, clientContext);
            return;
        }

        if (!Plc5AddressDecoder.TryDecodeWordRangeAddress(payload,
                out int fileNumber, out int rawFileType,
                out int element,    out int subElement,
                out int wordOffset, out int sizeWords,
                out int dataStart, out bool isFlatFormat))
        {
            SendErrorResponse(src, tns, 0x0F, 0x01, 0x10, clientContext);
            return;
        }

        // Resolve actual SLC file type from wire information
        int fileType;
        if (isFlatFormat)
        {
            // Flat format: rawFileType not provided, get from memory by file number
            fileType = _memory.GetFileTypeForNumber(fileNumber);
            if (fileType == 0)
            {
                SendErrorResponse(src, tns, 0x0F, 0x01, 0x50, clientContext);
                return;
            }
        }
        else
        {
            // Standard format: rawFileType is PLC-5 wire code, convert to SLC type
            fileType = Plc5AddressDecoder.Plc5ToSlcFileType(rawFileType);
        }

        // ─── Guard: reject bit‑level subElement (EN/TT/DN etc.) ─────────────────
        int bpe = _memory.GetBytesPerElement(fileType, fileNumber);
        if (bpe > 0 && subElement * 2 >= bpe)
        {
            // Word Range Read does not support sub‑element access beyond word boundary
            SendErrorResponse(src, tns, 0x0F, 0x01, 0x10, clientContext);
            return;
        }

        int byteOffset  = wordOffset * 2 + element * bpe + subElement * 2;
        int bytesToRead = sizeWords * 2;
        int fileSize    = _memory.GetFileSize(fileType, fileNumber);

        if (fileSize == 0)
        {
            SendErrorResponse(src, tns, 0x0F, 0x01, 0x50, clientContext);
            return;
        }
        if (byteOffset < 0 || byteOffset + bytesToRead > fileSize)
        {
            SendErrorResponse(src, tns, 0x0F, 0x01, 0x10, clientContext);
            return;
        }

        byte[] data = _memory.ReadRaw(fileType, fileNumber, byteOffset, bytesToRead, out int status);
        // Swap word for RSLinx
        if (status == 0 && fileType == 0x8A)  // float file
        {
            for (int i = 0; i < data.Length; i += 4)
            {
                // swap word: word0 (bytes i..i+1) with word1 (bytes i+2..i+3)
                byte tmp0 = data[i];
                byte tmp1 = data[i+1];
                data[i]   = data[i+2];
                data[i+1] = data[i+3];
                data[i+2] = tmp0;
                data[i+3] = tmp1;
            }
        }
        if (status != 0 || data.Length != bytesToRead)
        {
            SendErrorResponse(src, tns, 0x0F, 0x01, 0x10, clientContext);
            return;
        }
        SendDataResponse(src, tns, 0x4F, data, clientContext);
    }

    /// <summary>
    /// Handles Word Range Write (CMD=0x0F, FNC=0x00) for PLC-5.
    /// See <see cref="TryDecodeWordRangeAddress"/> for supported wire formats.
    /// </summary>
    private void HandleWordRangeWrite(int src, int tns, byte[] payload, object clientContext)
    {
        if (!Plc5AddressDecoder.TryDecodeWordRangeAddress(payload,
                out int fileNumber, out int rawFileType,
                out int element,    out int subElement,
                out int wordOffset, out int sizeWords,
                out int dataStart, out bool isFlatFormat))
        {
            SendErrorResponse(src, tns, 0x0F, 0x00, 0x10, clientContext);
            return;
        }

        // Resolve actual SLC file type from wire information
        int fileType;
        if (isFlatFormat)
        {
            fileType = _memory.GetFileTypeForNumber(fileNumber);
            if (fileType == 0)
            {
                SendErrorResponse(src, tns, 0x0F, 0x00, 0x50, clientContext);
                return;
            }
        }
        else
        {
            fileType = Plc5AddressDecoder.Plc5ToSlcFileType(rawFileType);
        }

        // ─── Guard: reject bit‑level subElement (EN/TT/DN etc.) ─────────────────
        int bpe = _memory.GetBytesPerElement(fileType, fileNumber);
        if (bpe > 0 && subElement * 2 >= bpe)
        {
            // Word Range Write does not support sub‑element access beyond word boundary
            SendErrorResponse(src, tns, 0x0F, 0x00, 0x10, clientContext);
            return;
        }

        int bytesToWrite = sizeWords * 2;
        if (payload.Length < dataStart + bytesToWrite)
        {
            SendErrorResponse(src, tns, 0x0F, 0x00, 0x10, clientContext);
            return;
        }

        byte[] writeData = new byte[bytesToWrite];
        Array.Copy(payload, dataStart, writeData, 0, bytesToWrite);

        int byteOffset = wordOffset * 2 + element * bpe + subElement * 2;
        int fileSize   = _memory.GetFileSize(fileType, fileNumber);

        if (fileSize == 0)
        {
            SendErrorResponse(src, tns, 0x0F, 0x00, 0x50, clientContext);
            return;
        }
        if (byteOffset < 0 || byteOffset + bytesToWrite > fileSize)
        {
            SendErrorResponse(src, tns, 0x0F, 0x00, 0x10, clientContext);
            return;
        }

        if (fileType == 0x8A) // float file
        {
            // swap word
            for (int i = 0; i < writeData.Length; i += 4)
            {
                byte tmp0 = writeData[i];
                byte tmp1 = writeData[i+1];
                writeData[i]   = writeData[i+2];
                writeData[i+1] = writeData[i+3];
                writeData[i+2] = tmp0;
                writeData[i+3] = tmp1;
            }
        }

        bool ok = _memory.WriteRaw(fileType, fileNumber, byteOffset, bytesToWrite, writeData);
        if (ok)
            SendEmptyResponse(src, tns, 0x4F, 0x00, clientContext);
        else
            SendErrorResponse(src, tns, 0x0F, 0x00, 0x10, clientContext);
    }

    // ─── Response Helpers ────────────────────────────────────────────────────
    // All responses eventually call SendResponse() which delegates to the
    // active transport's SendResponse() method.
    //
    // withFunc convention:
    //   true  — FUNC byte is included in the response frame (ACK-style responses,
    //           loopback echoes, error responses, empty acknowledgements).
    //   false — FUNC byte is omitted; data begins at inner[6] (data responses,
    //           GetStatus, DiagnosticCounters, ReadModifiedData).
    //           DF1Comm reads returned data from DataPackets[rTNS][6] = DATA[0].

    /// <summary>Sends an empty acknowledgement response (no data payload, FUNC included).</summary>
    private void SendEmptyResponse(int dst, int tns, int cmd, int func, object clientContext)
        => SendResponse(dst, tns, cmd, func, 0x00, Array.Empty<byte>(), withFunc: true, clientContext);

    /// <summary>
    /// Sends a data response (data payload present, FUNC byte omitted).
    /// DF1Comm reads the returned data starting at inner[6] = DATA[0].
    /// The func parameter is accepted for call-site symmetry but is not
    /// written to the frame when withFunc is false.
    /// </summary>
    private void SendDataResponse(int dst, int tns, int cmd, byte[] data, object clientContext)
        => SendResponse(dst, tns, cmd, 0x00, 0x00, data, withFunc: false, clientContext);

    /// <summary>Get Status loopback response (CMD=0x06, FNC=0x00) — echoes request data, FUNC included.</summary>
    private void SendGetStatusLoopbackResponse(int dst, int tns, byte[] data, object clientContext)
        => SendResponse(dst, tns, 0x46, 0x00, 0x00, data, withFunc: true, clientContext);

    /// <summary>Diagnostic loopback response (CMD=0x06, FNC=0x02) — echoes request data, FUNC included.</summary>
    private void SendLoopbackResponse(int dst, int tns, byte[] data, object clientContext)
        => SendResponse(dst, tns, 0x46, 0x02, 0x00, data, withFunc: true, clientContext);

    /// <summary>Sends an error response with the reply bit set (cmd | 0x40), FUNC included.</summary>
    private void SendErrorResponse(int dst, int tns, int cmd, int func, byte status, object clientContext)
        => SendResponse(dst, tns, cmd | 0x40, func, status, Array.Empty<byte>(), withFunc: true, clientContext);

    /// <summary>
    /// Sends the GetStatus response (CMD=0x06, FNC=0x03).
    /// Uses cached payload for performance, clones it because TNS and DST change per request.
    /// Response CMD = 0x46 (0x06 | 0x40), sent WITHOUT FUNC byte.
    /// 
    /// Payload layout per Publication 1770-6.5.16 Chapter 10 (1747-L532):
    ///   Byte  0    : mode/status flags — bits 0-5 = 0, bit 6 = testing edits,
    ///                bit 7 = edits in processor. NOT the mode code.
    ///   Byte  1    : 0xEE — type extender
    ///   Byte  2    : 0x34 — extended interface type (DF1 full-duplex, port 0)
    ///   Byte  3    : 0x5B — extended processor type (SLC 5/04)
    ///   Byte  4    : series/revision
    ///   Byte  5–15 : bulletin number "5/04" in ASCII, space-padded to 11 bytes
    ///   Byte 16–17 : major error word (0x0000 = no fault)
    ///   Byte 18    : processor mode status/control low byte — mode code
    ///                  0x11 = local PROG   0x1E = local RUN
    ///                  0x17 = TEST-cont   0x18 = TEST-single   0x19 = TEST-step
    ///   Byte 19    : processor mode status/control high byte — fault flags
    ///   Byte 20–21 : program ID
    ///   Byte 22    : RAM size in Kbytes — 0x40 = 64 KB (1747-L542)
    ///   Byte 23    : flags (bits 2-7 = program owner node, 0x3F = no owner)
    /// </summary>
    private void SendGetStatusResponse(int dst, int tns, object clientContext)
    {
        byte[] payload;
        lock (_cacheLock)
        {
            payload = (byte[])_cachedGetStatusPayload.Clone();
        }
        SendResponse(dst, tns, 0x46, 0x03, 0x00, payload, withFunc: false, clientContext);
    }

    /// <summary>
    /// Diagnostic Counters response.
    /// - CMD 0x06 FNC 0x01 → replyCmd = 0x46
    /// - CMD 0x0A           → replyCmd = 0x4A
    /// 
    /// Layout per AB Application Note (March 1995), page 17:
    ///   Bytes  0-1  : RS-232 modem line status (CTS/RTS/DSR/DCD/DTR bits)
    ///   Bytes  2-3  : total message packets sent
    ///   Bytes  4-5  : total message packets received
    ///   Bytes  6-7  : undelivered message packets
    ///   Bytes  8-9  : ENQuiry packets sent
    ///   Bytes 10-11 : NAK packets received (normal poll last scan time for DF1)
    ///   Bytes 12-13 : ENQ packets received (normal poll max scan time for DF1)
    ///   Bytes 14-15 : bad message packets received and NAK'd
    ///   Bytes 16-17 : no buffer space and NAK'd (unused, must be 0 per AB spec)
    ///   Bytes 18-19 : duplicate message packets received
    ///   Bytes 20-21 : 00h (unused — priority poll times are DH485 only)
    ///   Bytes 22-23 : DCD recover field
    ///   Bytes 24-25 : lost modem field
    ///   Bytes 26-33 : 00h (unused) × 8
    /// 
    /// Total: 34 bytes (modem status word + 32 counter bytes).
    /// </summary>
    private void SendDiagnosticCountersResponse(int dst, int tns, int replyCmd, object clientContext)
    {
        UpdateModemStatus();
        ushort modemSnap = _modemStatus;

        // Read all counters atomically via Volatile.Read
        int sent        = Volatile.Read(ref _totalPacketsSent);
        int received    = Volatile.Read(ref _totalPacketsReceived);
        int undelivered = Volatile.Read(ref _undeliveredPackets);
        int enqSent     = Volatile.Read(ref _enqSent);
        int nakRecv     = Volatile.Read(ref _nakReceived);
        int enqRecv     = Volatile.Read(ref _enqReceived);
        int bad         = Volatile.Read(ref _badPacketsDetected);
        int noBuf       = Volatile.Read(ref _noBufferNakd);
        int dup         = Volatile.Read(ref _duplicatePacketsReceived);
        int dcd         = Volatile.Read(ref _dcdRecoveryCount);
        int lost        = Volatile.Read(ref _lostModemCount);

        // 34 bytes total for DF1 full-duplex (AB Application Note page 17)
        byte[] counters = new byte[34];

        void W(int idx, int val)
        {
            if (idx + 1 < counters.Length)
            {
                counters[idx]     = (byte)(val & 0xFF);
                counters[idx + 1] = (byte)((val >> 8) & 0xFF);
            }
        }

        W(0,  modemSnap);     // Bytes 0-1:   RS-232 modem line status
        W(2,  sent);          // Bytes 2-3:   total packets sent
        W(4,  received);      // Bytes 4-5:   total packets received
        W(6,  undelivered);   // Bytes 6-7:   undelivered packets
        W(8,  enqSent);       // Bytes 8-9:   ENQ packets sent
        W(10, nakRecv);       // Bytes 10-11: NAK packets received
        W(12, enqRecv);       // Bytes 12-13: ENQ packets received
        W(14, bad);           // Bytes 14-15: bad packets / NAK'd
        W(16, noBuf);         // Bytes 16-17: no buffer space (must be 0 per AB spec)
        W(18, dup);           // Bytes 18-19: duplicate packets received
        // Bytes 20-21: 00h unused (DF1 has no poll scan time fields)
        W(22, dcd);           // Bytes 22-23: DCD recovery count
        W(24, lost);          // Bytes 24-25: lost modem count
        // Bytes 26-33: 00h unused × 8 (automatically zero from array initialization)

        SendResponse(dst, tns, replyCmd, 0x00, 0x00, counters, withFunc: false, clientContext);
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
        Interlocked.Exchange(ref _nakReceived,              0);
        Interlocked.Exchange(ref _enqReceived,              0);
        Interlocked.Exchange(ref _badPacketsDetected,       0);
        Interlocked.Exchange(ref _noBufferNakd,             0);
        Interlocked.Exchange(ref _duplicatePacketsReceived, 0);
        Interlocked.Exchange(ref _dcdRecoveryCount,         0);
        Interlocked.Exchange(ref _lostModemCount,           0);
        Logger.Always(this, "Diagnostic counters reset to zero.");
    }

    /// <summary>
    /// Core response sender. Builds the inner frame PDU and delegates to the
    /// active transport's SendResponse() method for transport-specific framing.
    /// </summary>
    /// <param name="dst">Destination node address</param>
    /// <param name="tns">Transaction number (echoed from request)</param>
    /// <param name="cmd">Command code (may have reply bit set)</param>
    /// <param name="func">Function code; only written to frame when withFunc is true</param>
    /// <param name="status">Status code (0 for success)</param>
    /// <param name="data">Data payload (may be empty)</param>
    /// <param name="withFunc">True to include FUNC byte in frame, false to omit</param>
    /// <param name="clientContext">Context information for the client</param>
    private void SendResponse(int dst, int tns, int cmd, int func, byte status, byte[] data, bool withFunc, object clientContext)
    {
        int dataLen   = data?.Length ?? 0;
        int headerLen = withFunc ? 7 : 6;
        byte[] inner  = new byte[headerLen + dataLen];

        inner[0] = (byte)dst;
        inner[1] = (byte)_myNode;
        inner[2] = (byte)cmd;
        inner[3] = status;
        inner[4] = (byte)(tns & 0xFF);
        inner[5] = (byte)((tns >> 8) & 0xFF);
        if (withFunc)
            inner[6] = (byte)func;
        if (dataLen > 0)
            data?.CopyTo(inner, headerLen);

        _transport?.SendResponse(inner, clientContext);
    }

    // ─── Timers ──────────────────────────────────────────────────────────────
    /// <summary>
    /// Updates S2 date/time registers (S2:37–S2:42) every second.
    /// Only advances when processor is in RUN mode.
    /// </summary>
    private void UpdateDateTime()
    {
        if (_isDisposing != 0) return;
        if (!IsRunMode) return;
        var now = DateTime.Now;

        _memory.Write(0x84, 2, 37, 0, 2, BitConverter.GetBytes((short)now.Year));
        _memory.Write(0x84, 2, 38, 0, 2, BitConverter.GetBytes((short)now.Month));
        _memory.Write(0x84, 2, 39, 0, 2, BitConverter.GetBytes((short)now.Day));
        _memory.Write(0x84, 2, 40, 0, 2, BitConverter.GetBytes((short)now.Hour));
        _memory.Write(0x84, 2, 41, 0, 2, BitConverter.GetBytes((short)now.Minute));
        _memory.Write(0x84, 2, 42, 0, 2, BitConverter.GetBytes((short)now.Second));
    }

    /// <summary>
    /// Updates processor mode in S2:1 status word and the cached GetStatus
    /// payload whenever the mode changes (via CMD 0x0F FNC 0x80 or 0x3A).
    /// </summary>
    private void UpdateProcessorMode()
    {
        byte mode = (byte)ProcessorModeValue;
        if (_family == EmulationFamily.SlcMicroLogix)
        {
            byte[] current = _memory.ReadRaw(0x84, 2, 2, 2, out int status);
            if (status == 0 && current.Length == 2)
            {
                current[0] = mode;
                _memory.Write(0x84, 2, 1, 0, 2, current);
            }
        }
        // For PLC-5, the mode is only stored in the GetStatus payload cache, not in the memory file.
        lock (_cacheLock)
        {
            if (_family == EmulationFamily.SlcMicroLogix)
            {
                _cachedGetStatusPayload[18] = mode;
            }
            else if (_family == EmulationFamily.Ml1400)
            {
                // ML1400 mode byte is at offset 28 in GetStatus response
                // Mode values: 0x02 = RemoteRun, 0x00 = RemoteProg
                _cachedGetStatusPayload[28] = mode switch
                {
                    (byte)ProcessorMode.RemoteRun  => 0x02,
                    (byte)ProcessorMode.LocalRun   => 0x02,
                    (byte)ProcessorMode.RemoteProg => 0x00,
                    _ => 0x00
                };
            }
            else
            {
                // For PLC-5, we update byte 0 according to the operating status
                byte operatingStatus = mode switch
                {
                    0x1E => 2,  // LocalRun
                    0x06 => 6,  // RemoteRun
                    0x01 => 4,  // RemoteProg
                    _ => 0
                };
                _cachedGetStatusPayload[0] = operatingStatus;
            }
        }
    }

    /// <summary>
    /// Periodically updates F8:0 (sine wave) and F8:1 (triangle wave) based on real-time.
    /// This ensures waveform continuity even after disconnection/reconnection.
    /// Only updates when processor is in RUN mode.
    /// </summary>
    private void UpdateWaveform()
    {
        if (_isDisposing != 0) return;
        if (!IsRunMode) return;

        // F8:0 — Sine wave (amplitude 100, period 2 seconds)
        double now = DateTime.UtcNow.TimeOfDay.TotalSeconds;
        double sinePhase = (now % 2.0) / 2.0 * (2.0 * Math.PI);
        float sineValue = (float)(100.0 * Math.Sin(sinePhase));
        _memory.Write(0x8A, 8, 0, 0, 4, BitConverter.GetBytes(sineValue));

        // F8:1 — Triangle wave (amplitude 100, period 4 seconds)
        double t = now % 4.0;
        float triValue;
        if (t < 2.0)
            triValue = (float)(-100.0 + (t / 2.0) * 200.0);
        else
            triValue = (float)(100.0 - ((t - 2.0) / 2.0) * 200.0);
        _memory.Write(0x8A, 8, 1, 0, 4, BitConverter.GetBytes(triValue));
    }

    private byte[] BuildGetStatusPayload()
    {
        if (_family == EmulationFamily.Plc5)
            return BuildPlc5GetStatusPayload();
        if (_family == EmulationFamily.Ml1400)
            return BuildMl1400GetStatusPayload();
        return BuildSlcGetStatusPayload();
    }

    /// <summary>
    /// Builds the 24-byte GetStatus payload for the current processor mode.
    /// Called once at construction and again implicitly via UpdateProcessorMode()
    /// which patches byte 18 in-place on the cached copy.
    /// </summary>
    private byte[] BuildSlcGetStatusPayload()
    {
        byte[] payload = new byte[24];

        payload[0] = 0x00;      // Mode/status flags (no edits active)
        payload[1] = 0xEE;      // Type extender
        payload[2] = 0x34;      // Extended interface type (DF1 full-duplex)
        payload[3] = 0x5B;      // Extended processor type (SLC-5/04)
        payload[4] = 0x32;      // Series/revision

        // Bulletin number "5/04" in ASCII, space-padded to 11 bytes (bytes 5–15)
        string catalog = "5/04";
        byte[] catBytes = System.Text.Encoding.ASCII.GetBytes(catalog);
        Array.Copy(catBytes, 0, payload, 5, catBytes.Length);
        for (int i = 5 + catBytes.Length; i < 16; i++) payload[i] = 0x20;

        payload[16] = 0x00;     // Major error word (low byte)
        payload[17] = 0x00;     // Major error word (high byte)
        payload[18] = (byte)ProcessorModeValue;  // Processor mode code (patched by UpdateProcessorMode)
        payload[19] = 0x00;     // High byte (fault flags)
        payload[20] = 0x00;     // Program ID (low byte)
        payload[21] = 0x00;     // Program ID (high byte)
        payload[22] = 0x40;     // RAM size in Kbytes — 0x40 = 64 KB (1747-L542)
        payload[23] = 0x3F;     // Flags (no program owner, directory not corrupted)

        return payload;
    }

    /// <summary>
    /// Builds the 29-byte GetStatus payload for MicroLogix 1400 (1766-LEC).
    ///
    /// Byte layout derived from real hardware capture (1766-L32BWA Series C FRN 15.0):
    ///   [0]    = 0x00  mode/status flags
    ///   [1]    = 0xEE  type extender (SLC/ML family)
    ///   [2]    = 0x34  extended interface type
    ///   [3]    = 0x9F  processor type = ML1400
    ///   [4]    = 0x23  series/revision byte
    ///   [5-15] = "1766-LEC   " product name, space-padded to 11 bytes
    ///   [16]   = 0x00  major error word low
    ///   [17]   = 0x00  major error word high
    ///   [18]   = 0x26  firmware revision (FRN)
    ///   [19]   = 0x04  firmware revision minor
    ///   [20]   = 0x71  flags
    ///   [21]   = 0x43  flags
    ///   [22]   = 0x9E  flags
    ///   [23]   = 0xFC  flags
    ///   [24-27]= reserved
    ///   [28]   = mode  processor mode (0x02=RemoteRun, 0x00=RemoteProg)
    /// </summary>
    private byte[] BuildMl1400GetStatusPayload()
    {
        byte[] payload = new byte[29];

        payload[0]  = 0x00;     // Mode/status flags
        payload[1]  = 0xEE;     // Type extender (SLC/ML family)
        payload[2]  = 0x34;     // Extended interface type
        payload[3]  = 0x9F;     // Processor type = ML1400
        payload[4]  = 0x23;     // Series/revision byte

        // Product name "1766-LEC" space-padded to 11 bytes (bytes 5–15)
        string catalog = "1766-LEC";
        byte[] catBytes = System.Text.Encoding.ASCII.GetBytes(catalog);
        Array.Copy(catBytes, 0, payload, 5, catBytes.Length);
        for (int i = 5 + catBytes.Length; i < 16; i++) payload[i] = 0x20;

        payload[16] = 0x00;     // Major error word low
        payload[17] = 0x00;     // Major error word high
        payload[18] = 0x26;     // Firmware revision (FRN 15.0 encoded as 0x26=38? empirical)
        payload[19] = 0x04;     // Firmware revision minor
        payload[20] = 0x71;     // Flags (empirical from capture)
        payload[21] = 0x43;     // Flags
        payload[22] = 0x9E;     // Flags
        payload[23] = 0xFC;     // Flags
        payload[24] = 0x00;     // Reserved
        payload[25] = 0x00;     // Reserved
        payload[26] = 0x00;     // Reserved
        payload[27] = 0x00;     // Reserved
        payload[28] = (byte)ProcessorModeValue switch
        {
            (byte)ProcessorMode.RemoteRun  => 0x02,
            (byte)ProcessorMode.LocalRun   => 0x02,
            (byte)ProcessorMode.RemoteProg => 0x00,
            _ => 0x00
        };

        return payload;
    }

    /// <summary>
    /// Gets or sets the processor expansion byte for PLC-5 diagnostic status.
    /// Default 0x4B (1785-L40E). Other values: 0x4A=1785-L20E, 0x59=1785-L80E, etc.
    /// </summary>
    public int Plc5ProcessorExpansionByte { get; set; } = 0x4B;

    private byte[] BuildPlc5GetStatusPayload()
    {
        // PLC-5 status layout per 1770-6.5.16 Chapter 10, page 10-22 (36 bytes)
        byte[] payload = new byte[36];

        // Byte 1 (index 0): operating status (bits 0-2)
        //   0=PROG, 2=Local Run, 4=Remote PROG, 6=Remote Run
        byte operatingStatus;
        switch (ProcessorModeValue)
        {
            case ProcessorMode.LocalRun:   operatingStatus = 2; break;
            case ProcessorMode.RemoteRun:  operatingStatus = 6; break;
            case ProcessorMode.RemoteProg: operatingStatus = 4; break;
            default:                       operatingStatus = 0; break;
        }
        payload[0] = operatingStatus;

        // Byte 2 (index 1): Processor Type (low nibble 0xB = PLC-5) and Expansion flag (high nibble 0xE)
        payload[1] = 0xEB;   // 0xE0 | 0x0B

        // Byte 3 (index 2): Processor Expansion Byte (default 1785-L40E)
        payload[2] = (byte)Plc5ProcessorExpansionByte; // property, default 0x4B

        // Bytes 4-7 (index 3-6): size of user memory in words (32-bit LE, 64K words)
        payload[3] = 0x00;
        payload[4] = 0x00;
        payload[5] = 0x01;
        payload[6] = 0x00;

        // Byte 8 (index 7): series/revision (bits 0-4 revision, bits 5-7 series)
        payload[7] = 0x32;

        // Byte 9 (index 8): processor number on DH+ link
        payload[8] = 0x01;

        // Byte 10 (index 9): I/O address (0xFD = scanner)
        payload[9] = 0xFD;

        // Byte 11 (index 10): I/O and communication parameters (double density + 115K baud)
        payload[10] = 0x21;

        // Bytes 12-13 (index 11-12): number of data table files (LE, 32 files)
        payload[11] = 0x20;
        payload[12] = 0x00;

        // Bytes 14-15 (index 13-14): number of program type files (LE, 24 files)
        payload[13] = 0x18;
        payload[14] = 0x00;

        // Byte 16 (index 15): forcing status
        payload[15] = 0x00;
        // Byte 17 (index 16): memory protect indication
        payload[16] = 0x00;
        // Byte 18 (index 17): bad RAM indication
        payload[17] = 0x00;
        // Byte 19 (index 18): debug mode
        payload[18] = 0x00;

        // Bytes 20-21 (index 19-20): hold point file (LE)
        payload[19] = 0x00;
        payload[20] = 0x00;
        // Bytes 22-23 (index 21-22): hold point element (LE)
        payload[21] = 0x00;
        payload[22] = 0x00;

        // Bytes 24-25 (index 23-24): edit timestamp seconds (LE)
        payload[23] = 0x00;
        payload[24] = 0x00;
        // Bytes 26-27 (index 25-26): edit timestamp minutes (LE)
        payload[25] = 0x00;
        payload[26] = 0x00;
        // Bytes 28-29 (index 27-28): edit timestamp hours (LE)
        payload[27] = 0x00;
        payload[28] = 0x00;
        // Bytes 30-31 (index 29-30): edit timestamp day (LE)
        payload[29] = 0x00;
        payload[30] = 0x00;
        // Bytes 32-33 (index 31-32): edit timestamp month (LE)
        payload[31] = 0x00;
        payload[32] = 0x00;
        // Bytes 34-35 (index 33-34): edit timestamp year (LE)
        payload[33] = 0x00;
        payload[34] = 0x00;

        // Byte 36 (index 35): port number this command received on
        payload[35] = 0x00;

        return payload;
    }
}
