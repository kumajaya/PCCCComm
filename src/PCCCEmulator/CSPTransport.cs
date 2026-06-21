// SPDX-License-Identifier: GPL-3.0-or-later
// 
// PCCCEmulator - PCCC Engine and Transports for .NET
// Copyright (c) 2026 Ketut Kumajaya
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
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// CSPv4 (Client Server Protocol) server transport for PCCCEmulator.
///
/// <b>Status: IMPLEMENTED</b> — validated against RSLinx OPC Server (RSWho detects
/// PLC-5/40E, PCCC read/write data matches DF1 and EIP transports byte-for-byte).
///
/// Listens on TCP port 2222 (default) and speaks the framing that RSLinx's
/// CSPv4/"AB Ethernet" driver uses to talk to a PLC-5E or SLC 5/05 native
/// Ethernet port — as opposed to CIP-encapsulated PCCC on TCP/44818, which
/// <c>EIPTransport</c> in this project emulates. Pointing that RSLinx driver
/// at this listener is the "deception": from RSLinx's point of view it is
/// talking to a real CSPv4-capable processor.
///
/// ============================================================================
/// FRAME FORMAT — confirmed against kevinherron/wireshark-cspv4-pccc
/// (cspv4.lua), a reverse-engineered Wireshark dissector citing the
/// Senthivel/Ahmed/Roussev DFRWS 2017 PCCC forensics paper, Lynn Linse's
/// iatips.com notes, Chipkin's CSP article, and Wireshark's own
/// packet-cip.c PCCC value_string tables. Rockwell never published an
/// official CSPv4 spec — treat this as the best available secondary
/// source, not a primary one.
///
/// <code>
/// [ CSPv4 header — 28 bytes ][ LSAP — 4B local / 15B routed ][ PCCC — variable ]
///
/// header  (all multi-byte fields BIG-ENDIAN):
///   mode(1)=0x01 Req/0x02 Resp  submode(1)=0x01 Connection/0x07 PCCC
///   data_length(2)  conn_id(4)  status(4)  context(16)
///
/// LSAP local form (4 bytes): dst(1) control(1) src(1) lsap(1)=0x00
///
/// PCCC (byte-identical to DF1's vocabulary):
///   CMD(1, reply sets bit 0x40)  STS(1)  TNS(2, LITTLE-ENDIAN)
///   [EXT_STS(1) if STS==0x0F]  [FNC(1) if (CMD&amp;~0x40) in {0x06,0x07,0x0F}]
///   DATA(...)
/// </code>
///
/// ============================================================================
/// VALIDATION STATUS — as of 2026-06-21, validated against RSLinx OPC Server
/// (PLC-5 family, node 1):
///
///   1. Connection-submode (register) handshake — bare 28-byte header,
///      data_length=0 both ways. Confirmed by successful registration and
///      subsequent PCCC data exchange with RSLinx.
///
///   2. LSAP "control" byte — RSLinx sends 0x05 (not 0x00). This class
///      echoes whatever the client sent, which matches real captures and
///      works without issue. Exact meaning is cosmetic; no further action
///      needed for direct Ethernet use.
///
///   3. Routed LSAP form (DH+/DH-485, 15 bytes) — NOT IMPLEMENTED.
///      This is out of scope for direct Ethernet to a single PLC-5/SLC
///      station, which always uses local form (4 bytes). DH+ gateway
///      support would require a separate extension if ever needed.
///
/// ============================================================================
/// USAGE:
///
/// Start the emulator in CSP mode:
///   dotnet run -- --mode csp --csp-port 2222
///
/// Then point RSLinx's "AB Ethernet" (CSPv4) driver to the emulator's IP
/// address and port 2222. RSWho will detect a PLC-5/40E (or SLC 5/05
/// depending on the configured emulation family).
///
/// For debugging, enable capture logging (default: csp_capture.log) — see
/// <see cref="CSPCapture"/> below.
/// ============================================================================
/// </summary>
public sealed class CSPTransport : ILinkTransport
{
    // ── CSPv4 header field values ────────────────────────────────────────────
    private const byte MODE_REQUEST  = 0x01;
    private const byte MODE_RESPONSE = 0x02;

    private const byte SUBMODE_CONNECTION = 0x01;
    private const byte SUBMODE_PCCC       = 0x07;

    private const uint CSP_STATUS_OK = 0x00000000;

    private const int CSPHeaderLen = 28; // mode(1)+submode(1)+len(2)+connId(4)+status(4)+context(16)
    private const int LsapLocalLen = 4;  // dst(1)+control(1)+src(1)+lsap(1)

    private readonly PCCCEmulator _emulator;
    private readonly int          _port;
    private readonly string?      _captureLogPath;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private volatile bool _disposing;

    // Active client connections, so Stop() can tear them all down.
    private readonly System.Collections.Generic.List<CSPClient> _clients = new();
    private readonly object _clientsLock = new object();

    private static uint s_nextConnId = 1;

    // ── Health monitoring (mirrors EIPTransport) ──────────────────────────────
    private Timer? _healthTimer;
    private long   _framesProcessed = 0;
    private long   _lastFrameCount  = 0;

    public string Name => "CSPv4";

    public event EventHandler<(byte[] pdu, object ClientContext)>? PduReceived;

    internal bool IsDisposing => _disposing;
    internal PCCCEmulator Emulator => _emulator;
    internal string? CaptureLogPath => _captureLogPath;

    /// <summary>
    /// Creates a new CSPv4 server transport.
    /// </summary>
    /// <param name="emulator">Owning PCCCEmulator (for counters and PduReceived routing).</param>
    /// <param name="port">CSPv4 TCP port (default 2222).</param>
    /// <param name="captureLogPath">
    /// Optional path to a plain-text hex-dump capture log. Every raw RX/TX byte
    /// sequence on every CSPv4 connection is appended here, independent of the
    /// normal <c>Logger</c> verbosity setting. Pass <c>null</c> to disable
    /// (default: "csp_capture.log" in the working directory).
    /// </param>
    public CSPTransport(PCCCEmulator emulator, int port = 2222, string? captureLogPath = "csp_capture.log")
    {
        _emulator       = emulator ?? throw new ArgumentNullException(nameof(emulator));
        _port           = port;
        _captureLogPath = captureLogPath;

        if (_captureLogPath != null)
        {
            try { CSPCapture.WriteHeader(_captureLogPath, _port); }
            catch (Exception ex)
            {
                Logger.Warn(this, $"CSP capture log could not be initialised at '{_captureLogPath}': {ex.Message}");
            }
        }
    }

    // ── ILinkTransport ─────────────────────────────────────────────────────────

    public void Start()
    {
        _disposing = false;
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();

        Logger.Always(this, $"CSPv4 server listening on port {_port}");

        // The health monitor is activated when verbose logging is disabled
        // (e.g. --quiet), so throughput/clients are still visible somewhere.
        SetHealthStatsEnabled(!Logger.Enabled);

        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        _disposing = true;
        SetHealthStatsEnabled(false);
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }

        lock (_clientsLock)
        {
            foreach (var c in _clients) c.Dispose();
            _clients.Clear();
        }

        _cts?.Dispose();
        _cts = null;
        _listener = null;
    }

    // ── Health monitoring (mirrors EIPTransport) ──────────────────────────────

    /// <summary>
    /// Enables or disables the periodic health-stats heartbeat for this
    /// transport instance. When enabled, a line is logged every 15 seconds
    /// with throughput, client count, and memory usage — useful when
    /// <c>Logger.Enabled</c> is false (e.g. --quiet mode) and there would
    /// otherwise be no ongoing visibility into the running server.
    /// </summary>
    public void SetHealthStatsEnabled(bool enabled)
    {
        if (enabled)
        {
            _healthTimer ??= new Timer(_ => LogHealthStats(), null, 15_000, 15_000);
            Logger.Always(this, "Logging disabled — health monitor active");
        }
        else
        {
            _healthTimer?.Dispose();
            _healthTimer = null;
        }
    }

    /// <summary>
    /// Transport-local frame counter used only for the health-monitor rate
    /// calculation. Separate from <see cref="PCCCEmulator"/>'s global
    /// frame counter, which is already incremented centrally by
    /// <c>OnPduReceived</c> whenever <see cref="PduReceived"/> fires —
    /// calling the emulator's counter here too would double-count.
    /// </summary>
    internal void IncrementFramesProcessed() =>
        Interlocked.Increment(ref _framesProcessed);

    private void LogHealthStats()
    {
        if (IsDisposing) return;
        long cur   = Interlocked.Read(ref _framesProcessed);
        long delta = cur - _lastFrameCount;
        _lastFrameCount = cur;

        int clientCount;
        lock (_clientsLock) clientCount = _clients.Count;

        Logger.Always(this,
            $"CSP Rate: {delta / 15,6}/s | Total: {cur,10:N0} | " +
            $"Clients: {clientCount,2} | " +
            $"Memory: {GC.GetTotalMemory(false) / 1024,6:N0} KB");
    }

    /// <summary>
    /// Routes a PCCCEngine response to the originating client. clientContext
    /// must be the CSPRequestContext captured when the request was received.
    /// </summary>
    public void SendResponse(byte[] pdu, object clientContext)
    {
        if (_disposing) return;
        if (clientContext is not CSPRequestContext ctx) return;

        _ = ctx.Client.SendSerializedAsync(pdu, ctx);
    }

    // ── Accept loop ───────────────────────────────────────────────────────────

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                Logger.Warn(this, $"AcceptLoopAsync error: {ex.Message}");
                continue;
            }

            uint connId = Interlocked.Increment(ref s_nextConnId);
            var client = new CSPClient(this, tcp, connId);

            lock (_clientsLock) _clients.Add(client);

            Logger.Info(this, $"CSPv4 client connected from {tcp.Client.RemoteEndPoint}, conn_id 0x{connId:X8}");

            _ = Task.Run(async () =>
            {
                try { await client.ProcessAsync().ConfigureAwait(false); }
                finally
                {
                    lock (_clientsLock) _clients.Remove(client);
                    client.Dispose();
                }
            }, ct);
        }
    }

    // ── Per-request context ──────────────────────────────────────────────────

    /// <summary>
    /// Per-request state: a reference to the client connection that
    /// originated the request, plus the LSAP dst/src bytes seen on the
    /// request so the reply can echo them back unchanged.
    /// </summary>
    internal sealed class CSPRequestContext
    {
        public CSPClient Client { get; }
        public byte Dst { get; }
        public byte Src { get; }
        public byte Control { get; }

        public CSPRequestContext(CSPClient client, byte dst, byte src, byte control)
        {
            Client = client;
            Dst = dst;
            Src = src;
            Control = control;
        }
    }

    // ── Per-connection handler ───────────────────────────────────────────────

    internal sealed class CSPClient : IDisposable
    {
        private readonly CSPTransport  _transport;
        private readonly TcpClient     _tcp;
        private readonly NetworkStream _stream;
        private bool _disposed;

        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);

        private readonly uint _connId;
        private bool _isRegistered;

        public bool IsConnected => !_disposed && _tcp.Connected;

        public CSPClient(CSPTransport transport, TcpClient tcp, uint connId)
        {
            _transport = transport;
            _tcp       = tcp;
            _stream    = tcp.GetStream();
            _connId    = connId;
        }

        public async Task ProcessAsync()
        {
            var header  = new byte[CSPHeaderLen];
            var payload = new byte[65536];

            while (!_transport.IsDisposing)
            {
                try
                {
                    if (await ReadExactAsync(header, 0, CSPHeaderLen).ConfigureAwait(false) < CSPHeaderLen)
                        break;

                    byte   mode    = header[0];
                    byte   submode = header[1];
                    ushort dataLen = ReadUInt16BE(header, 2);

                    if (dataLen > 0)
                    {
                        if (dataLen > payload.Length)
                        {
                            // Oversized payload; drain and ignore the frame.
                            var discard = new byte[dataLen];
                            if (await ReadExactAsync(discard, 0, dataLen).ConfigureAwait(false) < dataLen) break;
                            continue;
                        }
                        if (await ReadExactAsync(payload, 0, dataLen).ConfigureAwait(false) < dataLen) break;
                    }

                    if (_transport.CaptureLogPath != null)
                    {
                        var fullPacket = new byte[CSPHeaderLen + dataLen];
                        Array.Copy(header, 0, fullPacket, 0, CSPHeaderLen);
                        if (dataLen > 0) Array.Copy(payload, 0, fullPacket, CSPHeaderLen, dataLen);
                        CSPCapture.Append(_transport.CaptureLogPath, "RX", _connId, fullPacket, fullPacket.Length, dataLen, mode, submode);
                    }

                    await Dispatch(mode, submode, payload, dataLen).ConfigureAwait(false);
                }
                catch (IOException) { break; }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Logger.Warn(this, $"CSPv4 ProcessAsync error (conn_id 0x{_connId:X8}): {ex.Message}");
                    break;
                }
            }
        }

        private async Task<int> ReadExactAsync(byte[] buf, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = await _stream.ReadAsync(buf, offset + total, count - total).ConfigureAwait(false);
                if (n == 0) break;
                total += n;
            }
            return total;
        }

        private async Task Dispatch(byte mode, byte submode, byte[] payload, ushort dataLen)
        {
            if (mode != MODE_REQUEST)
            {
                Logger.Info(this, $"CSPv4: ignoring non-request frame, mode=0x{mode:X2}");
                return;
            }

            if (submode == SUBMODE_CONNECTION)
            {
                await HandleConnectionRegister().ConfigureAwait(false);
                return;
            }

            if (submode != SUBMODE_PCCC)
            {
                Logger.Info(this, $"CSPv4: unhandled submode 0x{submode:X2} — ignored");
                return;
            }

            if (!_isRegistered)
            {
                Logger.Info(this, "CSPv4: PCCC frame rejected — connection not registered");
                return;
            }

            if (dataLen < LsapLocalLen + 4)
            {
                Logger.Info(this, "CSPv4: PCCC frame too short for LSAP + minimal PCCC header");
                return;
            }

            byte dst     = payload[0];
            byte control = payload[1];
            byte src     = payload[2];
            var context = new CSPRequestContext(this, dst, src, control);

            Logger.Hex(this, "RX:", payload, dataLen);

            byte lsapFlag = payload[3];
            if (lsapFlag != 0x00)
            {
                Logger.Info(this, "CSPv4: routed-form LSAP (DH+/DH-485) is not supported by this emulator");
                return;
            }

            ExtractAndDispatchPCCC(payload, LsapLocalLen, dataLen, context);
        }

        /// <summary>
        /// VERIFY: Connection-submode register handshake payload shape is
        /// assumed to be a bare 28-byte header on both sides (no LSAP/PCCC
        /// body), with conn_id assigned here and echoed by the client on
        /// every later frame.
        /// </summary>
        private async Task HandleConnectionRegister()
        {
            var response = new byte[CSPHeaderLen];
            response[0] = MODE_RESPONSE;
            response[1] = SUBMODE_CONNECTION;
            WriteUInt16BE(response, 2, 0);       // data_length = 0
            WriteUInt32BE(response, 4, _connId); // assigned connection id
            WriteUInt32BE(response, 8, CSP_STATUS_OK);

            await SendRawResponse(response, response.Length, MODE_RESPONSE, SUBMODE_CONNECTION).ConfigureAwait(false);
            _isRegistered = true;

            Logger.Info(this, $"CSPv4 connection registered: conn_id 0x{_connId:X8}");
        }

        /// <summary>
        /// Parses a raw PCCC frame out of the SendRRData-equivalent (PCCC
        /// submode) payload, directly after the 4-byte local-form LSAP.
        /// Builds a PDU in the same [DST, SRC, CMD, STS, TNS_LO, TNS_HI,
        /// FNC?, DATA...] shape used by EIPClient, so PCCCEngine needs no
        /// changes.
        /// </summary>
        private void ExtractAndDispatchPCCC(byte[] payload, int pcccStart, int dataLen, CSPRequestContext context)
        {
            int offset  = pcccStart;
            int dataEnd = dataLen;

            // Minimum: CMD(1) STS(1) TNS(2) = 4 bytes.
            if (offset + 4 > dataEnd)
            {
                Logger.Info(this, "CSPv4 ExtractAndDispatchPCCC: truncated PCCC header");
                return;
            }

            byte   pcccCmd = payload[offset++];
            byte   pcccSts = payload[offset++];
            ushort pcccTns = (ushort)(payload[offset] | (payload[offset + 1] << 8)); // TNS is little-endian
            offset += 2;

            byte baseCmd = (byte)(pcccCmd & ~0x40);
            bool hasFnc  = baseCmd == 0x06 || baseCmd == 0x07 || baseCmd == 0x0F;

            // EXT_STS only ever appears on replies (STS==0x0F); requests
            // from a master never carry it, so it's not handled here.

            byte pcccFunc = 0;
            if (hasFnc)
            {
                if (offset >= dataEnd)
                {
                    Logger.Info(this, "CSPv4 ExtractAndDispatchPCCC: truncated FNC byte");
                    return;
                }
                pcccFunc = payload[offset++];
            }

            int remaining = Math.Max(0, dataEnd - offset);
            int pduLen    = 6 + (hasFnc ? 1 : 0) + remaining;
            var pdu       = new byte[pduLen];

            pdu[0] = context.Dst;
            pdu[1] = context.Src;
            pdu[2] = pcccCmd;
            pdu[3] = pcccSts;
            pdu[4] = (byte)(pcccTns & 0xFF);
            pdu[5] = (byte)((pcccTns >> 8) & 0xFF);

            int pduOff = 6;
            if (hasFnc) pdu[pduOff++] = pcccFunc;
            if (remaining > 0)
                Array.Copy(payload, offset, pdu, pduOff, remaining);

            Logger.Info(this, $"CSPv4 PCCC dispatch: CMD=0x{pcccCmd:X2} TNS=0x{pcccTns:X4} FNC=0x{pcccFunc:X2} data={remaining}B");

            // Transport-local counter (health monitor only) — NOT the
            // emulator's global counter, which OnPduReceived already
            // increments centrally once PduReceived fires below.
            _transport.IncrementFramesProcessed();
            _transport.Emulator.IncrementTotalPacketsReceived();

            _transport.PduReceived?.Invoke(this, (pdu, context));
        }

        // ── Response sender ──────────────────────────────────────────────────

        public async Task SendSerializedAsync(byte[] pdu, CSPRequestContext context)
        {
            if (_disposed || _transport.IsDisposing) return;

            await _sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await SendResponseAsync(pdu, context).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Warn(this, $"CSPv4 SendSerializedAsync failed for conn_id 0x{_connId:X8}: {ex.Message}");
                _transport.Emulator.IncrementUndeliveredPackets();
            }
            finally
            {
                _sendLock.Release();
            }
        }

        /// <summary>
        /// Builds and sends the CSPv4 PCCC-submode reply: 28-byte header +
        /// 4-byte local-form LSAP (echoing dst/src from the request) + raw
        /// PCCC reply bytes. PDU layout from PCCCEngine is [DST, SRC, CMD,
        /// STS, TNS_LO, TNS_HI, DATA...].
        /// </summary>
        private async Task SendResponseAsync(byte[] pdu, CSPRequestContext context)
        {
            if (_disposed || _transport.IsDisposing) return;

            const int pcccDataOffset = 2; // skip DST/SRC, now carried by LSAP
            int pcccDataLen = Math.Max(0, pdu.Length - pcccDataOffset);
            int totalAfterHeader = LsapLocalLen + pcccDataLen;

            var response = new byte[CSPHeaderLen + totalAfterHeader];
            response[0] = MODE_RESPONSE;
            response[1] = SUBMODE_PCCC;
            WriteUInt16BE(response, 2, (ushort)totalAfterHeader);
            WriteUInt32BE(response, 4, _connId);
            WriteUInt32BE(response, 8, CSP_STATUS_OK);

            int lsapOffset = CSPHeaderLen;
            // Confirmed against a real RSLinx CSPv4 session 2026-06-21: RSLinx
            // sent control=0x05 on its request, not 0x00 as first assumed.
            // Echo it back unchanged rather than hardcoding, since its exact
            // meaning is still unconfirmed (see class remarks).
            response[lsapOffset + 0] = context.Dst;
            response[lsapOffset + 1] = context.Control;
            response[lsapOffset + 2] = context.Src;
            response[lsapOffset + 3] = 0x00;

            if (pcccDataLen > 0)
                Array.Copy(pdu, pcccDataOffset, response, lsapOffset + LsapLocalLen, pcccDataLen);

            Logger.Info(this, $"CSPv4 SendResponseAsync: PDU length={pdu.Length}");
            await SendRawResponse(response, response.Length, MODE_RESPONSE, SUBMODE_PCCC).ConfigureAwait(false);
        }

        private async Task SendRawResponse(byte[] data, int length, byte mode, byte submode)
        {
            Logger.Hex(this, "TX:", data, length);

            if (_transport.CaptureLogPath != null)
            {
                ushort dataLen = length > CSPHeaderLen ? (ushort)(length - CSPHeaderLen) : (ushort)0;
                CSPCapture.Append(_transport.CaptureLogPath, "TX", _connId, data, length, dataLen, mode, submode);
            }

            await _stream.WriteAsync(data, 0, length).ConfigureAwait(false);
        }

        // ── Big-endian helpers ─────────────────────────────────────────────

        private static void WriteUInt16BE(byte[] buf, int offset, ushort value)
        {
            buf[offset]     = (byte)(value >> 8);
            buf[offset + 1] = (byte)(value & 0xFF);
        }

        private static void WriteUInt32BE(byte[] buf, int offset, uint value)
        {
            buf[offset]     = (byte)(value >> 24);
            buf[offset + 1] = (byte)(value >> 16);
            buf[offset + 2] = (byte)(value >> 8);
            buf[offset + 3] = (byte)(value & 0xFF);
        }

        private static ushort ReadUInt16BE(byte[] buf, int offset) =>
            (ushort)((buf[offset] << 8) | buf[offset + 1]);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _sendLock.Dispose();
            try { _stream.Close(); } catch { }
            try { _tcp.Close(); } catch { }
        }
    }

    // ── Capture logger ────────────────────────────────────────────────────────

    /// <summary>
    /// Append-only, human-readable hex-dump logger dedicated to CSPv4 session
    /// captures. Independent of <c>Logger</c>'s verbosity setting.
    ///
    /// Output format per frame:
    /// <code>
    /// ── 2026-06-22T03:14:07.1234Z RX conn=0x00000001 mode=Request submode=Connection len=0 total=28 ──
    /// 0000  01 01 00 00 00 00 00 00 00 00 00 00 00 00 00 00  ................
    /// 0010  00 00 00 00 00 00 00 00 00 00 00 00              ............
    /// </code>
    /// </summary>
    internal static class CSPCapture
    {
        private static readonly object s_lock = new object();

        public static void WriteHeader(string path, int port)
        {
            lock (s_lock)
            {
                File.AppendAllText(path,
                    $"{Environment.NewLine}===== CSPv4 capture started {DateTime.UtcNow:O} — listening on port {port} ====={Environment.NewLine}" +
                    $"NOTE: frame format confirmed against kevinherron/wireshark-cspv4-pccc; a few items (Connection-submode payload shape, control-byte meaning, routed LSAP) remain unverified — see CSPTransport.cs remarks.{Environment.NewLine}{Environment.NewLine}");
            }
        }

        /// <summary>
        /// Appends one captured frame (request or response) to the log.
        /// </summary>
        /// <param name="fullPacket">Complete raw frame: 28-byte header followed by payload.</param>
        /// <param name="totalLength">Total valid byte count in <paramref name="fullPacket"/>.</param>
        public static void Append(string path, string direction, uint connId,
            byte[] fullPacket, int totalLength, ushort payloadLength, byte mode, byte submode)
        {
            try
            {
                string modeName = mode switch
                {
                    0x01 => "Request",
                    0x02 => "Response",
                    _ => $"0x{mode:X2}"
                };
                string submodeName = submode switch
                {
                    0x01 => "Connection",
                    0x07 => "PCCC",
                    _ => $"0x{submode:X2}"
                };

                var sb = new StringBuilder();
                sb.Append("── ")
                  .Append(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture))
                  .Append(' ').Append(direction)
                  .Append(" conn=0x").Append(connId.ToString("X8"))
                  .Append(" mode=").Append(modeName)
                  .Append(" submode=").Append(submodeName)
                  .Append(" len=").Append(payloadLength)
                  .Append(" total=").Append(totalLength)
                  .Append(" ──").Append(Environment.NewLine);

                AppendHexDump(sb, fullPacket, totalLength);
                sb.Append(Environment.NewLine);

                lock (s_lock)
                {
                    File.AppendAllText(path, sb.ToString());
                }
            }
            catch
            {
                // Capture logging must never take down the emulator's actual
                // protocol handling — swallow and move on.
            }
        }

        /// <summary>
        /// Classic 16-bytes-per-line hex dump: offset, hex bytes, ASCII gutter.
        /// </summary>
        private static void AppendHexDump(StringBuilder sb, byte[] data, int length)
        {
            const int bytesPerLine = 16;

            for (int offset = 0; offset < length; offset += bytesPerLine)
            {
                int lineLen = Math.Min(bytesPerLine, length - offset);

                sb.Append(offset.ToString("X4", CultureInfo.InvariantCulture)).Append("  ");

                for (int j = 0; j < bytesPerLine; j++)
                {
                    if (j < lineLen)
                        sb.Append(data[offset + j].ToString("X2", CultureInfo.InvariantCulture)).Append(' ');
                    else
                        sb.Append("   ");

                    if (j == 7) sb.Append(' ');
                }

                sb.Append(' ');

                for (int j = 0; j < lineLen; j++)
                {
                    byte b = data[offset + j];
                    sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
                }

                sb.Append(Environment.NewLine);
            }
        }
    }
}
