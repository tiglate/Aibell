using System.Text;

namespace Aibell.Audio.Midi.Tests;

/// <summary>Writes small SoundFont 2 banks for the tests, straight from the structures of the specification.</summary>
internal sealed class SoundFontBuilder
{
    public const int Instrument = 41, KeyRange = 43, VelRange = 44, SampleId = 53;

    private readonly List<short> pcm = [];
    private readonly List<byte> low = [];
    private readonly List<(string Name, int Start, int End, int LoopStart, int LoopEnd, int Rate, int Pitch, int Correction, int Type)> samples = [];
    private readonly List<(string Name, List<ZoneSpec> Zones)> instruments = [];
    private readonly List<(string Name, int Program, int Bank, List<ZoneSpec> Zones)> presets = [];

    public int Major { get; init; } = 2;

    public int Minor { get; init; } = 4;

    public bool With24Bit { get; init; }

    public sealed record ZoneSpec(List<(int Op, int Amount)> Gens, int Index = -1, List<(int Src, int Dest, int Amount, int AmtSrc, int Trans)>? Mods = null)
    {
        public static ZoneSpec Of(int index, params (int Op, int Amount)[] gens) => new([.. gens], index);

        public static ZoneSpec Global(params (int Op, int Amount)[] gens) => new([.. gens]);
    }

    public static (int Op, int Amount) Range(int op, int lo, int hi) => (op, lo | hi << 8);

    /// <summary>A looped sine of <paramref name="period"/> samples per cycle; the loop covers whole cycles, followed by the 46 zero points the specification requires.</summary>
    public int AddSine(int period, int rate = 44100, int pitch = 60, int cycles = 60, double level = .8, int loopFromCycle = 1)
    {
        int start = pcm.Count;
        int length = period * cycles;
        for (int i = 0; i < length + 46; i++)
        {
            short v = i < length ? (short)(Math.Sin(2 * Math.PI * i / period) * 32767 * level) : (short)0;
            pcm.Add(v);
            low.Add((byte)(With24Bit ? 0x40 : 0));
        }
        // Eight points of padding are required around the loop; the sine repeats exactly, so the loop points sit on whole cycles.
        samples.Add(($"sine{period}", start, start + length, start + period * loopFromCycle, start + length - period, rate, pitch, 0, 1));
        return samples.Count - 1;
    }

    public int AddRawSample(short[] data, int rate, int pitch, int loopStart, int loopEnd, int type = 1)
    {
        int start = pcm.Count;
        foreach (short s in data) { pcm.Add(s); low.Add(0); }
        for (int i = 0; i < 46; i++) { pcm.Add(0); low.Add(0); }
        samples.Add(("raw", start, start + data.Length, start + loopStart, start + loopEnd, rate, pitch, 0, type));
        return samples.Count - 1;
    }

    public int AddInstrument(string name, params ZoneSpec[] zones)
    {
        instruments.Add((name, [.. zones]));
        return instruments.Count - 1;
    }

    public void AddPreset(string name, int program, int bank, params ZoneSpec[] zones) => presets.Add((name, program, bank, [.. zones]));

    public byte[] ToArray() => ToArray(null);

    /// <summary>Builds the file; <paramref name="damage"/> may edit the hydra chunks by name before they are written.</summary>
    public byte[] ToArray(Action<Dictionary<string, byte[]>>? damage)
    {
        var info = Concat(Chunk("ifil", [(byte)Major, 0, (byte)Minor, 0]), Chunk("INAM", ZString("Test bank")));
        var smpl = new byte[pcm.Count * 2];
        for (int i = 0; i < pcm.Count; i++) { smpl[i * 2] = (byte)pcm[i]; smpl[i * 2 + 1] = (byte)(pcm[i] >> 8); }
        var sdta = Chunk("smpl", smpl);
        if (With24Bit) sdta = Concat(sdta, Chunk("sm24", [.. low]));

        var hydra = new Dictionary<string, byte[]>();
        // Presets
        var phdr = new List<byte>();
        var pbag = new List<byte>();
        var pmod = new List<byte>();
        var pgen = new List<byte>();
        foreach ((string name, int program, int bank, List<ZoneSpec> zones) in presets)
        {
            phdr.AddRange(Fixed(name, 20));
            phdr.AddRange(W(program)); phdr.AddRange(W(bank)); phdr.AddRange(W(pbag.Count / 4));
            phdr.AddRange(new byte[12]);
            foreach (ZoneSpec z in zones) WriteZone(z, Instrument, pbag, pgen, pmod);
        }
        phdr.AddRange(Fixed("EOP", 20)); phdr.AddRange(W(0)); phdr.AddRange(W(0)); phdr.AddRange(W(pbag.Count / 4)); phdr.AddRange(new byte[12]);
        pbag.AddRange(W(pgen.Count / 4)); pbag.AddRange(W(pmod.Count / 10));
        pgen.AddRange(new byte[4]);
        pmod.AddRange(new byte[10]);

        var inst = new List<byte>();
        var ibag = new List<byte>();
        var imod = new List<byte>();
        var igen = new List<byte>();
        foreach ((string name, List<ZoneSpec> zones) in instruments)
        {
            inst.AddRange(Fixed(name, 20)); inst.AddRange(W(ibag.Count / 4));
            foreach (ZoneSpec z in zones) WriteZone(z, SampleId, ibag, igen, imod);
        }
        inst.AddRange(Fixed("EOI", 20)); inst.AddRange(W(ibag.Count / 4));
        ibag.AddRange(W(igen.Count / 4)); ibag.AddRange(W(imod.Count / 10));
        igen.AddRange(new byte[4]);
        imod.AddRange(new byte[10]);

        var shdr = new List<byte>();
        foreach ((string name, int start, int end, int ls, int le, int rate, int pitch, int correction, int type) in samples)
        {
            shdr.AddRange(Fixed(name, 20));
            foreach (int v in new[] { start, end, ls, le, rate }) shdr.AddRange(D(v));
            shdr.Add((byte)pitch); shdr.Add((byte)(sbyte)correction); shdr.AddRange(W(0)); shdr.AddRange(W(type));
        }
        shdr.AddRange(Fixed("EOS", 20)); shdr.AddRange(new byte[26]);

        hydra["phdr"] = [.. phdr]; hydra["pbag"] = [.. pbag]; hydra["pmod"] = [.. pmod]; hydra["pgen"] = [.. pgen];
        hydra["inst"] = [.. inst]; hydra["ibag"] = [.. ibag]; hydra["imod"] = [.. imod]; hydra["igen"] = [.. igen]; hydra["shdr"] = [.. shdr];
        damage?.Invoke(hydra);
        byte[] pdta = [];
        foreach (string id in new[] { "phdr", "pbag", "pmod", "pgen", "inst", "ibag", "imod", "igen", "shdr" }) if (hydra.TryGetValue(id, out byte[]? data)) pdta = Concat(pdta, Chunk(id, data));

        byte[] body = Concat("sfbk"u8.ToArray(), List("INFO", info), List("sdta", sdta), List("pdta", pdta));
        return Concat("RIFF"u8.ToArray(), D(body.Length), body);
    }

    private static void WriteZone(ZoneSpec zone, int terminal, List<byte> bag, List<byte> gen, List<byte> mod)
    {
        bag.AddRange(W(gen.Count / 4)); bag.AddRange(W(mod.Count / 10));
        foreach ((int op, int amount) in zone.Gens) { gen.AddRange(W(op)); gen.AddRange(BitConverter.GetBytes((short)amount)); }
        if (zone.Index >= 0) { gen.AddRange(W(terminal)); gen.AddRange(W(zone.Index)); }
        foreach ((int src, int dest, int amount, int amtSrc, int trans) in zone.Mods ?? [])
        {
            mod.AddRange(W(src)); mod.AddRange(W(dest)); mod.AddRange(BitConverter.GetBytes((short)amount)); mod.AddRange(W(amtSrc)); mod.AddRange(W(trans));
        }
    }

    private static byte[] W(int v) => [(byte)v, (byte)(v >> 8)];

    private static byte[] D(int v) => [(byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24)];

    private static byte[] Fixed(string s, int n) { var b = new byte[n]; Encoding.ASCII.GetBytes(s, b); return b; }

    private static byte[] ZString(string s) => [.. Encoding.ASCII.GetBytes(s), 0, 0];

    private static byte[] Chunk(string id, byte[] data) => Concat(Encoding.ASCII.GetBytes(id), D(data.Length), data, data.Length % 2 == 1 ? [(byte)0] : []);

    private static byte[] List(string form, byte[] content) => Concat(Encoding.ASCII.GetBytes("LIST"), D(content.Length + 4), Encoding.ASCII.GetBytes(form), content);

    private static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(p => p)];
}
