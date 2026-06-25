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
// IPlcFamilyProfile — extensibility contract for PLC family emulation
//
// PURPOSE
// -------
// Mirrors the ILinkTransport pattern for transport extensibility.
// Every aspect of emulated PLC behaviour that differs between families is
// expressed as a method on this interface.  Adding a new family requires
// only:
//   1. Create a class that implements IPlcFamilyProfile.
//   2. Register it in PlcFamilyRegistry.
//   3. Add the --family name string to Emulator Program.cs.
//
// No changes to PCCCEmulator.cs or PlcMemory.cs are needed.
//
// IMPLEMENTED BY
// --------------
//   SlcFamilyProfile  — SLC 500 / MicroLogix (default)
//   Ml1400FamilyProfile — MicroLogix 1400 (1766-L32BWA)
//   Plc5FamilyProfile — PLC-5 (memory layout placeholder; update from hardware)
// =============================================================================

/// <summary>
/// Specifies a single data file entry in a PLC family's memory layout.
/// </summary>
public record DataFileSpec(
    byte   FileType,    // PCCC file type code (e.g. 0x89 = N, 0x8A = F)
    byte   FileNumber,  // File number (0-255)
    int    SizeBytes,   // Total file size in bytes (elements × elemSize)
    int    ElemSize     // Bytes per element (e.g. 2 for N, 4 for F/L, 6 for T/C/R)
)
{
    /// <summary>Convenience constructor for 2-byte-per-element files (N, B, S, O, I).</summary>
    public DataFileSpec(byte fileType, byte fileNumber, int sizeBytes)
        : this(fileType, fileNumber, sizeBytes, 2) { }
}

/// <summary>
/// Memory layout descriptor passed from IPlcFamilyProfile to PlcMemory.
/// Replaces all if/switch on EmulationFamily inside PlcMemory.
/// </summary>
public record PlcMemoryConfig(
    int                        DirectorySize,
    int                        NumDataFiles,
    int                        NumProgramFiles,
    IReadOnlyList<DataFileSpec> DataFiles,
    /// <summary>Content to seed into the first string element (for self-test).</summary>
    string                     DefaultStringContent = "EMULATOR OK"
);

/// <summary>
/// Extensibility contract for PLC family behaviour.
/// Implement this interface to add a new emulated PLC family without
/// modifying PCCCEmulator.cs or PlcMemory.cs.
/// </summary>
public interface IPlcFamilyProfile
{
    // ── Identity ─────────────────────────────────────────────────────────────

    /// <summary>Human-readable name shown in console and logs (e.g. "SLC 5/04").</summary>
    string Name { get; }

    /// <summary>Enum tag used by PCCCEmulator for the few remaining family checks.</summary>
    PCCCEmulator.EmulationFamily FamilyType { get; }

    // ── GetStatus response (CMD 0x06 FNC 0x03) ───────────────────────────────

    /// <summary>
    /// Builds the complete GetStatus payload for the given processor mode.
    /// Called once at construction; the result is cached and patched in-place
    /// by <see cref="PatchModeInPayload"/> when the mode changes at runtime.
    /// </summary>
    byte[] BuildGetStatusPayload(ProcessorMode mode);

    /// <summary>
    /// Updates the mode byte(s) in an already-built GetStatus payload.
    /// Called by PCCCEmulator.UpdateProcessorMode() instead of rebuilding.
    /// </summary>
    void PatchModeInPayload(byte[] payload, ProcessorMode mode);

    /// <summary>
    /// True when S2:1 (word 1 of the Status file) should also be updated with
    /// the raw mode byte.  SLC/ML write the mode to S2:1; PLC-5 and ML1400 do not.
    /// </summary>
    bool WritesModeToStatusFile { get; }

    // ── Upload / download protocol variant ───────────────────────────────────

    /// <summary>
    /// True = use PLC-5 Procedure 2 upload response format (spec §7-33).
    /// False = use SLC segment-info format (max chunk + total memory, 4 bytes).
    /// </summary>
    bool UsesPlc5UploadProtocol { get; }

    // ── Memory layout ─────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the complete memory layout for this family.
    /// PlcMemory calls this once at construction and iterates the DataFiles list.
    /// </summary>
    PlcMemoryConfig BuildMemoryConfig();

    /// <summary>
    /// Seeds initial values into data files after they have been created.
    /// Called by PlcMemory.BuildDataFiles() after all CreateDataFile() calls.
    /// Use this to write sample N7, F8 values, seed strings, etc.
    /// </summary>
    void SeedInitialValues(PlcMemory memory);
}
