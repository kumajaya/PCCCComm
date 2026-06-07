using System;
using System.Linq;
using System.Text;
using System.Collections.Generic;

using PCCCImageTool.Models;

namespace PCCCImageTool.Services;

public static class FrameDecoder
{
    private const string Indent = "          "; // 10 spaces prefix

    /// <summary>
    /// Remove DLE stuffing: 0x10 0x10 → single 0x10.
    /// </summary>    
    private static byte[] RemoveDleStuffing(byte[] stuffed)
    {
        var list = new List<byte>(stuffed.Length);
        for (int i = 0; i < stuffed.Length; i++)
        {
            byte b = stuffed[i];
            if (b == 0x10 && i + 1 < stuffed.Length && stuffed[i + 1] == 0x10)
            {
                list.Add(0x10);
                i++;
            }
            else
                list.Add(b);
        }
        return list.ToArray();
    }

    /// <summary>
    /// Converts a byte array to a hex string with space separators.
    /// Optimised using string.Create to allocate the final string directly.
    /// </summary>
    public static string Hex(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0) return string.Empty;

        int length = (bytes.Length * 3) - 1;

        return string.Create(length, bytes, (chars, state) =>
        {
            int pos = 0;
            for (int i = 0; i < state.Length; i++)
            {
                byte b = state[i];
                chars[pos++] = ToHexChar(b >> 4);
                chars[pos++] = ToHexChar(b & 0x0F);
                if (i < state.Length - 1)
                    chars[pos++] = ' ';
            }
        });
    }

    private static char ToHexChar(int nibble) => (char)(nibble < 10 ? '0' + nibble : 'A' + nibble - 10);

    /// <summary>
    /// Decodes a raw DF1 or EIP frame into a human-readable description.
    /// </summary>
    public static string Decode(byte[] raw)
    {
        // EIP frame detection (first byte is not DLE 0x10, and length >= 24)
        if (raw.Length >= 24 && raw[0] != 0x10)
        {
            return DecodeEip(raw);
        }

        // 2-byte control frames: ACK, NAK, ENQ
        if (raw.Length == 2 && raw[0] == 0x10)
        {
            string type = raw[1] switch
            {
                0x06 => "ACK",
                0x15 => "NAK",
                0x05 => "ENQ",
                _ => $"DLE 0x{raw[1]:X2}"
            };
            return $"{Indent}{type}";
        }

        // 3-byte poll frame (DLE ENQ + address) for half-duplex master
        if (raw.Length == 3 && raw[0] == 0x10 && raw[1] == 0x05)
        {
            return $"{Indent}ENQ (poll) addr=0x{raw[2]:X2}";
        }

        // Normal DF1 data frame
        if (raw.Length < 6 || raw[0] != 0x10 || raw[1] != 0x02)
        {
            return $"{Indent}Invalid: {Hex(raw)}";
        }

        // Locate DLE ETX (skip stuffed DLE DLE)
        int etx = -1;
        for (int i = 2; i < raw.Length - 1; i++)
        {
            if (raw[i] == 0x10 && raw[i + 1] == 0x10)
                i++;
            else if (raw[i] == 0x10 && raw[i + 1] == 0x03)
            {
                etx = i;
                break;
            }
        }
        if (etx == -1) return $"{Indent}No ETX";

        byte[] unstuffed = RemoveDleStuffing(raw.Skip(2).Take(etx - 2).ToArray());
        if (unstuffed.Length < 6) return $"{Indent}Payload too short";

        int dst = unstuffed[0], src = unstuffed[1], cmd = unstuffed[2], sts = unstuffed[3];
        int tns = unstuffed[4] | (unstuffed[5] << 8);
        int fnc = unstuffed.Length >= 7 ? unstuffed[6] : 0;
        byte[] data = unstuffed.Length >= 8 ? unstuffed.Skip(7).ToArray() : Array.Empty<byte>();
        // Note: Length == 7 means there is an FNC but no data — valid for some response frames

        var sb = new StringBuilder();
        sb.AppendLine($"{Indent}DST={dst} SRC={src} TNS={tns} CMD=0x{cmd:X2} FNC=0x{fnc:X2} STS={sts}");

        if (cmd == 0x0F && (fnc == 0xA1 || fnc == 0xA2 || fnc == 0xAA || fnc == 0xAB) && data.Length >= 4)
        {
            int size = data[0], fileNum = data[1], fileType = data[2];
            string typeStr = FileTypeHelper.GetFileTypeName(fileType);
            int elem = data[3], idx = 4;
            if (elem == 0xFF && data.Length >= idx + 2)
            {
                elem = data[idx] | (data[idx + 1] << 8);
                idx += 2;
            }

            // size is the number of bytes requested in this transaction — not the total file size.
            // For large files (e.g. T4=468 bytes) PCCCComm splits into multiple transactions
            // (max 236 bytes each), so size/bpe reflects only this transaction's portion.
            int bpe = FileTypeHelper.GetBytesPerElement(fileType);
            int wordsRequested = size / bpe;
            sb.Append($"              Size={size} bytes ({wordsRequested} {(bpe == 2 ? "words" : "elements")}), File={fileNum}, Type={typeStr}, Element={elem}");
            if ((fnc == 0xA2 || fnc == 0xAB) && data.Length > idx)
                sb.Append($", SubElem={data[idx]}");
            if (fnc == 0xAB && data.Length >= idx + 4)
                sb.Append($", Mask=0x{(data[idx] | (data[idx + 1] << 8)):X4}");
            sb.AppendLine();
        }
        else if (cmd == 0x06 && fnc == 0x03)
        {
            sb.AppendLine("              (Diagnostic status data)");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Decodes an EIP encapsulation header into a one‑line summary.
    /// </summary>
    private static string DecodeEip(byte[] raw)
    {
        if (raw.Length < 24) return $"{Indent}EIP (truncated): {Hex(raw)}";
        ushort cmd = (ushort)(raw[0] | (raw[1] << 8));
        ushort len = (ushort)(raw[2] | (raw[3] << 8));
        uint session = BitConverter.ToUInt32(raw, 4);
        uint status = BitConverter.ToUInt32(raw, 8);
        string cmdName = cmd switch
        {
            0x0065 => "RegisterSession",
            0x0066 => "UnregisterSession",
            0x006F => "SendRRData",
            _ => $"0x{cmd:X4}"
        };
        return $"{Indent}EIP {cmdName} (session=0x{session:X8}, status=0x{status:X8}, len={len})";
    }
}
