// This file is derived from the Opus reference implementation published in RFC 6716 (silk/*.c).
// Copyright 1994-2011 IETF Trust, Xiph.Org, Skype Limited, Octasic, Jean-Marc Valin, Timothy B. Terriberry,
// CSIRO, Gregory Maxwell, Mark Borgerding, Erik de Castro Lopo. All rights reserved.
// Redistribution and use are permitted under the BSD terms reproduced in COPYING.Opus.
using static Aibell.Audio.Opus.SilkMath;

namespace Aibell.Audio.Opus;

/// <summary>An LSF codebook (NB/MB with 10 coefficients, or WB with 16).</summary>
internal sealed class SilkNlsfCodebook
{
    public const int Vectors = 32;
    public int Order;
    public int QuantStepSizeQ16;
    public byte[] Cb1Nlsf = [];
    public byte[] Cb1Icdf = [];
    public byte[] Pred = [];
    public byte[] EcSel = [];
    public byte[] EcIcdf = [];
    public short[] DeltaMin = [];

    public static readonly SilkNlsfCodebook NbMb = new()
    {
        Order = 10,
        QuantStepSizeQ16 = 11796,
        Cb1Nlsf = SilkTables.NlsfCb1NbMb.ToArray(),
        Cb1Icdf = SilkTables.NlsfCb1IcdfNbMb.ToArray(),
        Pred = SilkTables.NlsfPredNbMb.ToArray(),
        EcSel = SilkTables.NlsfCb2SelectNbMb.ToArray(),
        EcIcdf = SilkTables.NlsfCb2IcdfNbMb.ToArray(),
        DeltaMin = SilkTables.NlsfDeltaMinNbMb
    };

    public static readonly SilkNlsfCodebook Wb = new()
    {
        Order = 16,
        QuantStepSizeQ16 = 9830,
        Cb1Nlsf = SilkTables.NlsfCb1Wb.ToArray(),
        Cb1Icdf = SilkTables.NlsfCb1IcdfWb.ToArray(),
        Pred = SilkTables.NlsfPredWb.ToArray(),
        EcSel = SilkTables.NlsfCb2SelectWb.ToArray(),
        EcIcdf = SilkTables.NlsfCb2IcdfWb.ToArray(),
        DeltaMin = SilkTables.NlsfDeltaMinWb
    };
}

internal sealed class SilkIndices
{
    public readonly sbyte[] GainsIndices = new sbyte[4];
    public readonly sbyte[] LtpIndex = new sbyte[4];
    public readonly sbyte[] NlsfIndices = new sbyte[17];
    public short LagIndex;
    public sbyte ContourIndex;
    public sbyte SignalType;
    public sbyte QuantOffsetType;
    public sbyte NlsfInterpCoefQ2;
    public sbyte PerIndex;
    public sbyte LtpScaleIndex;
    public sbyte Seed;
}

/// <summary>Per-frame decoded control values (silk_decoder_control).</summary>
internal sealed class SilkControl
{
    public readonly int[] PitchL = new int[4];
    public readonly int[] GainsQ16 = new int[4];
    public readonly short[][] PredCoefQ12 = [new short[16], new short[16]];
    public readonly short[] LtpCoefQ14 = new short[20];
    public int LtpScaleQ14;
}

/// <summary>State of one SILK channel (silk_decoder_state) with its frame decoding.</summary>
internal sealed partial class SilkChannel
{
    public const int MaxFrameLength = 320;
    public const int MaxSubFrameLength = 80;
    public const int MaxLpcOrder = 16;
    public const int LtpOrder = 5;
    private const int TypeNoVoiceActivity = 0;
    private const int TypeVoiced = 2;
    public const int CodeIndependently = 0;
    public const int CodeIndependentlyNoLtpScaling = 1;
    public const int CodeConditionally = 2;
    public const int FlagDecodeNormal = 0;
    public const int FlagPacketLost = 1;
    public const int FlagDecodeLbrr = 2;

    public int PrevGainQ16;
    public readonly int[] ExcQ14 = new int[MaxFrameLength];
    public readonly int[] SLpcQ14Buf = new int[MaxLpcOrder];
    public readonly short[] OutBuf = new short[MaxFrameLength + 2 * MaxSubFrameLength];
    public int LagPrev;
    public sbyte LastGainIndex;
    public int FsKHz;
    public int FsApiHz;
    public int NbSubfr;
    public int FrameLength;
    public int SubfrLength;
    public int LtpMemLength;
    public int LpcOrder;
    public readonly short[] PrevNlsfQ15 = new short[MaxLpcOrder];
    public bool FirstFrameAfterReset;
    public int NFramesDecoded;
    public int NFramesPerPacket;
    public int EcPrevSignalType;
    public short EcPrevLagIndex;
    public readonly int[] VadFlags = new int[3];
    public int LbrrFlag;
    public readonly int[] LbrrFlags = new int[3];
    public SilkResampler Resampler = new();
    public SilkNlsfCodebook Codebook = SilkNlsfCodebook.NbMb;
    public readonly SilkIndices Indices = new();
    public int LossCnt;
    public int PrevSignalType;
    private byte[] pitchLagLowBitsIcdf = SilkTables.Uniform4.ToArray();
    private byte[] pitchContourIcdf = SilkTables.PitchContourIcdf.ToArray();
    private readonly SilkControl control = new();
    private readonly int[] pulses = new int[MaxFrameLength];
    private readonly short[] sLtp = new short[MaxFrameLength];
    private readonly int[] sLtpQ15 = new int[2 * MaxFrameLength];
    private readonly int[] resQ14 = new int[MaxSubFrameLength];
    private readonly int[] sLpcQ14 = new int[MaxSubFrameLength + MaxLpcOrder];

    public SilkChannel()
    {
        Reset();
    }

    public void Reset()
    {
        PrevGainQ16 = 65536;
        Array.Clear(ExcQ14);
        Array.Clear(SLpcQ14Buf);
        Array.Clear(OutBuf);
        LagPrev = 0;
        LastGainIndex = 0;
        FsKHz = 0;
        FsApiHz = 0;
        NbSubfr = 0;
        FrameLength = 0;
        SubfrLength = 0;
        LtpMemLength = 0;
        LpcOrder = 0;
        Array.Clear(PrevNlsfQ15);
        FirstFrameAfterReset = true;
        NFramesDecoded = 0;
        NFramesPerPacket = 0;
        EcPrevSignalType = 0;
        EcPrevLagIndex = 0;
        Array.Clear(VadFlags);
        LbrrFlag = 0;
        Array.Clear(LbrrFlags);
        Resampler = new SilkResampler();
        Codebook = SilkNlsfCodebook.NbMb;
        LossCnt = 0;
        PrevSignalType = 0;
        ResetCng();
        ResetPlc();
    }

    /// <summary>Sets the internal sampling rate (8, 12 or 16 kHz); the API rate is always 48 kHz.</summary>
    public void SetFs(int fsKHz, int fsApiHz)
    {
        SubfrLength = 5 * fsKHz;
        int frameLength = NbSubfr * SubfrLength;
        if (FsKHz != fsKHz || FsApiHz != fsApiHz)
        {
            Resampler.Init(fsKHz * 1000, fsApiHz);
            FsApiHz = fsApiHz;
        }
        if (FsKHz != fsKHz || frameLength != FrameLength)
        {
            if (fsKHz == 8) pitchContourIcdf = (NbSubfr == 4 ? SilkTables.PitchContourNbIcdf : SilkTables.PitchContour10NbIcdf).ToArray();
            else pitchContourIcdf = (NbSubfr == 4 ? SilkTables.PitchContourIcdf : SilkTables.PitchContour10Icdf).ToArray();
            if (FsKHz != fsKHz)
            {
                LtpMemLength = 20 * fsKHz;
                if (fsKHz == 8 || fsKHz == 12)
                {
                    LpcOrder = 10;
                    Codebook = SilkNlsfCodebook.NbMb;
                }
                else
                {
                    LpcOrder = 16;
                    Codebook = SilkNlsfCodebook.Wb;
                }
                pitchLagLowBitsIcdf = (fsKHz == 16 ? SilkTables.Uniform8 : fsKHz == 12 ? SilkTables.Uniform6 : SilkTables.Uniform4).ToArray();
                FirstFrameAfterReset = true;
                LagPrev = 100;
                LastGainIndex = 10;
                PrevSignalType = TypeNoVoiceActivity;
                Array.Clear(OutBuf);
                Array.Clear(SLpcQ14Buf);
            }
            FsKHz = fsKHz;
            FrameLength = frameLength;
        }
    }

    private static void NlsfUnpack(Span<short> ecIx, Span<byte> predQ8, SilkNlsfCodebook cb, int cb1Index)
    {
        int sel = cb1Index * cb.Order / 2;
        for (int i = 0; i < cb.Order; i += 2)
        {
            int entry = cb.EcSel[sel++];
            ecIx[i] = (short)(((entry >> 1) & 7) * 9);
            predQ8[i] = cb.Pred[i + (entry & 1) * (cb.Order - 1)];
            ecIx[i + 1] = (short)(((entry >> 5) & 7) * 9);
            predQ8[i + 1] = cb.Pred[i + ((entry >> 4) & 1) * (cb.Order - 1) + 1];
        }
    }

    /// <summary>Decodes the side information of one frame (silk_decode_indices).</summary>
    public void DecodeIndices(RangeDecoder dec, int frameIndex, bool decodeLbrr, int condCoding)
    {
        int ix;
        if (decodeLbrr || VadFlags[frameIndex] != 0) ix = dec.DecodeIcdf(SilkTables.TypeOffsetVad, 8) + 2;
        else ix = dec.DecodeIcdf(SilkTables.TypeOffsetNoVad, 8);
        Indices.SignalType = (sbyte)(ix >> 1);
        Indices.QuantOffsetType = (sbyte)(ix & 1);

        if (condCoding == CodeConditionally) Indices.GainsIndices[0] = (sbyte)dec.DecodeIcdf(SilkTables.DeltaGainIcdf, 8);
        else
        {
            Indices.GainsIndices[0] = (sbyte)(dec.DecodeIcdf(SilkTables.GainIcdf.Slice(Indices.SignalType * 8, 8), 8) << 3);
            Indices.GainsIndices[0] += (sbyte)dec.DecodeIcdf(SilkTables.Uniform8, 8);
        }
        for (int i = 1; i < NbSubfr; i++) Indices.GainsIndices[i] = (sbyte)dec.DecodeIcdf(SilkTables.DeltaGainIcdf, 8);

        Indices.NlsfIndices[0] = (sbyte)dec.DecodeIcdf(Codebook.Cb1Icdf.AsSpan((Indices.SignalType >> 1) * SilkNlsfCodebook.Vectors), 8);
        Span<short> ecIx = stackalloc short[MaxLpcOrder];
        Span<byte> predQ8 = stackalloc byte[MaxLpcOrder];
        NlsfUnpack(ecIx, predQ8, Codebook, Indices.NlsfIndices[0]);
        for (int i = 0; i < Codebook.Order; i++)
        {
            ix = dec.DecodeIcdf(Codebook.EcIcdf.AsSpan(ecIx[i]), 8);
            if (ix == 0) ix -= dec.DecodeIcdf(SilkTables.NlsfExtIcdf, 8);
            else if (ix == 8) ix += dec.DecodeIcdf(SilkTables.NlsfExtIcdf, 8);
            Indices.NlsfIndices[i + 1] = (sbyte)(ix - 4);
        }
        Indices.NlsfInterpCoefQ2 = NbSubfr == 4 ? (sbyte)dec.DecodeIcdf(SilkTables.NlsfInterpolationIcdf, 8) : (sbyte)4;

        if (Indices.SignalType == TypeVoiced)
        {
            bool decodeAbsolute = true;
            if (condCoding == CodeConditionally && EcPrevSignalType == TypeVoiced)
            {
                int delta = dec.DecodeIcdf(SilkTables.PitchDeltaIcdf, 8);
                if (delta > 0)
                {
                    delta -= 9;
                    Indices.LagIndex = (short)(EcPrevLagIndex + delta);
                    decodeAbsolute = false;
                }
            }
            if (decodeAbsolute)
            {
                Indices.LagIndex = (short)(dec.DecodeIcdf(SilkTables.PitchLagIcdf, 8) * (FsKHz >> 1));
                Indices.LagIndex += (short)dec.DecodeIcdf(pitchLagLowBitsIcdf, 8);
            }
            EcPrevLagIndex = Indices.LagIndex;
            Indices.ContourIndex = (sbyte)dec.DecodeIcdf(pitchContourIcdf, 8);
            Indices.PerIndex = (sbyte)dec.DecodeIcdf(SilkTables.LtpPerIndexIcdf, 8);
            for (int k = 0; k < NbSubfr; k++)
            {
                ReadOnlySpan<byte> table = Indices.PerIndex == 0 ? SilkTables.LtpGainIcdf0 : Indices.PerIndex == 1 ? SilkTables.LtpGainIcdf1 : SilkTables.LtpGainIcdf2;
                Indices.LtpIndex[k] = (sbyte)dec.DecodeIcdf(table, 8);
            }
            Indices.LtpScaleIndex = condCoding == CodeIndependently ? (sbyte)dec.DecodeIcdf(SilkTables.LtpScaleIcdf, 8) : (sbyte)0;
        }
        EcPrevSignalType = Indices.SignalType;
        Indices.Seed = (sbyte)dec.DecodeIcdf(SilkTables.Uniform4, 8);
    }

    /// <summary>Decodes the excitation pulses (silk_decode_pulses).</summary>
    public static void DecodePulses(RangeDecoder dec, int[] pulses, int signalType, int quantOffsetType, int frameLength)
    {
        int rateLevel = dec.DecodeIcdf(SilkTables.RateLevelsIcdf.Slice((signalType >> 1) * 9, 9), 8);
        int iter = frameLength >> 4;
        if (iter * 16 < frameLength) iter++;
        Span<int> sumPulses = stackalloc int[20];
        Span<int> lshifts = stackalloc int[20];
        ReadOnlySpan<byte> pulseTables = SilkTables.PulsesPerBlockIcdf;
        for (int i = 0; i < iter; i++)
        {
            lshifts[i] = 0;
            sumPulses[i] = dec.DecodeIcdf(pulseTables.Slice(rateLevel * 18, 18), 8);
            while (sumPulses[i] == 17)
            {
                lshifts[i]++;
                sumPulses[i] = dec.DecodeIcdf(pulseTables.Slice(9 * 18 + (lshifts[i] == 10 ? 1 : 0), 18), 8);
            }
        }
        for (int i = 0; i < iter; i++)
        {
            Span<int> block = pulses.AsSpan(i * 16, 16);
            if (sumPulses[i] > 0) ShellDecode(block, dec, sumPulses[i]);
            else block.Clear();
        }
        for (int i = 0; i < iter; i++)
        {
            if (lshifts[i] <= 0) continue;
            int nLs = lshifts[i];
            Span<int> block = pulses.AsSpan(i * 16, 16);
            for (int k = 0; k < 16; k++)
            {
                int absQ = block[k];
                for (int j = 0; j < nLs; j++)
                {
                    absQ <<= 1;
                    absQ += dec.DecodeIcdf(SilkTables.LsbIcdf, 8);
                }
                block[k] = absQ;
            }
            sumPulses[i] |= nLs << 5;
        }
        DecodeSigns(dec, pulses, frameLength, signalType, quantOffsetType, sumPulses);
    }

    private static void DecodeSigns(RangeDecoder dec, int[] pulses, int length, int signalType, int quantOffsetType, ReadOnlySpan<int> sumPulses)
    {
        Span<byte> icdf = stackalloc byte[2];
        icdf[1] = 0;
        int baseIndex = 7 * (quantOffsetType + (signalType << 1));
        int blocks = (length + 8) >> 4;
        for (int i = 0; i < blocks; i++)
        {
            int p = sumPulses[i];
            if (p > 0)
            {
                icdf[0] = SilkTables.SignIcdf[baseIndex + Math.Min(p & 0x1F, 6)];
                for (int j = 0; j < 16; j++)
                {
                    int index = i * 16 + j;
                    if (pulses[index] > 0) pulses[index] *= (dec.DecodeIcdf(icdf, 8) << 1) - 1;
                }
            }
        }
    }

    private static void DecodeSplit(out int child1, out int child2, RangeDecoder dec, int p, ReadOnlySpan<byte> table)
    {
        if (p > 0)
        {
            child1 = dec.DecodeIcdf(table[SilkTables.ShellCodeTableOffsets[p]..], 8);
            child2 = p - child1;
        }
        else
        {
            child1 = 0;
            child2 = 0;
        }
    }

    private static void ShellDecode(Span<int> p0, RangeDecoder dec, int p4)
    {
        Span<int> p3 = stackalloc int[2], p2 = stackalloc int[4], p1 = stackalloc int[8];
        DecodeSplit(out p3[0], out p3[1], dec, p4, SilkTables.ShellCodeTable3);
        DecodeSplit(out p2[0], out p2[1], dec, p3[0], SilkTables.ShellCodeTable2);
        DecodeSplit(out p1[0], out p1[1], dec, p2[0], SilkTables.ShellCodeTable1);
        DecodeSplit(out p0[0], out p0[1], dec, p1[0], SilkTables.ShellCodeTable0);
        DecodeSplit(out p0[2], out p0[3], dec, p1[1], SilkTables.ShellCodeTable0);
        DecodeSplit(out p1[2], out p1[3], dec, p2[1], SilkTables.ShellCodeTable1);
        DecodeSplit(out p0[4], out p0[5], dec, p1[2], SilkTables.ShellCodeTable0);
        DecodeSplit(out p0[6], out p0[7], dec, p1[3], SilkTables.ShellCodeTable0);
        DecodeSplit(out p2[2], out p2[3], dec, p3[1], SilkTables.ShellCodeTable2);
        DecodeSplit(out p1[4], out p1[5], dec, p2[2], SilkTables.ShellCodeTable1);
        DecodeSplit(out p0[8], out p0[9], dec, p1[4], SilkTables.ShellCodeTable0);
        DecodeSplit(out p0[10], out p0[11], dec, p1[5], SilkTables.ShellCodeTable0);
        DecodeSplit(out p1[6], out p1[7], dec, p2[3], SilkTables.ShellCodeTable1);
        DecodeSplit(out p0[12], out p0[13], dec, p1[6], SilkTables.ShellCodeTable0);
        DecodeSplit(out p0[14], out p0[15], dec, p1[7], SilkTables.ShellCodeTable0);
    }

    // ---- parameters ----------------------------------------------------------------------------------------------------------------------

    private static void GainsDequant(int[] gainQ16, sbyte[] ind, ref sbyte prevInd, bool conditional, int nbSubfr)
    {
        const int Offset = 2 * 128 / 6 + 16 * 128;
        const int InvScaleQ16 = 65536 * ((88 - 2) * 128 / 6) / 63;
        for (int k = 0; k < nbSubfr; k++)
        {
            if (k == 0 && !conditional) prevInd = (sbyte)Math.Max(ind[k], prevInd - 16);
            else
            {
                int indTmp = ind[k] - 4;
                int doubleStepThreshold = 2 * 36 - 64 + prevInd;
                if (indTmp > doubleStepThreshold) prevInd = (sbyte)(prevInd + (indTmp << 1) - doubleStepThreshold);
                else prevInd = (sbyte)(prevInd + indTmp);
            }
            prevInd = (sbyte)Limit(prevInd, 0, 63);
            gainQ16[k] = Log2Lin(Math.Min(Smulwb(InvScaleQ16, prevInd) + Offset, 3967));
        }
    }

    private static void NlsfVqWeightsLaroia(Span<short> w, ReadOnlySpan<short> nlsf, int d)
    {
        int tmp1 = Math.Max((int)nlsf[0], 1);
        tmp1 = (1 << 17) / tmp1;
        int tmp2 = Math.Max(nlsf[1] - nlsf[0], 1);
        tmp2 = (1 << 17) / tmp2;
        w[0] = (short)Math.Min(tmp1 + tmp2, short.MaxValue);
        for (int k = 1; k < d - 1; k += 2)
        {
            tmp1 = Math.Max(nlsf[k + 1] - nlsf[k], 1);
            tmp1 = (1 << 17) / tmp1;
            w[k] = (short)Math.Min(tmp1 + tmp2, short.MaxValue);
            tmp2 = Math.Max(nlsf[k + 2] - nlsf[k + 1], 1);
            tmp2 = (1 << 17) / tmp2;
            w[k + 1] = (short)Math.Min(tmp1 + tmp2, short.MaxValue);
        }
        tmp1 = Math.Max((1 << 15) - nlsf[d - 1], 1);
        tmp1 = (1 << 17) / tmp1;
        w[d - 1] = (short)Math.Min(tmp1 + tmp2, short.MaxValue);
    }

    private static void NlsfStabilize(Span<short> nlsf, ReadOnlySpan<short> deltaMin, int l)
    {
        int loops;
        for (loops = 0; loops < 20; loops++)
        {
            int minDiff = nlsf[0] - deltaMin[0];
            int worst = 0;
            for (int i = 1; i <= l - 1; i++)
            {
                int diff = nlsf[i] - (nlsf[i - 1] + deltaMin[i]);
                if (diff < minDiff)
                {
                    minDiff = diff;
                    worst = i;
                }
            }
            int last = (1 << 15) - (nlsf[l - 1] + deltaMin[l]);
            if (last < minDiff)
            {
                minDiff = last;
                worst = l;
            }
            if (minDiff >= 0) return;
            if (worst == 0) nlsf[0] = deltaMin[0];
            else if (worst == l) nlsf[l - 1] = (short)((1 << 15) - deltaMin[l]);
            else
            {
                int minCenter = 0;
                for (int k = 0; k < worst; k++) minCenter += deltaMin[k];
                minCenter += deltaMin[worst] >> 1;
                int maxCenter = 1 << 15;
                for (int k = l; k > worst; k--) maxCenter -= deltaMin[k];
                maxCenter -= deltaMin[worst] >> 1;
                short center = (short)Limit(RShiftRound(nlsf[worst - 1] + nlsf[worst], 1), minCenter, maxCenter);
                nlsf[worst - 1] = (short)(center - (deltaMin[worst] >> 1));
                nlsf[worst] = (short)(nlsf[worst - 1] + deltaMin[worst]);
            }
        }
        // Fallback: sort, then enforce the minimum spacing from both ends.
        for (int i = 1; i < l; i++)
        {
            short value = nlsf[i];
            int j;
            for (j = i - 1; j >= 0 && value < nlsf[j]; j--) nlsf[j + 1] = nlsf[j];
            nlsf[j + 1] = value;
        }
        nlsf[0] = (short)Math.Max((int)nlsf[0], (int)deltaMin[0]);
        for (int i = 1; i < l; i++) nlsf[i] = (short)Math.Max((int)nlsf[i], Math.Min(nlsf[i - 1] + deltaMin[i], short.MaxValue));
        nlsf[l - 1] = (short)Math.Min(nlsf[l - 1], (1 << 15) - deltaMin[l]);
        for (int i = l - 2; i >= 0; i--) nlsf[i] = (short)Math.Min((int)nlsf[i], nlsf[i + 1] - deltaMin[i + 1]);
    }

    private void NlsfDecode(Span<short> nlsf)
    {
        SilkNlsfCodebook cb = Codebook;
        int order = cb.Order;
        Span<short> res = stackalloc short[MaxLpcOrder];
        Span<byte> predQ8 = stackalloc byte[MaxLpcOrder];
        Span<short> ecIx = stackalloc short[MaxLpcOrder];
        Span<short> weights = stackalloc short[MaxLpcOrder];
        int cbIndex = Indices.NlsfIndices[0] * order;
        for (int i = 0; i < order; i++) nlsf[i] = (short)(cb.Cb1Nlsf[cbIndex + i] << 7);
        NlsfUnpack(ecIx, predQ8, cb, Indices.NlsfIndices[0]);
        int outQ10 = 0;
        for (int i = order - 1; i >= 0; i--)
        {
            int predQ10 = Smulbb(outQ10, predQ8[i]) >> 8;
            outQ10 = Indices.NlsfIndices[1 + i] << 10;
            if (outQ10 > 0) outQ10 = (short)(outQ10 - 102);
            else if (outQ10 < 0) outQ10 = (short)(outQ10 + 102);
            outQ10 = Smlawb(predQ10, outQ10, cb.QuantStepSizeQ16);
            res[i] = (short)outQ10;
        }
        NlsfVqWeightsLaroia(weights, nlsf, order);
        for (int i = 0; i < order; i++)
        {
            int wQ9 = SqrtApprox(weights[i] << 16);
            int tmp = nlsf[i] + ((res[i] << 14) / wQ9);
            nlsf[i] = (short)Limit(tmp, 0, 32767);
        }
        NlsfStabilize(nlsf, cb.DeltaMin, order);
    }

    private static void Nlsf2A(Span<short> aQ12, ReadOnlySpan<short> nlsf, int d)
    {
        const int Qa = 16;
        ReadOnlySpan<byte> ordering = d == 16 ? [0, 15, 8, 7, 4, 11, 12, 3, 2, 13, 10, 5, 6, 9, 14, 1] : [0, 9, 6, 3, 4, 5, 8, 1, 2, 7];
        Span<int> cosLsf = stackalloc int[16];
        Span<int> p = stackalloc int[9], q = stackalloc int[9], a32 = stackalloc int[16];
        for (int k = 0; k < d; k++)
        {
            int fInt = nlsf[k] >> 8;
            int fFrac = nlsf[k] - (fInt << 8);
            int cosVal = SilkTables.LsfCosTabQ12[fInt];
            int delta = SilkTables.LsfCosTabQ12[fInt + 1] - cosVal;
            cosLsf[ordering[k]] = RShiftRound((cosVal << 8) + delta * fFrac, 20 - Qa);
        }
        int dd = d >> 1;
        FindPoly(p, cosLsf, dd);
        FindPoly(q, cosLsf[1..], dd);
        for (int k = 0; k < dd; k++)
        {
            int ptmp = p[k + 1] + p[k];
            int qtmp = q[k + 1] - q[k];
            a32[k] = -qtmp - ptmp;
            a32[d - k - 1] = qtmp - ptmp;
        }
        int i;
        for (i = 0; i < 10; i++)
        {
            int maxAbs = 0, idx = 0;
            for (int k = 0; k < d; k++)
            {
                int absVal = Abs(a32[k]);
                if (absVal > maxAbs)
                {
                    maxAbs = absVal;
                    idx = k;
                }
            }
            maxAbs = RShiftRound(maxAbs, Qa + 1 - 12);
            if (maxAbs > short.MaxValue)
            {
                maxAbs = Math.Min(maxAbs, 163838);
                int sc = (int)(0.999 * 65536 + 0.5) - ((maxAbs - short.MaxValue) << 14) / ((maxAbs * (idx + 1)) >> 2);
                BwExpander32(a32, d, sc);
            }
            else break;
        }
        if (i == 10)
        {
            for (int k = 0; k < d; k++)
            {
                aQ12[k] = (short)Sat16(RShiftRound(a32[k], Qa + 1 - 12));
                a32[k] = aQ12[k] << (Qa + 1 - 12);
            }
        }
        else
        {
            for (int k = 0; k < d; k++) aQ12[k] = (short)RShiftRound(a32[k], Qa + 1 - 12);
        }
        for (i = 0; i < 16; i++)
        {
            if (LpcInversePredGain(aQ12, d) < (int)(1.0 / 1e4 * (1L << 30) + 0.5))
            {
                BwExpander32(a32, d, 65536 - (2 << i));
                for (int k = 0; k < d; k++) aQ12[k] = (short)RShiftRound(a32[k], Qa + 1 - 12);
            }
            else break;
        }
    }

    private static void FindPoly(Span<int> output, ReadOnlySpan<int> cLsf, int dd)
    {
        const int Qa = 16;
        output[0] = 1 << Qa;
        output[1] = -cLsf[0];
        for (int k = 1; k < dd; k++)
        {
            int ftmp = cLsf[2 * k];
            output[k + 1] = unchecked((output[k - 1] << 1) - (int)RShiftRound64((long)ftmp * output[k], Qa));
            for (int n = k; n > 1; n--) output[n] = unchecked(output[n] + output[n - 2] - (int)RShiftRound64((long)ftmp * output[n - 1], Qa));
            output[1] -= ftmp;
        }
    }

    private void DecodePitch(int[] pitchLags)
    {
        ReadOnlySpan<sbyte> cb;
        int cbSize;
        if (FsKHz == 8)
        {
            if (NbSubfr == 4)
            {
                cb = SilkTables.LagStage2;
                cbSize = 11;
            }
            else
            {
                cb = SilkTables.LagStage2Ms10;
                cbSize = 3;
            }
        }
        else if (NbSubfr == 4)
        {
            cb = SilkTables.LagStage3;
            cbSize = 34;
        }
        else
        {
            cb = SilkTables.LagStage3Ms10;
            cbSize = 12;
        }
        int minLag = 2 * FsKHz, maxLag = 18 * FsKHz;
        int lag = minLag + Indices.LagIndex;
        for (int k = 0; k < NbSubfr; k++) pitchLags[k] = Limit(lag + cb[k * cbSize + Indices.ContourIndex], minLag, maxLag);
    }

    private void DecodeParameters(int condCoding)
    {
        GainsDequant(control.GainsQ16, Indices.GainsIndices, ref LastGainIndex, condCoding == CodeConditionally, NbSubfr);
        Span<short> nlsf = stackalloc short[MaxLpcOrder];
        Span<short> nlsf0 = stackalloc short[MaxLpcOrder];
        NlsfDecode(nlsf);
        Nlsf2A(control.PredCoefQ12[1], nlsf, LpcOrder);
        if (FirstFrameAfterReset) Indices.NlsfInterpCoefQ2 = 4;
        if (Indices.NlsfInterpCoefQ2 < 4)
        {
            for (int i = 0; i < LpcOrder; i++) nlsf0[i] = (short)(PrevNlsfQ15[i] + ((Indices.NlsfInterpCoefQ2 * (nlsf[i] - PrevNlsfQ15[i])) >> 2));
            Nlsf2A(control.PredCoefQ12[0], nlsf0, LpcOrder);
        }
        else Array.Copy(control.PredCoefQ12[1], control.PredCoefQ12[0], LpcOrder);
        nlsf[..LpcOrder].CopyTo(PrevNlsfQ15);
        if (LossCnt != 0)
        {
            BwExpander(control.PredCoefQ12[0], LpcOrder, 63570);
            BwExpander(control.PredCoefQ12[1], LpcOrder, 63570);
        }
        if (Indices.SignalType == TypeVoiced)
        {
            DecodePitch(control.PitchL);
            ReadOnlySpan<sbyte> cbk = Indices.PerIndex == 0 ? SilkTables.LtpVq0 : Indices.PerIndex == 1 ? SilkTables.LtpVq1 : SilkTables.LtpVq2;
            for (int k = 0; k < NbSubfr; k++)
            {
                int ix = Indices.LtpIndex[k];
                for (int i = 0; i < LtpOrder; i++) control.LtpCoefQ14[k * LtpOrder + i] = (short)(cbk[ix * LtpOrder + i] << 7);
            }
            control.LtpScaleQ14 = SilkTables.LtpScalesQ14[Indices.LtpScaleIndex];
        }
        else
        {
            Array.Clear(control.PitchL, 0, NbSubfr);
            Array.Clear(control.LtpCoefQ14, 0, LtpOrder * NbSubfr);
            Indices.PerIndex = 0;
            control.LtpScaleQ14 = 0;
        }
    }
}
