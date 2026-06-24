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
using System.Threading;

namespace PCCCComm.Handlers;

/// <summary>
/// Provides the minimal context that a PLC handler needs from the main PCCCComm facade.
/// </summary>
public interface IHandlerContext
{
    /// <summary>Local node address (source).</summary>
    int MyNode { get; }

    /// <summary>Remote node address (target).</summary>
    int TargetNode { get; }

    /// <summary>If true, write operations are performed asynchronously (fire‑and‑forget).</summary>
    bool AsyncMode { get; }

    /// <summary>Suppresses the DataReceived event during bulk transfers.</summary>
    bool DisableEvent { get; set; }

    /// <summary>Raises the file progress event for upload/download operations.</summary>
    void RaiseFileProgress(PCCCComm.FileProgressEventArgs e);

    /// <summary>
    /// Token used to cancel long-running bulk operations (upload, download).
    /// Handlers should call <see cref="CancellationToken.ThrowIfCancellationRequested"/>
    /// at each per-file checkpoint during transfer.
    /// </summary>
    CancellationToken CancellationToken { get; }
}
