using System;

namespace PCCCImageTool.Models;

/// <summary>
/// Result of structure-only comparison (file existence and size)
/// </summary>
public class StructureCompareResult
{
    public int FileNumber { get; set; }
    public int FileType { get; set; }
    public string FileTypeName { get; set; } = "";
    public bool FileExistsInPlc { get; set; }
    public bool FileExistsInFile { get; set; }
    public bool SizeMatches { get; set; }
    public int? FileSizeBytes { get; set; }
    public int? PlcSizeBytes { get; set; }

    public string SizeDisplay => FileSizeBytes.HasValue ? $"{FileSizeBytes}" : "(missing)";
    public string PlcSizeDisplay => PlcSizeBytes.HasValue ? $"{PlcSizeBytes}" : "(missing)";

    public bool StructureMatch => FileExistsInPlc && FileExistsInFile && SizeMatches;

    public string StructureStatus => StructureMatch ? "✅ Match" : "❌ Mismatch";
    public string MismatchReason =>
        !FileExistsInPlc ? "File missing in PLC" :
        !FileExistsInFile ? "File missing in backup" :
        !SizeMatches ? $"Size mismatch: backup={FileSizeBytes}, PLC={PlcSizeBytes}" : "";
}

/// <summary>
/// Result of full comparison (structure + data via CRC32)
/// </summary>
public class FullCompareResult : StructureCompareResult
{
    public uint? FileCrc32 { get; set; }
    public uint? PlcCrc32 { get; set; }
    public bool DataMatches { get; set; }

    public string DataStatus => 
        !FileExistsInPlc || !FileExistsInFile || !SizeMatches ? "N/A" :
        DataMatches ? "✅ Match" : $"❌ Mismatch (CRC: file=0x{FileCrc32:X8}, PLC=0x{PlcCrc32:X8})";
}
