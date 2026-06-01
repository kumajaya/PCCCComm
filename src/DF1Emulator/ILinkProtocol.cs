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

/// <summary>
/// Protocol abstraction for DF1 emulator link layer implementations.
/// Supports DF1 Full-Duplex (serial), EtherNet/IP (EIP/PCCC), and future DH485.
/// </summary>
public interface ILinkProtocol
{
    /// <summary>
    /// Starts the protocol handler (opens serial port, starts listener, etc.)
    /// </summary>
    void Start();

    /// <summary>
    /// Stops the protocol handler gracefully.
    /// </summary>
    void Stop();

    /// <summary>
    /// Sends a response PDU back to the client using this protocol's framing.
    /// The PDU is the inner frame (DST, SRC, CMD, STS, TNS, FUNC, DATA...)
    /// </summary>
    /// <param name="pdu">Inner frame PDU to send</param>
    /// <param name="clientContext">Client context object (e.g., EIPClient instance) for routing response to correct client.
    /// For single-client protocols like DF1 serial, this parameter is ignored.</param>
    void SendResponse(byte[] pdu, object clientContext);

    /// <summary>
    /// Raised when a complete PDU (inner frame) has been received and parsed.
    /// The handler should dispatch the command to PlcMemory.
    /// </summary>
    event EventHandler<(byte[] pdu, object ClientContext)> PduReceived;

    /// <summary>
    /// Human-readable name of the protocol for logging.
    /// </summary>
    string Name { get; }
}
