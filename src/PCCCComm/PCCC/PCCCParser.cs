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

using System.Text.RegularExpressions;

namespace PCCCComm.Pccc;

/// <summary>
/// Parses Allen-Bradley PLC address strings into a DataAddress struct.
/// Supported formats (ref AB Publication 1770-6.5.16, page 7-18):
///   N7:0, B3:0/5, T4:1.ACC, C5:0.DN, F8:0, ST9:0, I:0, O:0, S:1
/// </summary>
public static partial class PCCCParser
{
    // Regex patterns - compiled for performance (.NET Standard 2.0 compatible)
    private static readonly Regex RE1 = new Regex(
        @"^\s*(?<FileType>([SBCTRNFAIOL])|(ST)|(MG)|(PD)|(PLS))(?<FileNumber>\d{1,3}):(?<ElementNumber>\d{1,3})(/(?<BitNumber>\d{1,4}))?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RE2 = new Regex(
        @"^\s*(?<FileType>[BN])(?<FileNumber>\d{1,3})(/(?<BitNumber>\d{1,4}))\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RE3 = new Regex(
        @"^\s*(?<FileType>[CT])(?<FileNumber>\d{1,3}):(?<ElementNumber>\d{1,3})[.](?<SubElement>(ACC|PRE|EN|DN|TT|CU|CD|OV|UN|UA))\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RE4 = new Regex(
        @"^\s*(?<FileType>([IOS])):(?<ElementNumber>\d{1,3})([.](?<SubElement>[0-7]))?(/(?<BitNumber>\d{1,4}))?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Parses an AB address string. Returns FileType=0 if the address is invalid.
    /// </summary>
    public static DataAddress Parse(string dataAddress)
    {
        var result = new DataAddress
        {
            FileType  = 0,
            BitNumber = 99  // 99 = no bit-level requested
        };

        if (string.IsNullOrWhiteSpace(dataAddress))
            return result;

        Match mc = RE1.Match(dataAddress);
        if (!mc.Success) mc = RE2.Match(dataAddress);
        if (!mc.Success) mc = RE3.Match(dataAddress);
        if (!mc.Success) mc = RE4.Match(dataAddress);
        if (!mc.Success) return result;

        // ── FileNumber ────────────────────────────────────────────────────────
        if (mc.Groups["FileNumber"].Length == 0)
        {
            // I/O/S addresses without an explicit file number
            string addr = dataAddress.ToUpperInvariant();
            if      (addr.Contains("I")) result.FileNumber = 1;
            else if (addr.Contains("O")) result.FileNumber = 0;
            else                         result.FileNumber = 2;
        }
        else
        {
            result.FileNumber = int.Parse(mc.Groups["FileNumber"].Value);
        }

        // BitNumber
        if (mc.Groups["BitNumber"].Length > 0)
            result.BitNumber = int.Parse(mc.Groups["BitNumber"].Value);

        // Element
        if (mc.Groups["ElementNumber"].Length > 0)
        {
            result.Element = int.Parse(mc.Groups["ElementNumber"].Value);
        }
        else
        {
            // RE2 path: address like B3/20 — bit number encodes word and bit position.
            // Upper bits select the word element; lower 4 bits are the bit within that word.
            result.Element   = result.BitNumber >> 4;
            result.BitNumber = result.BitNumber & 0xF;
        }

        // SubElement
        if (mc.Groups["SubElement"].Length > 0)
        {
            switch (mc.Groups["SubElement"].Value.ToUpperInvariant())
            {
                case "PRE": result.SubElement = 1;  break;
                case "ACC": result.SubElement = 2;  break;
                // Timer status bits
                case "EN":  result.SubElement = 15; break;
                case "TT":  result.SubElement = 14; break;
                case "DN":  result.SubElement = 13; break;
                // Counter status bits
                case "CU":  result.SubElement = 15; break;
                case "CD":  result.SubElement = 14; break;
                case "OV":  result.SubElement = 12; break;
                case "UN":  result.SubElement = 11; break;
                case "UA":  result.SubElement = 10; break;
                default:
                    if (int.TryParse(mc.Groups["SubElement"].Value, out int se))
                        result.SubElement = se;
                    break;
            }
        }

        // ── Collapse status-bit sub-elements to bit-level access ──────────────
        // SubElement values > 4 refer to status-word bit positions (e.g. T4:0.EN
        // maps to bit 15 of the status word).  Convert to BitNumber so the wire
        // protocol uses the bit-masked write function (0xAB).
        //
        // BUG FIX: original code did:
        //   result.SubElement = 0;
        //   result.BitNumber  = result.SubElement;   // always 0 — already zeroed!
        // The sub-element value must be captured before it is cleared.
        if (result.SubElement > 4)
        {
            int bitFromSubElement = result.SubElement;   // capture before zeroing
            result.SubElement     = 0;
            result.BitNumber      = bitFromSubElement;
        }

        // ── Translate file-type letter to numeric code ────────────────────────
        // BUG FIX: original code wrapped this block in `if (result.Element < 256)`
        // which caused the FileType assignment to be silently skipped for any
        // element >= 256, leaving FileType = 0 (invalid address).  The element
        // range has no bearing on the type mapping, so the guard is removed.
        result.BytesPerElements = 2; // default; overridden below where needed
        switch (mc.Groups["FileType"].Value.ToUpperInvariant())
        {
            case "N":   result.FileType = 0x89; break;
            case "B":   result.FileType = 0x85; break;
            case "T":   result.FileType = 0x86; break;
            case "C":   result.FileType = 0x87; break;
            case "F":   result.FileType = 0x8A; result.BytesPerElements = 4;  break;
            case "S":   result.FileType = 0x84; break;
            case "ST":  result.FileType = 0x8D; result.BytesPerElements = 84; break;
            case "A":   result.FileType = 0x8E; break;
            case "R":   result.FileType = 0x88; break;
            case "O":   result.FileType = 0x8B; break;
            case "I":   result.FileType = 0x8C; break;
            case "L":   result.FileType = 0x91; result.BytesPerElements = 4;  break;
            case "MG":  result.FileType = 0x92; result.BytesPerElements = 50; break;
            case "PD":  result.FileType = 0x93; result.BytesPerElements = 46; break;
            case "PLS": result.FileType = 0x94; result.BytesPerElements = 12; break;
        }

        return result;
    }
}
