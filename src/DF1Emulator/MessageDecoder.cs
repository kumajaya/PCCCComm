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

using System.Runtime.CompilerServices;

/// <summary>
/// Helper methods for checksum calculation, DLE stuffing/unstuffing,
/// and DF1 error code decoding.
///
/// HIGH-PERFORMANCE NOTES:
/// - All methods use ReadOnlySpan&lt;byte&gt; for zero-copy operations
/// - CRC uses CRC-16 table lookup (init=0x0000, polynomial 0xA001),
///   identical to the VB original. ETX byte (0x03) is included in the
///   CRC calculation as per AB specification.
/// - BCC uses two's complement of sum, as per VB original.
/// - RemoveDleStuffing writes to destination Span to avoid allocations
/// - ApplyDleStuffing returns length and writes to destination Span
/// </summary>
public static class MessageDecoder
{
    // ─── CRC-16 lookup table (matches VB aCRC16Table) ────────────────────────
    // 256 entries pre-computed for fast table lookup CRC calculation
    private static readonly ushort[] CRC16Table =
    {
        0x0000, 0xC0C1, 0xC181, 0x0140, 0xC301, 0x03C0, 0x0280, 0xC241,
        0xC601, 0x06C0, 0x0780, 0xC741, 0x0500, 0xC5C1, 0xC481, 0x0440,
        0xCC01, 0x0CC0, 0x0D80, 0xCD41, 0x0F00, 0xCFC1, 0xCE81, 0x0E40,
        0x0A00, 0xCAC1, 0xCB81, 0x0B40, 0xC901, 0x09C0, 0x0880, 0xC841,
        0xD801, 0x18C0, 0x1980, 0xD941, 0x1B00, 0xDBC1, 0xDA81, 0x1A40,
        0x1E00, 0xDEC1, 0xDF81, 0x1F40, 0xDD01, 0x1DC0, 0x1C80, 0xDC41,
        0x1400, 0xD4C1, 0xD581, 0x1540, 0xD701, 0x17C0, 0x1680, 0xD641,
        0xD201, 0x12C0, 0x1380, 0xD341, 0x1100, 0xD1C1, 0xD081, 0x1040,
        0xF001, 0x30C0, 0x3180, 0xF141, 0x3300, 0xF3C1, 0xF281, 0x3240,
        0x3600, 0xF6C1, 0xF781, 0x3740, 0xF501, 0x35C0, 0x3480, 0xF441,
        0x3C00, 0xFCC1, 0xFD81, 0x3D40, 0xFF01, 0x3FC0, 0x3E80, 0xFE41,
        0xFA01, 0x3AC0, 0x3B80, 0xFB41, 0x3900, 0xF9C1, 0xF881, 0x3840,
        0x2800, 0xE8C1, 0xE981, 0x2940, 0xEB01, 0x2BC0, 0x2A80, 0xEA41,
        0xEE01, 0x2EC0, 0x2F80, 0xEF41, 0x2D00, 0xEDC1, 0xEC81, 0x2C40,
        0xE401, 0x24C0, 0x2580, 0xE541, 0x2700, 0xE7C1, 0xE681, 0x2640,
        0x2200, 0xE2C1, 0xE381, 0x2340, 0xE101, 0x21C0, 0x2080, 0xE041,
        0xA001, 0x60C0, 0x6180, 0xA141, 0x6300, 0xA3C1, 0xA281, 0x6240,
        0x6600, 0xA6C1, 0xA781, 0x6740, 0xA501, 0x65C0, 0x6480, 0xA441,
        0x6C00, 0xACC1, 0xAD81, 0x6D40, 0xAF01, 0x6FC0, 0x6E80, 0xAE41,
        0xAA01, 0x6AC0, 0x6B80, 0xAB41, 0x6900, 0xA9C1, 0xA881, 0x6840,
        0x7800, 0xB8C1, 0xB981, 0x7940, 0xBB01, 0x7BC0, 0x7A80, 0xBA41,
        0xBE01, 0x7EC0, 0x7F80, 0xBF41, 0x7D00, 0xBDC1, 0xBC81, 0x7C40,
        0xB401, 0x74C0, 0x7580, 0xB541, 0x7700, 0xB7C1, 0xB681, 0x7640,
        0x7200, 0xB2C1, 0xB381, 0x7340, 0xB101, 0x71C0, 0x7080, 0xB041,
        0x5000, 0x90C1, 0x9181, 0x5140, 0x9301, 0x53C0, 0x5280, 0x9241,
        0x9601, 0x56C0, 0x5780, 0x9741, 0x5500, 0x95C1, 0x9481, 0x5440,
        0x9C01, 0x5CC0, 0x5D80, 0x9D41, 0x5F00, 0x9FC1, 0x9E81, 0x5E40,
        0x5A00, 0x9AC1, 0x9B81, 0x5B40, 0x9901, 0x59C0, 0x5880, 0x9841,
        0x8801, 0x48C0, 0x4980, 0x8941, 0x4B00, 0x8BC1, 0x8A81, 0x4A40,
        0x4E00, 0x8EC1, 0x8F81, 0x4F40, 0x8D01, 0x4DC0, 0x4C80, 0x8C41,
        0x4400, 0x84C1, 0x8581, 0x4540, 0x8701, 0x47C0, 0x4680, 0x8641,
        0x8201, 0x42C0, 0x4380, 0x8341, 0x4100, 0x81C1, 0x8081, 0x4040
    };

    // ─── Checksum ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Calculates CRC-16 or BCC checksum over the given data.
    /// For CRC, the ETX byte (0x03) is appended as per AB spec.
    /// 
    /// HIGH-PERFORMANCE: Uses ReadOnlySpan to avoid allocations.
    /// Caller can pass any contiguous memory (array, stackalloc, native).
    /// </summary>
    /// <param name="data">The payload data to checksum (without DLE stuffing)</param>
    /// <param name="option">CRC (default) or BCC checksum algorithm</param>
    /// <returns>16-bit checksum (BCC returns in low byte, high byte = 0)</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort CalculateChecksum(ReadOnlySpan<byte> data, CheckSumOptions option)
    {
        if (option == CheckSumOptions.Crc)
        {
            // CRC-16 with polynomial 0xA001, init 0x0000 (AB DF1 specification)
            ushort crc = 0x0000;
            
            // Process all data bytes
            for (int i = 0; i < data.Length; i++)
            {
                byte tableIdx = (byte)((crc & 0xFF) ^ data[i]);
                crc = (ushort)((crc >> 8) ^ CRC16Table[tableIdx]);
            }
            
            // Include ETX (0x03) as per AB DF1 specification
            // ETX is NOT part of the unstuffed payload; it's added during CRC calc
            byte etx = 0x03;
            byte finalIdx = (byte)((crc & 0xFF) ^ etx);
            crc = (ushort)((crc >> 8) ^ CRC16Table[finalIdx]);
            
            return crc;
        }
        else
        {
            // BCC: Two's complement of sum (matches VB CalculateBCC)
            int sum = 0;
            for (int i = 0; i < data.Length; i++)
            {
                sum += data[i];
            }
            sum = sum & 0xFF;
            return (ushort)((0x100 - sum) & 0xFF);
        }
    }

    // ─── DLE Stuffing (Zero-Allocation) ──────────────────────────────────────

    /// <summary>
    /// Apply DLE stuffing: every 0x10 in payload becomes 0x10 0x10.
    /// 
    /// HIGH-PERFORMANCE: Writes to destination Span instead of allocating new array.
    /// Returns the actual number of bytes written to destination.
    /// </summary>
    /// <param name="source">Unstuffed payload (raw bytes)</param>
    /// <param name="destination">Pre-allocated buffer (must be at least source.Length * 2)</param>
    /// <returns>Number of bytes written to destination</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ApplyDleStuffing(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int destPos = 0;
        
        for (int i = 0; i < source.Length; i++)
        {
            byte b = source[i];
            destination[destPos++] = b;
            
            // DLE byte (0x10) must be doubled
            if (b == 0x10)
            {
                destination[destPos++] = 0x10;
            }
        }
        
        return destPos;
    }

    /// <summary>
    /// Remove DLE stuffing: 0x10 0x10 → single 0x10.
    /// 
    /// HIGH-PERFORMANCE: Writes to destination Span, zero allocation.
    /// Returns the actual number of bytes written to destination.
    /// </summary>
    /// <param name="source">Stuffed payload (with doubled 0x10 bytes)</param>
    /// <param name="destination">Pre-allocated buffer (at least source.Length)</param>
    /// <returns>Number of bytes written to destination (always ≤ source.Length)</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int RemoveDleStuffing(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int destPos = 0;
        
        for (int i = 0; i < source.Length; i++)
        {
            byte b = source[i];
            destination[destPos++] = b;
            
            // If this is a DLE byte and next byte is also DLE, skip the next byte
            if (b == 0x10 && i + 1 < source.Length && source[i + 1] == 0x10)
            {
                i++; // Skip the stuffed duplicate
            }
        }
        
        return destPos;
    }

    // ─── DecodeMessage ───────────────────────────────────────────────────────

    /// <summary>
    /// Converts a DF1 message/status code to a human-readable string.
    /// Ref: AB Publication 1770-6.5.16, page 8-3.
    /// EXT STS codes have 256 added to distinguish from standard STS.
    /// </summary>
    public static string DecodeMessage(int msgNumber)
    {
        switch (msgNumber)
        {
            case 0:   return "";
            case -2:  return "Not Acknowledged (NAK)";
            case -3:  return "No Response, Check COM Settings";
            case -4:  return "Unknown Message from DataLink Layer";
            case -5:  return "Invalid Address";
            case -6:  return "Could Not Open Com Port";
            case -7:  return "No data specified to data link layer";
            case -8:  return "No data returned from PLC";
            case -20: return "No Data Returned";
            case -21: return "Received Message NAKd from invalid checksum";

            // Standard STS codes from PLC
            case 16:  return "Illegal Command or Format, Address may not exist or not enough elements in data file";
            case 32:  return "PLC Has a Problem and Will Not Communicate";
            case 48:  return "Remote Node Host is Missing, Disconnected, or Shut Down";
            case 64:  return "Host Could Not Complete Function Due To Hardware Fault";
            case 80:  return "Addressing problem or Memory Protect Rungs";
            case 96:  return "Function not allowed due to command protection selection";
            case 112: return "Processor is in Program mode";
            case 128: return "Compatibility mode file missing or communication zone problem";
            case 144: return "Remote node cannot buffer command";
            case 240: return "Error code in EXT STS Byte";

            // EXT STS codes (256 added to code to distinguish)
            case 257: return "A field has an illegal value";
            case 258: return "Less levels specified in address than minimum for any address";
            case 259: return "More levels specified in address than system supports";
            case 260: return "Symbol not found";
            case 261: return "Symbol is of improper format";
            case 262: return "Address doesn't point to something usable";
            case 263: return "File is wrong size";
            case 264: return "Cannot complete request, situation has changed since the start of the command";
            case 265: return "Data or file is too large";
            case 266: return "Transaction size plus word address is too large";
            case 267: return "Access denied, improper privilege";
            case 268: return "Condition cannot be generated - resource is not available";
            case 269: return "Condition already exists - resource is already available";
            case 270: return "Command cannot be executed";

            default: return "Unknown Message - " + msgNumber;
        }
    }
}

/// <summary>
/// Checksum algorithm selection for DF1 frames.
/// CRC (default) is more reliable and standard for modern AB PLCs.
/// BCC (XOR) is legacy but still supported by some older systems.
/// </summary>
public enum CheckSumOptions
{
    Crc = 0,
    Bcc = 1
}
