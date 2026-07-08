// SPDX-License-Identifier: GPL-3.0-or-later
// 
// PCCCEmulator - PCCC Engine and Transports for .NET
// Copyright (c) 2026 Ketut Kumajaya
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
/// Two binary address formats are supported:
///
/// 1. Legacy SLC/DF1 format (mask bits [7:4] = level count, bit [3] = sub‑element flag):
///    Byte 0 — mask: bits [7:4] = level count (2–4), bit [3] = hasSubElement
///    Each level: 1 byte if value < 255; 3 bytes (0xFF, lo, hi) if value >= 255
///    Level order: [0]=fileNumber, [1]=fileType, [2]=element, [3]=subElement
///
/// 2. PLC‑5‑compatible format (used by PCCCComm.Plc5Handler.EncodePlc5LogicalAddress
///    and by RSLinx for Word Range Read/Write):
///    Byte 0 — mask bit flags:
///       bit 0 (0x01) = Level 1 (Data Table Area) — default 0, rarely used
///       bit 1 (0x02) = Level 2 (File Number)     — default 1 if not present
///       bit 2 (0x04) = Level 3 (Element)         — default 0 if not present
///       bit 3 (0x08) = Level 4 (Sub‑Element)     — default 0 if not present
///    There is NO separate "file type" level; the file type is determined
///    by the file number and the context of the command.
///
/// ASCII format (same for both):
///    Byte 0 = 0x00, Byte 1 = '$' (0x24), then ASCII string, then 0x00 terminator
///    String format: "N7:0", "F8:2", "T4:1/ACC", etc.
/// </summary>
public static class Plc5AddressDecoder
{
    /// <summary>
    /// Decodes a PLC-5 logical binary address from the buffer, advancing <paramref name="offset"/>.
    /// Supports both legacy (level‑count in high nibble) and PLC‑5‑compatible (bit‑flag) masks.
    ///
    /// For legacy format, fileType is set to the value from level 1.
    /// For bit‑flag format, fileType is set to 0 (since it is not encoded).
    /// </summary>
    public static bool Decode(byte[] data, ref int offset,
        out int fileNumber, out int fileType, out int element, out int subElement)
    {
        fileNumber = 1;  // default file number per spec
        fileType = 0;    // default (not encoded in bit‑flag format)
        element = 0;
        subElement = 0;

        if (offset >= data.Length) return false;

        byte mask = data[offset++];

        // --- Detect which format is being used ---
        // Legacy format: high nibble contains level count (2–4).
        int levelCount = (mask >> 4) & 0x0F;
        bool isLegacy = (levelCount >= 2 && levelCount <= 4);

        if (isLegacy)
        {
            // Legacy SLC/DF1 format: read levels sequentially.
            bool hasSub = (mask & 0x08) != 0;
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

        // --- PLC‑5‑compatible bit‑flag format ---
        // Mask bits:
        //   bit 0 (0x01) = Level 1 (Data Table Area) — default 0, ignored
        //   bit 1 (0x02) = Level 2 (File Number)     — default 1
        //   bit 2 (0x04) = Level 3 (Element)         — default 0
        //   bit 3 (0x08) = Level 4 (Sub‑Element)     — default 0
        if ((mask & 0x06) == 0) // at least file number or element must be present
        {
            // If neither bit 1 nor bit 2 is set, this is not a valid address mask.
            return false;
        }

        // Level 1 (bit 0) – Data Table Area (usually 0)
        if ((mask & 0x01) != 0)
        {
            if (offset >= data.Length) return false;
            int _ = ReadLevelValue(data, ref offset);
            // ignore the value; it is almost always 0
        }

        // Level 2 (bit 1) – File Number
        if ((mask & 0x02) != 0)
        {
            if (offset >= data.Length) return false;
            fileNumber = ReadLevelValue(data, ref offset);
            if (fileNumber < 0 || fileNumber > 255) return false;
        }
        // else fileNumber remains 1 (default)

        // Level 3 (bit 2) – Element
        if ((mask & 0x04) != 0)
        {
            if (offset >= data.Length) return false;
            element = ReadLevelValue(data, ref offset);
            if (element < 0 || element > 65535) return false;
        }
        // else element remains 0

        // Level 4 (bit 3) – Sub‑Element
        if ((mask & 0x08) != 0)
        {
            if (offset >= data.Length) return false;
            subElement = ReadLevelValue(data, ref offset);
            if (subElement < 0 || subElement > 65535) return false;
        }
        // else subElement remains 0

        // fileType is not encoded in the bit‑flag format; leave it as 0.
        return true;
    }

    /// <summary>
    /// Reads a level value from the buffer. If the byte is 0xFF, reads the next
    /// two bytes as a little‑endian 16‑bit value. Otherwise returns the byte.
    /// </summary>
    private static int ReadLevelValue(byte[] data, ref int offset)
    {
        if (data[offset] != 0xFF)
        {
            return data[offset++];
        }
        else
        {
            if (offset + 3 > data.Length) return 0;
            offset++; // skip 0xFF
            int value = data[offset] | (data[offset + 1] << 8);
            offset += 2;
            return value;
        }
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
        var m = Regex.Match(addr, @"^([A-Z]+)(\d*):([0-9A-Fa-f]+)(?:[/\.](\w+))?$");
        if (!m.Success) return false;

        string filePrefix = m.Groups[1].Value;
        string fileNumStr = m.Groups[2].Value;
        string elementStr = m.Groups[3].Value;
        string subStr     = m.Groups[4].Success ? m.Groups[4].Value : "";

        if (string.IsNullOrEmpty(fileNumStr))
        {
            if (filePrefix == "O")      fileNumber = 0;
            else if (filePrefix == "I") fileNumber = 1;
            else if (filePrefix == "S") fileNumber = 2;
            else return false;
        }
        else
        {
            if (!int.TryParse(fileNumStr, out fileNumber)) return false;
        }

        if (filePrefix == "O" || filePrefix == "I")
        {
            try { element = Convert.ToInt32(elementStr, 8); }
            catch { return false; }
        }
        else
        {
            if (!int.TryParse(elementStr, out element)) return false;
        }

        fileType = filePrefix switch
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

        subElement = subStr switch
        {
            "PRE" => 1, "ACC" => 2,
            "EN"  => 15, "TT" => 14, "DN" => 13,
            "CU"  => 15, "CD" => 14, "OV" => 12, "UN" => 11, "UA" => 10,
            _ => int.TryParse(subStr, out int bit) ? bit : 0
        };

        return true;
    }

    /// <summary>
    /// Parses the address portion of a Word Range Read/Write payload.
    /// Supports two formats used by RSLinx and PLC-5:
    ///
    /// 1. RSLinx flat 10-byte header (used for data file access):
    ///    [00 00][sizeWords 1B][00][0F 00][fileNum 1B][element 1B][subElement 1B][byteCount 1B]
    ///
    /// 2. PLC-5 standard format (1770-6.5.16 §7-8) — binary or ASCII address.
    /// </summary>
    public static bool TryDecodeWordRangeReadAddress(
        byte[] payload,
        out int fileNumber, out int rawFileType,
        out int element,    out int subElement,
        out int wordOffset, out int sizeWords,
        out int dataStart, out bool isFlatFormat)
    {
        fileNumber = rawFileType = element = subElement = wordOffset = sizeWords = dataStart = 0;
        isFlatFormat = false;
        if (payload == null || payload.Length < 8) return false;

        // ─── Format 1: RSLinx flat 10-byte header ──────────────────────────────
        if (payload.Length >= 10 && payload[4] == 0x0F && payload[5] == 0x00)
        {
            wordOffset = 0;
            sizeWords  = payload[2];
            fileNumber = payload[6];
            element    = payload[7];
            subElement = payload[8];
            rawFileType = 0;
            dataStart = 10;
            isFlatFormat = true;
            return sizeWords > 0;
        }

        // ─── Format 2: PLC-5 standard ──────────────────────────────────────────
        // Check for ASCII marker.
        bool isAscii = (payload[4] == 0x00 && payload.Length > 5 && payload[5] == 0x24);

        // Check for binary mask: legacy (high nibble 2–4) or PLC‑5‑compatible (bit‑flag).
        byte mask = payload[4];
        bool isLegacyMask = ((mask >> 4) >= 2 && (mask >> 4) <= 4);
        bool isPlc5Mask   = ((mask & 0xF0) == 0 && (mask & 0x06) != 0);
        bool isStandard   = isAscii || isLegacyMask || isPlc5Mask;

        if (!isStandard) return false;

        int idx = 0;
        if (idx + 4 > payload.Length) return false;
        wordOffset = payload[idx] | (payload[idx + 1] << 8); idx += 2;
        idx += 2; // skip totalTrans

        bool ok = isAscii
            ? TryParseAsciiAddress(payload, ref idx,
                out fileNumber, out rawFileType, out element, out subElement)
            : Decode(payload, ref idx,
                out fileNumber, out rawFileType, out element, out subElement);
        if (!ok) return false;

        // Size is a single BYTE-count byte (max 244 per AB Pub. 1770-6.5.16), not a 2-byte
        // word count — matching what Plc5Handler.CreateWordRangeReadRequest actually puts on
        // the wire (and the same 1-byte convention EncodeReadBody uses for the SLC-style
        // Typed Read). The previous 2-byte read here required a 9th byte that a minimal
        // 2-level address (mask+fileNumber+element, 3 bytes) + 1-byte Size never sends,
        // making every PLC-5-format Word Range Read (e.g. "N7:0") fail before parsing.
        if (idx + 1 > payload.Length) return false;
        int byteCount = payload[idx]; idx += 1;
        sizeWords = byteCount / 2;
        dataStart = idx;
        isFlatFormat = false;
        return sizeWords > 0;
    }

    /// <summary>
    /// Parses the address portion of a Word Range Write payload (FNC 0x00).
    /// Format: [PktOff 2B][TotTrans 2B][address var][data]
    /// There is NO separate Size field; data starts immediately after the address.
    /// </summary>
    public static bool TryDecodeWordRangeWriteAddress(
        byte[] payload,
        out int fileNumber, out int rawFileType,
        out int element,    out int subElement,
        out int wordOffset, out int dataStart)
    {
        fileNumber = rawFileType = element = subElement = wordOffset = dataStart = 0;
        if (payload == null || payload.Length < 8) return false;

        int idx = 0;
        wordOffset = payload[idx] | (payload[idx + 1] << 8); idx += 2;
        idx += 2; // skip totalTrans

        // Check for ASCII marker.
        bool isAscii = (payload[idx] == 0x00 && payload.Length > idx + 1 && payload[idx + 1] == 0x24);

        // Check for binary mask: legacy (high nibble 2–4) or PLC‑5‑compatible (bit‑flag).
        byte mask = payload[idx];
        bool isLegacyMask = ((mask >> 4) >= 2 && (mask >> 4) <= 4);
        bool isPlc5Mask   = ((mask & 0xF0) == 0 && (mask & 0x06) != 0);
        bool isStandard   = isAscii || isLegacyMask || isPlc5Mask;

        if (!isStandard || payload.Length < idx + 1) return false;

        bool ok = isAscii
            ? TryParseAsciiAddress(payload, ref idx,
                out fileNumber, out rawFileType, out element, out subElement)
            : Decode(payload, ref idx,
                out fileNumber, out rawFileType, out element, out subElement);
        if (!ok) return false;

        // For Write, data starts at idx (no Size field)
        dataStart = idx;
        return true;
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
