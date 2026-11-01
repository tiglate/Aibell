namespace Aibell.Audio.Spc;

/// <summary>
/// The SNES sound unit: the SPC700 CPU (1.024 MHz), 64 KB of RAM, the I/O ports at 00F0h-00FFh with their three timers, and the
/// <see cref="SpcDsp"/>, which makes one output frame every 32 CPU cycles. The boot ROM is not emulated: a program that jumps into it
/// (as some drivers do to reset) idles there, as the ROM would while waiting for the main CPU.
/// </summary>
internal sealed class Spc700
{
    public const int CyclesPerFrame = 32;

    private const int FlagC = 0x01, FlagZ = 0x02, FlagH = 0x08, FlagB = 0x10, FlagP = 0x20, FlagV = 0x40, FlagN = 0x80;

    // Cycles of every opcode, branches excluded from their extra two when not taken.
    private static readonly byte[] Cycles =
    [
        2, 8, 4, 5, 3, 4, 3, 6, 2, 6, 5, 4, 5, 4, 6, 8,
        2, 8, 4, 5, 4, 5, 5, 6, 5, 5, 6, 5, 2, 2, 4, 6,
        2, 8, 4, 5, 3, 4, 3, 6, 2, 6, 5, 4, 5, 4, 5, 4,
        2, 8, 4, 5, 4, 5, 5, 6, 5, 5, 6, 5, 2, 2, 3, 8,
        2, 8, 4, 5, 3, 4, 3, 6, 2, 6, 4, 4, 5, 4, 6, 6,
        2, 8, 4, 5, 4, 5, 5, 6, 5, 5, 4, 5, 2, 2, 4, 3,
        2, 8, 4, 5, 3, 4, 3, 6, 2, 6, 4, 4, 5, 4, 5, 5,
        2, 8, 4, 5, 4, 5, 5, 6, 5, 5, 5, 5, 2, 2, 3, 6,
        2, 8, 4, 5, 3, 4, 3, 6, 2, 6, 5, 4, 5, 2, 4, 5,
        2, 8, 4, 5, 4, 5, 5, 6, 5, 5, 5, 5, 2, 2, 12, 5,
        3, 8, 4, 5, 3, 4, 3, 6, 2, 6, 4, 4, 5, 2, 4, 4,
        2, 8, 4, 5, 4, 5, 5, 6, 5, 5, 5, 5, 2, 2, 3, 4,
        3, 8, 4, 5, 4, 5, 4, 7, 2, 5, 6, 4, 5, 2, 4, 9,
        2, 8, 4, 5, 5, 6, 6, 7, 4, 5, 5, 5, 2, 2, 6, 3,
        2, 8, 4, 5, 3, 4, 3, 6, 2, 4, 5, 3, 4, 3, 4, 3,
        2, 8, 4, 5, 4, 5, 5, 6, 3, 4, 5, 4, 2, 2, 4, 3
    ];

    private readonly byte[] ram = new byte[0x10000];
    private readonly byte[] extraRam = new byte[64];
    private readonly byte[] portIn = new byte[4];
    private readonly byte[] portOut = new byte[4];
    private readonly bool[] timerEnabled = new bool[3];
    private readonly int[] timerClock = new int[3];
    private readonly int[] timerStage = new int[3];
    private readonly int[] timerOutput = new int[3];
    private int a, x, y, sp, pc;
    private int n, v, h, z, c;     // flags as 0/1 values
    private int psw;               // the P, B and I bits (the rest are kept in the flag fields)
    private bool romEnabled;
    private int dspAddress;
    private int frameCycles;
    private bool stopped;

    public Spc700()
    {
        Dsp = new SpcDsp(ram);
    }

    public SpcDsp Dsp { get; }

    public byte[] Ram => ram;

    /// <summary>Restores the state saved in <paramref name="file"/>.</summary>
    public void Load(SpcFile file)
    {
        file.Ram.CopyTo(ram);
        file.ExtraRam.CopyTo(extraRam);
        Dsp.Load(file.DspRegisters);
        pc = file.Pc;
        a = file.A;
        x = file.X;
        y = file.Y;
        sp = file.Sp;
        SetPsw(file.Psw);
        Array.Clear(timerClock);
        Array.Clear(timerStage);
        Array.Clear(timerOutput);
        for (int i = 0; i < 4; i++) portIn[i] = portOut[i] = ram[0xF4 + i];
        dspAddress = ram[0xF2];
        romEnabled = (ram[0xF1] & 0x80) != 0;
        for (int i = 0; i < 3; i++)
        {
            timerEnabled[i] = (ram[0xF1] >> i & 1) != 0;
            timerOutput[i] = ram[0xFD + i] & 0x0F;
        }
        frameCycles = 0;
        stopped = false;
    }

    /// <summary>The value the CPU last wrote to output port 0-3 (what the main CPU would read).</summary>
    public byte ReadOutputPort(int port) => portOut[port & 3];

    /// <summary>Sets what the CPU reads from input port 0-3 (what the main CPU wrote).</summary>
    public void WriteInputPort(int port, byte value) => portIn[port & 3] = value;

    /// <summary>Runs until the DSP has produced one frame.</summary>
    public void RunFrame(out int left, out int right)
    {
        while (frameCycles < CyclesPerFrame) Execute();
        frameCycles -= CyclesPerFrame;
        Dsp.Step(out left, out right);
    }

    private void SetPsw(int value)
    {
        n = value >> 7 & 1;
        v = value >> 6 & 1;
        h = value >> 3 & 1;
        z = value >> 1 & 1;
        c = value & 1;
        psw = value & (FlagP | FlagB | 0x04);
    }

    private int GetPsw() => psw | n << 7 | v << 6 | h << 3 | z << 1 | c;

    private int DirectBase => (psw & FlagP) != 0 ? 0x100 : 0;

    // --- Memory ---------------------------------------------------------------------------------------------------------------

    private int Read(int address)
    {
        address &= 0xFFFF;
        if (address < 0xF0 || address > 0xFF && (address < 0xFFC0 || !romEnabled)) return ram[address];
        if (address >= 0xFFC0) return extraRam[address - 0xFFC0];
        switch (address)
        {
            case 0xF2: return dspAddress;
            case 0xF3: return Dsp.Read(dspAddress);
            case >= 0xF4 and <= 0xF7: return portIn[address - 0xF4];
            case >= 0xFD:
                int timer = address - 0xFD;
                int value = timerOutput[timer];
                timerOutput[timer] = 0;
                return value;
            default: return ram[address];
        }
    }

    private void Write(int address, int value)
    {
        address &= 0xFFFF;
        ram[address] = (byte)value;
        if (address < 0xF0 || address > 0xFF) return;
        switch (address)
        {
            case 0xF1:
                for (int i = 0; i < 3; i++)
                {
                    bool enable = (value >> i & 1) != 0;
                    if (enable && !timerEnabled[i])
                    {
                        timerStage[i] = 0;
                        timerOutput[i] = 0;
                    }
                    timerEnabled[i] = enable;
                }
                if ((value & 0x10) != 0) portIn[0] = portIn[1] = 0;
                if ((value & 0x20) != 0) portIn[2] = portIn[3] = 0;
                romEnabled = (value & 0x80) != 0;
                break;
            case 0xF2: dspAddress = value; break;
            case 0xF3: Dsp.Write(dspAddress, (byte)value); break;
            case >= 0xF4 and <= 0xF7: portOut[address - 0xF4] = (byte)value; break;
        }
    }

    private int ReadWord(int address) => Read(address) | Read(address + 1) << 8;

    // A 16-bit access in the direct page wraps inside the page.
    private int ReadDirectWord(int offset) => Read(DirectBase + (offset & 0xFF)) | Read(DirectBase + (offset + 1 & 0xFF)) << 8;

    private void WriteDirectWord(int offset, int value)
    {
        Write(DirectBase + (offset & 0xFF), value & 0xFF);
        Write(DirectBase + (offset + 1 & 0xFF), value >> 8 & 0xFF);
    }

    private int Fetch()
    {
        int value = Read(pc);
        pc = pc + 1 & 0xFFFF;
        return value;
    }

    private int Direct() => DirectBase + Fetch();

    private int DirectX() => DirectBase + (Fetch() + x & 0xFF);

    private int DirectY() => DirectBase + (Fetch() + y & 0xFF);

    private int Absolute()
    {
        int low = Fetch();
        return low | Fetch() << 8;
    }

    private int IndirectX() => ReadDirectWord(Fetch() + x & 0xFF);

    private int IndirectY() => ReadDirectWord(Fetch()) + y & 0xFFFF;

    private void Push(int value)
    {
        Write(0x100 + sp, value);
        sp = sp - 1 & 0xFF;
    }

    private int Pop()
    {
        sp = sp + 1 & 0xFF;
        return Read(0x100 + sp);
    }

    private void PushPc()
    {
        Push(pc >> 8);
        Push(pc & 0xFF);
    }

    private void SetNz(int value)
    {
        n = value >> 7 & 1;
        z = (value & 0xFF) == 0 ? 1 : 0;
    }

    // --- ALU ------------------------------------------------------------------------------------------------------------------

    private int Adc(int left, int right)
    {
        int sum = left + right + c;
        h = (left & 0xF) + (right & 0xF) + c > 0xF ? 1 : 0;
        v = (~(left ^ right) & (left ^ sum) & 0x80) != 0 ? 1 : 0;
        c = sum > 0xFF ? 1 : 0;
        SetNz(sum);
        return sum & 0xFF;
    }

    private int Sbc(int left, int right)
    {
        int diff = left - right - (1 - c);
        h = (left & 0xF) - (right & 0xF) - (1 - c) >= 0 ? 1 : 0;
        v = ((left ^ right) & (left ^ diff) & 0x80) != 0 ? 1 : 0;
        c = diff >= 0 ? 1 : 0;
        SetNz(diff);
        return diff & 0xFF;
    }

    private void Compare(int left, int right)
    {
        int diff = left - right;
        c = diff >= 0 ? 1 : 0;
        SetNz(diff);
    }

    // The six two-operand operations of opcode rows x0-x9 and the memory forms; kind is 0 OR, 1 AND, 2 EOR, 3 CMP, 4 ADC, 5 SBC.
    private int Alu(int kind, int left, int right)
    {
        int result;
        switch (kind)
        {
            case 0: result = left | right; break;
            case 1: result = left & right; break;
            case 2: result = left ^ right; break;
            case 3: Compare(left, right); return left;
            case 4: return Adc(left, right);
            default: return Sbc(left, right);
        }
        SetNz(result);
        return result;
    }

    private int Shift(int kind, int value)
    {
        int result;
        switch (kind)
        {
            case 0: c = value >> 7 & 1; result = value << 1 & 0xFF; break;                       // ASL
            case 1: result = (value << 1 | c) & 0xFF; c = value >> 7 & 1; break;                // ROL
            case 2: c = value & 1; result = value >> 1; break;                                   // LSR
            default: result = value >> 1 | c << 7; c = value & 1; break;                         // ROR
        }
        SetNz(result);
        return result;
    }

    private void Branch(bool condition, ref int cycles)
    {
        int offset = (sbyte)Fetch();
        if (!condition) return;
        pc = pc + offset & 0xFFFF;
        cycles += 2;
    }

    // --- Instruction execution ------------------------------------------------------------------------------------------------

    private void Execute()
    {
        if (stopped || romEnabled && pc >= 0xFFC0)
        {
            Tick(2);
            return;
        }
        int opcode = Fetch();
        int cycles = Cycles[opcode];
        int address, value, temp;

        switch (opcode)
        {
            case 0x00: break;                                                                          // NOP
            case 0xEF or 0xFF: stopped = true; break;                                                  // SLEEP, STOP
            case 0x01 or 0x11 or 0x21 or 0x31 or 0x41 or 0x51 or 0x61 or 0x71 or 0x81 or 0x91 or 0xA1 or 0xB1 or 0xC1 or 0xD1 or 0xE1 or 0xF1:
                PushPc();                                                                              // TCALL n
                pc = ReadWord(0xFFDE - 2 * (opcode >> 4));
                break;
            case 0x4F: temp = Fetch(); PushPc(); pc = 0xFF00 | temp; break;                            // PCALL
            case 0x3F: address = Absolute(); PushPc(); pc = address; break;                            // CALL
            case 0x6F: temp = Pop(); pc = temp | Pop() << 8; break;                                    // RET
            case 0x7F: SetPsw(Pop()); temp = Pop(); pc = temp | Pop() << 8; break;                     // RETI
            case 0x0F:                                                                                 // BRK
                PushPc();
                Push(GetPsw());
                psw = (psw | FlagB) & ~0x04;
                pc = ReadWord(0xFFDE);
                break;
            case 0x5F: pc = Absolute(); break;                                                         // JMP !abs
            case 0x1F: pc = ReadWord(Absolute() + x); break;                                           // JMP [!abs+X]
            case 0x2F: Branch(true, ref cycles); break;                                                // BRA
            case 0x10: Branch(n == 0, ref cycles); break;
            case 0x30: Branch(n != 0, ref cycles); break;
            case 0x50: Branch(v == 0, ref cycles); break;
            case 0x70: Branch(v != 0, ref cycles); break;
            case 0x90: Branch(c == 0, ref cycles); break;
            case 0xB0: Branch(c != 0, ref cycles); break;
            case 0xD0: Branch(z == 0, ref cycles); break;
            case 0xF0: Branch(z != 0, ref cycles); break;
            case 0x03 or 0x23 or 0x43 or 0x63 or 0x83 or 0xA3 or 0xC3 or 0xE3:                         // BBS
                value = Read(Direct());
                Branch((value >> (opcode >> 5) & 1) != 0, ref cycles);
                break;
            case 0x13 or 0x33 or 0x53 or 0x73 or 0x93 or 0xB3 or 0xD3 or 0xF3:                         // BBC
                value = Read(Direct());
                Branch((value >> (opcode >> 5) & 1) == 0, ref cycles);
                break;
            case 0x02 or 0x22 or 0x42 or 0x62 or 0x82 or 0xA2 or 0xC2 or 0xE2:                         // SET1
                address = Direct();
                Write(address, Read(address) | 1 << (opcode >> 5));
                break;
            case 0x12 or 0x32 or 0x52 or 0x72 or 0x92 or 0xB2 or 0xD2 or 0xF2:                         // CLR1
                address = Direct();
                Write(address, Read(address) & ~(1 << (opcode >> 5)));
                break;
            case 0x2E: address = Direct(); Compare2(a, Read(address), ref cycles); break;              // CBNE dp,rel
            case 0xDE: address = DirectX(); Compare2(a, Read(address), ref cycles); break;             // CBNE dp+X,rel
            case 0x6E:                                                                                 // DBNZ dp,rel
                address = Direct();
                value = Read(address) - 1 & 0xFF;
                Write(address, value);
                Branch(value != 0, ref cycles);
                break;
            case 0xFE: y = y - 1 & 0xFF; Branch(y != 0, ref cycles); break;                            // DBNZ Y,rel

            // Flag operations
            case 0x20: psw &= ~FlagP; break;
            case 0x40: psw |= FlagP; break;
            case 0x60: c = 0; break;
            case 0x80: c = 1; break;
            case 0xED: c ^= 1; break;
            case 0xE0: v = 0; h = 0; break;
            case 0xA0: psw |= 0x04; break;
            case 0xC0: psw &= ~0x04; break;

            // Loads
            case 0xE8: a = Fetch(); SetNz(a); break;
            case 0xCD: x = Fetch(); SetNz(x); break;
            case 0x8D: y = Fetch(); SetNz(y); break;
            case 0xE4: a = Read(Direct()); SetNz(a); break;
            case 0xF4: a = Read(DirectX()); SetNz(a); break;
            case 0xE5: a = Read(Absolute()); SetNz(a); break;
            case 0xF5: a = Read(Absolute() + x); SetNz(a); break;
            case 0xF6: a = Read(Absolute() + y); SetNz(a); break;
            case 0xE6: a = Read(DirectBase + x); SetNz(a); break;
            case 0xBF: a = Read(DirectBase + x); x = x + 1 & 0xFF; SetNz(a); break;
            case 0xF7: a = Read(IndirectY()); SetNz(a); break;
            case 0xE7: a = Read(IndirectX()); SetNz(a); break;
            case 0xF8: x = Read(Direct()); SetNz(x); break;
            case 0xF9: x = Read(DirectY()); SetNz(x); break;
            case 0xE9: x = Read(Absolute()); SetNz(x); break;
            case 0xEB: y = Read(Direct()); SetNz(y); break;
            case 0xFB: y = Read(DirectX()); SetNz(y); break;
            case 0xEC: y = Read(Absolute()); SetNz(y); break;
            case 0x7D: a = x; SetNz(a); break;
            case 0xDD: a = y; SetNz(a); break;
            case 0x5D: x = a; SetNz(x); break;
            case 0xFD: y = a; SetNz(y); break;
            case 0x9D: x = sp; SetNz(x); break;
            case 0xBD: sp = x; break;
            case 0xBA: value = ReadDirectWord(Fetch()); a = value & 0xFF; y = value >> 8; n = value >> 15 & 1; z = value == 0 ? 1 : 0; break;

            // Stores
            case 0x8F: value = Fetch(); Write(Direct(), value); break;
            case 0xFA: value = Read(Direct()); Write(Direct(), value); break;
            case 0xC4: Write(Direct(), a); break;
            case 0xD8: Write(Direct(), x); break;
            case 0xCB: Write(Direct(), y); break;
            case 0xD4: Write(DirectX(), a); break;
            case 0xDB: Write(DirectX(), y); break;
            case 0xD9: Write(DirectY(), x); break;
            case 0xC5: Write(Absolute(), a); break;
            case 0xC9: Write(Absolute(), x); break;
            case 0xCC: Write(Absolute(), y); break;
            case 0xD5: Write(Absolute() + x, a); break;
            case 0xD6: Write(Absolute() + y, a); break;
            case 0xAF: Write(DirectBase + x, a); x = x + 1 & 0xFF; break;
            case 0xC6: Write(DirectBase + x, a); break;
            case 0xD7: Write(IndirectY(), a); break;
            case 0xC7: Write(IndirectX(), a); break;
            case 0xDA: WriteDirectWord(Fetch(), y << 8 | a); break;

            // Stack
            case 0x2D: Push(a); break;
            case 0x4D: Push(x); break;
            case 0x6D: Push(y); break;
            case 0x0D: Push(GetPsw()); break;
            case 0xAE: a = Pop(); break;
            case 0xCE: x = Pop(); break;
            case 0xEE: y = Pop(); break;
            case 0x8E: SetPsw(Pop()); break;

            // Compare X and Y
            case 0xC8: Compare(x, Fetch()); break;
            case 0x3E: Compare(x, Read(Direct())); break;
            case 0x1E: Compare(x, Read(Absolute())); break;
            case 0xAD: Compare(y, Fetch()); break;
            case 0x7E: Compare(y, Read(Direct())); break;
            case 0x5E: Compare(y, Read(Absolute())); break;

            // 16-bit
            case 0x7A: temp = ReadDirectWord(Fetch()); Addw(temp); break;
            case 0x9A: temp = ReadDirectWord(Fetch()); Subw(temp); break;
            case 0x5A:
                temp = ReadDirectWord(Fetch());
                value = (y << 8 | a) - temp;
                c = value >= 0 ? 1 : 0;
                n = value >> 15 & 1;
                z = (value & 0xFFFF) == 0 ? 1 : 0;
                break;
            case 0x3A: Incw(Fetch(), 1); break;
            case 0x1A: Incw(Fetch(), -1); break;
            case 0xCF: value = y * a; a = value & 0xFF; y = value >> 8; SetNz(y); break;              // MUL
            case 0x9E: Divide(); break;
            case 0xDF:                                                                                 // DAA
                if (c != 0 || a > 0x99) { a += 0x60; c = 1; }
                if (h != 0 || (a & 0xF) > 9) a += 6;
                a &= 0xFF;
                SetNz(a);
                break;
            case 0xBE:                                                                                 // DAS
                if (c == 0 || a > 0x99) { a -= 0x60; c = 0; }
                if (h == 0 || (a & 0xF) > 9) a -= 6;
                a &= 0xFF;
                SetNz(a);
                break;
            case 0x9F: a = (a >> 4 | a << 4) & 0xFF; SetNz(a); break;                                  // XCN
            case 0x0E: address = Absolute(); value = Read(address); SetNz(a - value); Write(address, value | a); break;   // TSET1
            case 0x4E: address = Absolute(); value = Read(address); SetNz(a - value); Write(address, value & ~a); break;  // TCLR1

            // Single bit operations on [aaa.b]
            case 0x0A: BitOp(0); break;
            case 0x2A: BitOp(1); break;
            case 0x4A: BitOp(2); break;
            case 0x6A: BitOp(3); break;
            case 0x8A: BitOp(4); break;
            case 0xAA: BitOp(5); break;
            case 0xCA: BitOp(6); break;
            case 0xEA: BitOp(7); break;

            default: Group(opcode); break;
        }
        Tick(cycles);
    }

    private void Compare2(int accumulator, int operand, ref int cycles) => Branch(accumulator != operand, ref cycles);

    // The regular instructions: ALU rows 0x00-0xA9 and the INC/DEC/shift forms.
    private void Group(int opcode)
    {
        int low = opcode & 0x1F;
        int high = opcode >> 5;
        int address;
        // Operations OR AND EOR CMP ADC SBC (high 0-5) in the forms x4-x9 and x4+0x10-x9+0x10, plus x8.
        if (high <= 5 && (low is 0x04 or 0x05 or 0x06 or 0x07 or 0x08 or 0x09 or 0x14 or 0x15 or 0x16 or 0x17 or 0x18 or 0x19))
        {
            int kind = high;
            switch (low)
            {
                case 0x08: a = Alu(kind, a, Fetch()); return;                                       // A,#imm  (CMP keeps A)
                case 0x04: a = Alu(kind, a, Read(Direct())); return;
                case 0x14: a = Alu(kind, a, Read(DirectX())); return;
                case 0x05: a = Alu(kind, a, Read(Absolute())); return;
                case 0x15: a = Alu(kind, a, Read(Absolute() + x)); return;
                case 0x16: a = Alu(kind, a, Read(Absolute() + y)); return;
                case 0x06: a = Alu(kind, a, Read(DirectBase + x)); return;
                case 0x17: a = Alu(kind, a, Read(IndirectY())); return;
                case 0x07: a = Alu(kind, a, Read(IndirectX())); return;
                case 0x09:                                                                          // dp,dp
                    {
                        int src = Read(Direct());
                        address = Direct();
                        int result = Alu(kind, Read(address), src);
                        if (kind != 3) Write(address, result);
                        return;
                    }
                case 0x18:                                                                          // dp,#imm
                    {
                        int imm = Fetch();
                        address = Direct();
                        int result = Alu(kind, Read(address), imm);
                        if (kind != 3) Write(address, result);
                        return;
                    }
                default:                                                                            // (X),(Y)
                    {
                        int src = Read(DirectBase + y);
                        address = DirectBase + x;
                        int result = Alu(kind, Read(address), src);
                        if (kind != 3) Write(address, result);
                        return;
                    }
            }
        }

        // Shifts, rotates, INC and DEC on A, X, Y and memory.
        switch (opcode)
        {
            case 0x1C: a = Shift(0, a); return;
            case 0x3C: a = Shift(1, a); return;
            case 0x5C: a = Shift(2, a); return;
            case 0x7C: a = Shift(3, a); return;
            case 0x9C: a = a - 1 & 0xFF; SetNz(a); return;
            case 0xBC: a = a + 1 & 0xFF; SetNz(a); return;
            case 0x1D: x = x - 1 & 0xFF; SetNz(x); return;
            case 0x3D: x = x + 1 & 0xFF; SetNz(x); return;
            case 0xDC: y = y - 1 & 0xFF; SetNz(y); return;
            case 0xFC: y = y + 1 & 0xFF; SetNz(y); return;
            case 0x0B or 0x2B or 0x4B or 0x6B: Memory(Direct(), opcode >> 5); return;
            case 0x1B or 0x3B or 0x5B or 0x7B: Memory(DirectX(), opcode >> 5); return;
            case 0x0C or 0x2C or 0x4C or 0x6C: Memory(Absolute(), opcode >> 5); return;
            case 0x8B or 0xAB: Memory(Direct(), opcode >> 5); return;
            case 0x9B or 0xBB: Memory(DirectX(), opcode >> 5); return;
            case 0x8C or 0xAC: Memory(Absolute(), opcode >> 5); return;
        }
    }

    // kind 0-3: ASL ROL LSR ROR; 4: DEC; 5: INC.
    private void Memory(int address, int kind)
    {
        int value = Read(address);
        switch (kind)
        {
            case 4: value = value - 1 & 0xFF; SetNz(value); break;
            case 5: value = value + 1 & 0xFF; SetNz(value); break;
            default: value = Shift(kind, value); break;
        }
        Write(address, value);
    }

    private void BitOp(int kind)
    {
        int operand = Absolute();
        int address = operand & 0x1FFF, bit = operand >> 13;
        int value = Read(address);
        int mask = 1 << bit;
        int set = (value & mask) != 0 ? 1 : 0;
        switch (kind)
        {
            case 0: c |= set; break;                  // OR1 C,m.b
            case 1: c |= set ^ 1; break;              // OR1 C,/m.b
            case 2: c &= set; break;                  // AND1 C,m.b
            case 3: c &= set ^ 1; break;              // AND1 C,/m.b
            case 4: c ^= set; break;                  // EOR1 C,m.b
            case 5: c = set; break;                   // MOV1 C,m.b
            case 6: Write(address, c != 0 ? value | mask : value & ~mask); break; // MOV1 m.b,C
            default: Write(address, value ^ mask); break;                        // NOT1 m.b
        }
    }

    private void Addw(int operand)
    {
        int ya = y << 8 | a;
        c = 0;
        int low = Adc(ya & 0xFF, operand & 0xFF);
        int high = Adc(ya >> 8, operand >> 8);
        int result = high << 8 | low;
        a = low;
        y = high;
        z = result == 0 ? 1 : 0;
        n = high >> 7;
    }

    private void Subw(int operand)
    {
        int ya = y << 8 | a;
        c = 1;
        int low = Sbc(ya & 0xFF, operand & 0xFF);
        int high = Sbc(ya >> 8, operand >> 8);
        int result = high << 8 | low;
        a = low;
        y = high;
        z = result == 0 ? 1 : 0;
        n = high >> 7;
    }

    private void Incw(int offset, int delta)
    {
        int value = ReadDirectWord(offset) + delta & 0xFFFF;
        WriteDirectWord(offset, value);
        n = value >> 15;
        z = value == 0 ? 1 : 0;
    }

    private void Divide()
    {
        int ya = y << 8 | a;
        v = y >= x ? 1 : 0;
        h = (y & 0xF) >= (x & 0xF) ? 1 : 0;
        if (y < x << 1)
        {
            a = ya / x & 0xFF;
            y = ya % x;
        }
        else
        {
            int rest = ya - (x << 9);
            int divisor = 256 - x;
            a = 255 - rest / divisor & 0xFF;
            y = x + rest % divisor & 0xFF;
        }
        SetNz(a);
    }

    // --- Timers and the DSP clock ---------------------------------------------------------------------------------------------

    private void Tick(int cycles)
    {
        frameCycles += cycles;
        for (int i = 0; i < 3; i++)
        {
            timerClock[i] += cycles;
            int period = i == 2 ? 16 : 128;
            while (timerClock[i] >= period)
            {
                timerClock[i] -= period;
                if (!timerEnabled[i]) continue;
                int target = ram[0xFA + i];
                if (target == 0) target = 256;
                if (++timerStage[i] == target)
                {
                    timerStage[i] = 0;
                    timerOutput[i] = timerOutput[i] + 1 & 0x0F;
                }
            }
        }
    }
}
