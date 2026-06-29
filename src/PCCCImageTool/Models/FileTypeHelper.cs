using System;

namespace PCCCImageTool.Models;

public static class FileTypeHelper
{
    /// <summary>
    /// Returns human-readable name for PCCC file type code.
    /// Supports SLC, MicroLogix, and common PLC-5 file types.
    /// </summary>
    public static string GetFileTypeName(int fileType)
    {
        return fileType switch
        {
            0x82 => "O (Output)",
            0x83 => "I (Input)",
            0x84 => "S (Status)",
            0x85 => "B (Binary)",
            0x86 => "T (Timer)",
            0x87 => "C (Counter)",
            0x88 => "R (Control)",
            0x89 => "N (Integer)",
            0x8A => "F (Float)",
            0x8B => "O (Output)",
            0x8C => "I (Input)",
            0x8D => "ST (String)",
            0x8E => "A (ASCII)",
            0x8F => "BCD",
            0x91 => "L (Long)",
            0x92 => "MG (Message)",
            0x93 => "PD (PID)",
            0x94 => "PLS (Limit Switch)",
            0xA4 => "Data Monitor",
            >= 0x01 and <= 0x1F => "SYS (System)",     // System files
            >= 0x20 and <= 0x3F => "LAD (Program)",    // Range for LAD
            >= 0x40 and <= 0x5F => "SYS (System)",     // Another system range
            >= 0x60 and <= 0x7F => "I/O Config",       // I/O configuration files
            >= 0x80 and <= 0x9F => "Data",             // Data files
            _ => $"0x{fileType:X2}"
        };
    }

    /// <summary>
    /// Returns bytes per element for given file type (SLC/MicroLogix).
    /// Defaults to 2 (word).
    /// </summary>
    public static int GetBytesPerElement(int fileType)
    {
        return fileType switch
        {
            0x86 or 0x87 or 0x88 => 6, // Timer, Counter, Control
            0x8A => 4,                 // Float
            0x8D => 84,                // String
            0xA4 => 40,                // Data Monitor File – 40 bytes per element
            _ => 2
        };
    }
}
