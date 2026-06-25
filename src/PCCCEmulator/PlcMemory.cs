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

// #define INCLUDE_INACTIVE_FILES
// Define this symbol to include inactive file slots 18-28.
// This enables the full 32-file directory layout (matches RSLogix display)
// but increases memory usage and directory size from 409 to 639 bytes.

using System.Reflection;
using System.Linq;
using System.Threading;
using System.Runtime.CompilerServices;

/// <summary>
/// In-memory PLC file store simulating an SLC 5/03 (1747-L532E).
/// Memory layout follows AB Publication 1770-6.5.16.
///
/// DIRECTORY STRUCTURE (fileType=1, fileNumber=0):
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
/// DATA FILES (verified against RSLogix 500 upload — Total Files=32, Active=21):
///   File  Type  Elem  Bytes  Notes
///   ────  ────  ────  ─────  ─────────────────────────────────────────────
///      0  O     2     12     Output image: O:0 (slot 4), O:1 (slot 5) — 6 words
///      1  I     7     42     Input image:  I:0–I:2 (slots 1–3), I:3–I:6 (slot 6 NI4)
///      2  S     83    166    Status S:0–S:82; stored in system memory
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
///     18  ST    10   840/880 ST18:0–ST18:9, 84 bytes/elem (SLC) or 88 bytes/elem (PLC-5)
///     19  L     25   100     L19:0–L19:24 (PLC-5 only), 4 bytes/elem
///     19  A4    10   400     Data Monitor File (type 0xA4), 40 bytes/elem
///  20–28  —     —     —      Inactive slots (reserved)
///     29  B     26    52     B29:0–B29:25
///     30  B     26    52     B30:0–B30:25
///     31  B     26    52     B31:0–B31:25
///
/// PROGRAM FILES (Total=24, Active=10):
///   File 0–1: SYS; Files 2–23: LAD (active: 2, 3, 5, 8, 12, 15, 18, 19, 22, 23)
///
/// RACK CONFIGURATION (1746-A7, 7 slots):
///   Slot 0: 1747-L532E  CPU           no I/O image
///   Slot 1: 1746-IB16   Digital In    2 bytes input
///   Slot 2: 1746-IB16   Digital In    2 bytes input
///   Slot 3: 1746-IB16   Digital In    2 bytes input
///   Slot 4: 1746-OB16   Digital Out   2 bytes output
///   Slot 5: 1746-OB16   Digital Out   2 bytes output
///   Slot 6: 1746-NI4    Analog In     8 bytes input (4 channels × 2 bytes)
///
/// HIGH-PERFORMANCE OPTIMIZATIONS:
///   - ReaderWriterLockSlim for concurrent read access (supports many simultaneous reads)
///   - Hot cache for frequently accessed files (O0, I1, S2, B3, N7, F8)
///   - Pre-computed file metadata for O(1) lookups
///   - Aggressive inlining on hot path methods
///   - Lock-free hot cache reads (only content changes, references are immutable)
/// </summary>
public class PlcMemory : IDisposable
{
    // ─── Synchronization ──────────────────────────────────────────────────────
    // ReaderWriterLockSlim provides better performance than lock() for read-heavy workloads
    private readonly ReaderWriterLockSlim _rwLock = new ReaderWriterLockSlim();
    
    // ─── File Storage ─────────────────────────────────────────────────────────
    // Primary storage: maps (fileType, fileNumber) to raw byte arrays
    private readonly Dictionary<(int fileType, int fileNumber), byte[]> _files = new();
    
    // Bytes per element for each file (2 for words, 4 for floats, 6 for timers/counters)
    private readonly Dictionary<(int, int), int> _bytesPerElement = new();
    
    // Maps file number to file type (for quick lookup by number only)
    private readonly Dictionary<int, int> _fileTypeByNumber = new();
    
    // ─── Hot Cache (lock-free reads for frequently accessed files) ────────────
    // Hot cache entries are immutable after initialization:
    // - File number to type mapping never changes
    // - Bytes per element never changes
    // - Data array reference never changes (only array content changes)
    // Therefore, reading BytesPerElement, Data.Length, and FileType is safe
    // without locks. Reading actual data bytes requires _rwLock read lock to
    // prevent seeing partially written data from concurrent writes.
    private struct HotFileEntry
    {
        public byte[] Data;
        public int BytesPerElement;
        public int FileType;
    }
    private readonly Dictionary<int, HotFileEntry> _hotCache = new();
    
    // Files that are accessed most frequently during normal operation
    private readonly int[] _hotFileNumbers = { 0, 1, 2, 3, 7, 8 }; // O, I, S, B, N, F
    
    // ─── Statistics (for debugging and performance monitoring) ────────────────
    private bool _programLoaded = false;
    private int _totalReads = 0;
    private int _totalWrites = 0;
    private int _hotCacheHits = 0;

    private const int DirectoryInternalType = 0xFF;
    private const int DirectoryInternalNumber = 0xFF;

    // Flat memory for PLC-5 family (used by ReadBytesPhysical / WriteBytesPhysical)
    private byte[] _flatMemory = Array.Empty<byte>();
    private readonly Dictionary<int, int> _flatOffsetByFileNumber = new();
    private readonly Dictionary<int, int> _flatFileTypeByNumber = new();
    private int _flatTotalBytes;

    // _family retained for WriteStString (string encoding differs for PLC-5)
    private PCCCEmulator.EmulationFamily _family = PCCCEmulator.EmulationFamily.SlcMicroLogix;
    private IPlcFamilyProfile _profile = new SlcFamilyProfile();

    // ─── Constructor ──────────────────────────────────────────────────────────
    /// <summary>
    /// Initializes the PLC memory with default file structures.
    /// If an embedded program (.bin resource) is found, it will be loaded
    /// and merged with the default files.
    /// </summary>
    public PlcMemory(PCCCEmulator.EmulationFamily family = PCCCEmulator.EmulationFamily.SlcMicroLogix)
        : this(PlcFamilyRegistry.Resolve(family)) { }

    /// <summary>
    /// Initializes the PLC memory from a family profile.
    /// This is the primary constructor — all others delegate here.
    /// </summary>
    public PlcMemory(IPlcFamilyProfile profile)
    {
        _profile = profile;
        _family  = profile.FamilyType;
        BuildDirectory();
        BuildDataFiles();
        BuildIoConfig();
        BuildDownloadSeed();
        LoadEmbeddedProgram();
        RebuildFlatMemory();   // ensures flat memory matches all files (including loaded program)

        InitializeHotCache();

        Logger.Always(this, _programLoaded
            ? "PCCC PLC memory initialized with embedded program."
            : "PCCC PLC memory initialized with default data.");
        Logger.Always(this, $"Hot cache initialized with {_hotCache.Count} files");
    }
    
    // ─── Hot Cache Initialization ─────────────────────────────────────────────
    /// <summary>
    /// Initializes the hot cache with frequently accessed files.
    /// Called once after all files are built/loaded.
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
    // DIRECTORY BUILDING (File 0)
    // =========================================================================
    //
    // The directory is a special file (type=1, number=0) that contains metadata
    // about all data and program files in the PLC. It has a 79-byte header
    // followed by 10-byte entries for each file.
    //
    // Directory size calculation:
    //   79 bytes header + 56 entries × 10 bytes = 639 bytes total
    //   56 entries = 32 data file slots + 24 program file slots
    //
    // Note: This 639 bytes is the size of File 0 (directory) itself.
    //       It is NOT the "Total Memory (Words): 714" shown in RSLogix.
    //       Total memory is the sum of user data table words from all data files.

    /// <summary>
    /// Builds the directory file (File 0). Creates the directory header,
    /// registers all data file entries, then all program file entries.
    /// </summary>
    private void BuildDirectory()
    {
        var cfg = _profile.BuildMemoryConfig();

#if INCLUDE_INACTIVE_FILES
        // Full layout: recalculate dirSize to include all files + inactive slots
        int totalEntries = cfg.NumDataFiles + cfg.NumProgramFiles + 9; // inactive padding
        int dirSize = 79 + totalEntries * 10;
#else
        int dirSize = cfg.DirectorySize;
#endif
        var dir = new byte[dirSize];

        WriteDirectoryHeader(dir, dirSize, cfg.NumProgramFiles, cfg.NumDataFiles);

        var (addr, dataPos) = WriteDataFileEntries(dir, 0, cfg);
        WriteProgramFileEntries(dir, addr, dataPos);

        _files[(DirectoryInternalType, DirectoryInternalNumber)] = dir;
    }

    private void WriteDirectoryHeader(byte[] dir, int dirSize, int numProgramFiles, int numDataFiles)
    {
        // Directory size at offset 70 (element 0x23 × 2)
        WriteU16(dir, 70, dirSize);
        WriteU16(dir, 46, numProgramFiles);
        WriteU16(dir, 52, numDataFiles);
    }

    private (int addr, int pos) WriteDataFileEntries(byte[] dir, int startAddr, PlcMemoryConfig cfg)
    {
        int addr = startAddr;
        int pos  = 79;

        void Register(byte type, int sizeBytes, byte fileNum, int elemSize = 2)
        {
            dir[pos]     = type;
            dir[pos + 1] = (byte)(sizeBytes & 0xFF);
            dir[pos + 2] = (byte)((sizeBytes >> 8) & 0xFF);
            dir[pos + 3] = fileNum;
            dir[pos + 4] = 0x00;
            dir[pos + 5] = (byte)elemSize;
            dir[pos + 6] = (byte)(addr & 0xFF);
            dir[pos + 7] = (byte)(addr >> 8);
            dir[pos + 8] = 0x00;
            dir[pos + 9] = 0x00;

            _flatOffsetByFileNumber[fileNum] = addr * 2;
            _flatFileTypeByNumber[fileNum]   = type;

            addr += sizeBytes / 2;
            _fileTypeByNumber[fileNum] = type;
            pos += 10;
        }

        // Write entries in file-number order, inserting empty slots for gaps.
        // This is required because the directory consumer (library, RSLogix) reads
        // numDataFiles sequential slots — gaps must be present as zero-size entries.
        var sorted = cfg.DataFiles.OrderBy(f => f.FileNumber).ToList();
        int nextExpected = 0;
        foreach (var f in sorted)
        {
            // Fill any gap with empty (inactive) entries
            while (nextExpected < f.FileNumber)
            {
                dir[pos + 3] = (byte)nextExpected;  // file number, rest stays zero
                pos += 10;
                nextExpected++;
            }
            Register(f.FileType, f.SizeBytes, f.FileNumber, f.ElemSize);
            nextExpected = f.FileNumber + 1;
        }

        return (addr, pos);
    }

    private void WriteProgramFileEntries(byte[] dir, int startAddr, int startPos)
    {
        int pos = startPos;
        // Starting byte offset for program files in flat memory (after all data files)
        int progFlatBase = startAddr * 2;
        // Maximum program file entries — determined by dirSize allocated in BuildDirectory.
        // Computed from remaining space in dir after data entries.
        int maxProgEntries = (dir.Length - pos) / 10;
        int progEntriesWritten = 0;

        // SYS file 0 — System data storage header (2 bytes)
        dir[pos]     = 0x01;
        dir[pos + 1] = 0x02;   // 2 bytes
        dir[pos + 2] = 0x00;
        dir[pos + 3] = 0x00;
        pos += 10;
        progEntriesWritten++;
        _fileTypeByNumber[0] = 0x01;
        _files[(0x01, 0)] = new byte[2];
        _bytesPerElement[(0x01, 0)] = 0;
        _flatOffsetByFileNumber[0] = progFlatBase;
        _flatFileTypeByNumber[0] = 0x01;
        progFlatBase += 2;

        // SYS file 1 — Reserved for future use (2 bytes)
        dir[pos]     = 0x01;
        dir[pos + 1] = 0x02;   // 2 bytes
        dir[pos + 2] = 0x00;
        dir[pos + 3] = 0x01;
        pos += 10;
        progEntriesWritten++;
        _fileTypeByNumber[1] = 0x01;
        _files[(0x01, 1)] = new byte[2];
        _bytesPerElement[(0x01, 1)] = 0;
        _flatOffsetByFileNumber[1] = progFlatBase;
        _flatFileTypeByNumber[1] = 0x01;
        progFlatBase += 2;

        // LAD files (active program files)
        // Sizes from .ACH disassembly (bytes)
        var actualLadSizes = new Dictionary<int, int>
        {
            {2, 757}, {3, 486}, {5, 972}, {8, 646}, {12, 1440},
            {15, 824}, {18, 646}, {19, 225}, {22, 903}, {23, 416}
        };
        int[] activeLad = { 2, 3, 5, 8, 12, 15, 18, 19, 22, 23 };

#if INCLUDE_INACTIVE_FILES
        // All LAD files 2-23 (active + inactive)
        int typeIndex = 0;
        for (int n = 2; n <= 23; n++)
        {
            bool active = Array.IndexOf(activeLad, n) >= 0;
            int sizeBytes = active && actualLadSizes.ContainsKey(n) ? actualLadSizes[n] : 0;
            byte fileType = (byte)(0x20 + typeIndex);
            typeIndex++;
            
            if (active && sizeBytes > 0)
            {
                _files[(fileType, n)] = new byte[sizeBytes];
                _bytesPerElement[(fileType, n)] = 0;  // Program files have no element size
            }
            _fileTypeByNumber[n] = fileType;
            
            dir[pos]     = fileType;
            dir[pos + 1] = (byte)(sizeBytes & 0xFF);
            dir[pos + 2] = (byte)((sizeBytes >> 8) & 0xFF);
            dir[pos + 3] = (byte)n;
            pos += 10;

            // Record flat memory offset for this program file
            _flatOffsetByFileNumber[n] = progFlatBase;
            _flatFileTypeByNumber[n] = fileType;
            progFlatBase += sizeBytes;
        }
#else
        // Active LAD files only
        int typeIndex = 0;
        foreach (int n in activeLad)
        {
            // Stop if no more room in directory for this family
            if (progEntriesWritten + 2 >= maxProgEntries) break;

            int sizeBytes = actualLadSizes[n];
            byte fileType = (byte)(0x20 + typeIndex);
            typeIndex++;

            _files[(fileType, n)] = new byte[sizeBytes];
            _bytesPerElement[(fileType, n)] = 0;  // Program files have no element size
            _fileTypeByNumber[n] = fileType;

            dir[pos]     = fileType;
            dir[pos + 1] = (byte)(sizeBytes & 0xFF);
            dir[pos + 2] = (byte)((sizeBytes >> 8) & 0xFF);
            dir[pos + 3] = (byte)n;
            pos += 10;
            progEntriesWritten++;

            _flatOffsetByFileNumber[n] = progFlatBase;
            _flatFileTypeByNumber[n] = fileType;
            progFlatBase += sizeBytes;
        }
#endif

        // Total flat memory size (in bytes) after all data and program files
        _flatTotalBytes = progFlatBase;
    }



    // =========================================================================
    // DATA FILES INITIALIZATION
    // =========================================================================

    /// <summary>
    /// Creates and initializes all data files with default values.
    /// Files created here include O0, I1, S2, B3, T4, C5, R6, N7, F8, and others.
    /// </summary>
    private void BuildDataFiles()
    {
        var cfg = _profile.BuildMemoryConfig();

        // Create all data files from the profile config
        foreach (var f in cfg.DataFiles)
            CreateDataFile(f.FileType, f.FileNumber, f.SizeBytes, f.ElemSize);

        // Initialize Status file (S2) with default values
        InitializeStatusFile();

        // Seed initial values (sample data, strings, etc.) via the profile
        _profile.SeedInitialValues(this);
    }


    private void InitializeStatusFile()
    {
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
        
        byte[] statusFile = _files[(0x84, 2)];
        // Write only as many words as the buffer can hold —
        // ML1400 S2 is 66 words (132 bytes) vs SLC 83 words (166 bytes).
        int wordsToWrite = Math.Min(s2.Length, statusFile.Length / 2);
        for (int i = 0; i < wordsToWrite; i++)
            WriteU16(statusFile, i * 2, s2[i]);
    }

    // =========================================================================
    // I/O CONFIGURATION (file type 0x60, file number 0)
    // =========================================================================
    //
    // This file is accessed via CMD=0x0F FNC=0xA2 (Protected Typed Logical Read)
    // by RSLinx to determine slot configuration and I/O module types.

    /// <summary>
    /// Builds the I/O configuration file (type 0x60, number 0).
    /// Contains slot count and I/O module information for each slot.
    /// </summary>
    private void BuildIoConfig()
    {
        // Buffer = 4 + 8×6 + 2 = 54 bytes minimum; padded to 64 for safety
        CreateDataFile(0x60, 0, 64, 2);
        byte[] io = _files[(0x60, 0)];

        io[0] = 8;  // Raw slot count → GetSlotCount() returns 7

        // InputBytes @ slot×6+4, OutputBytes @ slot×6+6
        // Slot 0 (CPU): both zero (default)
        io[1 * 6 + 4] = 2;     // Slot 1: 1746-IB16 (16 digital inputs)
        io[2 * 6 + 4] = 2;     // Slot 2: 1746-IB16 (16 digital inputs)
        io[3 * 6 + 4] = 2;     // Slot 3: 1746-IB16 (16 digital inputs)
        io[4 * 6 + 6] = 2;     // Slot 4: 1746-OB16 (16 digital outputs)
        io[5 * 6 + 6] = 2;     // Slot 5: 1746-OB16 (16 digital outputs)
        io[6 * 6 + 4] = 8;     // Slot 6: 1746-NI4 (4 analog inputs × 2 bytes)
    }

    // =========================================================================
    // DOWNLOAD SEED (file type 0x63, file number 0)
    // =========================================================================

    /// <summary>
    /// Builds the download seed file (type 0x63, number 0).
    /// DF1Comm.DownloadProgramData reads 4 bytes from this file and copies them
    /// into the FNC=0x88 init packet. Content 0x00000000 is sufficient.
    /// </summary>
    private void BuildDownloadSeed()
    {
        CreateDataFile(0x63, 0, 4, 4);
    }

    // =========================================================================
    // FLAT MEMORY MANAGEMENT (for PLC-5 family)
    // =========================================================================

    /// <summary>
    /// Rebuilds the flat memory image from individual file data.
    /// Called after initial construction, after loading embedded program,
    /// and after ResetToDefault.
    /// </summary>
    private void RebuildFlatMemory()
    {
        if (_flatTotalBytes == 0)
        {
            _flatMemory = Array.Empty<byte>();
            return;
        }
        _flatMemory = new byte[_flatTotalBytes];
        foreach (var (fileNum, byteOffset) in _flatOffsetByFileNumber)
        {
            if (!_flatFileTypeByNumber.TryGetValue(fileNum, out int fileType)) continue;
            if (!_files.TryGetValue((fileType, fileNum), out var fileData)) continue;
            int copyLen = Math.Min(fileData.Length, _flatTotalBytes - byteOffset);
            if (copyLen > 0)
                Array.Copy(fileData, 0, _flatMemory, byteOffset, copyLen);
        }
    }

    // =========================================================================
    // PUBLIC API (HIGH-PERFORMANCE)
    // =========================================================================

    /// <summary>
    /// Read bytes starting at raw byte offset within the specified file.
    /// </summary>
    /// <param name="fileType">DF1 file type code (e.g., 0x84 for Status)</param>
    /// <param name="fileNumber">File number (0-255)</param>
    /// <param name="element">Raw byte offset into the file array</param>
    /// <param name="lengthInBytes">Number of bytes to read</param>
    /// <param name="status">0=success, 2=file not found, 3=offset out of range</param>
    /// <returns>Byte array containing the requested data, or empty on error</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte[] ReadRaw(int fileType, int fileNumber, int element, int lengthInBytes, out int status)
    {
        Interlocked.Increment(ref _totalReads);
        
        // Fast path: hot cache (files 0,1,2,3,7,8)
        if (_hotCache.TryGetValue(fileNumber, out var hotEntry) && hotEntry.FileType == fileType)
        {
            Interlocked.Increment(ref _hotCacheHits);
            
            // Read lock prevents seeing partially written data from concurrent writes
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
        
        // Slow path: reader lock for all other files
        _rwLock.EnterReadLock();
        try
        {
            byte[]? data = Lookup(fileType, fileNumber);
            if (data == null)
            {
                status = 2;
                return Array.Empty<byte>();
            }
            
            if (element < 0 || element + lengthInBytes > data.Length)
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
    /// Write data to the specified file at the calculated offset.
    /// </summary>
    /// <param name="fileType">DF1 file type code</param>
    /// <param name="fileNumber">File number (0-255)</param>
    /// <param name="element">Element index (0-based)</param>
    /// <param name="subElement">Sub-element index (for structured files like T4)</param>
    /// <param name="lengthInBytes">Number of bytes to write</param>
    /// <param name="newData">Data to write</param>
    /// <returns>True if successful, false on error (file not found or offset out of range)</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Write(int fileType, int fileNumber, int element, int subElement,
                      int lengthInBytes, byte[] newData)
    {
        Interlocked.Increment(ref _totalWrites);
        
        // Write lock required for exclusive access
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

            // Synchronize flat memory if this file is mapped and we are in PLC-5 mode
            if (_flatOffsetByFileNumber.TryGetValue(fileNumber, out int flatBase))
            {
                int flatPos = flatBase + offset;
                int copyLen = Math.Min(lengthInBytes, _flatTotalBytes - flatPos);
                if (copyLen > 0)
                    Array.Copy(newData, 0, _flatMemory, flatPos, copyLen);
            }
            return true;
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Atomically reads a word, applies bitwise AND and OR masks, and writes the result back.
    /// The entire read-modify-write operation is performed under a single write lock,
    /// preventing race conditions when multiple clients write to the same address concurrently.
    /// </summary>
    public bool ReadModifyWrite(int fileType, int fileNumber, int element, int subElement,
                                int mask, int value)
    {
        _rwLock.EnterWriteLock();
        try
        {
            int bpe = _bytesPerElement.GetValueOrDefault((fileType, fileNumber), 2);
            int offset = element * bpe + subElement * 2;

            byte[]? data = Lookup(fileType, fileNumber);
            if (data == null || offset + 2 > data.Length) return false;

            // Read current word
            int current = data[offset] | (data[offset + 1] << 8);

            // Apply mask and value
            int newValue = (current & ~mask) | (value & mask);

            // Write back
            data[offset] = (byte)(newValue & 0xFF);
            data[offset + 1] = (byte)((newValue >> 8) & 0xFF);

            // Synchronize flat memory
            if (_flatOffsetByFileNumber.TryGetValue(fileNumber, out int flatBase))
            {
                int flatPos = flatBase + offset;
                if (flatPos + 2 <= _flatTotalBytes)
                {
                    _flatMemory[flatPos] = (byte)(newValue & 0xFF);
                    _flatMemory[flatPos + 1] = (byte)((newValue >> 8) & 0xFF);
                }
            }
            return true;
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Read-Modify-Write for FNC 0x26 using AND and OR masks.
    /// Formula: newValue = (current & andMask) | orMask
    /// </summary>
    public bool ReadModifyWriteWithMasks(int fileType, int fileNumber, int element, int subElement,
                                        int andMask, int orMask)
    {
        _rwLock.EnterWriteLock();
        try
        {
            int bpe = _bytesPerElement.GetValueOrDefault((fileType, fileNumber), 2);
            int offset = element * bpe + subElement * 2;

            byte[]? data = Lookup(fileType, fileNumber);
            if (data == null || offset + 2 > data.Length) return false;

            int current = data[offset] | (data[offset + 1] << 8);
            int newValue = (current & andMask) | orMask;

            data[offset] = (byte)(newValue & 0xFF);
            data[offset + 1] = (byte)((newValue >> 8) & 0xFF);

            // Synchronize flat memory
            if (_flatOffsetByFileNumber.TryGetValue(fileNumber, out int flatBase))
            {
                int flatPos = flatBase + offset;
                if (flatPos + 2 <= _flatTotalBytes)
                {
                    _flatMemory[flatPos] = (byte)(newValue & 0xFF);
                    _flatMemory[flatPos + 1] = (byte)((newValue >> 8) & 0xFF);
                }
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
            return hotEntry.BytesPerElement;
        
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
            return hotEntry.Data.Length;
        
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
    /// Used by HandleReadFileInfo (CMD=0x0F FNC=0x94).
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
    /// Gets performance statistics for debugging and monitoring.
    /// </summary>
    public void GetStats(out int totalReads, out int totalWrites, out int hotCacheHits, out double hitRate)
    {
        totalReads = Interlocked.CompareExchange(ref _totalReads, 0, 0);
        totalWrites = Interlocked.CompareExchange(ref _totalWrites, 0, 0);
        hotCacheHits = Interlocked.CompareExchange(ref _hotCacheHits, 0, 0);
        hitRate = totalReads > 0 ? (double)hotCacheHits / totalReads * 100.0 : 0.0;
    }

    /// <summary>
    /// Returns the raw directory file (type 0x01, number 0) bytes.
    /// </summary>
    public byte[] GetDirectory() 
        => _files[(DirectoryInternalType, DirectoryInternalNumber)];

    /// <summary>
    /// Writes raw bytes to a file at a given byte offset (no element/bpe conversion).
    /// Mirrors ReadRaw semantics. Used by FileWrite (0xAF) in the emulator.
    /// </summary>
    public bool WriteRaw(int fileType, int fileNumber, int byteOffset, int lengthInBytes, byte[] newData)
    {
        Interlocked.Increment(ref _totalWrites);
        _rwLock.EnterWriteLock();
        try
        {
            byte[]? data = Lookup(fileType, fileNumber);
            if (data == null) return false;
            if (byteOffset < 0 || byteOffset + lengthInBytes > data.Length) return false;
            Array.Copy(newData, 0, data, byteOffset, Math.Min(newData.Length, lengthInBytes));
            
            // Synchronize flat memory if this file is mapped
            if (_flatOffsetByFileNumber.TryGetValue(fileNumber, out int flatBase))
            {
                int flatPos = flatBase + byteOffset;
                int copyLen = Math.Min(lengthInBytes, _flatTotalBytes - flatPos);
                if (copyLen > 0)
                    Array.Copy(newData, 0, _flatMemory, flatPos, copyLen);
            }
            return true;
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Resets all data files to their default values (as after construction).
    /// Used by Initialize Memory command (0x0F/0x57).
    /// </summary>
    public void ResetToDefault()
    {
        _rwLock.EnterWriteLock();
        try
        {
            // O0: 12 bytes → all zero
            var o0 = Lookup(0x8B, 0);
            if (o0 != null) Array.Clear(o0, 0, o0.Length);
            
            // I1: 42 bytes → all zero
            var i1 = Lookup(0x8C, 1);
            if (i1 != null) Array.Clear(i1, 0, i1.Length);
            
            // S2: 166 bytes → re-initialise with known values
            var s2 = Lookup(0x84, 2);
            if (s2 != null) InitializeStatusFile();
            
            // B3: 28 bytes → reset to AA55, 0FF0 pattern
            var b3 = Lookup(0x85, 3);
            if (b3 != null)
            {
                Array.Clear(b3, 0, b3.Length);
                WriteU16(b3, 0, 0xAA55);
                WriteU16(b3, 2, 0x0FF0);
            }
            
            // T4: 468 bytes → all zero
            var t4 = Lookup(0x86, 4);
            if (t4 != null) Array.Clear(t4, 0, t4.Length);
            
            // C5: 6 bytes → all zero
            var c5 = Lookup(0x87, 5);
            if (c5 != null) Array.Clear(c5, 0, c5.Length);
            
            // R6: 12 bytes → all zero
            var r6 = Lookup(0x88, 6);
            if (r6 != null) Array.Clear(r6, 0, r6.Length);
            
            // N7: 148 bytes → reset to 123, 456, -789 pattern
            var n7 = Lookup(0x89, 7);
            if (n7 != null)
            {
                Array.Clear(n7, 0, n7.Length);
                WriteU16(n7, 0, 123);
                WriteU16(n7, 2, 456);
                WriteU16(n7, 4, -789);
            }
            
            // F8: 152 bytes → reset to 1.23, 4.56 pattern
            var f8 = Lookup(0x8A, 8);
            if (f8 != null)
            {
                Array.Clear(f8, 0, f8.Length);
                Array.Copy(BitConverter.GetBytes(1.23f), 0, f8, 0, 4);
                Array.Copy(BitConverter.GetBytes(4.56f), 0, f8, 4, 4);
            }
            
            // B9..B16, N17, B29..B31 → all zero
            for (int n = 9; n <= 16; n++)
            {
                var b = Lookup(0x85, n);
                if (b != null) Array.Clear(b, 0, b.Length);
            }
            var n17 = Lookup(0x89, 17);
            if (n17 != null) Array.Clear(n17, 0, n17.Length);
            
            // ST18:10 elements → reinitialize with "EMULATOR OK" at element 0, others empty
            var st18 = Lookup(0x8D, 18);
            if (st18 != null)
            {
                Array.Clear(st18, 0, st18.Length);
                WriteStString(st18, 0, "EMULATOR OK", _family);
            }
            
            for (int n = 29; n <= 31; n++)
            {
                var b = Lookup(0x85, n);
                if (b != null) Array.Clear(b, 0, b.Length);
            }
            
            // I/O config (file 0x60, 0) and download seed (0x63, 0) reset to default
            var io = Lookup(0x60, 0);
            if (io != null)
            {
                Array.Clear(io, 0, io.Length);
                io[0] = 8;
                io[1 * 6 + 4] = 2;
                io[2 * 6 + 4] = 2;
                io[3 * 6 + 4] = 2;
                io[4 * 6 + 6] = 2;
                io[5 * 6 + 6] = 2;
                io[6 * 6 + 4] = 8;
            }
            
            var seed = Lookup(0x63, 0);
            if (seed != null) Array.Clear(seed, 0, seed.Length);
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }

        RebuildFlatMemory();
        InitializeHotCache();
        Logger.Always(this, "Memory reset to default by Initialize Memory command.");
    }

    /// <summary>
    /// Writes a 16-bit unsigned integer to a byte array in little-endian format.
    /// </summary>
    /// <param name="buf">Target byte array</param>
    /// <param name="offset">Offset in bytes</param>
    /// <param name="value">16-bit value to write</param>
    public static void WriteU16(byte[] buf, int offset, int value)
    {
        buf[offset]     = (byte)(value & 0xFF);
        buf[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    /// <summary>
    /// Writes an ASCII string into an ST file element buffer at the given element offset.
    /// SLC 500 string format: bytes 0-1 = length (LE word), bytes 2-83 = char data.
    /// Strings longer than 82 characters are truncated.
    /// </summary>
    /// <param name="buf">ST file byte array</param>
    /// <param name="elementIndex">Element index (0-based)</param>
    /// <param name="value">String to write</param>
    public static void WriteStString(byte[] buf, int elementIndex, string value, PCCCEmulator.EmulationFamily family)
    {
        if (family == PCCCEmulator.EmulationFamily.Plc5)
        {
            // PLC-5 ST element: 88 bytes (44 words)
            // word 0 (byte 0-1): max length = 82 (constant)
            // word 1 (byte 2-3): current length
            // word 2+ (byte 4+): chars packed 2/word, low byte = char even index, high byte = char odd index
            const int elemSize = 88;
            const int maxChars = 82;
            int offset = elementIndex * elemSize;
            if (offset + elemSize > buf.Length) return;
            if (value.Length > maxChars) value = value[..maxChars];
            int len = value.Length;

            buf[offset] = 82;          // max length low byte
            buf[offset + 1] = 0;       // max length high byte
            buf[offset + 2] = (byte)(len & 0xFF);        // current length low
            buf[offset + 3] = (byte)((len >> 8) & 0xFF); // current length high
            for (int i = 0; i < len; i++)
            {
                int wordOffset = offset + 4 + (i / 2) * 2;
                if (i % 2 == 0)
                    buf[wordOffset] = (byte)value[i];     // low byte
                else
                    buf[wordOffset + 1] = (byte)value[i]; // high byte
            }
            // Zero-fill remaining char bytes (optional, array already zeroed)
        }
        else
        {
            // SLC format: 84 bytes, length word at offset, then sequential chars
            const int elemSize = 84;
            const int maxChars = 82;
            int offset = elementIndex * elemSize;
            if (offset + elemSize > buf.Length) return;
            if (value.Length > maxChars) value = value[..maxChars];
            int len = value.Length;
            buf[offset] = (byte)(len & 0xFF);
            buf[offset + 1] = (byte)((len >> 8) & 0xFF);
            for (int i = 0; i < len; i++)
                buf[offset + 2 + i] = (byte)value[i];
            // remaining already zero
        }
    }

    /// <summary>
    /// Reads bytes from flat physical memory.
    /// Used by ReadBytesPhysical (FNC 0x17) for PLC-5 upload.
    /// Address is a byte address in the flat memory space.
    /// Returns null if address or count is out of range.
    /// </summary>
    public byte[]? ReadPhysical(int byteAddress, int count)
    {
        if (count <= 0 || count > 240 || count % 2 != 0) return null;
        if (byteAddress < 0 || byteAddress + count > _flatTotalBytes) return null;
        
        _rwLock.EnterReadLock();
        try
        {
            var result = new byte[count];
            Array.Copy(_flatMemory, byteAddress, result, 0, count);
            return result;
        }
        finally { _rwLock.ExitReadLock(); }
    }

    /// <summary>
    /// Writes bytes to flat physical memory and syncs back to _files.
    /// Used by WriteBytesPhysical (FNC 0x18) for PLC-5 download.
    /// Returns false if address or count is out of range or data is odd-length.
    /// </summary>
    public bool WritePhysical(int byteAddress, byte[] data)
    {
        if (data == null || data.Length == 0 || data.Length > 238) return false;
        if (data.Length % 2 != 0) return false;
        if (byteAddress < 0 || byteAddress + data.Length > _flatTotalBytes) return false;

        _rwLock.EnterWriteLock();
        try
        {
            // Update flat memory
            Array.Copy(data, 0, _flatMemory, byteAddress, data.Length);

            // Sync back to each file that overlaps this address range
            foreach (var (fileNum, flatBase) in _flatOffsetByFileNumber)
            {
                if (!_flatFileTypeByNumber.TryGetValue(fileNum, out int ft)) continue;
                if (!_files.TryGetValue((ft, fileNum), out var fileData)) continue;

                int fileEnd      = flatBase + fileData.Length;
                int writeEnd     = byteAddress + data.Length;
                int overlapStart = Math.Max(byteAddress, flatBase);
                int overlapEnd   = Math.Min(writeEnd, fileEnd);

                if (overlapStart >= overlapEnd) continue;  // no overlap

                int srcOff  = overlapStart - byteAddress;
                int destOff = overlapStart - flatBase;
                int len     = overlapEnd - overlapStart;
                Array.Copy(data, srcOff, fileData, destOff, len);
            }
            return true;
        }
        finally { _rwLock.ExitWriteLock(); }
    }

    /// <summary>
    /// Returns total flat memory size in bytes. Used by emulator to build
    /// upload segment list for UploadAllRequest (FNC 0x53) reply.
    /// </summary>
    public int GetFlatMemorySize() => _flatTotalBytes;

    // =========================================================================
    // PRIVATE HELPERS
    // =========================================================================

    /// <summary>
    /// Looks up a file by its type and number.
    /// Handles type masking (0x7F) to ignore attribute bits (bit 7).
    /// </summary>
    /// <param name="fileType">File type code (may have high bit set for attributes)</param>
    /// <param name="fileNumber">File number (0-255)</param>
    /// <returns>File data byte array, or null if not found</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte[]? Lookup(int fileType, int fileNumber)
    {
        if (_files.TryGetValue((fileType, fileNumber), out var d)) return d;
        int t = fileType & 0x7F;  // Mask out attribute bit (bit 7)
        if (_files.TryGetValue((t, fileNumber), out d)) return d;
        if (_files.TryGetValue((t | 0x80, fileNumber), out d)) return d;
        return null;
    }

    /// <summary>
    /// Creates a new data file with the specified size and element size.
    /// </summary>
    /// <param name="fileType">DF1 file type code (e.g., 0x85 for Binary, 0x89 for Integer)</param>
    /// <param name="fileNumber">File number (0-255)</param>
    /// <param name="sizeBytes">Total file size in bytes</param>
    /// <param name="bytesPerElement">Number of bytes per element (2 for words, 4 for floats, 6 for timers)</param>
    private void CreateDataFile(byte fileType, int fileNumber, int sizeBytes, int bytesPerElement)
    {
        _files[(fileType, fileNumber)] = new byte[sizeBytes];
        _bytesPerElement[(fileType, fileNumber)] = bytesPerElement;
        _fileTypeByNumber[fileNumber] = fileType;
    }

    // =========================================================================
    // EMBEDDED PROGRAM LOADER
    // =========================================================================
    //
    // The .bin file format (generated by ach_to_df1.py from APS .ACH archive):
    //   - Magic number (2 bytes): 0xDF1A
    //   - Version (1 byte)
    //   - Processor type (4 bytes): 0x49 for SLC 5/03
    //   - Series/revision (1 byte)
    //   - RAM size in KB (1 byte)
    //   - Family name (8 bytes, ASCII)
    //   - Bulletin length (4 bytes)
    //   - Bulletin string (variable, UTF-8)
    //   - Timestamp (8 bytes, binary DateTime)
    //   - File count (4 bytes)
    //   - For each file: fileNumber (4 bytes), fileType (4 bytes), 
    //     numberOfBytes (4 bytes), dataLength (4 bytes), fileData (dataLength bytes)

    /// <summary>
    /// Loads program from embedded .bin resource (generated by ach_to_df1.py).
    /// If a file already exists (from BuildDataFiles), the binary content is merged
    /// (overwriting existing data). This allows the embedded program to override
    /// default values while preserving file structure.
    /// </summary>
    private void LoadEmbeddedProgram()
    {
        // Embedded program (e.g. DBU550.bin) is SLC-format only.
        // Skip for non-SLC families to avoid file size mismatches.
        if (_family != PCCCEmulator.EmulationFamily.SlcMicroLogix)
        {
            Logger.Always(this, $"Skipping embedded program for family {_family}.");
            return;
        }

        var assembly = Assembly.GetExecutingAssembly();

        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(".bin", StringComparison.OrdinalIgnoreCase));

        if (resourceName == null)
        {
            Logger.Always(this, "No embedded program found. Using default data.");
            return;
        }
        
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            Logger.Always(this, "Failed to load embedded resource.");
            return;
        }
        
        var data = new byte[stream.Length];
        stream.ReadExactly(data, 0, data.Length);
        
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
        
        Logger.Always(this, $"{resourceName}");
        Console.WriteLine($"      Size={data.Length} Magic=0x{magic:X4} Ver={version} Type=0x{procType:X2} {family} {bulletin}");
        Console.WriteLine($"      Files={fileCount} TS={DateTime.FromBinary(timestamp):yyyy-MM-dd HH:mm:ss}");
        
        int dataLoaded = 0, progLoaded = 0;
        
        for (int i = 0; i < fileCount; i++)
        {
            int fileNumber = br.ReadInt32();
            int fileType = br.ReadInt32();
            int numberOfBytes = br.ReadInt32();
            int dataLength = br.ReadInt32();
            byte[] fileData = br.ReadBytes(dataLength);
            
            // Skip directory and SYS files (already built from BuildDirectory)
            if ((fileType == 0 && fileNumber == 0) || (fileType == 0x01 && fileNumber <= 1))
                continue;
            
            if (fileType >= 0x20 && fileType <= 0x3F)
                progLoaded++;
            else if (fileType >= 0x80 && fileType <= 0x9F)
                dataLoaded++;
            
            if (!_files.ContainsKey((fileType, fileNumber)))
            {
                // New file (not in default set) — allocate and store
                int elemSize = fileType switch
                {
                    0x8B or 0x8C or 0x86 or 0x87 or 0x88 => 6,  // I/O, Timer, Counter, Control
                    0x8A => 4,  // Float
                    0x8D => 84, // String (ST): 2-byte length + 82 chars
                    _ => 2       // Default word
                };
                _files[(fileType, fileNumber)] = new byte[numberOfBytes];
                _bytesPerElement[(fileType, fileNumber)] = elemSize;
                _fileTypeByNumber[fileNumber] = fileType;
                Array.Copy(fileData, 0, _files[(fileType, fileNumber)], 0, Math.Min(fileData.Length, numberOfBytes));
            }
            else
            {
                // File already exists (from BuildDataFiles) - merge binary content
                var dest = _files[(fileType, fileNumber)];
                if (dest.Length != numberOfBytes)
                {
                    Logger.Always(this, $"File (0x{fileType:X2},{fileNumber}) size mismatch: " +
                        $"binary={numberOfBytes}, allocated={dest.Length}");
                }
                
                int copyLen = Math.Min(fileData.Length, dest.Length);
                if (copyLen < numberOfBytes)
                {
                    Logger.Always(this, $"File (0x{fileType:X2},{fileNumber}) truncated: " +
                        $"binary wants {numberOfBytes} bytes, destination has {dest.Length} bytes");
                }
                Array.Copy(fileData, 0, dest, 0, copyLen);
            }
        }
        
        Console.WriteLine($"      Loaded: {dataLoaded} data files, {progLoaded} program files");
        _programLoaded = true;
        // Rebuild flat memory to incorporate the newly loaded (or merged) files
        RebuildFlatMemory();
    }

    // ─── Seed helpers (called by IPlcFamilyProfile.SeedInitialValues) ────────

    /// <summary>Writes a 16-bit value at a byte offset within a data file.</summary>
    public void WriteU16Direct(int fileType, int fileNum, int byteOffset, ushort value)
    {
        if (_files.TryGetValue((fileType, fileNum), out var buf) && byteOffset + 1 < buf.Length)
        {
            buf[byteOffset]     = (byte)(value & 0xFF);
            buf[byteOffset + 1] = (byte)(value >> 8);
        }
    }

    /// <summary>Writes a 32-bit float at a byte offset within a data file.</summary>
    public void WriteFloatDirect(int fileType, int fileNum, int byteOffset, float value)
    {
        if (_files.TryGetValue((fileType, fileNum), out var buf) && byteOffset + 3 < buf.Length)
            Array.Copy(BitConverter.GetBytes(value), 0, buf, byteOffset, 4);
    }

    /// <summary>Writes a 32-bit signed long at a byte offset within a data file.</summary>
    public void WriteLongDirect(int fileType, int fileNum, int byteOffset, int value)
    {
        if (_files.TryGetValue((fileType, fileNum), out var buf) && byteOffset + 3 < buf.Length)
            Array.Copy(BitConverter.GetBytes(value), 0, buf, byteOffset, 4);
    }

    /// <summary>Writes a string into an SLC/PLC-5 ST file element.</summary>
    public void WriteStStringDirect(int fileType, int fileNum, int elementIndex,
        string value, PCCCEmulator.EmulationFamily family)
    {
        if (_files.TryGetValue((fileType, fileNum), out var buf))
            WriteStString(buf, elementIndex, value, family);
    }

    // ─── IDisposable ──────────────────────────────────────────────────────────
    public void Dispose()
    {
        _rwLock?.Dispose();
    }
}

