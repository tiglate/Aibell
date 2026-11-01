// This file is derived from the Opus reference implementation published in RFC 6716 (entdec.c, entcode.c).
// Copyright 1994-2011 IETF Trust, Xiph.Org, Skype Limited, Octasic, Jean-Marc Valin, Timothy B. Terriberry,
// CSIRO, Gregory Maxwell, Mark Borgerding, Erik de Castro Lopo. All rights reserved.
// Redistribution and use are permitted under the BSD terms reproduced in COPYING.Opus.
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Aibell.Audio.Opus;

/// <summary>
/// The Opus range decoder (RFC 6716 section 4.1): range-coded symbols read from the start of the frame and raw bits read backwards from its end.
/// It never reads outside the frame; missing bytes read as zero.
/// </summary>
internal sealed class RangeDecoder
{
    private const int SymbolBits = 8;
    private const int CodeBits = 32;
    private const uint CodeTop = 1u << (CodeBits - 1);
    private const uint CodeBottom = CodeTop >> SymbolBits;
    private const int CodeExtra = (CodeBits - 2) % SymbolBits + 1;
    private const int UintBits = 8;

    private byte[] buffer = [];
    private int start;
    private int storage;
    private int endOffset;
    private uint endWindow;
    private int endBits;
    private int bitsTotal;
    private int offset;
    private uint range;
    private uint value;
    private uint extent;
    private int remainder;

    /// <summary>True if a value outside its declared range was decoded (a corrupt frame).</summary>
    public bool Error { get; private set; }

    /// <summary>The current range, used for the final-range check of the conformance vectors.</summary>
    public uint Range => range;

    public int Storage => storage;

    /// <summary>Shrinks the frame by <paramref name="bytes"/> at its end (the redundancy of a hybrid or SILK frame sits there).</summary>
    public void Shrink(int bytes) => storage -= bytes;

    public void Init(byte[] data, int dataOffset, int length)
    {
        buffer = data;
        start = dataOffset;
        storage = length;
        endOffset = 0;
        endWindow = 0;
        endBits = 0;
        bitsTotal = CodeBits + 1 - (CodeBits - CodeExtra) / SymbolBits * SymbolBits;
        offset = 0;
        range = 1u << CodeExtra;
        remainder = ReadByte();
        value = range - 1 - (uint)(remainder >> (SymbolBits - CodeExtra));
        Error = false;
        Normalize();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ReadByte() => offset < storage ? buffer[start + offset++] : 0;

    private int ReadByteFromEnd() => endOffset < storage ? buffer[start + storage - ++endOffset] : 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Normalize()
    {
        while (range <= CodeBottom)
        {
            bitsTotal += SymbolBits;
            range <<= SymbolBits;
            int symbol = remainder;
            remainder = ReadByte();
            symbol = (symbol << SymbolBits | remainder) >> (SymbolBits - CodeExtra);
            value = ((value << SymbolBits) + (uint)(255 & ~symbol)) & (CodeTop - 1);
        }
    }

    public uint Decode(uint total)
    {
        extent = range / total;
        uint s = value / extent;
        return total - Math.Min(s + 1, total);
    }

    public uint DecodeBin(int bits)
    {
        extent = range >> bits;
        uint s = value / extent;
        return (1u << bits) - Math.Min(s + 1, 1u << bits);
    }

    public void Update(uint low, uint high, uint total)
    {
        uint s = extent * (total - high);
        value -= s;
        range = low > 0 ? extent * (high - low) : range - s;
        Normalize();
    }

    /// <summary>Decodes a bit whose probability of being 1 is 1/(1 &lt;&lt; logp).</summary>
    public int DecodeBitLogP(int logp)
    {
        uint r = range, d = value, s = r >> logp;
        int bit = d < s ? 1 : 0;
        if (bit == 0) value = d - s;
        range = bit != 0 ? s : r - s;
        Normalize();
        return bit;
    }

    /// <summary>Decodes a symbol from an inverse-CDF table whose total is 1 &lt;&lt; <paramref name="totalBits"/>.</summary>
    public int DecodeIcdf(ReadOnlySpan<byte> icdf, int totalBits)
    {
        uint s = range, d = value, r = s >> totalBits, t;
        int symbol = -1;
        do
        {
            t = s;
            s = r * icdf[++symbol];
        }
        while (d < s);
        value = d - s;
        range = t - s;
        Normalize();
        return symbol;
    }

    public uint DecodeBits(int bits)
    {
        uint window = endWindow;
        int available = endBits;
        if (available < bits)
        {
            do
            {
                window |= (uint)ReadByteFromEnd() << available;
                available += SymbolBits;
            }
            while (available <= 32 - SymbolBits);
        }
        uint result = window & ((1u << bits) - 1u);
        window >>= bits;
        available -= bits;
        endWindow = window;
        endBits = available;
        bitsTotal += bits;
        return result;
    }

    /// <summary>Decodes a uniformly distributed integer in [0, total).</summary>
    public uint DecodeUint(uint total)
    {
        total--;
        int bits = 32 - BitOperations.LeadingZeroCount(total);
        if (bits > UintBits)
        {
            bits -= UintBits;
            uint ft = (total >> bits) + 1;
            uint s = Decode(ft);
            Update(s, s + 1, ft);
            uint t = s << bits | DecodeBits(bits);
            if (t <= total) return t;
            Error = true;
            return total;
        }
        total++;
        uint symbol = Decode(total);
        Update(symbol, symbol + 1, total);
        return symbol;
    }

    /// <summary>Pretends that <paramref name="bits"/> more bits were consumed (used when a frame is declared silent).</summary>
    public void SkipBits(int bits) => bitsTotal += bits;

    /// <summary>Whole bits used so far (an upper bound).</summary>
    public int Tell() => bitsTotal - (32 - BitOperations.LeadingZeroCount(range));

    /// <summary>Bits used so far in 1/8 bit units (an upper bound).</summary>
    public int TellFrac()
    {
        int bits = bitsTotal << 3;
        int l = 32 - BitOperations.LeadingZeroCount(range);
        uint r = range >> (l - 16);
        for (int i = 3; i-- > 0;)
        {
            r = r * r >> 15;
            int b = (int)(r >> 16);
            l = l << 1 | b;
            r >>= b;
        }
        return bits - l;
    }
}
