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

namespace PCCCComm;

/// <summary>
/// Parsed address structure used by ReadRawData/WriteRawData/ParseAddress.
/// BitNumber: 99 = no bit-level access. 0–15 = specific bit.
/// BytesPerElements: element size in bytes for the file type.
/// </summary>
public struct DataAddress
{
    public int FileNumber;
    public int FileType;
    public int Element;
    public int SubElement;
    public int BitNumber;
    public int BytesPerElements;
}

/// <summary>
/// I/O card configuration for a single rack slot.
/// Returned by GetIOConfig, GetSLCIOConfig, and GetML1500IOConfig.
/// </summary>
public struct IOConfig
{
    public int InputBytes;
    public int OutputBytes;
    public int CardCode;
}

/// <summary>
/// Data file metadata returned by GetDataMemory.
/// </summary>
public struct DataFileDetails
{
    public string FileType { get; set; }
    public int FileNumber { get; set; }
    public int NumberOfElements { get; set; }
}

/// <summary>
/// Program/ladder file container used by Upload/Download operations.
/// </summary>
public struct PLCFileDetails
{
    public int FileType { get; set; }
    public int FileNumber { get; set; }
    public int NumberOfBytes { get; set; }
    public byte[] Data { get; set; }
}
