// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2011 Apple Inc. All rights reserved.
// Copyright (c) 2026 Tiglate Pileser III.
//
// Derived from Apple's ag_dec.c and aglib.h (Apple Lossless Audio Codec, https://github.com/macosforge/alac). Modified: ported from C to C#, bit access through AlacBitReader (bounds-checked), shifts that are undefined in C made explicit.
//
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file except in compliance with the License.
// You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0 (see LICENSE.Alac). Unless required by applicable
// law or agreed to in writing, software distributed under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR
// CONDITIONS OF ANY KIND, either express or implied. See the License for the specific language governing permissions and limitations.

using System.Numerics;

namespace Aibell.Audio.Alac;

/// <summary>The adaptive Golomb-Rice residual decoder of ALAC, including its run-length mode for stretches of zeros.</summary>
internal static class AdaptiveGolomb
{
    private const int QbShift = 9;
    private const int Qb = 1 << QbShift;
    private const int MulShift = 2;
    private const int DenShift = QbShift - MulShift - 1;
    private const int Off = 1 << (DenShift - 2);
    private const int BitOff = 24;
    private const int MaxPrefix16 = 9;
    private const int MaxPrefix32 = 9;
    private const int MaxDatatypeBits16 = 16;
    private const uint MeanClamp = 0xFFFF;

    /// <summary>
    /// Decodes <paramref name="count"/> residuals into <paramref name="output"/>. <paramref name="maxBits"/> is the sample width (the escape
    /// code for a large value carries that many raw bits); <paramref name="mb0"/>, <paramref name="pb"/> and <paramref name="kb"/> are the
    /// initial mean, the adaptation factor and the maximum Rice parameter.
    /// </summary>
    public static void Decode(ref AlacBitReader bits, Span<int> output, int count, int maxBits, uint mb0, uint pb, uint kb)
    {
        uint wb = (1u << (int)kb) - 1;
        uint mb = mb0;
        int zeroMode = 0;
        int index = 0;
        while (index < count)
        {
            if (bits.AtEnd) throw new InvalidDataException("The ALAC residuals run past the end of the packet.");

            uint m = mb >> QbShift;
            int k = Math.Min(Log2Plus3(m), (int)kb);
            m = (1u << k) - 1;

            uint n = Get32(ref bits, m, k, maxBits);

            // The least significant bit of the code is the sign.
            uint decoded = n + (uint)zeroMode;
            int multiplier = -(int)(decoded & 1) | 1;
            output[index++] = (int)(((decoded + 1) >> 1) * (uint)multiplier);

            mb = pb * (n + (uint)zeroMode) + mb - ((pb * mb) >> QbShift);
            if (n > MeanClamp) mb = MeanClamp;

            zeroMode = 0;
            if (((mb << MulShift) < Qb) && index < count)
            {
                zeroMode = 1;
                int zk = BitOperations.LeadingZeroCount(mb) - BitOff + (int)((mb + Off) >> DenShift);
                uint mz = ((1u << zk) - 1) & wb;
                uint run = Get16(ref bits, mz, zk);
                if (run > (uint)(count - index)) throw new InvalidDataException("An ALAC zero run is longer than the packet.");
                output.Slice(index, (int)run).Clear();
                index += (int)run;
                if (run >= 65535) zeroMode = 0;
                mb = 0;
            }
        }
    }

    private static int Log2Plus3(uint m) => 31 - BitOperations.LeadingZeroCount(m + 3);

    private static uint Top(uint word, int count) => count == 0 ? 0 : word >> (32 - count);

    private static uint Get16(ref AlacBitReader bits, uint m, int k)
    {
        uint word = bits.Peek32();
        int prefix = BitOperations.LeadingZeroCount(~word);
        if (prefix >= MaxPrefix16)
        {
            bits.Advance(MaxPrefix16);
            return bits.Read(MaxDatatypeBits16);
        }
        uint v = Top(word << (prefix + 1), k);
        uint result = (uint)prefix * m + v - 1;
        if (v < 2)
        {
            // A value of 0 or 1 in the remainder field is the short form: the field is only k - 1 bits wide.
            result -= v - 1;
            bits.Advance(prefix + k);
        }
        else
        {
            bits.Advance(prefix + 1 + k);
        }
        return result;
    }

    private static uint Get32(ref AlacBitReader bits, uint m, int k, int maxBits)
    {
        uint word = bits.Peek32();
        int prefix = BitOperations.LeadingZeroCount(~word);
        if (prefix >= MaxPrefix32)
        {
            bits.Advance(MaxPrefix32);
            return bits.Read(maxBits);
        }
        bits.Advance(prefix + 1);
        uint result = (uint)prefix;
        if (k != 1)
        {
            uint v = Top(word << (prefix + 1), k);
            bits.Advance(k - 1);
            result *= m;
            if (v >= 2)
            {
                result += v - 1;
                bits.Advance(1);
            }
        }
        return result;
    }
}
