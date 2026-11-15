using Aibell.Mpd.Config;

namespace Aibell.Mpd.Tests;

[TestFixture]
public sealed class MpdSettingCatalogTests
{
    /// <summary>The names MPD's own option tables define, so a setting it accepts is never missing from the editor.</summary>
    private static readonly string[] KnownSettings =
    [
        "music_directory", "playlist_directory", "follow_inside_symlinks", "follow_outside_symlinks", "db_file", "sticker_file", "log_file",
        "pid_file", "state_file", "state_file_interval", "restore_paused", "user", "group", "bind_to_address", "port", "log_level",
        "zeroconf_name", "zeroconf_enabled", "password", "host_permissions", "local_permissions", "default_permissions", "audio_output_format",
        "mixer_type", "replaygain", "replaygain_preamp", "replaygain_missing_preamp", "replaygain_limit", "volume_normalization",
        "samplerate_converter", "audio_buffer_size", "buffer_before_play", "http_proxy_host", "http_proxy_port", "http_proxy_user",
        "http_proxy_password", "connection_timeout", "max_connections", "max_playlist_length", "max_command_list_size", "max_output_buffer_size",
        "filesystem_charset", "id3v1_encoding", "metadata_to_use", "save_absolute_paths_in_playlists", "gapless_mp3_playback", "auto_update",
        "auto_update_depth", "mixramp_analyzer", "inhibit_idle",
    ];

    private static readonly string[] KnownBlocks =
    [
        "audio_output", "decoder", "input", "input_cache", "archive_plugin", "playlist_plugin", "resampler", "filter", "database", "neighbors", "partition",
    ];

    [Test]
    public void Settings_CoverEveryOptionMpdDefines()
    {
        Assert.That(MpdSettingCatalog.Settings.Select(item => item.Name), Is.EquivalentTo(KnownSettings));
        Assert.That(MpdSettingCatalog.Blocks.Select(item => item.Name), Is.EquivalentTo(KnownBlocks));
    }

    [Test]
    public void RepeatableFlags_MatchMpd()
    {
        string[] repeatable = [.. MpdSettingCatalog.Settings.Where(item => item.Repeatable).Select(item => item.Name)];
        Assert.That(repeatable, Is.EquivalentTo(new[] { "bind_to_address", "password", "host_permissions" }));
        string[] blocks = [.. MpdSettingCatalog.Blocks.Where(item => item.Repeatable).Select(item => item.Name)];
        Assert.That(blocks, Is.EquivalentTo(new[] { "audio_output", "decoder", "input", "archive_plugin", "playlist_plugin", "filter", "neighbors", "partition" }));
    }

    [Test]
    public void Definitions_AreWellFormed()
    {
        Assert.That(MpdSettingCatalog.Settings.Select(item => item.Name), Is.Unique);
        foreach (MpdSettingDefinition setting in MpdSettingCatalog.Settings) AssertField(setting);
        foreach (MpdBlockDefinition block in MpdSettingCatalog.Blocks)
        {
            Assert.That(block.Plugins.Select(item => item.Name), Is.Unique, block.Name);
            Assert.That(block.CommonFields.Select(item => item.Name), Is.Unique, block.Name);
            Assert.That(block.PluginKey is null, Is.EqualTo(block.Plugins.Count == 0), block.Name);
            foreach (MpdSettingDefinition field in block.CommonFields) AssertField(field);
            foreach (MpdPluginDefinition plugin in block.Plugins)
            {
                Assert.That(plugin.Fields.Select(item => item.Name), Is.Unique, $"{block.Name}/{plugin.Name}");
                foreach (MpdSettingDefinition field in plugin.Fields) AssertField(field);
            }
        }
        foreach (MpdSettingDefinition field in MpdSettingCatalog.Encoders.SelectMany(item => item.Fields)) AssertField(field);
    }

    private static void AssertField(MpdSettingDefinition field)
    {
        Assert.That(MpdConfigSyntax.IsValidName(field.Name), Is.True, field.Name);
        if (field.Kind == MpdValueKind.Choice) Assert.That(field.Choices, Is.Not.Null.And.Not.Empty, field.Name);
        if (field.Minimum is { } minimum && field.Maximum is { } maximum) Assert.That(minimum, Is.LessThan(maximum), field.Name);
    }

    [Test]
    public void Lookups_FindByNameAndPlugin()
    {
        Assert.That(MpdSettingCatalog.FindSetting("port")!.Kind, Is.EqualTo(MpdValueKind.WholeNumber));
        Assert.That(MpdSettingCatalog.FindSetting("nope"), Is.Null);
        MpdBlockDefinition output = MpdSettingCatalog.FindBlock("audio_output")!;
        Assert.That(output.PluginKey, Is.EqualTo("type"));
        Assert.That(output.FindPlugin("alsa")!.Fields.Select(item => item.Name), Does.Contain("mixer_control"));
        Assert.That(output.FindPlugin("nope"), Is.Null);
        Assert.That(MpdSettingCatalog.FindBlock("nope"), Is.Null);
        Assert.That(MpdSettingCatalog.PluginKeyDefinition("plugin").Name, Is.EqualTo("plugin"));
        Assert.That(MpdSettingCatalog.PluginKeyDefinition("type").Name, Is.EqualTo("type"));
        Assert.That(MpdSettingCatalog.BooleanChoices, Is.EqualTo(new[] { "yes", "no" }));
        Assert.That(MpdSettingCatalog.Permissions, Does.Contain("admin"));
    }

    [Test]
    public void FieldsFor_CombinesCommonPluginAndEncoderFieldsWithoutDuplicates()
    {
        MpdBlockDefinition output = MpdSettingCatalog.FindBlock("audio_output")!;
        string[] shout = [.. MpdSettingCatalog.FieldsFor(output, "shout", "opus").Select(item => item.Name)];
        Assert.That(shout, Does.Contain("name").And.Contain("host").And.Contain("complexity"));
        Assert.That(shout, Is.Unique);
        Assert.That(MpdSettingCatalog.FieldsFor(output, "alsa").Select(item => item.Name), Does.Not.Contain("complexity"));
        Assert.That(MpdSettingCatalog.FieldsFor(output, "unknown", "unknown").Select(item => item.Name), Is.EqualTo(output.CommonFields.Select(item => item.Name)));
        Assert.That(MpdSettingCatalog.FieldsFor(MpdSettingCatalog.FindBlock("decoder")!, "flac", "opus").Select(item => item.Name), Is.EqualTo(new[] { "enabled" }));
        Assert.Throws<ArgumentNullException>(() => MpdSettingCatalog.FieldsFor(null!, null));
    }
}
