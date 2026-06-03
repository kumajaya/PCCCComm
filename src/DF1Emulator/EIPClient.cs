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
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// EtherNet/IP (EIP) protocol implementation for SLC 5/03 emulator.
/// Handles TCP connections, session management, and CIP messaging.
///
/// This file contains the EIPClient nested class which manages individual
/// client connections and the EIPRequestContext class which encapsulates
/// per-request state to prevent race conditions.
///
/// KEY DESIGN PRINCIPLE: Request Context Encapsulation
/// ----------------------------------------------------
/// All per-request state (Sender Context, Request ID) is stored in an
/// EIPRequestContext object that flows with the request through the
/// processing pipeline. This prevents race conditions where a subsequent
/// request overwrites state before the previous response is sent.
///
/// Without this design, when logging is disabled (high performance mode),
/// the increased processing speed causes request B to overwrite
/// _pendingSenderContext before response A is sent, resulting in context
/// mismatch and client timeout.
/// </summary>
public sealed partial class EIPProtocol
{
    /// <summary>
    /// Encapsulates per-request state for EIP messaging.
    ///
    /// This object is created when a request is received and flows through
    /// the entire processing pipeline. It carries the Sender Context (which
    /// must be echoed in the response) and the Request ID (which must be
    /// echoed in the PCCC response).
    ///
    /// By storing per-request state in this context object rather than in
    /// instance fields of EIPClient, we eliminate race conditions that
    /// occur when multiple requests are processed concurrently or when
    /// a subsequent request arrives before the previous response is sent.
    /// </summary>
    private sealed class EIPRequestContext
    {
        /// <summary>
        /// The client connection that originated this request.
        /// </summary>
        public EIPClient Client { get; }

        /// <summary>
        /// The Sender Context (8 bytes) from the EIP encapsulation header.
        /// Must be echoed verbatim in the response. RSLinx uses these bytes
        /// to correlate responses to outstanding requests.
        /// </summary>
        public ulong SenderContext { get; }

        /// <summary>
        /// The EIP command code from the encapsulation header.
        /// Used to route the response to the correct handler.
        /// </summary>
        public ushort Command { get; }

        /// <summary>
        /// The Request ID bytes from the CIP Execute PCCC request.
        /// Contains requestIdSize (1 byte) + vendor_id (2) + vendor_serial (4).
        /// Must be echoed verbatim in the PCCC response.
        /// </summary>
        public byte[]? RequestId { get; set; }

        /// <summary>
        /// Creates a new request context for a received EIP packet.
        /// </summary>
        /// <param name="client">The client connection that received the packet</param>
        /// <param name="senderContext">Sender Context from EIP header (bytes 12-19)</param>
        /// <param name="command">EIP command code from header (bytes 0-1)</param>
        public EIPRequestContext(EIPClient client, ulong senderContext, ushort command)
        {
            Client        = client;
            SenderContext = senderContext;
            Command       = command;
        }
    }

    /// <summary>
    /// Represents one TCP client connection in the EIP protocol.
    ///
    /// This class handles:
    ///   - Per-connection session state (session handle, registration status)
    ///   - Connected messaging state (Forward Open/Close, connection IDs)
    ///   - Packet parsing and dispatching
    ///   - Response building and sending (using EIPRequestContext for state)
    ///
    /// All packet I/O for this connection runs on the async continuation chain
    /// started by ProcessAsync(); there is no secondary background thread.
    ///
    /// THREAD SAFETY:
    ///   The receive loop (ProcessAsync) and the send path (SendSerializedAsync)
    ///   may run on different thread-pool threads simultaneously.
    ///   _sendLock (SemaphoreSlim) serializes all outgoing sends to guarantee
    ///   FIFO response ordering and prevent interleaved writes on the same socket.
    ///   _disposed is checked in both paths before accessing the socket.
    /// </summary>
    private sealed class EIPClient : IDisposable
    {
        // ── Back-reference to protocol (access to shared state) ──────────────
        private readonly EIPProtocol _proto;

        // ── TCP plumbing ─────────────────────────────────────────────────────
        private readonly TcpClient     _tcp;
        private readonly NetworkStream _stream;
        private          bool          _disposed;

        // ── Per-client send serialization ────────────────────────────────────
        // SemaphoreSlim(1,1) used as an async mutex to guarantee that responses
        // are written to the socket in the order they are queued.  Without this,
        // two concurrent Task.Run sends can interleave their bytes on the wire.
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);

        // ── Session state ────────────────────────────────────────────────────

        // Session handle assigned by the server at RegisterSession time.
        // This is an immutable value set at construction.
        private readonly uint _sessionHandle;

        // True after a successful RegisterSession exchange; commands that
        // require a session (Unconnected/Connected Send) are rejected otherwise.
        private bool _isRegistered;

        // ── Connected messaging state (established by Forward Open) ──────────

        // Connection ID that the client must include in every Connected Send
        // packet it sends to us. We generate this value in HandleForwardOpen
        // and return it in the Forward Open response as orig_to_targ_conn_id.
        private uint _targConnectionId;

        // Connection ID we echo in the CPF Connected Address item of every
        // Connected Send response. Taken from the toConnId field of the client's
        // Forward Open request (its targ_to_orig_conn_id proposal).
        private uint _origConnectionId;

        // Shared counter for assigning unique connection IDs. Static so that
        // IDs do not repeat even across different client sessions.
        private static int s_nextConnectionId = 0;

        // Connection serial number and sequence counter for connected messaging.
        // _connSequenceNumber is stored as int to allow Interlocked.Increment;
        // cast to ushort on write so it wraps at 0xFFFF as per EIP spec.
        private ushort _connSerialNumber;
        private int    _connSequenceNumber;

        // True after a successful Forward Open; controls which response path
        // is used in SendResponseAsync().
        private bool _isConnected;

        // NOTE: Per-request state (Sender Context, Request ID) is NOT stored here.
        // Instead, it is encapsulated in EIPRequestContext and passed through
        // the processing pipeline. This eliminates race conditions that occur
        // when multiple requests are processed concurrently.

        // ── Properties ───────────────────────────────────────────────────────

        public uint SessionHandle => _sessionHandle;

        /// <summary>
        /// True while the underlying TCP socket is connected and this object
        /// has not been disposed. Used by <see cref="EIPProtocol.SendResponse"/>
        /// to guard against sending to already-disconnected clients.
        /// </summary>
        public bool IsConnected => !_disposed && _tcp.Connected;

        // ── Logging helpers ──────────────────────────────────────────────────

        private bool IsLogging => _proto._isLoggingEnabled;

        /// <summary>
        /// Sends a raw EIP response packet to the client. The data buffer must
        /// already contain a properly formatted EIP encapsulation header (24 bytes).
        /// Callers must hold _sendLock before calling this method.
        /// </summary>
        private async Task SendRawResponse(byte[] data, int length)
        {
            _proto.LogHex("TX:", data, length);
            await _stream.WriteAsync(data, 0, length).ConfigureAwait(false);
        }

        // ── Construction ─────────────────────────────────────────────────────

        /// <summary>
        /// Creates a new EIP client connection handler.
        /// </summary>
        /// <param name="proto">Parent EIPProtocol instance</param>
        /// <param name="tcp">Accepted TCP client connection</param>
        /// <param name="sessionHandle">Unique session handle for this connection</param>
        public EIPClient(EIPProtocol proto, TcpClient tcp, uint sessionHandle)
        {
            _proto         = proto;
            _tcp           = tcp;
            _stream        = tcp.GetStream();
            _sessionHandle = sessionHandle;
            // Use the lower 16 bits of the session handle as a deterministic
            // starting value for the connection serial number.
            _connSerialNumber = (ushort)(sessionHandle & 0xFFFF);
        }

        // ── Main receive loop ────────────────────────────────────────────────

        /// <summary>
        /// Reads and dispatches EIP packets until the TCP connection closes
        /// or the protocol is stopped.
        ///
        /// IMPORTANT: For each received packet, an EIPRequestContext is created
        /// to hold per-request state. This context flows through all processing
        /// and is used to build the response, preventing race conditions.
        /// </summary>
        public async Task ProcessAsync()
        {
            // Receive buffer: large enough for the maximum EIP packet size.
            // MAX_PACKET_SIZE_EX from libplctag session.h = 44 + 4002 bytes.
            var buf = new byte[65536];

            while (!_proto.IsDisposing)
            {
                try
                {
                    // Every EIP packet begins with a fixed 24-byte encapsulation header.
                    if (await ReadExactAsync(buf, 0, 24).ConfigureAwait(false) < 24)
                        break;

                    ushort command = (ushort)(buf[0] | (buf[1] << 8));
                    ushort length  = (ushort)(buf[2] | (buf[3] << 8));
                    // Session handle at offset 4 (uint, LE).
                    // Status at offset 8 — checked per-command where needed.
                    // Sender Context at offset 12 (uint64, LE) — will be echoed in reply.
                    ulong senderContext = BitConverter.ToUInt64(buf, 12);

                    // Create request context BEFORE reading payload. This context
                    // will carry all per-request state through the pipeline.
                    var context = new EIPRequestContext(this, senderContext, command);

                    if (length > 0)
                    {
                        if (await ReadExactAsync(buf, 24, length).ConfigureAwait(false) < length)
                            break;
                        _proto.LogHex("RX:", buf, 24 + length);
                    }

                    // Dispatch command with the request context.
                    await DispatchCommand(command, buf, length, context).ConfigureAwait(false);
                }
                catch (IOException) { break; }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _proto.Log($"ProcessAsync error (session 0x{_sessionHandle:X8}): {ex.Message}");
                    break;
                }
            }

            // Server never initiates Forward Close — simply reset connection state.
            _isConnected = false;
        }

        // ── Low-level I/O helpers ────────────────────────────────────────────

        /// <summary>
        /// Reads exactly <paramref name="count"/> bytes into
        /// <paramref name="buf"/> starting at <paramref name="offset"/>.
        /// Returns the number of bytes read; a value less than
        /// <paramref name="count"/> indicates EOF or connection closure.
        /// </summary>
        private async Task<int> ReadExactAsync(byte[] buf, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = await _stream.ReadAsync(buf, offset + total, count - total)
                                     .ConfigureAwait(false);
                if (n == 0) break;
                total += n;
            }
            return total;
        }


        // ── Command dispatch ─────────────────────────────────────────────────

        /// <summary>
        /// Dispatches an EIP command to the appropriate handler.
        /// <para>
        /// Per EIP spec (Vol 2, §2-3): List commands (ListIdentity, ListServices,
        /// ListInterfaces) and RegisterSession are valid without a registered
        /// session. All other commands require a prior RegisterSession.
        /// </para>
        /// </summary>
        /// <param name="command">EIP command code from header</param>
        /// <param name="buf">Raw packet buffer (includes header and payload)</param>
        /// <param name="length">Payload length (bytes after 24-byte header)</param>
        /// <param name="context">Request context containing per-request state</param>
        private async Task DispatchCommand(ushort command, byte[] buf, ushort length, EIPRequestContext context)
        {
            using var guard = _proto.BeginRequest();

            switch (command)
            {
                case EIP_REGISTER_SESSION:
                    await HandleRegisterSession(buf, context).ConfigureAwait(false);
                    break;

                case EIP_UNREGISTER_SESSION:
                    await HandleUnregisterSession(context).ConfigureAwait(false);
                    break;

                case EIP_LIST_SERVICES:
                    await HandleListServices(context).ConfigureAwait(false);
                    break;

                case EIP_LIST_IDENTITY:
                    await HandleListIdentity(context).ConfigureAwait(false);
                    break;

                case EIP_LIST_INTERFACES:
                    await HandleListInterfaces(context).ConfigureAwait(false);
                    break;

                case EIP_UNCONNECTED_SEND:
                    if (!_isRegistered) { _proto.Log("Unconnected Send rejected — no session"); return; }
                    await HandleUnconnectedSend(buf, length, context).ConfigureAwait(false);
                    break;

                case EIP_CONNECTED_SEND:
                    if (!_isRegistered) { _proto.Log("Connected Send rejected — no session"); return; }
                    await HandleConnectedSend(buf, length, context).ConfigureAwait(false);
                    break;

                default:
                    _proto.Log($"Unknown command 0x{command:X4} — sending error reply");
                    await SendErrorReply(command, EIP_STATUS_INVALID_CMD, context).ConfigureAwait(false);
                    break;
            }
        }

        // ── Session management ───────────────────────────────────────────────

        /// <summary>
        /// Handles RegisterSession (0x0065).
        /// Validates the requested protocol version and assigns a session handle.
        /// Responds with error status 0x0069 if the version is not supported.
        /// </summary>
        /// <param name="buf">Raw packet buffer with payload</param>
        /// <param name="context">Request context (contains Sender Context for echo)</param>
        private async Task HandleRegisterSession(byte[] buf, EIPRequestContext context)
        {
            // The RegisterSession data payload is 4 bytes:
            //   bytes 24-25: Protocol Version (UINT, LE)
            //   bytes 26-27: Options          (UINT, LE) — must be 0
            ushort requestedVersion = (ushort)(buf[24] | (buf[25] << 8));

            if (requestedVersion != EIP_PROTOCOL_VERSION)
            {
                _proto.Log($"RegisterSession: unsupported protocol version {requestedVersion} (expected {EIP_PROTOCOL_VERSION})");
                await SendErrorReply(EIP_REGISTER_SESSION, EIP_STATUS_UNSUPPORTED_VERSION, context)
                    .ConfigureAwait(false);
                return;
            }

            var response = new byte[28];

            // EIP header (24 bytes)
            response[0] = (byte)(EIP_REGISTER_SESSION & 0xFF);
            response[1] = (byte)((EIP_REGISTER_SESSION >> 8) & 0xFF);
            response[2] = 0x04; response[3] = 0x00;    // Data length = 4
            response[4] = (byte)(_sessionHandle & 0xFF);
            response[5] = (byte)((_sessionHandle >> 8)  & 0xFF);
            response[6] = (byte)((_sessionHandle >> 16) & 0xFF);
            response[7] = (byte)((_sessionHandle >> 24) & 0xFF);
            // Bytes 8-11:  Status = 0x00000000 (OK)
            // Bytes 12-19: Sender Context — echo from request context
            BitConverter.TryWriteBytes(response.AsSpan(12), context.SenderContext);
            // Bytes 20-23: Options = 0

            // Payload (4 bytes)
            response[24] = 0x01; response[25] = 0x00;  // Protocol Version = 1
            response[26] = 0x00; response[27] = 0x00;  // Options = 0

            await SendRawResponse(response, response.Length).ConfigureAwait(false);
            _isRegistered = true;

            _proto.Log($"RegisterSession: session 0x{_sessionHandle:X8} registered");
        }

        /// <summary>
        /// Handles UnregisterSession (0x0066).
        /// Releases the session and clears registration state.
        /// </summary>
        /// <param name="context">Request context (contains Sender Context for echo)</param>
        private async Task HandleUnregisterSession(EIPRequestContext context)
        {
            var response = new byte[24];

            response[0] = (byte)(EIP_UNREGISTER_SESSION & 0xFF);
            response[1] = (byte)((EIP_UNREGISTER_SESSION >> 8) & 0xFF);
            // Length = 0 (no payload for Unregister)
            response[4] = (byte)(_sessionHandle & 0xFF);
            response[5] = (byte)((_sessionHandle >> 8)  & 0xFF);
            response[6] = (byte)((_sessionHandle >> 16) & 0xFF);
            response[7] = (byte)((_sessionHandle >> 24) & 0xFF);
            BitConverter.TryWriteBytes(response.AsSpan(12), context.SenderContext);

            await SendRawResponse(response, response.Length).ConfigureAwait(false);
            _isRegistered = false;

            _proto.Log($"UnregisterSession: session 0x{_sessionHandle:X8} released");
        }

        // ── List commands ────────────────────────────────────────────────────

        /// <summary>
        /// Responds to ListServices (0x0004).
        /// Returns one Target Item describing the "Communications" service.
        /// Format per EIP Vol 2, §2-4.2:
        ///   Item type   = 0x0100
        ///   Item length = 20 bytes (Version 2 + Capability 2 + Name 16)
        ///   Version     = 1
        ///   Capability  = 0x0020 (supports EIP encapsulation)
        ///   Name        = "Communications" (16 bytes, null-padded)
        /// </summary>
        /// <param name="context">Request context (contains Sender Context for echo)</param>
        private async Task HandleListServices(EIPRequestContext context)
        {
            using var ms = new MemoryStream();
            using var w  = new BinaryWriter(ms);

            WriteEipHeader(w, EIP_LIST_SERVICES, _sessionHandle, context.SenderContext);
            WriteListCpfHeader(w, 1); // Item count = 1

            w.Write((ushort)0x0100); // Target Item type: Communications
            w.Write((ushort)20);     // Item length: 2 + 2 + 16 = 20 bytes
            w.Write((ushort)1);      // Version = 1
            w.Write((ushort)0x0020); // Capability: supports EIP encapsulation

            var name = new byte[16];
            Encoding.ASCII.GetBytes("Communications").CopyTo(name, 0);
            w.Write(name);           // 16-byte null-padded name field

            FixEipLength(ms, w);
            await FlushAsync(ms).ConfigureAwait(false);
            _proto.Log("ListServices response sent");
        }

        /// <summary>
        /// Responds to ListIdentity (0x0063) over TCP.
        /// Uses <see cref="BuildListIdentityResponse"/> which is also called
        /// by the UDP broadcast handler.
        /// </summary>
        /// <param name="context">Request context (contains Sender Context for echo)</param>
        private async Task HandleListIdentity(EIPRequestContext context)
        {
            // Use cached local IP address (avoid per-packet enumeration)
            if (_proto._cachedLocalAddress == null)
            {
                _proto.Log("[WARN] HandleListIdentity: no valid IPv4 unicast address found");
                return;
            }
            
            IPEndPoint localEndpoint = new IPEndPoint(_proto._cachedLocalAddress, _proto._port);
            byte[] reply = BuildListIdentityResponse(context.SenderContext, _sessionHandle, localEndpoint);
            await SendRawResponse(reply, reply.Length).ConfigureAwait(false);
            _proto.Log($"ListIdentity response sent ({EIP_PRODUCT_NAME})");
        }

        /// <summary>
        /// Responds to ListInterfaces (0x0068) with an empty list.
        /// The EIP specification defines this command but does not require
        /// devices to support any interface objects.
        /// </summary>
        /// <param name="context">Request context (contains Sender Context for echo)</param>
        private async Task HandleListInterfaces(EIPRequestContext context)
        {
            var response = new byte[26]; // 24-byte header + 2-byte item count

            response[0] = (byte)(EIP_LIST_INTERFACES & 0xFF);
            response[1] = (byte)((EIP_LIST_INTERFACES >> 8) & 0xFF);
            response[2] = 0x02; response[3] = 0x00;    // Length = 2
            response[4] = (byte)(_sessionHandle & 0xFF);
            response[5] = (byte)((_sessionHandle >> 8)  & 0xFF);
            response[6] = (byte)((_sessionHandle >> 16) & 0xFF);
            response[7] = (byte)((_sessionHandle >> 24) & 0xFF);
            BitConverter.TryWriteBytes(response.AsSpan(12), context.SenderContext);
            // Bytes 24-25: item count = 0

            await SendRawResponse(response, response.Length).ConfigureAwait(false);
            _proto.Log("ListInterfaces response sent (empty)");
        }

        // ── Unconnected Send (0x006F) ────────────────────────────────────────

        /// <summary>
        /// Processes an Unconnected Send packet. The CPF payload may contain:
        ///   - A Null Address Item (type 0x0000) followed by a data item, or
        ///   - Just the data item (older clients).
        /// The data item payload is dispatched by service code:
        ///   0x01 / 0x0E → Get Attributes (Identity Object)
        ///   0x54 / 0x5B → Forward Open (standard / extended)
        ///   0x4E        → Forward Close
        ///   0x52        → CM Unconnected Send wrapper (PCCC inside)
        ///   0x4B        → Execute PCCC (direct, without wrapper)
        /// </summary>
        /// <param name="buf">Raw packet buffer</param>
        /// <param name="length">Payload length</param>
        /// <param name="context">Request context (carries per-request state)</param>
        private async Task HandleUnconnectedSend(byte[] buf, ushort length, EIPRequestContext context)
        {
            // Body starts immediately after the 24-byte EIP header.
            int offset = 24;

            // Interface Handle (4 bytes, always 0 for CIP) + Timeout (2 bytes).
            offset += 6;

            if (offset + 2 > buf.Length) return;
            ushort itemCount = (ushort)(buf[offset] | (buf[offset + 1] << 8));
            offset += 2;

            for (int i = 0; i < itemCount; i++)
            {
                if (offset + 4 > buf.Length) return;
                ushort itemType   = (ushort)(buf[offset]     | (buf[offset + 1] << 8));
                ushort itemLength = (ushort)(buf[offset + 2] | (buf[offset + 3] << 8));
                offset += 4;

                int itemStart = offset;

                if (itemType == CPF_ITEM_NULL_ADDRESS)
                {
                    // Null Address Item carries no data; skip it and continue.
                    offset = itemStart + itemLength;
                    continue;
                }

                if (itemType == CPF_ITEM_UNCONNECTED_DATA && itemLength > 0)
                {
                    byte svc = buf[offset];

                    if (svc == CIP_SERVICE_GET_ATTRIBUTES_ALL ||
                        svc == CIP_SERVICE_GET_ATTRIBUTE_SINGLE)
                    {
                        await HandleGetAttributes(buf, offset, svc, context).ConfigureAwait(false);
                    }
                    else if (svc == CIP_SERVICE_FORWARD_OPEN ||
                             svc == CIP_SERVICE_FORWARD_OPEN_EX)
                    {
                        await HandleForwardOpen(buf, offset, itemLength,
                            isExtended: svc == CIP_SERVICE_FORWARD_OPEN_EX, context)
                            .ConfigureAwait(false);
                    }
                    else if (svc == CIP_SERVICE_FORWARD_CLOSE)
                    {
                        await HandleForwardClose(buf, offset, itemLength, context).ConfigureAwait(false);
                    }
                    else if (svc == CIP_SERVICE_UNCONNECTED_SEND)
                    {
                        // CM Unconnected Send wrapper (service 0x52).
                        // Structure (per ODVA CIP Vol 1, §3-5.8):
                        //   serviceCode(1) + pathSize(1) + path(pathSize*2)
                        //   + secsPerTick(1) + timeoutTicks(1)
                        //   + ucCmdLength(2) + [pad if ucCmdLength is odd]
                        //   + embedded PCCC request
                        int inner = offset + 1;                        // skip service code
                        if (inner >= buf.Length) return;
                        byte pathSize = buf[inner++];
                        inner += pathSize * 2;                         // skip CM object path
                        inner += 2;                                    // secsPerTick + timeoutTicks
                        if (inner + 2 > buf.Length) return;
                        ushort ucLen = (ushort)(buf[inner] | (buf[inner + 1] << 8));
                        inner += 2;
                        // Pad byte required when embedded command length is odd.
                        if ((ucLen & 1) != 0 && inner < buf.Length) inner++;

                        ExtractAndDispatchPCCC(buf, inner, ucLen, context);
                    }
                    else
                    {
                        // Direct Execute PCCC (0x4B) — no CM wrapper.
                        ExtractAndDispatchPCCC(buf, itemStart, itemLength, context);
                    }
                    break; // Only one data item is expected per Unconnected Send.
                }

                offset = itemStart + itemLength; // skip unrecognised item
            }
        }

        // ── Connected Send (0x0070) ──────────────────────────────────────────

        /// <summary>
        /// Processes a Connected Send packet. The CPF payload must contain:
        ///   1. Connected Address Item (type 0x00A1, length 4) carrying the
        ///      connection ID that was issued in the Forward Open response.
        ///   2. Connected Data Item   (type 0x00B1) carrying a sequence
        ///      counter (2 bytes) followed by the CIP request payload.
        /// </summary>
        /// <param name="buf">Raw packet buffer</param>
        /// <param name="length">Payload length</param>
        /// <param name="context">Request context (carries per-request state)</param>
        private async Task HandleConnectedSend(byte[] buf, ushort length, EIPRequestContext context)
        {
            int offset = 24 + 6; // EIP header + Interface Handle(4) + Timeout(2)

            if (offset + 2 > buf.Length) return;
            ushort itemCount = (ushort)(buf[offset] | (buf[offset + 1] << 8));
            offset += 2;

            for (int i = 0; i < itemCount && offset + 4 <= buf.Length; i++)
            {
                ushort itemType   = (ushort)(buf[offset]     | (buf[offset + 1] << 8));
                ushort itemLength = (ushort)(buf[offset + 2] | (buf[offset + 3] << 8));
                offset += 4;

                int itemStart = offset;

                if (itemType == CPF_ITEM_CONNECTED_ADDRESS && itemLength >= 4)
                {
                    uint connId = (uint)(buf[offset]     | (buf[offset + 1] << 8) |
                                        (buf[offset + 2] << 16) | (buf[offset + 3] << 24));
                    if (connId != _targConnectionId)
                    {
                        _proto.Log($"Connected Send: bad connection ID 0x{connId:X8} " +
                            $"(expected 0x{_targConnectionId:X8}) — packet dropped");
                        return;
                    }
                    offset = itemStart + itemLength;
                }
                else if (itemType == CPF_ITEM_CONNECTED_DATA && itemLength >= 2)
                {
                    // First two bytes are the connection sequence number.
                    _connSequenceNumber = (ushort)(buf[offset] | (buf[offset + 1] << 8));
                    ExtractAndDispatchPCCC(buf, offset + 2, (ushort)(itemLength - 2), context);
                    break;
                }
                else
                {
                    offset = itemStart + itemLength; // skip unknown item
                }
            }

            await Task.CompletedTask; // keep signature async for future async operations
        }

        // ── Forward Open / Close ─────────────────────────────────────────────

        /// <summary>
        /// Handles both standard Forward Open (0x54) and Extended Forward Open
        /// (0x5B). Parses the request to obtain the connection IDs proposed by
        /// the client, generates a unique server-side orig_to_targ_conn_id, and
        /// sends the appropriate Forward Open response.
        ///
        /// Connection ID semantics (from libplctag session.h / defs.h):
        ///   otConnId  — O→T connection ID proposed by the client (used by us to
        ///               validate incoming Connected Send packets when the client
        ///               echoes it in CPF Connected Address items).
        ///   toConnId  — T→O connection ID proposed by the client (echoed in our
        ///               response and in Connected Send reply CPF address items).
        ///   newId     — Fresh connection ID we assign for this connection.
        ///               Returned as orig_to_targ_conn_id in our response so the
        ///               client knows what value to put in CPF Connected Address.
        /// </summary>
        /// <param name="buf">Raw packet buffer</param>
        /// <param name="offset">Starting offset of the CIP request</param>
        /// <param name="length">Total length of the CIP request</param>
        /// <param name="isExtended">True for Extended Forward Open (0x5B)</param>
        /// <param name="context">Request context (contains Sender Context for echo)</param>
        private async Task HandleForwardOpen(byte[] buf, int offset,
                                             ushort length, bool isExtended, EIPRequestContext context)
        {
            offset++;  // skip service code byte
            if (offset >= buf.Length) return;

            byte pathSize = buf[offset++];
            offset += pathSize * 2;  // skip connection path

            // Both standard and extended Forward Open share these fields.
            if (offset + 14 > buf.Length) return;
            _ = buf[offset++]; // secsPerTick  — not stored, we use RPI_US
            _ = buf[offset++]; // timeoutTicks — not stored
            uint   otConnId   = ReadU32(buf, ref offset); // O→T proposed ID
            uint   toConnId   = ReadU32(buf, ref offset); // T→O proposed ID
            ushort connSerial = ReadU16(buf, ref offset);
            ushort vendorId   = ReadU16(buf, ref offset);
            uint   serialNum  = ReadU32(buf, ref offset);
            offset++;        // timeoutMultiplier
            offset += 3;     // reserved bytes

            // Skip RPI and connection parameter fields (different widths per variant).
            if (isExtended)
                offset += 4 + 4 + 4 + 4 + 1; // otRpi(4) + otParamsEx(4) + toRpi(4) + toParamsEx(4) + transport(1)
            else
                offset += 4 + 2 + 4 + 2 + 1; // otRpi(4) + otParams(2)   + toRpi(4) + toParams(2)   + transport(1)

            // Generate a unique connection ID for this session. The high bit
            // distinguishes server-generated IDs from client-generated ones.
            uint newId = ((uint)Interlocked.Increment(ref s_nextConnectionId) << 1) | 0x80000000;

            _targConnectionId   = newId;     // Client must send this in Connected Send packets
            _origConnectionId   = toConnId;  // We echo this in our Connected Send replies
            _connSerialNumber   = connSerial;
            _connSequenceNumber = 0;
            _isConnected        = true;

            _proto.Log($"ForwardOpen{(isExtended ? "Ex" : "")}: " +
                $"OT=0x{otConnId:X8} TO=0x{toConnId:X8} → assigned TargID=0x{newId:X8}");

            await SendForwardOpenResponse(otConnId, toConnId, connSerial, vendorId, serialNum, isExtended, context)
                  .ConfigureAwait(false);
        }

        /// <summary>
        /// Handles Forward Close (0x4E) request.
        /// Closes the connected messaging session and resets connection state.
        /// </summary>
        private async Task HandleForwardClose(byte[] buf, int offset, ushort length, EIPRequestContext context)
        {
            offset++;  // skip service code byte (0x4E)
            if (offset >= buf.Length) return;

            byte pathSize = buf[offset++];
            offset += pathSize * 2;

            if (offset + 10 > buf.Length) return;
            _ = buf[offset++]; // secsPerTick
            _ = buf[offset++]; // timeoutTicks
            ushort connSerial = ReadU16(buf, ref offset);
            ushort vendorId   = ReadU16(buf, ref offset);
            uint   serialNum  = ReadU32(buf, ref offset);

            _isConnected = false;
            _proto.Log($"ForwardClose: connection 0x{_origConnectionId:X8} closed");

            await SendForwardCloseResponse(connSerial, vendorId, serialNum, context).ConfigureAwait(false);
        }

        // ── Get Attributes (Identity Object) ────────────────────────────────

        /// <summary>
        /// Handles Get Attributes All (0x01) and Get Attribute Single (0x0E)
        /// requests for the Identity Object.
        /// </summary>
        private async Task HandleGetAttributes(byte[] buf, int offset, byte serviceCode, EIPRequestContext context)
        {
            using var ms = new MemoryStream();
            using var w  = new BinaryWriter(ms);

            WriteEipHeader(w, EIP_UNCONNECTED_SEND, _sessionHandle, context.SenderContext);
            WriteSendCpfHeader(w, 2);
            WriteNullAddressItem(w);

            w.Write(CPF_ITEM_UNCONNECTED_DATA);
            long lenPos    = ms.Position; w.Write((ushort)0);
            long dataStart = ms.Position;

            byte replySvc = (byte)(((serviceCode == CIP_SERVICE_GET_ATTRIBUTES_ALL) ? 0x01 : 0x0E) | 0x80);
            w.Write(replySvc);
            w.Write((byte)0x00);       // Reserved
            w.Write(CIP_STATUS_OK);
            w.Write((byte)0x00);       // Additional status size = 0
            w.Write(s_identityData);   // Identity attributes payload

            long dataEnd = ms.Position;
            ms.Seek(lenPos, SeekOrigin.Begin);
            w.Write((ushort)(dataEnd - dataStart));
            ms.Seek(dataEnd, SeekOrigin.Begin);

            FixEipLength(ms, w);
            await FlushAsync(ms).ConfigureAwait(false);
        }

        // ── Forward Open / Close response builders ───────────────────────────

        private async Task SendForwardOpenResponse(uint otConnId, uint toConnId,
            ushort connSerial, ushort vendorId, uint serialNum, bool isExtended, EIPRequestContext context)
        {
            byte replySvc = isExtended ? (byte)0xDB : (byte)0xD4;

            using var ms = new MemoryStream();
            using var w  = new BinaryWriter(ms);

            WriteEipHeader(w, EIP_UNCONNECTED_SEND, _sessionHandle, context.SenderContext);
            WriteSendCpfHeader(w, 2);
            WriteNullAddressItem(w);

            w.Write(CPF_ITEM_UNCONNECTED_DATA);
            long lenPos    = ms.Position; w.Write((ushort)0);
            long dataStart = ms.Position;

            // Forward Open Response body — matches eip_forward_open_response_t in libplctag defs.h.
            w.Write(replySvc);          // Reply service code (0xD4 or 0xDB)
            w.Write((byte)0x00);        // Reserved
            w.Write(CIP_STATUS_OK);     // General status
            w.Write((byte)0x00);        // Additional status size = 0
            w.Write(_targConnectionId); // orig_to_targ_conn_id — client uses this in Connected Send
            w.Write(_origConnectionId); // targ_to_orig_conn_id — echoed in our Connected Send replies
            w.Write(connSerial);        // Connection serial number (echoed)
            w.Write(vendorId);          // Originator vendor ID (echoed)
            w.Write(serialNum);         // Originator serial number (echoed)
            w.Write(RPI_US);            // O→T Actual Packet Interval (µs)
            w.Write(RPI_US);            // T→O Actual Packet Interval (µs)
            w.Write((byte)0x00);        // Application data size = 0
            w.Write((byte)0x00);        // Reserved

            long dataEnd = ms.Position;
            ms.Seek(lenPos, SeekOrigin.Begin);
            w.Write((ushort)(dataEnd - dataStart));
            ms.Seek(dataEnd, SeekOrigin.Begin);

            FixEipLength(ms, w);
            await FlushAsync(ms).ConfigureAwait(false);

            _proto.Log($"ForwardOpen response: replySvc=0x{replySvc:X2}, TargID=0x{_targConnectionId:X8}");
        }

        private async Task SendForwardCloseResponse(ushort connSerial, ushort vendorId, uint serialNum, EIPRequestContext context)
        {
            using var ms = new MemoryStream();
            using var w  = new BinaryWriter(ms);

            WriteEipHeader(w, EIP_UNCONNECTED_SEND, _sessionHandle, context.SenderContext);
            WriteSendCpfHeader(w, 2);
            WriteNullAddressItem(w);

            w.Write(CPF_ITEM_UNCONNECTED_DATA);
            long lenPos    = ms.Position; w.Write((ushort)0);
            long dataStart = ms.Position;

            // Forward Close Response body — matches eip_forward_close_resp_t in libplctag defs.h.
            w.Write((byte)0xCE);   // Reply service: 0x4E | 0x80
            w.Write((byte)0x00);   // Reserved
            w.Write(CIP_STATUS_OK);
            w.Write((byte)0x00);   // Additional status size = 0
            w.Write(connSerial);   // Connection serial number (echoed)
            w.Write(vendorId);     // Originator vendor ID (echoed)
            w.Write(serialNum);    // Originator serial number (echoed)
            w.Write((byte)0x00);   // Connection path size = 0
            w.Write((byte)0x00);   // Reserved

            long dataEnd = ms.Position;
            ms.Seek(lenPos, SeekOrigin.Begin);
            w.Write((ushort)(dataEnd - dataStart));
            ms.Seek(dataEnd, SeekOrigin.Begin);

            FixEipLength(ms, w);
            await FlushAsync(ms).ConfigureAwait(false);
        }

        // ── PCCC extraction and dispatch ─────────────────────────────────────

        /// <summary>
        /// Parses a CIP Execute PCCC request (service 0x4B) out of
        /// <paramref name="buf"/> starting at <paramref name="startOffset"/>,
        /// builds a DF1-style PDU, saves the Request ID bytes into the context
        /// for response echoing, and raises <see cref="PduReceived"/>.
        /// <para>
        /// PDU layout expected by <c>DF1Emulator.DispatchCommand</c>:
        ///   [DST, SRC, CMD, STS, TNS_LO, TNS_HI, FUNC?, DATA...]
        /// </para>
        /// <para>
        /// Request ID section layout (from libplctag eip_pccc_req_old in defs.h):
        ///   requestIdSize (1 byte) — total size of this section including itself
        ///   vendor_id     (2 bytes)
        ///   vendor_serial (4 bytes)
        ///   → requestIdSize is always 7 (1 + 2 + 4). Bytes to skip = requestIdSize − 1.
        /// </para>
        /// </summary>
        /// <param name="buf">Raw packet buffer</param>
        /// <param name="startOffset">Offset where the PCCC request begins</param>
        /// <param name="itemLength">Total length of the PCCC request</param>
        /// <param name="context">Request context (Request ID will be stored here)</param>
        private void ExtractAndDispatchPCCC(byte[] buf, int startOffset, ushort itemLength, EIPRequestContext context)
        {
            int offset  = startOffset;
            int itemEnd = startOffset + itemLength;

            if (offset >= buf.Length || offset >= itemEnd) return;

            // ── Service code ────────────────────────────────────────────────
            byte svc = buf[offset++];
            if (svc != CIP_SERVICE_EXECUTE_PCCC)
            {
                _proto.Log($"ExtractAndDispatchPCCC: unexpected service 0x{svc:X2} (expected 0x4B)");
                return;
            }

            // ── CIP path ────────────────────────────────────────────────────
            if (offset >= itemEnd || offset >= buf.Length) return;
            byte pathSize  = buf[offset++];
            int  pathBytes = pathSize * 2;
            if (offset + pathBytes > buf.Length || offset + pathBytes > itemEnd) return;
            offset += pathBytes;

            // ── Request ID ──────────────────────────────────────────────────
            // requestIdSize includes itself (1 byte) + vendor_id (2) + serial (4) = 7.
            // Save the entire section into the context for verbatim echo in the response.
            if (offset >= itemEnd || offset >= buf.Length) return;
            byte requestIdSize = buf[offset++];

            int skipBytes = requestIdSize >= 1 ? requestIdSize - 1 : 0;
            if (offset + skipBytes > buf.Length || offset + skipBytes > itemEnd) return;

            // Store Request ID in the context object (not in instance field).
            context.RequestId = new byte[requestIdSize];
            context.RequestId[0] = requestIdSize;
            for (int k = 1; k < requestIdSize; k++)
                context.RequestId[k] = buf[offset + k - 1];
            offset += skipBytes;

            // ── PCCC command header ──────────────────────────────────────────
            // Minimum: CMD(1) STS(1) TNS(2) FUNC(1) = 5 bytes.
            if (offset + 5 > buf.Length || offset + 5 > itemEnd)
            {
                _proto.Log($"ExtractAndDispatchPCCC: truncated PCCC header at offset {offset}");
                return;
            }

            byte   pcccCmd  = buf[offset++];
            byte   pcccSts  = buf[offset++];
            ushort pcccTns  = (ushort)(buf[offset] | (buf[offset + 1] << 8)); offset += 2;
            byte   pcccFunc = buf[offset++];

            int remaining = Math.Max(0, itemEnd - offset);

            // ── Build DF1-style PDU ──────────────────────────────────────────
            bool hasFunc = pcccFunc != 0 || remaining > 0;
            int  pduLen  = 6 + (hasFunc ? 1 : 0) + remaining;
            var  pdu     = new byte[pduLen];

            pdu[0] = 0x01;   // DST — emulator node
            pdu[1] = 0x01;   // SRC — client node
            pdu[2] = pcccCmd;
            pdu[3] = pcccSts;
            pdu[4] = (byte)(pcccTns & 0xFF);
            pdu[5] = (byte)((pcccTns >> 8) & 0xFF);

            int pduOff = 6;
            if (hasFunc)    pdu[pduOff++] = pcccFunc;
            if (remaining > 0)
                Array.Copy(buf, offset, pdu, pduOff, Math.Min(remaining, buf.Length - offset));

            _proto.Log($"PCCC dispatch: CMD=0x{pcccCmd:X2} TNS=0x{pcccTns:X4} FNC=0x{pcccFunc:X2} data={remaining}B");

            _proto.IncrementFramesProcessed();
            _proto._emulator.IncrementTotalPacketsReceived();

            // Raise PduReceived with the context object as clientContext.
            // DF1Emulator will process the command and call SendResponse(pdu, context)
            // when the reply is ready. The context contains SenderContext and RequestId
            // needed to build the response.
            _proto.PduReceived?.Invoke(this, (pdu, context));
        }

        // ── Response senders ─────────────────────────────────────────────────

        /// <summary>
        /// Serialized entry point called by <see cref="EIPProtocol.SendResponse"/>.
        /// Acquires _sendLock before delegating to SendResponseAsync() to guarantee
        /// FIFO ordering of outgoing responses within a single client session.
        ///
        /// Using a SemaphoreSlim(1,1) here ensures that if two requests complete on
        /// the thread pool at the same time, their responses are written to the socket
        /// in the order they were queued, not interleaved.
        ///
        /// Exceptions from the send path are caught and logged here so the caller
        /// (EIPProtocol.SendResponse) can safely fire-and-forget this task.
        /// </summary>
        /// <param name="pdu">PDU to send (built by DF1Emulator)</param>
        /// <param name="context">Request context containing SenderContext and RequestId</param>
        public async Task SendSerializedAsync(byte[] pdu, EIPRequestContext context)
        {
            if (_disposed || _proto.IsDisposing) return;

            await _sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await SendResponseAsync(pdu, context).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _proto.Log($"SendSerializedAsync failed for session 0x{_sessionHandle:X8}: {ex.Message}");
                _proto._emulator.IncrementUndeliveredPackets();
            }
            finally
            {
                _sendLock.Release();
            }
        }

        /// <summary>
        /// Routes to <see cref="SendConnectedResponse"/> or
        /// <see cref="SendUnconnectedResponse"/> depending on whether a
        /// Forward Open connection has been established.
        /// Called exclusively from <see cref="SendSerializedAsync"/> which
        /// holds _sendLock for the duration of this call.
        /// </summary>
        /// <param name="pdu">PDU to send (built by DF1Emulator)</param>
        /// <param name="context">Request context containing SenderContext and RequestId</param>
        private async Task SendResponseAsync(byte[] pdu, EIPRequestContext context)
        {
            if (_disposed || _proto.IsDisposing) return;

            _proto.Log($"SendResponseAsync: PDU length={pdu.Length}, connected={_isConnected}");

            if (_isConnected)
                await SendConnectedResponse(pdu, context).ConfigureAwait(false);
            else
                await SendUnconnectedResponse(pdu, context).ConfigureAwait(false);
        }

        /// <summary>
        /// Builds and sends a PCCC response inside a CIP Unconnected Send reply
        /// (EIP command 0x006F). CPF layout: NULL Address Item + Unconnected Data Item.
        /// </summary>
        /// <param name="pdu">PDU to send</param>
        /// <param name="context">Request context (contains SenderContext and RequestId)</param>
        private async Task SendUnconnectedResponse(byte[] pdu, EIPRequestContext context)
        {
            using var ms = new MemoryStream();
            using var w  = new BinaryWriter(ms);

            WriteEipHeader(w, EIP_UNCONNECTED_SEND, _sessionHandle, context.SenderContext);
            WriteSendCpfHeader(w, 2);
            WriteNullAddressItem(w);

            w.Write(CPF_ITEM_UNCONNECTED_DATA);
            long lenPos    = ms.Position; w.Write((ushort)0);
            long dataStart = ms.Position;

            WritePcccReplyHeader(w, pdu, context.RequestId);

            // Data payload — response PDU layout from DF1Emulator:
            //   [DST, SRC, CMD, STS, TNS_LO, TNS_HI, DATA...]
            // Data bytes start at offset 6 (no FUNC byte in DF1Emulator data responses).
            const int dataOffset = 6;
            if (pdu.Length > dataOffset)
                w.Write(pdu, dataOffset, pdu.Length - dataOffset);

            long dataEnd = ms.Position;
            ms.Seek(lenPos, SeekOrigin.Begin);
            w.Write((ushort)(dataEnd - dataStart));
            ms.Seek(dataEnd, SeekOrigin.Begin);

            FixEipLength(ms, w);
            await FlushAsync(ms).ConfigureAwait(false);
        }

        /// <summary>
        /// Builds and sends a PCCC response inside a CIP Connected Send reply
        /// (EIP command 0x0070). CPF layout: Connected Address Item + Connected Data Item.
        /// </summary>
        /// <param name="pdu">PDU to send</param>
        /// <param name="context">Request context (contains SenderContext and RequestId)</param>
        private async Task SendConnectedResponse(byte[] pdu, EIPRequestContext context)
        {
            using var ms = new MemoryStream();
            using var w  = new BinaryWriter(ms);

            WriteEipHeader(w, EIP_CONNECTED_SEND, _sessionHandle, context.SenderContext);
            WriteSendCpfHeader(w, 2);

            // Connected Address Item carries the T→O connection ID.
            w.Write(CPF_ITEM_CONNECTED_ADDRESS);
            w.Write((ushort)4);
            w.Write(_origConnectionId);

            // Connected Data Item.
            w.Write(CPF_ITEM_CONNECTED_DATA);
            long lenPos    = ms.Position; w.Write((ushort)0);
            long dataStart = ms.Position;

            // Connection sequence number increments monotonically; cast to ushort
            // so it wraps at 0xFFFF as per EIP spec (intentional truncation).
            w.Write((ushort)(Interlocked.Increment(ref _connSequenceNumber) & 0xFFFF));

            WritePcccReplyHeader(w, pdu, context.RequestId);

            const int dataOffset = 6;
            if (pdu.Length > dataOffset)
                w.Write(pdu, dataOffset, pdu.Length - dataOffset);

            long dataEnd = ms.Position;
            ms.Seek(lenPos, SeekOrigin.Begin);
            w.Write((ushort)(dataEnd - dataStart));
            ms.Seek(dataEnd, SeekOrigin.Begin);

            FixEipLength(ms, w);
            await FlushAsync(ms).ConfigureAwait(false);
        }

        /// <summary>
        /// Writes the CIP Execute PCCC reply header (service 0xCB) and echoes
        /// the Request ID bytes from the context.
        /// <para>
        /// Layout (from libplctag pccc_resp in defs.h):
        ///   reply_code     (1) = 0xCB
        ///   reserved       (1) = 0x00
        ///   general_status (1) = 0x00 (ok)
        ///   status_size    (1) = 0x00
        ///   request_id     (N) = echoed from request context
        ///   pccc_command   (1)
        ///   pccc_status    (1)
        ///   pccc_seq_num   (2)
        /// </para>
        /// </summary>
        /// <param name="w">BinaryWriter to write to</param>
        /// <param name="pdu">PDU containing response fields</param>
        /// <param name="requestId">Request ID bytes from the context (to echo)</param>
        private void WritePcccReplyHeader(BinaryWriter w, byte[] pdu, byte[]? requestId)
        {
            w.Write((byte)0xCB);   // Execute PCCC reply service code (0x4B | 0x80)
            w.Write((byte)0x00);   // Reserved
            w.Write(CIP_STATUS_OK);
            w.Write((byte)0x00);   // Additional status size = 0

            // Echo the Request ID that was stored in the request context.
            if (requestId != null)
            {
                w.Write(requestId);
            }
            else
            {
                // Fallback: use our own vendor identity when no Request ID was saved.
                w.Write((byte)7);
                w.Write(VENDOR_ID);
                w.Write(VENDOR_SERIAL_NUMBER);
            }

            // PCCC response fields (echoed from PDU).
            w.Write(pdu[2]);                                    // CMD
            w.Write(pdu[3]);                                    // STS
            w.Write((ushort)(pdu[4] | (pdu[5] << 8)));         // TNS
        }

        // ── Error reply ──────────────────────────────────────────────────────

        /// <summary>
        /// Sends an EIP error response for commands that cannot be fulfilled.
        /// Per EIP Vol 2, §2-3: the Status field in the header carries the error
        /// code; the payload length is zero.
        /// </summary>
        /// <param name="command">Command code to echo in response</param>
        /// <param name="errorStatus">Status code (EIP_STATUS_*)</param>
        /// <param name="context">Request context (contains SenderContext for echo)</param>
        private async Task SendErrorReply(ushort command, uint errorStatus, EIPRequestContext context)
        {
            var response = new byte[24];
            response[0] = (byte)(command & 0xFF);
            response[1] = (byte)((command >> 8) & 0xFF);
            // Length = 0 (bytes 2-3 remain zero)
            response[4] = (byte)(_sessionHandle & 0xFF);
            response[5] = (byte)((_sessionHandle >> 8)  & 0xFF);
            response[6] = (byte)((_sessionHandle >> 16) & 0xFF);
            response[7] = (byte)((_sessionHandle >> 24) & 0xFF);
            // Error status at bytes 8-11
            response[8]  = (byte)(errorStatus & 0xFF);
            response[9]  = (byte)((errorStatus >> 8)  & 0xFF);
            response[10] = (byte)((errorStatus >> 16) & 0xFF);
            response[11] = (byte)((errorStatus >> 24) & 0xFF);
            BitConverter.TryWriteBytes(response.AsSpan(12), context.SenderContext);

            await SendRawResponse(response, response.Length).ConfigureAwait(false);
        }

        // ── Stream flush helper ───────────────────────────────────────────────

        private async Task FlushAsync(MemoryStream ms)
        {
            long end = ms.Position;
            byte[] bytes = ms.GetBuffer();
            await SendRawResponse(bytes, (int)end).ConfigureAwait(false);
        }

        // ── IDisposable ───────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _sendLock.Dispose();
            try { _stream.Close(); } catch { }
            try { _tcp.Close();    } catch { }
        }
    }
}
