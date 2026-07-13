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

using System.Collections.ObjectModel;
using System.Threading;
using System.Text;
using System.Xml;
using PCCCComm.Core;
using PCCCComm.Handlers;
using PCCCComm.Pccc;

namespace PCCCComm;

/// <summary>
/// PCCC application layer for Allen‑Bradley PLCs (DF1, EIP, CSPv4).
/// Facade for PcccProtocol, handling chunking, data conversion, upload/download.
/// </summary>
public class PCCCComm : IDisposable, IHandlerContext
{
    // ─── Fields ─────────────────────────────────────────────────────────────
    private PCCCConstants.ProcessorFamily _processorFamily = PCCCConstants.ProcessorFamily.Unknown;
    private int _processorType;  // cached processor type from diagnostic status

    private volatile bool _disableEvent;            // suppress DataReceived during bulk transfers

    // CancellationToken for the current bulk transfer (upload/download).
    // Reset to None after the operation completes.
    private CancellationToken _cancellationToken = CancellationToken.None;

    private ITransport? _currentTransport;
    private readonly string? _remoteHost;
    private readonly int _remotePort;
    private readonly NetworkTransportType _networkType = NetworkTransportType.None;
    private readonly byte _lsapControlByte = 0x00;   // for CSP only

    private enum NetworkTransportType
    {
        None,   // for serial (DF1/DF1Master)
        EIP,
        CSP
    }

    // DF1Master configuration
    private int _slaveAddress = 1;
    private DF1HalfDuplexTransport.Rs485ControlMode _rs485Mode = DF1HalfDuplexTransport.Rs485ControlMode.Auto;
    private int _rtsAssertDelayMs = 1;
    private int _rtsDeassertDelayMs = 5;
    private bool _echoSuppression;

    // PCCC engine
    private PCCCProtocol? _protocol;
    private IPlcHandler? _handler;

    // ─── Properties (exactly as original) ──────────────────────────────────
    public int MyNode { get; set; }
    public int TargetNode { get; set; }

    private int _baudRate = 19200;
    public int BaudRate
    {
        get => _baudRate;
        set { if (value != _baudRate) CloseComms(); _baudRate = value; }
    }

    private string _comPort = "COM1";
    public string ComPort
    {
        get => _comPort;
        set { if (value != _comPort) CloseComms(); _comPort = value; }
    }

    private System.IO.Ports.Parity _parity = System.IO.Ports.Parity.None;
    public System.IO.Ports.Parity Parity
    {
        get => _parity;
        set { if (value != _parity) CloseComms(); _parity = value; }
    }

    private string _protocolName = "DF1";
    public string Protocol
    {
        get => _protocolName;
        set
        {
            if (value != "DF1" && value != "DF1Master")
                throw new NotSupportedException($"Protocol '{value}' is not supported. Only 'DF1' or 'DF1Master' are supported.");
            _protocolName = value;
        }
    }

    private CheckSumOptions _checkSum = CheckSumOptions.Crc;
    public CheckSumOptions CheckSum
    {
        get => _checkSum;
        set
        {
            _checkSum = value;
            if (_currentTransport is DF1BaseTransport df1)
                df1.ChecksumType = value;
        }
    }

    public bool AsyncMode { get; set; }

    public int SlaveAddress
    {
        get => _slaveAddress;
        set
        {
            if (value < 1 || value > 254)
                throw new ArgumentOutOfRangeException(nameof(SlaveAddress), "Address must be 1-254.");
            _slaveAddress = value;
            if (_currentTransport is DF1HalfDuplexTransport master)
                master.SlaveAddress = value;
        }
    }

    public DF1HalfDuplexTransport.Rs485ControlMode Rs485Mode
    {
        get => _rs485Mode;
        set
        {
            _rs485Mode = value;
            if (_currentTransport is DF1HalfDuplexTransport master)
                master.Rs485Mode = value;
        }
    }

    public int Rs485AssertDelayMs
    {
        get => _rtsAssertDelayMs;
        set
        {
            _rtsAssertDelayMs = Math.Max(0, value);
            if (_currentTransport is DF1HalfDuplexTransport master)
                master.RtsAssertDelayMs = _rtsAssertDelayMs;
        }
    }

    public int Rs485DeassertDelayMs
    {
        get => _rtsDeassertDelayMs;
        set
        {
            _rtsDeassertDelayMs = Math.Max(0, value);
            if (_currentTransport is DF1HalfDuplexTransport master)
                master.RtsDeassertDelayMs = _rtsDeassertDelayMs;
        }
    }

    public bool EchoSuppression
    {
        get => _echoSuppression;
        set
        {
            _echoSuppression = value;
            if (_currentTransport is DF1HalfDuplexTransport master)
                master.EchoSuppression = value;
        }
    }

    private int _responseTimeoutMs = 2000;
    public int ResponseTimeoutMs
    {
        get => _responseTimeoutMs;
        set
        {
            _responseTimeoutMs = value > 0 ? value : 2000;
            if (_protocol != null)
                _protocol.ResponseTimeoutMs = _responseTimeoutMs;
            if (_currentTransport is DF1BaseTransport df1)
                df1.MaxTicks = _responseTimeoutMs / 20;
        }
    }

    // --- Circuit Breaker & Bulkhead (Anti-Starvation) ---------------------

    private int _maxConcurrentRequests = 10;
    /// <summary>
    /// Gets or sets the maximum number of concurrent PCCC requests.
    /// Limits parallel polling to avoid thread pool exhaustion when the PLC is slow or offline.
    /// Default is 10.
    /// </summary>
    public int MaxConcurrentRequests
    {
        get => _maxConcurrentRequests;
        set
        {
            if (value > 0)
            {
                _maxConcurrentRequests = value;
                if (_protocol != null)
                    _protocol.MaxConcurrentRequests = value;
            }
        }
    }

    private int _maxConsecutiveTimeouts = 3;
    /// <summary>
    /// Gets or sets the number of consecutive timeouts that will trip the circuit breaker.
    /// Once tripped, all requests fail immediately without waiting for a timeout.
    /// Default is 3.
    /// </summary>
    public int MaxConsecutiveTimeouts
    {
        get => _maxConsecutiveTimeouts;
        set
        {
            if (value > 0)
            {
                _maxConsecutiveTimeouts = value;
                if (_protocol != null)
                    _protocol.MaxConsecutiveTimeouts = value;
            }
        }
    }

    /// <summary>
    /// Manually resets the circuit breaker. Only call this when you are sure
    /// the PLC is responsive (e.g., after a successful probe).
    /// </summary>
    public void ResetCircuitBreaker()
    {
        _protocol?.ResetHealth();
    }

    // ─── Events ────────────────────────────────────────────────────────────
    public event EventHandler? DataReceived;
    public event EventHandler? UnsolicitedMessageRcvd;
    public event EventHandler? AutoDetectTry;
    public event EventHandler<FileProgressEventArgs>? FileProgress;
    public event EventHandler<byte[]>? RawFrameSent;
    public event EventHandler<byte[]>? RawFrameReceived;

    public class FileProgressEventArgs : EventArgs
    {
        public int FileNumber { get; set; }
        public int FileType { get; set; }
        public int FileSizeBytes { get; set; }
        public int FilesCompleted { get; set; }
        public int TotalFiles { get; set; }
        public long TotalBytesTransferred { get; set; }
        public long GrandTotalBytes { get; set; }
    }

    bool IHandlerContext.DisableEvent
    {
        get => _disableEvent;
        set => _disableEvent = value;
    }

    void IHandlerContext.RaiseFileProgress(FileProgressEventArgs e)
    => FileProgress?.Invoke(this, e);

    CancellationToken IHandlerContext.CancellationToken => _cancellationToken;

    int IHandlerContext.MyNode => MyNode;
    int IHandlerContext.TargetNode => TargetNode;
    bool IHandlerContext.AsyncMode => AsyncMode;

    // ─── Constructors ──────────────────────────────────────────────────────

    /// <summary>
    /// PCCC application layer for Allen‑Bradley PLCs (DF1, DF1Master, EIP, CSPv4).
    /// Use <see cref="ForEip"/> or <see cref="ForCsp"/> for network transports.
    /// </summary>
    public PCCCComm(string? portName = null, int baud = 19200,
                    System.IO.Ports.Parity parity = System.IO.Ports.Parity.None)
    {
        if (!string.IsNullOrEmpty(portName))
        {
            _comPort = portName!;
            _baudRate = baud;
            _parity = parity;
        }
    }

    private PCCCComm(NetworkTransportType networkType, string host, int port, int timeoutMs, byte lsapControlByte)
    {
        _responseTimeoutMs = timeoutMs;
        _remoteHost = host;
        _remotePort = port;
        _networkType = networkType;
        _lsapControlByte = lsapControlByte;
    }

    /// <summary>Creates a PCCCComm instance for EtherNet/IP communication.
    /// The connection is not opened automatically; call <see cref="OpenComms"/> to establish the session.
    /// </summary>
    /// <param name="host">IP address or hostname of the EIP device.</param>
    /// <param name="port">EIP TCP port (default 44818).</param>
    /// <param name="timeoutMs">Response timeout in milliseconds.</param>
    public static PCCCComm ForEip(string host, int port = 44818, int timeoutMs = 5000)
        => new(NetworkTransportType.EIP, host, port, timeoutMs, lsapControlByte: 0x00);

    /// <summary>
    /// Creates a PCCCComm instance for CSPv4 (Client Server Protocol) communication.
    /// </summary>
    /// <param name="host">IP address or hostname of the CSPv4 device (PLC-5E/SLC 5/05).</param>
    /// <param name="port">CSPv4 TCP port (default 2222).</param>
    /// <param name="timeoutMs">Response timeout in milliseconds.</param>
    /// <param name="lsapControlByte">LSAP control byte (default 0x00; use 0x05 for RSLinx).</param>
    /// <example>
    /// var comm = PCCCComm.ForCsp("192.168.1.80", 2222, 5000, 0x05);
    /// </example>
    public static PCCCComm ForCsp(string host, int port = 2222, int timeoutMs = 5000, byte lsapControlByte = 0x00)
        => new(NetworkTransportType.CSP, host, port, timeoutMs, lsapControlByte);

    private void AttachTransportEvents()
    {
        if (_currentTransport == null) return;
        _currentTransport.FrameReceived += OnFrameReceived;
        _currentTransport.RawFrameSent += OnRawFrameSent;
        _currentTransport.RawFrameReceived += OnRawFrameReceived;
    }

    private void DetachTransportEvents()
    {
        if (_currentTransport == null) return;
        _currentTransport.FrameReceived -= OnFrameReceived;
        _currentTransport.RawFrameSent -= OnRawFrameSent;
        _currentTransport.RawFrameReceived -= OnRawFrameReceived;
    }

    private void OnRawFrameSent(object? sender, byte[] e) => RawFrameSent?.Invoke(this, e);
    private void OnRawFrameReceived(object? sender, byte[] e) => RawFrameReceived?.Invoke(this, e);

    // ─── Helper for selecting handler ──────────────────────────────────────
    private PCCCConstants.ProcessorFamily GetProcessorFamily()
    {
        if (_processorFamily != PCCCConstants.ProcessorFamily.Unknown)
            return _processorFamily;

        EnsureProtocol();
        byte[]? diagData = _protocol!.GetDiagnosticStatusRaw((byte)MyNode, (byte)TargetNode);
        if (diagData == null)
            throw new PCCCException("Failed to retrieve diagnostic status for processor family detection.");

        _processorFamily = PCCCConstants.DetectFamily(diagData);
        
        // Save processor type (byte offset 3) if available
        if (diagData.Length > PCCCConstants.ResponseOffsets.DiagnosticStatus.ProcessorType)
        {
            _processorType = diagData[PCCCConstants.ResponseOffsets.DiagnosticStatus.ProcessorType];
        }
        
        return _processorFamily;
    }

    private void EnsureHandler()
    {
        if (_handler != null) return;
        EnsureProtocol();

        PCCCConstants.ProcessorFamily family = GetProcessorFamily(); // also fill in _processorType
        switch (family)
        {
            case PCCCConstants.ProcessorFamily.SlcMicroLogix:
                _handler = new SlcHandler(this, _protocol!, _processorType);
                break;
            case PCCCConstants.ProcessorFamily.Plc5:
                _handler = new Plc5Handler(this, _protocol!, _processorType);
                break;
            case PCCCConstants.ProcessorFamily.Unknown when _processorType != 0:
                // DetectFamily could not identify the family from the type-extender byte
                // (e.g. some DF1 responses omit it or use a non-standard value), but we
                // do have a valid processor type code. Fall back to SlcHandler which
                // covers the broadest range of PCCC-compatible PLCs.
                _handler = new SlcHandler(this, _protocol!, _processorType);
                break;
            case PCCCConstants.ProcessorFamily.Plc3:
            case PCCCConstants.ProcessorFamily.Plc2:
            default:
                throw new NotSupportedException($"Processor family '{family}' is not supported.");
        }
    }

    // ─── Public API (delegated to handler) ─────────────────────────────────
    /// <summary>Places the processor in Run mode.</summary>
    public void SetRunMode()
    {
        EnsureHandler();
        _handler!.SetRunMode();
    }

    /// <summary>Sets the CPU mode using a raw mode value.</summary>
    public int SetCpuMode(byte modeValue)
    {
        EnsureHandler();
        return _handler!.SetCpuMode(modeValue);
    }

    /// <summary>Returns 1 if the processor is in Run mode, 0 if not in Run mode,
    /// or -1 if the diagnostic status could not be retrieved.</summary>
    public int GetRunMode()
    {
        EnsureHandler();
        return _handler!.GetRunMode();
    }

    /// <summary>Disables forces on the processor.</summary>
    public int DisableForces()
    {
        EnsureHandler();
        return _handler!.DisableForces();
    }

    /// <summary>Enables forces on the processor (SLC/MicroLogix only).</summary>
    public void EnableForces()
    {
        EnsureHandler();
        _handler!.EnableForces();
    }

    /// <summary>Clears all forces from the processor (SLC/MicroLogix only).</summary>
    public void ClearForces()
    {
        EnsureHandler();
        _handler!.ClearForces();
    }

    /// <summary>Places the processor in Program mode.</summary>
    public void SetProgramMode()
    {
        EnsureHandler();
        _handler!.SetProgramMode();
    }

    /// <summary>Returns the processor type code (e.g., 0x49 for SLC 5/03).</summary>
    public int GetProcessorType()
    {
        EnsureHandler();
        return _handler!.GetProcessorType();
    }

    /// <summary>Returns raw diagnostic status data.</summary>
    public byte[]? GetDiagnosticStatusRaw()
    {
        EnsureHandler();
        return _handler!.GetDiagnosticStatusRaw();
    }

    /// <summary>
    /// Sends an Echo command (CMD 0x06 FNC 0x00) and returns the echoed payload.
    /// Supported by all PCCC-compatible PLCs (SLC, MicroLogix, PLC-5).
    /// Useful as a lightweight connectivity probe or round-trip latency check.
    /// </summary>
    /// <param name="data">Payload to echo (max 243 bytes per AB spec).</param>
    /// <returns>Echoed bytes — should match <paramref name="data"/> exactly.</returns>
    public byte[] Echo(byte[] data)
    {
        EnsureHandler();
        return _handler!.Echo(data);
    }

    /// <summary>Reads raw 16-bit words from the specified PCCC address.</summary>
    public ushort[] ReadWords(string startAddress, int numberOfWords)
    {
        EnsureHandler();
        return _handler!.ReadWords(startAddress, numberOfWords);
    }

    /// <summary>Writes raw 16-bit words to the specified PCCC address.</summary>
    public void WriteWords(string startAddress, ushort[] data)
    {
        EnsureHandler();
        _handler!.WriteWords(startAddress, data);
    }

    /// <summary>Reads data from the specified address and returns it as strings.</summary>
    public string[] ReadAny(string startAddress, int numberOfElements)
    {
        EnsureHandler();
        return _handler!.ReadAny(startAddress, numberOfElements);
    }

    /// <summary>Reads a single element from the specified address.</summary>
    public string ReadAny(string startAddress) => ReadAny(startAddress, 1)[0];

    /// <summary>Reads integer values from the specified address.</summary>
    public int[] ReadInt(string startAddress, int numberOfElements)
    {
        EnsureHandler();
        return _handler!.ReadInt(startAddress, numberOfElements);
    }

    /// <summary>
    /// Reads numeric data from the specified address and returns raw values as doubles.
    /// This method is more efficient than <see cref="ReadAny(string, int)"/> for SCADA polling
    /// because it avoids string allocation and parsing.
    /// </summary>
    /// <param name="startAddress">PCCC address (e.g., "N7:0", "F8:0", "T4:0.ACC", "B3:0/5").</param>
    /// <param name="numberOfElements">Number of elements to read.</param>
    /// <returns>Array of double values.</returns>
    /// <exception cref="NotSupportedException">Thrown for String (ST) files.</exception>
    public double[] ReadAnyValues(string startAddress, int numberOfElements)
    {
        EnsureHandler();
        return _handler!.ReadAnyValues(startAddress, numberOfElements);
    }

   /// <summary>Reads a single element from the specified address.</summary>
    public double ReadAnyValues(string startAddress) => ReadAnyValues(startAddress, 1)[0];

    /// <summary>Performs a read-modify-write operation on multiple addresses.</summary>
    public int ReadModifyWrite(string[] addresses, ushort[] andMasks, ushort[] orMasks)
    {
        EnsureHandler();
        return _handler!.ReadModifyWrite(addresses, andMasks, orMasks);
    }

    /// <summary>Writes an integer value to the specified address.</summary>
    public string WriteData(string startAddress, int dataToWrite)
    {
        EnsureHandler();
        return _handler!.WriteData(startAddress, dataToWrite);
    }

    /// <summary>Writes multiple integer values to the specified address.</summary>
    public int WriteData(string startAddress, int numberOfElements, int[] dataToWrite)
    {
        EnsureHandler();
        return _handler!.WriteData(startAddress, numberOfElements, dataToWrite);
    }

    /// <summary>Writes a float value to the specified address.</summary>
    public int WriteData(string startAddress, float dataToWrite)
    {
        EnsureHandler();
        return _handler!.WriteData(startAddress, dataToWrite);
    }

    /// <summary>Writes multiple float values to the specified address.</summary>
    public int WriteData(string startAddress, int numberOfElements, float[] dataToWrite)
    {
        EnsureHandler();
        return _handler!.WriteData(startAddress, numberOfElements, dataToWrite);
    }

    /// <summary>Writes a string to an ST file or word-packed integer file.</summary>
    public int WriteData(string startAddress, string dataToWrite)
    {
        EnsureHandler();
        return _handler!.WriteData(startAddress, dataToWrite);
    }

    /// <summary>Uploads the entire program and data from the PLC.</summary>
    public Collection<PLCFileDetails> UploadProgramData()
        => UploadProgramData(CancellationToken.None);

    /// <summary>Uploads the entire program and data from the PLC.</summary>
    /// <param name="cancellationToken">Token to cancel the operation between files.</param>
    public Collection<PLCFileDetails> UploadProgramData(CancellationToken cancellationToken)
    {
        EnsureHandler();
        _cancellationToken = cancellationToken;
        try   { return _handler!.UploadProgramData(); }
        finally { _cancellationToken = CancellationToken.None; }
    }

    /// <summary>Downloads a program to the PLC.</summary>
    public void DownloadProgramData(Collection<PLCFileDetails> plcFiles)
        => DownloadProgramData(plcFiles, CancellationToken.None);

    /// <summary>Downloads a program to the PLC.</summary>
    /// <param name="cancellationToken">Token to cancel the operation between files.</param>
    public void DownloadProgramData(Collection<PLCFileDetails> plcFiles, CancellationToken cancellationToken)
    {
        EnsureHandler();
        _cancellationToken = cancellationToken;
        try   { _handler!.DownloadProgramData(plcFiles); }
        finally { _cancellationToken = CancellationToken.None; }
    }

    /// <summary>Returns the number of slots in the chassis.</summary>
    public int GetSlotCount()
    {
        EnsureHandler();
        return _handler!.GetSlotCount();
    }

    /// <summary>Returns I/O configuration for all slots.</summary>
    public IOConfig[] GetIOConfig()
    {
        EnsureHandler();
        return _handler!.GetIOConfig();
    }

    /// <summary>
    /// Returns a list of data files present in the processor.
    /// For MicroLogix 1400 the directory is read directly over PCCC (like RSLinx Data
    /// Monitor); other processors delegate to the protocol handler's directory parsing.
    /// </summary>
    public DataFileDetails[] GetDataMemory()
    {
        EnsureHandler();
        return _handler!.GetDataMemory();
    }

    /// <summary>Word Range Read for PLC-5 (FNC=0x01).</summary>
    public byte[] WordRangeRead(byte[] logicalAddress, int wordOffset, int sizeWords)
    {
        EnsureHandler();
        if (_handler is Plc5Handler plc5)
            return plc5.WordRangeRead(logicalAddress, wordOffset, sizeWords);
        throw new NotSupportedException("WordRangeRead is only supported for PLC-5 processors.");
    }

    /// <summary>Word Range Write for PLC-5 (FNC=0x00).</summary>
    /// <summary>Typed Read (PLC-5, FNC 0x68). Returns element data bytes (descriptor stripped).</summary>
    public byte[] TypedRead(byte[] logicalAddress, int elementCount)
    {
        EnsureHandler();
        if (_handler is Plc5Handler plc5) return plc5.TypedReadRaw(logicalAddress, elementCount);
        throw new NotSupportedException("TypedRead is only supported for PLC-5 processors.");
    }

    /// <summary>Typed Write (PLC-5, FNC 0x67). Caller supplies the type/data parameter for the target type.</summary>
    public void TypedWrite(byte[] logicalAddress, byte[] typeDataParam, byte[] data, int elementCount)
    {
        EnsureHandler();
        if (_handler is Plc5Handler plc5) { plc5.TypedWriteRaw(logicalAddress, typeDataParam, data, elementCount); return; }
        throw new NotSupportedException("TypedWrite is only supported for PLC-5 processors.");
    }

    public void WordRangeWrite(byte[] logicalAddress, int wordOffset, byte[] data)
    {
        EnsureHandler();
        if (_handler is Plc5Handler plc5)
            plc5.WordRangeWrite(logicalAddress, wordOffset, data);
        else
            throw new NotSupportedException("WordRangeWrite is only supported for PLC-5 processors.");
    }

    // ─── Comms management (unchanged from original) ────────────────────────
    
    private void EnsureProtocol()
    {
        if (_protocol == null && _currentTransport != null)
            _protocol = new PCCCProtocol(_currentTransport) { ResponseTimeoutMs = _responseTimeoutMs };
        if (_protocol == null)
            throw new InvalidOperationException("Communications not open. Call OpenComms() first.");
    }

    public int OpenComms()
    {
        if (_currentTransport != null)
        {
            if (!_currentTransport.IsOpen)
                _currentTransport.Open();
            EnsureProtocol();
            EnsureHandler();
            return 0;
        }

        if (_remoteHost != null)
        {
            try
            {
                ITransport transport = _networkType switch
                {
                    NetworkTransportType.EIP => new EIPTransport(_remoteHost, _remotePort, _responseTimeoutMs),
                    NetworkTransportType.CSP => new CSPTransport(_remoteHost, _remotePort, _responseTimeoutMs, _lsapControlByte),
                    _ => throw new InvalidOperationException($"Unsupported network transport type: {_networkType}")
                };

                _currentTransport = transport;
                AttachTransportEvents();
                transport.Open();
                EnsureProtocol();
                EnsureHandler();
                return 0;
            }
            catch (Exception ex)
            {
                throw new PCCCException($"Failed to connect to {_remoteHost}:{_remotePort}. {ex.Message}");
            }
        }

        try
        {
            var port = new SerialPortWrapper(_comPort, _baudRate, _parity);
            ITransport transport;
            if (_protocolName == "DF1Master")
            {
                var master = new DF1HalfDuplexTransport(port);
                master.ChecksumType = _checkSum;
                master.MaxTicks = _responseTimeoutMs / 20;
                master.SlaveAddress = _slaveAddress;
                master.Rs485Mode = _rs485Mode;
                master.RtsAssertDelayMs = _rtsAssertDelayMs;
                master.RtsDeassertDelayMs = _rtsDeassertDelayMs;
                master.EchoSuppression = _echoSuppression;
                transport = master;
            }
            else
            {
                var full = new DF1FullDuplexTransport(port);
                full.ChecksumType = _checkSum;
                full.MaxTicks = _responseTimeoutMs / 20;
                transport = full;
            }
            _currentTransport = transport;
            AttachTransportEvents();

            // Open the physical port first. A failure here is genuinely a port problem
            // (missing device, permissions, already in use) and is reported as such.
            try
            {
                transport.Open();
            }
            catch (Exception ex)
            {
                throw new PCCCException("Failed To Open " + _comPort + ". " + ex.Message);
            }

            // From here the port is open; the remaining steps talk to the PLC. A failure
            // in EnsureHandler (processor-family diagnostic probe) means the PLC did not
            // respond — NOT that the port failed to open. Let that exception propagate with
            // its own message ("Failed to retrieve diagnostic status...") instead of
            // mislabeling it as a port-open failure, which previously sent troubleshooting
            // in the wrong direction (checking cables/permissions when the serial link was
            // fine and only the PLC was unresponsive).
            EnsureProtocol();
            EnsureHandler();
            return 0;
        }
        catch (PCCCException)
        {
            // Already a well-formed PCCC error (port-open failure above, or a PLC-side
            // diagnostic/protocol failure). Propagate as-is.
            throw;
        }
        catch (Exception ex)
        {
            // Unexpected non-PCCC failure during setup.
            throw new PCCCException("Failed to open serial communication on " + _comPort + ". " + ex.Message);
        }
    }

    public void CloseComms()
    {
        _protocol?.Dispose();
        _protocol = null;
        if (_currentTransport != null)
        {
            DetachTransportEvents();
            _currentTransport.Close();
            _currentTransport.Dispose();
            _currentTransport = null;
        }
        _handler = null; // handler also depends on protocol and transport
        // _processorFamily and _processorType are NOT reset —
        // The hardware is unchanged, so re-detection is not required upon reconnection.
    }

    public int DetectCommSettings()
        => DetectCommSettings(CancellationToken.None);

    /// <param name="cancellationToken">Token to cancel between baud rate attempts.</param>
    public int DetectCommSettings(CancellationToken cancellationToken)
    {
        CloseComms();

        int[] baudRates = { 38400, 19200, 9600 };
        var parities = new System.IO.Ports.Parity[] { System.IO.Ports.Parity.None, System.IO.Ports.Parity.Even };
        var checksums = new CheckSumOptions[] { CheckSumOptions.Crc, CheckSumOptions.Bcc };

        int reply = -1;
        bool portError = false;

        string originalPort = _comPort;
        int originalBaud = _baudRate;
        var originalParity = _parity;
        var originalChecksum = _checkSum;

        foreach (int baud in baudRates)
        {
            if (reply == 0 || portError) break;
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var parity in parities)
            {
                if (reply == 0 || portError) break;
                foreach (var cs in checksums)
                {
                    _baudRate = baud;
                    _parity = parity;
                    _checkSum = cs;

                    AutoDetectTry?.Invoke(this, EventArgs.Empty);

                    try
                    {
                        var port = new SerialPortWrapper(_comPort, _baudRate, _parity);
                        using var transport = new DF1FullDuplexTransport(
                            new SerialPortWrapper(_comPort, _baudRate, _parity));
                        transport.ChecksumType = _checkSum;
                        transport.MaxTicks = 3;
                        transport.Open();
                        reply = transport.SendEnqAndWaitForAck();
                        transport.Close();
                        if (reply == 0) break;
                    }
                    catch (UnauthorizedAccessException) { portError = true; reply = -6; break; }
                    catch (Exception) { reply = -6; }
                    if (reply == -6) { portError = true; break; }
                }
            }
        }

        if (reply != 0)
        {
            _comPort = originalPort;
            _baudRate = originalBaud;
            _parity = originalParity;
            _checkSum = originalChecksum;
        }
        return reply;
    }

    // ─── Frame received handler (for unsolicited messages) ─────────────────
    private void OnFrameReceived(object? sender, byte[] innerFrame)
    {
        if (innerFrame.Length < 6) return;

        // Parse into a PCCCMessage so field names are used instead of raw indices.
        var msg = PCCCMessage.FromBytes(innerFrame);

        // Check if this is a reply frame: bit 6 (0x40) of the CMD byte is set.
        const byte replyBitMask = 0x40;
        if (!_disableEvent && (msg.Cmd & replyBitMask) != 0)
        {
            // Check if this TNS is NOT being waited for by the protocol.
            if (_protocol != null && !_protocol.IsTnsPending(msg.Tns))
            {
                DataReceived?.Invoke(this, EventArgs.Empty);
            }
        }
        else if (msg.Fnc.HasValue &&
                 msg.Cmd == PCCCConstants.Cmd.ProtectedWrite &&
                 msg.Fnc.Value == PCCCConstants.Fnc.WriteWordRange)
        {
            // Unsolicited write message - same as original.
            if (_currentTransport is DF1BaseTransport)
            {
                // Reply CMD = request CMD | 0x40
                SendUnsolicitedResponse(msg.Cmd | 0x40, msg.Tns);
            }
            UnsolicitedMessageRcvd?.Invoke(this, EventArgs.Empty);
        }
    }

    private int SendUnsolicitedResponse(int command, int rTNS)
    {
        if (_currentTransport == null) return -6;
        var reply = new PCCCMessage(0, 0, (byte)command, 0, (ushort)rTNS, null, Array.Empty<byte>());
        try
        {
            _currentTransport.SendFrame(reply.ToBytes());
            return 0;
        }
        catch
        {
            return -6;
        }
    }

    // ─── Raw PDU send/receive (for debugging and low-level testing) ────────

    /// <summary>
    /// Sends a raw PCCC PDU and waits for the matching response.
    /// TNS bytes in the input PDU are replaced automatically.
    /// </summary>
    /// <param name="pdu">
    /// Raw PCCC PDU: [dst][src][cmd][sts][tns_lo][tns_hi][fnc?][data...]
    /// Minimum 6 bytes.
    /// </param>
    /// <returns>
    ///   0  = success, ResponsePdu contains the inner PCCC frame
    ///  -1  = pdu null or too short
    ///  -2  = not connected
    /// -20  = timeout / no response
    /// </returns>
    public (int Status, byte[]? ResponsePdu, string Diagnostics)
        SendRawPduAndGetResponse(byte[] pdu)
    {
        if (pdu == null || pdu.Length < 6)
            return (-1, null, "PDU null or too short (minimum 6 bytes)");

        if (_currentTransport == null || !_currentTransport.IsOpen)
            return (-2, null, "Transport not open — call OpenComms() first");

        if (_protocol == null)
            return (-2, null, "Protocol not initialized — call OpenComms() first");

        byte cmd    = pdu[2];
        byte sts    = pdu[3];
        bool hasFnc = (cmd == 0x06 || cmd == 0x0F || cmd == 0x0A) && pdu.Length >= 7;
        byte? fnc   = hasFnc ? pdu[6] : (byte?)null;

        int startIdx = hasFnc ? 7 : 6;
        byte[] data;
        if (pdu.Length > startIdx)
        {
            int length = pdu.Length - startIdx;
            data = new byte[length];
            Array.Copy(pdu, startIdx, data, 0, length);
        }
        else
        {
            data = Array.Empty<byte>();
        }

        var req   = new PCCCMessage((byte)TargetNode, (byte)MyNode, cmd, sts, 0, fnc, data);
        var reply = _protocol.SendRequest(req, out int replySts);
        

        if (reply == null)
            return (replySts == 0 ? -20 : replySts, null,
                    PCCCErrors.DecodeStatus(replySts));

        return (0, reply.ToBytes(), $"cmd=0x{cmd:X2} fnc={fnc?.ToString("X2") ?? "n/a"}");
    }

    // ─── String helpers (static) ──────────────────────────────────────────
    public static string WordsToString(int[] words) => StringConverter.WordsToString(words);
    public static string WordsToString(int[] words, int index) => StringConverter.WordsToString(words, index);
    public static string WordsToString(int[] words, int index, int count) => StringConverter.WordsToString(words, index, count);
    public static int[]? StringToWords(string source) => StringConverter.StringToWords(source);

    // ─── Dispose ───────────────────────────────────────────────────────────
    public void Dispose()
    {
        CloseComms();
    }
}
