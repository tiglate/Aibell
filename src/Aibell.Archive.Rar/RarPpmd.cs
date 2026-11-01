namespace Aibell.Archive.Rar;

/// <summary>
/// PPMd variant H (Dmitry Shkarin, public domain) as used by RAR 3.x text compression, with RAR's range decoder. The model lives in one
/// byte array addressed by offsets, exactly like the original memory layout: when the allocator runs out of memory the model restarts,
/// so the allocation behaviour is part of the format. Ported from the public-domain C implementation by Igor Pavlov as found in libarchive.
/// </summary>
internal sealed class RarPpmd
{
    private const int MaxOrder = 64;
    private const int UnitSize = 12;
    private const int MaxFreq = 124;
    private const int IntBits = 7;
    private const int PeriodBits = 7;
    private const int BinScale = 1 << IntBits + PeriodBits;
    private const int NumIndexes = 4 + 4 + 4 + (128 + 3 - 1 * 4 - 2 * 4 - 3 * 4) / 4;
    private const uint TopValue = 1 << 24;
    private const int DummySee = 25 * 16;

    private static readonly ushort[] InitBinEsc = [0x3CDD, 0x1F3F, 0x59BF, 0x48F3, 0x64A1, 0x5ABC, 0x6632, 0x6051];
    private static readonly byte[] ExpEscape = [25, 14, 9, 7, 5, 5, 4, 4, 4, 3, 3, 3, 2, 2, 2, 2];

    private readonly byte[] mem;
    private readonly uint size;
    private readonly uint alignOffset;
    private readonly byte[] indexToUnits = new byte[NumIndexes];
    private readonly byte[] unitsToIndex = new byte[128];
    private readonly uint[] freeList = new uint[NumIndexes];
    private readonly byte[] ns2Index = new byte[256];
    private readonly byte[] ns2BsIndex = new byte[256];
    private readonly byte[] hb2Flag = new byte[256];
    private readonly ushort[] seeSumm = new ushort[DummySee + 1];
    private readonly byte[] seeShift = new byte[DummySee + 1];
    private readonly byte[] seeCount = new byte[DummySee + 1];
    private readonly ushort[] binSumm = new ushort[128 * 64];
    private readonly byte[] charMask = new byte[256];
    private readonly uint[] statePointers = new uint[256];
    private readonly byte[] scratch = new byte[6];

    private RarBitReader reader;
    private uint minContext, maxContext, foundState;
    private uint orderFall, initEsc, prevSuccess, maxOrder, hiBitsFlag;
    private int runLength, initRunLength;
    private uint glueCount, text, loUnit, hiUnit, unitsStart;
    private uint range, code, low, bottom;

    public RarPpmd(int memorySize, int order, RarBitReader reader)
    {
        this.reader = reader;
        size = (uint)memorySize;
        alignOffset = 4 - (size & 3);
        mem = new byte[alignOffset + size + UnitSize];
        BuildTables();
        maxOrder = (uint)order;
        RestartModel();
        seeShift[DummySee] = PeriodBits;
        seeSumm[DummySee] = 0;
        seeCount[DummySee] = 64;
        StartRange(reader);
    }

    // --- Memory access --------------------------------------------------------------------------------------------------------

    private ushort U16(uint offset) => (ushort)(mem[offset] | mem[offset + 1] << 8);

    private void SetU16(uint offset, int value)
    {
        mem[offset] = (byte)value;
        mem[offset + 1] = (byte)(value >> 8);
    }

    private uint U32(uint offset) => mem[offset] | (uint)mem[offset + 1] << 8 | (uint)mem[offset + 2] << 16 | (uint)mem[offset + 3] << 24;

    private void SetU32(uint offset, uint value)
    {
        mem[offset] = (byte)value;
        mem[offset + 1] = (byte)(value >> 8);
        mem[offset + 2] = (byte)(value >> 16);
        mem[offset + 3] = (byte)(value >> 24);
    }

    // Context: NumStats u16 @0, SummFreq u16 @2, Stats u32 @4, Suffix u32 @8; a lone state is stored at @2.
    private int NumStats(uint context) => U16(context);

    private void SetNumStats(uint context, int value) => SetU16(context, value);

    private int SummFreq(uint context) => U16(context + 2);

    private void SetSummFreq(uint context, int value) => SetU16(context + 2, value);

    private uint Stats(uint context) => U32(context + 4);

    private void SetStats(uint context, uint value) => SetU32(context + 4, value);

    private uint Suffix(uint context) => U32(context + 8);

    private static uint OneState(uint context) => context + 2;

    // State (6 bytes): Symbol @0, Freq @1, Successor u32 @2.
    private int Symbol(uint state) => mem[state];

    private int Freq(uint state) => mem[state + 1];

    private void SetFreq(uint state, int value) => mem[state + 1] = (byte)value;

    private uint Successor(uint state) => U32(state + 2);

    private void SetSuccessor(uint state, uint value) => SetU32(state + 2, value);

    private void CopyState(uint destination, uint source) => Array.Copy(mem, source, mem, destination, 6);

    private void SwapStates(uint a, uint b)
    {
        Span<byte> temp = stackalloc byte[6];
        mem.AsSpan((int)a, 6).CopyTo(temp);
        mem.AsSpan((int)b, 6).CopyTo(mem.AsSpan((int)a, 6));
        temp.CopyTo(mem.AsSpan((int)b, 6));
    }

    private uint I2U(int index) => indexToUnits[index];

    private int U2I(uint units) => unitsToIndex[units - 1];

    private static uint U2B(uint units) => units * UnitSize;

    // --- Allocator ------------------------------------------------------------------------------------------------------------

    private void BuildTables()
    {
        int k = 0;
        for (int i = 0; i < NumIndexes; i++)
        {
            int step = i >= 12 ? 4 : (i >> 2) + 1;
            do { unitsToIndex[k++] = (byte)i; } while (--step != 0);
            indexToUnits[i] = (byte)k;
        }
        ns2BsIndex[0] = 0 << 1;
        ns2BsIndex[1] = 1 << 1;
        Array.Fill(ns2BsIndex, (byte)(2 << 1), 2, 9);
        Array.Fill(ns2BsIndex, (byte)(3 << 1), 11, 256 - 11);
        int j;
        for (j = 0; j < 3; j++) ns2Index[j] = (byte)j;
        int m = j;
        k = 1;
        for (; j < 256; j++)
        {
            ns2Index[j] = (byte)m;
            if (--k == 0) k = ++m - 2;
        }
        Array.Fill(hb2Flag, (byte)8, 0x40, 0x100 - 0x40);
    }

    private void InsertNode(uint node, int index)
    {
        SetU32(node, freeList[index]);
        freeList[index] = node;
    }

    private uint RemoveNode(int index)
    {
        uint node = freeList[index];
        freeList[index] = U32(node);
        return node;
    }

    private void SplitBlock(uint pointer, int oldIndex, int newIndex)
    {
        uint units = I2U(oldIndex) - I2U(newIndex);
        pointer += U2B(I2U(newIndex));
        int i = U2I(units);
        if (I2U(i) != units)
        {
            uint k = I2U(--i);
            InsertNode(pointer + U2B(k), (int)(units - k - 1));
        }
        InsertNode(pointer, i);
    }

    // Node (12 bytes): Stamp u16 @0, NU u16 @2, Next u32 @4, Prev u32 @8.
    private void GlueFreeBlocks()
    {
        uint head = alignOffset + size;
        uint n = head;
        glueCount = 255;

        for (int i = 0; i < NumIndexes; i++)
        {
            int nu = (int)I2U(i);
            uint next = freeList[i];
            freeList[i] = 0;
            while (next != 0)
            {
                uint node = next;
                SetU32(node + 4, n);
                SetU32(n + 8, next);
                n = next;
                next = U32(node);
                SetU16(node, 0);
                SetU16(node + 2, nu);
            }
        }
        SetU16(head, 1);
        SetU32(head + 4, n);
        SetU32(n + 8, head);
        if (loUnit != hiUnit) SetU16(loUnit, 1);

        while (n != head)
        {
            uint node = n;
            uint nu = U16(node + 2);
            for (; ; )
            {
                uint node2 = node + nu * UnitSize;
                nu += U16(node2 + 2);
                if (U16(node2) != 0 || nu >= 0x10000) break;
                SetU32(U32(node2 + 8) + 4, U32(node2 + 4));
                SetU32(U32(node2 + 4) + 8, U32(node2 + 8));
                SetU16(node + 2, (int)nu);
            }
            n = U32(node + 4);
        }

        for (n = U32(head + 4); n != head;)
        {
            uint node = n;
            uint next = U32(node + 4);
            uint nu;
            for (nu = U16(node + 2); nu > 128; nu -= 128, node += 128 * UnitSize) InsertNode(node, NumIndexes - 1);
            int index = U2I(nu);
            if (I2U(index) != nu)
            {
                uint k = I2U(--index);
                InsertNode(node + k * UnitSize, (int)(nu - k - 1));
            }
            InsertNode(node, index);
            n = next;
        }
    }

    private uint AllocUnitsRare(int index)
    {
        if (glueCount == 0)
        {
            GlueFreeBlocks();
            if (freeList[index] != 0) return RemoveNode(index);
        }
        int i = index;
        do
        {
            if (++i == NumIndexes)
            {
                uint bytes = U2B(I2U(index));
                glueCount--;
                if (unitsStart - text > bytes)
                {
                    unitsStart -= bytes;
                    return unitsStart;
                }
                return 0;
            }
        }
        while (freeList[i] == 0);
        uint result = RemoveNode(i);
        SplitBlock(result, i, index);
        return result;
    }

    private uint AllocUnits(int index)
    {
        if (freeList[index] != 0) return RemoveNode(index);
        uint bytes = U2B(I2U(index));
        if (bytes <= hiUnit - loUnit)
        {
            uint result = loUnit;
            loUnit += bytes;
            return result;
        }
        return AllocUnitsRare(index);
    }

    private uint ShrinkUnits(uint oldPointer, uint oldUnits, uint newUnits)
    {
        int i0 = U2I(oldUnits);
        int i1 = U2I(newUnits);
        if (i0 == i1) return oldPointer;
        if (freeList[i1] != 0)
        {
            uint pointer = RemoveNode(i1);
            Array.Copy(mem, oldPointer, mem, pointer, newUnits * UnitSize);
            InsertNode(oldPointer, i0);
            return pointer;
        }
        SplitBlock(oldPointer, i0, i1);
        return oldPointer;
    }

    // --- Model ----------------------------------------------------------------------------------------------------------------

    private void RestartModel()
    {
        Array.Clear(freeList);
        text = alignOffset;
        hiUnit = text + size;
        loUnit = unitsStart = hiUnit - size / 8 / UnitSize * 7 * UnitSize;
        glueCount = 0;

        orderFall = maxOrder;
        runLength = initRunLength = -(int)(maxOrder < 12 ? maxOrder : 12) - 1;
        prevSuccess = 0;

        hiUnit -= UnitSize;
        minContext = maxContext = hiUnit;
        SetU32(minContext + 8, 0);
        SetNumStats(minContext, 256);
        SetSummFreq(minContext, 256 + 1);
        foundState = loUnit;
        loUnit += U2B(256 / 2);
        SetStats(minContext, foundState);
        for (int i = 0; i < 256; i++)
        {
            uint s = foundState + (uint)i * 6;
            mem[s] = (byte)i;
            mem[s + 1] = 1;
            SetSuccessor(s, 0);
        }

        for (int i = 0; i < 128; i++)
        {
            for (int k = 0; k < 8; k++)
            {
                ushort value = (ushort)(BinScale - InitBinEsc[k] / (i + 2));
                for (int m = 0; m < 64; m += 8) binSumm[i * 64 + k + m] = value;
            }
        }

        for (int i = 0; i < 25; i++)
        {
            for (int k = 0; k < 16; k++)
            {
                int see = i * 16 + k;
                seeShift[see] = PeriodBits - 4;
                seeSumm[see] = (ushort)(5 * i + 10 << PeriodBits - 4);
                seeCount[see] = 4;
            }
        }
    }

    private uint CreateSuccessors(bool skip)
    {
        uint c = minContext;
        uint upBranch = Successor(foundState);
        Span<uint> ps = stackalloc uint[MaxOrder];
        int numPs = 0;

        if (!skip) ps[numPs++] = foundState;

        while (Suffix(c) != 0)
        {
            c = Suffix(c);
            uint s;
            if (NumStats(c) != 1)
            {
                s = Stats(c);
                while (Symbol(s) != Symbol(foundState)) s += 6;
            }
            else s = OneState(c);
            uint successor = Successor(s);
            if (successor != upBranch)
            {
                c = successor;
                if (numPs == 0) return c;
                break;
            }
            ps[numPs++] = s;
        }

        byte upSymbol = mem[upBranch];
        uint upSuccessor = upBranch + 1;
        int upFreq;
        if (NumStats(c) == 1) upFreq = Freq(OneState(c));
        else
        {
            uint s = Stats(c);
            while (Symbol(s) != upSymbol) s += 6;
            uint cf = (uint)Freq(s) - 1;
            uint s0 = (uint)SummFreq(c) - (uint)NumStats(c) - cf;
            upFreq = (byte)(1 + (2 * cf <= s0 ? (5 * cf > s0 ? 1 : 0) : (2 * cf + 3 * s0 - 1) / (2 * s0)));
        }

        while (numPs != 0)
        {
            uint c1;
            if (hiUnit != loUnit)
            {
                hiUnit -= UnitSize;
                c1 = hiUnit;
            }
            else if (freeList[0] != 0) c1 = RemoveNode(0);
            else
            {
                c1 = AllocUnitsRare(0);
                if (c1 == 0) return 0;
            }
            SetNumStats(c1, 1);
            uint one = OneState(c1);
            mem[one] = upSymbol;
            mem[one + 1] = (byte)upFreq;
            SetSuccessor(one, upSuccessor);
            SetU32(c1 + 8, c);
            SetSuccessor(ps[--numPs], c1);
            c = c1;
        }
        return c;
    }

    private void UpdateModel()
    {
        uint fSuccessor = Successor(foundState);
        int foundSymbol = Symbol(foundState);
        int foundFreq = Freq(foundState);

        if (foundFreq < MaxFreq / 4 && Suffix(minContext) != 0)
        {
            uint c = Suffix(minContext);
            if (NumStats(c) == 1)
            {
                uint s = OneState(c);
                if (Freq(s) < 32) SetFreq(s, Freq(s) + 1);
            }
            else
            {
                uint s = Stats(c);
                if (Symbol(s) != foundSymbol)
                {
                    do { s += 6; } while (Symbol(s) != foundSymbol);
                    if (Freq(s) >= Freq(s - 6))
                    {
                        SwapStates(s, s - 6);
                        s -= 6;
                    }
                }
                if (Freq(s) < MaxFreq - 9)
                {
                    SetFreq(s, Freq(s) + 2);
                    SetSummFreq(c, SummFreq(c) + 2);
                }
            }
        }

        if (orderFall == 0)
        {
            minContext = maxContext = CreateSuccessors(true);
            if (minContext == 0)
            {
                RestartModel();
                return;
            }
            SetSuccessor(foundState, minContext);
            return;
        }

        mem[text++] = (byte)foundSymbol;
        uint successor = text;
        if (text >= unitsStart)
        {
            RestartModel();
            return;
        }

        if (fSuccessor != 0)
        {
            if (fSuccessor <= successor)
            {
                uint cs = CreateSuccessors(false);
                if (cs == 0)
                {
                    RestartModel();
                    return;
                }
                fSuccessor = cs;
            }
            if (--orderFall == 0)
            {
                successor = fSuccessor;
                if (maxContext != minContext) text--;
            }
        }
        else
        {
            SetSuccessor(foundState, successor);
            fSuccessor = minContext;
        }

        uint ns = (uint)NumStats(minContext);
        uint s0 = (uint)SummFreq(minContext) - ns - (uint)(foundFreq - 1);

        for (uint c = maxContext; c != minContext; c = Suffix(c))
        {
            uint ns1 = (uint)NumStats(c);
            if (ns1 != 1)
            {
                if ((ns1 & 1) == 0)
                {
                    uint oldUnits = ns1 >> 1;
                    int i = U2I(oldUnits);
                    if (i != U2I(oldUnits + 1))
                    {
                        uint pointer = AllocUnits(i + 1);
                        if (pointer == 0)
                        {
                            RestartModel();
                            return;
                        }
                        uint oldPointer = Stats(c);
                        Array.Copy(mem, oldPointer, mem, pointer, oldUnits * UnitSize);
                        InsertNode(oldPointer, i);
                        SetStats(c, pointer);
                    }
                }
                int summ = SummFreq(c);
                summ += (2 * ns1 < ns ? 1 : 0) + 2 * ((4 * ns1 <= ns ? 1 : 0) & (summ <= 8 * ns1 ? 1 : 0));
                SetSummFreq(c, summ);
            }
            else
            {
                uint s = AllocUnits(0);
                if (s == 0)
                {
                    RestartModel();
                    return;
                }
                CopyState(s, OneState(c));
                SetStats(c, s);
                int freq = Freq(s);
                if (freq < MaxFreq / 4 - 1) freq <<= 1;
                else freq = MaxFreq - 4;
                SetFreq(s, freq);
                SetSummFreq(c, freq + (int)initEsc + (ns > 3 ? 1 : 0));
            }

            uint cf = 2 * (uint)foundFreq * ((uint)SummFreq(c) + 6);
            uint sf = s0 + (uint)SummFreq(c);
            if (cf < 6 * sf)
            {
                cf = 1 + (cf > sf ? 1u : 0) + (cf >= 4 * sf ? 1u : 0);
                SetSummFreq(c, SummFreq(c) + 3);
            }
            else
            {
                cf = 4 + (cf >= 9 * sf ? 1u : 0) + (cf >= 12 * sf ? 1u : 0) + (cf >= 15 * sf ? 1u : 0);
                SetSummFreq(c, SummFreq(c) + (int)cf);
            }
            uint state = Stats(c) + ns1 * 6;
            SetSuccessor(state, successor);
            mem[state] = (byte)foundSymbol;
            mem[state + 1] = (byte)cf;
            SetNumStats(c, (int)ns1 + 1);
        }
        maxContext = minContext = fSuccessor;
    }

    private void Rescale()
    {
        uint stats = Stats(minContext);
        uint s = foundState;
        {
            Span<byte> temp = stackalloc byte[6];
            mem.AsSpan((int)s, 6).CopyTo(temp);
            for (; s != stats; s -= 6) CopyState(s, s - 6);
            temp.CopyTo(mem.AsSpan((int)s, 6));
        }
        uint escFreq = (uint)SummFreq(minContext) - (uint)Freq(s);
        SetFreq(s, Freq(s) + 4);
        uint adder = orderFall != 0 ? 1u : 0;
        SetFreq(s, (int)((uint)Freq(s) + adder) >> 1);
        uint sumFreq = (uint)Freq(s);

        int i = NumStats(minContext) - 1;
        do
        {
            s += 6;
            escFreq -= (uint)Freq(s);
            SetFreq(s, (int)((uint)Freq(s) + adder) >> 1);
            sumFreq += (uint)Freq(s);
            if (Freq(s) > Freq(s - 6))
            {
                uint s1 = s;
                Span<byte> temp = scratch;
                mem.AsSpan((int)s1, 6).CopyTo(temp);
                int tempFreq = temp[1];
                do
                {
                    CopyState(s1, s1 - 6);
                    s1 -= 6;
                }
                while (s1 != stats && tempFreq > Freq(s1 - 6));
                temp.CopyTo(mem.AsSpan((int)s1, 6));
            }
        }
        while (--i != 0);

        if (Freq(s) == 0)
        {
            int numStats = NumStats(minContext);
            do { i++; s -= 6; } while (Freq(s) == 0);
            escFreq += (uint)i;
            SetNumStats(minContext, NumStats(minContext) - i);
            if (NumStats(minContext) == 1)
            {
                Span<byte> temp = stackalloc byte[6];
                mem.AsSpan((int)stats, 6).CopyTo(temp);
                do
                {
                    temp[1] = (byte)(temp[1] - (temp[1] >> 1));
                    escFreq >>= 1;
                }
                while (escFreq > 1);
                InsertNode(stats, U2I((uint)(numStats + 1) >> 1));
                foundState = OneState(minContext);
                temp.CopyTo(mem.AsSpan((int)foundState, 6));
                return;
            }
            uint n0 = (uint)(numStats + 1) >> 1;
            uint n1 = (uint)(NumStats(minContext) + 1) >> 1;
            if (n0 != n1) SetStats(minContext, ShrinkUnits(stats, n0, n1));
        }
        SetSummFreq(minContext, (int)(sumFreq + escFreq - (escFreq >> 1)));
        foundState = Stats(minContext);
    }

    private int MakeEscFreq(uint numMasked, out uint escFreq)
    {
        uint numStats = (uint)NumStats(minContext);
        uint nonMasked = numStats - numMasked;
        if (numStats != 256)
        {
            int see = ns2Index[nonMasked - 1] * 16
                + (nonMasked < (uint)NumStats(Suffix(minContext)) - numStats ? 1 : 0)
                + 2 * (SummFreq(minContext) < 11 * numStats ? 1 : 0)
                + 4 * (numMasked > nonMasked ? 1 : 0)
                + (int)hiBitsFlag;
            uint r = (uint)(seeSumm[see] >> seeShift[see]);
            seeSumm[see] = (ushort)(seeSumm[see] - r);
            escFreq = r + (r == 0 ? 1u : 0);
            return see;
        }
        escFreq = 1;
        return DummySee;
    }

    private void SeeUpdate(int see)
    {
        if (seeShift[see] < PeriodBits && --seeCount[see] == 0)
        {
            seeSumm[see] <<= 1;
            seeCount[see] = (byte)(3 << seeShift[see]++);
        }
    }

    private void NextContext()
    {
        uint c = Successor(foundState);
        if (orderFall == 0 && c > text) minContext = maxContext = c;
        else UpdateModel();
    }

    private void Update1()
    {
        uint s = foundState;
        SetFreq(s, Freq(s) + 4);
        SetSummFreq(minContext, SummFreq(minContext) + 4);
        if (Freq(s) > Freq(s - 6))
        {
            SwapStates(s, s - 6);
            foundState = s -= 6;
            if (Freq(s) > MaxFreq) Rescale();
        }
        NextContext();
    }

    private void Update1Zero()
    {
        prevSuccess = 2 * (uint)Freq(foundState) > (uint)SummFreq(minContext) ? 1u : 0;
        runLength += (int)prevSuccess;
        SetSummFreq(minContext, SummFreq(minContext) + 4);
        SetFreq(foundState, Freq(foundState) + 4);
        if (Freq(foundState) > MaxFreq) Rescale();
        NextContext();
    }

    private void UpdateBin()
    {
        SetFreq(foundState, Freq(foundState) + (Freq(foundState) < 128 ? 1 : 0));
        prevSuccess = 1;
        runLength++;
        NextContext();
    }

    private void Update2()
    {
        SetSummFreq(minContext, SummFreq(minContext) + 4);
        SetFreq(foundState, Freq(foundState) + 4);
        if (Freq(foundState) > MaxFreq) Rescale();
        runLength = initRunLength;
        UpdateModel();
    }

    // --- RAR range decoder ----------------------------------------------------------------------------------------------------

    /// <summary>Starts (or restarts) the range decoder at the reader's current position, at the beginning of every PPMd block.</summary>
    public void StartRange(RarBitReader newReader)
    {
        reader = newReader;
        low = 0;
        code = 0;
        range = 0xFFFFFFFF;
        for (int i = 0; i < 4; i++) code = code << 8 | (uint)reader.ReadByte();
        bottom = 0x8000;
    }

    private uint GetThreshold(uint total)
    {
        range /= total;
        return (code - low) / range;
    }

    private void Normalize()
    {
        while (true)
        {
            if ((low ^ low + range) >= TopValue)
            {
                if (range >= bottom) break;
                range = (uint)-(int)low & bottom - 1;
            }
            code = code << 8 | (uint)reader.ReadByte();
            range <<= 8;
            low <<= 8;
        }
    }

    private void RangeDecode(uint start, uint length)
    {
        low += start * range;
        range *= length;
        Normalize();
    }

    private uint DecodeBit(uint size0)
    {
        uint value = GetThreshold(BinScale);
        if (value < size0)
        {
            RangeDecode(0, size0);
            return 0;
        }
        RangeDecode(size0, BinScale - size0);
        return 1;
    }

    // --- Symbol decoding ------------------------------------------------------------------------------------------------------

    /// <summary>Decodes the next byte of the text. Throws on damaged data.</summary>
    public int DecodeSymbol()
    {
        if (reader.Overrun) throw new InvalidDataException("The RAR PPMd data is truncated.");
        if (NumStats(minContext) != 1)
        {
            uint s = Stats(minContext);
            uint count = GetThreshold((uint)SummFreq(minContext));
            uint hiCnt = (uint)Freq(s);
            if (count < hiCnt)
            {
                RangeDecode(0, (uint)Freq(s));
                foundState = s;
                int symbol = Symbol(s);
                Update1Zero();
                return symbol;
            }
            prevSuccess = 0;
            int i = NumStats(minContext) - 1;
            do
            {
                s += 6;
                hiCnt += (uint)Freq(s);
                if (hiCnt > count)
                {
                    RangeDecode(hiCnt - (uint)Freq(s), (uint)Freq(s));
                    foundState = s;
                    int symbol = Symbol(s);
                    Update1();
                    return symbol;
                }
            }
            while (--i != 0);
            if (count >= (uint)SummFreq(minContext)) throw new InvalidDataException("Bad RAR PPMd data.");
            hiBitsFlag = hb2Flag[Symbol(foundState)];
            RangeDecode(hiCnt, (uint)SummFreq(minContext) - hiCnt);
            Array.Fill(charMask, (byte)0xFF);
            charMask[Symbol(s)] = 0;
            i = NumStats(minContext) - 1;
            do
            {
                s -= 6;
                charMask[Symbol(s)] = 0;
            }
            while (--i != 0);
        }
        else
        {
            uint one = OneState(minContext);
            hiBitsFlag = hb2Flag[Symbol(foundState)];
            int index = (Freq(one) - 1) * 64
                + (int)prevSuccess
                + ns2BsIndex[NumStats(Suffix(minContext)) - 1]
                + (int)hiBitsFlag
                + 2 * hb2Flag[Symbol(one)]
                + (runLength >> 26 & 0x20);
            ushort prob = binSumm[index];
            if (DecodeBit(prob) == 0)
            {
                binSumm[index] = (ushort)(prob + (1 << IntBits) - ((prob + (1 << PeriodBits - 2)) >> PeriodBits));
                foundState = one;
                int symbol = Symbol(one);
                UpdateBin();
                return symbol;
            }
            prob = (ushort)(prob - ((prob + (1 << PeriodBits - 2)) >> PeriodBits));
            binSumm[index] = prob;
            initEsc = ExpEscape[prob >> 10];
            Array.Fill(charMask, (byte)0xFF);
            charMask[Symbol(one)] = 0;
            prevSuccess = 0;
        }

        for (; ; )
        {
            uint numMasked = (uint)NumStats(minContext);
            do
            {
                orderFall++;
                if (Suffix(minContext) == 0) throw new InvalidDataException("Bad RAR PPMd data.");
                minContext = Suffix(minContext);
            }
            while ((uint)NumStats(minContext) == numMasked);

            uint hiCnt = 0;
            uint s = Stats(minContext);
            int i = 0;
            int num = (int)((uint)NumStats(minContext) - numMasked);
            do
            {
                int k = (sbyte)charMask[Symbol(s)];
                hiCnt += (uint)(Freq(s) & k);
                statePointers[i] = s;
                s += 6;
                i -= k;
            }
            while (i != num);

            int see = MakeEscFreq(numMasked, out uint freqSum);
            freqSum += hiCnt;
            uint count = GetThreshold(freqSum);

            if (count < hiCnt)
            {
                int pointer = 0;
                hiCnt = 0;
                while ((hiCnt += (uint)Freq(statePointers[pointer])) <= count) pointer++;
                s = statePointers[pointer];
                RangeDecode(hiCnt - (uint)Freq(s), (uint)Freq(s));
                SeeUpdate(see);
                foundState = s;
                int symbol = Symbol(s);
                Update2();
                return symbol;
            }
            if (count >= freqSum) throw new InvalidDataException("Bad RAR PPMd data.");
            RangeDecode(hiCnt, freqSum - hiCnt);
            seeSumm[see] = (ushort)(seeSumm[see] + freqSum);
            do { charMask[Symbol(statePointers[--i])] = 0; } while (i != 0);
        }
    }
}
