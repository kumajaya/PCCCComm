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

//#define INCLUDE_INACTIVE_FILES // Comment out to exclude inactive files
using System.Reflection;
using System.Threading;
using System.Runtime.CompilerServices;

/// <summary>
/// In-memory PLC file store simulating an SLC 5/03 (1747-L532E).
/// Memory layout follows AB Publication 1770-6.5.16.
///
/// HIGH-PERFORMANCE OPTIMIZATIONS:
/// - ReaderWriterLockSlim for concurrent read access (supports 50+ simultaneous reads)
/// - Hot cache for frequently accessed files (O0, I1, S2, N7, F8)
/// - Pre-computed file metadata for O(1) lookups
/// - Aggressive inlining on hot path methods
///
/// Directory structure (fileType=1, fileNumber=0):
///   offset 46/47 = number of program files (little-endian)
///   offset 52/53 = number of data tables   (little-endian)
///   offset 70/71 = total directory size in bytes
///   offset 79..  = file table, 10 bytes per entry:
///                  [type, sizeBytes_lo, sizeBytes_hi, fileNum, attr, elemSize, addrLo, addrHi, 0, 0]
///
/// IMPORTANT: The directory stores file sizes in BYTES (not words), which matches
///            the DF1 specification where "Byte Size" fields are in bytes.
///            See AB Publication 1770-6.5.16, page 7-17.
///
/// ReadRaw: `element` parameter is a raw byte offset into the file array.
///          DF1Comm computes: element * bytesPerElement + subElement * 2.
///
/// Data files (verified against RSLogix 500 upload — Total Files=32, Active=21):
///
///   File  Type  Elem  Bytes  Notes
///   ────  ────  ────  ─────  ─────────────────────────────────────────────
///      0  O     2     12     Output image: O:0 (slot 4), O:1 (slot 5) — 6 words
///      1  I     7     42     Input image:  I:0–I:2 (slots 1–3), I:3–I:6 (slot 6 NI4) — 21 words
///      2  S     83    166    Status S:0–S:82; stored in system memory (addr not advanced)
///      3  B     14    28     B3:0–B3:13
///      4  T     78    468    T4:0–T4:77,  6 bytes/elem
///      5  C     1     6      C5:0,        6 bytes/elem
///      6  R     2     12     R6:0–R6:1,   6 bytes/elem
///      7  N     74    148    N7:0–N7:73
///      8  F     38    152    F8:0–F8:37,  4 bytes/elem
///      9  B     10    20     B9:0–B9:9
///     10  B     71    142    B10:0–B10:70
///     11  B     9     18     B11:0–B11:8
///     12  B     1     2      B12:0
///     13  B     2     4      B13:0–B13:1
///     14  B     1     2      B14:0
///     15  B     41    82     B15:0–B15:40
///     16  B     41    82     B16:0–B16:40
///     17  N     26    52     N17:0–N17:25
///  18–28  —     —     —      Inactive slots
///     29  B     26    52     B29:0–B29:25
///     30  B     26    52     B30:0–B30:25
///     31  B     26    52     B31:0–B31:25
///
/// Program files (Total=24, Active=10):
///   File 0–1: SYS; Files 2–23: LAD (active: 2, 3, 5, 8, 12, 15, 18, 19, 22, 23)
///
/// Rack (1746-A7, 7 slots):
///   Slot 0: 1747-L532E  CPU           no I/O image
///   Slot 1: 1746-IB16   Digital In    2 InputBytes
///   Slot 2: 1746-IB16   Digital In    2 InputBytes
///   Slot 3: 1746-IB16   Digital In    2 InputBytes
///   Slot 4: 1746-OB16   Digital Out   2 OutputBytes
///   Slot 5: 1746-OB16   Digital Out   2 OutputBytes
///   Slot 6: 1746-NI4    Analog In     8 InputBytes (4 ch × 2 bytes)
/// </summary>
public class PlcMemory
{
    // ─── HIGH-PERFORMANCE: ReaderWriterLockSlim instead of lock() ────────────
    private readonly ReaderWriterLockSlim _rwLock = new ReaderWriterLockSlim();
    
    private readonly Dictionary<(int, int), byte[]> _files = new();
    private readonly Dictionary<(int, int), int>    _bytesPerElement = new();
    private readonly Dictionary<int, int>           _fileTypeByNumber = new();
    
    // ─── HOT CACHE: Frequently accessed files (lock-free read) ──────────────
    private struct HotFileEntry
    {
        public byte[] Data;
        public int BytesPerElement;
        public int FileType;
    }

    // ─── HOT CACHE: Frequently accessed files ──────────────────────────────
    // NOTE: Hot cache entries are immutable after initialization (file number to type mapping,
    // bytes per element, and array reference never change). Only the array content can change
    // via Write() operations. Therefore, reading BytesPerElement, Data.Length, and FileType
    // is safe without locks. Reading actual data bytes requires _rwLock read lock to prevent
    // seeing partially written data from concurrent writes.
    private readonly Dictionary<int, HotFileEntry> _hotCache = new();
    private readonly int[] _hotFileNumbers = { 0, 1, 2, 3, 7, 8 }; // O, I, S, B, N, F
    
    private bool _programLoaded = false;
    private int _totalReads = 0;
    private int _totalWrites = 0;
    private int _hotCacheHits = 0;

    public PlcMemory()
    {
        BuildDirectory();
        BuildDataFiles();
        BuildIoConfig();
        BuildDownloadSeed();
        LoadEmbeddedProgram();
        
        // Initialize hot cache after all files are loaded
        InitializeHotCache();
        
        if (_programLoaded)
            Console.WriteLine("DF1 PLC memory initialized with embedded program.");
        else
            Console.WriteLine("DF1 PLC memory initialized with default data.");
            
        Console.WriteLine($"Hot cache initialized with {_hotCache.Count} files");
    }
    
    /// <summary>
    /// Initialize hot cache for frequently accessed files.
    /// These files are accessed lock-free for reads.
    /// </summary>
    private void InitializeHotCache()
    {
        foreach (int fileNum in _hotFileNumbers)
        {
            if (_fileTypeByNumber.TryGetValue(fileNum, out int fileType))
            {
                if (_files.TryGetValue((fileType, fileNum), out var data))
                {
                    int bpe = _bytesPerElement.GetValueOrDefault((fileType, fileNum), 2);
                    _hotCache[fileNum] = new HotFileEntry
                    {
                        Data = data,
                        BytesPerElement = bpe,
                        FileType = fileType
                    };
                }
            }
        }
    }

    // =========================================================================
    // DIRECTORY
    // =========================================================================

    private void BuildDirectory()
    {
        // Directory size calculation:
        //   79 bytes header + 56 entries × 10 bytes = 639 bytes total
        //   56 entries = 32 data file slots + 24 program file slots
        //
        // Note: This 639 bytes is the size of File 0 (directory) itself.
        //       It is NOT the "Total Memory (Words): 714" shown in RSLogix.
        //       The 714 words is the sum of user data table words (O, I, B, N, F, T, C, R files)
        //       which is calculated from sizeBytes of each data file registered below.
        //
#if INCLUDE_INACTIVE_FILES
        const int dirSize = 639;
        const int numProgramFiles = 24;  // SYS×2 + LAD×22
        const int numDataFiles = 32;     // 32 data file slots
#else
        const int dirSize = 409;         // 79 + (33 × 10)
        const int numProgramFiles = 12;  // SYS×2 + LAD×10
        const int numDataFiles = 21;     // 21 data files aktif
#endif
        var dir = new byte[dirSize];

        WriteU16(dir, 70, dirSize); // directory size at offset 70 (element 0x23 × 2)
        WriteU16(dir, 46, numProgramFiles);
        WriteU16(dir, 52, numDataFiles);

        int pos  = 79;   // file table starts at offset 79
        int addr = 0;    // running base address in WORDS

        /// <summary>
        /// Write a 10-byte data file entry to the directory.
        /// </summary>
        /// <param name="type">DF1 file type code (e.g., 0x8B for O, 0x8C for I, 0x85 for B)</param>
        /// <param name="sizeBytes">File size in BYTES (matches DF1 "Byte Size" specification)</param>
        /// <param name="fileNum">File number (0-255)</param>
        /// <param name="elemSize">Size of each element in bytes (default 2)</param>
        void Reg(byte type, int sizeBytes, byte fileNum, int elemSize = 2)
        {
            // Store sizeBytes directly in BYTES (per AB Publication 1770-6.5.16 page 7-17)
            dir[pos]     = type;
            dir[pos + 1] = (byte)(sizeBytes & 0xFF);
            dir[pos + 2] = (byte)((sizeBytes >> 8) & 0xFF);
            dir[pos + 3] = fileNum;
            dir[pos + 4] = 0x00;              // attribute: normal
            dir[pos + 5] = (byte)elemSize;    // element size in bytes
            dir[pos + 6] = (byte)(addr & 0xFF);
            dir[pos + 7] = (byte)(addr >> 8);
            dir[pos + 8] = 0x00;
            dir[pos + 9] = 0x00;
            addr += sizeBytes / 2;            // addr advances in WORDS
            _fileTypeByNumber[fileNum] = type;
            pos += 10;
        }

        // ── Data files ───────────────────────────────────────────────────────
        Reg(0x8B,  12,  0);       // O0  — 6 words = 12 bytes
        Reg(0x8C,  42,  1);       // I1  — 21 words = 42 bytes
        Reg(0x84, 166,  2);       // S2  — system memory, no user address space
        Reg(0x85,  28,  3);       // B3  — 14 words = 28 bytes
        Reg(0x86, 468,  4, 6);    // T4  — 78 timers × 6 bytes = 468 bytes
        Reg(0x87,   6,  5, 6);    // C5  — 1 counter × 6 bytes = 6 bytes
        Reg(0x88,  12,  6, 6);    // R6  — 2 controls × 6 bytes = 12 bytes
        Reg(0x89, 148,  7);       // N7  — 74 words = 148 bytes
        Reg(0x8A, 152,  8, 4);    // F8  — 38 floats × 4 bytes = 152 bytes
        Reg(0x85,  20,  9);       // B9  — 10 words = 20 bytes
        Reg(0x85, 142, 10);       // B10 — 71 words = 142 bytes
        Reg(0x85,  18, 11);       // B11 — 9 words = 18 bytes
        Reg(0x85,   2, 12);       // B12 — 1 word = 2 bytes
        Reg(0x85,   4, 13);       // B13 — 2 words = 4 bytes
        Reg(0x85,   2, 14);       // B14 — 1 word = 2 bytes
        Reg(0x85,  82, 15);       // B15 — 41 words = 82 bytes
        Reg(0x85,  82, 16);       // B16 — 41 words = 82 bytes
        Reg(0x89,  52, 17);       // N17 — 26 words = 52 bytes

#if INCLUDE_INACTIVE_FILES
        // Files 18–28: inactive slots (type 0x85 = Binary, size 0).
        // RSLogix shows Total Files=32, Active Files=21; these occupy directory
        // slots but have no data and do not appear in RSLogix file lists.
        for (int n = 18; n <= 28; n++)
        {
            dir[pos]     = 0x85;
            dir[pos + 1] = 0x00;
            dir[pos + 2] = 0x00;
            dir[pos + 3] = (byte)n;
            // bytes 4–9 all zero
            pos += 10;
        }
#endif

        Reg(0x85, 52, 29);        // B29 — 26 words = 52 bytes
        Reg(0x85, 52, 30);        // B30 — 26 words = 52 bytes
        Reg(0x85, 52, 31);        // B31 — 26 words = 52 bytes

        // ── Program files ────────────────────────────────────────────────────
        // Actual size per LAD file from .ACH disassembly (bytes)
        var actualLadSizes = new Dictionary<int, int>
        {
            {2, 757},   // LAD 2: 757 bytes
            {3, 486},   // LAD 3: 486 bytes
            {5, 972},   // LAD 5: 972 bytes
            {8, 646},   // LAD 8: 646 bytes
            {12, 1440}, // LAD 12: 1440 bytes
            {15, 824},  // LAD 15: 824 bytes
            {18, 646},  // LAD 18: 646 bytes
            {19, 225},  // LAD 19: 225 bytes
            {22, 903},  // LAD 22: 903 bytes
            {23, 416},  // LAD 23: 416 bytes
        };
        int[] activeLad = { 2, 3, 5, 8, 12, 15, 18, 19, 22, 23 };

        // SYS file 0 (2 bytes)
        dir[pos]     = 0x01;
        dir[pos + 1] = 0x02;   // 2 bytes
        dir[pos + 2] = 0x00;
        dir[pos + 3] = 0x00;
        pos += 10;
        _fileTypeByNumber[0] = 0x01;

        // SYS file 1 (2 bytes)
        dir[pos]     = 0x01;
        dir[pos + 1] = 0x02;   // 2 bytes
        dir[pos + 2] = 0x00;
        dir[pos + 3] = 0x01;
        pos += 10;
        _fileTypeByNumber[1] = 0x01;

#if INCLUDE_INACTIVE_FILES
        // LAD files 2-23
        int ladTypeIndex = 0;
        for (int n = 2; n <= 23; n++)
        {
            bool active = Array.IndexOf(activeLad, n) >= 0;
            int sizeBytes = 0;
            // Use sequential type index for consistency with #else branch
            byte fileType = (byte)(0x20 + ladTypeIndex);
            ladTypeIndex++;
            
            if (active && actualLadSizes.ContainsKey(n))
            {
                sizeBytes = actualLadSizes[n];
                // Allocate storage for active LAD logic
                _files[(fileType, n)] = new byte[sizeBytes];
                _bytesPerElement[(fileType, n)] = 0;
                _fileTypeByNumber[n] = fileType;
            }
            else
            {
                // Inactive LAD file: no storage allocation, but directory entry exists
                _fileTypeByNumber[n] = fileType;
            }
            
            // Directory entry stores size in BYTES (per AB specification)
            dir[pos]     = fileType;
            dir[pos + 1] = (byte)(sizeBytes & 0xFF);
            dir[pos + 2] = (byte)((sizeBytes >> 8) & 0xFF);
            dir[pos + 3] = (byte)n;
            // bytes 4-9 remain zero
            pos += 10;
        }
#else
        int typeIndex = 0;
        foreach (int n in activeLad)
        {
            int sizeBytes = actualLadSizes[n];
            byte fileType = (byte)(0x20 + typeIndex);
            typeIndex++;
            
            _files[(fileType, n)] = new byte[sizeBytes];
            _bytesPerElement[(fileType, n)] = 0;
            _fileTypeByNumber[n] = fileType;
            
            dir[pos]     = fileType;
            dir[pos + 1] = (byte)(sizeBytes & 0xFF);
            dir[pos + 2] = (byte)((sizeBytes >> 8) & 0xFF);
            dir[pos + 3] = (byte)n;
            pos += 10;
        }
#endif

        // Store the directory itself as File 0
        _files[(1, 0)] = dir;
    }

    // =========================================================================
    // DATA FILES
    // =========================================================================

    private void BuildDataFiles()
    {
        // ── O0 — Output image (6 words = 12 bytes) ───────────────────────────
        // O:0 = slot 4 (1746-OB16), O:1 = slot 5 (1746-OB16)
        _files[(0x8B, 0)]          = new byte[12];
        _bytesPerElement[(0x8B, 0)] = 2;

        // ── I1 — Input image (21 words = 42 bytes) ───────────────────────────
        // I:0–I:2 = slots 1–3 (1746-IB16 × 3), I:3–I:6 = slot 6 (1746-NI4, 4 ch)
        _files[(0x8C, 1)]          = new byte[42];
        _bytesPerElement[(0x8C, 1)] = 2;

        // ── S2 — Status (83 words = 166 bytes, S:0–S:82) ─────────────────────
        // System memory, not counted in user Total Memory
        _files[(0x84, 2)]          = new byte[166];
        _bytesPerElement[(0x84, 2)] = 2;
        ushort[] s2 =
        {
            // S2:0  – S2:9
            0x0004, 0x001E, 0x9012, 0xA003, 0x69C4, 0x0000, 0x0000, 0x0000, 0x0000, 0x0003,
            // S2:10 – S2:19
            0x0000, 0x0000, 0x0000, 0x0000, 0x001E, 0x0401, 0x0016, 0x0002, 0x0000, 0x0000,
            // S2:20 – S2:29
            0x0000, 0x0000, 0x0031, 0x000C, 0x0020, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000,
            // S2:30 – S2:39
            0x0000, 0x0000, 0x0018, 0x0000, 0x007D, 0x0000, 0x07EA, 0x0005, 0x0015, 0x0000,
            // S2:40 – S2:49
            0x0000, 0x0034, 0x002E, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000,
            // S2:50 – S2:59
            0x0000, 0x0000, 0x0000, 0x0004, 0x0000, 0x0000, 0x012E, 0x012E, 0x0004, 0x0000,
            // S2:60 – S2:69
            0x0214, 0x0004, 0x0008, 0x0001, 0x005F, 0x0010, 0x01E0, 0x0006, 0x0000, 0x0000,
            // S2:70 – S2:82
            0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000,
            0x0000, 0x0000, 0x0000,
        };
        for (int i = 0; i < s2.Length; i++)
            WriteU16(_files[(0x84, 2)], i * 2, s2[i]);

        // ── B3 — Binary (14 words = 28 bytes) ────────────────────────────────
        _files[(0x85, 3)]          = new byte[28];
        _bytesPerElement[(0x85, 3)] = 2;
        WriteU16(_files[(0x85, 3)], 0, 0xAA55);
        WriteU16(_files[(0x85, 3)], 2, 0x0FF0);

        // ── T4 — Timer (78 timers × 6 bytes = 468 bytes) ─────────────────────
        _files[(0x86, 4)]          = new byte[468];
        _bytesPerElement[(0x86, 4)] = 6;

        // ── C5 — Counter (1 counter × 6 bytes = 6 bytes) ────────────────────
        _files[(0x87, 5)]          = new byte[6];
        _bytesPerElement[(0x87, 5)] = 6;

        // ── R6 — Control (2 controls × 6 bytes = 12 bytes) ───────────────────
        _files[(0x88, 6)]          = new byte[12];
        _bytesPerElement[(0x88, 6)] = 6;

        // ── N7 — Integer (74 words = 148 bytes) ──────────────────────────────
        _files[(0x89, 7)]          = new byte[148];
        _bytesPerElement[(0x89, 7)] = 2;
        WriteU16(_files[(0x89, 7)],  0,   123);
        WriteU16(_files[(0x89, 7)],  2,   456);
        WriteU16(_files[(0x89, 7)],  4, -789);

        // ── F8 — Float (38 floats × 4 bytes = 152 bytes) ─────────────────────
        _files[(0x8A, 8)]          = new byte[152];
        _bytesPerElement[(0x8A, 8)] = 4;
        Array.Copy(BitConverter.GetBytes(1.23f), 0, _files[(0x8A, 8)], 0, 4);
        Array.Copy(BitConverter.GetBytes(4.56f), 0, _files[(0x8A, 8)], 4, 4);

        // ── B9–B16, N17, B29–B31 ─────────────────────────────────────────────
        CreateDataFile(0x85,  9,  20, 2);   // B9  — 10 words = 20 bytes
        CreateDataFile(0x85, 10, 142, 2);   // B10 — 71 words = 142 bytes
        CreateDataFile(0x85, 11,  18, 2);   // B11 — 9 words = 18 bytes
        CreateDataFile(0x85, 12,   2, 2);   // B12 — 1 word = 2 bytes
        CreateDataFile(0x85, 13,   4, 2);   // B13 — 2 words = 4 bytes
        CreateDataFile(0x85, 14,   2, 2);   // B14 — 1 word = 2 bytes
        CreateDataFile(0x85, 15,  82, 2);   // B15 — 41 words = 82 bytes
        CreateDataFile(0x85, 16,  82, 2);   // B16 — 41 words = 82 bytes
        CreateDataFile(0x89, 17,  52, 2);   // N17 — 26 words = 52 bytes
        CreateDataFile(0x85, 29,  52, 2);   // B29 — 26 words = 52 bytes
        CreateDataFile(0x85, 30,  52, 2);   // B30 — 26 words = 52 bytes
        CreateDataFile(0x85, 31,  52, 2);   // B31 — 26 words = 52 bytes
    }

    // =========================================================================
    // I/O CONFIGURATION  (file type 0x60, file number 0)
    // =========================================================================

    private void BuildIoConfig()
    {
        // Accessed via CMD=0x0F FNC=0xA2.
        // GetSlotCount()    reads byte [0] → raw slot count; returns (raw - 1).
        // GetSLCIOConfig()  reads result[i] from offset i*6+4:
        //   +0 = InputBytes, +2 = OutputBytes, +4/+5 = CardCode.
        // Slot 0 (CPU) at offset 4: InputBytes=0, OutputBytes=0 (default zero).
        // Buffer = 4 + 8*6 + 2 = 54 bytes minimum; padded to 64.
        CreateDataFile(0x60, 0, 64, 2);
        byte[] io = _files[(0x60, 0)];

        io[0] = 8;      // raw slot count → GetSlotCount() returns 7

        // InputBytes  @ slot*6+4, OutputBytes @ slot*6+6
        // Slot 0 (CPU): both zero — default array value.
        io[1 * 6 + 4] = 2;     // Slot 1: 1746-IB16  InputBytes=2
        io[2 * 6 + 4] = 2;     // Slot 2: 1746-IB16  InputBytes=2
        io[3 * 6 + 4] = 2;     // Slot 3: 1746-IB16  InputBytes=2
        io[4 * 6 + 6] = 2;     // Slot 4: 1746-OB16  OutputBytes=2
        io[5 * 6 + 6] = 2;     // Slot 5: 1746-OB16  OutputBytes=2
        io[6 * 6 + 4] = 8;     // Slot 6: 1746-NI4   InputBytes=8 (4 ch × 2 bytes)
    }

    // =========================================================================
    // DOWNLOAD SEED  (file type 0x63, file number 0)
    // =========================================================================

    private void BuildDownloadSeed()
    {
        // DF1Comm.DownloadProgramData reads 4 bytes from this file and copies them
        // into the FNC=0x88 init packet. Content 0x00000000 is sufficient.
        CreateDataFile(0x63, 0, 4, 4);
    }

    // =========================================================================
    // PUBLIC API (HIGH-PERFORMANCE)
    // =========================================================================

    /// <summary>
    /// Read <paramref name="lengthInBytes"/> bytes starting at raw byte offset
    /// <paramref name="element"/> within the specified file.
    /// Returns an empty array and sets <paramref name="status"/> != 0 on error:
    ///   2 = file not found, 3 = offset or length out of range.
    /// 
    /// HIGH-PERFORMANCE: Uses hot cache for files 0,1,2,3,7,8 (lock-free read).
    /// Falls back to reader lock for other files.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte[] ReadRaw(int fileType, int fileNumber, int element, int lengthInBytes, out int status)
    {
        Interlocked.Increment(ref _totalReads);
        
        // FAST PATH: Check hot cache first
        if (_hotCache.TryGetValue(fileNumber, out var hotEntry) && hotEntry.FileType == fileType)
        {
            Interlocked.Increment(ref _hotCacheHits);
            
            // MUST use read lock to prevent seeing partially written data
            _rwLock.EnterReadLock();
            try
            {
                byte[] data = hotEntry.Data;
                if (element < 0 || element + lengthInBytes > data.Length)
                {
                    status = 3;
                    return Array.Empty<byte>();
                }
                
                status = 0;
                var result = new byte[lengthInBytes];
                Array.Copy(data, element, result, 0, lengthInBytes);
                return result;
            }
            finally
            {
                _rwLock.ExitReadLock();
            }
        }
        
        // SLOW PATH: Use reader lock for other files
        _rwLock.EnterReadLock();
        try
        {
            byte[]? data = Lookup(fileType, fileNumber);
            if (data == null)
            {
                status = 2;
                return Array.Empty<byte>();
            }
            
            if (element < 0 || element >= data.Length)
            {
                status = 3;
                return Array.Empty<byte>();
            }
            
            if (element + lengthInBytes > data.Length)
            {
                status = 3;
                return Array.Empty<byte>();
            }
            
            var result = new byte[lengthInBytes];
            Array.Copy(data, element, result, 0, lengthInBytes);
            status = 0;
            return result;
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Write <paramref name="newData"/> at byte offset
    /// <c>element * bytesPerElement + subElement * 2</c>.
    /// Returns false if the file is not found or the offset is out of range.
    /// 
    /// HIGH-PERFORMANCE: Uses write lock (exclusive access for writes).
    /// Updates hot cache if the written file is in hot cache.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Write(int fileType, int fileNumber, int element, int subElement,
                      int lengthInBytes, byte[] newData)
    {
        Interlocked.Increment(ref _totalWrites);
        
        _rwLock.EnterWriteLock();
        try
        {
            byte[]? data = Lookup(fileType, fileNumber);
            if (data == null) return false;
            
            int bpe = _bytesPerElement.GetValueOrDefault((fileType, fileNumber), 2);
            int offset = element * bpe + subElement * 2;
            
            if (offset < 0 || offset >= data.Length) return false;
            if (offset + lengthInBytes > data.Length) return false;
            
            Array.Copy(newData, 0, data, offset, Math.Min(newData.Length, data.Length - offset));
            
            // Update hot cache if this file is cached (data reference unchanged)
            if (_hotCache.ContainsKey(fileNumber) && _hotCache[fileNumber].FileType == fileType)
            {
                // Data array reference is same, no need to update cache entry
                // Just invalidate if we want to force re-read, but array content changed
                // so cache is still valid
            }
            
            return true;
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Returns the bytes-per-element for a file, or 2 if not registered.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetBytesPerElement(int fileType, int fileNumber)
    {
        // Fast path: hot cache
        if (_hotCache.TryGetValue(fileNumber, out var hotEntry) && hotEntry.FileType == fileType)
        {
            return hotEntry.BytesPerElement;
        }
        
        // Slow path
        _rwLock.EnterReadLock();
        try
        {
            return _bytesPerElement.GetValueOrDefault((fileType, fileNumber), 2);
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Returns the total byte size of a file, or 0 if not found.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetFileSize(int fileType, int fileNumber)
    {
        // Fast path: hot cache
        if (_hotCache.TryGetValue(fileNumber, out var hotEntry) && hotEntry.FileType == fileType)
        {
            return hotEntry.Data.Length;
        }
        
        // Slow path
        _rwLock.EnterReadLock();
        try
        {
            return Lookup(fileType, fileNumber)?.Length ?? 0;
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Returns the file type code for a given file number, or 0 if not found.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetFileTypeForNumber(int fileNumber)
    {
        _rwLock.EnterReadLock();
        try
        {
            return _fileTypeByNumber.GetValueOrDefault(fileNumber, 0);
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Returns file type, byte size, and element count for a given file number.
    /// Used by HandleReadFileInfo. Returns false if the file number is not registered.
    /// </summary>
    public bool GetFileInfo(int fileNumber, out int fileType, out int sizeBytes, out int elementCount)
    {
        fileType = sizeBytes = elementCount = 0;
        
        // Fast path: hot cache
        if (_hotCache.TryGetValue(fileNumber, out var hotEntry))
        {
            fileType = hotEntry.FileType;
            sizeBytes = hotEntry.Data.Length;
            elementCount = hotEntry.BytesPerElement > 0 ? sizeBytes / hotEntry.BytesPerElement : 0;
            return true;
        }
        
        // Slow path
        _rwLock.EnterReadLock();
        try
        {
            if (!_fileTypeByNumber.TryGetValue(fileNumber, out fileType) || fileType == 0)
                return false;
                
            byte[]? data = Lookup(fileType, fileNumber);
            if (data == null) return false;
            
            sizeBytes = data.Length;
            int bpe = _bytesPerElement.GetValueOrDefault((fileType, fileNumber), 2);
            elementCount = bpe > 0 ? sizeBytes / bpe : 0;
            return true;
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }
    
    /// <summary>
    /// Get performance statistics (for debugging)
    /// </summary>
    public void GetStats(out int totalReads, out int totalWrites, out int hotCacheHits, out double hitRate)
    {
        totalReads = Interlocked.CompareExchange(ref _totalReads, 0, 0);
        totalWrites = Interlocked.CompareExchange(ref _totalWrites, 0, 0);
        hotCacheHits = Interlocked.CompareExchange(ref _hotCacheHits, 0, 0);
        hitRate = totalReads > 0 ? (double)hotCacheHits / totalReads * 100.0 : 0.0;
    }

    // =========================================================================
    // PRIVATE HELPERS
    // =========================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte[]? Lookup(int fileType, int fileNumber)
    {
        if (_files.TryGetValue((fileType, fileNumber), out var d)) return d;
        int t = fileType & 0x7F;
        if (_files.TryGetValue((t, fileNumber), out d)) return d;
        if (_files.TryGetValue((t | 0x80, fileNumber), out d)) return d;
        return null;
    }

    private void CreateDataFile(byte fileType, int fileNumber, int sizeBytes, int bytesPerElement)
    {
        _files[(fileType, fileNumber)] = new byte[sizeBytes];
        _bytesPerElement[(fileType, fileNumber)] = bytesPerElement;
        _fileTypeByNumber[fileNumber] = fileType;
    }

    private static void WriteU16(byte[] buf, int offset, int value)
    {
        buf[offset]     = (byte)(value & 0xFF);
        buf[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    // =========================================================================
    // EMBEDDED PROGRAM LOADER
    // =========================================================================

    /// <summary>
    /// Load program from embedded .bin resource (generated by ach_to_df1.py)
    /// File must be placed in Resources/ folder and marked as EmbeddedResource.
    /// </summary>
    private void LoadEmbeddedProgram()
    {
        var assembly = Assembly.GetExecutingAssembly();
        
        // Find first .bin embedded resource (case insensitive)
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(".bin", StringComparison.OrdinalIgnoreCase));
        
        if (resourceName == null)
        {
            Console.WriteLine("[INFO] No embedded program found. Using default data.");
            return;
        }
        
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            Console.WriteLine("[ERROR] Failed to load embedded resource.");
            return;
        }
        
        var data = new byte[stream.Length];
        
        // .NET 8+ guarantees to read exactly the requested number of bytes
        // Throws EndOfStreamException if the stream ends prematurely
        stream.ReadExactly(data, 0, data.Length);
        
        // Parse header
        using var ms = new MemoryStream(data);
        using var br = new BinaryReader(ms);
        
        ushort magic = br.ReadUInt16();
        byte version = br.ReadByte();
        int procType = br.ReadInt32();
        byte seriesRev = br.ReadByte();
        byte ramKb = br.ReadByte();
        string family = System.Text.Encoding.ASCII.GetString(br.ReadBytes(8)).Trim();
        int bulletinLen = br.ReadInt32();
        string bulletin = bulletinLen > 0 ? System.Text.Encoding.UTF8.GetString(br.ReadBytes(bulletinLen)) : "";
        long timestamp = br.ReadInt64();
        int fileCount = br.ReadInt32();
        
        Console.WriteLine($"[BIN]  {resourceName}");
        Console.WriteLine($"       Size={data.Length} Magic=0x{magic:X4} Ver={version} Type=0x{procType:X2} {family} {bulletin}");
        Console.WriteLine($"       Files={fileCount} TS={DateTime.FromBinary(timestamp):yyyy-MM-dd HH:mm:ss}");
        
        int dataLoaded = 0, progLoaded = 0;
        
        for (int i = 0; i < fileCount; i++)
        {
            int fileNumber = br.ReadInt32();
            int fileType = br.ReadInt32();
            int numberOfBytes = br.ReadInt32();
            int dataLength = br.ReadInt32();
            byte[] fileData = br.ReadBytes(dataLength);
            
            // Skip directory (0,0) and SYS (0x01,0-1)
            if ((fileType == 0 && fileNumber == 0) || (fileType == 0x01 && fileNumber <= 1))
                continue;
            
            if (fileType >= 0x20 && fileType <= 0x3F)
                progLoaded++;
            else if (fileType >= 0x80 && fileType <= 0x9F)
                dataLoaded++;
            
            if (!_files.ContainsKey((fileType, fileNumber)))
            {
                int elemSize = fileType switch
                {
                    0x8B or 0x8C or 0x86 or 0x87 or 0x88 => 6,
                    0x8A => 4,
                    _ => 2
                };
                _files[(fileType, fileNumber)] = new byte[numberOfBytes];
                _bytesPerElement[(fileType, fileNumber)] = elemSize;
                _fileTypeByNumber[fileNumber] = fileType;
            }
            else
            {
                // File already exists (from BuildDataFiles) - check size compatibility
                var dest = _files[(fileType, fileNumber)];
                if (dest.Length != numberOfBytes)
                {
                    Console.WriteLine($"[WARN] File (0x{fileType:X2},{fileNumber}) size mismatch: " +
                        $"binary={numberOfBytes}, allocated={dest.Length}");
                }
                
                // Copy as much as destination can hold
                int copyLen = Math.Min(fileData.Length, dest.Length);
                if (copyLen < numberOfBytes)
                {
                    Console.WriteLine($"[WARN] File (0x{fileType:X2},{fileNumber}) truncated: " +
                        $"binary wants {numberOfBytes} bytes, destination has {dest.Length} bytes");
                }
                Array.Copy(fileData, 0, dest, 0, copyLen);
                continue; // Skip the allocation code below
            }

            // Only reach here for newly created files
            Array.Copy(fileData, 0, _files[(fileType, fileNumber)], 0, Math.Min(fileData.Length, numberOfBytes));
        }
        
        Console.WriteLine($"       Loaded: {dataLoaded} data files, {progLoaded} program files");
        _programLoaded = true;
    }
}
