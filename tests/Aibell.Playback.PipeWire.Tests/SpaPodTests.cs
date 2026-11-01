using Aibell.Playback.PipeWire;
using NUnit.Framework;

namespace Aibell.Playback.PipeWire.Tests;

/// <summary>The wire format of a POD is the foundation of the whole client, so every rule is pinned here.</summary>
[TestFixture]
public class SpaPodTests
{
    [Test]
    public void Int_Pod_Is_Sixteen_Bytes_With_Size_Four()
    {
        byte[] bytes = SpaPod.Int(4).Write();

        Assert.That(bytes, Has.Length.EqualTo(16));
        Assert.That(SpaPod.Read(bytes).AsInt(), Is.EqualTo(4));
        Assert.That(BitConverter.ToUInt32(bytes, 0), Is.EqualTo(4u), "the size field counts body bytes only");
        Assert.That(BitConverter.ToUInt32(bytes, 4), Is.EqualTo((uint)SpaPodType.Int));
        Assert.That(BitConverter.ToUInt32(bytes, 8), Is.EqualTo(4u), "the value follows the header");
    }

    [Test]
    public void String_Pod_Size_Counts_The_Nul_But_The_Pod_Is_Padded()
    {
        byte[] bytes = SpaPod.String("log.level").Write();

        // "log.level" plus the NUL is 10 bytes; the POD occupies align8(8 + 10) = 24 bytes.
        Assert.That(BitConverter.ToUInt32(bytes, 0), Is.EqualTo(10u));
        Assert.That(bytes, Has.Length.EqualTo(24));
        Assert.That(SpaPod.Read(bytes).AsString(), Is.EqualTo("log.level"));
    }

    [Test]
    public void String_Pod_Pads_To_Eight_Bytes()
    {
        byte[] bytes = SpaPod.String("ab").Write();

        // "ab" plus the NUL is 3 bytes; the POD occupies align8(8 + 3) = 16 bytes.
        Assert.That(BitConverter.ToUInt32(bytes, 0), Is.EqualTo(3u));
        Assert.That(bytes, Has.Length.EqualTo(16));
    }

    [Test]
    public void Empty_String_Pod_Is_Still_Eight_Aligned()
    {
        byte[] bytes = SpaPod.String("").Write();

        Assert.That(BitConverter.ToUInt32(bytes, 0), Is.EqualTo(1u), "the NUL still counts");
        Assert.That(bytes, Has.Length.EqualTo(16));
        Assert.That(SpaPod.Read(bytes).AsString(), Is.Empty);
    }

    [Test]
    public void Struct_Pod_Size_Sums_The_Children_Allocated_Sizes()
    {
        byte[] bytes = SpaPod.Struct(SpaPod.Int(3), SpaPod.Int(4)).Write();

        // Two Int PODs occupy 16 bytes each.
        Assert.That(BitConverter.ToUInt32(bytes, 0), Is.EqualTo(32u));
        Assert.That(bytes, Has.Length.EqualTo(40));
        Assert.That(SpaPod.Read(bytes).Children, Has.Count.EqualTo(2));
    }

    [Test]
    public void Struct_Pod_Round_Trips_Its_Children()
    {
        SpaPod pod = SpaPod.Read(SpaPod.Struct(SpaPod.Int(7), SpaPod.String("x")).Write());

        Assert.That(pod.Children[0].AsInt(), Is.EqualTo(7));
        Assert.That(pod.Children[1].AsString(), Is.EqualTo("x"));
    }

    [Test]
    public void Dictionary_Pod_Is_A_Struct_Of_Count_Then_Key_Value_Strings()
    {
        byte[] bytes = SpaPod.Dictionary([new KeyValuePair<string, string>("application.name", "Aibell")]).Write();

        SpaPod pod = SpaPod.Read(bytes);
        Assert.That(pod.Children, Has.Count.EqualTo(3));
        Assert.That(pod.Children[0].AsInt(), Is.EqualTo(1), "the entry count comes first");
        Assert.That(pod.Children[1].AsString(), Is.EqualTo("application.name"));
        Assert.That(pod.Children[2].AsString(), Is.EqualTo("Aibell"));
    }

    [Test]
    public void Empty_Dictionary_Has_No_Entries()
    {
        SpaPod pod = SpaPod.Read(SpaPod.Dictionary([]).Write());

        Assert.That(pod.Children, Has.Count.EqualTo(1));
        Assert.That(pod.Children[0].AsInt(), Is.Zero);
    }

    [Test]
    public void Bool_Pod_Reads_Back()
    {
        Assert.That(SpaPod.Read(SpaPod.Bool(true).Write()).AsBool(), Is.True);
        Assert.That(SpaPod.Read(SpaPod.Bool(false).Write()).AsBool(), Is.False);
    }

    [Test]
    public void Id_Pod_Reads_Back()
    {
        Assert.That(SpaPod.Read(SpaPod.Id(42).Write()).AsId(), Is.EqualTo(42u));
    }

    [Test]
    public void Long_And_Float_And_Double_Pods_Read_Back()
    {
        Assert.That(SpaPod.Read(SpaPod.Long(9_000_000_000L).Write()).AsLong(), Is.EqualTo(9_000_000_000L));
        Assert.That(SpaPod.Read(SpaPod.Float(1.5f).Write()).AsFloat(), Is.EqualTo(1.5f));
        Assert.That(SpaPod.Read(SpaPod.Double(2.25).Write()).AsDouble(), Is.EqualTo(2.25));
    }

    [Test]
    public void Fd_Pod_Reads_Back()
    {
        Assert.That(SpaPod.Read(SpaPod.Fd(17).Write()).AsFd(), Is.EqualTo(17));
    }

    [Test]
    public void Object_Pod_Holds_Its_Type_And_Properties()
    {
        byte[] bytes = SpaPod.Object(SpaObjectType.Props, 0,
            new SpaProperty(SpaParamKey.MediaClass, SpaPropFlags.None, SpaPod.String("Audio/Sink"))).Write();

        SpaPod pod = SpaPod.Read(bytes);
        Assert.That(pod.Type, Is.EqualTo(SpaPodType.Object));
        Assert.That(pod.ObjectType, Is.EqualTo(SpaObjectType.Props));
        Assert.That(pod.Properties, Has.Count.EqualTo(1));
        Assert.That(pod.Properties[0].Key, Is.EqualTo(SpaParamKey.MediaClass));
        Assert.That(pod.Properties[0].Value.AsString(), Is.EqualTo("Audio/Sink"));
    }

    [Test]
    public void Object_Pod_Property_Reads_Back()
    {
        SpaPod pod = SpaPod.Read(SpaPod.Object(SpaObjectType.Format, 0,
            new SpaProperty(SpaParamKey.MediaType, SpaPropFlags.None, SpaPod.Int(1))).Write());

        Assert.That(pod.Properties[0].Value.AsInt(), Is.EqualTo(1));
    }

    [Test]
    public void Choice_Pod_Holds_Its_Default_And_Alternatives()
    {
        byte[] bytes = SpaPod.Choice(SpaChoiceType.Enum, SpaPod.Int(48000), SpaPod.Int(44100)).Write();

        SpaPod pod = SpaPod.Read(bytes);
        Assert.That(pod.Type, Is.EqualTo(SpaPodType.Choice));
        Assert.That(pod.ChoiceType, Is.EqualTo(SpaChoiceType.Enum));
        Assert.That(pod.Children, Has.Count.EqualTo(2));
        Assert.That(pod.Children[0].AsInt(), Is.EqualTo(48000));
        Assert.That(pod.Children[1].AsInt(), Is.EqualTo(44100));
    }

    [Test]
    public void Array_Pod_Holds_Its_Child_And_Values()
    {
        byte[] bytes = SpaPod.ArrayPod(SpaPod.Int(0), SpaPod.Int(10), SpaPod.Int(20)).Write();

        SpaPod pod = SpaPod.Read(bytes);
        Assert.That(pod.Type, Is.EqualTo(SpaPodType.Array));
        Assert.That(pod.Children, Has.Count.EqualTo(3), "the element template comes first");
        Assert.That(pod.Children[0].Type, Is.EqualTo(SpaPodType.Int), "the template names the element type");
        Assert.That(pod.Children[1].AsInt(), Is.EqualTo(10));
        Assert.That(pod.Children[2].AsInt(), Is.EqualTo(20));
    }

    [Test]
    public void Reading_Past_The_End_Throws()
    {
        Assert.Throws<InvalidDataException>(() => SpaPod.Read(new byte[] { 4, 0, 0, 0 }));
    }

    [Test]
    public void Reading_A_Truncated_Pod_Throws()
    {
        byte[] bytes = SpaPod.String("hello").Write();
        Assert.Throws<InvalidDataException>(() => SpaPod.Read(bytes.AsSpan(0, 10)));
    }

    [Test]
    public void Reading_A_Truncated_Struct_Throws()
    {
        byte[] bytes = SpaPod.Struct(SpaPod.Int(1), SpaPod.Int(2)).Write();
        Assert.Throws<InvalidDataException>(() => SpaPod.Read(bytes.AsSpan(0, 20)));
    }

    [Test]
    public void Asking_A_Pod_For_The_Wrong_Type_Throws()
    {
        SpaPod pod = SpaPod.Read(SpaPod.Int(1).Write());

        Assert.Throws<InvalidDataException>(() => pod.AsString());
        Assert.Throws<InvalidDataException>(() => pod.AsBool());
    }

    [Test]
    public void Reading_A_Non_Zero_Padding_Byte_Throws()
    {
        byte[] bytes = SpaPod.Int(1).Write();
        bytes[12] = 0xff; // inside the Int POD's padding

        Assert.Throws<InvalidDataException>(() => SpaPod.Read(bytes));
    }
}
