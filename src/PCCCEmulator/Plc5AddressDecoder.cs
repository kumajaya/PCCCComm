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

    /// <summary>
    /// Parses the address portion of a Word Range Read/Write payload and returns the
    /// resolved file coordinates and the index of the first data byte.
    ///
    /// Two wire formats are handled:
    ///
    ///   A) PLC-5 standard (1770-6.5.16 §7-8)
    ///      [wordOffset 2B LE] [totalTrans 2B LE — ignored]
    ///      [logical address — binary or ASCII (variable)]
    ///      [sizeWords 2B LE]
    ///
    ///   B) RSLinx flat 10-byte header (observed from DF1 capture)
    ///      [00 00] [sizeWords 1B] [00]
    ///      [fileNum 2B LE] [fileType 1B] [element 1B] [subElement 1B] [byteCount 1B]
    ///
    ///   Format is identified by inspecting the candidate mask byte at payload[4]:
    ///   a valid PLC-5 binary address mask has level count 2–4 in bits [7:4].
    ///   RSLinx flat format has fileNum_hi (0x00) there, giving level count 0.
    ///
    /// On success, <paramref name="dataStart"/> points to the first write-data byte
    /// (for Read, it points past the end of the address fields — not used).
    ///
    /// For standard format (PLC-5 logical addressing), <paramref name="rawFileType"/> is set
    /// to the wire file type code (0x00-0x0F per Table 13-1). For flat format,
    /// <paramref name="rawFileType"/> is set to 0 and the caller must resolve the actual
    /// file type from the file number (e.g., via PlcMemory.GetFileTypeForNumber).
    /// </summary>
    public static bool TryDecodeWordRangeAddress(
        byte[] payload,
        out int fileNumber, out int rawFileType,
        out int element,    out int subElement,
        out int wordOffset, out int sizeWords,
        out int dataStart, out bool isFlatFormat)
    {
        fileNumber = rawFileType = element = subElement = wordOffset = sizeWords = dataStart = 0;
        isFlatFormat = false;
        if (payload == null || payload.Length < 8) return false;

        // Discriminate by the candidate mask byte at payload[4].
        // Standard format: payload[4] is the binary address mask byte, level count 2–4.
        // RSLinx flat:     payload[4] is fileNum_hi (0x00), level count = 0.
        // ASCII marker:    payload[4] == 0x00 && payload[5] == 0x24 ('$').
        int levelCount = (payload[4] >> 4) & 0x0F;
        bool isStandard = (levelCount >= 2 && levelCount <= 4)
                          || (payload[4] == 0x00 && payload.Length > 5 && payload[5] == 0x24);

        if (isStandard)
        {
            // --- Format A: PLC-5 standard ---
            if (payload.Length < 9) return false;

            int idx = 0;
            wordOffset = payload[idx] | (payload[idx + 1] << 8); idx += 2;
            idx += 2; // skip totalTrans — not used

            // Decode the logical address (binary or ASCII) into components
            bool ok = (payload[idx] == 0x00 && idx + 1 < payload.Length && payload[idx + 1] == 0x24)
                ? TryParseAsciiAddress(payload, ref idx,
                      out fileNumber, out rawFileType, out element, out subElement)
                : Decode(payload, ref idx,
                      out fileNumber, out rawFileType, out element, out subElement);
            if (!ok) return false;

            if (idx + 2 > payload.Length) return false;
            sizeWords = payload[idx] | (payload[idx + 1] << 8); idx += 2;
            dataStart = idx;
            isFlatFormat = false;
        }
        else
        {
            // --- Format B: RSLinx flat 10-byte header ---
            // [00 00] [sizeWords 1B] [00] [fileNum 2B LE] [fileType 1B] [element 1B] [sub 1B] [byteCount 1B]
            if (payload.Length < 10) return false;

            wordOffset = 0;
            sizeWords  = payload[2];
            // payload[3]    = 0x00 padding
            // payload[4..5] = 0x0F 0x00 (unknown constant, ignored)
            fileNumber  = payload[6];
            element     = payload[7];
            subElement  = payload[8];
            // rawFileType not present in flat format; set to 0 and caller must resolve.
            rawFileType = 0;
            dataStart = 10;
            isFlatFormat = true;
            Logger.Info(null, $"WR flat format: file={fileNumber} elem={element} words={sizeWords}");
        }

        return sizeWords > 0;
    }

    /// <summary>
    /// Translates a PLC-5 wire file type code (1770-6.5.16 Table 13-1) to the
    /// SLC 500 / DF1 file type code used internally by PlcMemory.
    /// Returns the input unchanged if it is already an SLC 500 code (>= 0x80)
    /// or has no known PLC-5 equivalent.
    ///
    /// PLC-5 → SLC 500 mapping:
    ///   0x00 O  → 0x8B    0x01 I  → 0x8C    0x02 S  → 0x84    0x03 B  → 0x85
    ///   0x04 T  → 0x86    0x05 C  → 0x87    0x06 R  → 0x88    0x07 N  → 0x89
    ///   0x08 F  → 0x8A    0x09 D  → 0x89*   0x0A ST → 0x8D
    ///   (*BCD has no direct SLC equivalent; mapped to Integer as closest fit)
    /// </summary>
    public static int Plc5ToSlcFileType(int t) => t switch
    {
        0x00 => 0x8B,   // O — Output image
        0x01 => 0x8C,   // I — Input image
        0x02 => 0x84,   // S — Status
        0x03 => 0x85,   // B — Bit
        0x04 => 0x86,   // T — Timer        (6 bytes/elem)
        0x05 => 0x87,   // C — Counter      (6 bytes/elem)
        0x06 => 0x88,   // R — Control      (6 bytes/elem)
        0x07 => 0x89,   // N — Integer      (2 bytes/elem)
        0x08 => 0x8A,   // F — Float        (4 bytes/elem)
        0x09 => 0x89,   // D — BCD (no SLC equivalent, map to Integer)
        0x0A => 0x8D,   // ST — String      (84 bytes/elem)
        _    => t       // already SLC 500 code (>= 0x80) or unrecognised — pass through
    };
}
