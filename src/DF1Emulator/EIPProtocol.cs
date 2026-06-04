// SPDX-License-Identifier: GPL-3.0-or-later
//
// DF1Comm - DF1 Protocol Library for .NET
// Copyright (c) 2026 Ketut Kumajaya
//
// Based on libplctag by Kyle Hayes (https://github.com/libplctag/libplctag)
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
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// EtherNet/IP (EIP/PCCC) protocol implementation targeting SLC 500 and MicroLogix PLCs.
///
/// Architecture overview
/// ─────────────────────
/// EIPProtocol implements ILinkProtocol and acts as the TCP transport layer.
/// Each accepted TCP connection runs in its own EIPClient instance which owns
/// the per-connection state (session handle, Forward Open / connected-messaging
/// state, pending Sender Context echo, pending Request ID echo).
///
/// The PCCC command processing path is:
///   TCP RX → EIPClient.ProcessAsync()
///          → HandleCommand()
///          → HandleUnconnectedSend() or HandleConnectedSend()
///          → ExtractAndDispatchPCCC()
///          → ILinkProtocol.PduReceived event  (raises DF1Emulator.OnPduReceived)
///          → DF1Emulator.DispatchCommand()     (reads/writes PlcMemory)
///          → ILinkProtocol.SendResponse()      (routes back to originating client)
///          → EIPClient.SendSerializedAsync()   (serialized per-client send queue)
///          → SendUnconnectedResponse() or SendConnectedResponse()
///          → TCP TX
///
/// RSLinx compatibility requirements addressed here
/// ─────────────────────────────────────────────────
///   1. Sender Context (bytes 12-19 of every EIP header) must be echoed verbatim
///      in every response. RSLinx uses it to match responses to outstanding requests.
///   2. List Identity and List Services responses use a two-field CPF layout
///      (item count + items only, no Interface Handle / Timeout prefix). All other
///      responses (Unconnected Send, Connected Send, Forward Open/Close) use the
///      full six-field CPF layout.
///   3. RegisterSession must validate Protocol Version; return status 0x00000001
///      for unsupported versions.
///   4. A UDP listener on port 44818 answers broadcast ListIdentity so the emulator
///      appears in RSLinx "Browse Network" without a manual IP entry.
///   5. Response ordering per client is guaranteed by a per-client SemaphoreSlim
///      inside EIPClient.SendSerializedAsync(), preventing interleaved responses
///      when two requests arrive in rapid succession.
///
/// References
/// ──────────
///   - libplctag: defs.h, session.c, eip_cip.c, eip_slc_pccc.c
///   - ODVA EtherNet/IP Specification Volume 1 (Common Industrial Protocol)
///   - ODVA EtherNet/IP Specification Volume 2 (Adaptation for EtherNet)
/// </summary>
public partial class EIPProtocol : ILinkProtocol, IDisposable
{
    // ── External dependencies ────────────────────────────────────────────────

    private readonly DF1Emulator _emulator;
    private readonly int _port;

    // ── TCP server ───────────────────────────────────────────────────────────

    private TcpListener? _listener;
    private Task?        _acceptLoopTask;

    // Thread-safe client registry: session handle → EIPClient.
    private readonly Dictionary<uint, EIPClient> _clients    = new();
    private readonly object                       _clientLock = new object();
    private const int MAX_CLIENTS = 32;

    // Session handle generator. Stored as int so Interlocked.Increment can be
    // used; cast to uint when assigned because EIP session handles are 32-bit
    // unsigned values ranging from 0x00000001 to 0xFFFFFFFF.
    private int _nextSessionHandleInt = 0;

    // ── UDP listener (RSLinx broadcast ListIdentity) ─────────────────────────

    private UdpClient? _udpListener;
    private Task?      _udpTask;
    // Cached local unicast IP address for ListIdentity responses
    private IPAddress? _cachedLocalAddress;

    // ── Lifecycle ────────────────────────────────────────────────────────────

    // _isDisposing: 0 = running, 1 = shutting down.
    // Written with Interlocked.CompareExchange; read via IsDisposing property
    // which uses Volatile.Read for a clean atomic read.
    private int _isDisposing = 0;

    /// <summary>
    /// True once Stop() or Dispose() has been called.
    /// Used by EIPClient to abort in-flight operations during shutdown.
    /// </summary>
    public bool IsDisposing => Volatile.Read(ref _isDisposing) != 0;

    private volatile bool _isRunning;
    private CancellationTokenSource? _cts;

    // Counts in-flight requests to allow graceful drain on Stop().
    private int _activeRequests = 0;

    // ── Health monitoring ────────────────────────────────────────────────────

    private Timer? _healthTimer;
    private long   _framesProcessed = 0;
    private long   _lastFrameCount  = 0;
    private bool   _isLoggingEnabled = true;

    // ── EIP Encapsulation command codes (CIP Vol 2, Appendix A) ─────────────

    // Commands valid both before and after session registration.
    private const ushort EIP_LIST_SERVICES      = 0x0004; // Discover available services
    private const ushort EIP_LIST_IDENTITY      = 0x0063; // Read device identity
    private const ushort EIP_LIST_INTERFACES    = 0x0064; // Read CIP interface objects (optional)
    private const ushort EIP_REGISTER_SESSION   = 0x0065; // Open an EIP session
    private const ushort EIP_UNREGISTER_SESSION = 0x0066; // Close an EIP session

    // Commands that require a registered session.
    private const ushort EIP_UNCONNECTED_SEND = 0x006F; // CIP Unconnected messaging
    private const ushort EIP_CONNECTED_SEND   = 0x0070; // CIP Connected (class 3) messaging

    // EIP encapsulation status codes (CIP Vol 2, §2-3.2).
    private const uint EIP_STATUS_OK                  = 0x00000000;
    private const uint EIP_STATUS_INVALID_CMD         = 0x00000001; // Unsupported command
    private const uint EIP_STATUS_UNSUPPORTED_VERSION = 0x00000069; // Protocol version mismatch

    // Supported EIP protocol version.
    private const ushort EIP_PROTOCOL_VERSION = 1;

    // ── CIP Common Services (CIP Vol 1, §3-5.2) ─────────────────────────────

    private const byte CIP_SERVICE_GET_ATTRIBUTES_ALL   = 0x01; // Read all instance attributes
    private const byte CIP_SERVICE_GET_ATTRIBUTE_SINGLE = 0x0E; // Read one attribute
    private const byte CIP_SERVICE_FORWARD_OPEN         = 0x54; // Open Class 3 connection
    private const byte CIP_SERVICE_FORWARD_OPEN_EX      = 0x5B; // Extended Forward Open (large frames)
    private const byte CIP_SERVICE_FORWARD_CLOSE        = 0x4E; // Close Class 3 connection
    private const byte CIP_SERVICE_EXECUTE_PCCC         = 0x4B; // Execute PCCC command (SLC/MLGX)
    private const byte CIP_SERVICE_UNCONNECTED_SEND     = 0x52; // CM Unconnected Send wrapper

    // ── Common Packet Format item type codes (CIP Vol 1, §3-5.5) ────────────

    private const ushort CPF_ITEM_NULL_ADDRESS      = 0x0000; // Null address — no additional addressing
    private const ushort CPF_ITEM_CONNECTED_ADDRESS = 0x00A1; // Connected address — carries connection ID
    private const ushort CPF_ITEM_CONNECTED_DATA    = 0x00B1; // Connected data payload
    private const ushort CPF_ITEM_UNCONNECTED_DATA  = 0x00B2; // Unconnected data payload

    // ── CIP General Status codes (CIP Vol 1, §3-5.3) ────────────────────────

    private const byte CIP_STATUS_OK       = 0x00; // Success
    private const byte CIP_STATUS_FRAGMENT = 0x06; // Fragmented reply (more data follows)

    // ── Forward Open / connection parameters ────────────────────────────────

    // Requested Packet Interval: 1 second expressed in microseconds.
    // Returned in Forward Open response as the actual O→T and T→O API.
    private const uint RPI_US = 1_000_000;

    // ── Identity Object (CIP Vol 1, §5-4) ───────────────────────────────────
    //
    // We emulate an SLC 5/05 (the closest EIP-capable member of the SLC 500
    // family to the SLC 5/03).  RSLinx will label the device accordingly.
    // The Identity Object attributes here are from RSLinx EDS files.

    private const ushort EIP_VENDOR_ID    = 0x0001; // Rockwell Automation / Allen-Bradley
    private const ushort EIP_DEVICE_TYPE  = 0x000E; // General-Purpose Discrete I/O Controller
    private const ushort EIP_PRODUCT_CODE = 0x000D; // SLC 5/05 (1747-L551 C)
    private const byte   EIP_REV_MAJOR    = 19;     // Firmware major revision
    private const byte   EIP_REV_MINOR    = 6;      // Firmware minor revision
    private const uint   EIP_SERIAL_NUM   = 0x600DCAFE; // Emulator serial number
    private const string EIP_PRODUCT_NAME = "1747-L551 C SLC 5/05";

    // Identity attribute bytes, built once at type initialisation.
    // Shared by List Identity, Get Attributes All, and Get Attribute Single replies.
    private static readonly byte[] s_identityData = BuildIdentityData();

    // ── Vendor identification embedded in Execute PCCC Request ID ────────────
    //
    // libplctag uses these values when constructing the Request ID section of
    // every CIP Execute PCCC request (service 0x4B).  We echo them back in
    // our response when the client does not supply its own Request ID bytes.
    private const ushort VENDOR_ID            = 0xF33D;     // "tres"
    private const uint   VENDOR_SERIAL_NUMBER = 0x21504345; // "!PCE" (ASCII)

    // ── ILinkProtocol ────────────────────────────────────────────────────────

    public string Name => "EIP";

    /// <summary>
    /// Raised when a complete PCCC PDU has been extracted from an incoming
    /// EIP frame.  The event argument carries both the raw PDU bytes and the
    /// originating <see cref="EIPClient"/> as the client context so that
    /// <see cref="SendResponse"/> can route the reply to the correct client.
    /// </summary>
    public event EventHandler<(byte[] pdu, object ClientContext)>? PduReceived;

    // ── Construction ─────────────────────────────────────────────────────────

    public EIPProtocol(DF1Emulator emulator, int port = EIP_DEFAULT_PORT)
    {
        _emulator = emulator ?? throw new ArgumentNullException(nameof(emulator));
        _port     = port;
    }

    public const int EIP_DEFAULT_PORT = 44818;

    // ── ILinkProtocol: Start / Stop ──────────────────────────────────────────

    public void Start()
    {
        if (_isRunning) return;

        _isRunning = true;
        _cts       = new CancellationTokenSource();

        // TCP listener — accepts RSLinx, pycomm3, libplctag sessions.
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();
        _acceptLoopTask = Task.Run(AcceptClientsAsync, _cts.Token);

        // UDP listener — answers broadcast ListIdentity so the emulator is
        // visible in RSLinx "Browse Network" without a manual IP entry.
        try
        {
            _udpListener = new UdpClient(_port);
            _udpTask     = Task.Run(HandleUdpBroadcastAsync, _cts.Token);
        }
        catch (Exception ex)
        {
            // UDP bind failure (e.g. port already in use) is non-fatal.
            // RSLinx manual-connect still works via TCP.
            Console.WriteLine($"[EIP]  UDP listener not started (RSLinx auto-browse disabled): {ex.Message}");
            _udpListener = null;
        }

        // Cache local IP address once at startup (avoid repeated enumeration)
        _cachedLocalAddress = GetLocalUnicastIPv4Address();
        if (_cachedLocalAddress == null)
            Console.WriteLine("[WARN] No valid IPv4 unicast address found for ListIdentity");

        Console.WriteLine($"[EIP]  EtherNet/IP emulator started on TCP/UDP port {_port}");
    }

    /// <summary>
    /// Stops the EIP protocol handler asynchronously.
    /// Drains in-flight requests, disposes all client connections,
    /// stops the TCP listener and UDP listener, and waits for background
    /// tasks to complete before returning.
    /// </summary>
    public async Task StopAsync()
    {
        // Only one caller proceeds; subsequent calls are no-ops.
        if (Interlocked.CompareExchange(ref _isDisposing, 1, 0) != 0) return;

        _isRunning = false;
        _cts?.Cancel();

        _healthTimer?.Dispose();
        _healthTimer = null;

        // Drain in-flight requests (max 3 s).
        const int maxWaitMs = 3000;
        const int stepMs    = 100;
        int elapsed = 0;
        int active;
        while ((active = Volatile.Read(ref _activeRequests)) > 0 && elapsed < maxWaitMs)
        {
            await Task.Delay(stepMs).ConfigureAwait(false);
            elapsed += stepMs;
        }
        if (active > 0)
            Console.WriteLine($"[WARN] EIP Stop: {active} request(s) still active after {maxWaitMs} ms — forcing shutdown");

        // Dispose all client connections.
        lock (_clientLock)
        {
            foreach (var c in _clients.Values)
                try { c.Dispose(); } catch { }
            _clients.Clear();
        }

        // Stop accepting new connections.
        _listener?.Stop();
        _listener = null;

        _udpListener?.Close();
        _udpListener = null;

        // Wait for background tasks to complete (with timeout).
        var tasksToWait = new List<Task>();
        if (_acceptLoopTask != null && !_acceptLoopTask.IsCompleted)
            tasksToWait.Add(_acceptLoopTask);
        if (_udpTask != null && !_udpTask.IsCompleted)
            tasksToWait.Add(_udpTask);

        if (tasksToWait.Count > 0)
        {
            try
            {
                await Task.WhenAll(tasksToWait).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Console.WriteLine("[WARN] Background tasks did not complete within timeout");
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
        }

        Console.WriteLine("[EIP]  EtherNet/IP emulator stopped");
    }

    /// <summary>
    /// Synchronous Stop() for ILinkProtocol compatibility.
    /// Blocks until all in-flight requests are drained or the 3-second
    /// timeout expires.
    /// </summary>
    public void Stop() => StopAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Releases all managed resources held by EIPProtocol.
    /// Calls StopAsync() if not already stopped, then disposes the health
    /// timer and any remaining network resources as a safety net.
    /// Safe to call multiple times.
    /// </summary>
    public void Dispose()
    {
        // StopAsync sets _isDisposing = 1 via CompareExchange, so a second
        // call to either Stop() or Dispose() is a harmless no-op.
        Stop();

        // Safety-net disposal for resources that Stop() may not have reached
        // (e.g. if Start() was never called).
        _healthTimer?.Dispose();
        _healthTimer = null;

        try { _udpListener?.Dispose(); } catch { }
        try { _listener?.Stop();       } catch { }

        GC.SuppressFinalize(this);
    }

    // ── ILinkProtocol: SendResponse ──────────────────────────────────────────

    /// <summary>
    /// Routes a PCCC response PDU back to the client that raised
    /// <see cref="PduReceived"/>.  <paramref name="clientContext"/> must be
    /// the <see cref="EIPRequestContext"/> instance that was passed as the
    /// event argument; any other value is silently ignored.
    ///
    /// Response ordering per client is guaranteed by
    /// <see cref="EIPClient.SendSerializedAsync"/>, which serializes all
    /// outgoing sends through a per-client SemaphoreSlim.  This prevents
    /// interleaved or reordered responses when two requests from the same
    /// client are processed concurrently on the thread pool.
    /// </summary>
    public void SendResponse(byte[] pdu, object clientContext)
    {
        if (clientContext is not EIPRequestContext context) return;
        if (!context.Client.IsConnected) return;

        Log($"SendResponse → session {context.Client.SessionHandle:X8}, PDU length={pdu.Length}");

        // Use SendSerializedAsync to guarantee FIFO ordering of responses
        // within a single client session.  The discard (_=) is intentional:
        // exceptions are caught inside SendSerializedAsync and logged there.
        _ = context.Client.SendSerializedAsync(pdu, context);
    }

    // ── Health monitoring ────────────────────────────────────────────────────

    public void SetLoggingEnabled(bool enabled)
    {
        _isLoggingEnabled = enabled;

        if (enabled)
        {
            _healthTimer?.Dispose();
            _healthTimer = null;
        }
        else
        {
            // Activate periodic health stats when verbose logging is off.
            _healthTimer ??= new Timer(_ => LogHealthStats(), null, 15_000, 15_000);
            Console.WriteLine("[PERF] EIP logging disabled — health monitor active");
        }
    }

    internal void IncrementFramesProcessed() =>
        Interlocked.Increment(ref _framesProcessed);

    private void LogHealthStats()
    {
        if (IsDisposing) return;
        long cur   = Interlocked.Read(ref _framesProcessed);
        long delta = cur - _lastFrameCount;
        _lastFrameCount = cur;

        int clientCount;
        lock (_clientLock) clientCount = _clients.Count;

        Console.WriteLine(
            $"[MONI] EIP Rate: {delta / 15,6}/s | Total: {cur,10:N0} | " +
            $"Clients: {clientCount,2} | " +
            $"Memory: {GC.GetTotalMemory(false) / 1024,6:N0} KB");

        if (delta == 0 && cur > 0)
            Console.WriteLine("[WARN] EIP: no frames in last 15 s — check client connection");
    }

    private void Log(string msg)
    {
        if (_isLoggingEnabled) Console.WriteLine($"[EIP]  {msg}");
    }

    private void LogHex(string tag, byte[] data, int len)
    {
        if (_isLoggingEnabled && len > 0)
            Console.WriteLine(
                $"[EIP]  {tag} {BitConverter.ToString(data, 0, len).Replace("-", " ")}");
    }

    // ── Request lifecycle guard ──────────────────────────────────────────────

    // Returned by BeginRequest(); Dispose() decrements the counter so Stop()
    // knows when all in-flight operations have completed.
    private sealed class RequestHandle : IDisposable
    {
        private readonly EIPProtocol _p;
        public RequestHandle(EIPProtocol p) => _p = p;
        public void Dispose()               => Interlocked.Decrement(ref _p._activeRequests);
    }

    // Dummy disposable used when we are already shutting down.
    private sealed class NoOpDisposable : IDisposable
    {
        public static readonly NoOpDisposable Instance = new();
        public void Dispose() { }
    }

    internal IDisposable BeginRequest()
    {
        if (IsDisposing) return NoOpDisposable.Instance;
        Interlocked.Increment(ref _activeRequests);
        return new RequestHandle(this);
    }

    // ── TCP accept loop ──────────────────────────────────────────────────────

    private async Task AcceptClientsAsync()
    {
        int errorCount = 0;
        const int maxErrors = 10;
        
        while (_isRunning && _listener != null)
        {
            try
            {
                var tcp = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                errorCount = 0; // Reset on success
                if (_isRunning)
                    _ = Task.Run(() => HandleClientAsync(tcp), _cts!.Token);
                else
                    tcp.Close();
            }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                errorCount++;
                if (_isRunning)
                    Console.WriteLine($"[EIP]  Accept error ({errorCount}/{maxErrors}): {ex.Message}");
                
                if (errorCount >= maxErrors)
                {
                    Console.WriteLine($"[EIP]  Too many accept errors, stopping listener");
                    break;
                }
                
                await Task.Delay(100).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleClientAsync(TcpClient tcp)
    {
        int clientCount;
        lock (_clientLock)
            clientCount = _clients.Count;
        
        if (clientCount >= MAX_CLIENTS)
        {
            Console.WriteLine($"[EIP]  Max clients reached ({MAX_CLIENTS}), rejecting connection");
            tcp.Close();
            return;
        }

        uint handle = (uint)Interlocked.Increment(ref _nextSessionHandleInt);
        var  client = new EIPClient(this, tcp, handle);

        lock (_clientLock)
            _clients[handle] = client;

        Log($"Client connected, session handle=0x{handle:X8}");

        try
        {
            await client.ProcessAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log($"Client 0x{handle:X8} unhandled exception: {ex.Message}");
        }
        finally
        {
            lock (_clientLock)
                _clients.Remove(handle);
            client.Dispose();
            Log($"Client disconnected, session handle=0x{handle:X8}");
        }
    }

    // ── UDP broadcast handler (RSLinx auto-browse) ───────────────────────────

    /// <summary>
    /// Listens for UDP broadcast <c>ListIdentity</c> (0x0063) packets on port
    /// 44818 and sends a unicast reply to the originating host.  This is what
    /// makes the emulator appear in the RSLinx "Browse Network" tree without
    /// requiring the operator to type the IP address manually.
    /// </summary>
    private async Task HandleUdpBroadcastAsync()
    {
        while (_isRunning && _udpListener != null)
        {
            try
            {
                var result = await _udpListener.ReceiveAsync().ConfigureAwait(false);
                var data   = result.Buffer;
                LogHex("RX:", data, data.Length);

                // Minimum EIP header is 24 bytes.
                if (data.Length < 24) continue;

                ushort cmd = (ushort)(data[0] | (data[1] << 8));
                if (cmd != EIP_LIST_IDENTITY) continue;

                // Use cached local IP address (avoid per-packet enumeration)
                if (_cachedLocalAddress == null) return;
                IPEndPoint localEndpoint = new IPEndPoint(_cachedLocalAddress, _port);

                // Echo the Sender Context from the request (bytes 12-19).
                ulong senderCtx = BitConverter.ToUInt64(data, 12);

                byte[] reply = BuildListIdentityResponse(senderCtx, sessionHandle: 0, localEndpoint);
                LogHex("TX:", reply, reply.Length);
                await _udpListener.SendAsync(reply, reply.Length, result.RemoteEndPoint)
                                  .ConfigureAwait(false);

                Log($"UDP ListIdentity reply sent to {result.RemoteEndPoint}");
            }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                if (_isRunning) Console.WriteLine($"[EIP]  UDP broadcast handler error: {ex.Message}");
            }
        }
    }

    // ── Response builder helpers (static, shared by TCP and UDP paths) ───────

    /// <summary>
    /// Writes a standard 24-byte EIP encapsulation header into
    /// <paramref name="w"/>.
    /// <para>
    /// The <c>Length</c> field (bytes 2-3) is written as zero here; the
    /// caller must fix it by calling <see cref="FixEipLength"/> once all
    /// payload bytes have been written.
    /// </para>
    /// </summary>
    private static void WriteEipHeader(BinaryWriter w, ushort command,
                                       uint sessionHandle, ulong senderContext = 0)
    {
        w.Write(command);
        w.Write((ushort)0);     // Length — placeholder, fixed by FixEipLength()
        w.Write(sessionHandle);
        w.Write(EIP_STATUS_OK);
        w.Write(senderContext); // Must echo the value received from the client
        w.Write((uint)0);       // Options — always zero
    }

    /// <summary>
    /// Writes a standard 24-byte EIP header whose Status field carries an
    /// error code.  Used when a request cannot be fulfilled.
    /// </summary>
    private static void WriteEipErrorHeader(BinaryWriter w, ushort command,
                                            uint sessionHandle, uint errorStatus,
                                            ulong senderContext = 0)
    {
        w.Write(command);
        w.Write((ushort)0);     // Length
        w.Write(sessionHandle);
        w.Write(errorStatus);   // Non-zero status indicates an error
        w.Write(senderContext);
        w.Write((uint)0);
    }

    /// <summary>
    /// Writes the six-field CPF (Common Packet Format) header used inside
    /// <c>SendRRData</c> / <c>SendUnitData</c> packets: Interface Handle,
    /// Timeout, and item count.
    /// <para>
    /// <b>Do not</b> use this for List commands (ListIdentity, ListServices,
    /// ListInterfaces); those use a two-field layout — see
    /// <see cref="WriteListCpfHeader"/>.
    /// </para>
    /// </summary>
    private static void WriteSendCpfHeader(BinaryWriter w, ushort itemCount)
    {
        w.Write((uint)0);       // Interface Handle — always 0 for CIP
        w.Write((ushort)0);     // Timeout — 0 means "no timeout"
        w.Write(itemCount);
    }

    /// <summary>
    /// Writes the two-field CPF layout used in List command responses
    /// (ListIdentity, ListServices, ListInterfaces).  These responses do NOT
    /// include Interface Handle or Timeout before the item count.
    /// </summary>
    private static void WriteListCpfHeader(BinaryWriter w, ushort itemCount)
    {
        w.Write(itemCount);
    }

    /// <summary>
    /// Writes a Null Address CPF item (type 0x0000, length 0).
    /// Required as the first CPF item in Unconnected Send responses to
    /// comply with EIP Vol 2, §2-6.
    /// </summary>
    private static void WriteNullAddressItem(BinaryWriter w)
    {
        w.Write(CPF_ITEM_NULL_ADDRESS);
        w.Write((ushort)0);
    }

    /// <summary>
    /// Seeks back to byte offset 2 and writes the actual payload length
    /// (total bytes written minus the 24-byte EIP header), then seeks
    /// back to the current end so the caller can continue writing or flush.
    /// </summary>
    private static void FixEipLength(MemoryStream ms, BinaryWriter w)
    {
        long end = ms.Position;
        ms.Seek(2, SeekOrigin.Begin);
        w.Write((ushort)(end - 24));
        ms.Seek(end, SeekOrigin.Begin);
    }

    /// <summary>
    /// Builds the Identity Object attribute bytes shared by
    /// <c>ListIdentity</c> and <c>GetAttributesAll</c> / <c>GetAttributeSingle</c>
    /// responses.  Constructed once at static initialisation to avoid
    /// repeated allocations.
    /// </summary>
    private static byte[] BuildIdentityData()
    {
        using var ms = new MemoryStream();
        using var w  = new BinaryWriter(ms);

        w.Write(EIP_VENDOR_ID);    // Attribute 1: Vendor ID          (UINT)
        w.Write(EIP_DEVICE_TYPE);  // Attribute 2: Device Type         (UINT)
        w.Write(EIP_PRODUCT_CODE); // Attribute 3: Product Code        (UINT)
        w.Write(EIP_REV_MAJOR);    // Attribute 4: Revision — Major    (USINT)
        w.Write(EIP_REV_MINOR);    // Attribute 4: Revision — Minor    (USINT)
        w.Write((ushort)0x0060);   // Attribute 5: Status              (WORD)  — Owned, no faults
        w.Write(EIP_SERIAL_NUM);   // Attribute 6: Serial Number       (UDINT)

        // Attribute 7: Product Name — SHORT_STRING (1-byte length prefix + chars).
        byte[] nameBytes = Encoding.ASCII.GetBytes(EIP_PRODUCT_NAME);
        w.Write((byte)nameBytes.Length);
        w.Write(nameBytes);
        if ((nameBytes.Length % 2) != 0) w.Write((byte)0); // Pad to even byte boundary

        w.Write((byte)0x03); // Attribute 8: State (USINT) — 0x03 = Operational
        w.Write((byte)0x00); // Pad byte

        return ms.ToArray();
    }

    // ── Static List Identity response builder (used by both TCP and UDP) ─────


    /// <summary>
    /// Builds a complete List Identity response packet.
    /// </summary>
    /// <param name="senderContext">Sender Context bytes from request — echoed verbatim.</param>
    /// <param name="sessionHandle">EIP session handle; use 0 for UDP replies.</param>
    /// <param name="localEndpoint">Local endpoint (IP and port) for Socket Address field.</param>
    private static byte[] BuildListIdentityResponse(ulong senderContext, uint sessionHandle, IPEndPoint localEndpoint)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        // ========================================================================
        // Part 1: Encapsulation Header (24 bytes)
        // ========================================================================
        WriteEipHeader(w, EIP_LIST_IDENTITY, sessionHandle, senderContext);

        // ========================================================================
        // Part 2: CPF Header
        // ========================================================================
        WriteListCpfHeader(w, 1);  // Item count = 1

        // ========================================================================
        // Part 3: Identity Item
        // ========================================================================
        w.Write((ushort)0x000C);   // Item Type = Identity Object
        long itemLenPos = ms.Position;
        w.Write((ushort)0);        // Item Length (placeholder)

        // ========================================================================
        // Part 3a: Encapsulation Protocol Version (MUST be 0x0001)
        // ========================================================================
        w.Write((ushort)1);        // Protocol version = 1

        // ========================================================================
        // Part 3b: Socket Address (16 bytes)
        // ========================================================================
        w.Write((ushort)0x0002);   // sin_family = AF_INET
        // Convert port to network byte order (big-endian) per EIP spec
        ushort portBE = (ushort)((localEndpoint.Port >> 8) | ((localEndpoint.Port & 0xFF) << 8));
        w.Write(portBE);          // sin_port = 44818
        byte[] ipBytes = localEndpoint.Address.GetAddressBytes();
        w.Write(ipBytes);          // sin_addr
        w.Write(new byte[8]);      // sin_zero padding

        // ========================================================================
        // Part 3c: Identity Object Attributes
        // ========================================================================
        w.Write(s_identityData);   // Vendor ID, Device Type, Product Code, etc.

        // ========================================================================
        // Fix lengths
        // ========================================================================
        long itemEnd = ms.Position;
        ms.Seek(itemLenPos, SeekOrigin.Begin);
        w.Write((ushort)(itemEnd - (itemLenPos + 2)));
        ms.Seek(itemEnd, SeekOrigin.Begin);

        FixEipLength(ms, w);
        return ms.ToArray();
    }

    /// <summary>
    /// Gets the first non-loopback IPv4 unicast address of the local machine.
    /// </summary>
    private IPAddress? GetLocalUnicastIPv4Address()
    {
        foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            // Skip interfaces that are not operational
            if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                continue;
            
            // Skip loopback interfaces
            if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                continue;
            
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(ua.Address))
                {
                    return ua.Address;
                }
            }
        }
        return null;
    }

    // ── Binary read helpers ──────────────────────────────────────────────────

    private static ushort ReadU16(byte[] b, ref int o)
    {
        ushort v = (ushort)(b[o] | (b[o + 1] << 8));
        o += 2;
        return v;
    }

    private static uint ReadU32(byte[] b, ref int o)
    {
        uint v = (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
        o += 4;
        return v;
    }
}
