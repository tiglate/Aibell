namespace Aibell.Audio.Vorbis;

/// <summary>
/// Turns audio packets into PCM (section 4.3): floor and residue decoding, inverse coupling, the floor/residue product, the inverse MDCT, windowing and
/// overlap-add. Everything is allocated once, for the largest block size; decoding a packet allocates nothing. Output is interleaved in WAVE channel order.
/// </summary>
internal sealed class VorbisSynthesizer
{
    // Scale of the inverse MDCT output (the unscaled sum of section 4.3.7's transform is exactly what the encoder's spectrum expects: see VorbisDecoderTests).
    private const float TransformScale = 1f;

    private readonly VorbisIdentification id;
    private readonly VorbisSetup setup;
    private readonly int channels;
    private readonly int[] channelOrder;
    private readonly Imdct shortTransform;
    private readonly Imdct longTransform;
    private readonly float[] shortSlope;
    private readonly float[] longSlope;
    private readonly float[][] spectrum;
    private readonly float[][] floorCurve;
    private readonly float[][] pending;
    private readonly float[] time;
    private readonly bool[] hasFloor;
    private readonly bool[] floorUsed;
    private readonly bool[] skipResidue;
    private readonly SubmapPlan[][] plans;
    private int previousSize;
    private bool primed;

    // The channels of each submap of each mapping, with the arrays the residue decoder fills (they are the spectrum buffers themselves).
    private sealed record SubmapPlan(int[] Channels, float[][] Vectors, bool[] Skip);

    public VorbisSynthesizer(VorbisIdentification id, VorbisSetup setup)
    {
        this.id = id;
        this.setup = setup;
        channels = id.Channels;
        channelOrder = WaveOrder(channels);
        int maxHalf = id.BlockSize1 / 2;
        shortTransform = new Imdct(id.BlockSize0 / 2);
        longTransform = id.BlockSize1 == id.BlockSize0 ? shortTransform : new Imdct(id.BlockSize1 / 2);
        shortSlope = Slope(id.BlockSize0 / 2);
        longSlope = id.BlockSize1 == id.BlockSize0 ? shortSlope : Slope(id.BlockSize1 / 2);
        spectrum = new float[channels][];
        floorCurve = new float[channels][];
        pending = new float[channels][];
        for (int c = 0; c < channels; c++)
        {
            spectrum[c] = new float[maxHalf];
            floorCurve[c] = new float[maxHalf];
            pending[c] = new float[maxHalf];
        }
        time = new float[id.BlockSize1];
        hasFloor = new bool[channels];
        floorUsed = new bool[channels];
        skipResidue = new bool[channels];
        plans = new SubmapPlan[setup.Mappings.Length][];
        for (int m = 0; m < plans.Length; m++)
        {
            VorbisMapping mapping = setup.Mappings[m];
            plans[m] = new SubmapPlan[mapping.Submaps];
            for (int s = 0; s < mapping.Submaps; s++)
            {
                int[] members = [.. Enumerable.Range(0, channels).Where(c => mapping.Mux[c] == s)];
                plans[m][s] = new SubmapPlan(members, [.. members.Select(c => spectrum[c])], new bool[members.Length]);
            }
        }
        Output = new float[(id.BlockSize1 / 2 + id.BlockSize0 / 2) * channels];
    }

    /// <summary>Interleaved PCM of the last packet.</summary>
    public float[] Output { get; }

    public int Channels => channels;

    public int SampleRate => id.SampleRate;

    public int MaximumFrames => id.BlockSize1 / 2 + id.BlockSize0 / 2;

    /// <summary>Forgets the overlap with the previous packet (after a seek or a lost packet).</summary>
    public void Reset()
    {
        primed = false;
        previousSize = 0;
    }

    /// <summary>Decodes a packet. Returns the number of finished frames now in <see cref="Output"/> (0 for the first packet after a reset), or -1 if the packet is not audio.</summary>
    public int Decode(ReadOnlySpan<byte> packet)
    {
        VorbisBitReader reader = new(packet);
        if (reader.Read(1) != 0) return -1;
        int mode = (int)reader.Read(VorbisCodebook.Ilog(setup.Modes.Length - 1));
        if (mode >= setup.Modes.Length) return -1;
        VorbisMode modeInfo = setup.Modes[mode];
        int n = modeInfo.LongBlock ? id.BlockSize1 : id.BlockSize0;
        bool previousLong = true, nextLong = true;
        if (modeInfo.LongBlock)
        {
            previousLong = reader.Read(1) == 1;
            nextLong = reader.Read(1) == 1;
        }
        if (reader.EndOfPacket) return -1;
        int half = n / 2;
        VorbisMapping mapping = setup.Mappings[modeInfo.Mapping];

        // Floors, in channel order. The end of the packet inside a floor silences the whole packet.
        bool silent = false;
        for (int c = 0; c < channels; c++)
        {
            Array.Clear(spectrum[c], 0, half);
            VorbisFloor1 floor = setup.Floors[mapping.SubmapFloor[mapping.Mux[c]]];
            floorUsed[c] = floor.Decode(ref reader, setup.Codebooks);
            if (reader.EndOfPacket)
            {
                silent = true;
                break;
            }
            if (floorUsed[c]) floor.Synthesize(floorCurve[c], half);
        }

        if (silent)
        {
            Array.Fill(hasFloor, false);
            for (int c = 0; c < channels; c++) Array.Clear(spectrum[c], 0, half);
        }
        else
        {
            // A channel coupled to a used channel is used too (section 4.3.3).
            for (int c = 0; c < channels; c++) skipResidue[c] = !floorUsed[c];
            for (int i = 0; i < mapping.Magnitude.Length; i++)
            {
                int m = mapping.Magnitude[i], a = mapping.Angle[i];
                if (!skipResidue[m] || !skipResidue[a])
                {
                    skipResidue[m] = false;
                    skipResidue[a] = false;
                }
            }
            SubmapPlan[] plan = plans[modeInfo.Mapping];
            for (int s = 0; s < plan.Length; s++)
            {
                SubmapPlan submap = plan[s];
                if (submap.Channels.Length == 0) continue;
                for (int j = 0; j < submap.Channels.Length; j++) submap.Skip[j] = skipResidue[submap.Channels[j]];
                setup.Residues[mapping.SubmapResidue[s]].Decode(ref reader, setup.Codebooks, half, submap.Vectors, submap.Skip);
            }
            for (int i = mapping.Magnitude.Length - 1; i >= 0; i--) InverseCoupling(spectrum[mapping.Magnitude[i]], spectrum[mapping.Angle[i]], half);
            for (int c = 0; c < channels; c++)
            {
                // A channel kept alive only by coupling has no floor of its own: its floor is the zero curve, so it ends up silent.
                hasFloor[c] = floorUsed[c];
                if (!floorUsed[c])
                {
                    Array.Clear(spectrum[c], 0, half);
                    continue;
                }
                Span<float> bins = spectrum[c].AsSpan(0, half);
                ReadOnlySpan<float> curve = floorCurve[c].AsSpan(0, half);
                for (int i = 0; i < bins.Length; i++) bins[i] *= curve[i];
            }
        }

        // Window geometry (section 4.3.1).
        int leftStart, leftEnd, leftSize, rightStart, rightEnd, rightSize;
        if (modeInfo.LongBlock && !previousLong)
        {
            leftStart = n / 4 - id.BlockSize0 / 4;
            leftEnd = n / 4 + id.BlockSize0 / 4;
            leftSize = id.BlockSize0 / 2;
        }
        else
        {
            leftStart = 0;
            leftEnd = half;
            leftSize = half;
        }
        if (modeInfo.LongBlock && !nextLong)
        {
            rightStart = n * 3 / 4 - id.BlockSize0 / 4;
            rightEnd = n * 3 / 4 + id.BlockSize0 / 4;
            rightSize = id.BlockSize0 / 2;
        }
        else
        {
            rightStart = half;
            rightEnd = n;
            rightSize = half;
        }
        float[] leftSlope = leftSize == id.BlockSize0 / 2 ? shortSlope : longSlope;
        float[] rightSlope = rightSize == id.BlockSize0 / 2 ? shortSlope : longSlope;
        Imdct transform = modeInfo.LongBlock ? longTransform : shortTransform;

        int frames = primed ? previousSize / 4 + n / 4 : 0;
        int offset = (n - previousSize) / 4;
        Span<float> y = time.AsSpan(0, n);
        for (int c = 0; c < channels; c++)
        {
            if (hasFloor[c])
            {
                transform.Transform(spectrum[c], y, TransformScale);
                y[..leftStart].Clear();
                for (int i = leftStart; i < leftEnd; i++) y[i] *= leftSlope[i - leftStart];
                for (int i = rightStart; i < rightEnd; i++) y[i] *= rightSlope[rightSize - 1 - (i - rightStart)];
                y[rightEnd..].Clear();
            }
            else y.Clear();

            if (primed)
            {
                int destination = channelOrder[c];
                float[] saved = pending[c];
                int savedLength = previousSize / 2;
                for (int i = 0; i < frames; i++)
                {
                    float value = i < savedLength ? saved[i] : 0f;
                    int index = i + offset;
                    if ((uint)index < (uint)n) value += y[index];
                    Output[i * channels + destination] = value;
                }
            }
            y[half..].CopyTo(pending[c]);
        }
        previousSize = n;
        primed = true;
        return frames;
    }

    private static void InverseCoupling(float[] magnitude, float[] angle, int half)
    {
        for (int i = 0; i < half; i++)
        {
            float m = magnitude[i], a = angle[i];
            float newM, newA;
            if (m > 0)
            {
                if (a > 0)
                {
                    newM = m;
                    newA = m - a;
                }
                else
                {
                    newA = m;
                    newM = m + a;
                }
            }
            else if (a > 0)
            {
                newM = m;
                newA = m + a;
            }
            else
            {
                newA = m;
                newM = m - a;
            }
            magnitude[i] = newM;
            angle[i] = newA;
        }
    }

    private static float[] Slope(int length)
    {
        float[] slope = new float[length];
        for (int i = 0; i < length; i++)
        {
            double s = Math.Sin((i + 0.5) / length * Math.PI / 2);
            slope[i] = (float)Math.Sin(Math.PI / 2 * s * s);
        }
        return slope;
    }

    private static int[] WaveOrder(int channels) => channels switch
    {
        3 => [0, 2, 1],
        5 => [0, 2, 1, 3, 4],
        6 => [0, 2, 1, 4, 5, 3],
        7 => [0, 2, 1, 5, 6, 4, 3],
        8 => [0, 2, 1, 6, 7, 4, 5, 3],
        _ => [.. Enumerable.Range(0, channels)]
    };
}
