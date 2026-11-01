using System.Buffers.Binary;
using System.Text;

namespace Aibell.Audio.Midi;

/// <summary>
/// A SoundFont 2 bank (versions 2.00 to 2.04, 16 and 24 bit samples) read from the SoundFont Technical Specification 2.04: the RIFF structure, the nine
/// "hydra" sub-chunks, generators and modulators with their preset/instrument/global-zone precedence. The file is untrusted: every index, offset and size is
/// validated and anything structurally unsound raises <see cref="InvalidDataException"/>. SoundFont 3 (compressed samples) and ROM samples are not supported.
/// </summary>
public sealed class SoundFont
{
    /// <summary>Largest sample pool read into memory.</summary>
    public const long MaxSampleBytes = 1_500_000_000;

    private const int MaxChunkBytes = 64 * 1024 * 1024;
    private const int MaxZones = 500_000;
    private const int Gens = Gen.Count;

    private readonly short[] pcm;
    private readonly byte[]? low;
    private readonly Dictionary<int, Preset> presets = [];

    private SoundFont(string name, short[] pcm, byte[]? low)
    {
        Name = name;
        this.pcm = pcm;
        this.low = low;
    }

    /// <summary>The bank name (INAM), or empty.</summary>
    public string Name { get; }

    public int PresetCount => presets.Count;

    internal int SampleCount => pcm.Length;

    /// <summary>One sample point as a float in +/-1 (24 bit when the bank has the sm24 chunk).</summary>
    internal float At(int index)
    {
        if ((uint)index >= (uint)pcm.Length) return 0;
        return low is null ? pcm[index] * (1f / 32768) : (pcm[index] * 256 + low[index]) * (1f / 8388608);
    }

    /// <summary>Reads a bank from a file. Throws <see cref="InvalidDataException"/>, <see cref="NotSupportedException"/> or <see cref="IOException"/>.</summary>
    public static SoundFont Load(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Open(stream);
    }

    /// <summary>Reads a bank from a seekable stream.</summary>
    public static SoundFont Open(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!StreamLength.TryGet(stream, out long length)) throw new NotSupportedException("A SoundFont must be read from a stream that can seek.");
        var file = new Reader(stream, length);
        return Parse(file);
    }

    internal Preset? FindPreset(int bank, int program, bool percussion)
    {
        if (presets.TryGetValue(bank << 8 | program, out Preset? exact)) return exact;
        int home = percussion ? 128 : 0;
        if (presets.TryGetValue(home << 8 | program, out Preset? sameProgram)) return sameProgram;
        return presets.GetValueOrDefault(home << 8);
    }

    private static SoundFont Parse(Reader file)
    {
        byte[] header = file.Bytes(12);
        if (!header.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !header.AsSpan(8, 4).SequenceEqual("sfbk"u8)) throw new InvalidDataException("This is not a SoundFont file.");
        long riffEnd = Math.Min(8L + BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)), file.Length);

        string name = "";
        int major = 0, minor = 0;
        short[]? pcm = null;
        byte[]? sm24 = null;
        var hydra = new Dictionary<string, byte[]>();
        while (file.Position + 12 <= riffEnd)
        {
            (string id, long size) = file.ChunkHeader(riffEnd);
            long end = file.Position + size;
            if (id != "LIST")
            {
                file.SkipTo(end + (size & 1), riffEnd);
                continue;
            }
            if (size < 4) throw new InvalidDataException("A SoundFont LIST chunk is too small.");
            string form = Encoding.ASCII.GetString(file.Bytes(4));
            switch (form)
            {
                case "INFO":
                    while (file.Position + 8 <= end)
                    {
                        (string sub, long subSize) = file.ChunkHeader(end);
                        long subEnd = file.Position + subSize;
                        if (sub == "ifil")
                        {
                            if (subSize != 4) throw new InvalidDataException("The SoundFont version chunk is damaged.");
                            byte[] v = file.Bytes(4);
                            major = BinaryPrimitives.ReadUInt16LittleEndian(v);
                            minor = BinaryPrimitives.ReadUInt16LittleEndian(v.AsSpan(2));
                        }
                        else if (sub == "INAM" && subSize <= 256)
                        {
                            name = ZeroString(file.Bytes((int)subSize));
                        }
                        file.SkipTo(subEnd + (subSize & 1), end);
                    }
                    break;
                case "sdta":
                    while (file.Position + 8 <= end)
                    {
                        (string sub, long subSize) = file.ChunkHeader(end);
                        long subEnd = file.Position + subSize;
                        if (sub == "smpl")
                        {
                            if (subSize > MaxSampleBytes) throw new InvalidDataException("The SoundFont sample data is too large.");
                            pcm = new short[subSize / 2];
                            file.Fill(System.Runtime.InteropServices.MemoryMarshal.AsBytes(pcm.AsSpan()));
                            if (!BitConverter.IsLittleEndian) for (int i = 0; i < pcm.Length; i++) pcm[i] = BinaryPrimitives.ReverseEndianness(pcm[i]);
                        }
                        else if (sub == "sm24" && subSize <= MaxSampleBytes)
                        {
                            sm24 = file.Bytes((int)subSize);
                        }
                        file.SkipTo(subEnd + (subSize & 1), end);
                    }
                    break;
                case "pdta":
                    while (file.Position + 8 <= end)
                    {
                        (string sub, long subSize) = file.ChunkHeader(end);
                        long subEnd = file.Position + subSize;
                        if (subSize > MaxChunkBytes) throw new InvalidDataException("A SoundFont articulation chunk is too large.");
                        hydra[sub] = file.Bytes((int)subSize);
                        file.SkipTo(subEnd + (subSize & 1), end);
                    }
                    break;
            }
            file.SkipTo(end + (size & 1), riffEnd);
        }

        if (major == 0) throw new InvalidDataException("The SoundFont has no version chunk.");
        if (major != 2) throw new NotSupportedException($"SoundFont {major}.{minor:00} is not supported (only version 2).");
        pcm ??= [];
        // sm24 counts only for 2.04 or later and when it is exactly half the size of smpl (plus one byte when that is odd).
        if (sm24 is not null && (minor < 4 || sm24.Length != pcm.Length && !((pcm.Length & 1) == 1 && sm24.Length == pcm.Length + 1))) sm24 = null;

        var font = new SoundFont(name, pcm, sm24);
        font.BuildPresets(hydra);
        return font;
    }

    private static string ZeroString(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? bytes : bytes[..end]).Trim();
    }

    private void BuildPresets(Dictionary<string, byte[]> h)
    {
        byte[] Need(string id, int record, int minRecords)
        {
            if (!h.TryGetValue(id, out byte[]? data) || data.Length % record != 0 || data.Length / record < minRecords)
                throw new InvalidDataException($"The SoundFont '{id}' chunk is missing or damaged.");
            return data;
        }

        byte[] phdr = Need("phdr", 38, 2), pbag = Need("pbag", 4, 1), pmod = Need("pmod", 10, 1), pgen = Need("pgen", 4, 1);
        byte[] inst = Need("inst", 22, 2), ibag = Need("ibag", 4, 1), imod = Need("imod", 10, 1), igen = Need("igen", 4, 1), shdr = Need("shdr", 46, 2);

        // Samples
        int sampleTotal = shdr.Length / 46 - 1;
        var samples = new SampleHeader?[sampleTotal];
        for (int i = 0; i < sampleTotal; i++) samples[i] = ReadSample(shdr.AsSpan(i * 46, 46), pcm.Length);

        var instruments = ReadObjects(inst, 22, 20, ibag, imod, igen, instrument: true, count: inst.Length / 22 - 1, limit: sampleTotal);
        int presetTotal = phdr.Length / 38 - 1;
        var headers = new List<(string Name, int Program, int Bank, int Bag)>();
        for (int i = 0; i <= presetTotal; i++)
        {
            ReadOnlySpan<byte> r = phdr.AsSpan(i * 38, 38);
            headers.Add((ZeroString(r[..20]), BinaryPrimitives.ReadUInt16LittleEndian(r[20..]), BinaryPrimitives.ReadUInt16LittleEndian(r[22..]), BinaryPrimitives.ReadUInt16LittleEndian(r[24..])));
        }
        var presetObjects = ReadObjects(phdr, 38, 20, pbag, pmod, pgen, instrument: false, count: presetTotal, limit: instruments.Length);

        int zoneTotal = 0;
        for (int p = 0; p < presetTotal; p++)
        {
            (string pname, int program, int bank, _) = headers[p];
            if (program > 127 || bank > 128) continue;
            var zones = new List<SampleZone>();
            RawObject preset = presetObjects[p];
            foreach (RawZone pz in preset.Zones)
            {
                RawObject instrumentObject = instruments[pz.Index];
                foreach (RawZone iz in instrumentObject.Zones)
                {
                    SampleHeader? sample = samples[iz.Index];
                    if (sample is null) continue;
                    SampleZone? zone = Combine(preset.Global, pz, instrumentObject.Global, iz, sample.Value);
                    if (zone is null) continue;
                    if (++zoneTotal > MaxZones) throw new InvalidDataException("The SoundFont has too many zones.");
                    zones.Add(zone);
                }
            }
            // The first preset with a given bank and program is the active one.
            presets.TryAdd(bank << 8 | program, new Preset(pname, [.. zones]));
        }
    }

    private static SampleHeader? ReadSample(ReadOnlySpan<byte> r, int total)
    {
        long start = BinaryPrimitives.ReadUInt32LittleEndian(r[20..]);
        long end = BinaryPrimitives.ReadUInt32LittleEndian(r[24..]);
        long loopStart = BinaryPrimitives.ReadUInt32LittleEndian(r[28..]);
        long loopEnd = BinaryPrimitives.ReadUInt32LittleEndian(r[32..]);
        int rate = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(r[36..]), 192000u);
        int pitch = r[40];
        int correction = (sbyte)r[41];
        int type = BinaryPrimitives.ReadUInt16LittleEndian(r[44..]);
        // ROM samples cannot be played; samples that do not lie inside the sample pool are skipped, not fatal.
        if ((type & 0x8000) != 0 || start >= end || end > total) return null;
        if (rate <= 0) rate = 44100;
        if (pitch is > 127) pitch = 60;
        return new SampleHeader((int)start, (int)end, (int)loopStart, (int)loopEnd, rate, pitch, correction);
    }

    // ---- hydra: presets and instruments share one layout ----

    private sealed class RawZone
    {
        public readonly bool[] Has = new bool[Gens];
        public readonly int[] Amount = new int[Gens];
        public int KeyLo, KeyHi = 127, VelLo, VelHi = 127;
        public bool HasKey, HasVel;
        public int Index = -1;
        public List<Modulator> Mods = [];
    }

    private sealed class RawObject
    {
        public RawZone? Global;
        public readonly List<RawZone> Zones = [];
    }

    private static RawObject[] ReadObjects(byte[] headers, int recordSize, int nameSize, byte[] bags, byte[] mods, byte[] gens, bool instrument, int count, int limit)
    {
        int bagTotal = bags.Length / 4, modTotal = mods.Length / 10, genTotal = gens.Length / 4;
        int Bag(int i) => BinaryPrimitives.ReadUInt16LittleEndian(headers.AsSpan(i * recordSize + (instrument ? 20 : 24)));
        var objects = new RawObject[count];
        int previousBag = 0;
        for (int i = 0; i <= count; i++)
        {
            int bag = Bag(i);
            if (bag < previousBag || bag > bagTotal - 1) throw new InvalidDataException("A SoundFont bag index is out of order or out of range.");
            previousBag = bag;
        }
        if (Bag(count) != bagTotal - 1) throw new InvalidDataException("The SoundFont bag list does not match its terminal record.");
        // Bag records must increase monotonically in their generator and modulator indices, ending at the terminal counts.
        int g = 0, m = 0;
        for (int b = 0; b < bagTotal; b++)
        {
            int gi = BinaryPrimitives.ReadUInt16LittleEndian(bags.AsSpan(b * 4)), mi = BinaryPrimitives.ReadUInt16LittleEndian(bags.AsSpan(b * 4 + 2));
            if (gi < g || mi < m || gi > genTotal - 1 || mi > modTotal - 1) throw new InvalidDataException("A SoundFont generator or modulator index is out of order or out of range.");
            g = gi;
            m = mi;
        }
        if (g != genTotal - 1 || m != modTotal - 1) throw new InvalidDataException("The SoundFont generator or modulator list does not match its terminal record.");

        for (int i = 0; i < count; i++)
        {
            var obj = new RawObject();
            int first = Bag(i), last = Bag(i + 1);
            for (int b = first; b < last; b++)
            {
                RawZone? zone = ReadZone(bags, b, gens, mods, instrument, limit);
                if (zone is null) continue;
                if (zone.Index >= 0) obj.Zones.Add(zone);
                else if (b == first && obj.Global is null) obj.Global = zone;
            }
            objects[i] = obj;
        }
        return objects;
    }

    private static RawZone? ReadZone(byte[] bags, int bag, byte[] gens, byte[] mods, bool instrument, int limit)
    {
        int g0 = BinaryPrimitives.ReadUInt16LittleEndian(bags.AsSpan(bag * 4)), g1 = BinaryPrimitives.ReadUInt16LittleEndian(bags.AsSpan(bag * 4 + 4));
        int m0 = BinaryPrimitives.ReadUInt16LittleEndian(bags.AsSpan(bag * 4 + 2)), m1 = BinaryPrimitives.ReadUInt16LittleEndian(bags.AsSpan(bag * 4 + 6));
        int terminal = instrument ? Gen.SampleId : Gen.Instrument;
        var zone = new RawZone();
        bool sawKey = false;
        for (int i = g0; i < g1; i++)
        {
            int op = BinaryPrimitives.ReadUInt16LittleEndian(gens.AsSpan(i * 4));
            byte lo = gens[i * 4 + 2], hi = gens[i * 4 + 3];
            short value = BinaryPrimitives.ReadInt16LittleEndian(gens.AsSpan(i * 4 + 2));
            if (op == Gen.KeyRange)
            {
                if (i != g0) continue;
                zone.KeyLo = lo;
                zone.KeyHi = Math.Min((int)hi, 127);
                zone.HasKey = sawKey = true;
            }
            else if (op == Gen.VelRange)
            {
                if (i != g0 && !(sawKey && i == g0 + 1)) continue;
                zone.VelLo = lo;
                zone.VelHi = Math.Min((int)hi, 127);
                zone.HasVel = true;
            }
            else if (op == terminal)
            {
                int index = BinaryPrimitives.ReadUInt16LittleEndian(gens.AsSpan(i * 4 + 2));
                if (index >= limit) throw new InvalidDataException("A SoundFont zone points at a missing instrument or sample.");
                zone.Index = index;
                break; // generators after the terminal one are ignored
            }
            else if (op is Gen.Instrument or Gen.SampleId)
            {
                continue; // an index generator at the wrong level
            }
            else if (op < Gen.Count && op != 60 && op is not (14 or 18 or 19 or 20 or 42 or 49 or 55 or 59))
            {
                zone.Has[op] = true;
                zone.Amount[op] = value;
            }
        }
        for (int i = m0; i < m1; i++)
        {
            ReadOnlySpan<byte> r = mods.AsSpan(i * 10, 10);
            var mod = new Modulator(BinaryPrimitives.ReadUInt16LittleEndian(r), BinaryPrimitives.ReadUInt16LittleEndian(r[2..]),
                BinaryPrimitives.ReadInt16LittleEndian(r[4..]), BinaryPrimitives.ReadUInt16LittleEndian(r[6..]), BinaryPrimitives.ReadUInt16LittleEndian(r[8..]));
            if (!mod.IsUsable) continue;
            int duplicate = zone.Mods.FindIndex(x => x.IsIdenticalTo(mod));
            if (duplicate >= 0) zone.Mods[duplicate] = mod; // a second modulator with the same enumerators replaces the first
            else zone.Mods.Add(mod);
        }
        // A zone other than the first with no terminal generator is ignored; a global zone with nothing in it too.
        if (zone.Index < 0 && g1 == g0 && m1 == m0) return null;
        return zone;
    }

    // Section 9.4 and 8.5: instrument generators are absolute (default < global zone < local zone), preset generators add to them
    // (global zone < local zone), ranges intersect, and the generators that only exist at the instrument level are ignored in presets.
    private static SampleZone? Combine(RawZone? presetGlobal, RawZone presetZone, RawZone? instrumentGlobal, RawZone instrumentZone, SampleHeader sample)
    {
        int keyLo = Math.Max(Range(presetGlobal, presetZone, true, true), Range(instrumentGlobal, instrumentZone, true, true));
        int keyHi = Math.Min(Range(presetGlobal, presetZone, true, false), Range(instrumentGlobal, instrumentZone, true, false));
        int velLo = Math.Max(Range(presetGlobal, presetZone, false, true), Range(instrumentGlobal, instrumentZone, false, true));
        int velHi = Math.Min(Range(presetGlobal, presetZone, false, false), Range(instrumentGlobal, instrumentZone, false, false));
        if (keyLo > keyHi || velLo > velHi) return null;

        int[] value = Gen.Defaults();
        for (int g = 0; g < Gens; g++)
        {
            if (instrumentGlobal is not null && instrumentGlobal.Has[g]) value[g] = instrumentGlobal.Amount[g];
            if (instrumentZone.Has[g]) value[g] = instrumentZone.Amount[g];
            if (Gen.InstrumentOnly(g) || Gen.IsRangeOrIndex(g)) continue;
            if (presetZone.Has[g]) value[g] += presetZone.Amount[g];
            else if (presetGlobal is not null && presetGlobal.Has[g]) value[g] += presetGlobal.Amount[g];
        }

        // Modulators: the defaults, replaced by identical instrument ones (global, then local), then preset ones add to identical entries.
        var mods = new List<Modulator>(Modulator.Defaults());
        foreach (RawZone? z in new[] { instrumentGlobal, instrumentZone })
        {
            if (z is null) continue;
            foreach (Modulator m in z.Mods)
            {
                int at = mods.FindIndex(x => x.IsIdenticalTo(m));
                if (at >= 0) mods[at] = m;
                else mods.Add(m);
            }
        }
        var presetMods = new List<Modulator>(presetGlobal?.Mods ?? []);
        foreach (Modulator m in presetZone.Mods)
        {
            int at = presetMods.FindIndex(x => x.IsIdenticalTo(m));
            if (at >= 0) presetMods[at] = m;
            else presetMods.Add(m);
        }
        foreach (Modulator m in presetMods)
        {
            int at = mods.FindIndex(x => x.IsIdenticalTo(m));
            if (at >= 0) mods[at] = mods[at] with { Amount = mods[at].Amount + m.Amount };
            else mods.Add(m);
        }
        return new SampleZone(keyLo, keyHi, velLo, velHi, value, sample, [.. mods]);
    }

    // The zone's own range if it has one, else the global zone's, else everything.
    private static int Range(RawZone? global, RawZone local, bool key, bool lo)
    {
        RawZone? z = (key ? local.HasKey : local.HasVel) ? local : global is not null && (key ? global.HasKey : global.HasVel) ? global : null;
        if (z is null) return lo ? 0 : 127;
        return key ? lo ? z.KeyLo : z.KeyHi : lo ? z.VelLo : z.VelHi;
    }

    // ---- RIFF reading with bounds ----

    private sealed class Reader(Stream stream, long length)
    {
        public long Length { get; } = length;

        public long Position { get; private set; }

        public byte[] Bytes(int count)
        {
            if (count < 0 || Position + count > Length) throw new InvalidDataException("The SoundFont is truncated.");
            var buffer = new byte[count];
            Fill(buffer);
            return buffer;
        }

        public void Fill(Span<byte> destination)
        {
            if (Position + destination.Length > Length) throw new InvalidDataException("The SoundFont is truncated.");
            stream.ReadExactly(destination);
            Position += destination.Length;
        }

        public (string Id, long Size) ChunkHeader(long limit)
        {
            if (Position + 8 > limit) throw new InvalidDataException("A SoundFont chunk header is truncated.");
            byte[] h = Bytes(8);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(4));
            if (Position + size > Math.Min(limit, Length)) throw new InvalidDataException("A SoundFont chunk is larger than its container.");
            return (Encoding.ASCII.GetString(h, 0, 4), size);
        }

        public void SkipTo(long target, long limit)
        {
            target = Math.Min(target, limit);
            if (target < Position) throw new InvalidDataException("A SoundFont chunk overlaps another.");
            stream.Seek(target, SeekOrigin.Begin);
            Position = target;
        }
    }

    // ---- model ----

    internal readonly record struct SampleHeader(int Start, int End, int StartLoop, int EndLoop, int SampleRate, int OriginalPitch, int PitchCorrection);

    internal sealed record SampleZone(int KeyLo, int KeyHi, int VelLo, int VelHi, int[] Gen, SampleHeader Sample, Modulator[] Modulators)
    {
        public int ExclusiveClass => Gen[Midi.Gen.ExclusiveClass];
    }

    internal sealed record Preset(string Name, SampleZone[] Zones);
}
