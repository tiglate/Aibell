// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2011 Apple Inc. All rights reserved.
// Copyright (c) 2026 Tiglate Pileser III.
//
// Derived from Apple's ALACDecoder.cpp, matrix_dec.c and ALACAudioTypes.h (Apple Lossless Audio Codec, https://github.com/macosforge/alac). Modified: ported from C++ to C#. Output is interleaved float in WAVE channel order instead of packed integers in the bitstream's order; the packet is read through a bounds-checked bit reader; frame lengths and element sequences are validated instead of trusted.
//
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file except in compliance with the License.
// You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0 (see LICENSE.Alac). Unless required by applicable
// law or agreed to in writing, software distributed under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR
// CONDITIONS OF ANY KIND, either express or implied. See the License for the specific language governing permissions and limitations.

namespace Aibell.Audio.Alac;

/// <summary>
/// Decodes ALAC packets (one frame each, normally 4096 samples per channel) into interleaved float samples in the WAVE channel order.
/// The decoder keeps no state between packets, so any packet can be decoded on its own. Nothing is allocated once a packet buffer of
/// the right size exists.
/// </summary>
public sealed class AlacFrameDecoder
{
    private const int ElementSingle = 0;
    private const int ElementPair = 1;
    private const int ElementCoupling = 2;
    private const int ElementLfe = 3;
    private const int ElementDataStream = 4;
    private const int ElementProgramConfig = 5;
    private const int ElementFill = 6;
    private const int ElementEnd = 7;

    // For each channel count, where each channel of the bitstream's order goes in the WAVE order (FL FR FC LFE BL BR FLC FRC BC SL SR).
    // Bitstream orders: 1 C | 2 L R | 3 C L R | 4 C L R Cs | 5 C L R Ls Rs | 6 C L R Ls Rs LFE | 7 C L R Ls Rs Cs LFE | 8 C Lc Rc L R Ls Rs LFE.
    private static readonly int[][] WaveSlots =
    [
        [0],
        [0, 1],
        [2, 0, 1],
        [2, 0, 1, 3],
        [2, 0, 1, 3, 4],
        [2, 0, 1, 4, 5, 3],
        [2, 0, 1, 5, 6, 4, 3],
        [2, 6, 7, 0, 1, 4, 5, 3]
    ];

    private readonly int[] predictor;
    private readonly int[] mixU;
    private readonly int[] mixV;
    private readonly ushort[] shiftBuffer;
    private readonly short[] coefsU = new short[32];
    private readonly short[] coefsV = new short[32];
    private readonly int[] slots;
    private readonly float scale;
    private readonly int signShift;
    private byte[] packet = new byte[4096 + AlacBitReader.Padding];

    public AlacFrameDecoder(AlacConfig config)
    {
        Config = config;
        predictor = new int[config.FrameLength];
        mixU = new int[config.FrameLength];
        mixV = new int[config.FrameLength];
        shiftBuffer = new ushort[config.FrameLength * 2];
        slots = WaveSlots[config.Channels - 1];
        scale = 1f / (1L << (config.BitDepth - 1));
        signShift = 32 - config.BitDepth;
    }

    /// <summary>Creates a decoder from a magic cookie (see <see cref="AlacConfig.Parse"/>).</summary>
    public AlacFrameDecoder(ReadOnlySpan<byte> magicCookie) : this(AlacConfig.Parse(magicCookie))
    {
    }

    public AlacConfig Config { get; }

    /// <summary>
    /// Decodes one packet into <paramref name="output"/>, which must hold <c>FrameLength * Channels</c> floats, and returns the number of
    /// frames written. Throws <see cref="InvalidDataException"/> for a damaged packet.
    /// </summary>
    public int Decode(ReadOnlySpan<byte> data, Span<float> output)
    {
        int channels = Config.Channels;
        if (output.Length < Config.FrameLength * channels) throw new ArgumentException("The output buffer is smaller than one frame.", nameof(output));
        if (data.Length == 0) throw new InvalidDataException("An ALAC packet is empty.");
        if (packet.Length < data.Length + AlacBitReader.Padding) packet = new byte[data.Length + AlacBitReader.Padding];
        data.CopyTo(packet);
        packet.AsSpan(data.Length, AlacBitReader.Padding).Clear();
        AlacBitReader bits = new(packet, data.Length);

        int frames = Config.FrameLength;
        int channel = 0;
        while (true)
        {
            if (bits.AtEnd) throw new InvalidDataException("The ALAC packet has no end element.");
            int tag = (int)bits.Read(3);
            switch (tag)
            {
                case ElementSingle:
                case ElementLfe:
                    frames = DecodeSingle(ref bits, output, channel, frames);
                    channel++;
                    break;
                case ElementPair:
                    if (channel + 2 > channels) goto NoMoreChannels;
                    frames = DecodePair(ref bits, output, channel, frames);
                    channel += 2;
                    break;
                case ElementCoupling:
                case ElementProgramConfig:
                    throw new InvalidDataException("This ALAC element is not supported.");
                case ElementDataStream:
                    SkipDataStream(ref bits);
                    break;
                case ElementFill:
                    SkipFill(ref bits);
                    break;
                case ElementEnd:
                    goto NoMoreChannels;
            }
            if (channel >= channels) break;
        }

    NoMoreChannels:
        // Channels the stream did not carry are silent.
        for (; channel < channels; channel++)
        {
            int slot = slots[channel];
            for (int i = 0; i < frames; i++) output[i * channels + slot] = 0;
        }
        return frames;
    }

    private struct ElementHeader
    {
        public int BytesShifted;
        public bool Escape;
        public int Frames;
    }

    private ElementHeader ReadHeader(ref AlacBitReader bits, int frames)
    {
        bits.Read(4); // element instance tag
        if (bits.Read(12) != 0) throw new InvalidDataException("The ALAC element header is damaged.");
        uint flags = bits.Read(4);
        ElementHeader header = new() { BytesShifted = (int)(flags >> 1) & 3, Escape = (flags & 1) != 0, Frames = frames };
        if (header.BytesShifted == 3) throw new InvalidDataException("The ALAC element header is damaged.");
        if ((flags & 8) != 0)
        {
            header.Frames = (int)Math.Min(bits.Read(16) << 16 | bits.Read(16), int.MaxValue);
            if (header.Frames < 1 || header.Frames > Config.FrameLength) throw new InvalidDataException("The ALAC partial frame length is out of range.");
        }
        return header;
    }

    private int DecodeSingle(ref AlacBitReader bits, Span<float> output, int channel, int frames)
    {
        ElementHeader header = ReadHeader(ref bits, frames);
        int count = header.Frames;
        int bytesShifted = header.BytesShifted;
        int chanBits = Config.BitDepth - bytesShifted * 8;
        AlacBitReader shiftBits = default;

        if (!header.Escape)
        {
            bits.Read(8); // mix bits
            bits.Read(8); // mix resolution; neither is used for a single channel
            ReadPredictorHeader(ref bits, coefsU, out int mode, out int denShift, out int pbFactor, out int numCoefs);

            if (bytesShifted != 0)
            {
                shiftBits = bits;
                bits.Advance((long)bytesShifted * 8 * count);
            }
            Reconstruct(ref bits, mixU, count, chanBits, mode, denShift, pbFactor, numCoefs, coefsU);
        }
        else
        {
            ReadUncompressed(ref bits, mixU, count, chanBits);
            bytesShifted = 0;
        }

        if (bytesShifted != 0)
        {
            int shift = bytesShifted * 8;
            for (int i = 0; i < count; i++) shiftBuffer[i] = (ushort)shiftBits.Read(shift);
        }

        int channels = Config.Channels;
        int slot = slots[channel];
        int depthShift = Config.BitDepth is 24 or 32 ? bytesShifted * 8 : 0;
        for (int i = 0; i < count; i++)
        {
            int sample = mixU[i];
            if (depthShift != 0) sample = sample << depthShift | shiftBuffer[i];
            output[i * channels + slot] = (sample << signShift >> signShift) * scale;
        }
        return count;
    }

    private int DecodePair(ref AlacBitReader bits, Span<float> output, int channel, int frames)
    {
        ElementHeader header = ReadHeader(ref bits, frames);
        int count = header.Frames;
        int bytesShifted = header.BytesShifted;
        int chanBits = Config.BitDepth - bytesShifted * 8 + 1;
        int mixBits = 0, mixRes = 0;
        AlacBitReader shiftBits = default;

        if (!header.Escape)
        {
            mixBits = (int)bits.Read(8);
            mixRes = (sbyte)bits.Read(8);
            ReadPredictorHeader(ref bits, coefsU, out int modeU, out int denShiftU, out int pbFactorU, out int numU);
            ReadPredictorHeader(ref bits, coefsV, out int modeV, out int denShiftV, out int pbFactorV, out int numV);

            if (bytesShifted != 0)
            {
                shiftBits = bits;
                bits.Advance((long)bytesShifted * 8 * 2 * count);
            }
            Reconstruct(ref bits, mixU, count, chanBits, modeU, denShiftU, pbFactorU, numU, coefsU);
            Reconstruct(ref bits, mixV, count, chanBits, modeV, denShiftV, pbFactorV, numV, coefsV);
        }
        else
        {
            ReadUncompressed(ref bits, mixU, mixV, count, Config.BitDepth);
            bytesShifted = 0;
        }

        if (bytesShifted != 0)
        {
            int shift = bytesShifted * 8;
            for (int i = 0; i < count * 2; i++) shiftBuffer[i] = (ushort)shiftBits.Read(shift);
        }

        int channels = Config.Channels;
        int slotL = slots[channel], slotR = slots[channel + 1];
        int depthShift = Config.BitDepth is 24 or 32 ? bytesShifted * 8 : 0;
        for (int i = 0; i < count; i++)
        {
            int l, r;
            if (mixRes != 0)
            {
                // The generalized middle/side matrix: u = (rL + (m-r)R) / m, v = L - R, undone exactly.
                l = mixU[i] + mixV[i] - ((mixRes * mixV[i]) >> mixBits);
                r = l - mixV[i];
            }
            else
            {
                l = mixU[i];
                r = mixV[i];
            }
            if (depthShift != 0)
            {
                l = l << depthShift | shiftBuffer[i * 2];
                r = r << depthShift | shiftBuffer[i * 2 + 1];
            }
            output[i * channels + slotL] = (l << signShift >> signShift) * scale;
            output[i * channels + slotR] = (r << signShift >> signShift) * scale;
        }
        return count;
    }

    private static void ReadPredictorHeader(ref AlacBitReader bits, short[] coefs, out int mode, out int denShift, out int pbFactor, out int numCoefs)
    {
        uint header = bits.Read(8);
        mode = (int)(header >> 4);
        denShift = (int)(header & 0xF);
        header = bits.Read(8);
        pbFactor = (int)(header >> 5);
        numCoefs = (int)(header & 0x1F);
        for (int i = 0; i < numCoefs; i++) coefs[i] = (short)bits.Read(16);
    }

    private void Reconstruct(ref AlacBitReader bits, int[] destination, int count, int chanBits, int mode, int denShift, int pbFactor, int numCoefs, short[] coefs)
    {
        if (chanBits > 32) throw new InvalidDataException("The ALAC sample width is out of range.");
        AdaptiveGolomb.Decode(ref bits, predictor, count, chanBits, (uint)Config.Mb, (uint)(Config.Pb * pbFactor / 4), (uint)Config.Kb);
        if (mode != 0)
        {
            // The special 31-coefficient mode: a plain delta pass, in place, before the real filter.
            DynamicPredictor.Run(predictor, predictor, count, coefs, DynamicPredictor.DeltaOnly, chanBits, 0);
        }
        DynamicPredictor.Run(predictor, destination, count, coefs, numCoefs, chanBits, denShift);
    }

    private static void ReadUncompressed(ref AlacBitReader bits, int[] destination, int count, int chanBits)
    {
        for (int i = 0; i < count; i++) destination[i] = ReadRaw(ref bits, chanBits);
    }

    private static void ReadUncompressed(ref AlacBitReader bits, int[] first, int[] second, int count, int chanBits)
    {
        for (int i = 0; i < count; i++)
        {
            first[i] = ReadRaw(ref bits, chanBits);
            second[i] = ReadRaw(ref bits, chanBits);
        }
    }

    // A signed sample of chanBits bits, read in pieces because the Apple reader handles 16 bits at a time.
    private static int ReadRaw(ref AlacBitReader bits, int chanBits)
    {
        int shift = 32 - chanBits;
        if (chanBits <= 16) return (int)bits.Read(chanBits) << shift >> shift;
        int high = (int)bits.Read(16) << 16 >> shift;
        return high | (int)bits.Read(chanBits - 16);
    }

    private static void SkipFill(ref AlacBitReader bits)
    {
        int count = (int)bits.Read(4);
        if (count == 15) count += (int)bits.Read(8) - 1;
        bits.Advance(count * 8L);
    }

    private static void SkipDataStream(ref AlacBitReader bits)
    {
        bits.Read(4); // element instance tag
        bool align = bits.Read(1) != 0;
        int count = (int)bits.Read(8);
        if (count == 255) count += (int)bits.Read(8);
        if (align) bits.AlignToByte();
        bits.Advance(count * 8L);
    }
}
