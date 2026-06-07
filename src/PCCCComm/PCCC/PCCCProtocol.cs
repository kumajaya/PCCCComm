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
            ushort tns = (ushort)(innerFrame[4] | (innerFrame[5] << 8));
            _responseData[tns] = innerFrame;
            if (_responseEvents.TryGetValue(tns, out var ev))
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
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (!_transport.IsOpen) throw new InvalidOperationException("Transport is not open.");

            // Assign TNS if not set
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
                _responseEvents.TryRemove(tns, out _);
                statusCode = -6; // Send error
                return null;
            }

            if (!ev.Wait(_responseTimeoutMs))
            {
                _responseEvents.TryRemove(tns, out _);
                statusCode = -20; // Timeout
                return null;
            }

            if (!_responseData.TryGetValue(tns, out var rawReply))
            {
                _responseEvents.TryRemove(tns, out _);
                statusCode = -8; // No data returned
                return null;
            }

            _responseEvents.TryRemove(tns, out _);
            _responseData.TryRemove(tns, out _);

            var reply = PCCCMessage.FromBytes(rawReply, hasFnc: false);
            statusCode = reply.Sts;
            // Check for extended status (STS = 0xF0)
            if (statusCode == Sts.ExtStsPresent && reply.Data.Length >= 3)
                statusCode = 0x100 + reply.Data[reply.Data.Length - 1];
            return reply;
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

        /// <summary>
        /// Returns true if there is a pending request waiting for the given TNS.
        /// </summary>
        public bool IsTnsPending(ushort tns) => _responseEvents.ContainsKey(tns);

        // --- Cleanup ---------------------------------------------------------
        public void Dispose()
        {
            _transport.FrameReceived -= OnFrameReceived;
            // Clear any pending events (without disposing them, as original never disposed)
            _responseEvents.Clear();
            _responseData.Clear();
        }
    }
}
