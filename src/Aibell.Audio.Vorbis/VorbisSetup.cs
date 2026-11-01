using System.Buffers.Binary;

namespace Aibell.Audio.Vorbis;

/// <summary>The identification header (section 4.2.2).</summary>
internal sealed record VorbisIdentification(int Channels, int SampleRate, int BlockSize0, int BlockSize1)
{
    public static bool IsIdentification(ReadOnlySpan<byte> packet) => packet.Length >= 7 && packet[0] == 1 && packet.Slice(1, 6).SequenceEqual("vorbis"u8);

    public static VorbisIdentification Parse(ReadOnlySpan<byte> packet)
    {
        if (!IsIdentification(packet) || packet.Length < 30) throw new InvalidDataException("This is not a Vorbis identification header.");
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(packet[7..]);
        int channels = packet[11];
        uint rate = BinaryPrimitives.ReadUInt32LittleEndian(packet[12..]);
        int blocks = packet[28];
        int bs0 = 1 << (blocks & 15), bs1 = 1 << (blocks >> 4);
        if (version != 0 || channels == 0 || rate == 0 || rate > int.MaxValue || (packet[29] & 1) == 0) throw new InvalidDataException("The Vorbis identification header is invalid.");
        if ((blocks & 15) < 6 || (blocks & 15) > 13 || (blocks >> 4) < 6 || (blocks >> 4) > 13 || bs0 > bs1) throw new InvalidDataException("The Vorbis block sizes are invalid.");
        return new VorbisIdentification(channels, (int)rate, bs0, bs1);
    }
}

internal sealed record VorbisMapping(int Submaps, int[] Magnitude, int[] Angle, int[] Mux, int[] SubmapFloor, int[] SubmapResidue);

internal sealed record VorbisMode(bool LongBlock, int Mapping);

/// <summary>The setup header (section 4.2.4): codebooks, floors, residues, mappings and modes.</summary>
internal sealed class VorbisSetup
{
    public VorbisCodebook[] Codebooks { get; private init; } = [];

    public VorbisFloor1[] Floors { get; private init; } = [];

    public VorbisResidue[] Residues { get; private init; } = [];

    public VorbisMapping[] Mappings { get; private init; } = [];

    public VorbisMode[] Modes { get; private init; } = [];

    public static bool IsSetup(ReadOnlySpan<byte> packet) => packet.Length >= 7 && packet[0] == 5 && packet.Slice(1, 6).SequenceEqual("vorbis"u8);

    public static VorbisSetup Parse(ReadOnlySpan<byte> packet, int channels)
    {
        if (!IsSetup(packet)) throw new InvalidDataException("This is not a Vorbis setup header.");
        VorbisBitReader reader = new(packet[7..]);
        VorbisCodebook[] codebooks = new VorbisCodebook[(int)reader.Read(8) + 1];
        for (int i = 0; i < codebooks.Length; i++) codebooks[i] = VorbisCodebook.Read(ref reader);

        int timeCount = (int)reader.Read(6) + 1;
        for (int i = 0; i < timeCount; i++)
        {
            if (reader.Read(16) != 0) throw new InvalidDataException("A Vorbis time transform is not zero.");
        }

        VorbisFloor1[] floors = new VorbisFloor1[(int)reader.Read(6) + 1];
        for (int i = 0; i < floors.Length; i++)
        {
            uint floorType = reader.Read(16);
            if (floorType == 0) throw new NotSupportedException("This Vorbis stream uses floor type 0, which is not supported.");
            if (floorType != 1) throw new InvalidDataException("A Vorbis floor type is invalid.");
            floors[i] = VorbisFloor1.Read(ref reader, codebooks.Length);
        }

        VorbisResidue[] residues = new VorbisResidue[(int)reader.Read(6) + 1];
        for (int i = 0; i < residues.Length; i++)
        {
            uint residueType = reader.Read(16);
            if (residueType > 2) throw new InvalidDataException("A Vorbis residue type is invalid.");
            residues[i] = VorbisResidue.Read(ref reader, (int)residueType, codebooks);
        }

        VorbisMapping[] mappings = new VorbisMapping[(int)reader.Read(6) + 1];
        int channelBits = VorbisCodebook.Ilog(channels - 1);
        for (int i = 0; i < mappings.Length; i++)
        {
            if (reader.Read(16) != 0) throw new InvalidDataException("A Vorbis mapping type is invalid.");
            int submaps = reader.Read(1) == 1 ? (int)reader.Read(4) + 1 : 1;
            int steps = 0;
            int[] magnitude = [], angle = [];
            if (reader.Read(1) == 1)
            {
                steps = (int)reader.Read(8) + 1;
                magnitude = new int[steps];
                angle = new int[steps];
                for (int j = 0; j < steps; j++)
                {
                    magnitude[j] = (int)reader.Read(channelBits);
                    angle[j] = (int)reader.Read(channelBits);
                    if (magnitude[j] == angle[j] || magnitude[j] >= channels || angle[j] >= channels) throw new InvalidDataException("A Vorbis channel coupling is invalid.");
                }
            }
            if (reader.Read(2) != 0) throw new InvalidDataException("A Vorbis mapping uses a reserved field.");
            int[] mux = new int[channels];
            if (submaps > 1)
            {
                for (int j = 0; j < channels; j++)
                {
                    mux[j] = (int)reader.Read(4);
                    if (mux[j] >= submaps) throw new InvalidDataException("A Vorbis channel refers to a missing submap.");
                }
            }
            int[] submapFloor = new int[submaps], submapResidue = new int[submaps];
            for (int j = 0; j < submaps; j++)
            {
                reader.Read(8);
                submapFloor[j] = (int)reader.Read(8);
                submapResidue[j] = (int)reader.Read(8);
                if (submapFloor[j] >= floors.Length || submapResidue[j] >= residues.Length) throw new InvalidDataException("A Vorbis submap refers to a missing floor or residue.");
            }
            mappings[i] = new VorbisMapping(submaps, magnitude, angle, mux, submapFloor, submapResidue);
        }

        VorbisMode[] modes = new VorbisMode[(int)reader.Read(6) + 1];
        for (int i = 0; i < modes.Length; i++)
        {
            bool longBlock = reader.Read(1) == 1;
            if (reader.Read(16) != 0 || reader.Read(16) != 0) throw new InvalidDataException("A Vorbis mode uses an unknown window or transform type.");
            int mapping = (int)reader.Read(8);
            if (mapping >= mappings.Length) throw new InvalidDataException("A Vorbis mode refers to a missing mapping.");
            modes[i] = new VorbisMode(longBlock, mapping);
        }
        if (reader.Read(1) == 0 || reader.EndOfPacket) throw new InvalidDataException("The Vorbis setup header is damaged.");
        return new VorbisSetup { Codebooks = codebooks, Floors = floors, Residues = residues, Mappings = mappings, Modes = modes };
    }
}
