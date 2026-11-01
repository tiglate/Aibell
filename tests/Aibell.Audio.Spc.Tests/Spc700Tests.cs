namespace Aibell.Audio.Spc.Tests;

[TestFixture]
public class Spc700Tests
{
    // Flag bits of the PSW as stored by "PUSH PSW".
    private const int C = 0x01, Z = 0x02, H = 0x08, V = 0x40, N = 0x80;

    private static byte[] Run(SpcBuilder builder, int frames = 40)
    {
        var machine = new Spc700();
        machine.Load(SpcFile.Parse(builder.Build()));
        for (int i = 0; i < frames; i++) machine.RunFrame(out _, out _);
        return machine.Ram;
    }

    // Stores A at $10 and the flags at $11, then spins.
    private static readonly byte[] Epilogue = [0xC4, 0x10, 0x0D, 0xAE, 0xC4, 0x11, 0x2F, 0xFE];

    private static byte[] Compute(params byte[] instructions) => Run(new SpcBuilder().Program([.. instructions, .. Epilogue]));

    [Test]
    public void Adc_SetsOverflowNegativeAndHalfCarry()
    {
        byte[] ram = Compute(0x60, 0xE8, 0x7F, 0x88, 0x01);   // CLRC, MOV A,#7F, ADC A,#1

        Assert.Multiple(() =>
        {
            Assert.That(ram[0x10], Is.EqualTo(0x80));
            Assert.That(ram[0x11], Is.EqualTo(N | V | H));
        });
    }

    [Test]
    public void Adc_CarriesOut()
    {
        byte[] ram = Compute(0x80, 0xE8, 0xFF, 0x88, 0x00);   // SETC, MOV A,#FF, ADC A,#0

        Assert.Multiple(() =>
        {
            Assert.That(ram[0x10], Is.EqualTo(0));
            Assert.That(ram[0x11], Is.EqualTo(Z | H | C));
        });
    }

    [Test]
    public void Sbc_SetsOverflowAndKeepsCarryWithoutBorrow()
    {
        byte[] ram = Compute(0x80, 0xE8, 0x80, 0xA8, 0x01);   // SETC, MOV A,#80, SBC A,#1

        Assert.Multiple(() =>
        {
            Assert.That(ram[0x10], Is.EqualTo(0x7F));
            Assert.That(ram[0x11], Is.EqualTo(V | C));
        });
    }

    [Test]
    public void Sbc_BorrowsWithCarryClear()
    {
        byte[] ram = Compute(0x60, 0xE8, 0x00, 0xA8, 0x00);   // CLRC, MOV A,#0, SBC A,#0

        Assert.Multiple(() =>
        {
            Assert.That(ram[0x10], Is.EqualTo(0xFF));
            Assert.That(ram[0x11], Is.EqualTo(N));
        });
    }

    [Test]
    public void Cmp_SetsCarryWhenAccumulatorIsNotLess()
    {
        byte[] less = Compute(0xE8, 0x05, 0x68, 0x06);        // MOV A,#5, CMP A,#6
        byte[] equal = Compute(0xE8, 0x06, 0x68, 0x06);

        Assert.Multiple(() =>
        {
            Assert.That(less[0x11], Is.EqualTo(N));
            Assert.That(equal[0x11], Is.EqualTo(Z | C));
            Assert.That(less[0x10], Is.EqualTo(5), "CMP leaves A alone");
        });
    }

    [Test]
    public void Daa_AdjustsBcdSum()
    {
        byte[] ram = Compute(0x60, 0xE8, 0x15, 0x88, 0x27, 0xDF);   // 15 + 27 in BCD

        Assert.That(ram[0x10], Is.EqualTo(0x42));
    }

    [Test]
    public void Das_AdjustsBcdDifference()
    {
        byte[] ram = Compute(0x80, 0xE8, 0x42, 0xA8, 0x27, 0xBE);   // 42 - 27 in BCD

        Assert.That(ram[0x10], Is.EqualTo(0x15));
    }

    [Test]
    public void Mul_MultipliesYAIntoYA()
    {
        // MOV A,#12 / MOV Y,#34 / MUL YA / MOV $12,Y
        byte[] ram = Compute(0xE8, 0x12, 0x8D, 0x34, 0xCF, 0xCB, 0x12);

        Assert.Multiple(() =>
        {
            Assert.That(ram[0x10], Is.EqualTo(0xA8));
            Assert.That(ram[0x12], Is.EqualTo(0x03));
        });
    }

    [Test]
    public void Div_DividesYAByX()
    {
        // YA = 1000 = 03E8, X = 10: quotient 100, remainder 0.
        byte[] ram = Compute(0xE8, 0xE8, 0x8D, 0x03, 0xCD, 0x0A, 0x9E, 0xCB, 0x12);

        Assert.Multiple(() =>
        {
            Assert.That(ram[0x10], Is.EqualTo(100));
            Assert.That(ram[0x12], Is.EqualTo(0));
        });
    }

    [Test]
    public void Div_FlagsOverflowWhenQuotientDoesNotFit()
    {
        byte[] ram = Compute(0xE8, 0x00, 0x8D, 0x10, 0xCD, 0x01, 0x9E);   // YA = 0x1000, X = 1

        Assert.That(ram[0x11] & V, Is.EqualTo(V));
    }

    [Test]
    public void Addw_AddsSixteenBitsWithHalfCarryFromBit11()
    {
        // MOV A,#FF / MOV Y,#0F / ADDW YA,$20 with [$20] = 0001
        var builder = new SpcBuilder().Ram(0x20, 0x01, 0x00).Program([0xE8, 0xFF, 0x8D, 0x0F, 0x7A, 0x20, 0xC4, 0x10, 0xCB, 0x12, 0x0D, 0xAE, 0xC4, 0x11, 0x2F, 0xFE]);
        byte[] ram = Run(builder);

        Assert.Multiple(() =>
        {
            Assert.That(ram[0x10], Is.EqualTo(0x00));
            Assert.That(ram[0x12], Is.EqualTo(0x10));
            Assert.That(ram[0x11] & H, Is.EqualTo(H));
            Assert.That(ram[0x11] & C, Is.EqualTo(0));
        });
    }

    [Test]
    public void Incw_CarriesIntoHighByte()
    {
        var builder = new SpcBuilder().Ram(0x30, 0xFF, 0x00).Program(0x3A, 0x30, 0x2F, 0xFE);
        byte[] ram = Run(builder);

        Assert.That((ram[0x30], ram[0x31]), Is.EqualTo(((byte)0x00, (byte)0x01)));
    }

    [Test]
    public void BitOperations_SetClearAndInvert()
    {
        // SET1 $10.3 / SET1 $10.0 / CLR1 $10.0 / MOV A,#80 / MOV $12,A / NOT1 $12.0 (address 0x0012, bit 0 -> operand 0x0012)
        byte[] ram = Run(new SpcBuilder().Program(0x62, 0x10, 0x02, 0x10, 0x12, 0x10, 0xE8, 0x80, 0xC4, 0x12, 0xEA, 0x12, 0x00, 0x2F, 0xFE));

        Assert.Multiple(() =>
        {
            Assert.That(ram[0x10], Is.EqualTo(0x08));
            Assert.That(ram[0x12], Is.EqualTo(0x81));
        });
    }

    [Test]
    public void Call_PushesReturnAddressAndRetReturns()
    {
        // 0200: CALL !0210 / MOV $11,#AA / BRA $ ; 0210: MOV $10,#55 / RET
        var builder = new SpcBuilder().Program(0x3F, 0x10, 0x02, 0x8F, 0xAA, 0x11, 0x2F, 0xFE)
            .ProgramAt(0x0210, 0x8F, 0x55, 0x10, 0x6F);
        byte[] ram = Run(builder);

        Assert.That((ram[0x10], ram[0x11]), Is.EqualTo(((byte)0x55, (byte)0xAA)));
    }

    [Test]
    public void Tcall_JumpsThroughTheVectorTable()
    {
        // TCALL 4 reads its vector at FFDE - 8 = FFD6.
        var builder = new SpcBuilder().Ram(0xFFD6, 0x10, 0x02).Program(0x41, 0x8F, 0x77, 0x11, 0x2F, 0xFE)
            .ProgramAt(0x0210, 0x8F, 0x66, 0x10, 0x6F);
        byte[] ram = Run(builder);

        Assert.That((ram[0x10], ram[0x11]), Is.EqualTo(((byte)0x66, (byte)0x77)));
    }

    [Test]
    public void Dbnz_LoopsUntilZero()
    {
        // MOV $10,#0 / MOV Y,#5 / loop: INC $10 / DBNZ Y,loop
        byte[] ram = Run(new SpcBuilder().Program(0x8F, 0x00, 0x10, 0x8D, 0x05, 0xAB, 0x10, 0xFE, 0xFC, 0x2F, 0xFE));

        Assert.That(ram[0x10], Is.EqualTo(5));
    }

    [Test]
    public void DirectPageFlag_SelectsPageOne()
    {
        // SETP / MOV $10,#42 (goes to 0110)
        byte[] ram = Run(new SpcBuilder().Program(0x40, 0x8F, 0x42, 0x10, 0x2F, 0xFE));

        Assert.Multiple(() =>
        {
            Assert.That(ram[0x110], Is.EqualTo(0x42));
            Assert.That(ram[0x10], Is.EqualTo(0));
        });
    }

    [Test]
    public void IndirectAddressing_ReadsThroughPointers()
    {
        // [$20] = 0300, Y = 2, [0302] = 9C: MOV A,[$20]+Y
        var builder = new SpcBuilder().Ram(0x20, 0x00, 0x03).Ram(0x0302, 0x9C)
            .Program([0x8D, 0x02, 0xF7, 0x20, .. Epilogue]);
        byte[] ram = Run(builder);

        Assert.That(ram[0x10], Is.EqualTo(0x9C));
    }

    [Test]
    public void Timer_CountsAtItsDividedRate()
    {
        // Timer 0 (8 kHz, one tick every 128 cycles) with divider 2 gives an output every 256 cycles.
        // MOV $FA,#2 / MOV $F1,#1 / loop: MOV A,$FD / BEQ loop / MOV $10,A / BRA $
        var builder = new SpcBuilder().Program(0x8F, 0x02, 0xFA, 0x8F, 0x01, 0xF1, 0xE4, 0xFD, 0xF0, 0xFC, 0xC4, 0x10, 0x2F, 0xFE);
        var machine = new Spc700();
        machine.Load(SpcFile.Parse(builder.Build()));
        int frames = 0;
        while (machine.Ram[0x10] == 0 && frames < 100)
        {
            machine.RunFrame(out _, out _);
            frames++;
        }

        Assert.Multiple(() =>
        {
            Assert.That(machine.Ram[0x10], Is.EqualTo(1));
            Assert.That(frames, Is.InRange(7, 10), "256 cycles are 8 frames of 32");
        });
    }

    [Test]
    public void SavedPorts_AreSeenAsInput()
    {
        var builder = new SpcBuilder().Ram(0xF4, 0x42).Program([0xE4, 0xF4, .. Epilogue]);
        byte[] ram = Run(builder);

        Assert.That(ram[0x10], Is.EqualTo(0x42));
    }

    [Test]
    public void DspRegisters_AreReachedThroughTheIndexAndDataPorts()
    {
        // MOV $F2,#0D / MOV $F3,#33 / MOV A,$F3
        byte[] ram = Run(new SpcBuilder().Program([0x8F, 0x0D, 0xF2, 0x8F, 0x33, 0xF3, 0xE4, 0xF3, .. Epilogue]));

        Assert.That(ram[0x10], Is.EqualTo(0x33));
    }

    [Test]
    public void ProgramInsideTheBootRom_Idles()
    {
        var builder = new SpcBuilder { Pc = 0xFFC0 }.Ram(0xF1, 0x80);
        var machine = new Spc700();
        machine.Load(SpcFile.Parse(builder.Build()));

        Assert.DoesNotThrow(() =>
        {
            for (int i = 0; i < 10; i++) machine.RunFrame(out _, out _);
        });
    }
}
