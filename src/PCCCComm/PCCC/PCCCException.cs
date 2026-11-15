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

namespace PCCCComm.Pccc;

/// <summary>
/// Custom exception for DF1 library errors.
/// </summary>
public class PCCCException : Exception
{
    /// <summary>
    /// Status code that caused the exception: a PCCC STS/EXT STS (0x01-0xFF, 0x100+ for
    /// EXT STS) or a negative library code (e.g. -20 timeout, -23 circuit open).
    /// 0 when the exception was not raised from a status code (e.g. an invalid address).
    /// </summary>
    public int StatusCode { get; }

    public PCCCException() { }

    public PCCCException(string message) : base(message) { }

    public PCCCException(string message, Exception inner) : base(message, inner) { }

    /// <summary>Creates an exception whose message is the decoded <paramref name="statusCode"/>.</summary>
    public PCCCException(int statusCode) : base(PCCCErrors.DecodeStatus(statusCode))
    {
        StatusCode = statusCode;
    }

    /// <summary>Creates an exception with a custom message and the status code behind it.</summary>
    public PCCCException(string message, int statusCode) : base(message)
    {
        StatusCode = statusCode;
    }
}
