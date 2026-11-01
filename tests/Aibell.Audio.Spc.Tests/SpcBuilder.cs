using System.Text;

namespace Aibell.Audio.Spc.Tests;

/// <summary>Builds synthetic SPC files: a hand-assembled program, a BRR sample and DSP registers. No game data is involved.</summary>
internal sealed class SpcBuilder
{
    public const int Code = 0x0200;

    private readonly byte[] ram = new byte[0x10000];
    private readonly byte[] dsp = new byte[128];
    private byte[]? xid6;

    public int Pc { get; set; } = Code;

    public byte A { get; set; }

    public byte X { get; set; }

    public byte Y { get; set; }

    public byte Psw { get; set; }

    public byte Sp { get; set; } = 0xEF;

    public string Title { get; set; } = "";

    public string Game { get; set; } = "";

    public string Artist { get; set; } = "";

    public string Seconds { get; set; } = "";

    public string FadeMilliseconds { get; set; } = "";

    public bool HasTag { get; set; } = true;

    /// <summary>Overrides the length fields with binary values (the other tag variant).</summary>
    public (int Seconds, int FadeMilliseconds)? BinaryLengths { get; set; }

    public SpcBuilder Program(params byte[] code) => ProgramAt(Code, code);

    public SpcBuilder ProgramAt(int address, params byte[] code)
    {
        code.CopyTo(ram, address);
        return this;
    }

    public SpcBuilder Ram(int address, params byte[] data)
    {
        data.CopyTo(ram, address);
        return this;
    }

    public SpcBuilder Dsp(int register, params byte[] values)
    {
        values.CopyTo(dsp, register);
        return this;
    }

    public SpcBuilder Xid6(params (byte Id, byte Type, byte[] Data)[] items)
    {
        using var body = new MemoryStream();
        foreach ((byte id, byte type, byte[] data) in items)
        {
            body.WriteByte(id);
            body.WriteByte(type);
            if (type == 0)
            {
                body.Write(data);
                continue;
            }
            body.Write(BitConverter.GetBytes((ushort)data.Length));
            body.Write(data);
            while (body.Length % 4 != 0) body.WriteByte(0);
        }
        using var chunk = new MemoryStream();
        chunk.Write("xid6"u8);
        chunk.Write(BitConverter.GetBytes((int)body.Length));
        chunk.Write(body.ToArray());
        xid6 = chunk.ToArray();
        return this;
    }

    public static (byte, byte, byte[]) Text(byte id, string text) => (id, 1, [.. Encoding.UTF8.GetBytes(text), (byte)0]);

    public static (byte, byte, byte[]) Integer(byte id, int value) => (id, 4, BitConverter.GetBytes(value));

    public static (byte, byte, byte[]) Small(byte id, int value) => (id, 0, BitConverter.GetBytes((ushort)value));

    public byte[] Build()
    {
        var file = new byte[0x10200 + (xid6?.Length ?? 0)];
        "SNES-SPC700 Sound File Data v0.30"u8.CopyTo(file);
        file[0x21] = file[0x22] = 26;
        file[0x23] = (byte)(HasTag ? 26 : 27);
        file[0x24] = 30;
        file[0x25] = (byte)Pc;
        file[0x26] = (byte)(Pc >> 8);
        file[0x27] = A;
        file[0x28] = X;
        file[0x29] = Y;
        file[0x2A] = Psw;
        file[0x2B] = Sp;
        Put(file, 0x2E, 32, Title);
        Put(file, 0x4E, 32, Game);
        Put(file, 0xB1, 32, Artist);
        if (BinaryLengths is { } binary)
        {
            // Binary files hold the artist one byte earlier and integers in the length fields.
            Array.Clear(file, 0xB1, 32);
            Put(file, 0xB0, 32, Artist);
            file[0xA9] = (byte)binary.Seconds;
            file[0xAA] = (byte)(binary.Seconds >> 8);
            file[0xAB] = (byte)(binary.Seconds >> 16);
            BitConverter.GetBytes(binary.FadeMilliseconds).CopyTo(file, 0xAC);
        }
        else
        {
            Put(file, 0xA9, 3, Seconds);
            Put(file, 0xAC, 5, FadeMilliseconds);
        }
        ram.CopyTo(file, 0x100);
        dsp.CopyTo(file, 0x10100);
        xid6?.CopyTo(file, 0x10200);
        return file;
    }

    private static void Put(byte[] file, int offset, int length, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        Array.Copy(bytes, 0, file, offset, Math.Min(length, bytes.Length));
    }

    /// <summary>One BRR block of 16 samples: header (shift, filter, end/loop flags) then the nibbles.</summary>
    public static byte[] Block(int shift, int filter, int flags, params int[] nibbles)
    {
        var block = new byte[9];
        block[0] = (byte)(shift << 4 | filter << 2 | flags);
        for (int i = 0; i < 16; i++) block[1 + i / 2] |= (byte)((nibbles[i] & 0xF) << (i % 2 == 0 ? 4 : 0));
        return block;
    }

    /// <summary>A square wave sample of eight high and eight low values that loops on itself.</summary>
    public static byte[] SquareLoop(int shift = 12) =>
        Block(shift, 0, 3, 7, 7, 7, 7, 7, 7, 7, 7, 9, 9, 9, 9, 9, 9, 9, 9);

    /// <summary>
    /// A song that plays voice 0 forever: BRR sample at 0x1000, directory at 0x0F00, full volume, direct gain, then KON. The extra
    /// registers (pitch, gain, ...) can be changed with <see cref="Dsp"/> before building.
    /// </summary>
    public static SpcBuilder SquareVoice(int pitch = 0x1000)
    {
        var builder = new SpcBuilder();
        builder.Ram(0x1000, SquareLoop());
        builder.Ram(0x0F00, 0x00, 0x10, 0x00, 0x10);
        builder.Dsp(0x00, 0x7F, 0x7F, (byte)pitch, (byte)(pitch >> 8), 0x00, 0x00, 0x00, 0x7F);
        builder.Dsp(0x0C, 0x7F);
        builder.Dsp(0x1C, 0x7F);
        builder.Dsp(0x5D, 0x0F);
        builder.Dsp(0x6C, 0x20); // echo writes off, unmuted
        // MOV $F2,#$4C / MOV $F3,#$01 / BRA $
        builder.Program(0x8F, 0x4C, 0xF2, 0x8F, 0x01, 0xF3, 0x2F, 0xFE);
        return builder;
    }
}
