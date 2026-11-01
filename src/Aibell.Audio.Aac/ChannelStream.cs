namespace Aibell.Audio.Aac;

internal enum WindowSequence
{
    OnlyLong = 0,
    LongStart = 1,
    EightShort = 2,
    LongStop = 3
}

/// <summary>ics_info(): the window layout shared by a channel or a common-window pair.</summary>
internal sealed class IcsInfo
{
    public WindowSequence Sequence;
    public int WindowShape;
    public int MaxSfb;
    public int NumWindowGroups;
    public int NumWindows;
    public int NumSwb;
    public int[] SwbOffset = [];
    public readonly int[] GroupLength = new int[8];

    public bool IsShort => Sequence == WindowSequence.EightShort;

    public void Read(ref BitReader reader, int samplingIndex, int objectType)
    {
        if (reader.ReadFlag()) throw new AacException("The ics_reserved_bit is set.");
        Sequence = (WindowSequence)reader.ReadBits(2);
        WindowShape = reader.ReadBit();
        if (IsShort)
        {
            MaxSfb = (int)reader.ReadBits(4);
            uint grouping = reader.ReadBits(7);
            NumWindows = 8;
            NumWindowGroups = 1;
            GroupLength[0] = 1;
            for (int i = 0; i < 7; i++)
            {
                if ((grouping >> (6 - i) & 1) != 0) GroupLength[NumWindowGroups - 1]++;
                else GroupLength[NumWindowGroups++] = 1;
            }
            SwbOffset = ScaleFactorBandTables.Short[IndexFor(samplingIndex)];
        }
        else
        {
            MaxSfb = (int)reader.ReadBits(6);
            NumWindows = 1;
            NumWindowGroups = 1;
            GroupLength[0] = 1;
            SwbOffset = ScaleFactorBandTables.Long[IndexFor(samplingIndex)];
            if (reader.ReadFlag())
            {
                // predictor_data_present: main-profile prediction or long-term prediction, neither of which radio uses.
                throw new AacUnsupportedException(objectType == 1 ? "AAC Main profile prediction is not supported." : "AAC long-term prediction is not supported.");
            }
        }
        NumSwb = SwbOffset.Length - 1;
        if (MaxSfb > NumSwb) throw new AacException("max_sfb is larger than the number of scalefactor bands.");
    }

    private static int IndexFor(int samplingIndex) =>
        samplingIndex < 12 ? samplingIndex : throw new AacUnsupportedException("The 7.35 kHz sampling rate is not supported.");
}

/// <summary>
/// Everything one individual_channel_stream() decodes to, in preallocated arrays: codebooks, scalefactors, quantized and
/// reconstructed spectrum, pulse and TNS data. Reused for every frame.
/// </summary>
internal sealed class ChannelStream
{
    public const int BandStride = 64;
    public const int SpectrumLength = 1024;

    public IcsInfo Info = new();
    public readonly IcsInfo OwnInfo;
    public int GlobalGain;
    public readonly byte[] Codebook = new byte[8 * BandStride];
    public readonly int[] ScaleFactor = new int[8 * BandStride];
    public readonly int[] Quantized = new int[SpectrumLength];
    public readonly float[] Spectrum = new float[SpectrumLength];

    public bool PulsePresent;
    public int PulseCount;
    public int PulseStartSfb;
    public readonly int[] PulseOffset = new int[4];
    public readonly int[] PulseAmplitude = new int[4];

    public bool TnsPresent;
    public readonly int[] TnsFilterCount = new int[8];
    public readonly int[] TnsResolution = new int[8];
    public readonly int[,] TnsLength = new int[8, 4];
    public readonly int[,] TnsOrder = new int[8, 4];
    public readonly int[,] TnsDirection = new int[8, 4];
    public readonly int[,] TnsCompress = new int[8, 4];
    public readonly int[,,] TnsCoefficient = new int[8, 4, 32];

    public ChannelStream()
    {
        OwnInfo = Info;
    }

    public bool IsNoise(int group, int sfb) => Codebook[group * BandStride + sfb] == Codebooks.NoiseBook;

    public bool IsIntensity(int group, int sfb) => Codebook[group * BandStride + sfb] >= Codebooks.IntensityBook2;
}
