using Aibell.Mpd.Config;
using Aibell.Mpd.Config.Editing;

namespace Aibell.Mpd.Tests;

[TestFixture]
public sealed class MpdConfigEditorTests
{
    private string directory = string.Empty;

    [SetUp]
    public void CreateDirectory() => directory = Directory.CreateTempSubdirectory("aibell-mpd-editor-").FullName;

    [TearDown]
    public void DeleteDirectory() => Directory.Delete(directory, recursive: true);

    private MpdConfigEditor Open(string text)
    {
        string path = Path.Combine(directory, "mpd.conf");
        File.WriteAllText(path, text);
        MpdConfigEditor editor = new();
        editor.Load(path);
        return editor;
    }

    [Test]
    public void Fields_ReadAndWriteTheDocument()
    {
        MpdConfigEditor editor = Open("music_directory \"~/Music\"\nport \"6600\"\n");
        int changes = 0;
        editor.Changed += (_, _) => changes++;
        MpdFieldEditor music = editor.Fields(MpdSettingGroup.Files).Single(item => item.Name == "music_directory");
        Assert.That(music.Value, Is.EqualTo("~/Music"));
        Assert.That(music.IsSet, Is.True);
        music.SetValue("/srv/music");
        music.SetValue("/srv/music");
        Assert.That(changes, Is.EqualTo(1));
        Assert.That(editor.Document.Get("music_directory"), Is.EqualTo("/srv/music"));
        MpdFieldEditor state = editor.Fields(MpdSettingGroup.Process).Single(item => item.Name == "state_file");
        Assert.That(state.IsSet, Is.False);
        state.SetValue("~/.mpd/state");
        state.SetValue("");
        Assert.That(editor.Document.Get("state_file"), Is.Null);
        Assert.That(editor.IsDirty, Is.True);
    }

    [Test]
    public void Fields_CoverEverySingleValueSettingExactlyOnce()
    {
        MpdConfigEditor editor = new();
        string[] shown = [.. Enum.GetValues<MpdSettingGroup>().SelectMany(group => editor.Fields(group)).Select(item => item.Name)];
        Assert.That(shown, Is.Unique);
        Assert.That(shown, Is.EquivalentTo(MpdSettingCatalog.Settings.Where(item => !item.Repeatable).Select(item => item.Name)));
    }

    [Test]
    public void Booleans_AreThreeStateAndWriteYesOrNo()
    {
        MpdConfigEditor editor = Open("restore_paused \"true\"\n");
        MpdFieldEditor field = editor.Fields(MpdSettingGroup.Process).Single(item => item.Name == "restore_paused");
        Assert.That(field.BooleanValue, Is.True);
        field.SetBoolean(false);
        Assert.That(editor.Document.Get("restore_paused"), Is.EqualTo("no"));
        field.SetBoolean(true);
        Assert.That(editor.Document.Get("restore_paused"), Is.EqualTo("yes"));
        field.SetBoolean(null);
        Assert.That(editor.Document.Get("restore_paused"), Is.Null);
        Assert.That(field.BooleanValue, Is.Null);
        Assert.That(field.Default, Is.EqualTo("no"));
    }

    [TestCase("port", "6600", null)]
    [TestCase("port", "abc", "NotNumber")]
    [TestCase("port", "70000", "OutOfRange")]
    [TestCase("port", "0", "OutOfRange")]
    [TestCase("replaygain_preamp", "-3.5", null)]
    [TestCase("replaygain_preamp", "20", "OutOfRange")]
    [TestCase("replaygain_preamp", "nan", "NotNumber")]
    [TestCase("replaygain_preamp", "x", "NotNumber")]
    [TestCase("restore_paused", "maybe", "NotYesNo")]
    [TestCase("restore_paused", "No", null)]
    [TestCase("log_level", "verbose", null)]
    [TestCase("log_level", "loud", "NotChoice")]
    [TestCase("audio_output_format", "44100:16:2", null)]
    [TestCase("audio_output_format", "*:*:*", null)]
    [TestCase("audio_output_format", "48000:16:*", null)]
    [TestCase("audio_output_format", "dsd64:2", null)]
    [TestCase("audio_output_format", "::2", null)]
    [TestCase("audio_output_format", "44100:15:2", "NotAudioFormat")]
    [TestCase("audio_output_format", "44100:16", "NotAudioFormat")]
    [TestCase("music_directory", "any text at all", null)]
    [TestCase("music_directory", "line\nbreak", "LineBreak")]
    public void Validate_ChecksValuesAgainstTheKind(string setting, string value, string? expected) =>
        Assert.That(MpdFieldEditor.Validate(MpdSettingCatalog.FindSetting(setting)!, value), Is.EqualTo(expected));

    [Test]
    public void Validate_IgnoresUnsetValuesAndRejectsNull()
    {
        Assert.That(MpdFieldEditor.Validate(MpdSettingCatalog.FindSetting("port")!, null), Is.Null);
        Assert.Throws<ArgumentNullException>(() => MpdFieldEditor.Validate(null!, "x"));
    }

    [Test]
    public void Lines_EditRepeatableSettings()
    {
        MpdConfigEditor editor = Open("bind_to_address \"localhost\"\n");
        Assert.That(editor.Lines("bind_to_address"), Is.EqualTo(new[] { "localhost" }));
        int changes = 0;
        editor.Changed += (_, _) => changes++;
        editor.SetLines("bind_to_address", ["localhost"]);
        Assert.That(changes, Is.Zero);
        editor.SetLines("bind_to_address", ["  192.168.1.2 ", "", "~/.mpd/socket"]);
        Assert.That(editor.Lines("bind_to_address"), Is.EqualTo(new[] { "192.168.1.2", "~/.mpd/socket" }));
        Assert.That(changes, Is.EqualTo(1));
        Assert.Throws<ArgumentNullException>(() => editor.SetLines("password", null!));
    }

    [Test]
    public void Blocks_AreAddedEditedMovedAndRemoved()
    {
        MpdConfigEditor editor = Open("audio_output {\n    type \"null\"\n    name \"One\"\n}\n");
        MpdBlockEditor one = editor.Blocks("audio_output").Single();
        Assert.That(one.Title, Is.EqualTo("One (null)"));
        Assert.That(one.PluginKey, Is.EqualTo("type"));
        Assert.That(one.Plugin, Is.EqualTo("null"));
        MpdBlockEditor two = editor.AddBlock("audio_output", "alsa");
        Assert.That(two.Title, Is.EqualTo("alsa"));
        two.Fields.Single(item => item.Name == "name").SetValue("Speakers");
        two.Fields.Single(item => item.Name == "device").SetValue("hw:1");
        Assert.That(two.Title, Is.EqualTo("Speakers (alsa)"));
        Assert.That(editor.Blocks("audio_output").Select(item => item.Title), Is.EqualTo(new[] { "One (null)", "Speakers (alsa)" }));
        Assert.That(editor.MoveBlock(two, -1), Is.True);
        Assert.That(editor.MoveBlock(two, -1), Is.False);
        Assert.That(editor.Blocks("audio_output")[0].Title, Is.EqualTo("Speakers (alsa)"));
        editor.RemoveBlock(editor.Blocks("audio_output")[0]);
        Assert.That(editor.Blocks("audio_output").Single().Title, Is.EqualTo("One (null)"));
        Assert.Throws<ArgumentNullException>(() => editor.RemoveBlock(null!));
        Assert.Throws<ArgumentNullException>(() => editor.MoveBlock(null!, 1));
    }

    [Test]
    public void BlockEditor_OffersTheFieldsOfThePluginAndKeepsUnknownSettings()
    {
        MpdConfigEditor editor = Open("audio_output {\n    type \"httpd\"\n    name \"Stream\"\n    encoder \"opus\"\n    custom_thing \"1\"\n    port \"8000\"\n}\n");
        MpdBlockEditor output = editor.Blocks("audio_output").Single();
        Assert.That(output.Encoder, Is.EqualTo("opus"));
        string[] names = [.. output.Fields.Select(item => item.Name)];
        Assert.That(names, Does.Contain("max_clients").And.Contain("complexity").And.Contain("name"));
        Assert.That(output.OtherSettings.Select(item => item.Name), Is.EqualTo(new[] { "custom_thing" }));
        output.SetOther("custom_thing", "2");
        output.SetOther("another", "x");
        Assert.That(output.OtherSettings.Select(item => item.Value), Is.EqualTo(new[] { "2", "x" }));
        output.RemoveSetting(output.OtherSettings[0]);
        Assert.That(output.OtherSettings.Single().Name, Is.EqualTo("another"));
        output.SetPlugin("alsa");
        Assert.That(output.Plugin, Is.EqualTo("alsa"));
        Assert.That(output.OtherSettings.Select(item => item.Name), Is.EqualTo(new[] { "encoder", "port", "another" }), "settings of the old plugin stay visible so nothing is lost silently");
        output.SetPlugin("alsa");
        output.SetPlugin(null);
        Assert.That(output.Plugin, Is.Null);
    }

    [Test]
    public void BlockEditor_HandlesBlocksWithoutPluginsAndUnknownBlocks()
    {
        MpdConfigEditor editor = Open("input_cache {\n    size \"1 GB\"\n}\nweird {\n}\nplaylist_plugin {\n    name \"m3u\"\n    enabled \"no\"\n}\n");
        MpdBlockEditor cache = editor.Blocks("input_cache").Single();
        Assert.That(cache.PluginKey, Is.Null);
        Assert.That(cache.Plugin, Is.Null);
        Assert.That(cache.Title, Is.EqualTo("input_cache"));
        cache.SetPlugin("x");
        Assert.That(cache.Fields.Single().Name, Is.EqualTo("size"));
        Assert.That(editor.UnknownBlocks.Single().Name, Is.EqualTo("weird"));
        MpdBlockEditor unknown = new(editor.UnknownBlocks[0], null, () => { });
        Assert.That(unknown.Fields, Is.Empty);
        Assert.That(unknown.Title, Is.EqualTo("weird"));
        Assert.That(editor.Blocks("playlist_plugin").Single().Title, Is.EqualTo("m3u"));
        Assert.That(editor.Blocks("partition").Count, Is.Zero);
        MpdBlockEditor partition = editor.AddBlock("partition");
        partition.SetOther("name", "kitchen");
        Assert.That(partition.Title, Is.EqualTo("kitchen"));
    }

    [Test]
    public void OtherSettingsAndIncludes_AreListed()
    {
        MpdConfigEditor editor = Open("include \"a.conf\"\ninclude_optional \"b.conf\"\nfuture_option \"1\"\nport \"6600\"\n");
        Assert.That(editor.Includes.Select(item => item.Value), Is.EqualTo(new[] { "a.conf", "b.conf" }));
        Assert.That(editor.OtherSettings.Select(item => item.Name), Is.EqualTo(new[] { "future_option" }));
        editor.SetOther("future_option", "2");
        editor.SetOther("another_one", "x");
        Assert.That(editor.OtherSettings.Select(item => item.Value), Is.EqualTo(new[] { "2", "x" }));
        editor.RemoveSetting(editor.OtherSettings[0]);
        Assert.That(editor.OtherSettings.Single().Name, Is.EqualTo("another_one"));
    }

    [Test]
    public void SetText_ReplacesTheDocumentAndKeepsTheBaseline()
    {
        MpdConfigEditor editor = Open("port \"6600\"\n");
        int reloads = 0;
        int changes = 0;
        editor.Reloaded += (_, _) => reloads++;
        editor.Changed += (_, _) => changes++;
        editor.SetText("port \"6600\"\n");
        Assert.That((reloads, changes), Is.EqualTo((0, 0)));
        editor.SetText("port \"6601\"\n");
        Assert.That((reloads, changes), Is.EqualTo((1, 1)));
        Assert.That(editor.IsDirty, Is.True);
        Assert.That(editor.Text, Is.EqualTo("port \"6601\"\n"));
        editor.SetText("port \"6600\"\n");
        Assert.That(editor.IsDirty, Is.False);
        Assert.Throws<ArgumentNullException>(() => editor.SetText(null!));
    }

    [Test]
    public void Problems_CombineParserFieldAndPluginChecks()
    {
        MpdConfigEditor editor = Open("port \"abc\"\nbogus \"1\"\naudio_output {\n    name \"No plugin\"\n}\naudio_output {\n    type \"alsa\"\n    buffer_time \"x\"\n}\nconnection_timeout \"60\"\n");
        string[] codes = [.. editor.Problems.Select(item => item.Code ?? "-")];
        Assert.That(codes, Does.Contain("NotNumber").And.Contain("PluginMissing"));
        Assert.That(editor.Problems.Any(item => item.Subject == "audio_output/buffer_time"), Is.True);
        Assert.That(editor.Problems.Any(item => item.Message.Contains("bogus", StringComparison.Ordinal)), Is.True);
        Assert.That(editor.CanSave, Is.False);
        Assert.Throws<InvalidOperationException>(() => editor.Save());
        editor.SetText("port \"6600\"\nunknown_block {\n}\n");
        Assert.That(editor.CanSave, Is.True, "an unknown block is only a warning: a newer MPD may know it");
        Assert.That(editor.Problems.Any(item => item.Severity == MpdConfigSeverity.Warning && item.Message.Contains("unknown_block", StringComparison.Ordinal)), Is.True);
    }

    [Test]
    public void SaveAndSaveAs_WriteWithABackupAndTrackTheFile()
    {
        MpdConfigEditor editor = Open("port \"6600\"\n");
        Assert.That(editor.IsReadOnly, Is.False);
        editor.Fields(MpdSettingGroup.Network).Single(item => item.Name == "port").SetValue("6601");
        Assert.That(editor.IsDirty, Is.True);
        string? backup = editor.Save();
        Assert.That(backup, Is.Not.Null);
        Assert.That(File.ReadAllText(backup!), Is.EqualTo("port \"6600\"\n"));
        Assert.That(File.ReadAllText(editor.Path!), Is.EqualTo("port \"6601\"\n"));
        Assert.That(editor.IsDirty, Is.False);
        string copy = Path.Combine(directory, "copy", "mpd.conf");
        Assert.That(editor.SaveAs(copy), Is.Null);
        Assert.That(editor.Path, Is.EqualTo(copy));
        Assert.Throws<ArgumentException>(() => editor.SaveAs(""));
    }

    [Test]
    public void StartNew_BeginsEmptyAndSaveRequiresAPath()
    {
        MpdConfigEditor fresh = new();
        Assert.Throws<InvalidOperationException>(() => fresh.Save());
        string path = Path.Combine(directory, "new", "mpd.conf");
        int reloads = 0;
        fresh.Reloaded += (_, _) => reloads++;
        fresh.StartNew(path);
        Assert.That(reloads, Is.EqualTo(1));
        Assert.That(fresh.Text, Is.Empty);
        Assert.That(fresh.IsDirty, Is.False);
        fresh.Fields(MpdSettingGroup.Network).Single(item => item.Name == "port").SetValue("6600");
        Assert.That(fresh.Save(), Is.Null);
        Assert.That(File.ReadAllText(path), Is.EqualTo("port \"6600\"\n"));
    }

    [Test]
    public void Load_MarksAFileTheUserCannotWriteAsReadOnly()
    {
        if (Environment.UserName == "root") Assert.Ignore("Needs a non-root user.");
        if (OperatingSystem.IsWindows()) { Assert.Ignore("Unix permissions only."); return; }
        string path = Path.Combine(directory, "locked.conf");
        File.WriteAllText(path, "port \"6600\"\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead);
        MpdConfigEditor editor = new();
        editor.Load(path);
        Assert.That(editor.IsReadOnly, Is.True);
        MpdConfigEditor fresh = new();
        fresh.StartNew("/proc/aibell-no/mpd.conf");
        Assert.That(fresh.IsReadOnly, Is.True);
    }
}
