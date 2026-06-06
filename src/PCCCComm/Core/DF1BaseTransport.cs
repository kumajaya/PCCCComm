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
using System.Collections.Generic;
using System.Threading;

namespace PCCCComm.Core;

/// <inheritdoc cref="ITransport"/>
/// <summary>
/// Abstract base class for DF1 transport implementations (full‑duplex and half‑duplex master).
/// Provides common DF1 framing services: DLE stuffing, checksum calculation, control byte
/// transmission, and raw frame events. Derived classes must implement <see cref="SendFrame"/>.
/// </summary>
public abstract class DF1BaseTransport : ITransport
{
    // --- DF1 control characters (Publication 1770-6.5.16, Chapter 5) ---
    /// <summary>Data Link Escape (0x10).</summary>
    protected const byte DLE = 0x10;
    /// <summary>Start of Text (0x02).</summary>
    protected const byte STX = 0x02;
    /// <summary>End of Text (0x03).</summary>
    protected const byte ETX = 0x03;
    /// <summary>Acknowledge (0x06).</summary>
    protected const byte ACK = 0x06;
    /// <summary>Not Acknowledge (0x15).</summary>
    protected const byte NAK = 0x15;
    /// <summary>Enquiry (0x05).</summary>
    protected const byte ENQ = 0x05;

    // --- Common fields ---
    /// <summary>Serial port abstraction.</summary>
    protected readonly ISerialPort _port;

    private CheckSumOptions _checksumType = CheckSumOptions.Crc;
    private int _sleepDelay = 0;
    private int _maxTicks = 100;      // 100 * 20ms = 2 seconds

    /// <inheritdoc/>
    public event EventHandler<byte[]>? FrameReceived;

    /// <inheritdoc/>
    public event EventHandler<byte[]>? RawFrameSent;

    /// <inheritdoc/>
    public event EventHandler<byte[]>? RawFrameReceived;

    /// <inheritdoc/>
    public bool IsOpen => _port.IsOpen;

    /// <summary>Gets or sets the checksum algorithm (CRC or BCC).</summary>
    public CheckSumOptions ChecksumType
    {
        get => _checksumType;
        set => _checksumType = value;
    }

    /// <summary>
    /// Sleep delay (ms) added after a NAK. Increases automatically on repeated NAKs.
    /// Helps stabilise communication with USB‑to‑serial converters.
    /// </summary>
    public int SleepDelay
    {
        get => _sleepDelay;
        set => _sleepDelay = value < 0 ? 0 : value;
    }

    /// <summary>
    /// Maximum number of polling ticks (each tick = 20 ms) to wait for ACK/NAK.
    /// Default is 100 (2 seconds). Used in auto‑detect and normal sends.
    /// </summary>
    public int MaxTicks
    {
        get => _maxTicks;
        set => _maxTicks = value > 0 ? value : 100;
    }

    /// <summary>
    /// Initialises the base DF1 transport with a custom <see cref="ISerialPort"/>.
    /// </summary>
    protected DF1BaseTransport(ISerialPort port)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
    }

    /// <summary>
    /// Initialises the base DF1 transport with standard serial port parameters.
    /// </summary>
    protected DF1BaseTransport(string portName, int baudRate, System.IO.Ports.Parity parity)
        : this(new SerialPortWrapper(portName, baudRate, parity))
    {
    }

    /// <inheritdoc/>
    public virtual void Open() => _port.Open();

    /// <inheritdoc/>
    public virtual void Close() => _port.Close();

    /// <inheritdoc/>
    public abstract void SendFrame(byte[] innerFrame);

    /// <summary>
    /// Applies DLE stuffing to a payload: every 0x10 byte is duplicated.
    /// </summary>
    protected static byte[] ApplyDleStuffing(byte[] payload)
    {
        var result = new List<byte>(payload.Length * 2);
        foreach (byte b in payload)
        {
            result.Add(b);
            if (b == DLE)
                result.Add(DLE);
        }
        return result.ToArray();
    }

    /// <summary>
    /// Removes DLE stuffing from a stuffed payload.
    /// </summary>
    protected static byte[] RemoveDleStuffing(byte[] stuffed)
    {
        var result = new List<byte>(stuffed.Length);
        for (int i = 0; i < stuffed.Length; i++)
        {
            if (stuffed[i] == DLE && i + 1 < stuffed.Length && stuffed[i + 1] == DLE)
            {
                result.Add(DLE);
                i++; // skip the stuffed duplicate
            }
            else
                result.Add(stuffed[i]);
        }
        return result.ToArray();
    }

    /// <summary>
    /// Sends a single DF1 control byte (ACK, NAK, or ENQ) prefixed with DLE.
    /// </summary>
    protected void SendControl(byte controlByte)
    {
        if (controlByte != ACK && controlByte != NAK && controlByte != ENQ)
            throw new ArgumentException("Invalid control byte.", nameof(controlByte));
        var frame = new byte[] { DLE, controlByte };
        _port.Write(frame, 0, frame.Length);
    }

    /// <summary>
    /// Builds a complete wire frame from an inner PCCC frame.
    /// Performs DLE stuffing and appends the checksum (CRC or BCC).
    /// </summary>
    protected byte[] BuildWireFrame(byte[] innerFrame)
    {
        // 1. DLE stuffing
        byte[] stuffed = ApplyDleStuffing(innerFrame);

        // 2. Calculate checksum over the UNSTUFFED inner frame
        ushort checksum = MessageDecoder.CalculateChecksum(innerFrame, _checksumType);
        int csLen = (_checksumType == CheckSumOptions.Crc) ? 2 : 1;

        // 3. Build: DLE STX + stuffed + DLE ETX + checksum
        byte[] frame = new byte[2 + stuffed.Length + 2 + csLen];
        int idx = 0;
        frame[idx++] = DLE;
        frame[idx++] = STX;
        Array.Copy(stuffed, 0, frame, idx, stuffed.Length);
        idx += stuffed.Length;
        frame[idx++] = DLE;
        frame[idx++] = ETX;
        frame[idx++] = (byte)(checksum & 0xFF);
        if (csLen == 2)
            frame[idx++] = (byte)((checksum >> 8) & 0xFF);
        return frame;
    }

    /// <summary>Raises the <see cref="FrameReceived"/> event.</summary>
    protected virtual void OnFrameReceived(byte[] innerFrame)
    {
        FrameReceived?.Invoke(this, innerFrame);
    }

    /// <summary>Raises the <see cref="RawFrameSent"/> event.</summary>
    protected virtual void OnRawFrameSent(byte[] rawFrame)
    {
        RawFrameSent?.Invoke(this, rawFrame);
    }

    /// <summary>Raises the <see cref="RawFrameReceived"/> event.</summary>
    protected virtual void OnRawFrameReceived(byte[] rawFrame)
    {
        RawFrameReceived?.Invoke(this, rawFrame);
    }

    /// <inheritdoc/>
    public virtual void Dispose()
    {
        _port.Dispose();
    }
}