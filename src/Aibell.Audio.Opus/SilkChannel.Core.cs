// This file is derived from the Opus reference implementation published in RFC 6716 (silk/*.c).
// Copyright 1994-2011 IETF Trust, Xiph.Org, Skype Limited, Octasic, Jean-Marc Valin, Timothy B. Terriberry,
// CSIRO, Gregory Maxwell, Mark Borgerding, Erik de Castro Lopo. All rights reserved.
// Redistribution and use are permitted under the BSD terms reproduced in COPYING.Opus.
using static Aibell.Audio.Opus.SilkMath;

namespace Aibell.Audio.Opus;

internal sealed partial class SilkChannel
{
    private const int QuantLevelAdjustQ10 = 80;
    private const int CngBufMaskMax = 255;
    private const int CngGainSmthQ16 = 4634;
    private const int CngNlsfSmthQ16 = 16348;
    private const int RandBufSize = 128;
    private const int RandBufMask = RandBufSize - 1;
    private const int PitchDriftFacQ16 = 655;
    private const int MaxPitchLagMs = 18;
    private const int VPitchGainStartMinQ14 = 11469;
    private const int VPitchGainStartMaxQ14 = 15565;

    // Comfort noise state.
    private int cngFsKHz;
    private readonly short[] cngSmthNlsfQ15 = new short[MaxLpcOrder];
    private readonly int[] cngExcBufQ14 = new int[MaxFrameLength];
    private int cngSmthGainQ16;
    private int cngRandSeed;
    private readonly int[] cngSynthState = new int[MaxLpcOrder];

    // Packet loss concealment state.
    private int plcFsKHz;
    private int plcPitchLQ8;
    private readonly short[] plcLtpCoefQ14 = new short[LtpOrder];
    private readonly short[] plcPrevLpcQ12 = new short[MaxLpcOrder];
    private bool plcLastFrameLost;
    private int plcRandSeed;
    private int plcRandScaleQ14;
    private int plcConcEnergy;
    private int plcConcEnergyShift;
    private int plcPrevLtpScaleQ14;
    private readonly int[] plcPrevGainQ16 = new int[2];
    private int plcSubfrLength;
    private int plcNbSubfr;
    private readonly short[] plcExcBuf = new short[2 * MaxSubFrameLength];

    private void ResetCng()
    {
        cngFsKHz = 0;
        Array.Clear(cngExcBufQ14);
        Array.Clear(cngSynthState);
        ResetCngNlsf();
    }

    private void ResetCngNlsf()
    {
        int step = short.MaxValue / (LpcOrder + 1);
        int acc = 0;
        for (int i = 0; i < LpcOrder; i++)
        {
            acc += step;
            cngSmthNlsfQ15[i] = (short)acc;
        }
        cngSmthGainQ16 = 0;
        cngRandSeed = 3176576;
    }

    private void ResetPlc()
    {
        plcFsKHz = 0;
        Array.Clear(plcLtpCoefQ14);
        Array.Clear(plcPrevLpcQ12);
        plcLastFrameLost = false;
        plcRandSeed = 0;
        plcRandScaleQ14 = 0;
        plcConcEnergy = 0;
        plcConcEnergyShift = 0;
        plcPrevLtpScaleQ14 = 0;
        ResetPlcRate();
    }

    private void ResetPlcRate()
    {
        plcPitchLQ8 = FrameLength << (8 - 1);
        plcPrevGainQ16[0] = 65536;
        plcPrevGainQ16[1] = 65536;
        plcSubfrLength = 20;
        plcNbSubfr = 2;
    }

    // ---- frame decoding -------------------------------------------------------------------------------------------------------------------

    /// <summary>Decodes (or conceals) one frame into <paramref name="pOut"/> and returns its length (silk_decode_frame).</summary>
    public int DecodeFrame(RangeDecoder dec, Span<short> pOut, int lostFlag, int condCoding)
    {
        int length = FrameLength;
        control.LtpScaleQ14 = 0;
        if (lostFlag == FlagDecodeNormal || (lostFlag == FlagDecodeLbrr && LbrrFlags[NFramesDecoded] == 1))
        {
            DecodeIndices(dec, NFramesDecoded, lostFlag == FlagDecodeLbrr, condCoding);
            DecodePulses(dec, pulses, Indices.SignalType, Indices.QuantOffsetType, FrameLength);
            DecodeParameters(condCoding);
            length = FrameLength;
            DecodeCore(pOut, pulses);
            Plc(pOut, false);
            LossCnt = 0;
            PrevSignalType = Indices.SignalType;
            FirstFrameAfterReset = false;
        }
        else Plc(pOut, true);

        int mvLen = LtpMemLength - FrameLength;
        Array.Copy(OutBuf, FrameLength, OutBuf, 0, mvLen);
        pOut[..FrameLength].CopyTo(OutBuf.AsSpan(mvLen));
        PlcGlueFrames(pOut, length);
        Cng(pOut, length);
        LagPrev = control.PitchL[NbSubfr - 1];
        return length;
    }

    private static void LpcAnalysisFilter(Span<short> output, ReadOnlySpan<short> input, ReadOnlySpan<short> b, int len, int d)
    {
        for (int ix = d; ix < len; ix++)
        {
            int p = ix - 1;
            int acc = Smulbb(input[p], b[0]);
            for (int j = 1; j < d; j++) acc = Smlabb(acc, input[p - j], b[j]);
            acc = unchecked((input[p + 1] << 12) - acc);
            output[ix] = (short)Sat16(RShiftRound(acc, 12));
        }
        output[..d].Clear();
    }

    private static void SumSqrShift(out int energy, out int shift, ReadOnlySpan<short> x, int len)
    {
        int nrg = 0, shft = 0, i;
        len--;
        for (i = 0; i < len; i += 2)
        {
            nrg = unchecked(nrg + x[i] * x[i] + x[i + 1] * x[i + 1]);
            if (nrg < 0)
            {
                nrg = (int)((uint)nrg >> 2);
                shft = 2;
                break;
            }
        }
        for (; i < len; i += 2)
        {
            int tmp = x[i] * x[i];
            tmp = unchecked(tmp + x[i + 1] * x[i + 1]);
            nrg = unchecked(nrg + (int)((uint)tmp >> shft));
            if (nrg < 0)
            {
                nrg = (int)((uint)nrg >> 2);
                shft += 2;
            }
        }
        if (i == len)
        {
            int tmp = x[i] * x[i];
            nrg = unchecked(nrg + (int)((uint)tmp >> shft));
        }
        if ((nrg & 0xC0000000) != 0)
        {
            nrg = (int)((uint)nrg >> 2);
            shft += 2;
        }
        shift = shft;
        energy = nrg;
    }

    /// <summary>Inverse NSQ: long-term and short-term synthesis (silk_decode_core).</summary>
    private void DecodeCore(Span<short> xq, int[] pulseSignal)
    {
        SilkControl ctrl = control;
        int offsetQ10 = SilkTables.QuantizationOffsetsQ10[(Indices.SignalType >> 1) * 2 + Indices.QuantOffsetType];
        bool interpolate = Indices.NlsfInterpCoefQ2 < 4;
        int lag = 0;

        int randSeed = Indices.Seed;
        for (int i = 0; i < FrameLength; i++)
        {
            randSeed = Rand(randSeed);
            int e = pulseSignal[i] << 14;
            if (e > 0) e -= QuantLevelAdjustQ10 << 4;
            else if (e < 0) e += QuantLevelAdjustQ10 << 4;
            e += offsetQ10 << 4;
            if (randSeed < 0) e = -e;
            ExcQ14[i] = e;
            randSeed = unchecked(randSeed + pulseSignal[i]);
        }

        Array.Copy(SLpcQ14Buf, sLpcQ14, MaxLpcOrder);
        int excPos = 0;
        int xqPos = 0;
        int sLtpBufIdx = LtpMemLength;
        Span<short> aTmp = stackalloc short[MaxLpcOrder];
        for (int k = 0; k < NbSubfr; k++)
        {
            short[] aQ12 = ctrl.PredCoefQ12[k >> 1];
            aQ12.AsSpan(0, LpcOrder).CopyTo(aTmp);
            int bOff = k * LtpOrder;
            short[] bQ14 = ctrl.LtpCoefQ14;
            int signalType = Indices.SignalType;
            int gainQ10 = ctrl.GainsQ16[k] >> 6;
            int invGainQ31 = Inverse32VarQ(ctrl.GainsQ16[k], 47);

            int gainAdjQ16;
            if (ctrl.GainsQ16[k] != PrevGainQ16)
            {
                gainAdjQ16 = Div32VarQ(PrevGainQ16, ctrl.GainsQ16[k], 16);
                for (int i = 0; i < MaxLpcOrder; i++) sLpcQ14[i] = Smulww(gainAdjQ16, sLpcQ14[i]);
            }
            else gainAdjQ16 = 1 << 16;
            PrevGainQ16 = ctrl.GainsQ16[k];

            if (LossCnt != 0 && PrevSignalType == TypeVoiced && Indices.SignalType != TypeVoiced && k < 2)
            {
                Array.Clear(bQ14, bOff, LtpOrder);
                bQ14[bOff + LtpOrder / 2] = 1 << 12;
                signalType = TypeVoiced;
                ctrl.PitchL[k] = LagPrev;
            }

            Span<int> res;
            if (signalType == TypeVoiced)
            {
                lag = ctrl.PitchL[k];
                if (k == 0 || (k == 2 && interpolate))
                {
                    int startIdx = LtpMemLength - lag - LpcOrder - LtpOrder / 2;
                    if (k == 2) xq[..(2 * SubfrLength)].CopyTo(OutBuf.AsSpan(LtpMemLength));
                    LpcAnalysisFilter(sLtp.AsSpan(startIdx), OutBuf.AsSpan(startIdx + k * SubfrLength), aQ12, LtpMemLength - startIdx, LpcOrder);
                    if (k == 0) invGainQ31 = Smulwb(invGainQ31, ctrl.LtpScaleQ14) << 2;
                    for (int i = 0; i < lag + LtpOrder / 2; i++) sLtpQ15[sLtpBufIdx - i - 1] = Smulwb(invGainQ31, sLtp[LtpMemLength - i - 1]);
                }
                else if (gainAdjQ16 != 1 << 16)
                {
                    for (int i = 0; i < lag + LtpOrder / 2; i++) sLtpQ15[sLtpBufIdx - i - 1] = Smulww(gainAdjQ16, sLtpQ15[sLtpBufIdx - i - 1]);
                }

                int pred = sLtpBufIdx - lag + LtpOrder / 2;
                for (int i = 0; i < SubfrLength; i++)
                {
                    int ltp = 2;
                    ltp = Smlawb(ltp, sLtpQ15[pred], bQ14[bOff]);
                    ltp = Smlawb(ltp, sLtpQ15[pred - 1], bQ14[bOff + 1]);
                    ltp = Smlawb(ltp, sLtpQ15[pred - 2], bQ14[bOff + 2]);
                    ltp = Smlawb(ltp, sLtpQ15[pred - 3], bQ14[bOff + 3]);
                    ltp = Smlawb(ltp, sLtpQ15[pred - 4], bQ14[bOff + 4]);
                    pred++;
                    resQ14[i] = unchecked(ExcQ14[excPos + i] + (ltp << 1));
                    sLtpQ15[sLtpBufIdx] = resQ14[i] << 1;
                    sLtpBufIdx++;
                }
                res = resQ14;
            }
            else res = ExcQ14.AsSpan(excPos);

            for (int i = 0; i < SubfrLength; i++)
            {
                int acc = LpcOrder >> 1;
                int basePos = MaxLpcOrder + i - 1;
                for (int j = 0; j < LpcOrder; j++) acc = Smlawb(acc, sLpcQ14[basePos - j], aTmp[j]);
                sLpcQ14[MaxLpcOrder + i] = unchecked(res[i] + (acc << 4));
                xq[xqPos + i] = (short)Sat16(RShiftRound(Smulww(sLpcQ14[MaxLpcOrder + i], gainQ10), 8));
            }
            Array.Copy(sLpcQ14, SubfrLength, sLpcQ14, 0, MaxLpcOrder);
            excPos += SubfrLength;
            xqPos += SubfrLength;
        }
        Array.Copy(sLpcQ14, SLpcQ14Buf, MaxLpcOrder);
    }

    // ---- packet loss concealment ----------------------------------------------------------------------------------------------------------

    private void Plc(Span<short> frame, bool lost)
    {
        if (FsKHz != plcFsKHz)
        {
            ResetPlcRate();
            plcFsKHz = FsKHz;
        }
        if (lost)
        {
            PlcConceal(frame);
            LossCnt++;
        }
        else PlcUpdate();
    }

    private void PlcUpdate()
    {
        SilkControl ctrl = control;
        PrevSignalType = Indices.SignalType;
        int ltpGainQ14 = 0;
        if (Indices.SignalType == TypeVoiced)
        {
            for (int j = 0; j * SubfrLength < ctrl.PitchL[NbSubfr - 1]; j++)
            {
                if (j == NbSubfr) break;
                int temp = 0;
                for (int i = 0; i < LtpOrder; i++) temp += ctrl.LtpCoefQ14[(NbSubfr - 1 - j) * LtpOrder + i];
                if (temp > ltpGainQ14)
                {
                    ltpGainQ14 = temp;
                    Array.Copy(ctrl.LtpCoefQ14, (NbSubfr - 1 - j) * LtpOrder, plcLtpCoefQ14, 0, LtpOrder);
                    plcPitchLQ8 = ctrl.PitchL[NbSubfr - 1 - j] << 8;
                }
            }
            Array.Clear(plcLtpCoefQ14);
            plcLtpCoefQ14[LtpOrder / 2] = (short)ltpGainQ14;
            if (ltpGainQ14 < VPitchGainStartMinQ14)
            {
                int scaleQ10 = (VPitchGainStartMinQ14 << 10) / Math.Max(ltpGainQ14, 1);
                for (int i = 0; i < LtpOrder; i++) plcLtpCoefQ14[i] = (short)(Smulbb(plcLtpCoefQ14[i], scaleQ10) >> 10);
            }
            else if (ltpGainQ14 > VPitchGainStartMaxQ14)
            {
                int scaleQ14 = (VPitchGainStartMaxQ14 << 14) / Math.Max(ltpGainQ14, 1);
                for (int i = 0; i < LtpOrder; i++) plcLtpCoefQ14[i] = (short)(Smulbb(plcLtpCoefQ14[i], scaleQ14) >> 14);
            }
        }
        else
        {
            plcPitchLQ8 = Smulbb(FsKHz, 18) << 8;
            Array.Clear(plcLtpCoefQ14);
        }
        Array.Copy(ctrl.PredCoefQ12[1], plcPrevLpcQ12, LpcOrder);
        plcPrevLtpScaleQ14 = ctrl.LtpScaleQ14;
        plcPrevGainQ16[0] = ctrl.GainsQ16[NbSubfr - 2];
        plcPrevGainQ16[1] = ctrl.GainsQ16[NbSubfr - 1];
        plcSubfrLength = SubfrLength;
        plcNbSubfr = NbSubfr;
    }

    private static readonly int[] HarmAttQ15 = [32440, 31130];
    private static readonly int[] RandAttenuateVQ15 = [31130, 26214];
    private static readonly int[] RandAttenuateUvQ15 = [32440, 29491];

    private void PlcConceal(Span<short> frame)
    {
        SilkControl ctrl = control;
        Span<short> a = stackalloc short[MaxLpcOrder];
        Span<int> prevGainQ10 = [plcPrevGainQ16[0] >> 6, plcPrevGainQ16[1] >> 6];
        if (FirstFrameAfterReset) Array.Clear(plcPrevLpcQ12);

        for (int k = 0; k < 2; k++)
        {
            for (int i = 0; i < plcSubfrLength; i++)
            {
                plcExcBuf[k * plcSubfrLength + i] = (short)Sat16(Smulww(ExcQ14[i + (k + plcNbSubfr - 2) * plcSubfrLength], prevGainQ10[k]) >> 8);
            }
        }
        SumSqrShift(out int energy1, out int shift1, plcExcBuf, plcSubfrLength);
        SumSqrShift(out int energy2, out int shift2, plcExcBuf.AsSpan(plcSubfrLength), plcSubfrLength);
        int randPtr = (energy1 >> shift2) < (energy2 >> shift1)
            ? Math.Max(0, (plcNbSubfr - 1) * plcSubfrLength - RandBufSize)
            : Math.Max(0, plcNbSubfr * plcSubfrLength - RandBufSize);

        short[] b = plcLtpCoefQ14;
        int randScaleQ14 = plcRandScaleQ14;
        int harmGainQ15 = HarmAttQ15[Math.Min(1, LossCnt)];
        int randGainQ15 = PrevSignalType == TypeVoiced ? RandAttenuateVQ15[Math.Min(1, LossCnt)] : RandAttenuateUvQ15[Math.Min(1, LossCnt)];

        BwExpander(plcPrevLpcQ12, LpcOrder, 64881);
        plcPrevLpcQ12.AsSpan(0, LpcOrder).CopyTo(a);

        if (LossCnt == 0)
        {
            randScaleQ14 = 1 << 14;
            if (PrevSignalType == TypeVoiced)
            {
                for (int i = 0; i < LtpOrder; i++) randScaleQ14 -= b[i];
                randScaleQ14 = Math.Max(3277, randScaleQ14);
                randScaleQ14 = Smulbb(randScaleQ14, plcPrevLtpScaleQ14) >> 14;
            }
            else
            {
                int invGainQ30 = LpcInversePredGain(plcPrevLpcQ12, LpcOrder);
                int downScaleQ30 = Math.Min((1 << 30) >> 3, invGainQ30);
                downScaleQ30 = Math.Max((1 << 30) >> 8, downScaleQ30);
                downScaleQ30 <<= 3;
                randGainQ15 = Smulwb(downScaleQ30, randGainQ15) >> 14;
            }
        }

        int randSeed = plcRandSeed;
        int lag = RShiftRound(plcPitchLQ8, 8);
        int sLtpBufIdx = LtpMemLength;

        int idx = LtpMemLength - lag - LpcOrder - LtpOrder / 2;
        LpcAnalysisFilter(sLtp.AsSpan(idx), OutBuf.AsSpan(idx), a, LtpMemLength - idx, LpcOrder);
        int invGainQ30b = Math.Min(Inverse32VarQ(plcPrevGainQ16[1], 46), int.MaxValue >> 1);
        for (int i = idx + LpcOrder; i < LtpMemLength; i++) sLtpQ15[i] = Smulwb(invGainQ30b, sLtp[i]);

        for (int k = 0; k < NbSubfr; k++)
        {
            int pred = sLtpBufIdx - lag + LtpOrder / 2;
            for (int i = 0; i < SubfrLength; i++)
            {
                int ltp = 2;
                ltp = Smlawb(ltp, sLtpQ15[pred], b[0]);
                ltp = Smlawb(ltp, sLtpQ15[pred - 1], b[1]);
                ltp = Smlawb(ltp, sLtpQ15[pred - 2], b[2]);
                ltp = Smlawb(ltp, sLtpQ15[pred - 3], b[3]);
                ltp = Smlawb(ltp, sLtpQ15[pred - 4], b[4]);
                pred++;
                randSeed = Rand(randSeed);
                idx = (randSeed >> 25) & RandBufMask;
                sLtpQ15[sLtpBufIdx] = Smlawb(ltp, ExcQ14[randPtr + idx], (short)randScaleQ14) << 2;
                sLtpBufIdx++;
            }
            for (int j = 0; j < LtpOrder; j++) b[j] = (short)(Smulbb(harmGainQ15, b[j]) >> 15);
            randScaleQ14 = Smulbb(randScaleQ14, randGainQ15) >> 15;
            plcPitchLQ8 = Smlawb(plcPitchLQ8, plcPitchLQ8, PitchDriftFacQ16);
            plcPitchLQ8 = Math.Min(plcPitchLQ8, Smulbb(MaxPitchLagMs, FsKHz) << 8);
            lag = RShiftRound(plcPitchLQ8, 8);
        }

        int lpcBase = LtpMemLength - MaxLpcOrder;
        Array.Copy(SLpcQ14Buf, 0, sLtpQ15, lpcBase, MaxLpcOrder);
        for (int i = 0; i < FrameLength; i++)
        {
            int acc = LpcOrder >> 1;
            for (int j = 0; j < LpcOrder; j++) acc = Smlawb(acc, sLtpQ15[lpcBase + MaxLpcOrder + i - 1 - j], a[j]);
            sLtpQ15[lpcBase + MaxLpcOrder + i] = unchecked(sLtpQ15[lpcBase + MaxLpcOrder + i] + (acc << 4));
            frame[i] = (short)Sat16(RShiftRound(Smulww(sLtpQ15[lpcBase + MaxLpcOrder + i], prevGainQ10[1]), 8));
        }
        Array.Copy(sLtpQ15, lpcBase + FrameLength, SLpcQ14Buf, 0, MaxLpcOrder);

        plcRandSeed = randSeed;
        plcRandScaleQ14 = randScaleQ14;
        for (int i = 0; i < 4; i++) ctrl.PitchL[i] = lag;
    }

    private void PlcGlueFrames(Span<short> frame, int length)
    {
        if (LossCnt != 0)
        {
            SumSqrShift(out plcConcEnergy, out plcConcEnergyShift, frame, length);
            plcLastFrameLost = true;
            return;
        }
        if (plcLastFrameLost)
        {
            SumSqrShift(out int energy, out int energyShift, frame, length);
            if (energyShift > plcConcEnergyShift) plcConcEnergy >>= energyShift - plcConcEnergyShift;
            else if (energyShift < plcConcEnergyShift) energy >>= plcConcEnergyShift - energyShift;
            if (energy > plcConcEnergy)
            {
                int lz = Clz32(plcConcEnergy) - 1;
                plcConcEnergy <<= lz;
                energy >>= Math.Max(24 - lz, 0);
                int fracQ24 = plcConcEnergy / Math.Max(energy, 1);
                int gainQ16 = SqrtApprox(fracQ24) << 4;
                int slopeQ16 = ((1 << 16) - gainQ16) / length;
                slopeQ16 <<= 2;
                for (int i = 0; i < length; i++)
                {
                    frame[i] = (short)Smulwb(gainQ16, frame[i]);
                    gainQ16 += slopeQ16;
                    if (gainQ16 > 1 << 16) break;
                }
            }
        }
        plcLastFrameLost = false;
    }

    // ---- comfort noise --------------------------------------------------------------------------------------------------------------------

    private void Cng(Span<short> frame, int length)
    {
        if (FsKHz != cngFsKHz)
        {
            ResetCngNlsf();
            cngFsKHz = FsKHz;
        }
        if (LossCnt == 0 && PrevSignalType == TypeNoVoiceActivity)
        {
            for (int i = 0; i < LpcOrder; i++) cngSmthNlsfQ15[i] += (short)Smulwb(PrevNlsfQ15[i] - cngSmthNlsfQ15[i], CngNlsfSmthQ16);
            int maxGainQ16 = 0, subfr = 0;
            for (int i = 0; i < NbSubfr; i++)
            {
                if (control.GainsQ16[i] > maxGainQ16)
                {
                    maxGainQ16 = control.GainsQ16[i];
                    subfr = i;
                }
            }
            Array.Copy(cngExcBufQ14, 0, cngExcBufQ14, SubfrLength, (NbSubfr - 1) * SubfrLength);
            Array.Copy(ExcQ14, subfr * SubfrLength, cngExcBufQ14, 0, SubfrLength);
            for (int i = 0; i < NbSubfr; i++) cngSmthGainQ16 += Smulwb(control.GainsQ16[i] - cngSmthGainQ16, CngGainSmthQ16);
        }
        if (LossCnt != 0)
        {
            Span<int> sig = stackalloc int[MaxFrameLength + MaxLpcOrder];
            Span<short> a = stackalloc short[MaxLpcOrder];
            int mask = CngBufMaskMax;
            while (mask > length) mask >>= 1;
            int seed = cngRandSeed;
            for (int i = 0; i < length; i++)
            {
                seed = Rand(seed);
                int idx = (seed >> 24) & mask;
                sig[MaxLpcOrder + i] = (short)Sat16(Smulww(cngExcBufQ14[idx], cngSmthGainQ16 >> 4));
            }
            cngRandSeed = seed;
            Nlsf2A(a, cngSmthNlsfQ15, LpcOrder);
            cngSynthState.AsSpan(0, MaxLpcOrder).CopyTo(sig);
            for (int i = 0; i < length; i++)
            {
                int sum = LpcOrder >> 1;
                for (int j = 0; j < LpcOrder; j++) sum = Smlawb(sum, sig[MaxLpcOrder + i - 1 - j], a[j]);
                sig[MaxLpcOrder + i] = unchecked(sig[MaxLpcOrder + i] + (sum << 4));
                frame[i] = (short)Sat16(frame[i] + RShiftRound(sum, 6));
            }
            sig.Slice(length, MaxLpcOrder).CopyTo(cngSynthState);
        }
        else Array.Clear(cngSynthState, 0, LpcOrder);
    }
}
