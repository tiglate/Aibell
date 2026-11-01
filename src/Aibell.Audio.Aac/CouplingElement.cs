namespace Aibell.Audio.Aac;

/// <summary>
/// One coupling_channel_element() of the block being decoded (ISO/IEC 14496-3 subclause 4.6.8.3): its embedded channel stream, what it is
/// coupled onto and the gain exponents. Preallocated and reused for every block.
/// </summary>
internal sealed class CouplingElement
{
    public const int MaxTargets = 8;
    public const int MaxLists = 16;

    public bool Independent;
    public int Domain;
    public bool Signed;
    public int Scale;
    public int TargetCount;
    public readonly bool[] TargetIsCpe = new bool[MaxTargets];
    public readonly int[] TargetTag = new int[MaxTargets];
    public readonly bool[] Left = new bool[MaxTargets];
    public readonly bool[] Right = new bool[MaxTargets];
    public int Lists;

    /// <summary>Gain exponent per list, window group and band; list 0 is all zeros (the first target is coupled at natural scale).</summary>
    public readonly int[] Gain = new int[MaxLists * 8 * ChannelStream.BandStride];

    /// <summary>+1 or -1 per list, window group and band: the in-phase or out-of-phase flag carried by signed gain elements.</summary>
    public readonly sbyte[] Sign = new sbyte[MaxLists * 8 * ChannelStream.BandStride];

    public readonly ChannelStream Stream = new();
    public readonly float[] Time = new float[AacDecoder.FrameLength];
    public Filterbank? Bank;

    private static readonly double[] ScaleExponent = [0.125, 0.25, 0.5, 1.0];

    /// <summary>The multiplier cc_sign * cc_scale^gain for a gain exponent and sign.</summary>
    public float GainFactor(int exponent, int sign, int exponentSign = -1) => sign * (float)Math.Pow(2.0, ScaleExponent[Scale] * exponent * exponentSign);

    /// <summary>
    /// Lists the (channel, gain list) pairs this element is coupled onto, in the order of the standard's decode_coupling_channel().
    /// <paramref name="sceChannel"/> and <paramref name="cpeChannel"/> map element_instance_tag to the first channel of that element, or -1.
    /// </summary>
    public int Targets(int[] sceChannel, int[] cpeChannel, Span<(int Channel, int List)> into)
    {
        int count = 0, list = 0;
        for (int c = 0; c < TargetCount; c++)
        {
            if (!TargetIsCpe[c])
            {
                if (sceChannel[TargetTag[c]] >= 0) into[count++] = (sceChannel[TargetTag[c]], list);
                list++;
                continue;
            }
            int channel = cpeChannel[TargetTag[c]];
            if (!Left[c] && !Right[c])
            {
                if (channel >= 0)
                {
                    into[count++] = (channel, list);
                    into[count++] = (channel + 1, list);
                }
                list++;
            }
            if (Left[c])
            {
                if (channel >= 0) into[count++] = (channel, list);
                list++;
            }
            if (Right[c])
            {
                if (channel >= 0) into[count++] = (channel + 1, list);
                list++;
            }
        }
        return count;
    }
}
