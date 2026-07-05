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
/// Supported formats (ref AB Publication 1770-6.5.16, page 7-18, and AVEVA
/// Plant SCADA tag addressing references for MicroLogix/SLC 500/PLC-5):
///   N7:0, B3:0/5, B:0/5, T4:1.ACC, T4:1/EN, C5:0.DN, C5:0/UN, R6:0.LEN,
///   R6:0/EN, F8:0, ST9:0, I:0, O:0, S:1
///
/// Deliberately NOT supported (out of scope until a concrete need arises —
/// see project history for the AVEVA Plant SCADA compatibility review that
/// established this boundary):
///   - PLC-5 native octal I/O addressing (O:O/o, I:O/o) — MicroLogix/SLC 500
///     I/O addressing (Of:e.s, decimal) IS supported; only the PLC-5-specific
///     octal element/bit notation is not, and could not be verified against
///     AB Publication 1770-6.5.16 (which documents wire-level logical binary
///     addressing, not this text convention, so it neither confirms nor
///     contradicts AVEVA's octal claim for PLC-5).
///   - PID (PD) dot-notation sub-elements/bits (e.g. PD12:0.SP, PD12:0/EN)
///   - Sequential Function Chart (SC) and Block Transfer (BT) file types
///   - BCD (D) file type
///   - Control (R) raw numeric sub-element form (R6:1.0) — only the named
///     forms (R6:1.LEN, R6:1.POS) and named bit mnemonics (R6:2/EN etc.) are
///     supported
///   - String sub-addressing (ST9:1.LEN, ST9:1.DATA[n]) — only whole-string
///     addressing (ST9:1) is supported, matching how DrvSigPccc.Logic reads
///     strings (whole element via chunking, not per-character)
/// </summary>
public static partial class PCCCParser
{
    // Regex patterns - source-generated at build time via [GeneratedRegex] (net7.0+)
    [GeneratedRegex(@"^\s*(?<FileType>([SBCTRNFAIOL])|(ST)|(MG)|(PD)|(PLS))(?<FileNumber>\d{1,3}):(?<ElementNumber>\d{1,3})(/(?<BitNumber>\d{1,4}))?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex RE1();

    [GeneratedRegex(@"^\s*(?<FileType>[BN])(?<FileNumber>\d{1,3})(/(?<BitNumber>\d{1,4}))\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex RE2();

    // Control (R) sub-elements only (LEN/POS, dot-notation, word-level) — per
    // AVEVA Plant SCADA: "Rf:e.LEN", "Rf:e.POS". Bit-status mnemonics (EN, EU,
    // DN, EM, ER, UL, IN, FD) use slash notation instead ("Rf:e/EN") and are
    // handled by RE5 below, not here.
    [GeneratedRegex(@"^\s*(?<FileType>[RCT])(?<FileNumber>\d{1,3}):(?<ElementNumber>\d{1,3})[.](?<SubElement>(ACC|PRE|LEN|POS|EN|DN|TT|CU|CD|OV|UN|UA))\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex RE3();

    // I/O, Status, and Bit addressing without an explicit file number:
    // O:e.s, Of:e.s, O:e.s/b, Of:e.s/b (I/S equivalents), and B:e, Bf:e, B:e/b, Bf:e/b.
    // f (file number) is optional; e = slot (element) for I/O, or element for B;
    // s = word within slot (sub-element, 0-255) — not used by B; b = terminal/bit
    // number (0-15). RE1 already handles the explicit-file-number form for all of
    // these types (e.g. "O0:1.3", "B3:4") — this pattern only covers the omitted-
    // file-number form, tried after RE1 fails to match.
    // Previously the file number was not permitted at all for I/O/S (only "O:e"
    // matched, not "O0:e"), and the word number was capped at a single digit
    // (0-7) instead of the full 0-255 range. B was not included here at all,
    // so "B:4" (Bit file, default file number 3, no explicit "3") failed to
    // parse even though AVEVA Plant SCADA documents it as valid — only the
    // explicit "B3:4" form worked.
    [GeneratedRegex(@"^\s*(?<FileType>([IOSB]))(?<FileNumber>\d{1,3})?:(?<ElementNumber>\d{1,3})([.](?<SubElement>\d{1,3}))?(/(?<BitNumber>\d{1,4}))?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex RE4();

    // Named bit-status mnemonics using slash notation, per AVEVA Plant SCADA:
    // "Tf:e/EN", "Cf:e/UN", "Rf:e/EN", etc. Purely additive — an alternate,
    // AVEVA-matching spelling that reaches the same BitNumber values already
    // produced by RE3's dot-notation mnemonics (e.g. "T4:0.EN" and "T4:0/EN"
    // both resolve to bit 15). Does not change any existing dot-notation
    // behavior, so no regression risk for templates already using the dot form.
    [GeneratedRegex(@"^\s*(?<FileType>[RCT])(?<FileNumber>\d{1,3}):(?<ElementNumber>\d{1,3})/(?<BitMnemonic>EN|EU|EM|ER|UL|IN|FD|TT|DN|CU|CD|OV|UN|UA)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex RE5();

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

        Match mc = RE1().Match(dataAddress);
        if (!mc.Success) mc = RE2().Match(dataAddress);
        if (!mc.Success) mc = RE3().Match(dataAddress);
        if (!mc.Success) mc = RE4().Match(dataAddress);
        if (!mc.Success) mc = RE5().Match(dataAddress);
        if (!mc.Success) return result;

        // ── FileNumber ────────────────────────────────────────────────────────
        if (mc.Groups["FileNumber"].Length == 0)
        {
            // I/O/S addresses without an explicit file number.
            // Use the already-parsed FileType group instead of searching the raw
            // string — addr.Contains("I") would false-positive on any address
            // whose string happens to contain the letter (e.g. "SI:0").
            switch (mc.Groups["FileType"].Value.ToUpperInvariant())
            {
                case "I": result.FileNumber = 1; break;
                case "O": result.FileNumber = 0; break;
                case "B": result.FileNumber = 3; break;
                default:  result.FileNumber = 2; break; // "S"
            }
        }
        else
        {
            result.FileNumber = int.Parse(mc.Groups["FileNumber"].Value);
        }

        // BitNumber
        if (mc.Groups["BitNumber"].Length > 0)
            result.BitNumber = int.Parse(mc.Groups["BitNumber"].Value);

        // Named bit-status mnemonic via slash notation (RE5 matches only —
        // e.g. "T4:0/EN", "R6:2/EN"). Maps directly to BitNumber, same values
        // as RE3's dot-notation mnemonics for the shared names (EN/TT/DN/CU/
        // CD/OV/UN/UA); EU/EM/ER/UL/IN/FD are Control-only and have no dot
        // form at all — slash is their only accepted syntax.
        if (mc.Groups["BitMnemonic"].Length > 0)
        {
            switch (mc.Groups["BitMnemonic"].Value.ToUpperInvariant())
            {
                case "EN": result.BitNumber = 15; break;
                case "TT": result.BitNumber = 14; break;
                case "EU": result.BitNumber = 14; break;
                case "DN": result.BitNumber = 13; break;
                case "CU": result.BitNumber = 15; break;
                case "CD": result.BitNumber = 14; break;
                case "EM": result.BitNumber = 12; break;
                case "OV": result.BitNumber = 12; break;
                case "ER": result.BitNumber = 11; break;
                case "UN": result.BitNumber = 11; break;
                case "UL": result.BitNumber = 10; break;
                case "UA": result.BitNumber = 10; break;
                case "IN": result.BitNumber = 9;  break;
                case "FD": result.BitNumber = 8;  break;
            }
        }

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
        // Tracks whether SubElement came from a *named* timer/counter status-bit
        // mnemonic (EN/TT/DN/CU/CD/OV/UN/UA) as opposed to a plain numeric
        // sub-element/word number (e.g. the "s" in "O0:e.s" I/O addressing,
        // which legitimately ranges 0-255 and must NOT be treated as a bit).
        bool isTimerCounterStatusBit = false;
        if (mc.Groups["SubElement"].Length > 0)
        {
            switch (mc.Groups["SubElement"].Value.ToUpperInvariant())
            {
                case "PRE": result.SubElement = 1;  break;
                case "ACC": result.SubElement = 2;  break;
                // Timer status bits
                case "EN":  result.SubElement = 15; isTimerCounterStatusBit = true; break;
                case "TT":  result.SubElement = 14; isTimerCounterStatusBit = true; break;
                case "DN":  result.SubElement = 13; isTimerCounterStatusBit = true; break;
                // Counter status bits
                case "CU":  result.SubElement = 15; isTimerCounterStatusBit = true; break;
                case "CD":  result.SubElement = 14; isTimerCounterStatusBit = true; break;
                case "OV":  result.SubElement = 12; isTimerCounterStatusBit = true; break;
                case "UN":  result.SubElement = 11; isTimerCounterStatusBit = true; break;
                case "UA":  result.SubElement = 10; isTimerCounterStatusBit = true; break;
                // Control (R) sub-elements — LEN/POS are word-level, not bits
                // (same category as PRE/ACC above), per AVEVA Plant SCADA
                // Control Data File reference: "Rf:e.LEN" -> sub-element 1,
                // "Rf:e.POS" -> sub-element 2.
                case "LEN": result.SubElement = 1;  break;
                case "POS": result.SubElement = 2;  break;
                // Control's EU/EM/ER/UL/IN/FD are intentionally absent here:
                // RE3 no longer matches them via dot notation (they're
                // slash-only, per AVEVA — see RE3's comment above). They're
                // handled by the BitMnemonic switch above instead (RE5 matches).
                default:
                    if (int.TryParse(mc.Groups["SubElement"].Value, out int se))
                        result.SubElement = se;
                    break;
            }
        }

        // ── Collapse status-bit sub-elements to bit-level access ──────────────
        // Only named timer/counter status mnemonics (e.g. T4:0.EN -> bit 15 of the
        // status word) get converted to BitNumber so the wire protocol uses the
        // bit-masked write function (0xAB). Numeric sub-elements/word numbers
        // (e.g. O0:5.10 — slot 5, word 10) must pass through untouched.
        //
        // BUG FIX (previous): this used to key off "SubElement > 4", which also
        // fired for legitimate numeric sub-elements/word numbers >4, silently
        // corrupting I/O slot.word addresses (and originally even had a second
        // bug where the SubElement value was zeroed before being read for
        // BitNumber, always yielding 0).
        if (isTimerCounterStatusBit)
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
            case "F":   result.FileType = 0x8A; result.BytesPerElements = PCCCConstants.Df1Limits.BytesPerFloat; break;
            case "S":   result.FileType = 0x84; break;
            case "ST":  result.FileType = 0x8D; result.BytesPerElements = PCCCConstants.Df1Limits.SlcStringElementBytes; break;
            case "A":   result.FileType = 0x8E; break;
            case "R":   result.FileType = 0x88; break;
            case "O":   result.FileType = 0x8B; break;
            case "I":   result.FileType = 0x8C; break;
            case "L":   result.FileType = 0x91; result.BytesPerElements = PCCCConstants.Df1Limits.BytesPerLong; break;
            case "MG":  result.FileType = 0x92; result.BytesPerElements = PCCCConstants.Df1Limits.SlcMessageElementBytes; break;
            case "PD":  result.FileType = 0x93; result.BytesPerElements = PCCCConstants.Df1Limits.SlcPidElementBytes; break;
            case "PLS": result.FileType = 0x94; result.BytesPerElements = PCCCConstants.Df1Limits.SlcPlsElementBytes; break;
        }

        return result;
    }
}
