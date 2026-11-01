namespace Aibell.Audio.Aac;

/// <summary>
/// The parts of an MPEG-4 AudioSpecificConfig (ISO/IEC 14496-3, subclause 1.6.2.1) that the decoder needs. <see cref="Parse"/> throws
/// <see cref="NotSupportedException"/> for configurations it cannot play (HE-AAC, other object types, 960-sample frames, a program config element).
/// </summary>
internal sealed record AudioSpecificConfig(int ObjectType, int SamplingIndex, int SampleRate, int ChannelConfiguration)
{
    public static AudioSpecificConfig Parse(ReadOnlySpan<byte> data)
    {
        try
        {
            BitReader reader = new(data);
            int objectType = (int)reader.ReadBits(5);
            if (objectType == 31) objectType = 32 + (int)reader.ReadBits(6);
            int samplingIndex = (int)reader.ReadBits(4);
            if (samplingIndex == 15) throw new NotSupportedException("The AAC stream uses an explicit sampling rate.");
            int channels = (int)reader.ReadBits(4);
            if (objectType is 5 or 29) throw new NotSupportedException("HE-AAC (SBR) is not supported.");
            if (objectType is not (1 or 2)) throw new NotSupportedException($"AAC object type {objectType} is not supported.");
            if (samplingIndex > 11) throw new NotSupportedException("This AAC sampling rate is not supported.");
            if (channels is 0 or > 7) throw new NotSupportedException("This AAC channel configuration is not supported.");
            if (reader.ReadFlag()) throw new NotSupportedException("AAC frames of 960 samples are not supported.");
            if (reader.ReadFlag()) throw new NotSupportedException("Scalable AAC is not supported.");
            reader.ReadBit(); // extensionFlag
            // An explicit backward compatible extension follows the core configuration (SBR: 0x2b7, then the object type 5 and sbrPresentFlag; PS: 0x548).
            // Some encoders write the SBR extension with the flag cleared, which is plain AAC-LC.
            if (reader.BitsLeft >= 16)
            {
                uint sync = reader.ReadBits(11);
                if (sync == 0x548) throw new NotSupportedException("HE-AAC (SBR) is not supported.");
                if (sync == 0x2b7 && (reader.ReadBits(5) != 5 || reader.BitsLeft >= 1 && reader.ReadFlag())) throw new NotSupportedException("HE-AAC (SBR) is not supported.");
            }
            return new AudioSpecificConfig(objectType, samplingIndex, SamplingRates.FromIndex(samplingIndex), channels);
        }
        catch (AacException exception)
        {
            throw new InvalidDataException("The AAC configuration is damaged.", exception);
        }
    }

    /// <summary>Channels the configuration describes (configuration 7 is 7.1).</summary>
    public int Channels => ChannelConfiguration == 7 ? 8 : ChannelConfiguration;
}
