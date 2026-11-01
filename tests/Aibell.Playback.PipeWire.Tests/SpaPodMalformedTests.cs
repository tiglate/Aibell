using Aibell.Playback.PipeWire;
using NUnit.Framework;

namespace Aibell.Playback.PipeWire.Tests;

/// <summary>The malformed input paths: a bad message must be refused, never read out of bounds.</summary>
[TestFixture]
public class SpaPodMalformedTests
{
    [Test]
    public void A_Pod_Size_That_Cannot_Be_Allocated_Throws()
    {
        byte[] bytes = new byte[16];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), uint.MaxValue);
        BitConverter.TryWriteBytes(bytes.AsSpan(4, 4), (uint)SpaPodType.Int);

        Assert.Throws<InvalidDataException>(() => SpaPod.Read(bytes));
    }

    [Test]
    public void An_Integer_Pod_With_A_Short_Body_Throws()
    {
        byte[] bytes = [2, 0, 0, 0, 4, 0, 0, 0, 1, 0];

        Assert.Throws<InvalidDataException>(() => SpaPod.Read(bytes).AsInt());
    }

    [Test]
    public void An_Array_Pod_Without_Its_Template_Throws()
    {
        byte[] bytes = [0, 0, 0, 0, 13, 0, 0, 0];

        Assert.Throws<InvalidDataException>(() => SpaPod.Read(bytes));
    }

    [Test]
    public void A_Choice_Pod_Without_Its_Header_Throws()
    {
        byte[] bytes = [8, 0, 0, 0, 19, 0, 0, 0];

        Assert.Throws<InvalidDataException>(() => SpaPod.Read(bytes));
    }

    [Test]
    public void An_Object_Pod_Without_Its_Header_Throws()
    {
        byte[] bytes = [4, 0, 0, 0, 15, 0, 0, 0];

        Assert.Throws<InvalidDataException>(() => SpaPod.Read(bytes));
    }

    [Test]
    public void An_Object_Pod_With_A_Truncated_Property_Throws()
    {
        // The body is the object type, then a property header without a value.
        byte[] bytes = [12, 0, 0, 0, 15, 0, 0, 0, 2, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0];

        Assert.Throws<InvalidDataException>(() => SpaPod.Read(bytes));
    }

    [Test]
    public void A_Struct_Whose_Child_Runs_Past_The_End_Throws()
    {
        // A struct whose body announces a child that does not fit.
        byte[] bytes = [16, 0, 0, 0, 14, 0, 0, 0, 64, 0, 0, 0, 4, 0, 0, 0, 1, 0, 0, 0];

        Assert.Throws<InvalidDataException>(() => SpaPod.Read(bytes));
    }

    [Test]
    public void A_Struct_Whose_Child_Header_Is_Truncated_Throws()
    {
        byte[] bytes = [4, 0, 0, 0, 14, 0, 0, 0, 4, 0, 0, 0];

        Assert.Throws<InvalidDataException>(() => SpaPod.Read(bytes));
    }

    [Test]
    public void A_Long_Pod_With_A_Short_Body_Throws()
    {
        byte[] bytes = [2, 0, 0, 0, 5, 0, 0, 0, 1, 0];

        Assert.Throws<InvalidDataException>(() => SpaPod.Read(bytes).AsLong());
    }

    [Test]
    public void A_Float_Pod_With_A_Short_Body_Throws()
    {
        byte[] bytes = [2, 0, 0, 0, 6, 0, 0, 0, 1, 0];

        Assert.Throws<InvalidDataException>(() => SpaPod.Read(bytes).AsFloat());
    }

    [Test]
    public void A_Double_Pod_With_A_Short_Body_Throws()
    {
        byte[] bytes = [2, 0, 0, 0, 7, 0, 0, 0, 1, 0];

        Assert.Throws<InvalidDataException>(() => SpaPod.Read(bytes).AsDouble());
    }

    [Test]
    public void An_Fd_Pod_With_A_Short_Body_Throws()
    {
        byte[] bytes = [2, 0, 0, 0, 18, 0, 0, 0, 1, 0];

        Assert.Throws<InvalidDataException>(() => SpaPod.Read(bytes).AsFd());
    }

    [Test]
    public void A_String_Without_A_Nul_Is_Read_In_Full()
    {
        byte[] bytes = [3, 0, 0, 0, 8, 0, 0, 0, (byte)'a', (byte)'b', (byte)'c'];

        Assert.That(SpaPod.Read(bytes).AsString(), Is.EqualTo("abc"));
    }
}
