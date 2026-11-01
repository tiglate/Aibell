namespace Aibell.Audio.Spc;

/// <summary>
/// The SNES S-DSP: eight BRR voices with Gaussian interpolation, ADSR/GAIN envelopes, pitch modulation and noise, then the echo unit
/// (ring buffer in sound RAM with an 8-tap FIR filter). <see cref="Step"/> produces one stereo frame at 32 kHz. Behaviour follows the
/// published hardware documentation at the level of whole samples: register polling is not modelled per clock cycle.
/// </summary>
internal sealed class SpcDsp
{
    public const int SampleRate = 32000;

    private enum EnvelopeMode : byte { Attack, Decay, Sustain, Release }

    // Samples between two envelope steps for each 5-bit rate; 0 means never.
    private static readonly int[] RatePeriods =
    [
        0, 2048, 1536, 1280, 1024, 768, 640, 512, 384, 320, 256, 192, 160, 128, 96, 80,
        64, 48, 40, 32, 24, 20, 16, 12, 10, 8, 6, 5, 4, 3, 2, 1
    ];

    // Every period divides this, so the counter wraps without disturbing any rate.
    private const int RateCounterWrap = 30720;

    private struct Voice
    {
        public EnvelopeMode Mode;
        public int Envelope;
        public int KeyOnDelay;
        public int Address;      // start of the BRR block being played
        public int Index;        // next nibble to decode in that block (0-16)
        public int Counter;      // 12-bit fraction of the pitch counter
        public int Older, Old;   // the two previous decoded samples (BRR filter state)
        public int H0, H1, H2, H3; // decoded samples for interpolation, newest in H3
        public bool BlockEnds;
        public int BlockLoopFlag;
        public int LastOutput;   // post-envelope 15-bit sample, used by pitch modulation
        public bool Playing;
    }

    private readonly byte[] ram;
    private readonly byte[] registers = new byte[128];
    private readonly Voice[] voices = new Voice[8];
    private readonly int[] firLeft = new int[8];
    private readonly int[] firRight = new int[8];
    private int firPosition;
    private int rateCounter;
    private int noise = 0x4000;
    private int keyOn;
    private int echoIndex;
    private int echoLimit;

    public SpcDsp(byte[] ram)
    {
        this.ram = ram;
        Reset();
    }

    public int Address { get; set; }

    public void Reset()
    {
        Array.Clear(registers);
        Array.Clear(voices);
        Array.Clear(firLeft);
        Array.Clear(firRight);
        for (int i = 0; i < voices.Length; i++) voices[i].Mode = EnvelopeMode.Release;
        registers[0x6C] = 0xE0;
        noise = 0x4000;
        keyOn = 0;
        rateCounter = 0;
        echoIndex = 0;
        echoLimit = 0;
        firPosition = 0;
    }

    /// <summary>Loads the register image saved in a file. Voices start silent: the saved state has no BRR positions.</summary>
    public void Load(ReadOnlySpan<byte> image)
    {
        Reset();
        image[..128].CopyTo(registers);
        registers[0x4C] = 0;
        registers[0x7C] = 0;
    }

    public byte Read(int register) => registers[register & 0x7F];

    public void Write(int register, byte value)
    {
        if (register >= 0x80) return;
        switch (register)
        {
            case 0x4C: keyOn |= value; break;
            case 0x7C: value = 0; break;
        }
        registers[register] = value;
    }

    /// <summary>Renders one frame into <paramref name="left"/> and <paramref name="right"/> (16-bit scale).</summary>
    public void Step(out int left, out int right)
    {
        rateCounter = rateCounter + 1 == RateCounterWrap ? 0 : rateCounter + 1;
        int flags = registers[0x6C];
        int noiseRate = flags & 0x1F;
        if (RateEvent(noiseRate)) noise = (noise >> 1 & 0x3FFF) | ((noise ^ noise >> 1) & 1) << 14;
        int noiseSample = (short)(noise << 1) >> 1;

        int mainL = 0, mainR = 0, echoL = 0, echoR = 0;
        int previous = 0;
        int pitchMod = registers[0x2D], noiseOn = registers[0x3D], echoOn = registers[0x4D], keyOff = registers[0x5C];
        for (int v = 0; v < 8; v++)
        {
            int bank = v << 4;
            ref Voice voice = ref voices[v];

            if ((flags & 0x80) != 0)
            {
                voice.Mode = EnvelopeMode.Release;
                voice.Envelope = 0;
            }
            else if ((keyOff >> v & 1) != 0) voice.Mode = EnvelopeMode.Release;
            if ((keyOn >> v & 1) != 0) KeyOn(ref voice, bank);

            int output = 0;
            if (voice.KeyOnDelay > 0)
            {
                voice.KeyOnDelay--;
                voice.LastOutput = 0;
            }
            else
            {
                int pitch = registers[bank + 2] | (registers[bank + 3] & 0x3F) << 8;
                if (v > 0 && (pitchMod >> v & 1) != 0) pitch = Math.Min(0x3FFF, pitch * ((previous >> 4) + 0x400) >> 10);

                int sample;
                if ((noiseOn >> v & 1) != 0) sample = noiseSample;
                else sample = Interpolate(ref voice);
                output = sample * voice.Envelope >> 11;
                voice.LastOutput = output;

                UpdateEnvelope(ref voice, bank);
                if (voice.Playing) Advance(ref voice, pitch, bank, v);
            }
            previous = voice.LastOutput;
            registers[bank + 8] = (byte)(voice.Envelope >> 4);
            registers[bank + 9] = (byte)(output >> 8);

            int volL = (sbyte)registers[bank], volR = (sbyte)registers[bank + 1];
            int l = output * volL >> 6, r = output * volR >> 6;
            mainL = Clamp16(mainL + l);
            mainR = Clamp16(mainR + r);
            if ((echoOn >> v & 1) != 0)
            {
                echoL = Clamp16(echoL + l);
                echoR = Clamp16(echoR + r);
            }
        }
        keyOn = 0;

        EchoStep(flags, echoL, echoR, out int firL, out int firR);
        mainL = Clamp16(Clamp16(mainL * (sbyte)registers[0x0C] >> 7) + Clamp16(firL * (sbyte)registers[0x2C] >> 7));
        mainR = Clamp16(Clamp16(mainR * (sbyte)registers[0x1C] >> 7) + Clamp16(firR * (sbyte)registers[0x3C] >> 7));
        if ((flags & 0x40) != 0) mainL = mainR = 0;
        left = mainL;
        right = mainR;
    }

    private void EchoStep(int flags, int echoL, int echoR, out int outL, out int outR)
    {
        int address = ((registers[0x6D] << 8) + echoIndex * 4) & 0xFFFF;
        int inL = (short)(ram[address] | ram[(address + 1) & 0xFFFF] << 8) >> 1;
        int inR = (short)(ram[(address + 2) & 0xFFFF] | ram[(address + 3) & 0xFFFF] << 8) >> 1;
        firLeft[firPosition] = inL;
        firRight[firPosition] = inR;
        outL = Fir(firLeft);
        outR = Fir(firRight);
        firPosition = (firPosition + 1) & 7;

        if ((flags & 0x20) == 0)
        {
            int feedback = (sbyte)registers[0x0D];
            int l = Clamp16(echoL + Clamp16(outL * feedback >> 7)) & ~1;
            int r = Clamp16(echoR + Clamp16(outR * feedback >> 7)) & ~1;
            ram[address] = (byte)l;
            ram[(address + 1) & 0xFFFF] = (byte)(l >> 8);
            ram[(address + 2) & 0xFFFF] = (byte)r;
            ram[(address + 3) & 0xFFFF] = (byte)(r >> 8);
        }
        if (echoIndex == 0) echoLimit = (registers[0x7D] & 0x0F) << 9;
        echoIndex++;
        if (echoIndex >= echoLimit) echoIndex = 0;
    }

    // buffer[position] holds the newest sample once firPosition has been stored at and not yet advanced.
    private int Fir(int[] buffer)
    {
        int sum = 0;
        for (int tap = 0; tap < 7; tap++)
        {
            int sample = buffer[(firPosition - 7 + tap) & 7];
            sum += sample * (sbyte)registers[tap * 16 + 0x0F] >> 6;
        }
        sum = (short)sum;
        sum += buffer[firPosition] * (sbyte)registers[7 * 16 + 0x0F] >> 6;
        return Clamp16(sum);
    }

    private void KeyOn(ref Voice voice, int bank)
    {
        int entry = (registers[0x5D] << 8) + (registers[bank + 4] << 2);
        voice.Address = ram[entry & 0xFFFF] | ram[entry + 1 & 0xFFFF] << 8;
        voice.Index = 16;
        voice.Counter = 0;
        voice.Older = voice.Old = 0;
        voice.H0 = voice.H1 = voice.H2 = voice.H3 = 0;
        voice.Envelope = 0;
        voice.Mode = EnvelopeMode.Attack;
        voice.KeyOnDelay = 5;
        voice.Playing = true;
        voice.BlockEnds = false;
        voice.BlockLoopFlag = 0;
        registers[0x7C] &= (byte)~(1 << (bank >> 4));
        StartBlock(ref voice, bank >> 4);
    }

    // Reads the header of the block at voice.Address; the end flag shows up in ENDX as soon as the block starts.
    private void StartBlock(ref Voice voice, int v)
    {
        byte header = ram[voice.Address & 0xFFFF];
        voice.BlockLoopFlag = header & 3;
        voice.BlockEnds = (header & 1) != 0;
        if (voice.BlockEnds) registers[0x7C] |= (byte)(1 << v);
        voice.Index = 0;
    }

    private void Advance(ref Voice voice, int pitch, int bank, int v)
    {
        voice.Counter += pitch;
        int steps = voice.Counter >> 12;
        voice.Counter &= 0xFFF;
        for (; steps > 0; steps--) DecodeNext(ref voice, bank, v);
    }

    private void DecodeNext(ref Voice voice, int bank, int v)
    {
        if (voice.Index >= 16)
        {
            // The finished block's end flag decides what follows.
            if (voice.BlockEnds)
            {
                int entry = (registers[0x5D] << 8) + (registers[bank + 4] << 2);
                voice.Address = ram[entry + 2 & 0xFFFF] | ram[entry + 3 & 0xFFFF] << 8;
                if (voice.BlockLoopFlag == 1)
                {
                    voice.Mode = EnvelopeMode.Release;
                    voice.Envelope = 0;
                }
            }
            else voice.Address = voice.Address + 9 & 0xFFFF;
            StartBlock(ref voice, v);
        }
        byte header = ram[voice.Address & 0xFFFF];
        int shift = header >> 4, filter = header >> 2 & 3;
        int data = ram[voice.Address + 1 + (voice.Index >> 1) & 0xFFFF];
        int nibble = (voice.Index & 1) == 0 ? (sbyte)data >> 4 : (sbyte)(data << 4) >> 4;
        voice.Index++;

        int s = shift <= 12 ? nibble << shift >> 1 : (nibble < 0 ? -2048 : 0);
        int old = voice.Old, older = voice.Older;
        switch (filter)
        {
            case 1: s += old + (-old >> 4); break;
            case 2: s += old * 2 + (-old * 3 >> 5) - older + (older >> 4); break;
            case 3: s += old * 2 + (-old * 13 >> 6) - older + (older * 3 >> 4); break;
        }
        s = Clamp16(s);
        s = (short)(s << 1) >> 1;
        voice.Older = old;
        voice.Old = s;
        voice.H0 = voice.H1;
        voice.H1 = voice.H2;
        voice.H2 = voice.H3;
        voice.H3 = s;
    }

    private static int Interpolate(ref Voice voice)
    {
        int i = voice.Counter >> 4 & 0xFF;
        short[] g = GaussTable.Values;
        int sum = g[0xFF - i] * voice.H0 >> 10;
        sum += g[0x1FF - i] * voice.H1 >> 10;
        sum = (short)sum;
        sum += g[0x100 + i] * voice.H2 >> 10;
        sum = Clamp16(sum + (g[i] * voice.H3 >> 10));
        return sum >> 1;
    }

    private bool RateEvent(int rate)
    {
        int period = RatePeriods[rate];
        return period != 0 && rateCounter % period == 0;
    }

    private void UpdateEnvelope(ref Voice voice, int bank)
    {
        int adsr1 = registers[bank + 5];
        int envelope = voice.Envelope;
        if (voice.Mode == EnvelopeMode.Release)
        {
            voice.Envelope = Math.Max(0, envelope - 8);
            return;
        }

        int rate, step;
        if ((adsr1 & 0x80) != 0)
        {
            int adsr2 = registers[bank + 6];
            switch (voice.Mode)
            {
                case EnvelopeMode.Attack:
                    rate = (adsr1 & 0x0F) * 2 + 1;
                    step = rate == 31 ? 1024 : 32;
                    break;
                case EnvelopeMode.Decay:
                    rate = (adsr1 >> 4 & 7) * 2 + 16;
                    step = -((envelope - 1 >> 8) + 1);
                    break;
                default:
                    rate = adsr2 & 0x1F;
                    step = -((envelope - 1 >> 8) + 1);
                    break;
            }
            if (RateEvent(rate)) envelope += step;
        }
        else
        {
            int gain = registers[bank + 7];
            if ((gain & 0x80) == 0) envelope = (gain & 0x7F) * 16;
            else
            {
                rate = gain & 0x1F;
                if (RateEvent(rate))
                {
                    switch (gain >> 5 & 3)
                    {
                        case 0: envelope -= 32; break;
                        case 1: envelope -= (envelope - 1 >> 8) + 1; break;
                        case 2: envelope += 32; break;
                        default: envelope += envelope < 0x600 ? 32 : 8; break;
                    }
                }
            }
        }
        // The phase is tracked in GAIN mode too, so that switching to ADSR continues from the right one.
        int boundary = (adsr1 & 0x80) != 0 ? registers[bank + 6] >> 5 : registers[bank + 7] >> 5;
        if (voice.Mode == EnvelopeMode.Attack && envelope >= 0x7E0) voice.Mode = EnvelopeMode.Decay;
        else if (voice.Mode == EnvelopeMode.Decay && envelope <= (boundary + 1) * 0x100) voice.Mode = EnvelopeMode.Sustain;
        voice.Envelope = Math.Clamp(envelope, 0, 0x7FF);
    }

    private static int Clamp16(int value) => value > short.MaxValue ? short.MaxValue : value < short.MinValue ? short.MinValue : value;
}
