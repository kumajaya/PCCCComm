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

using System.Collections.ObjectModel;

namespace PCCCComm.Handlers;

/// <summary>
/// Protocol handler interface for different PLC families (SLC, MicroLogix, PLC-5, etc.).
/// Each handler implements the PCCC commands specific to a processor family.
/// </summary>
public interface IPlcHandler
{
    // ─── Mode Control ─────────────────────────────────────────────────────
    
    /// <summary>Places the processor in Run mode.</summary>
    void SetRunMode();
    
    /// <summary>Places the processor in Program mode.</summary>
    void SetProgramMode();
    
    /// <summary>Sets the CPU mode using a raw mode value.</summary>
    int SetCpuMode(byte modeValue);
    
    /// <summary>Returns 1 if the processor is in Run mode, 0 if not in Run mode,
    /// or -1 if the diagnostic status could not be retrieved.</summary>
    int GetRunMode();
    
    /// <summary>Disables forces on the processor.</summary>
    int DisableForces();

    /// <summary>Enables forces on the processor.</summary>
    void EnableForces();

    /// <summary>Clears all forces from the processor.</summary>
    void ClearForces();

    // ─── Read / Write ─────────────────────────────────────────────────────
    
    /// <summary>Reads data from the specified address and returns it as strings.</summary>
    string[] ReadAny(string startAddress, int numberOfElements);
    
    /// <summary>Reads a single element from the specified address.</summary>
    string ReadAny(string startAddress);
    
    /// <summary>Reads integer values from the specified address.</summary>
    int[] ReadInt(string startAddress, int numberOfElements);

    /// <summary>Reads data from the specified address and returns it as double.</summary>
    double[] ReadAnyValues(string startAddress, int numberOfElements);
    
    /// <summary>Reads a single element from the specified address.</summary>
    double ReadAnyValues(string startAddress);
    
    /// <summary>Performs a read-modify-write operation on multiple addresses.</summary>
    int ReadModifyWrite(string[] addresses, ushort[] andMasks, ushort[] orMasks);
    
    /// <summary>Writes an integer value to the specified address.</summary>
    string WriteData(string startAddress, int dataToWrite);
    
    /// <summary>Writes multiple integer values to the specified address.</summary>
    int WriteData(string startAddress, int numberOfElements, int[] dataToWrite);
    
    /// <summary>Writes a float value to the specified address.</summary>
    int WriteData(string startAddress, float dataToWrite);
    
    /// <summary>Writes multiple float values to the specified address.</summary>
    int WriteData(string startAddress, int numberOfElements, float[] dataToWrite);
    
    /// <summary>Writes a string to an ST file or word-packed integer file.</summary>
    int WriteData(string startAddress, string dataToWrite);

    // ─── Upload / Download ────────────────────────────────────────────────
    
    /// <summary>Uploads the entire program and data from the PLC.</summary>
    Collection<PLCFileDetails> UploadProgramData();
    
    /// <summary>Downloads a program to the PLC.</summary>
    void DownloadProgramData(Collection<PLCFileDetails> plcFiles);

    // ─── I/O Configuration ────────────────────────────────────────────────
    
    /// <summary>Returns the number of slots in the chassis.</summary>
    int GetSlotCount();
    
    /// <summary>Returns I/O configuration for all slots.</summary>
    IOConfig[] GetIOConfig();

    // ─── Diagnostic ───────────────────────────────────────────────────────
    
    /// <summary>Returns the processor type code.</summary>
    int GetProcessorType();
    
    /// <summary>Returns raw diagnostic status data.</summary>
    byte[]? GetDiagnosticStatusRaw();

    // ─── Data Memory ──────────────────────────────────────────────────────
    
    /// <summary>Returns a list of data files present in the processor.</summary>
    DataFileDetails[] GetDataMemory();
    
    /// <summary>Returns data file information specific to MicroLogix 1500.</summary>
    DataFileDetails[] GetML1500DataMemory();

    // File management
    ushort OpenFile(int fileNumber, int fileType);
    void CloseFile(ushort tag);
    byte[] FileRead(ushort tag, int offset, int length);
    int FileWrite(ushort tag, int offset, byte[] data);

    // Edit resource
    void GetEditResource();
    void ReturnEditResource();

    // Upload/Download mode
    void UploadAllRequest();
    void UploadCompleted();
    void DownloadAllRequest();
    void DownloadCompleted();

    // Configuration
    void ApplyPortConfiguration();
    void InitializeMemory();

    // Diagnostic
    byte[] ReadDiagnosticCounters();
    void ResetDiagnosticCounters();
    byte ReadLinkParameters();
    void SetLinkParameters(byte maxAddress);

    // Testing
    byte[] Echo(byte[] data);
}
