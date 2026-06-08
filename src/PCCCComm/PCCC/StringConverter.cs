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

using System.Text;

namespace PCCCComm.Pccc;

/// <summary>
/// Converts between strings and AB PLC integer-stored word arrays.
/// AB PLCs store strings in integer files with characters packed
/// two per word, high byte first. Example: word 0x4142 = "AB".
/// </summary>
public static class StringConverter
{
    /// <summary>
    /// Converts an array of words to a string.
    /// Stops at first null high byte or low byte.
    /// </summary>
    public static string WordsToString(int[] words)
        => WordsToString(words, 0, words.Length);

    /// <summary>
    /// Converts a portion of a word array to a string, starting at index.
    /// </summary>
    public static string WordsToString(int[] words, int index)
        => WordsToString(words, index, words.Length - index);

    /// <summary>
    /// Converts wordCount words starting at index to a string.
    /// High byte = first character, low byte = second character.
    /// Stops when high byte is 0 (end of string).
    /// </summary>
    public static string WordsToString(int[] words, int index, int wordCount)
    {
        var sb = new StringBuilder(wordCount * 2);
        int end = index + wordCount;
        for (int j = index; j < end; j++)
        {
            int word = words[j];
            // Mask to avoid sign extension
            byte high = (byte)((word >> 8) & 0xFF);
            byte low = (byte)(word & 0xFF);

            if (high == 0)
                break;  // end of string (null padding)

            sb.Append((char)high);

            if (low == 0)
                break;

            sb.Append((char)low);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Converts a string to an array of words (high byte = first char, low byte = second char).
    /// Uses ASCII encoding – non-ASCII characters are replaced with '?'.
    /// Returns null if source is null.
    /// </summary>
    public static int[]? StringToWords(string source)
    {
        if (source == null)
            return null;

        byte[] bytes = Encoding.ASCII.GetBytes(source);
        int wordCount = (bytes.Length + 1) / 2;
        int[] result = new int[wordCount];

        for (int i = 0; i < wordCount; i++)
        {
            int high = (i * 2) < bytes.Length ? bytes[i * 2] : 0;
            int low = (i * 2 + 1) < bytes.Length ? bytes[i * 2 + 1] : 0;
            result[i] = (high << 8) | low;
        }
        return result;
    }
}
