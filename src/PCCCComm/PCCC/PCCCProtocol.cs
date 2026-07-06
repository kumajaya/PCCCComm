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
using System.Collections.Concurrent;
using System.Threading;
using PCCCComm.Core;
using static PCCCComm.Pccc.PCCCConstants;

namespace PCCCComm.Pccc
{
    /// <summary>
    /// Core PCCC protocol engine. Handles request/reply exchange, TNS management,
    /// and basic commands (read, write, mode control, etc.).
    /// Does NOT perform chunking or data type conversion; those belong to the facade.
    /// </summary>
    public class PCCCProtocol : IDisposable
    {
        private readonly ITransport _transport;
        private ushort _nextTns = 1;
        private readonly object _tnsLock = new object();
        private readonly ConcurrentDictionary<ushort, ManualResetEventSlim> _responseEvents = new();
        private readonly ConcurrentDictionary<ushort, byte[]> _responseData = new();
        private int _responseTimeoutMs = 2000;

        /// <summary>Initializes the protocol engine with a transport.</summary>
        public PCCCProtocol(ITransport transport)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _transport.FrameReceived += OnFrameReceived;
            _requestThrottle = new SemaphoreSlim(_maxConcurrentRequests, _maxConcurrentRequests);
        }

        /// <summary>Timeout in milliseconds for waiting a reply (default 2000).</summary>
        public int ResponseTimeoutMs
        {
            get => _responseTimeoutMs;
            set => _responseTimeoutMs = value > 0 ? value : 2000;
        }

        private ushort GetNextTns()
        {
            lock (_tnsLock)
            {
                if (++_nextTns == 0) _nextTns = 1;
                return _nextTns;
            }
        }

        private void OnFrameReceived(object? sender, byte[] innerFrame)
        {
            if (innerFrame == null || innerFrame.Length < 6) return;
            // Parse into a PCCCMessage so field names are used instead of raw indices.
            var msg = PCCCMessage.FromBytes(innerFrame);
            _responseData[msg.Tns] = innerFrame;
            if (_responseEvents.TryGetValue(msg.Tns, out var ev))
                ev.Set();
        }

        /// <summary>
        /// Sends a PCCC request and waits for the reply.
        /// </summary>
        /// <param name="request">The request message (TNS can be zero to auto-assign).</param>
        /// <param name="statusCode">Output status code (STS or combined STS+EXT STS).</param>
        /// <returns>Reply message, or null if timeout or transport error.</returns>
        public PCCCMessage? SendRequest(PCCCMessage request, out int statusCode)
        {
            statusCode = 0;

            // 1. Circuit Breaker: Reject new requests immediately if the circuit is open.
            if (_healthState == 0)
            {
                statusCode = -23; // Circuit Open
                return null;
            }

            // 2. Bulkhead: Limit concurrent requests to prevent thread pool starvation.
            if (!_requestThrottle.Wait(_requestQueueTimeoutMs))
            {
                statusCode = -22; // Too busy (throttled)
                return null;
            }

            try
            {
                if (request == null) throw new ArgumentNullException(nameof(request));
                if (!_transport.IsOpen) throw new InvalidOperationException("Transport is not open.");

                ushort tns = request.Tns;
                if (tns == 0)
                {
                    tns = GetNextTns();
                    request.Tns = tns;
                }

                var ev = new ManualResetEventSlim(false);
                _responseEvents[tns] = ev;
                _responseData.TryRemove(tns, out _);

                try
                {
                    _transport.SendFrame(request.ToBytes());
                }
                catch (Exception)
                {
                    // Send failure (e.g., timeout, port error) counts as a consecutive failure
                    // for the circuit breaker, just like a response timeout.
                    if (_responseEvents.TryRemove(tns, out var evToDispose))
                        evToDispose.Dispose();

                    int timeouts = Interlocked.Increment(ref _consecutiveTimeouts);
                    if (timeouts >= MaxConsecutiveTimeouts)
                    {
                        Interlocked.Exchange(ref _healthState, 0); // Trip the circuit
                    }

                    statusCode = -6; // Send error (kept as-is for backward compatibility)
                    return null;
                }

                // 3. Wait for the transport response.
                if (!ev.Wait(_responseTimeoutMs))
                {
                    if (_responseEvents.TryRemove(tns, out var evToDispose))
                        evToDispose.Dispose();

                    // Circuit Breaker Logic: Count consecutive timeouts to trip the breaker.
                    int timeouts = Interlocked.Increment(ref _consecutiveTimeouts);
                    if (timeouts >= MaxConsecutiveTimeouts)
                    {
                        Interlocked.Exchange(ref _healthState, 0); // Trip the circuit
                    }

                    statusCode = -20;
                    return null;
                }

                // 4. Success: Reset the timeout counter and close the circuit.
                Interlocked.Exchange(ref _consecutiveTimeouts, 0);
                Interlocked.Exchange(ref _healthState, 1);

                if (!_responseData.TryGetValue(tns, out var rawReply))
                {
                    if (_responseEvents.TryRemove(tns, out var evToDispose))
                        evToDispose.Dispose();
                    statusCode = -8;
                    return null;
                }

                if (_responseEvents.TryRemove(tns, out var evToDisposeSuccess))
                    evToDisposeSuccess.Dispose();
                _responseData.TryRemove(tns, out _);

                var reply = PCCCMessage.FromBytes(rawReply, hasFnc: false);
                statusCode = reply.Sts;
                if (statusCode == Sts.ExtStsPresent && reply.Data.Length >= 3)
                    statusCode = 0x100 + reply.Data[reply.Data.Length - 1];

                return reply;
            }
            finally
            {
                _requestThrottle.Release(); // Release the throttle slot
            }
        }

        /// <summary>
        /// Sends a PCCC request asynchronously (fire-and-forget).
        /// No response is waited for, and no event is created.
        /// </summary>
        public void SendRequestAsync(PCCCMessage request)
        {
            if (!_transport.IsOpen) throw new InvalidOperationException("Transport not open.");
            ushort tns = request.Tns == 0 ? GetNextTns() : request.Tns;
            request.Tns = tns;
            // No event registration; just send.
            _transport.SendFrame(request.ToBytes());
        }

        // --- Basic single-PDU operations ------------------------------------

        /// <summary>Reads raw data from a single PDU (no chunking).</summary>
        public byte[] Read(DataAddress addr, int bytesToRead, out int statusCode, byte myNode, byte targetNode)
        {
            var req = PCCCMessage.CreateReadRequest(addr, bytesToRead, 0, myNode, targetNode);
            var reply = SendRequest(req, out statusCode);
            return (reply != null && statusCode == Sts.Success) ? reply.Data : Array.Empty<byte>();
        }

        /// <summary>Writes raw data in a single PDU (no chunking).</summary>
        public void Write(DataAddress addr, byte[] data, out int statusCode, byte myNode, byte targetNode)
        {
            var req = PCCCMessage.CreateWriteRequest(addr, data, 0, data.Length, 0, myNode, targetNode);
            SendRequest(req, out statusCode);
        }

        /// <summary>Read-Modify-Write operation (single PDU).</summary>
        public void ReadModifyWrite(DataAddress[] addrs, ushort[] andMasks, ushort[] orMasks,
                    out int statusCode, byte myNode, byte targetNode)
        {
            var req = PCCCMessage.CreateReadModifyWriteRequest(addrs, andMasks, orMasks, 0, myNode, targetNode);
            SendRequest(req, out statusCode);
        }

        // --- Processor information and mode control -------------------------

        /// <summary>Returns the processor type code (e.g., 0x49 for SLC 5/03).</summary>
        internal byte GetProcessorType(byte myNode, byte targetNode)
        {
            var req = PCCCMessage.CreateDiagnosticStatusRequest(0, myNode, targetNode);
            var reply = SendRequest(req, out int sts);
            if (reply == null || sts != Sts.Success || reply.Data.Length < 10)
                return 0;
            return reply.Data[ResponseOffsets.DiagnosticStatus.ProcessorType];
        }

        internal byte[]? GetDiagnosticStatusRaw(byte myNode, byte targetNode)
        {
            var req = PCCCMessage.CreateDiagnosticStatusRequest(0, myNode, targetNode);
            var reply = SendRequest(req, out int sts);
            if (sts != Sts.Success || reply?.Data == null)
                return null;
            return reply.Data;
        }

        /// <summary>Places the processor in Run mode.</summary>
        public void SetRunMode(bool isMicroLogix, byte myNode, byte targetNode)
        {
            byte modeValue = isMicroLogix ? (byte)2 : (byte)6;
            var req = PCCCMessage.CreateChangeModeRequest(modeValue, isMicroLogix, 0, myNode, targetNode);
            SendRequest(req, out int sts);
            if (sts != Sts.Success)
                throw new PCCCException($"SetRunMode failed: {PCCCErrors.DecodeStatus(sts)}");
        }

        /// <summary>Places the processor in Program mode.</summary>
        public void SetProgramMode(bool isMicroLogix, byte myNode, byte targetNode)
        {
            byte modeValue = isMicroLogix ? (byte)0 : (byte)1;
            var req = PCCCMessage.CreateChangeModeRequest(modeValue, isMicroLogix, 0, myNode, targetNode);
            SendRequest(req, out int sts);
            if (sts != Sts.Success)
                throw new PCCCException($"SetProgramMode failed: {PCCCErrors.DecodeStatus(sts)}");
        }

        /// <summary>Disables forces on the processor (CMD=0x0F, FNC=0x41).</summary>
        public void DisableForces(byte myNode, byte targetNode)
        {
            var req = PCCCMessage.CreateDisableForcesRequest(0, myNode, targetNode);
            SendRequest(req, out int sts);
            if (sts != Sts.Success)
                throw new PCCCException($"DisableForces failed: {PCCCErrors.DecodeStatus(sts)}");
        }

        /// <summary>Enables forces on the processor (CMD=0x0F, FNC=0x42).</summary>
        public void EnableForces(byte myNode, byte targetNode)
        {
            var req = PCCCMessage.CreateEnableForcesRequest(0, myNode, targetNode);
            SendRequest(req, out int sts);
            if (sts != Sts.Success)
                throw new PCCCException($"EnableForces failed: {PCCCErrors.DecodeStatus(sts)}");
        }

        /// <summary>Clears all forces on the processor (CMD=0x0F, FNC=0x43).</summary>
        public void ClearForces(byte myNode, byte targetNode)
        {
            var req = PCCCMessage.CreateClearForcesRequest(0, myNode, targetNode);
            SendRequest(req, out int sts);
            if (sts != Sts.Success)
                throw new PCCCException($"ClearForces failed: {PCCCErrors.DecodeStatus(sts)}");
        }

        // ========================================================================
        // File-based upload/download commands (SLC 5/03+)
        // ========================================================================

        public ushort OpenFile(byte fileNumber, byte fileType, byte myNode, byte targetNode)
        {
            var req = PCCCMessage.CreateOpenFileRequest(fileNumber, fileType, 0, myNode, targetNode);
            var reply = SendRequest(req, out int sts);
            if (sts != Sts.Success || reply?.Data == null || reply.Data.Length < 2)
                throw new PCCCException($"OpenFile failed: {PCCCErrors.DecodeStatus(sts)}");
            return (ushort)(reply.Data[0] | (reply.Data[1] << 8));
        }

        public void CloseFile(ushort tag, byte myNode, byte targetNode)
        {
            var req = PCCCMessage.CreateCloseFileRequest(tag, 0, myNode, targetNode);
            SendRequest(req, out int sts);
            if (sts != Sts.Success)
                throw new PCCCException($"CloseFile failed: {PCCCErrors.DecodeStatus(sts)}");
        }

        public byte[] FileRead(ushort tag, int offset, int bytesToRead, byte myNode, byte targetNode)
        {
            var req = PCCCMessage.CreateFileReadRequest(tag, offset, bytesToRead, 0, myNode, targetNode);
            var reply = SendRequest(req, out int sts);
            if (sts != Sts.Success || reply?.Data == null)
                throw new PCCCException($"FileRead failed: {PCCCErrors.DecodeStatus(sts)}");
            return reply.Data;
        }

        public int FileWrite(ushort tag, int offset, byte[] data, byte myNode, byte targetNode)
        {
            var req = PCCCMessage.CreateFileWriteRequest(tag, offset, data, 0, myNode, targetNode);
            SendRequest(req, out int sts);
            return sts;
        }

        public byte[] UploadAllRequest(byte myNode, byte targetNode)
        {
            var req = PCCCMessage.CreateUploadAllRequest(0, myNode, targetNode);
            var reply = SendRequest(req, out int sts);
            if (sts != Sts.Success)
                throw new PCCCException($"UploadAllRequest failed: {PCCCErrors.DecodeStatus(sts)}");
            return reply?.Data ?? Array.Empty<byte>();
        }

        public void UploadCompleted(byte myNode, byte targetNode)
        {
            var req = PCCCMessage.CreateUploadCompletedRequest(0, myNode, targetNode);
            SendRequest(req, out int sts);
            if (sts != Sts.Success)
                throw new PCCCException($"UploadCompleted failed: {PCCCErrors.DecodeStatus(sts)}");
        }

        public byte[] DownloadAllRequest(byte myNode, byte targetNode)
        {
            var req = PCCCMessage.CreateDownloadAllRequest(0, myNode, targetNode);
            var reply = SendRequest(req, out int sts);
            if (sts != Sts.Success)
                throw new PCCCException($"DownloadAllRequest failed: {PCCCErrors.DecodeStatus(sts)}");
            return reply?.Data ?? Array.Empty<byte>();
        }

        public void DownloadCompleted(byte myNode, byte targetNode)
        {
            var req = PCCCMessage.CreateDownloadCompletedRequest(0, myNode, targetNode);
            SendRequest(req, out int sts);
            if (sts != Sts.Success)
                throw new PCCCException($"DownloadCompleted failed: {PCCCErrors.DecodeStatus(sts)}");
        }

        public void ExecuteCommandList(byte[][] commands, byte myNode, byte targetNode)
        {
            var req = PCCCMessage.CreateExecuteCommandListRequest(commands, 0, myNode, targetNode);
            var reply = SendRequest(req, out int sts);
            if (sts != Sts.Success)
                throw new PCCCException($"ExecuteCommandList failed: {PCCCErrors.DecodeStatus(sts)}");
            // Response data may contain per‑command status; usually not needed.
        }

        public void GetEditResource(byte myNode, byte targetNode)
        {
            var req = PCCCMessage.CreateGetEditResourceRequest(0, myNode, targetNode);
            SendRequest(req, out int sts);
            if (sts != Sts.Success)
                throw new PCCCException($"GetEditResource failed: {PCCCErrors.DecodeStatus(sts)}");
        }

        public void ReturnEditResource(byte myNode, byte targetNode)
        {
            var req = PCCCMessage.CreateReturnEditResourceRequest(0, myNode, targetNode);
            SendRequest(req, out int sts);
            if (sts != Sts.Success)
                throw new PCCCException($"ReturnEditResource failed: {PCCCErrors.DecodeStatus(sts)}");
        }

        public void ApplyPortConfiguration(byte myNode, byte targetNode)
        {
            var req = PCCCMessage.CreateApplyPortConfigRequest(0, myNode, targetNode);
            SendRequest(req, out int sts);
            if (sts != Sts.Success)
                throw new PCCCException($"ApplyPortConfiguration failed: {PCCCErrors.DecodeStatus(sts)}");
        }

        public void InitializeMemory(byte myNode, byte targetNode)
        {
            var req = PCCCMessage.CreateInitializeMemoryRequest(0, myNode, targetNode);
            SendRequest(req, out int sts);
            if (sts != Sts.Success)
                throw new PCCCException($"InitializeMemory failed: {PCCCErrors.DecodeStatus(sts)}");
        }

        // ========================================================================
        // Diagnostic commands (CMD=0x06)
        // ========================================================================

        public byte[] ReadDiagnosticCounters(byte myNode, byte targetNode)
        {
            var req = PCCCMessage.CreateReadDiagnosticCountersRequest(0, myNode, targetNode);
            var reply = SendRequest(req, out int sts);
            if (sts != Sts.Success || reply?.Data == null)
                throw new PCCCException($"ReadDiagnosticCounters failed: {PCCCErrors.DecodeStatus(sts)}");
            return reply.Data;
        }

        public void ResetDiagnosticCounters(byte myNode, byte targetNode)
        {
            var req = PCCCMessage.CreateResetDiagnosticCountersRequest(0, myNode, targetNode);
            SendRequest(req, out int sts);
            if (sts != Sts.Success)
                throw new PCCCException($"ResetDiagnosticCounters failed: {PCCCErrors.DecodeStatus(sts)}");
        }

        public byte ReadLinkParameters(byte myNode, byte targetNode)
        {
            var req = PCCCMessage.CreateReadLinkParamsRequest(0, myNode, targetNode);
            var reply = SendRequest(req, out int sts);
            if (sts != Sts.Success || reply?.Data == null || reply.Data.Length == 0)
                throw new PCCCException($"ReadLinkParameters failed: {PCCCErrors.DecodeStatus(sts)}");
            return reply.Data[0];
        }

        public void SetLinkParameters(byte maxAddress, byte myNode, byte targetNode)
        {
            var req = PCCCMessage.CreateSetLinkParamsRequest(maxAddress, 0, myNode, targetNode);
            SendRequest(req, out int sts);
            if (sts != Sts.Success)
                throw new PCCCException($"SetLinkParameters failed: {PCCCErrors.DecodeStatus(sts)}");
        }

        public byte[] Echo(byte[] data, byte myNode, byte targetNode)
        {
            var req = PCCCMessage.CreateEchoRequest(data, 0, myNode, targetNode);
            var reply = SendRequest(req, out int sts);
            if (sts != Sts.Success || reply?.Data == null)
                throw new PCCCException($"Echo failed: {PCCCErrors.DecodeStatus(sts)}");

            // Echo response format (AB 1770-6.5.16): CMD_reply STS TNS FNC(0x00) DATA
            // SendRequest parses with hasFnc:false so FNC byte lands in Data[0].
            // Strip it so callers receive only the echoed payload.
            byte[] d = reply.Data;
            if (d.Length > 0 && d[0] == PCCCConstants.Fnc.Echo)
            {
                byte[] stripped = new byte[d.Length - 1];
                Array.Copy(d, 1, stripped, 0, stripped.Length);
                return stripped;
            }
            return d;
        }

        /// <summary>Performs a Word Range Read (PLC-5, FNC=0x01).</summary>
        public byte[] WordRangeRead(byte[] logicalAddress, int wordOffset, int sizeWords,
            byte myNode, byte targetNode)
        {
            var req = PCCCMessage.CreateWordRangeReadRequest(logicalAddress, wordOffset, sizeWords,
                0, myNode, targetNode);
            var reply = SendRequest(req, out int sts);
            if (sts != Sts.Success)
                throw new PCCCException($"WordRangeRead failed: {PCCCErrors.DecodeStatus(sts)}");
            return reply?.Data ?? Array.Empty<byte>();
        }

        /// <summary>Performs a Word Range Write (PLC-5, FNC=0x00).</summary>
        public void WordRangeWrite(byte[] logicalAddress, int wordOffset, byte[] data,
            byte myNode, byte targetNode)
        {
            if (data.Length % 2 != 0)
                throw new ArgumentException("Data length must be even for word write.", nameof(data));
            var req = PCCCMessage.CreateWordRangeWriteRequest(logicalAddress, wordOffset, data,
                0, myNode, targetNode);
            var reply = SendRequest(req, out int sts);
            if (sts != Sts.Success)
                throw new PCCCException($"WordRangeWrite failed: {PCCCErrors.DecodeStatus(sts)}");
        }

        /// <summary>Reads raw physical memory from PLC-5.</summary>
        public byte[] ReadBytesPhysical(int address, int bytesToRead, byte myNode, byte targetNode)
        {
            var req = PCCCMessage.CreateReadBytesPhysicalRequest(address, bytesToRead, 0, myNode, targetNode);
            var reply = SendRequest(req, out int sts);
            if (sts != Sts.Success || reply?.Data == null)
                throw new PCCCException($"ReadBytesPhysical failed: {PCCCErrors.DecodeStatus(sts)}");
            return reply.Data;
        }

        /// <summary>Writes raw physical memory to PLC-5.</summary>
        public bool WriteBytesPhysical(int address, byte[] data, byte myNode, byte targetNode)
        {
            var req = PCCCMessage.CreateWriteBytesPhysicalRequest(address, data, 0, myNode, targetNode);
            SendRequest(req, out int sts);
            if (sts != Sts.Success)
                throw new PCCCException($"WriteBytesPhysical failed: {PCCCErrors.DecodeStatus(sts)}");
            return true;   // success
        }

        /// <summary>
        /// Returns true if there is a pending request waiting for the given TNS.
        /// </summary>
        public bool IsTnsPending(ushort tns) => _responseEvents.ContainsKey(tns);

        // --- Circuit Breaker & Bulkhead (Anti-Starvation) ---------------------

        private SemaphoreSlim _requestThrottle;
        private int _maxConcurrentRequests = 10;
        private int _requestQueueTimeoutMs = 50;
        private int _consecutiveTimeouts = 0;
        private const int DefaultMaxConsecutiveTimeouts = 3;
        private volatile int _healthState = 1; // 1 = Healthy, 0 = Circuit Open

        /// <summary>
        /// Gets or sets the maximum number of concurrent PCCC requests allowed.
        /// Prevents thread pool starvation when many requests are fired simultaneously.
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
                    var old = Interlocked.Exchange(ref _requestThrottle, new SemaphoreSlim(value, value));
                    old?.Dispose();
                }
            }
        }

        /// <summary>
        /// Gets or sets the maximum number of consecutive timeouts allowed before
        /// the circuit breaker trips and blocks further requests.
        /// Default is 3.
        /// </summary>
        public int MaxConsecutiveTimeouts { get; set; } = DefaultMaxConsecutiveTimeouts;

        /// <summary>
        /// Resets the circuit breaker state. Should be called after a successful reconnection
        /// (e.g., in OpenComms) to allow requests to flow again.
        /// </summary>
        public void ResetHealth()
        {
            Interlocked.Exchange(ref _consecutiveTimeouts, 0);
            Interlocked.Exchange(ref _healthState, 1);
        }

        // --- Cleanup ---------------------------------------------------------
        public void Dispose()
        {
            _transport.FrameReceived -= OnFrameReceived;

            // Wake any thread currently blocked in SendRequest's ev.Wait(timeout) before
            // clearing the table. Without this, a request in flight when the connection is
            // closed (e.g. the processor-family diagnostic probe in OpenComms, or a normal
            // poll) would keep waiting the full response timeout even though the transport
            // is going away — this is what made stopping the driver hang until the request
            // timed out (or until the PLC happened to answer). Setting the event lets Wait
            // return immediately; SendRequest then finds no response data and exits with a
            // no-data/timeout status, which the caller treats as a failed request.
            foreach (var ev in _responseEvents.Values)
            {
                try { ev.Set(); } catch { /* event may already be disposed elsewhere */ }
            }

            // Clear any pending events (without disposing them, as original never disposed)
            _responseEvents.Clear();
            _responseData.Clear();
            _requestThrottle?.Dispose();
        }
    }
}
