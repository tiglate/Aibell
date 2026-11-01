// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2011 Apple Inc. All rights reserved.
// Copyright (c) 2026 Tiglate Pileser III.
//
// Derived from Apple's ALACBitUtilities.c (Apple Lossless Audio Codec, https://github.com/macosforge/alac). Modified: ported from C to C#,
// rewritten as a bounds-checked bit reader over a padded array instead of a pointer pair.
//
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file except in compliance with the License.
// You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0 (see LICENSE.Alac). Unless required by applicable
// law or agreed to in writing, software distributed under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR
// CONDITIONS OF ANY KIND, either express or implied. See the License for the specific language governing permissions and limitations.

using System.Buffers.Binary;

namespace Aibell.Audio.Alac;

/// <summary>
/// Reads an ALAC packet most significant bit first. The array holds the packet followed by <see cref="Padding"/> zero bytes so that a
/// look-ahead past the last bit is harmless; consuming bits beyond the packet throws <see cref="InvalidDataException"/>.
/// </summary>
internal struct AlacBitReader
{
    public const int Padding = 16;

    private readonly byte[] data;
    private readonly int payloadBits;
    private int position;

    public AlacBitReader(byte[] data, int payloadBytes)
    {
        this.data = data;
        payloadBits = payloadBytes * 8;
        position = 0;
    }

    public readonly int Position => position;

    /// <summary>True once every bit of the packet is consumed.</summary>
    public readonly bool AtEnd => position >= payloadBits;

    /// <summary>The next 32 bits without consuming them (zeros past the end of the packet).</summary>
    public readonly uint Peek32()
    {
        ulong word = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(position >> 3, 8));
        return (uint)((word << (position & 7)) >> 32);
    }

    /// <summary>Reads 0 to 32 bits.</summary>
    public uint Read(int count)
    {
        if (count == 0) return 0;
        uint value = Peek32() >> (32 - count);
        Advance(count);
        return value;
    }

    public void Advance(long count)
    {
        long next = position + count;
        if (next > payloadBits) throw new InvalidDataException("The ALAC packet ends early.");
        position = (int)next;
    }

    public void AlignToByte() => Advance((8 - (position & 7)) & 7);
}
