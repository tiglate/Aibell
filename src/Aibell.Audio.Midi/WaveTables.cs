namespace Aibell.Audio.Midi;

/// <summary>
/// Single-cycle tables for every timbre, one per octave of the keyboard, each holding only the harmonics that stay below the Nyquist frequency at the
/// top of its octave (so nothing aliases however bright the spectrum is). Built once per sample rate and shared.
/// </summary>
internal static class WaveTables
{
    public const int Size = 2048;
    public const int Bands = 11;

    private static readonly Dictionary<int, float[][][]> Cache = [];
    private static readonly float[] Sine = [.. Enumerable.Range(0, Size).Select(i => MathF.Sin(2 * MathF.PI * i / Size))];

    /// <summary>The tables indexed [timbre][band][sample]; a timbre without harmonics has no bands.</summary>
    public static float[][][] For(int sampleRate)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(sampleRate, out float[][][]? tables))
            {
                tables = [.. Timbres.Families.Select(t => Build(t, sampleRate))];
                Cache[sampleRate] = tables;
            }
            return tables;
        }
    }

    public static int Band(int note) => Math.Clamp(note / 12, 0, Bands - 1);

    private static float[][] Build(Timbre timbre, int sampleRate)
    {
        if (timbre.Harmonics.Length == 0) return [];
        var bands = new float[Bands][];
        for (int b = 0; b < Bands; b++)
        {
            // Top of the octave plus a few semitones for bends and vibrato.
            double top = 440 * Math.Pow(2, ((b + 1) * 12 + 2 - 69) / 12.0);
            int count = Math.Clamp((int)(sampleRate * .45 / top), 1, timbre.Harmonics.Length);
            var table = new float[Size];
            for (int h = 1; h <= count; h++)
            {
                float amplitude = timbre.Harmonics[h - 1];
                if (amplitude == 0) continue;
                for (int i = 0; i < Size; i++) table[i] += amplitude * Sine[h * i & (Size - 1)];
            }
            float peak = table.Max(MathF.Abs);
            if (peak > 0) for (int i = 0; i < Size; i++) table[i] /= peak;
            bands[b] = table;
        }
        return bands;
    }
}
