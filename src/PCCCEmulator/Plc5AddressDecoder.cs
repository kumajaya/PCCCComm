// SPDX-License-Identifier: GPL-3.0-or-later
// 
// PCCCEmulator - PCCC Engine and Transports for .NET
// Copyright (c) 2026 Ketut Kumajaya
// 
// Initial reference: DF1Comm.vb (Archie Jacobs); implementation substantially modified.
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

using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Decodes PLC-5 logical addresses (binary or ASCII) from a byte stream.
/// Reference: AB Publication 1770-6.5.16, Chapter 13.
///
/// BINARY ADDRESS FORMAT (mask byte + level bytes):
///   Byte 0 — mask: bits [7:4] = level count (2–4), bit [3] = hasSubElement flag
///   Each level: 1 byte if value < 255; 3 bytes (0xFF, lo, hi) if value >= 255
///   Level order: [0]=fileNumber, [1]=fileType, [2]=element, [3]=subElement
///
/// ASCII ADDRESS FORMAT (logical address string, null-terminated):
///   Byte 0 = 0x00, Byte 1 = '$' (0x24), then ASCII string, then 0x00 terminator
///   String format: "N7:0", "F8:2", "T4:1/ACC", etc.
///
/// PLC-5 FILE TYPE CODES (1770-6.5.16 Table 13-1):
///   0x00=O(Output)  0x01=I(Input)  0x02=S(Status)  0x03=B(Bit)
///   0x04=T(Timer)   0x05=C(Counter) 0x06=R(Control) 0x07=N(Integer)
///   0x08=F(Float)   0x09=D(BCD)     0x0A=ST(String) 0x0B=A(ASCII)
///   0x0C=L(Long)    0x0D=MG(Msg)    0x0E=PD(PID)    0x0F=PLS
/// </summary>
public static class Plc5AddressDecoder
{
    /// <summary>
    /// Decodes a PLC-5 logical binary address from the buffer, advancing <paramref name="offset"/>.
    /// </summary>
    public static bool Decode(byte[] data, ref int offset,
        out int fileNumber, out int fileType, out int element, out int subElement)
    {
        fileNumber = fileType = element = subElement = 0;
        if (offset >= data.Length) return false;

        byte mask       = data[offset++];
        int  levelCount = (mask >> 4) & 0x0F;
        bool hasSub     = (mask & 0x08) != 0;

        if (levelCount < 2 || levelCount > 4) return false;

        int[] levels = new int[4];
        for (int i = 0; i < levelCount; i++)
        {
            if (offset >= data.Length) return false;
            if (data[offset] == 0xFF)
            {
                if (offset + 3 > data.Length) return false;
                levels[i] = data[offset + 1] | (data[offset + 2] << 8);
                offset += 3;
            }
            else
            {
                levels[i] = data[offset++];
            }
        }

        fileNumber = levels[0];
        fileType   = levels[1];
        element    = levelCount >= 3 ? levels[2] : 0;
        subElement = hasSub && levelCount >= 4 ? levels[3] : 0;
        return true;
    }

    /// <summary>
    /// Decodes a PLC-5 logical ASCII address (0x00, '$', string, 0x00) from the buffer.
    /// Returns false if the marker bytes are absent or the string cannot be parsed.
    /// </summary>
    public static bool TryParseAsciiAddress(byte[] data, ref int offset,
        out int fileNumber, out int fileType, out int element, out int subElement)
    {
        fileNumber = fileType = element = subElement = 0;
        if (offset + 2 >= data.Length) return false;
        if (data[offset] != 0x00 || data[offset + 1] != 0x24) return false;
        offset += 2;

        int start = offset;
        while (offset < data.Length && data[offset] != 0x00) offset++;
        if (offset >= data.Length) return false;

        string addr = Encoding.ASCII.GetString(data, start, offset - start);
        offset++; // skip null terminator

        return ParseAddressString(addr, out fileNumber, out fileType, out element, out subElement);
    }

    // ─── Internal ────────────────────────────────────────────────────────────

    private static bool ParseAddressString(string addr,
        out int fileNumber, out int fileType, out int element, out int subElement)
    {
        fileNumber = fileType = element = subElement = 0;
        if (string.IsNullOrEmpty(addr)) return false;

        addr = addr.ToUpperInvariant();
        var m = Regex.Match(addr, @"^([A-Z]+)(\d+):(\d+)(?:[/\.](\w+))?$");
        if (!m.Success) return false;

        if (!int.TryParse(m.Groups[2].Value, out fileNumber)) return false;
        if (!int.TryParse(m.Groups[3].Value, out element))    return false;

        // PLC-5 file type codes per 1770-6.5.16 Table 13-1
        fileType = m.Groups[1].Value switch
        {
            "O"   => 0x00,
            "I"   => 0x01,
            "S"   => 0x02,
            "B"   => 0x03,
            "T"   => 0x04,
            "C"   => 0x05,
            "R"   => 0x06,
            "N"   => 0x07,
            "F"   => 0x08,
            "D"   => 0x09,
            "ST"  => 0x0A,
            "A"   => 0x0B,
            "L"   => 0x0C,
            "MG"  => 0x0D,
            "PD"  => 0x0E,
            "PLS" => 0x0F,
            _     => 0x00
        };

        string sub = m.Groups[4].Success ? m.Groups[4].Value : "";
        subElement = sub switch
        {
            "PRE" => 1, "ACC" => 2,
            "EN"  => 15, "TT" => 14, "DN" => 13,
            "CU"  => 15, "CD" => 14, "OV" => 12, "UN" => 11, "UA" => 10,
            _ => int.TryParse(sub, out int bit) ? bit : 0
        };

        return true;
    }
}
