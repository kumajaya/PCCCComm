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

using System.Runtime.InteropServices;

namespace PCCCComm.Pccc;

/// <summary>
/// Zero-allocation conversions between AB PLC two-word (32-bit) data types
/// (Float file, Long file) and their constituent 16-bit words.
/// </summary>
/// <remarks>
/// Replaces per-element <c>new byte[4]</c> / <c>BitConverter.GetBytes(...)</c>
/// allocations previously used in <see cref="SlcHandler"/> and
/// <see cref="Plc5Handler"/> read/write loops. Uses explicit-layout union
/// structs rather than <c>Span&lt;byte&gt;</c>-based BitConverter overloads,
/// since PCCCComm targets netstandard2.0 and those overloads require
/// netstandard2.1/.NET Core 2.1+.
/// 
/// Word order matches AB's native low-word-first layout used throughout
/// SlcHandler/Plc5Handler (rawWords[offset] = low word, rawWords[offset+1] =
/// high word) — i.e. the same order as the existing
/// <c>Buffer.BlockCopy(rawWords, offset*2, buf, 0, 4)</c> +
/// <c>BitConverter.ToSingle/ToInt32(buf, 0)</c> pattern being replaced.
/// </remarks>
public static class WordConverter
{
    [StructLayout(LayoutKind.Explicit)]
    private struct FloatWordUnion
    {
        [FieldOffset(0)] public float FloatValue;
        [FieldOffset(0)] public ushort Word0; // low word
        [FieldOffset(2)] public ushort Word1; // high word
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct Int32WordUnion
    {
        [FieldOffset(0)] public int IntValue;
        [FieldOffset(0)] public ushort Word0; // low word
        [FieldOffset(2)] public ushort Word1; // high word
    }

    /// <summary>
    /// Reconstructs a 32-bit float from two consecutive 16-bit words
    /// (low word first), with zero heap allocation.
    /// </summary>
    public static float WordsToFloat(ushort lowWord, ushort highWord)
    {
        var u = new FloatWordUnion { Word0 = lowWord, Word1 = highWord };
        return u.FloatValue;
    }

    /// <summary>
    /// Reconstructs a 32-bit signed integer from two consecutive 16-bit words
    /// (low word first), with zero heap allocation.
    /// </summary>
    public static int WordsToInt32(ushort lowWord, ushort highWord)
    {
        var u = new Int32WordUnion { Word0 = lowWord, Word1 = highWord };
        return u.IntValue;
    }

    /// <summary>
    /// Splits a 32-bit float into its two constituent 16-bit words
    /// (low word first), with zero heap allocation.
    /// </summary>
    public static void FloatToWords(float value, out ushort lowWord, out ushort highWord)
    {
        var u = new FloatWordUnion { FloatValue = value };
        lowWord = u.Word0;
        highWord = u.Word1;
    }

    /// <summary>
    /// Splits a 32-bit signed integer into its two constituent 16-bit words
    /// (low word first), with zero heap allocation.
    /// </summary>
    public static void Int32ToWords(int value, out ushort lowWord, out ushort highWord)
    {
        var u = new Int32WordUnion { IntValue = value };
        lowWord = u.Word0;
        highWord = u.Word1;
    }
}
