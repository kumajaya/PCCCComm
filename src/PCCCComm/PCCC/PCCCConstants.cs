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

namespace PCCCComm.Pccc;

/// <summary>
/// Centralized constants for the PCCC (Programmable Controller Communications Command) protocol.
/// Reference: Allen-Bradley Publication 1770-6.5.16 (DF1 Protocol and Command Set Reference Manual).
/// </summary>
public static class PCCCConstants
{
    // ========================================================================
    // Command Codes (CMD) – see Chapter 7, page 7-2 ff.
    // ========================================================================
    public static class Cmd
    {
        /// <summary>Diagnostic Status (0x06). Reads processor status and type.</summary>
        public const byte DiagnosticStatus = 0x06;

        /// <summary>Protected Write (0x0F). Most PCCC read/write commands use this CMD.</summary>
        public const byte ProtectedWrite = 0x0F;

        /// <summary>Unprotected Read (0x01). Reads from PLC-2 compatibility file.</summary>
        public const byte UnprotectedRead = 0x01;

        /// <summary>Unprotected Write (0x08). Writes to PLC-2 compatibility file.</summary>
        public const byte UnprotectedWrite = 0x08;

        /// <summary>Output Control (0x07). Used with FNC 0x00 (disable) or 0x01 (enable).</summary>
        public const byte OutputControl = 0x07;
    }

    // ========================================================================
    // Function Codes (FNC) – used with CMD = 0x0F (Protected Write)
    // ========================================================================
    public static class Fnc
    {
        // --- Mode Control ----------------------------------------------------
        /// <summary>Set Run mode for SLC processors (0x80).</summary>
        public const byte SetRunModeSLC = 0x80;
        /// <summary>Set Run mode for MicroLogix 1000 (0x3A).</summary>
        public const byte SetRunModeML = 0x3A;
        /// <summary>Set Program mode for SLC (0x80).</summary>
        public const byte SetProgramModeSLC = 0x80;
        /// <summary>Set Program mode for MicroLogix (0x3A).</summary>
        public const byte SetProgramModeML = 0x3A;
        /// <summary>Generic Set CPU mode (0x3A).</summary>
        public const byte SetCpuMode = 0x3A;
        /// <summary>Get Run mode / diagnostic status (0x03).</summary>
        public const byte GetRunMode = 0x03;

        // --- Read/Write ------------------------------------------------------
        /// <summary>Protected Typed Logical Read – word range (0xA1).</summary>
        public const byte ReadWordRange = 0xA1;
        /// <summary>Protected Typed Logical Read – with sub-element (0xA2).</summary>
        public const byte ReadSubElement = 0xA2;
        /// <summary>Protected Typed Logical Write – word range (0xAA).</summary>
        public const byte WriteWordRange = 0xAA;
        /// <summary>Protected Typed Logical Write – bit (0xAB).</summary>
        public const byte WriteBit = 0xAB;
        /// <summary>Read-Modify-Write (0x26).</summary>
        public const byte ReadModifyWrite = 0x26;

        // --- Upload/Download & Edit Resource ---------------------------------
        /// <summary>Initialize download (0x88).</summary>
        public const byte DownloadInit = 0x88;
        /// <summary>Secure sole access (0x11).</summary>
        public const byte SecureAccess = 0x11;
        /// <summary>Release sole access (0x12).</summary>
        public const byte ReleaseAccess = 0x12;
        /// <summary>Download complete (0x52).</summary>
        public const byte DownloadComplete = 0x52;
        /// <summary>Upload all request (0x53).</summary>
        public const byte UploadAll = 0x53;
        /// <summary>Upload complete (0x55).</summary>
        public const byte UploadComplete = 0x55;

        // --- Forces & Outputs -----------------------------------------------
        /// <summary>Disable forces (0x41).</summary>
        public const byte DisableForces = 0x41;
        /// <summary>Disable outputs (0x00, with CMD=0x07).</summary>
        public const byte DisableOutputs = 0x00;
        /// <summary>Enable outputs (0x01, with CMD=0x07).</summary>
        public const byte EnableOutputs = 0x01;

        // --- I/O Configuration -----------------------------------------------
        /// <summary>Get slot count / I/O config (0xA2).</summary>
        public const byte GetSlotCount = 0xA2;
        /// <summary>Get I/O config (0xA2, same FNC but different data).</summary>
        public const byte GetIOConfig = 0xA2;

        // --- Echo -----------------------------------------------------------
        /// <summary>Echo command (0x00).</summary>
        public const byte Echo = 0x00;
    }

    // ========================================================================
    // Status Codes (STS) – see Chapter 8, page 8-2 ff.
    // ========================================================================
    public static class Sts
    {
        /// <summary>Command executed successfully.</summary>
        public const byte Success = 0x00;
        /// <summary>Extended status byte follows (STS = 0xF0).</summary>
        public const byte ExtStsPresent = 0xF0;

        // Local STS (link layer errors)
        public const byte DstOutOfBuffer = 0x01;
        public const byte CannotGuaranteeDelivery = 0x02;
        public const byte DuplicateToken = 0x03;
        public const byte LocalPortDisconnected = 0x04;
        public const byte AppLayerTimeout = 0x05;
        public const byte DuplicateNode = 0x06;
        public const byte StationOffline = 0x07;
        public const byte HardwareFault = 0x08;

        // Remote STS (application layer errors)
        public const byte IllegalCommandOrFormat = 0x10;
        public const byte HostProblem = 0x20;
        public const byte RemoteNodeMissing = 0x30;
        public const byte HardwareFaultRemote = 0x40;
        public const byte AddressingProblem = 0x50;
        public const byte CommandProtection = 0x60;
        public const byte ProcessorInProgramMode = 0x70;
        public const byte CompatibilityModeMissing = 0x80;
        public const byte RemoteCannotBuffer = 0x90;
        public const byte WaitAck = 0xA0;
        public const byte DownloadProblem = 0xB0;
        public const byte WaitAck2 = 0xC0;
        // 0xD0, 0xE0 reserved
    }

    /// <summary>
    /// Extended Status Codes (EXT STS). Values are defined as 256 + extStsByte
    /// to avoid collision with standard STS codes.
    /// Reference: AB 1770-6.5.16, Chapter 8, page 8-4.
    /// </summary>
    public static class ExtSts
    {
        public const int IllegalFieldValue = 0x101;
        public const int LessLevelsThanMinimum = 0x102;
        public const int MoreLevelsThanSystemSupports = 0x103;
        public const int SymbolNotFound = 0x104;
        public const int ImproperSymbolFormat = 0x105;
        public const int AddressNotUsable = 0x106;
        public const int FileWrongSize = 0x107;
        public const int SituationChanged = 0x108;
        public const int DataOrFileTooLarge = 0x109;
        public const int TransactionSizeTooLarge = 0x10A;
        public const int AccessDenied = 0x10B;
        public const int ResourceNotAvailable = 0x10C;
        public const int ResourceAlreadyAvailable = 0x10D;
        public const int CommandCannotBeExecuted = 0x10E;
        public const int HistogramOverflow = 0x10F;
        public const int NoAccess = 0x110;
        public const int IllegalDataType = 0x111;
        public const int InvalidParameter = 0x112;
        public const int AddressDeleted = 0x113;
        public const int UnknownFailure = 0x114;
        public const int DataConversionError = 0x115;
        public const int ScannerCantCommunicate = 0x116;
        public const int TypeMismatch = 0x117;
        public const int ModuleResponseInvalid = 0x118;
        public const int DuplicateLabel = 0x119;
        public const int FileOpenAnotherNode = 0x11A;
        public const int ProgramOwnerAnotherNode = 0x11B;
        public const int DataTableProtection = 0x11E;
        public const int TemporaryInternalProblem = 0x11F;
        public const int RemoteRackFault = 0x122;
        public const int Timeout = 0x123;
        public const int UnknownError = 0x124;
    }

    // ========================================================================
    // Processor Type Codes (from diagnostic status reply)
    // Reference: AB 1770-6.5.16, Chapter 10 (various tables)
    // ========================================================================
    public enum ProcessorTypeCode : byte
    {
        FixedSLC500 = 0x1A,
        SLC501 = 0x18,
        SLC502 = 0x25,
        SLC503 = 0x49,
        SLC504 = 0x5B,
        SLC505 = 0x78,
        ML1000 = 0x58,
        ML1100 = 0x9C,
        ML1200 = 0x88,
        ML1500LSP = 0x89,
        ML1500LRP = 0x8C,
    }

    // ========================================================================
    // SLC 500 Data File Types – see Chapter 7, page 7-17
    // ========================================================================
    public enum SlcFileTypeCode : byte
    {
        Output     = 0x82,
        OutputAlt  = 0x8B,
        Input      = 0x83,
        InputAlt   = 0x8C,
        Status     = 0x84,
        Binary     = 0x85,
        Timer      = 0x86,
        Counter    = 0x87,
        Control    = 0x88,
        Integer    = 0x89,
        Float      = 0x8A,
        String     = 0x8D,
        Ascii      = 0x8E,
        Long       = 0x91,
        Message    = 0x92,
        Pid        = 0x93,
        Pls        = 0x94,
    }

    public static class SlcFileTypeInfo
    {
        /// <summary>Returns the size in bytes of one element of the given file type.</summary>
        public static int GetBytesPerElement(SlcFileTypeCode type) => type switch
        {
            SlcFileTypeCode.Timer or SlcFileTypeCode.Counter or SlcFileTypeCode.Control => 6,
            SlcFileTypeCode.Float or SlcFileTypeCode.Long => 4,
            SlcFileTypeCode.String => 84,
            SlcFileTypeCode.Message => 50,
            SlcFileTypeCode.Pid => 46,
            SlcFileTypeCode.Pls => 12,
            _ => 2
        };

        /// <summary>Returns the human-readable type name (e.g., "N", "F", "ST").</summary>
        public static string GetTypeName(SlcFileTypeCode type) => type switch
        {
            SlcFileTypeCode.Output or SlcFileTypeCode.OutputAlt => "O",
            SlcFileTypeCode.Input or SlcFileTypeCode.InputAlt => "I",
            SlcFileTypeCode.Status => "S",
            SlcFileTypeCode.Binary => "B",
            SlcFileTypeCode.Timer => "T",
            SlcFileTypeCode.Counter => "C",
            SlcFileTypeCode.Control => "R",
            SlcFileTypeCode.Integer => "N",
            SlcFileTypeCode.Float => "F",
            SlcFileTypeCode.String => "ST",
            SlcFileTypeCode.Ascii => "A",
            SlcFileTypeCode.Long => "L",
            SlcFileTypeCode.Message => "MG",
            SlcFileTypeCode.Pid => "PD",
            SlcFileTypeCode.Pls => "PLS",
            _ => "??"
        };
    }

    // ========================================================================
    // DF1 Link-Layer Payload Limits (from original VB code and AB spec)
    // ========================================================================
    public static class Df1Limits
    {
        /// <summary>Maximum read payload bytes (most PLCs) = 236.</summary>
        public const int MaxReadPayloadBytes = 236;
        /// <summary>Maximum write payload bytes = 164.</summary>
        public const int MaxWritePayloadBytes = 164;
        /// <summary>Maximum read for string files (ST) = 168 bytes (two elements).</summary>
        public const int MaxStringReadBytes = 168;
        /// <summary>Maximum read for timer/counter files = 234 bytes (multiple of 6).</summary>
        public const int MaxTimerCounterReadBytes = 234;
        /// <summary>Maximum read for SLC 5/02 = 0x50 (80 bytes).</summary>
        public const int MaxSlc502ReadBytes = 0x50;
        /// <summary>Maximum data for Echo command = 243 bytes.</summary>
        public const int MaxEchoDataBytes = 243;
        /// <summary>Maximum reply size for diagnostic status = 244 bytes.</summary>
        public const int MaxDiagnosticReplyBytes = 244;
        /// <summary>Maximum bytes per physical read = 240.</summary>
        public const int MaxPhysicalReadBytes = 240;
        /// <summary>Maximum bytes per physical write = 238.</summary>
        public const int MaxPhysicalWriteBytes = 238;
        public const int MaxReadModifyWriteBodyBytes = 243;
    }

    // ========================================================================
    // Offsets within inner PCCC frame (after DLE stuffing removed)
    // Standard inner frame: DST, SRC, CMD, STS, TNS_LO, TNS_HI, [FNC], DATA...
    // ========================================================================
    public static class ResponseOffsets
    {
        public const int CmdIndex = 2;
        public const int StsIndex = 3;
        public const int TnsLo = 4;
        public const int TnsHi = 5;
        public const int FuncIndex = 6;   // Present only for commands with function code

        /// <summary>Offsets for diagnostic status reply (CMD 0x06, FNC 0x03).</summary>
        public static class DiagnosticStatus
        {
            /// <summary>Offset of processor type within DATA payload (inner frame offset 9 → 3).</summary>
            public const int ProcessorType = 3;
            
            /// <summary>Offset of mode code within DATA payload (inner frame offset 24 → 18).</summary>
            public const int ModeCode = 18;
        }

        /// <summary>Offsets in the file directory (FileZeroData) after reading.</summary>
        public static class FileDirectory
        {
            public const int NumberOfProgramFilesLo = 46;
            public const int NumberOfProgramFilesHi = 47;
            public const int NumberOfDataFilesLo = 52;
            public const int NumberOfDataFilesHi = 53;
            public const int StartOffsetDefault = 79;
            public const int StartOffsetSlc502Ml1000 = 93;
            public const int StartOffsetMl1100Ml1500 = 103;
            public const int BytesPerEntryDefault = 10;
            public const int BytesPerEntrySlc502 = 8;
        }
    }
}
