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

// =============================================================================
// PlcFamilyProfiles.cs
//
// One class per emulated PLC family.  To add a new family:
//   1. Add a class here implementing IPlcFamilyProfile.
//   2. Register it in PlcFamilyRegistry below.
//   3. Add the --family name string in Emulator Program.cs.
//
// No other files need to change.
// =============================================================================

// ─── Registry ─────────────────────────────────────────────────────────────────

/// <summary>
/// Lookup table: CLI name → profile instance.
/// The registry is the single point of registration for all families.
/// </summary>
public static class PlcFamilyRegistry
{
    private static readonly Dictionary<string, IPlcFamilyProfile> _map = new(
        StringComparer.OrdinalIgnoreCase)
    {
        ["slc"]    = new SlcFamilyProfile(),
        ["ml1400"] = new Ml1400FamilyProfile(),
        ["plc5"]   = new Plc5FamilyProfile(),
    };

    /// <summary>Returns all registered CLI names (e.g. "slc|plc5|ml1400").</summary>
    public static string OptionList => string.Join("|", _map.Keys);

    /// <summary>Resolves a CLI name to a profile, falling back to SLC on unknown input.</summary>
    public static IPlcFamilyProfile Resolve(string name) =>
        _map.TryGetValue(name, out var p) ? p : _map["slc"];

    /// <summary>Resolves an enum tag to a profile.</summary>
    public static IPlcFamilyProfile Resolve(PCCCEmulator.EmulationFamily family)
    {
        foreach (var p in _map.Values)
            if (p.FamilyType == family) return p;
        return _map["slc"];
    }
}

// ─── Helpers ──────────────────────────────────────────────────────────────────

file static class PayloadHelper
{
    /// <summary>Writes a space-padded ASCII catalog string into payload[5..15].</summary>
    internal static void WriteCatalog(byte[] payload, string catalog)
    {
        byte[] b = System.Text.Encoding.ASCII.GetBytes(catalog);
        Array.Copy(b, 0, payload, 5, b.Length);
        for (int i = 5 + b.Length; i < 16; i++) payload[i] = 0x20;
    }
}

// ─── SLC / MicroLogix ─────────────────────────────────────────────────────────

/// <summary>
/// SLC 500 / MicroLogix family profile (SLC 5/04 defaults).
/// </summary>
public sealed class SlcFamilyProfile : IPlcFamilyProfile
{
    public string Name => "SLC 5/04";
    public PCCCEmulator.EmulationFamily FamilyType => PCCCEmulator.EmulationFamily.SlcMicroLogix;
    public bool WritesModeToStatusFile => true;
    public bool UsesPlc5UploadProtocol => false;
    public bool NeedsHttpServer        => false;

    public byte[] BuildGetStatusPayload(ProcessorMode mode)
    {
        byte[] p = new byte[24];
        p[0]  = 0x00;                           // mode/status flags
        p[1]  = 0xEE;                           // type extender
        p[2]  = 0x34;                           // extended interface type (DF1 FD)
        p[3]  = 0x5B;                           // processor type (SLC 5/04)
        p[4]  = 0x32;                           // series/revision
        PayloadHelper.WriteCatalog(p, "5/04");
        p[16] = 0x00; p[17] = 0x00;            // major error word
        p[18] = (byte)mode;                     // processor mode
        p[19] = 0x00; p[20] = 0x00; p[21] = 0x00;
        p[22] = 0x40;                           // RAM size (64 KB)
        p[23] = 0x3F;                           // flags
        return p;
    }

    public void PatchModeInPayload(byte[] payload, ProcessorMode mode)
        => payload[18] = (byte)mode;

    public PlcMemoryConfig BuildMemoryConfig()
    {
        // numDataFiles = 32: file numbers 0-31 (slots 20-28 are empty/inactive gaps)
        // dirSize = 79 + (32+12)×10 = 519 bytes
        const int dirSize        = 519;
        const int numDataFiles   = 32;
        const int numProgramFiles = 12;

        var files = new List<DataFileSpec>
        {
            new(0x8B,  0,  12),       // O0  — 6 words
            new(0x8C,  1,  42),       // I1  — 21 words
            new(0x84,  2, 166),       // S2  — 83 words
            new(0x85,  3,  28),       // B3  — 14 words
            new(0x86,  4, 468, 6),    // T4  — 78 timers
            new(0x87,  5,   6, 6),    // C5  — 1 counter
            new(0x88,  6,  12, 6),    // R6  — 2 controls
            new(0x89,  7, 148),       // N7  — 74 words
            new(0x8A,  8, 152, 4),    // F8  — 38 floats
            new(0x85,  9,  20),       // B9  — 10 words
            new(0x85, 10, 142),       // B10 — 71 words
            new(0x85, 11,  18),       // B11 — 9 words
            new(0x85, 12,   2),       // B12 — 1 word
            new(0x85, 13,   4),       // B13 — 2 words
            new(0x85, 14,   2),       // B14 — 1 word
            new(0x85, 15,  82),       // B15 — 41 words
            new(0x85, 16,  82),       // B16 — 41 words
            new(0x89, 17,  52),       // N17 — 26 words
            new(0x8D, 18, 840, 84),   // ST18 — 10 strings × 84 bytes
            new(0xA4, 19, 400, 40),   // Data Monitor File
            new(0x85, 29,  52),       // B29 — 26 words
            new(0x85, 30,  52),       // B30 — 26 words
            new(0x85, 31,  52),       // B31 — 26 words
        };

        return new PlcMemoryConfig(dirSize, numDataFiles, numProgramFiles, files,
            ProgramFiles: System.Array.Empty<ProgramFileSpec>());
    }

    public void SeedInitialValues(PlcMemory memory)
    {
        // B3: pattern values
        memory.WriteU16Direct(0x85, 3, 0, 0xAA55);
        memory.WriteU16Direct(0x85, 3, 2, 0x0FF0);
        // N7: sample integers
        memory.WriteU16Direct(0x89, 7, 0,  123);
        memory.WriteU16Direct(0x89, 7, 2,  456);
        memory.WriteU16Direct(0x89, 7, 4, unchecked((ushort)-789));
        // F8: sample floats
        memory.WriteFloatDirect(0x8A, 8, 0, 1.23f);
        memory.WriteFloatDirect(0x8A, 8, 4, 4.56f);
        // ST18:0 — default string
        memory.WriteStStringDirect(0x8D, 18, 0, "EMULATOR OK", FamilyType);
    }
}

// ─── MicroLogix 1400 ──────────────────────────────────────────────────────────

/// <summary>
/// MicroLogix 1400 family profile (1766-L32BWA Series C FRN 15.0).
/// Memory layout derived from real hardware filelist.xml.
/// </summary>
public sealed class Ml1400FamilyProfile : IPlcFamilyProfile
{
    public string Name => "MicroLogix 1400 (1766-L32BWA)";
    public PCCCEmulator.EmulationFamily FamilyType => PCCCEmulator.EmulationFamily.Ml1400;
    public bool WritesModeToStatusFile => false;
    public bool UsesPlc5UploadProtocol => false;
    public bool NeedsHttpServer        => true;

    public byte[] BuildGetStatusPayload(ProcessorMode mode)
    {
        byte[] p = new byte[29];
        p[0]  = 0x00;
        p[1]  = 0xEE;                           // type extender (SLC/ML family)
        p[2]  = 0x34;                           // extended interface type
        p[3]  = 0x9F;                           // processor type = ML1400
        p[4]  = 0x23;                           // series/revision
        PayloadHelper.WriteCatalog(p, "1766-LEC");
        p[16] = 0x00; p[17] = 0x00;
        p[18] = 0x26;                           // FRN 15.0
        p[19] = 0x04;
        p[20] = 0x71; p[21] = 0x43;
        p[22] = 0x9E; p[23] = 0xFC;
        // p[24..27] = reserved = 0x00
        p[28] = ModeToMl1400Byte(mode);
        return p;
    }

    public void PatchModeInPayload(byte[] payload, ProcessorMode mode)
        => payload[28] = ModeToMl1400Byte(mode);

    private static byte ModeToMl1400Byte(ProcessorMode mode) => mode switch
    {
        ProcessorMode.RemoteRun  => 0x02,
        ProcessorMode.LocalRun   => 0x02,
        ProcessorMode.RemoteProg => 0x00,
        _ => 0x00
    };

    public PlcMemoryConfig BuildMemoryConfig()
    {
        // 68 data files + 4 program files (SYS×2 + LAD×2 minimal)
        // dirSize = 79 + (72 × 10) = 799
        var files = new List<DataFileSpec>
        {
            // Files 0-17 (from filelist.xml)
            new(0x8B,   0,   18),        // O0   — 9 words
            new(0x8C,   1,   82),        // I1   — 41 words
            new(0x84,   2,  132),        // S2   — 66 words
            new(0x85,   3,   18),        // B3   — 9 words
            new(0x86,   4,  456, 6),     // T4   — 76 timers
            new(0x87,   5,   24, 6),     // C5   — 4 counters
            new(0x88,   6,    6, 6),     // R6   — 1 control
            new(0x89,   7,   34),        // N7   — 17 words
            new(0x8A,   8,  808, 4),     // F8   — 202 floats
            new(0x85,   9,   42),        // B9   — 21 words
            new(0x85,  10,    6),        // B10  — 3 words
            new(0x85,  11,    8),        // B11  — 4 words
            new(0x89,  12,   48),        // N12  — 24 words
            new(0x8A,  13,   40, 4),     // F13  — 10 floats
            new(0x89,  14,    4),        // N14  — 2 words
            new(0x89,  15,    4),        // N15  — 2 words
            new(0x85,  16,   58),        // B16  — 29 words
            new(0x89,  17,   54),        // N17  — 27 words
            // Files 18-218
            new(0x8A,  18,  856, 4),     // F18  — 214 floats
            new(0x89,  19,   12),        // N19  — 6 words
            new(0x91,  20,  192, 4),     // L20  — 48 longs
            new(0x8D,  21,   84, 84),    // ST21 — 1 string
            new(0x86,  25, 1458, 6),     // T25  — 243 timers
            new(0x89,  26,  142),        // N26  — 71 words
            new(0x85,  27,    6),        // B27  — 3 words
            new(0x91,  28,   24, 4),     // L28  — 6 longs
            new(0x8A,  29,   28, 4),     // F29  — 7 floats
            new(0x8A,  30,  360, 4),     // F30  — 90 floats
            new(0x86,  35,   24, 6),     // T35  — 4 timers
            new(0x89,  36,    4),        // N36  — 2 words
            new(0x8A,  37,   60, 4),     // F37  — 15 floats
            new(0x91,  38,   32, 4),     // L38  — 8 longs
            new(0x91,  60,  192, 4),     // L60  — 48 longs
            new(0x91,  61,   32, 4),     // L61  — 8 longs
            new(0x8A,  70,  360, 4),     // F70  — 90 floats
            new(0x8A,  71,   60, 4),     // F71  — 15 floats
            new(0x91,  80,  192, 4),     // L80  — 48 longs
            new(0x91,  81,   32, 4),     // L81  — 8 longs
            new(0x8A,  90,  360, 4),     // F90  — 90 floats
            new(0x8A,  91,   60, 4),     // F91  — 15 floats
            new(0x91, 100,  480, 4),     // L100 — 120 longs
            new(0x91, 101,  480, 4),     // L101 — 120 longs
            new(0x91, 102,  480, 4),     // L102 — 120 longs
            new(0x91, 103,  480, 4),     // L103 — 120 longs
            new(0x91, 104,  480, 4),     // L104 — 120 longs
            new(0x91, 105,  480, 4),     // L105 — 120 longs
            new(0x91, 106,   24, 4),     // L106 — 6 longs
            new(0x8A, 116,   68, 4),     // F116 — 17 floats
            new(0x89, 120,   44),        // N120 — 22 words
            new(0x89, 121,   16),        // N121 — 8 words
            new(0x8A, 122,  816, 4),     // F122 — 204 floats
            new(0x8A, 123, 1024, 4),     // F123 — 256 floats
            new(0x8A, 124, 1024, 4),     // F124 — 256 floats
            new(0x8A, 125,   44, 4),     // F125 — 11 floats
            new(0x85, 127,    2),        // B127 — 1 word
            new(0x91, 128,   24, 4),     // L128 — 6 longs
            new(0x91, 129,    8, 4),     // L129 — 2 longs
            new(0x91, 130,   72, 4),     // L130 — 18 longs
            new(0x91, 131,   20, 4),     // L131 — 5 longs
            new(0x89, 210,  510),        // N210 — 255 words
            new(0x89, 211,  510),        // N211 — 255 words
            new(0x89, 212,  510),        // N212 — 255 words
            new(0x89, 213,  510),        // N213 — 255 words
            new(0x89, 214,  510),        // N214 — 255 words
            new(0x89, 215,  510),        // N215 — 255 words
            new(0x89, 216,   58),        // N216 — 29 words
            new(0x89, 217,  510),        // N217 — 255 words
            new(0x89, 218,  510),        // N218 — 255 words
        };

        // numDataFiles = 219: file numbers 0-218, with gaps filled by WriteDataFileEntries.
        // dirSize = 79 + (219 + 4) × 10 = 2309 bytes
        return new PlcMemoryConfig(2309, 219, 4, files,
            ProgramFiles: System.Array.Empty<ProgramFileSpec>());
    }

    public void SeedInitialValues(PlcMemory memory)
    {
        memory.WriteU16Direct(0x89,  7, 0,  123);
        memory.WriteU16Direct(0x89,  7, 2,  456);
        memory.WriteU16Direct(0x89,  7, 4, unchecked((ushort)-789));
        memory.WriteFloatDirect(0x8A,  8, 0, 1.23f);
        memory.WriteFloatDirect(0x8A,  8, 4, 4.56f);
        memory.WriteFloatDirect(0x8A, 18, 0, 1.23f);
        memory.WriteStStringDirect(0x8D, 21, 0, "EMULATOR OK", FamilyType);
    }
}

// ─── PLC-5 ────────────────────────────────────────────────────────────────────

/// <summary>
/// PLC-5 family profile.
/// Memory layout currently reuses SLC with PLC-5-specific adjustments
/// (ST18 element size 88, L19 Long file).
/// TODO: replace BuildMemoryConfig() with real PLC-5 layout once hardware is available.
/// </summary>
public sealed class Plc5FamilyProfile : IPlcFamilyProfile
{
    public string Name => "PLC-5/40E";
    public PCCCEmulator.EmulationFamily FamilyType => PCCCEmulator.EmulationFamily.Plc5;
    public bool WritesModeToStatusFile => false;
    public bool UsesPlc5UploadProtocol => true;
    public bool NeedsHttpServer        => false;

    /// <summary>
    /// Processor expansion byte selects the PLC-5 model reported in GetStatus.
    /// Default 0x4B = 1785-L40E.  Other values: 0x4A=L20E, 0x59=L80E, 0x4E=L60E.
    /// </summary>
    public byte ProcessorExpansionByte { get; set; } = 0x4B;

    public byte[] BuildGetStatusPayload(ProcessorMode mode)
    {
        byte[] p = new byte[36];

        // Byte 0: operating status
        p[0] = mode switch
        {
            ProcessorMode.LocalRun   => 2,
            ProcessorMode.RemoteRun  => 6,
            ProcessorMode.RemoteProg => 4,
            _ => 0
        };
        p[1]  = 0xEB;                           // low nibble 0xB = PLC-5
        p[2]  = ProcessorExpansionByte;
        p[3]  = 0x00; p[4] = 0x00; p[5] = 0x01; p[6] = 0x00; // user memory 64K words
        p[7]  = 0x32;                           // series/revision
        p[8]  = 0x01;                           // DH+ node
        p[9]  = 0xFD;                           // I/O address (scanner)
        p[10] = 0x21;                           // I/O & comm params
        p[11] = 0xC9; p[12] = 0x00;            // 201 data files
        p[13] = 0x36; p[14] = 0x00;            // 54 program files
        // p[15..34] = zeroes (forcing, hold point, timestamps)
        return p;
    }

    public void PatchModeInPayload(byte[] payload, ProcessorMode mode)
    {
        payload[0] = mode switch
        {
            ProcessorMode.LocalRun   => 2,
            ProcessorMode.RemoteRun  => 6,
            ProcessorMode.RemoteProg => 4,
            _ => 0
        };
    }

    public PlcMemoryConfig BuildMemoryConfig()
    {
        // Memory layout from real PLC-5/40E (1785-L40E) hardware.
        // 64 active data files, 201 total slots (file 0-200).
        // 40 active program files, 54 total slots (file 0-53).
        // Total data memory: 5572 words (verified against hardware stats).
        //
        // File type codes (PLC-5 / SLC shared):
        //   0x8B=O  0x8C=I  0x84=S  0x85=B  0x86=T  0x87=C  0x88=R
        //   0x89=N  0x8A=F  0x93=PD 0x95=BT
        //
        // Element sizes:
        //   B/N/O/I/S = 2 bytes (1 word)
        //   F         = 4 bytes (2 words)
        //   T/C/R     = 6 bytes (3 words)
        //   PD (PID)  = 164 bytes (82 words/loop)
        //   BT        = 12 bytes (6 words/element, measured from hardware)
        const int numDataFiles    = 201;  // slots 0-200 (64 active)
        const int numProgramFiles =  54;  // slots 0-53  (40 active)
        const int dirSize         = 79 + (numDataFiles + numProgramFiles) * 10;  // = 2629

        var files = new List<DataFileSpec>
        {
            new(0x8B,   0,   256),           // O0   — output image (128 words)
            new(0x8C,   1,   256),           // I1   — input image  (128 words)
            new(0x84,   2,   256),           // S2   — status       (128 words)
            new(0x85,   3,     2),           // B3
            new(0x86,   4,  1206,   6),      // T4   — 201 timers
            new(0x87,   5,    48,   6),      // C5   — 8 counters
            new(0x88,   6,     6,   6),      // R6   — 1 control
            new(0x89,   7,   610),           // N7   — 305 integers
            new(0x8A,   8,     4,   4),      // F8
            new(0x85,   9,    38),           // B9
            new(0x85,  10,   202),           // B10
            new(0x85,  11,    28),           // B11
            new(0x85,  12,     8),           // B12
            new(0x8A,  13,  1016,   4),      // F13  — 254 floats
            new(0x8A,  14,  1784,   4),      // F14  — 446 floats
            new(0x89,  15,    42),           // N15
            new(0x8A,  16,   216,   4),      // F16
            new(0x93,  17,  1804, 164),      // PD17 — 11 PID loops (82 words/loop)
            new(0x95,  19,   192,  12),      // BT19 — 16 block transfers (6 words/elem)
            new(0x89,  20,   402),           // N20
            new(0x89,  21,   146),           // N21
            new(0x8A,  22,    44,   4),      // F22
            new(0x8A,  23,     4,   4),      // F23
            new(0x89,  25,     4),           // N25
            new(0x89,  30,   240),           // N30
            new(0x89,  31,   250),           // N31
            new(0x89,  32,   320),           // N32
            new(0x89,  33,    40),           // N33
            new(0x89,  93,    80),           // N93  — channel config
            new(0x89,  94,    88),           // N94  — channel config
            new(0x89,  99,    80),           // N99
            new(0x8A, 100,     8,   4),      // F100
            new(0x85, 102,     2),           // B102
            new(0x85, 104,  1296),           // B104 — 648 binary words
            new(0x85, 105,     2),           // B105
            new(0x85, 107,     8),           // B107
            new(0x85, 108,     2),           // B108
            new(0x85, 109,     2),           // B109
            new(0x85, 110,    24),           // B110
            new(0x85, 111,     2),           // B111
            new(0x85, 112,     2),           // B112
            new(0x85, 113,     2),           // B113
            new(0x85, 115,     2),           // B115
            new(0x85, 117,     6),           // B117
            new(0x85, 122,    40),           // B122
            new(0x85, 127,     2),           // B127
            new(0x85, 132,     2),           // B132
            new(0x85, 134,    26),           // B134
            new(0x85, 135,     2),           // B135
            new(0x85, 136,     2),           // B136
            new(0x85, 137,     4),           // B137
            new(0x85, 138,     4),           // B138
            new(0x85, 139,     4),           // B139
            new(0x85, 140,     2),           // B140
            new(0x85, 142,     4),           // B142
            new(0x85, 143,     4),           // B143
            new(0x85, 144,     4),           // B144
            new(0x85, 147,     2),           // B147
            new(0x85, 148,     2),           // B148
            new(0x85, 149,     2),           // B149
            new(0x85, 150,     2),           // B150
            new(0x85, 152,     2),           // B152
            new(0x85, 153,     2),           // B153
            new(0x85, 200,     2),           // B200
        };

        var progFiles = new List<ProgramFileSpec>
        {
            // PLC-5/40E (1785-L40E) program files.
            // Sizes = PLC binary bytes (RSLogix serialized × 0.696).
            // Verified: sum ≈ 39080 bytes = 19540 words (RSLogix report).
            new(0x01,   0,    308),   // SYSTEM
            new(0x20,   2,    394),   // MAIN
            new(0x20,   3,   2232),   // FLEX_IO
            new(0x20,   4,   7842),   // CATOX
            new(0x20,   5,    476),   // SODA_SCRUB
            new(0x20,   7,   1838),   // CO2_CTR
            new(0x20,   8,    592),   // CO2_COMP_A
            new(0x20,   9,    592),   // CO2_COMP_B
            new(0x20,  10,   2366),   // DEHYDRATOR
            new(0x20,  11,     72),   // CARBON_FIL
            new(0x20,  12,    754),   // REFLEX
            new(0x20,  13,    710),   // DESTILAT
            new(0x20,  14,     10),   // REF_CONT
            new(0x20,  15,    604),   // REF_COMP
            new(0x20,  16,      2),   // SPARE
            new(0x20,  17,   1600),   // STOR_CONT
            new(0x20,  18,     12),   // STORAGE_A
            new(0x20,  19,     12),   // STORAGE_B
            new(0x20,  20,     12),   // STORAGE_C
            new(0x20,  22,    156),   // COOL_WATER
            new(0x20,  23,    102),   // GAS_ANALYZ
            new(0x20,  24,      2),   // CALCULAT
            new(0x20,  31,   2394),   // ASD_GROUPS
            new(0x20,  32,    666),   // MAIN_ALARM
            new(0x20,  34,   5282),   // CATOX_AL
            new(0x20,  35,    252),   // SOD_SCR_AL
            new(0x20,  36,    612),   // SOD_DOS_AL
            new(0x20,  37,    912),   // CO2_COMP_AL
            new(0x20,  38,   1212),   // CO2_A_AL
            new(0x20,  39,   1212),   // CO2_B_AL
            new(0x20,  40,    376),   // DEHYD_AL
            new(0x20,  42,   1080),   // REFLEX_AL
            new(0x20,  43,   1032),   // DESTIL_AL
            new(0x20,  44,    844),   // REF_COMP_AL
            new(0x20,  47,    232),   // TAN_COMP_AL
            new(0x20,  48,    458),   // TANK_A_AL
            new(0x20,  49,    458),   // TANK_B_AL
            new(0x20,  50,    458),   // TANK_C_AL
            new(0x20,  52,    186),   // COOL_W_AL
            new(0x20,  53,    730),   // GAS_AN_AL
        };
        return new PlcMemoryConfig(dirSize, numDataFiles, numProgramFiles, files,
            ProgramFiles: progFiles);
    }

    public void SeedInitialValues(PlcMemory memory)
    {
        // B3: binary pattern
        memory.WriteU16Direct(0x85,  3, 0, 0xAA55);
        // N7: sample integers
        memory.WriteU16Direct(0x89,  7, 0,  123);
        memory.WriteU16Direct(0x89,  7, 2,  456);
        memory.WriteU16Direct(0x89,  7, 4, unchecked((ushort)-789));
        // F8: sample float
        memory.WriteFloatDirect(0x8A, 8, 0, 1.23f);
        // N20: sample integers
        memory.WriteU16Direct(0x89, 20, 0, 1000);
        memory.WriteU16Direct(0x89, 20, 2, 2000);
    }
}
