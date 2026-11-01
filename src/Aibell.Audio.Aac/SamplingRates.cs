namespace Aibell.Audio.Aac;

/// <summary>The sampling_frequency_index table of ISO/IEC 14496-3 subclause 1.6.3.4 (Table 1.18).</summary>
public static class SamplingRates
{
    private static readonly int[] Rates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];

    public static int Count => Rates.Length;

    public static int FromIndex(int index) => (uint)index < (uint)Rates.Length ? Rates[index] : throw new AacException("The AAC sampling frequency index is reserved.");

    public static int ToIndex(int rate) => Array.IndexOf(Rates, rate);
}
