using System;

namespace PCCCImageTool.Utilities;

/// <summary>
/// Small CRC32 helper (IEEE 802.3 polynomial 0xEDB88320).
/// Returns unsigned 32-bit CRC.
/// </summary>
public static class Crc32
{
    private static readonly uint[] Table = CreateTable();

    private static uint[] CreateTable()
    {
        const uint poly = 0xEDB88320u;
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int j = 0; j < 8; j++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ poly : crc >> 1;
            table[i] = crc;
        }
        return table;
    }

    /// <summary>
    /// Compute CRC32 for the provided byte array.
    /// </summary>
    public static uint Compute(byte[] data)
    {
        if (data == null || data.Length == 0) return 0u;
        uint crc = 0xFFFFFFFFu;
        foreach (var b in data)
            crc = (crc >> 8) ^ Table[(crc ^ b) & 0xFF];
        return ~crc;
    }
}
