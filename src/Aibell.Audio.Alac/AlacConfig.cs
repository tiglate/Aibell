// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2011 Apple Inc. All rights reserved.
// Copyright (c) 2026 Tiglate Pileser III.
//
// Derived from Apple's ALACAudioTypes.h and ALACDecoder.cpp (Init) (Apple Lossless Audio Codec, https://github.com/macosforge/alac). Modified: ported from C++ to C#; the magic cookie is parsed with bounds checks and validated.
//
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file except in compliance with the License.
// You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0 (see LICENSE.Alac). Unless required by applicable
// law or agreed to in writing, software distributed under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR
// CONDITIONS OF ANY KIND, either express or implied. See the License for the specific language governing permissions and limitations.

using System.Buffers.Binary;

namespace Aibell.Audio.Alac;

/// <summary>The ALACSpecificConfig of the magic cookie: how the encoder was set up.</summary>
public sealed record AlacConfig(int FrameLength, int BitDepth, int Pb, int Mb, int Kb, int Channels, int MaxRun, int MaxFrameBytes, int AvgBitRate, int SampleRate)
{
    /// <summary>Size of the ALACSpecificConfig structure.</summary>
    public const int Size = 24;

    private const int AtomHeaderSize = 12;
    private const int MaxFrameLength = 1 << 16;

    /// <summary>
    /// Parses a magic cookie: the ALACSpecificConfig, optionally preceded by the 12-byte 'frma' and 'alac' atom headers that older encoders
    /// wrote, and optionally followed by channel layout information (which is not needed). Throws <see cref="InvalidDataException"/> or
    /// <see cref="NotSupportedException"/>.
    /// </summary>
    public static AlacConfig Parse(ReadOnlySpan<byte> cookie)
    {
        if (cookie.Length >= AtomHeaderSize && cookie.Slice(4, 4).SequenceEqual("frma"u8)) cookie = cookie[AtomHeaderSize..];
        if (cookie.Length >= AtomHeaderSize && cookie.Slice(4, 4).SequenceEqual("alac"u8)) cookie = cookie[AtomHeaderSize..];
        if (cookie.Length < Size) throw new InvalidDataException("The ALAC magic cookie is too short.");

        uint frameLength = BinaryPrimitives.ReadUInt32BigEndian(cookie);
        byte compatibleVersion = cookie[4];
        AlacConfig config = new((int)Math.Min(frameLength, int.MaxValue), cookie[5], cookie[6], cookie[7], cookie[8], cookie[9],
            BinaryPrimitives.ReadUInt16BigEndian(cookie[10..]), (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(cookie[12..]), int.MaxValue),
            (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(cookie[16..]), int.MaxValue), (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(cookie[20..]), int.MaxValue));

        if (compatibleVersion != 0) throw new NotSupportedException("This ALAC stream needs a newer decoder.");
        if (config.FrameLength is < 1 or > MaxFrameLength) throw new InvalidDataException("The ALAC frame length is out of range.");
        if (config.BitDepth is not (16 or 20 or 24 or 32)) throw new NotSupportedException("This ALAC bit depth is not supported.");
        if (config.Channels is < 1 or > 8) throw new NotSupportedException("This ALAC channel count is not supported.");
        if (config.Kb > 31) throw new InvalidDataException("The ALAC Rice parameter is out of range.");
        if (config.SampleRate < 1) throw new InvalidDataException("The ALAC sample rate is missing.");
        return config;
    }
}
