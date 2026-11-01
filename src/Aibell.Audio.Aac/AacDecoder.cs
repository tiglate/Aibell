namespace Aibell.Audio.Aac;

/// <summary>
/// AAC-LC decoder for raw_data_block() payloads (ISO/IEC 14496-3 subpart 4). Single-threaded; all buffers are allocated once, so
/// decoding a frame allocates nothing in the steady state. A frame that fails to decode throws <see cref="AacException"/>; the decoder stays usable
/// and the caller decides whether to <see cref="Conceal"/> the gap with silence.
/// </summary>
public sealed class AacDecoder
{
    public const int FrameLength = 1024;
    private const int MaxChannels = 8;
    private const int MaxCouplings = 4;
    private const int NoiseOffset = 90;
    private const int ScaleFactorOffset = 100;

    private static readonly float[] ScaleFactorGain = BuildGains();
    private static readonly float[] Pow43 = BuildPow43();
    private static readonly int[] TnsMaxBandsLong = [31, 31, 34, 40, 42, 51, 46, 46, 42, 42, 42, 39];
    private static readonly int[] TnsMaxBandsShort = [9, 9, 10, 14, 14, 14, 14, 14, 14, 14, 14, 14];
    private static readonly string[][] Layouts =
    [
        [], ["C"], ["L", "R"], ["C", "L", "R"], ["C", "L", "R", "S"], ["C", "L", "R", "Ls", "Rs"], ["C", "L", "R", "Ls", "Rs", "LFE"],
        ["C", "L", "R", "SL", "SR", "Ls", "Rs", "LFE"]
    ];

    private readonly Imdct longTransform = new(1024);
    private readonly Imdct shortTransform = new(128);
    private readonly Filterbank[] filterbanks = new Filterbank[MaxChannels];
    private readonly float[][] output = new float[MaxChannels][];
    private readonly ChannelStream[] chans = new ChannelStream[MaxChannels];
    private readonly CouplingElement[] couplings = new CouplingElement[MaxCouplings];
    private readonly int[] sceChannel = new int[16];
    private readonly int[] cpeChannel = new int[16];
    private int couplingCount;

    /// <summary>
    /// -1 (the default) applies the coupling gain as cc_scale^-gain, as FFmpeg does; +1 would apply it as the ISO/IEC 14496-3 and 13818-7 texts
    /// literally write it, cc_scale^gain. FFmpeg's decoder was corrected to this direction "to attain conformance" with the MPEG conformance
    /// streams (its 2008 commit), so the written text is taken to be the one that is wrong; see aac-decoder.md.
    /// </summary>
    internal int CouplingExponentSign { get; set; } = -1;
    private readonly bool[] msUsed = new bool[8 * ChannelStream.BandStride];
    private readonly uint[] noiseSeedAtBand = new uint[8 * ChannelStream.BandStride];
    private readonly float[] noiseScratch = new float[FrameLength];
    private readonly int[] pair = new int[4];
    private readonly CrcRange[] crcRanges = new CrcRange[64];
    private int crcRangeCount;
    private bool crcUnknown;
    private uint noiseSeed = 0x2545f491;
    private int samplingIndex = -1;
    private int channelConfiguration = -1;
    private int objectType = 2;
    private int decodedChannels;
    private string[] roles = [];

    public AacDecoder()
    {
        for (int i = 0; i < MaxChannels; i++)
        {
            filterbanks[i] = new Filterbank(longTransform, shortTransform);
            output[i] = new float[FrameLength];
            chans[i] = new ChannelStream();
        }
        for (int i = 0; i < MaxCouplings; i++) couplings[i] = new CouplingElement();
    }

    /// <summary>True once an SBR extension (HE-AAC) has been seen: the core decodes at half the real sampling rate and sounds dull.</summary>
    public bool SbrSignaled { get; private set; }

    /// <summary>When set, a frame that signals SBR (HE-AAC) throws <see cref="AacUnsupportedException"/> instead of decoding the core alone.</summary>
    public bool RejectSbr { get; set; } = true;

    /// <summary>Counts of the coding tools met so far; used by tests and diagnostics, never by decoding.</summary>
    public AacStatistics Statistics { get; } = new();

    public int SampleRate => samplingIndex < 0 ? 0 : SamplingRates.FromIndex(samplingIndex);

    /// <summary>Number of channels in the stream configuration (before any downmix).</summary>
    public int Channels => decodedChannels;

    /// <summary>
    /// The bit ranges of the last decoded raw data block that an ADTS <c>crc_check</c> protects (ISO/IEC 13818-7, subclause 8.1.1.1), or null
    /// when the block held something whose extent was not parsed (a coupling channel element, a decoding error).
    /// </summary>
    internal ReadOnlySpan<CrcRange> ProtectedRanges => crcUnknown ? default : crcRanges.AsSpan(0, crcRangeCount);

    internal bool CrcRangesKnown => !crcUnknown;

    /// <summary>The quantized spectrum of the first channel stream of the last element, for tests.</summary>
    internal ReadOnlySpan<int> FirstQuantized => chans[0].Quantized;

    /// <summary>The decoded samples of a channel after <see cref="Decode"/>: 1024 floats in ±1.</summary>
    public ReadOnlySpan<float> GetChannel(int channel) => output[channel];

    /// <summary>Decodes an ADTS payload. Returns the number of samples per channel (always 1024).</summary>
    public int Decode(in AdtsHeader header, ReadOnlySpan<byte> payload)
    {
        if (header.RawBlocks != 1) throw new AacException("Use DecodeBlock for ADTS frames with several raw data blocks.");
        return Decode(header.ObjectType, header.SamplingIndex, header.ChannelConfiguration, payload);
    }

    /// <summary>
    /// Decodes raw data block number <paramref name="blockIndex"/> of an ADTS frame, which may hold up to four. Call it for each block in order, starting
    /// with <paramref name="offset"/> = <see cref="AdtsHeader.HeaderLength"/>; it moves <paramref name="offset"/> to the start of the next block (past the
    /// block's CRC when the frame is protected). <paramref name="crcValid"/> is the block's CRC verdict, or null when there was nothing to check.
    /// </summary>
    public int DecodeBlock(ReadOnlySpan<byte> frame, in AdtsHeader header, int blockIndex, ref int offset, out bool? crcValid)
    {
        crcValid = null;
        if ((uint)blockIndex >= (uint)header.RawBlocks) throw new ArgumentOutOfRangeException(nameof(blockIndex));
        if (frame.Length < header.FrameLength || offset < header.HeaderLength || offset >= header.FrameLength) throw new AacException("The ADTS frame holds fewer raw data blocks than it announces.");
        int samples = Decode(header.ObjectType, header.SamplingIndex, header.ChannelConfiguration, frame[offset..header.FrameLength]);
        int used = LastBlockBytes;
        if (header.HasCrc && CrcRangesKnown)
        {
            if (header.RawBlocks == 1)
            {
                ushort stored = (ushort)(frame[7] << 8 | frame[8]);
                crcValid = AdtsCrc.Compute(frame[..header.FrameLength], header.HeaderLength, ProtectedRanges) == stored;
            }
            else if (offset + used + 2 <= header.FrameLength)
            {
                ushort stored = (ushort)(frame[offset + used] << 8 | frame[offset + used + 1]);
                crcValid = AdtsCrc.ComputeBlock(frame[offset..header.FrameLength], ProtectedRanges) == stored;
            }
        }
        if (header.HasCrc && header.RawBlocks > 1) used += 2;
        offset += used;
        if (blockIndex + 1 < header.RawBlocks && offset >= header.FrameLength) throw new AacException("The ADTS frame holds fewer raw data blocks than it announces.");
        return samples;
    }

    /// <summary>Decodes one raw_data_block() with the given stream parameters.</summary>
    public int Decode(int audioObjectType, int sfIndex, int channelConfig, ReadOnlySpan<byte> payload)
    {
        if (audioObjectType is not (1 or 2)) throw new AacUnsupportedException("Only AAC LC (and Main without prediction) streams are supported.");
        if (sfIndex is < 0 or > 11) throw new AacUnsupportedException("This sampling rate is not supported.");
        if (channelConfig is < 0 or > 7) throw new AacUnsupportedException("This channel configuration is not supported.");
        if (sfIndex != samplingIndex || channelConfig != channelConfiguration)
        {
            samplingIndex = sfIndex;
            channelConfiguration = channelConfig;
            foreach (Filterbank bank in filterbanks) bank.Reset();
        }
        objectType = audioObjectType;
        roles = Layouts[channelConfig];
        decodedChannels = 0;
        crcRangeCount = 0;
        crcUnknown = true; // known only once the block has been parsed to its end
        couplingCount = 0;
        Array.Fill(sceChannel, -1);
        Array.Fill(cpeChannel, -1);
        BitReader reader = new(payload);
        bool done = false;
        while (!done && reader.BitsLeft >= 3)
        {
            uint id = reader.ReadBits(3);
            int elementStart = reader.BitPosition;
            switch (id)
            {
                case 0: // SCE
                case 3: // LFE
                    int singleTag = (int)reader.ReadBits(4);
                    if (id == 0) sceChannel[singleTag] = decodedChannels;
                    DecodeSingle(ref reader);
                    AddCrcRange(elementStart, reader.BitPosition - elementStart, 192);
                    break;
                case 1:
                    cpeChannel[(int)reader.ReadBits(4)] = decodedChannels;
                    DecodePair(ref reader);
                    AddCrcRange(elementStart, reader.BitPosition - elementStart, 192);
                    AddCrcRange(secondStreamStart, reader.BitPosition - secondStreamStart, 128);
                    break;
                case 2:
                    DecodeCoupling(ref reader);
                    AddCrcRange(elementStart, reader.BitPosition - elementStart, 192);
                    break;
                case 4:
                    SkipDataStream(ref reader);
                    AddCrcRange(elementStart, reader.BitPosition - elementStart, reader.BitPosition - elementStart);
                    break;
                case 5:
                    SkipProgramConfig(ref reader);
                    AddCrcRange(elementStart, reader.BitPosition - elementStart, reader.BitPosition - elementStart);
                    break;
                case 6:
                    ReadFill(ref reader);
                    break;
                default: // END
                    done = true;
                    break;
            }
        }
        crcUnknown = crcRangeCount > crcRanges.Length;
        reader.ByteAlign();
        LastBlockBytes = Math.Min(reader.BitPosition / 8, payload.Length);
        FinishChannels();
        if (channelConfig != 0 && decodedChannels > roles.Length) throw new AacException("The frame has more channels than its configuration.");
        for (int c = decodedChannels; c < Math.Max(decodedChannels, roles.Length); c++) Array.Clear(output[c]);
        if (decodedChannels == 0 && roles.Length > 0) decodedChannels = roles.Length;
        return FrameLength;
    }

    /// <summary>Forgets the overlap of every channel, as after a jump in the stream; the next frames fade in from silence.</summary>
    public void Reset()
    {
        foreach (Filterbank bank in filterbanks) bank.Reset();
    }

    /// <summary>Silences the output for a frame that could not be decoded, keeping the stream's timing.</summary>
    public void Conceal(int channels)
    {
        decodedChannels = Math.Clamp(channels, 1, MaxChannels);
        for (int c = 0; c < decodedChannels; c++)
        {
            Array.Clear(output[c]);
            filterbanks[c].Reset();
        }
    }

    /// <summary>Mixes the last decoded frame to interleaved stereo (mono is duplicated, surround is folded down); returns frames written.</summary>
    public int MixToStereo(Span<float> interleaved)
    {
        if (interleaved.Length < 2 * FrameLength) throw new ArgumentException("The buffer must hold 2048 samples.", nameof(interleaved));
        int count = decodedChannels;
        if (count == 1)
        {
            for (int n = 0; n < FrameLength; n++) interleaved[2 * n] = interleaved[2 * n + 1] = output[0][n];
            return FrameLength;
        }
        if (count == 2)
        {
            for (int n = 0; n < FrameLength; n++)
            {
                interleaved[2 * n] = output[0][n];
                interleaved[2 * n + 1] = output[1][n];
            }
            return FrameLength;
        }
        // ITU-R BS.775 style fold-down: centre and surrounds at -3 dB, LFE dropped, scaled so full-scale input cannot clip.
        // Without a channel configuration (a program config element instead) the channels are taken in the standard order for their count.
        string[] mixRoles = roles.Length > 0 ? roles : Layouts[Math.Min(count, Layouts.Length - 1)];
        float norm = 1f / (1f + 0.7071f + 0.7071f);
        Span<float> leftGain = stackalloc float[MaxChannels];
        Span<float> rightGain = stackalloc float[MaxChannels];
        for (int c = 0; c < count && c < MaxChannels; c++)
        {
            string role = c < mixRoles.Length ? mixRoles[c] : "";
            (leftGain[c], rightGain[c]) = role switch
            {
                "C" => (0.7071f, 0.7071f),
                "L" or "SL" or "Ls" => (1f, 0f),
                "R" or "SR" or "Rs" => (0f, 1f),
                "S" => (0.7071f, 0.7071f),
                _ => (0f, 0f)
            };
            if (role is "Ls" or "SL" or "Rs" or "SR") { leftGain[c] *= 0.7071f; rightGain[c] *= 0.7071f; }
        }
        for (int n = 0; n < FrameLength; n++)
        {
            float l = 0, r = 0;
            for (int c = 0; c < count && c < MaxChannels; c++)
            {
                l += output[c][n] * leftGain[c];
                r += output[c][n] * rightGain[c];
            }
            interleaved[2 * n] = l * norm;
            interleaved[2 * n + 1] = r * norm;
        }
        return FrameLength;
    }

    /// <summary>Bytes the last decoded raw_data_block() occupied, including its closing alignment.</summary>
    public int LastBlockBytes { get; private set; }

    private void DecodeSingle(ref BitReader reader)
    {
        int channel = decodedChannels;
        if (channel >= MaxChannels) throw new AacException("Too many channels in one frame.");
        ChannelStream stream = chans[channel];
        stream.Info = stream.OwnInfo;
        ReadChannel(ref reader, stream, commonWindow: false);
        Dequantize(stream);
        GenerateNoise(stream, null, default);
        decodedChannels = channel + 1;
    }

    private int secondStreamStart;

    private void AddCrcRange(int start, int available, int required)
    {
        if (crcRangeCount < crcRanges.Length) crcRanges[crcRangeCount] = new CrcRange(start, available, required);
        crcRangeCount++;
    }

    private void DecodePair(ref BitReader reader)
    {
        int channel = decodedChannels;
        if (channel + 2 > MaxChannels) throw new AacException("Too many channels in one frame.");
        ChannelStream left = chans[channel], right = chans[channel + 1];
        bool common = reader.ReadFlag();
        int maskPresent = 0;
        Array.Clear(msUsed);
        if (common)
        {
            left.Info = left.OwnInfo;
            right.Info = left.OwnInfo;
            left.Info.Read(ref reader, samplingIndex, objectType);
            maskPresent = (int)reader.ReadBits(2);
            if (maskPresent == 3) throw new AacException("The ms_mask_present value 3 is reserved.");
            if (maskPresent == 1)
            {
                for (int g = 0; g < left.Info.NumWindowGroups; g++)
                {
                    for (int sfb = 0; sfb < left.Info.MaxSfb; sfb++) msUsed[g * ChannelStream.BandStride + sfb] = reader.ReadFlag();
                }
            }
        }
        else
        {
            left.Info = left.OwnInfo;
            right.Info = right.OwnInfo;
        }
        ReadChannel(ref reader, left, common);
        secondStreamStart = reader.BitPosition;
        ReadChannel(ref reader, right, common);
        PairContext context = new(maskPresent, common);
        Reconstruct(left, right, context);
        decodedChannels = channel + 2;
    }

    private readonly record struct PairContext(int MaskPresent, bool Common);

    private void Reconstruct(ChannelStream left, ChannelStream right, PairContext context)
    {
        Dequantize(left);
        Dequantize(right);
        GenerateNoise(left, null, context);
        GenerateNoise(right, left, context);
        if (!context.Common) return;
        IcsInfo info = left.Info;
        if (context.MaskPresent != 0) ApplyMidSide(left, right, info, context.MaskPresent);
        ApplyIntensity(left, right, info, context.MaskPresent);
    }

    private void DecodeCoupling(ref BitReader reader)
    {
        reader.Skip(4); // element_instance_tag
        if (couplingCount >= MaxCouplings) throw new AacException("Too many coupling channel elements in one block.");
        CouplingElement cce = couplings[couplingCount];
        cce.Independent = reader.ReadFlag();
        cce.TargetCount = (int)reader.ReadBits(3) + 1;
        int lists = 0;
        for (int c = 0; c < cce.TargetCount; c++)
        {
            lists++;
            cce.TargetIsCpe[c] = reader.ReadFlag();
            cce.TargetTag[c] = (int)reader.ReadBits(4);
            cce.Left[c] = cce.Right[c] = false;
            if (cce.TargetIsCpe[c])
            {
                cce.Left[c] = reader.ReadFlag();
                cce.Right[c] = reader.ReadFlag();
                if (cce.Left[c] && cce.Right[c]) lists++;
            }
        }
        cce.Lists = lists;
        cce.Domain = reader.ReadBit();
        cce.Signed = reader.ReadFlag();
        cce.Scale = (int)reader.ReadBits(2);
        ChannelStream stream = cce.Stream;
        stream.Info = stream.OwnInfo;
        ReadChannel(ref reader, stream, commonWindow: false);
        Dequantize(stream);
        IcsInfo info = stream.Info;
        // List 0 (the first target) is coupled at natural scale: exponent 0, in phase.
        Array.Clear(cce.Gain, 0, ChannelStream.BandStride * 8);
        Array.Fill(cce.Sign, (sbyte)1, 0, ChannelStream.BandStride * 8);
        for (int list = 1; list < lists; list++)
        {
            bool common = cce.Independent || reader.ReadFlag();
            int baseIndex = list * 8 * ChannelStream.BandStride;
            if (common)
            {
                int value = Codebooks.ScaleFactor.Decode(ref reader) - 60;
                for (int g = 0; g < info.NumWindowGroups; g++)
                {
                    for (int sfb = 0; sfb < info.MaxSfb; sfb++)
                    {
                        cce.Gain[baseIndex + g * ChannelStream.BandStride + sfb] = value;
                        cce.Sign[baseIndex + g * ChannelStream.BandStride + sfb] = 1;
                    }
                }
                continue;
            }
            // Differential elements, accumulated over the list.
            int accumulated = 0;
            for (int g = 0; g < info.NumWindowGroups; g++)
            {
                for (int sfb = 0; sfb < info.MaxSfb; sfb++)
                {
                    int at = baseIndex + g * ChannelStream.BandStride + sfb;
                    cce.Sign[at] = 1;
                    if (stream.Codebook[g * ChannelStream.BandStride + sfb] != Codebooks.ZeroBook)
                        accumulated += Codebooks.ScaleFactor.Decode(ref reader) - 60;
                    // ISO/IEC 14496-3: the phase is the lowest bit of the accumulated element and the exponent the rest.
                    cce.Sign[at] = (sbyte)(cce.Signed ? 1 - 2 * (accumulated & 1) : 1);
                    cce.Gain[at] = cce.Signed ? accumulated >> 1 : accumulated;
                }
            }
        }
        couplingCount++;
        Statistics.CouplingElements++;
    }

    /// <summary>Applies coupling, TNS and the filterbank to every channel of the block, once all its elements are known.</summary>
    private void FinishChannels()
    {
        Span<(int Channel, int List)> targets = stackalloc (int, int)[CouplingElement.MaxTargets * 2];
        // An independently switched coupling channel is decoded to the time domain on its own and added to its targets afterwards.
        for (int k = 0; k < couplingCount; k++)
        {
            CouplingElement cce = couplings[k];
            if (!cce.Independent) continue;
            if (cce.Stream.TnsPresent) ApplyTns(cce.Stream);
            cce.Bank ??= new Filterbank(longTransform, shortTransform);
            cce.Bank.Process(cce.Stream.Spectrum, cce.Stream.Info.Sequence, cce.Stream.Info.WindowShape, cce.Time);
        }
        for (int channel = 0; channel < decodedChannels; channel++)
        {
            ChannelStream stream = chans[channel];
            for (int k = 0; k < couplingCount; k++)
            {
                CouplingElement cce = couplings[k];
                if (cce.Independent || cce.Domain != 0) continue;
                CoupleDependent(cce, channel, stream, targets);
            }
            Statistics.Count(stream.Info.Sequence);
            if (stream.Info.WindowShape == 1) Statistics.KaiserBesselChannels++;
            if (stream.TnsPresent)
            {
                Statistics.TnsChannels++;
                ApplyTns(stream);
            }
            for (int k = 0; k < couplingCount; k++)
            {
                CouplingElement cce = couplings[k];
                if (cce.Independent || cce.Domain != 1) continue;
                CoupleDependent(cce, channel, stream, targets);
            }
            filterbanks[channel].Process(stream.Spectrum, stream.Info.Sequence, stream.Info.WindowShape, output[channel]);
            for (int k = 0; k < couplingCount; k++)
            {
                CouplingElement cce = couplings[k];
                if (!cce.Independent) continue;
                int found = cce.Targets(sceChannel, cpeChannel, targets);
                for (int t = 0; t < found; t++)
                {
                    if (targets[t].Channel != channel) continue;
                    float factor = targets[t].List == 0 ? 1f : cce.GainFactor(cce.Gain[targets[t].List * 8 * ChannelStream.BandStride], 1, CouplingExponentSign);
                    for (int n = 0; n < FrameLength; n++) output[channel][n] += factor * cce.Time[n];
                }
            }
        }
    }

    // Adds the coupling channel's spectrum, band by band with the gain of its list, to a channel with the same window layout.
    private void CoupleDependent(CouplingElement cce, int channel, ChannelStream target, Span<(int Channel, int List)> targets)
    {
        IcsInfo source = cce.Stream.Info, destination = target.Info;
        if (source.Sequence != destination.Sequence || source.NumWindowGroups != destination.NumWindowGroups) return;
        int found = cce.Targets(sceChannel, cpeChannel, targets);
        for (int t = 0; t < found; t++)
        {
            if (targets[t].Channel != channel) continue;
            int list = targets[t].List;
            int windowStart = 0;
            for (int g = 0; g < source.NumWindowGroups; g++)
            {
                if (source.GroupLength[g] != destination.GroupLength[g]) return;
                for (int sfb = 0; sfb < source.MaxSfb; sfb++)
                {
                    int at = (list * 8 + g) * ChannelStream.BandStride + sfb;
                    float factor = list == 0 ? 1f : cce.GainFactor(cce.Gain[at], cce.Sign[at], CouplingExponentSign);
                    int width = source.SwbOffset[sfb + 1] - source.SwbOffset[sfb];
                    for (int w = 0; w < source.GroupLength[g]; w++)
                    {
                        int start = (windowStart + w) * (source.IsShort ? 128 : 0) + source.SwbOffset[sfb];
                        for (int i = start; i < start + width; i++) target.Spectrum[i] += factor * cce.Stream.Spectrum[i];
                    }
                }
                windowStart += source.GroupLength[g];
            }
        }
    }

    // ---- individual_channel_stream() -----------------------------------------------------------------------------

    private void ReadChannel(ref BitReader reader, ChannelStream stream, bool commonWindow)
    {
        stream.GlobalGain = (int)reader.ReadBits(8);
        if (!commonWindow) stream.Info.Read(ref reader, samplingIndex, objectType);
        IcsInfo info = stream.Info;
        ReadSections(ref reader, stream, info);
        ReadScaleFactors(ref reader, stream, info);
        stream.PulsePresent = reader.ReadFlag();
        if (stream.PulsePresent) ReadPulse(ref reader, stream, info);
        stream.TnsPresent = reader.ReadFlag();
        if (stream.TnsPresent) ReadTns(ref reader, stream, info);
        if (reader.ReadFlag()) throw new AacUnsupportedException("AAC SSR gain control is not supported.");
        ReadSpectrum(ref reader, stream, info);
    }

    private static void ReadSections(ref BitReader reader, ChannelStream stream, IcsInfo info)
    {
        int lengthBits = info.IsShort ? 3 : 5;
        int escape = (1 << lengthBits) - 1;
        for (int g = 0; g < info.NumWindowGroups; g++)
        {
            int k = 0;
            while (k < info.MaxSfb)
            {
                int book = (int)reader.ReadBits(4);
                if (book == 12) throw new AacException("The reserved codebook 12 is used.");
                int length = 0, increment;
                do
                {
                    increment = (int)reader.ReadBits(lengthBits);
                    length += increment;
                }
                while (increment == escape);
                if (length == 0 || k + length > info.MaxSfb) throw new AacException("A section runs past max_sfb.");
                for (int sfb = k; sfb < k + length; sfb++) stream.Codebook[g * ChannelStream.BandStride + sfb] = (byte)book;
                k += length;
            }
            Array.Clear(stream.Codebook, g * ChannelStream.BandStride + info.MaxSfb, ChannelStream.BandStride - info.MaxSfb);
        }
    }

    private static void ReadScaleFactors(ref BitReader reader, ChannelStream stream, IcsInfo info)
    {
        int last = stream.GlobalGain;
        int noise = stream.GlobalGain - NoiseOffset - 256;
        int intensity = 0;
        bool firstNoise = true;
        for (int g = 0; g < info.NumWindowGroups; g++)
        {
            for (int sfb = 0; sfb < info.MaxSfb; sfb++)
            {
                int at = g * ChannelStream.BandStride + sfb;
                int book = stream.Codebook[at];
                if (book == Codebooks.ZeroBook)
                {
                    stream.ScaleFactor[at] = 0;
                }
                else if (book >= Codebooks.IntensityBook2)
                {
                    intensity += Codebooks.ScaleFactor.Decode(ref reader) - 60;
                    stream.ScaleFactor[at] = intensity;
                }
                else if (book == Codebooks.NoiseBook)
                {
                    if (firstNoise)
                    {
                        noise += (int)reader.ReadBits(9);
                        firstNoise = false;
                    }
                    else noise += Codebooks.ScaleFactor.Decode(ref reader) - 60;
                    stream.ScaleFactor[at] = noise;
                }
                else
                {
                    last += Codebooks.ScaleFactor.Decode(ref reader) - 60;
                    if (last is < 0 or > 255) throw new AacException("A scalefactor is out of range.");
                    stream.ScaleFactor[at] = last;
                }
            }
        }
    }

    private static void ReadPulse(ref BitReader reader, ChannelStream stream, IcsInfo info)
    {
        if (info.IsShort) throw new AacException("Pulse data is not allowed in short blocks.");
        stream.PulseCount = (int)reader.ReadBits(2) + 1;
        stream.PulseStartSfb = (int)reader.ReadBits(6);
        if (stream.PulseStartSfb >= info.NumSwb) throw new AacException("pulse_start_sfb is out of range.");
        for (int i = 0; i < stream.PulseCount; i++)
        {
            stream.PulseOffset[i] = (int)reader.ReadBits(5);
            stream.PulseAmplitude[i] = (int)reader.ReadBits(4);
        }
    }

    private static void ReadTns(ref BitReader reader, ChannelStream stream, IcsInfo info)
    {
        bool isShort = info.IsShort;
        for (int w = 0; w < info.NumWindows; w++)
        {
            int filters = (int)reader.ReadBits(isShort ? 1 : 2);
            stream.TnsFilterCount[w] = filters;
            if (filters == 0) continue;
            int resolution = reader.ReadBit();
            stream.TnsResolution[w] = resolution;
            for (int f = 0; f < filters; f++)
            {
                stream.TnsLength[w, f] = (int)reader.ReadBits(isShort ? 4 : 6);
                int order = (int)reader.ReadBits(isShort ? 3 : 5);
                stream.TnsOrder[w, f] = order;
                if (order == 0) continue;
                stream.TnsDirection[w, f] = reader.ReadBit();
                int compress = reader.ReadBit();
                stream.TnsCompress[w, f] = compress;
                int bits = resolution + 3 - compress;
                for (int i = 0; i < order; i++) stream.TnsCoefficient[w, f, i] = (int)reader.ReadBits(bits);
            }
        }
    }

    private void ReadSpectrum(ref BitReader reader, ChannelStream stream, IcsInfo info)
    {
        Array.Clear(stream.Quantized);
        Span<int> values = pair;
        int[] offsets = info.SwbOffset;
        int windowStart = 0;
        for (int g = 0; g < info.NumWindowGroups; g++)
        {
            int groupLength = info.GroupLength[g];
            for (int sfb = 0; sfb < info.MaxSfb; sfb++)
            {
                int book = stream.Codebook[g * ChannelStream.BandStride + sfb];
                if (book is Codebooks.ZeroBook or >= Codebooks.NoiseBook) continue;
                int width = offsets[sfb + 1] - offsets[sfb];
                int dimension = Codebooks.Size(book);
                for (int w = 0; w < groupLength; w++)
                {
                    int baseIndex = (windowStart + w) * (info.IsShort ? 128 : 0) + offsets[sfb];
                    for (int bin = 0; bin < width; bin += dimension)
                    {
                        Codebooks.Decode(book, ref reader, values);
                        if (book == 11)
                        {
                            for (int i = 0; i < 2; i++)
                            {
                                if (Math.Abs(values[i]) == 16) values[i] = values[i] < 0 ? -ReadEscape(ref reader) : ReadEscape(ref reader);
                            }
                        }
                        for (int i = 0; i < dimension; i++) stream.Quantized[baseIndex + bin + i] = values[i];
                    }
                }
            }
            windowStart += groupLength;
        }
        if (stream.PulsePresent)
        {
            Statistics.PulseChannels++;
            int k = offsets[stream.PulseStartSfb];
            for (int j = 0; j < stream.PulseCount; j++)
            {
                k += stream.PulseOffset[j];
                if (k >= FrameLength) throw new AacException("A pulse position is out of range.");
                if (stream.Quantized[k] > 0) stream.Quantized[k] += stream.PulseAmplitude[j];
                else stream.Quantized[k] -= stream.PulseAmplitude[j];
            }
        }
    }

    private static int ReadEscape(ref BitReader reader)
    {
        int bits = 4;
        while (reader.ReadFlag())
        {
            if (++bits > 12) throw new AacException("An escape sequence is too long.");
        }
        return (1 << bits) + (int)reader.ReadBits(bits);
    }

    // ---- reconstruction ------------------------------------------------------------------------------------------

    private static void Dequantize(ChannelStream stream)
    {
        IcsInfo info = stream.Info;
        Array.Clear(stream.Spectrum);
        int windowStart = 0;
        for (int g = 0; g < info.NumWindowGroups; g++)
        {
            for (int sfb = 0; sfb < info.MaxSfb; sfb++)
            {
                int at = g * ChannelStream.BandStride + sfb;
                int book = stream.Codebook[at];
                if (book is Codebooks.ZeroBook or >= Codebooks.NoiseBook) continue;
                float gain = ScaleFactorGain[stream.ScaleFactor[at]];
                int width = info.SwbOffset[sfb + 1] - info.SwbOffset[sfb];
                for (int w = 0; w < info.GroupLength[g]; w++)
                {
                    int start = (windowStart + w) * (info.IsShort ? 128 : 0) + info.SwbOffset[sfb];
                    for (int bin = 0; bin < width; bin++)
                    {
                        int q = stream.Quantized[start + bin];
                        float magnitude = Pow43[Math.Min(Math.Abs(q), Pow43.Length - 1)] * gain;
                        stream.Spectrum[start + bin] = q < 0 ? -magnitude : magnitude;
                    }
                }
            }
            windowStart += info.GroupLength[g];
        }
    }

    private void GenerateNoise(ChannelStream stream, ChannelStream? first, PairContext context)
    {
        IcsInfo info = stream.Info;
        int windowStart = 0;
        for (int g = 0; g < info.NumWindowGroups; g++)
        {
            for (int sfb = 0; sfb < info.MaxSfb; sfb++)
            {
                int at = g * ChannelStream.BandStride + sfb;
                if (stream.Codebook[at] != Codebooks.NoiseBook) continue;
                Statistics.NoiseBands++;
                // A pair may ask for the same noise in both channels: the second channel replays the first one's random numbers.
                bool correlated = first is not null && context.Common && first.Codebook[at] == Codebooks.NoiseBook
                    && (context.MaskPresent == 2 || context.MaskPresent == 1 && msUsed[at]);
                if (first is null) noiseSeedAtBand[at] = noiseSeed;
                int width = info.SwbOffset[sfb + 1] - info.SwbOffset[sfb];
                double target = Math.Pow(2.0, 0.25 * stream.ScaleFactor[at]);
                for (int w = 0; w < info.GroupLength[g]; w++)
                {
                    int start = (windowStart + w) * (info.IsShort ? 128 : 0) + info.SwbOffset[sfb];
                    uint seed = correlated ? noiseSeedAtBand[at] : noiseSeed;
                    double energy = 0;
                    for (int i = 0; i < width; i++)
                    {
                        seed = seed * 1664525u + 1013904223u;
                        float value = (int)seed;
                        noiseScratch[i] = value;
                        energy += (double)value * value;
                    }
                    if (!correlated) noiseSeed = seed;
                    else if (w + 1 < info.GroupLength[g]) noiseSeedAtBand[at] = seed;
                    float scale = energy > 0 ? (float)(target / Math.Sqrt(energy)) : 0f;
                    for (int i = 0; i < width; i++) stream.Spectrum[start + i] = noiseScratch[i] * scale;
                }
            }
            windowStart += info.GroupLength[g];
        }
    }

    private void ApplyMidSide(ChannelStream left, ChannelStream right, IcsInfo info, int maskPresent)
    {
        int windowStart = 0;
        for (int g = 0; g < info.NumWindowGroups; g++)
        {
            for (int sfb = 0; sfb < info.MaxSfb; sfb++)
            {
                int at = g * ChannelStream.BandStride + sfb;
                if (!(msUsed[at] || maskPresent == 2) || left.Codebook[at] >= Codebooks.NoiseBook || right.Codebook[at] >= Codebooks.NoiseBook) continue;
                Statistics.MidSideBands++;
                int width = info.SwbOffset[sfb + 1] - info.SwbOffset[sfb];
                for (int w = 0; w < info.GroupLength[g]; w++)
                {
                    int start = (windowStart + w) * (info.IsShort ? 128 : 0) + info.SwbOffset[sfb];
                    for (int i = start; i < start + width; i++)
                    {
                        float m = left.Spectrum[i], s = right.Spectrum[i];
                        left.Spectrum[i] = m + s;
                        right.Spectrum[i] = m - s;
                    }
                }
            }
            windowStart += info.GroupLength[g];
        }
    }

    private void ApplyIntensity(ChannelStream left, ChannelStream right, IcsInfo info, int maskPresent)
    {
        int windowStart = 0;
        for (int g = 0; g < info.NumWindowGroups; g++)
        {
            for (int sfb = 0; sfb < info.MaxSfb; sfb++)
            {
                int at = g * ChannelStream.BandStride + sfb;
                int book = right.Codebook[at];
                if (book < Codebooks.IntensityBook2) continue;
                Statistics.IntensityBands++;
                float sign = book == Codebooks.IntensityBook ? 1f : -1f;
                if (maskPresent == 1 && msUsed[at]) sign = -sign;
                float scale = sign * (float)Math.Pow(0.5, 0.25 * right.ScaleFactor[at]);
                int width = info.SwbOffset[sfb + 1] - info.SwbOffset[sfb];
                for (int w = 0; w < info.GroupLength[g]; w++)
                {
                    int start = (windowStart + w) * (info.IsShort ? 128 : 0) + info.SwbOffset[sfb];
                    for (int i = start; i < start + width; i++) right.Spectrum[i] = scale * left.Spectrum[i];
                }
            }
            windowStart += info.GroupLength[g];
        }
    }

    private void ApplyTns(ChannelStream stream)
    {
        IcsInfo info = stream.Info;
        int maxBands = info.IsShort ? TnsMaxBandsShort[samplingIndex] : TnsMaxBandsLong[samplingIndex];
        int maxOrder = info.IsShort ? 7 : objectType == 1 ? 20 : 12;
        Span<float> lpc = stackalloc float[33];
        Span<float> scratch = stackalloc float[33];
        Span<float> reflection = stackalloc float[32];
        for (int w = 0; w < info.NumWindows; w++)
        {
            int bottom = info.NumSwb;
            for (int f = 0; f < stream.TnsFilterCount[w]; f++)
            {
                int top = bottom;
                bottom = Math.Max(top - stream.TnsLength[w, f], 0);
                int order = Math.Min(stream.TnsOrder[w, f], maxOrder);
                if (order == 0) continue;
                int resolutionBits = stream.TnsResolution[w] + 3;
                int storedBits = resolutionBits - stream.TnsCompress[w, f];
                int signMask = 1 << (storedBits - 1);
                float quantUp = (float)(((1 << (resolutionBits - 1)) - 0.5) / (Math.PI / 2));
                float quantDown = (float)(((1 << (resolutionBits - 1)) + 0.5) / (Math.PI / 2));
                for (int i = 0; i < order; i++)
                {
                    int coefficient = stream.TnsCoefficient[w, f, i];
                    if ((coefficient & signMask) != 0) coefficient |= ~((signMask << 1) - 1);
                    reflection[i] = (float)Math.Sin(coefficient / (coefficient >= 0 ? quantUp : quantDown));
                }
                lpc[0] = 1;
                for (int m = 1; m <= order; m++)
                {
                    for (int i = 1; i < m; i++) scratch[i] = lpc[i] + reflection[m - 1] * lpc[m - i];
                    for (int i = 1; i < m; i++) lpc[i] = scratch[i];
                    lpc[m] = reflection[m - 1];
                }
                int start = info.SwbOffset[Math.Min(Math.Min(bottom, maxBands), info.MaxSfb)];
                int end = info.SwbOffset[Math.Min(Math.Min(top, maxBands), info.MaxSfb)];
                int size = end - start;
                if (size <= 0) continue;
                int increment = 1, index = start;
                if (stream.TnsDirection[w, f] != 0)
                {
                    increment = -1;
                    index = end - 1;
                }
                int windowBase = w * (info.IsShort ? 128 : 0);
                for (int n = 0; n < size; n++)
                {
                    int position = windowBase + index;
                    float value = stream.Spectrum[position];
                    int available = Math.Min(order, n);
                    for (int i = 1; i <= available; i++) value -= lpc[i] * stream.Spectrum[position - i * increment];
                    stream.Spectrum[position] = value;
                    index += increment;
                }
            }
        }
    }

    // ---- other elements ------------------------------------------------------------------------------------------

    private static void SkipDataStream(ref BitReader reader)
    {
        reader.Skip(4);
        bool align = reader.ReadFlag();
        int count = (int)reader.ReadBits(8);
        if (count == 255) count += (int)reader.ReadBits(8);
        if (align) reader.ByteAlign();
        reader.Skip(count * 8);
    }

    private static void SkipProgramConfig(ref BitReader reader)
    {
        reader.Skip(4 + 2 + 4);
        int front = (int)reader.ReadBits(4), side = (int)reader.ReadBits(4), back = (int)reader.ReadBits(4);
        int lfe = (int)reader.ReadBits(2), assoc = (int)reader.ReadBits(3), coupling = (int)reader.ReadBits(4);
        if (reader.ReadFlag()) reader.Skip(4);
        if (reader.ReadFlag()) reader.Skip(4);
        if (reader.ReadFlag()) reader.Skip(3);
        reader.Skip((front + side + back) * 5 + lfe * 4 + assoc * 4 + coupling * 5);
        reader.ByteAlign();
        int comment = (int)reader.ReadBits(8);
        reader.Skip(comment * 8);
    }

    private void ReadFill(ref BitReader reader)
    {
        int count = (int)reader.ReadBits(4);
        if (count == 15) count += (int)reader.ReadBits(8) - 1;
        if (count == 0) return;
        // extension_type is the first nibble; 13 and 14 carry SBR data (HE-AAC), the sign that this stream is not plain AAC-LC.
        uint type = reader.Peek(4);
        if (type is 13 or 14)
        {
            SbrSignaled = true;
            if (RejectSbr) throw new AacUnsupportedException("This stream uses HE-AAC (SBR), which is not supported yet.");
        }
        reader.Skip(count * 8);
    }

    private static float[] BuildGains()
    {
        float[] gains = new float[256];
        for (int i = 0; i < gains.Length; i++) gains[i] = (float)Math.Pow(2.0, 0.25 * (i - ScaleFactorOffset));
        return gains;
    }

    private static float[] BuildPow43()
    {
        float[] table = new float[8192 + 64];
        for (int i = 0; i < table.Length; i++) table[i] = (float)Math.Pow(i, 4.0 / 3.0);
        return table;
    }
}
