// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2011 Apple Inc. All rights reserved.
// Copyright (c) 2026 Tiglate Pileser III.
//
// Derived from Apple's dp_dec.c (Apple Lossless Audio Codec, https://github.com/macosforge/alac). Modified: ported from C to C#, the unrolled 4 and 8 coefficient loops folded into the general loop (they compute the same values), reads past the packet turned into bounds checks.
//
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file except in compliance with the License.
// You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0 (see LICENSE.Alac). Unless required by applicable
// law or agreed to in writing, software distributed under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR
// CONDITIONS OF ANY KIND, either express or implied. See the License for the specific language governing permissions and limitations.

namespace Aibell.Audio.Alac;

/// <summary>The sign-sign LMS dynamic predictor of ALAC, run backwards to turn residuals into samples.</summary>
internal static class DynamicPredictor
{
    /// <summary>The coefficient count that stands for the plain delta filter (no coefficients, no adaptation).</summary>
    public const int DeltaOnly = 31;

    private static int Sign(int value) => (int)((uint)-value >> 31) | (value >> 31);

    /// <summary>
    /// Reconstructs <paramref name="count"/> samples of <paramref name="chanBits"/> bits from the residuals in <paramref name="input"/>.
    /// <paramref name="input"/> and <paramref name="output"/> may be the same memory. <paramref name="coefs"/> is adapted in place.
    /// </summary>
    public static void Run(ReadOnlySpan<int> input, Span<int> output, int count, Span<short> coefs, int numActive, int chanBits, int denShift)
    {
        if (count <= 0) return;
        int chanShift = 32 - chanBits;
        output[0] = input[0];
        if (numActive == 0)
        {
            if (count > 1 && !input.Overlaps(output)) input.Slice(1, count - 1).CopyTo(output[1..]);
            return;
        }
        if (numActive == DeltaOnly)
        {
            int previous = output[0];
            for (int j = 1; j < count; j++)
            {
                previous = (input[j] + previous) << chanShift >> chanShift;
                output[j] = previous;
            }
            return;
        }

        int denHalf = 1 << (denShift - 1);
        int warmup = Math.Min(numActive, count - 1);
        for (int j = 1; j <= warmup; j++) output[j] = (input[j] + output[j - 1]) << chanShift >> chanShift;

        int lim = numActive + 1;
        for (int j = lim; j < count; j++)
        {
            int top = output[j - lim];
            int sum = 0;
            for (int k = 0; k < numActive; k++) sum += coefs[k] * (output[j - 1 - k] - top);

            int del = input[j];
            int remaining = del;
            int sg = Sign(del);
            del += top + ((sum + denHalf) >> denShift);
            output[j] = del << chanShift >> chanShift;

            if (sg > 0)
            {
                for (int k = numActive - 1; k >= 0; k--)
                {
                    int dd = top - output[j - 1 - k];
                    int sgn = Sign(dd);
                    coefs[k] -= (short)sgn;
                    remaining -= (numActive - k) * ((sgn * dd) >> denShift);
                    if (remaining <= 0) break;
                }
            }
            else if (sg < 0)
            {
                for (int k = numActive - 1; k >= 0; k--)
                {
                    int dd = top - output[j - 1 - k];
                    int sgn = Sign(dd);
                    coefs[k] += (short)sgn;
                    remaining -= (numActive - k) * ((-sgn * dd) >> denShift);
                    if (remaining >= 0) break;
                }
            }
        }
    }
}
