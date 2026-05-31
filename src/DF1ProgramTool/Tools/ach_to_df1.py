#!/usr/bin/env python3
"""
ach_to_df1.py — Convert APS .ACH archive to DF1ProgramTool .bin format
==========================================================================
Reads an SLC 500 .ACH archive (APS / Advanced Programming Software) and
writes a .bin file in the binary format consumed by DF1ProgramTool's
DownloadFromFileAsync() / LoadFromFileAndValidate().

Verified against DBU550.ACH (SLC 5/03, 1747-L532E):
  21 data files  |  1594 bytes data
  10 LAD files   |  7315 bytes program
  Grand total    |  8909 payload bytes

Automatically reconstructs File 0 (Directory) and SYS files which are
not stored in .ACH archives.

Usage
-----
  # Minimal — output written next to the source file:
  python ach_to_df1.py DBU550.ACH

  # Specify output path:
  python ach_to_df1.py DBU550.ACH --out DBU550.bin

  # Supply PLC metadata (embedded in .bin header, checked on download):
  python ach_to_df1.py DBU550.ACH \\
      --processor-type 0x49 \\
      --bulletin "5/03" \\
      --family SLC \\
      --series-rev 1 \\
      --ram-kb 16

  # Explicit Bit / Integer file-number map (when program skips file numbers):
  python ach_to_df1.py DBU550.ACH \\
      --bit-files  3,9,10,11,12,13,14,15,16,29,30,31 \\
      --int-files  7,17

  # Dump parsed contents to stdout without writing a file:
  python ach_to_df1.py DBU550.ACH --dump

  # Quiet:
  python ach_to_df1.py DBU550.ACH --quiet

.bin file format (DF1ProgramTool v1)
--------------------------------------
  [0x00] uint16  magic = 0xDF1A
  [0x02] uint8   version = 1
  [0x03] int32   processorType
  [0x07] uint8   seriesRevision
  [0x08] uint8   ramKb
  [0x09] 8 bytes familyTag (ASCII, space-padded)
  [0x11] int32   bulletinLength
  [0x15] N bytes bulletin (UTF-8)
  [0x15+N] int64  timestamp (DateTime.UtcNow.ToBinary())
  ...    int32   fileCount
  per file:
    int32  fileNumber
    int32  fileType       (DF1 type code)
    int32  numberOfBytes
    int32  dataLength
    N bytes data
  [end-36] uint32  CRC32  (IEEE 802.3, over all bytes before this)
  [end-32] 32 bytes SHA256 (over all bytes before CRC32+SHA256)

ACH format (reverse-engineered)
---------------------------------
  Global header: 5 × uint32 LE → offsets of blocks 1-5
  Block 2 (data files):
    O0, I1 at fixed offsets (no descriptor)
    Other files: [aps_type:u16][elem_count:u16] descriptor then raw words
  Block 3 (program files):
    Concatenated LAD data, split by per-LAD byte sizes from PLC directory.
    Remainder bytes after last LAD file are ignored.

APS type codes → DF1 file types:
  0=O(0x8B)  1=I(0x8C)  2=S(0x84)  3=B(0x85)  4=T(0x86)
  5=C(0x87)  6=R(0x88)  7=N(0x89)  8=F(0x8A)

LAD file type codes:
  Standard: 0x20 + (n - 2)  (used by this script)
"""

import argparse
import hashlib
import struct
import sys
import zlib
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import List, Optional, Tuple, Dict


# ---------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------

DF1BIN_MAGIC   = 0xDF1A
DF1BIN_VERSION = 1

# APS type code → (DF1 file type code, words per element)
APS_TYPE_MAP = {
    0: (0x8B, 3),  # O  Output      3 words/elem
    1: (0x8C, 3),  # I  Input       3 words/elem
    2: (0x84, 1),  # S  Status      1 word/elem
    3: (0x85, 1),  # B  Bit         1 word/elem
    4: (0x86, 3),  # T  Timer       3 words/elem
    5: (0x87, 3),  # C  Counter     3 words/elem
    6: (0x88, 3),  # R  Control     3 words/elem
    7: (0x89, 1),  # N  Integer     1 word/elem
    8: (0x8A, 2),  # F  Float       2 words/elem
}

# Fixed-position files in Block 2: {aps_type: (offset_in_block2, elements)}
FIXED_FILES = {
    0: (0x0000, 2),   # O0 — 2 elem × 3 words = 6 words = 12 bytes
    1: (0x000C, 7),   # I1 — 7 elem × 3 words = 21 words = 42 bytes
}

# Descriptor scan begins after the fixed-file region
DESCRIPTOR_SCAN_START = 0x003A

# Type codes that appear at most once via descriptor  (O/I handled as fixed)
SINGLE_OCCURRENCE = {2, 4, 5, 6, 8}

# Type codes to skip in descriptor scan (already handled as fixed)
DESCRIPTOR_SKIP   = {0, 1}

MAX_ELEM = 512

# Default Bit-file number ordering (3 → 9-16 → 29-31 is the SLC 500 convention;
# use --bit-files to override when a program skips file numbers).
DEFAULT_BIT_POOL = [3, 9, 10, 11, 12, 13, 14, 15, 16, 29, 30, 31,
                    32, 33, 34, 35, 36, 37, 38, 39, 40]

# Default Integer-file number ordering
DEFAULT_INT_POOL = [7, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28]

# LAD file-number → file type code (standard formula: 0x20 + (n-2))
def lad_type_code(lad_num: int) -> int:
    return 0x20 + (lad_num - 2)

# Active LAD files and their byte sizes (from PlcMemory.actualLadSizes).
# This table is the ground truth for splitting Block 3.
# Override with --lad-sizes if your ACH has different active files.
DEFAULT_LAD_LAYOUT = [
    (2,   757),
    (3,   486),
    (5,   972),
    (8,   646),
    (12, 1440),
    (15,  824),
    (18,  646),
    (19,  225),
    (22,  903),
    (23,  416),
]

# SYS file content (2 bytes each)
# SYS0 and SYS1 are system files not stored in .ACH archives.
# DF1Emulator initializes these with zeros, so we do the same.
SYS0_DATA = bytes([0x00, 0x00])
SYS1_DATA = bytes([0x00, 0x00])


# ---------------------------------------------------------------------------
# Data structures
# ---------------------------------------------------------------------------

@dataclass
class PlcFileEntry:
    file_number:    int
    file_type:      int     # DF1 type code
    number_of_bytes: int
    data:           bytes


@dataclass
class ConversionResult:
    processor_name:  str
    data_files:      List[PlcFileEntry]
    program_files:   List[PlcFileEntry]
    block3_remainder: int    # bytes after last LAD file (informational)

    @property
    def all_files(self) -> List[PlcFileEntry]:
        return self.data_files + self.program_files


# ---------------------------------------------------------------------------
# ACH parser
# ---------------------------------------------------------------------------

def parse_ach(
    raw:          bytes,
    bit_pool:     List[int],
    int_pool:     List[int],
    lad_layout:   List[tuple],
) -> ConversionResult:
    """Parse raw .ACH bytes; return data and program file entries."""

    if len(raw) < 20:
        raise ValueError(f"File too small ({len(raw)} bytes); not a valid .ACH")

    # --- Global TOC ---
    b1, b2, b3, b4 = struct.unpack_from("<4I", raw, 0)
    _check_offset(raw, b1, "Block 1")
    _check_offset(raw, b2, "Block 2")
    _check_offset(raw, b3, "Block 3")

    proc_name = _read_cstr_ascii(raw, b1, 32)

    block2 = raw[b2:b3]
    block3 = raw[b3:b4]

    data_files    = _parse_data_files(block2, bit_pool, int_pool)
    program_files = _parse_program_files(block3, lad_layout)
    remainder     = len(block3) - sum(s for _, s in lad_layout)

    return ConversionResult(
        processor_name  = proc_name,
        data_files      = data_files,
        program_files   = program_files,
        block3_remainder = remainder,
    )


def _parse_data_files(
    block2:   bytes,
    bit_pool: List[int],
    int_pool: List[int],
) -> List[PlcFileEntry]:

    files       = []
    global_used = set()

    # --- Fixed files (O0, I1) ---
    for aps_type, (data_off, elem) in FIXED_FILES.items():
        df1_type, wpe = APS_TYPE_MAP[aps_type]
        nbytes = elem * wpe * 2
        if data_off + nbytes > len(block2):
            raise ValueError(
                f"Fixed file (APS type {aps_type}) extends past Block 2 boundary.")
        files.append(PlcFileEntry(
            file_number     = aps_type,        # O=0, I=1
            file_type       = df1_type,
            number_of_bytes = nbytes,
            data            = bytes(block2[data_off: data_off + nbytes]),
        ))
        global_used.add(aps_type)

    # --- Descriptor scan ---
    type_count  = {}
    bit_idx     = 0
    int_idx     = 0
    offset      = DESCRIPTOR_SCAN_START

    while offset < len(block2) - 4:
        aps_type  = struct.unpack_from("<H", block2, offset)[0]
        elem      = struct.unpack_from("<H", block2, offset + 2)[0]

        if (aps_type not in APS_TYPE_MAP
                or aps_type in DESCRIPTOR_SKIP
                or not (1 <= elem <= MAX_ELEM)):
            offset += 2
            continue

        seen = type_count.get(aps_type, 0)
        if aps_type in SINGLE_OCCURRENCE and seen >= 1:
            offset += 2
            continue

        df1_type, wpe = APS_TYPE_MAP[aps_type]
        nbytes   = elem * wpe * 2
        data_off = offset + 4
        data_end = data_off + nbytes

        if data_end > len(block2):
            offset += 2
            continue

        # Assign file number
        if aps_type == 3:    # Bit
            fn, bit_idx = _next_fn(bit_pool, bit_idx, global_used)
        elif aps_type == 7:  # Integer
            fn, int_idx = _next_fn(int_pool, int_idx, global_used)
        else:
            fn = aps_type    # standard: file number == type code

        global_used.add(fn)
        type_count[aps_type] = seen + 1

        files.append(PlcFileEntry(
            file_number     = fn,
            file_type       = df1_type,
            number_of_bytes = nbytes,
            data            = bytes(block2[data_off: data_end]),
        ))
        offset = data_end

    files.sort(key=lambda f: f.file_number)
    return files


def _parse_program_files(
    block3:     bytes,
    lad_layout: List[tuple],
) -> List[PlcFileEntry]:

    files  = []
    offset = 0
    idx = 0

    for lad_num, size in lad_layout:
        if offset + size > len(block3):
            raise ValueError(
                f"LAD {lad_num}: expected {size} bytes at block3+{offset:#x} "
                f"but block3 is only {len(block3)} bytes.")
        data = bytes(block3[offset: offset + size])
        file_type = 0x20 + idx
        files.append(PlcFileEntry(
            file_number     = lad_num,
            file_type       = file_type,
            number_of_bytes = size,
            data            = data,
        ))
        offset += size
        idx = idx + 1

    return files


# ---------------------------------------------------------------------------
# Directory Builder (File 0) - reconstructed from metadata
# ---------------------------------------------------------------------------

def build_directory_file(
    data_files: List[PlcFileEntry],
    program_files: List[PlcFileEntry],
) -> bytes:
    """
    Build File 0 (Directory) according to AB Publication 1770-6.5.16.
    
    The directory contains a header (79 bytes) followed by a 10-byte entry
    for each file. This file is NOT stored in .ACH archives and must be
    reconstructed from the file list.
    
    Directory entry format (10 bytes):
      offset +0: file type code
      offset +1: size bytes (low)
      offset +2: size bytes (high)
      offset +3: file number
      offset +4: attribute (0 = normal)
      offset +5: element size in bytes
      offset +6: base address (low)
      offset +7: base address (high)
      offset +8: reserved (0)
      offset +9: reserved (0)
    """
    num_sys_files = 2   # SYS0 and SYS1
    num_data_files = len(data_files)
    num_prog_files = len(program_files)
    total_entries = num_sys_files + num_data_files + num_prog_files
    
    # Directory size: 79 bytes header + (total_entries * 10) bytes table
    dir_size = 79 + (total_entries * 10)
    directory = bytearray(dir_size)
    
    # --- Header fields (per AB spec) ---
    # Offset 70-71: directory size in bytes (little-endian)
    _write_u16(directory, 70, dir_size)
    
    # Offset 46-47: number of program files (including SYS)
    _write_u16(directory, 46, num_prog_files + num_sys_files)
    
    # Offset 52-53: number of data files
    _write_u16(directory, 52, num_data_files)
    
    # Offsets 0-45: reserved/unknown - leave as zeros
    # Offsets 72-78: reserved/unknown - leave as zeros
    
    pos = 79          # Start of file table
    addr = 0          # Running base address in WORDS
    
    # -------------------------------------------------------------------
    # 1. SYS file 0 (file number 0, type 0x01)
    # -------------------------------------------------------------------
    _write_dir_entry(directory, pos,
        file_type=0x01,
        size_bytes=2,
        file_number=0,
        elem_size=2,
        addr=addr
    )
    addr += 1         # 2 bytes = 1 word
    pos += 10
    
    # -------------------------------------------------------------------
    # 2. SYS file 1 (file number 1, type 0x01)
    # -------------------------------------------------------------------
    _write_dir_entry(directory, pos,
        file_type=0x01,
        size_bytes=2,
        file_number=1,
        elem_size=2,
        addr=addr
    )
    addr += 1         # 2 bytes = 1 word
    pos += 10
    
    # -------------------------------------------------------------------
    # 3. Data files (sorted by file_number)
    # -------------------------------------------------------------------
    for f in sorted(data_files, key=lambda x: x.file_number):
        # Determine element size in bytes based on file type
        if f.file_type in (0x86, 0x87, 0x88):  # T, C, R
            elem_size = 6
        elif f.file_type == 0x8A:               # F (float)
            elem_size = 4
        else:
            elem_size = 2                       # O, I, S, B, N
        
        words = f.number_of_bytes // 2
        
        _write_dir_entry(directory, pos,
            file_type=f.file_type,
            size_bytes=f.number_of_bytes,
            file_number=f.file_number,
            elem_size=elem_size,
            addr=addr
        )
        addr += words
        pos += 10
    
    # -------------------------------------------------------------------
    # 4. Program files (LAD files)
    # -------------------------------------------------------------------
    for f in sorted(program_files, key=lambda x: x.file_number):
        words = f.number_of_bytes // 2
        
        _write_dir_entry(directory, pos,
            file_type=f.file_type,
            size_bytes=f.number_of_bytes,
            file_number=f.file_number,
            elem_size=0,      # LAD files have no fixed element size
            addr=addr
        )
        addr += words
        pos += 10
    
    return bytes(directory)


def _write_dir_entry(
    buf: bytearray,
    offset: int,
    file_type: int,
    size_bytes: int,
    file_number: int,
    elem_size: int,
    addr: int
):
    """Write a 10-byte directory entry."""
    buf[offset]     = file_type & 0xFF
    buf[offset + 1] = size_bytes & 0xFF
    buf[offset + 2] = (size_bytes >> 8) & 0xFF
    buf[offset + 3] = file_number & 0xFF
    buf[offset + 4] = 0x00          # attribute: normal file
    buf[offset + 5] = elem_size & 0xFF
    buf[offset + 6] = addr & 0xFF
    buf[offset + 7] = (addr >> 8) & 0xFF
    buf[offset + 8] = 0x00
    buf[offset + 9] = 0x00


def _write_u16(buf: bytearray, offset: int, value: int):
    """Write 16-bit little-endian value to bytearray."""
    buf[offset] = value & 0xFF
    buf[offset + 1] = (value >> 8) & 0xFF


# ---------------------------------------------------------------------------
# .bin serialiser (mirrors ProgramTransferService.SaveToFile)
# ---------------------------------------------------------------------------

def build_bin(
    result:       ConversionResult,
    processor_type: int,
    family:       str,
    series_rev:   int,
    ram_kb:       int,
    bulletin:     str,
) -> bytes:
    """Serialise ConversionResult to DF1ProgramTool .bin format.
    
    The .bin file includes:
      - File 0 (Directory) - reconstructed
      - SYS0 and SYS1 - system files (set to zeros)
      - All data files from the ACH
      - All program files (LAD) from the ACH
    """
    import io
    
    # Build missing system files
    directory = build_directory_file(result.data_files, result.program_files)
    sys0 = SYS0_DATA
    sys1 = SYS1_DATA
    
    # Build complete file list in the order DF1Comm expects:
    # 1. Directory (file number 0, special type 0)
    # 2. SYS file 0 (file number 0, type 0x01)
    # 3. SYS file 1 (file number 1, type 0x01)
    # 4. All data files
    # 5. All program files
    all_files = [
        PlcFileEntry(file_number=0, file_type=0,
                     number_of_bytes=len(directory), data=directory),
        PlcFileEntry(file_number=0, file_type=0x01,
                     number_of_bytes=len(sys0), data=sys0),
    ]
    all_files.extend(result.data_files)
    all_files.extend(result.program_files)
    
    buf = io.BytesIO()
    
    # --- Header ---
    buf.write(struct.pack("<H", DF1BIN_MAGIC))           # uint16
    buf.write(struct.pack("<B", DF1BIN_VERSION))         # uint8
    buf.write(struct.pack("<i", processor_type))         # int32
    buf.write(struct.pack("<B", series_rev & 0xFF))      # uint8
    buf.write(struct.pack("<B", ram_kb & 0xFF))          # uint8
    
    # Family tag: 8 ASCII bytes, space-padded, right-truncated
    tag = (family or "PLC").encode("ascii", errors="replace")
    tag = (tag + b"        ")[:8]
    buf.write(tag)
    
    # Bulletin: length-prefixed UTF-8
    bul = (bulletin or "").encode("utf-8")
    buf.write(struct.pack("<i", len(bul)))
    buf.write(bul)
    
    # Timestamp: DateTime.UtcNow.ToBinary() in .NET = 64-bit UTC ticks
    DOTNET_EPOCH_TICKS = 621_355_968_000_000_000   # 1970-01-01 in .NET ticks
    TICKS_PER_SECOND   = 10_000_000
    now_utc = datetime.now(timezone.utc)
    py_secs = now_utc.timestamp()
    net_ticks = int(py_secs * TICKS_PER_SECOND) + DOTNET_EPOCH_TICKS
    # Set Kind=UTC: bit 62 set (0x4000_0000_0000_0000)
    net_binary = net_ticks | 0x4000_0000_0000_0000
    buf.write(struct.pack("<q", net_binary))              # int64
    
    # File count
    buf.write(struct.pack("<i", len(all_files)))          # int32
    
    # Per-file records
    for f in all_files:
        buf.write(struct.pack("<i", f.file_number))       # int32
        buf.write(struct.pack("<i", f.file_type))         # int32
        buf.write(struct.pack("<i", f.number_of_bytes))   # int32
        buf.write(struct.pack("<i", len(f.data)))         # int32
        buf.write(f.data)
    
    content = buf.getvalue()
    
    # --- Trailer: CRC32 + SHA256 ---
    crc32 = _crc32_ieee(content)
    sha   = hashlib.sha256(content).digest()
    
    return content + struct.pack("<I", crc32) + sha


def _crc32_ieee(data: bytes) -> int:
    """CRC32 matching C# Crc32.Compute (IEEE 802.3, polynomial 0xEDB88320)."""
    return zlib.crc32(data) & 0xFFFF_FFFF


# ---------------------------------------------------------------------------
# Dump helpers
# ---------------------------------------------------------------------------

def dump(result: ConversionResult, processor_type: int, bulletin: str):
    print(f"\n{'='*72}")
    print(f"  ACH PARSE RESULT")
    print(f"{'='*72}")
    print(f"  Processor name : {result.processor_name or '(not stored in .ACH)'}")
    print(f"  Processor type : 0x{processor_type:02X}" if processor_type else
          f"  Processor type : (not supplied — use --processor-type)")
    print(f"  Bulletin       : {bulletin or '(not supplied)'}")
    print(f"  Data files     : {len(result.data_files)}")
    print(f"  Program files  : {len(result.program_files)}")
    print(f"  Block3 remainder (ignored): {result.block3_remainder} bytes")
    print()
    
    type_names = {
        0x8B:"O", 0x8C:"I", 0x84:"S", 0x85:"B", 0x86:"T",
        0x87:"C", 0x88:"R", 0x89:"N", 0x8A:"F",
    }
    
    # Calculate directory size for display
    dir_size = 79 + (2 + len(result.data_files) + len(result.program_files)) * 10
    
    print(f"  {'File':>6}  {'Type':>6}  {'Bytes':>6}  Label")
    print(f"  {'─'*46}")
    print(f"  F0       0x00    {dir_size:>5}  DIR (Directory, reconstructed)")
    print(f"  F0       0x01         2  SYS0 (system, zeros)")
    print(f"  F1       0x01         2  SYS1 (system, zeros)")
    
    for f in result.data_files:
        ltr = type_names.get(f.file_type, "?")
        print(f"  F{f.file_number:<5}  0x{f.file_type:02X}    {f.number_of_bytes:>5}  "
              f"{ltr}{f.file_number}")
    print()
    for f in result.program_files:
        print(f"  LAD{f.file_number:<3}  0x{f.file_type:02X}    {f.number_of_bytes:>5}  "
              f"LAD {f.file_number}")
    
    total_data = sum(f.number_of_bytes for f in result.data_files)
    total_prog = sum(f.number_of_bytes for f in result.program_files)
    
    print(f"\n  Directory size: {dir_size:6d} bytes (reconstructed)")
    print(f"  SYS files     :      4 bytes (2+2, zeros)")
    print(f"  Data  total   : {total_data:6d} bytes")
    print(f"  Prog  total   : {total_prog:6d} bytes")
    print(f"  ─────────────────────────")
    print(f"  Grand total   : {dir_size + 4 + total_data + total_prog:6d} bytes")
    print()


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _next_fn(pool: List[int], idx: int, used: set) -> tuple:
    """Return (next_file_number, new_idx) from pool, skipping used numbers."""
    while idx < len(pool):
        fn = pool[idx]
        idx += 1
        if fn not in used:
            return fn, idx
    # Fallback beyond pool
    candidate = max(pool, default=32) + 1
    while candidate in used:
        candidate += 1
    return candidate, idx


def _check_offset(raw: bytes, offset: int, name: str):
    if offset == 0 or offset >= len(raw):
        raise ValueError(
            f"ACH {name} offset 0x{offset:06X} is out of range "
            f"(file size = {len(raw)}).")


def _read_cstr_ascii(raw: bytes, offset: int, max_len: int) -> str:
    end = offset
    limit = min(offset + max_len, len(raw))
    while end < limit and raw[end] != 0:
        end += 1
    return raw[offset:end].decode("ascii", errors="replace").strip()


def _parse_int_list(s: str) -> List[int]:
    """Parse '3,9,10,29,30,31' → [3, 9, 10, 29, 30, 31]."""
    return [int(x.strip()) for x in s.split(",") if x.strip()]


def _parse_lad_sizes(s: str) -> List[tuple]:
    """Parse '2:757,3:486,5:972' → [(2,757),(3,486),(5,972)]."""
    result = []
    for token in s.split(","):
        token = token.strip()
        if ":" not in token:
            raise ValueError(f"Invalid --lad-sizes token '{token}' (expected n:size)")
        lad, size = token.split(":", 1)
        result.append((int(lad.strip()), int(size.strip())))
    return result


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

def main():
    ap = argparse.ArgumentParser(
        description="Convert APS .ACH archive to DF1ProgramTool .bin format.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__,
    )
    ap.add_argument("ach_file", type=Path,
                    help="Source .ACH file")
    ap.add_argument("--out", type=Path, default=None,
                    help="Output .bin path (default: <stem>.bin next to source)")

    # PLC metadata — embedded in .bin header; checked by DF1ProgramTool on download
    ap.add_argument("--processor-type", default="0x49",
                    help="Hex processor type e.g. 0x49 for SLC 5/03 (default: 0x49)")
    ap.add_argument("--bulletin", default="5/03",
                    help='Bulletin string e.g. "5/03" (default: 5/03)')
    ap.add_argument("--family", default="SLC",
                    help='Family tag: SLC | MicroLogix | PLC5 | PLC (default: SLC)')
    ap.add_argument("--series-rev", type=int, default=1,
                    help="Series/revision byte (default: 1)")
    ap.add_argument("--ram-kb", type=int, default=16,
                    help="RAM size in KB (default: 16)")

    # File-number overrides
    ap.add_argument("--bit-files", default=None, metavar="N,N,...",
                    help=(
                        "Ordered Bit file numbers in appearance order. "
                        "Default: 3,9,10,11,12,13,14,15,16,29,30,31,... "
                        "Use when the program skips file numbers "
                        "(e.g. B16 → B29).  Example: 3,9,10,11,12,13,14,15,16,29,30,31"
                    ))
    ap.add_argument("--int-files", default=None, metavar="N,N,...",
                    help=(
                        "Ordered Integer file numbers in appearance order. "
                        "Default: 7,17,18,...  Example: 7,17"
                    ))

    # LAD layout override
    ap.add_argument("--lad-sizes", default=None, metavar="N:SIZE,...",
                    help=(
                        "Active LAD files and their sizes in bytes, in order. "
                        "Default matches PlcMemory.actualLadSizes for DBU550. "
                        "Example: 2:757,3:486,5:972,8:646,12:1440,15:824,"
                        "18:646,19:225,22:903,23:416"
                    ))

    ap.add_argument("--dump", action="store_true",
                    help="Print parsed file list; do not write output")
    ap.add_argument("--quiet", "-q", action="store_true",
                    help="Suppress informational output")
    args = ap.parse_args()

    # --- Validate inputs ---
    ach_path: Path = args.ach_file
    if not ach_path.exists():
        print(f"ERROR: file not found: {ach_path}", file=sys.stderr)
        sys.exit(1)

    try:
        proc_type = int(args.processor_type, 0)
    except ValueError:
        print(f"ERROR: invalid --processor-type '{args.processor_type}'", file=sys.stderr)
        sys.exit(1)

    bit_pool = _parse_int_list(args.bit_files) if args.bit_files else DEFAULT_BIT_POOL
    int_pool = _parse_int_list(args.int_files) if args.int_files else DEFAULT_INT_POOL

    try:
        lad_layout = _parse_lad_sizes(args.lad_sizes) if args.lad_sizes else DEFAULT_LAD_LAYOUT
    except ValueError as e:
        print(f"ERROR in --lad-sizes: {e}", file=sys.stderr)
        sys.exit(1)

    out_path = args.out or ach_path.with_suffix(".bin")

    def log(msg: str):
        if not args.quiet:
            print(msg)

    # --- Parse ---
    log(f"\nach_to_df1 — {ach_path.name}")
    log("─" * 50)

    try:
        raw = ach_path.read_bytes()
        result = parse_ach(raw, bit_pool, int_pool, lad_layout)
    except Exception as e:
        print(f"ERROR parsing {ach_path.name}: {e}", file=sys.stderr)
        sys.exit(1)

    log(f"  Processor name : {result.processor_name or '(none)'}")
    log(f"  Data files     : {len(result.data_files)}")
    log(f"  Program files  : {len(result.program_files)}")
    log(f"  Block3 tail    : {result.block3_remainder} bytes (ignored)")

    if args.dump:
        dump(result, proc_type, args.bulletin)
        return

    # --- Serialise ---
    try:
        bin_bytes = build_bin(
            result,
            processor_type = proc_type,
            family         = args.family,
            series_rev     = args.series_rev,
            ram_kb         = args.ram_kb,
            bulletin       = args.bulletin,
        )
    except Exception as e:
        print(f"ERROR building .bin: {e}", file=sys.stderr)
        sys.exit(1)

    # --- Write ---
    try:
        out_path.write_bytes(bin_bytes)
    except OSError as e:
        print(f"ERROR writing {out_path}: {e}", file=sys.stderr)
        sys.exit(1)

    total_data = sum(f.number_of_bytes for f in result.data_files)
    total_prog = sum(f.number_of_bytes for f in result.program_files)
    dir_size = 79 + (2 + len(result.data_files) + len(result.program_files)) * 10

    log(f"  Directory size : {dir_size} bytes (reconstructed)")
    log(f"  SYS files      : 4 bytes (2+2, zeros)")
    log(f"  Data total     : {total_data} bytes")
    log(f"  Program total  : {total_prog} bytes")
    log(f"  .bin size      : {len(bin_bytes):,} bytes")
    log(f"\n  Output: {out_path}")
    log("  Done.\n")


if __name__ == "__main__":
    main()
