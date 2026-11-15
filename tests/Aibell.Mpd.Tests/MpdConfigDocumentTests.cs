using System.IO.Compression;
using Aibell.Mpd.Config;

namespace Aibell.Mpd.Tests;

[TestFixture]
public sealed class MpdConfigDocumentTests
{
    private const string Sample = """
        # A comment
        music_directory		"~/Music"   # trailing note
        bind_to_address "localhost"
        bind_to_address "~/.mpd/socket"
        port "6600"

        audio_output {
            type	"alsa"
            name	"My \"quoted\" ALSA"
            # device "hw:1"
        }

        audio_output {
            type "null"
            name "Silent"
        }
        """;

    [Test]
    public void ParseAndSerialize_RoundTripsByteForByte()
    {
        string[] samples =
        [
            Sample, Sample + "\n", Sample.Replace("\n", "\r\n", StringComparison.Ordinal), "﻿" + Sample, string.Empty, "\n\n", "   \t\n",
            "port \"1\"\nbogus line here\n}\naudio_output {\n  type \"x\"\n", "port \"1\"#x\nport  \"2\"  \n",
        ];
        foreach (string text in samples)
            Assert.That(MpdConfigDocument.Parse(text).Serialize(), Is.EqualTo(text));
    }

    [Test]
    public void SystemFiles_RoundTripAndHaveNoErrors()
    {
        string user = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "mpd", "mpd.conf");
        List<string> texts = [];
        if (File.Exists(user)) texts.Add(File.ReadAllText(user));
        const string example = "/usr/share/doc/mpd/mpdconf.example.gz";
        if (File.Exists(example))
        {
            using GZipStream gzip = new(File.OpenRead(example), CompressionMode.Decompress);
            using StreamReader reader = new(gzip);
            texts.Add(reader.ReadToEnd());
        }
        if (texts.Count == 0) Assert.Ignore("No system MPD configuration to read.");
        foreach (string text in texts)
        {
            MpdConfigDocument document = MpdConfigDocument.Parse(text);
            Assert.That(document.Serialize(), Is.EqualTo(text));
            Assert.That(document.Validate().Where(item => item.Severity == MpdConfigSeverity.Error), Is.Empty);
        }
    }

    [Test]
    public void Parse_ReadsSettingsBlocksAndComments()
    {
        MpdConfigDocument document = MpdConfigDocument.Parse(Sample);
        Assert.That(document.Get("music_directory"), Is.EqualTo("~/Music"));
        Assert.That(document.GetAll("bind_to_address"), Is.EqualTo(new[] { "localhost", "~/.mpd/socket" }));
        Assert.That(document.Get("missing"), Is.Null);
        MpdConfigBlock[] outputs = [.. document.BlocksNamed("audio_output")];
        Assert.That(outputs, Has.Length.EqualTo(2));
        Assert.That(outputs[0].Get("name"), Is.EqualTo("My \"quoted\" ALSA"));
        Assert.That(outputs[0].Get("device"), Is.Null, "a commented-out line is not a setting");
        Assert.That(outputs[0].Number, Is.EqualTo(7));
        Assert.That(outputs[1].IsClosed, Is.True);
        Assert.That(document.Validate(), Is.Empty);
    }

    [Test]
    public void Parse_ABackslashEscapesTheNextCharacter()
    {
        MpdConfigDocument document = MpdConfigDocument.Parse("zeroconf_name \"a\\\\b\\nc\"\n");
        Assert.That(document.Get("zeroconf_name"), Is.EqualTo("a\\bnc"));
    }

    [TestCase("port 6600", "Invalid unquoted value")]
    [TestCase("port", "Value missing")]
    [TestCase("port \"6600", "Missing closing")]
    [TestCase("port \"6600\"x", "Space expected")]
    [TestCase("port \"6600\" extra", "Unknown tokens after value")]
    [TestCase("1port \"1\"", "Letter expected")]
    [TestCase("po-rt \"1\"", "Invalid word character")]
    public void Parse_KeepsUnreadableLinesAndReportsThem(string line, string expected)
    {
        string text = line + "\nuser \"me\"\n";
        MpdConfigDocument document = MpdConfigDocument.Parse(text);
        Assert.That(document.Serialize(), Is.EqualTo(text));
        Assert.That(document.Get("user"), Is.EqualTo("me"));
        Assert.That(document.Diagnostics, Has.Count.EqualTo(1));
        Assert.That(document.Diagnostics[0].Line, Is.EqualTo(1));
        if (expected != "Invalid unquoted value") Assert.That(document.Diagnostics[0].Message, Does.Contain(expected));
    }

    [Test]
    public void Parse_ARepeatableSettingWithoutAValueClearsTheEarlierOnes()
    {
        MpdConfigDocument document = MpdConfigDocument.Parse("password \"a@read\"\npassword\npassword \"b@read\"\n");
        Assert.That(document.GetAll("password"), Is.EqualTo(new[] { "b@read" }));
        Assert.That(document.Diagnostics, Is.Empty);
    }

    [Test]
    public void Parse_ReportsBlockProblems()
    {
        Assert.That(MpdConfigDocument.Parse("audio_output {\n type \"null\"\n").Diagnostics.Single().Message, Does.Contain("Expected '}'"));
        Assert.That(MpdConfigDocument.Parse("audio_output { x\n}\n").Diagnostics.Single().Message, Does.Contain("after '{'"));
        Assert.That(MpdConfigDocument.Parse("audio_output {\n}  x\n").Diagnostics.Single().Message, Does.Contain("after '}'"));
        MpdConfigDocument nested = MpdConfigDocument.Parse("audio_output {\n  decoder {\n  type \"null\"\n}\n}\n");
        Assert.That(nested.Diagnostics, Is.Not.Empty);
        Assert.That(MpdConfigDocument.Parse("a { # note\n}\n").Diagnostics, Is.Empty);
    }

    [Test]
    public void Set_ChangesOnlyTheValueAndKeepsAlignmentAndComments()
    {
        MpdConfigDocument document = MpdConfigDocument.Parse(Sample);
        document.Set("music_directory", "/srv/music \"x\"");
        Assert.That(document.Serialize(), Does.Contain("music_directory\t\t\"/srv/music \\\"x\\\"\"   # trailing note\n"));
        document.Set("music_directory", "/srv/music \"x\"");
        document.Set("port", "6601");
        Assert.That(document.Get("port"), Is.EqualTo("6601"));
        Assert.That(MpdConfigDocument.Parse(document.Serialize()).Get("music_directory"), Is.EqualTo("/srv/music \"x\""));
    }

    [Test]
    public void Set_AddsNewSettingsAfterTheOtherSettingsAndRemovesWithNull()
    {
        MpdConfigDocument document = MpdConfigDocument.Parse(Sample);
        document.Set("state_file", "~/.mpd/state");
        string text = document.Serialize();
        Assert.That(text.IndexOf("state_file", StringComparison.Ordinal), Is.GreaterThan(text.IndexOf("port", StringComparison.Ordinal)).And.LessThan(text.IndexOf("audio_output", StringComparison.Ordinal)));
        document.Set("state_file", null);
        Assert.That(document.Serialize(), Is.EqualTo(Sample));
        document.Set("nothing", null);
        Assert.Throws<ArgumentException>(() => document.Set("bad name", "x"));
        Assert.Throws<ArgumentException>(() => document.Set("user", "line\nbreak"));
    }

    [Test]
    public void Set_OnAnEmptyOrUnterminatedFile_BuildsAValidFile()
    {
        MpdConfigDocument empty = MpdConfigDocument.CreateEmpty();
        empty.Set("port", "6600");
        Assert.That(empty.Serialize(), Is.EqualTo("port \"6600\"\n"));
        MpdConfigDocument unterminated = MpdConfigDocument.Parse("port \"1\"");
        unterminated.Set("user", "me");
        Assert.That(unterminated.Serialize(), Is.EqualTo("port \"1\"\nuser \"me\"\n"));
        MpdConfigDocument crlf = MpdConfigDocument.Parse("port \"1\"\r\n");
        crlf.Set("user", "me");
        Assert.That(crlf.Serialize(), Is.EqualTo("port \"1\"\r\nuser \"me\"\r\n"));
        MpdConfigDocument blocksOnly = MpdConfigDocument.Parse("audio_output {\n}\n");
        blocksOnly.Set("user", "me");
        Assert.That(blocksOnly.Serialize(), Does.StartWith("user \"me\"\naudio_output"));
    }

    [Test]
    public void SetAll_ReusesLinesAddsAndRemoves()
    {
        MpdConfigDocument document = MpdConfigDocument.Parse(Sample);
        document.SetAll("bind_to_address", ["any"]);
        Assert.That(document.GetAll("bind_to_address"), Is.EqualTo(new[] { "any" }));
        document.SetAll("bind_to_address", ["a", "b", "c"]);
        Assert.That(document.GetAll("bind_to_address"), Is.EqualTo(new[] { "a", "b", "c" }));
        document.SetAll("bind_to_address", []);
        Assert.That(document.GetAll("bind_to_address"), Is.Empty);
        document.SetAll("password", ["x@read", "y@read"]);
        Assert.That(document.GetAll("password"), Is.EqualTo(new[] { "x@read", "y@read" }));
        Assert.Throws<ArgumentNullException>(() => document.SetAll("password", null!));
    }

    [Test]
    public void Blocks_CanBeAddedEditedMovedAndRemoved()
    {
        MpdConfigDocument document = MpdConfigDocument.Parse(Sample);
        MpdConfigBlock added = document.AddBlock("audio_output");
        added.Set("type", "pipewire");
        added.Set("name", "PW");
        added.Set("target", "x");
        added.Set("name", "PipeWire");
        Assert.That(added.GetAll("target"), Is.EqualTo(new[] { "x" }));
        added.Set("target", null);
        Assert.That(added.Get("target"), Is.Null);
        Assert.That(MpdConfigDocument.Parse(document.Serialize()).BlocksNamed("audio_output").Last().Get("name"), Is.EqualTo("PipeWire"));
        Assert.That(document.Move(added, -1), Is.True);
        Assert.That(document.BlocksNamed("audio_output").ElementAt(1), Is.SameAs(added));
        Assert.That(document.Move(added, -1), Is.True);
        Assert.That(document.Move(added, -1), Is.False);
        Assert.That(document.Move(added, 0), Is.False);
        Assert.That(document.Move(added, 1), Is.True);
        Assert.That(document.Remove(added), Is.True);
        Assert.That(document.Remove(added), Is.False);
        Assert.That(document.Serialize(), Is.EqualTo(Sample + "\n"));
    }

    [Test]
    public void AddBlock_FollowsTheLastLineAndSeparatesWithABlankLine()
    {
        MpdConfigDocument document = MpdConfigDocument.Parse("port \"1\"");
        document.AddBlock("input_cache").Set("size", "1 GB");
        Assert.That(document.Serialize(), Is.EqualTo("port \"1\"\n\ninput_cache {\n    size \"1 GB\"\n}\n"));
        MpdConfigDocument open = MpdConfigDocument.Parse("a {\n");
        open.AddBlock("input_cache");
        Assert.That(open.Serialize(), Does.Contain("a {\n\ninput_cache {"));
        Assert.Throws<ArgumentException>(() => document.AddBlock("1x"));
    }

    [Test]
    public void Remove_DeletesATopLevelLineAndBlockLine()
    {
        MpdConfigDocument document = MpdConfigDocument.Parse(Sample);
        Assert.That(document.Remove(document.Settings.First(item => item.Name == "port")), Is.True);
        Assert.That(document.Get("port"), Is.Null);
        MpdConfigBlock block = document.Blocks.First();
        Assert.That(block.Remove(block.Settings.First()), Is.True);
        Assert.That(block.Get("type"), Is.Null);
    }

    [Test]
    public void Includes_AreListedAndKept()
    {
        MpdConfigDocument document = MpdConfigDocument.Parse("include \"a.conf\"\ninclude_optional \"b.conf\"\nport \"1\"\n");
        Assert.That(document.Includes.Select(item => item.Value), Is.EqualTo(new[] { "a.conf", "b.conf" }));
        Assert.That(document.Validate(), Is.Empty);
        Assert.That(MpdConfigDocument.Parse("include\n").Validate().Single().Message, Does.Contain("Value missing"));
    }

    [Test]
    public void Validate_ReportsWhatMpdWouldReject()
    {
        MpdConfigDocument document = MpdConfigDocument.Parse(
            "bogus \"1\"\nhttp_proxy_host \"h\"\ndatabase {\n}\ndatabase {\n}\nfoo {\n}\nweird {\n}\naudio_output {\n type \"null\"\n type \"alsa\"\n}\npartition {\n}\n");
        string[] messages = [.. document.Validate().Select(item => item.Message)];
        Assert.That(messages.Any(message => message.StartsWith("Unrecognized parameter: bogus", StringComparison.Ordinal)), Is.True);
        Assert.That(messages, Does.Contain("http_proxy_host is deprecated"));
        Assert.That(messages, Does.Contain("database may appear only once"));
        Assert.That(messages.Any(message => message.StartsWith("Unrecognized parameter: foo", StringComparison.Ordinal)), Is.True);
        Assert.That(document.Validate().Where(item => item.Message.Contains("bogus", StringComparison.Ordinal)).Select(item => item.Severity), Is.All.EqualTo(MpdConfigSeverity.Warning));
        Assert.That(messages, Does.Contain("type is duplicate"));
        Assert.That(MpdConfigDocument.Parse("restore_paused \"yes\"\n").Validate(), Is.Empty);
    }

    [Test]
    public void Validate_ReportsDeprecatedBlocksAndSeverity()
    {
        MpdConfigDocument document = MpdConfigDocument.Parse("http_proxy_host \"h\"\nport \"x\"\n");
        Assert.That(document.Validate().Single().Severity, Is.EqualTo(MpdConfigSeverity.Warning));
    }

    [Test]
    public void Syntax_QuotesNamesAndBooleans()
    {
        Assert.That(MpdConfigSyntax.Quote("a\\b\"c"), Is.EqualTo("\"a\\\\b\\\"c\""));
        Assert.Throws<ArgumentException>(() => MpdConfigSyntax.Quote("a\0b"));
        Assert.Throws<ArgumentNullException>(() => MpdConfigSyntax.Quote(null!));
        Assert.That(MpdConfigSyntax.IsValidName("audio_output2"), Is.True);
        Assert.That(MpdConfigSyntax.IsValidName("2x"), Is.False);
        Assert.That(MpdConfigSyntax.IsValidName("a-b"), Is.False);
        Assert.That(MpdConfigSyntax.IsValidName(null), Is.False);
        Assert.That(MpdConfigSyntax.ParseBoolean("YES"), Is.True);
        Assert.That(MpdConfigSyntax.ParseBoolean("true"), Is.True);
        Assert.That(MpdConfigSyntax.ParseBoolean("1"), Is.True);
        Assert.That(MpdConfigSyntax.ParseBoolean("No"), Is.False);
        Assert.That(MpdConfigSyntax.ParseBoolean("false"), Is.False);
        Assert.That(MpdConfigSyntax.ParseBoolean("0"), Is.False);
        Assert.That(MpdConfigSyntax.ParseBoolean("maybe"), Is.Null);
        Assert.That(MpdConfigSyntax.ParseBoolean(null), Is.Null);
    }

    [Test]
    public void ToString_IsTheSerializedText() => Assert.That(MpdConfigDocument.Parse(Sample).ToString(), Is.EqualTo(Sample));

    [Test]
    public void Setting_ValueCanBeClearedAndRestored()
    {
        MpdConfigDocument document = MpdConfigDocument.Parse("password \"x@read\"  # c\n");
        MpdConfigSetting setting = document.Settings.Single();
        setting.Value = null;
        Assert.That(setting.Text, Is.EqualTo("password"));
        setting.Value = "y@read";
        Assert.That(document.Serialize(), Is.EqualTo("password \"y@read\"\n"));
        setting.Value = "z@read";
        Assert.That(document.Serialize(), Is.EqualTo("password \"z@read\"\n"));
    }
}
