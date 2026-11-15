namespace Aibell.Mpd.Config;

/// <summary>
/// Every top-level setting and block of <c>mpd.conf</c> with the settings of each plugin, written from MPD's user manual, its plugin
/// reference and its option tables. Names are MPD's own identifiers and are shown as they are. A name missing here can still be edited
/// as a plain name/value pair, so a newer MPD never locks a setting out.
/// </summary>
public static class MpdSettingCatalog
{
    private static readonly string[] YesNo = ["yes", "no"];

    private static readonly string[] EncoderNames = ["vorbis", "opus", "lame", "shine", "twolame", "flac", "wave", "null"];

    private static MpdSettingDefinition Text(string name, string? fallback = null) => new(name, MpdValueKind.Text, Default: fallback);

    private static MpdSettingDefinition FilePath(string name, string? fallback = null) => new(name, MpdValueKind.Path, Default: fallback);

    private static MpdSettingDefinition Flag(string name, string? fallback = null) => new(name, MpdValueKind.Boolean, Default: fallback);

    private static MpdSettingDefinition Number(string name, double? minimum = null, double? maximum = null, string? fallback = null) =>
        new(name, MpdValueKind.WholeNumber, Default: fallback, Minimum: minimum, Maximum: maximum);

    private static MpdSettingDefinition Real(string name, double? minimum = null, double? maximum = null, string? fallback = null) =>
        new(name, MpdValueKind.RealNumber, Default: fallback, Minimum: minimum, Maximum: maximum);

    private static MpdSettingDefinition Pick(string name, string[] choices, string? fallback = null) =>
        new(name, MpdValueKind.Choice, Default: fallback, Choices: choices);

    private static MpdSettingDefinition Encoder(string? fallback = null) =>
        new("encoder", MpdValueKind.Choice, Default: fallback, Choices: EncoderNames, OpenChoices: true);

    private static MpdSettingDefinition Secret(string name) => new(name, MpdValueKind.Secret);

    private static MpdSettingDefinition Format(string name) => new(name, MpdValueKind.AudioFormat);

    private static MpdSettingDefinition In(this MpdSettingDefinition item, MpdSettingGroup group) => item with { Group = group };

    private static MpdSettingDefinition Many(this MpdSettingDefinition item) => item with { Repeatable = true };

    private static MpdSettingDefinition Old(this MpdSettingDefinition item) => item with { Deprecated = true };

    private static MpdPluginDefinition Plugin(string name, params MpdSettingDefinition[] fields) => new(name, fields);

    /// <summary>The top-level settings, in the order the editor lists them.</summary>
    public static IReadOnlyList<MpdSettingDefinition> Settings { get; } =
    [
        FilePath("music_directory").In(MpdSettingGroup.Files),
        FilePath("playlist_directory").In(MpdSettingGroup.Files),
        Flag("follow_inside_symlinks", "yes").In(MpdSettingGroup.Files),
        Flag("follow_outside_symlinks", "yes").In(MpdSettingGroup.Files),
        Text("filesystem_charset").In(MpdSettingGroup.Files),
        Flag("save_absolute_paths_in_playlists", "no").In(MpdSettingGroup.Files),
        FilePath("db_file").In(MpdSettingGroup.Database),
        Flag("auto_update", "no").In(MpdSettingGroup.Database),
        Number("auto_update_depth", 0).In(MpdSettingGroup.Database),
        FilePath("sticker_file").In(MpdSettingGroup.Database),
        FilePath("log_file").In(MpdSettingGroup.Process),
        Pick("log_level", ["error", "warning", "notice", "info", "verbose"], "notice").In(MpdSettingGroup.Process),
        FilePath("pid_file").In(MpdSettingGroup.Process),
        FilePath("state_file").In(MpdSettingGroup.Process),
        Number("state_file_interval", 0, fallback: "120").In(MpdSettingGroup.Process),
        Flag("restore_paused", "no").In(MpdSettingGroup.Process),
        Text("user").In(MpdSettingGroup.Process),
        Text("group").In(MpdSettingGroup.Process),
        Flag("inhibit_idle", "no").In(MpdSettingGroup.Process),
        Text("bind_to_address", "any").Many().In(MpdSettingGroup.Network),
        Number("port", 1, 65535, "6600").In(MpdSettingGroup.Network),
        Number("connection_timeout", 1, fallback: "60").In(MpdSettingGroup.Limits),
        Number("max_connections", 1, fallback: "100").In(MpdSettingGroup.Limits),
        Number("max_playlist_length", 1, fallback: "16384").In(MpdSettingGroup.Limits),
        Number("max_command_list_size", 1, fallback: "2048").In(MpdSettingGroup.Limits),
        Number("max_output_buffer_size", 1, fallback: "8192").In(MpdSettingGroup.Limits),
        Text("audio_buffer_size", "4 MB").In(MpdSettingGroup.Limits),
        Secret("password").Many().In(MpdSettingGroup.Permissions),
        Text("default_permissions", "read,add,control,admin").In(MpdSettingGroup.Permissions),
        Text("local_permissions").In(MpdSettingGroup.Permissions),
        Text("host_permissions").Many().In(MpdSettingGroup.Permissions),
        Format("audio_output_format").In(MpdSettingGroup.Audio),
        Pick("mixer_type", ["hardware", "software", "null", "none"]).In(MpdSettingGroup.Audio),
        Text("samplerate_converter").In(MpdSettingGroup.Audio),
        Flag("volume_normalization", "no").In(MpdSettingGroup.Audio),
        Flag("mixramp_analyzer", "no").In(MpdSettingGroup.Audio),
        Pick("replaygain", ["off", "track", "album", "auto"], "off").In(MpdSettingGroup.ReplayGain),
        Real("replaygain_preamp", -15, 15, "0").In(MpdSettingGroup.ReplayGain),
        Real("replaygain_missing_preamp", -15, 15).In(MpdSettingGroup.ReplayGain),
        Flag("replaygain_limit", "yes").In(MpdSettingGroup.ReplayGain),
        Text("metadata_to_use").In(MpdSettingGroup.Metadata),
        Flag("zeroconf_enabled", "yes").In(MpdSettingGroup.Zeroconf),
        Text("zeroconf_name", "Music Player @ %h").In(MpdSettingGroup.Zeroconf),
        Text("buffer_before_play").Old().In(MpdSettingGroup.Audio),
        Text("http_proxy_host").Old().In(MpdSettingGroup.Proxy),
        Number("http_proxy_port", 1, 65535).Old().In(MpdSettingGroup.Proxy),
        Text("http_proxy_user").Old().In(MpdSettingGroup.Proxy),
        Secret("http_proxy_password").Old().In(MpdSettingGroup.Proxy),
        Text("id3v1_encoding").Old().In(MpdSettingGroup.Metadata),
        Flag("gapless_mp3_playback").Old().In(MpdSettingGroup.Audio),
    ];

    /// <summary>The permissions a client can hold, as used by <c>default_permissions</c>, <c>local_permissions</c>, <c>host_permissions</c> and <c>password</c>.</summary>
    public static IReadOnlyList<string> Permissions { get; } = ["read", "add", "control", "player", "admin"];

    private static readonly MpdSettingDefinition PluginName = Text("plugin");

    private static readonly MpdSettingDefinition Enabled = Flag("enabled", "yes");

    /// <summary>The blocks MPD understands.</summary>
    public static IReadOnlyList<MpdBlockDefinition> Blocks { get; } =
    [
        new("audio_output", true, "type",
            [
                Text("name"), Format("format"), Flag("enabled", "yes"), Flag("tags", "yes"), Flag("always_on", "no"),
                Pick("mixer_type", ["hardware", "software", "null", "none"]),
                Pick("replay_gain_handler", ["software", "mixer", "none"], "software"),
                Text("filters"),
            ],
            [
                Plugin("alsa", Text("device", "default"), Number("buffer_time", 0), Number("period_time", 0), Flag("auto_resample", "yes"),
                    Flag("auto_channels", "yes"), Flag("auto_format", "yes"), Flag("dop", "no"), Flag("stop_dsd_silence", "no"),
                    Text("allowed_formats"), Flag("close_on_pause", "yes"), Text("mixer_device", "default"), Text("mixer_control", "PCM"),
                    Number("mixer_index", 0, fallback: "0")),
                Plugin("pipewire", Text("target"), Text("remote", "pipewire-0"), Flag("reconnect_stream"), Flag("dsd", "no")),
                Plugin("pulse", Text("server"), Text("sink"), Text("media_role", "music"), Real("scale_volume", 0.5, 5)),
                Plugin("jack", Text("client_name", "Music Player Daemon"), Text("server_name"), Flag("autostart", "no"), Text("source_ports"),
                    Text("destination_ports"), Flag("auto_destination_ports", "yes"), Number("ringbuffer_size", 0)),
                Plugin("oss", FilePath("device"), Flag("dop", "no"), FilePath("mixer_device", "/dev/mixer"), Text("mixer_control", "PCM")),
                Plugin("openal", Text("device")),
                Plugin("ao", Text("driver", "default"), Text("options"), Number("write_size", 1, fallback: "1024")),
                Plugin("sndio", Text("device", "default"), Number("buffer_time", 0)),
                Plugin("fifo", FilePath("path")),
                Plugin("pipe", Text("command")),
                Plugin("null", Flag("sync", "yes")),
                Plugin("httpd", Number("port", 1, 65535), Text("bind_to_address"), Text("dscp_class"), Encoder(), Number("max_clients", 0),
                    Text("genre"), Text("website")),
                Plugin("snapcast", Number("port", 1, 65535, "1704"), Text("bind_to_address"), Flag("zeroconf", "yes")),
                Plugin("shout", Text("host"), Number("port", 1, 65535), Pick("protocol", ["icecast2", "icecast1", "shoutcast"], "icecast2"),
                    Pick("tls", ["disabled", "auto", "auto_no_plain", "rfc2818", "rfc2817"], "disabled"), Text("mount"), Text("user", "source"),
                    Secret("password"), Text("genre"), Text("description"), Text("url"), Flag("public", "no"), Encoder("vorbis")),
                Plugin("recorder", FilePath("path"), Text("format_path"), Encoder()),
                Plugin("osx", Text("device"), Flag("hog_device"), Flag("dop", "no"), Text("channel_map")),
                Plugin("wasapi", Text("device"), Flag("enumerate", "no"), Flag("exclusive", "no"), Flag("dop", "no")),
                Plugin("sles", Flag("audio_focus")),
                Plugin("solaris", FilePath("device", "/dev/audio")),
                Plugin("tinyalsa", Number("card", 0), Number("device", 0), Number("buffer_time", 0), Number("period_time", 0),
                    Text("mixer_control"), Number("mixer_index", 0)),
            ]),
        new("decoder", true, "plugin", [Enabled],
            [
                Plugin("adplug", Number("sample_rate", 1, fallback: "48000")),
                Plugin("audiofile"), Plugin("faad"), Plugin("flac"), Plugin("dsf"), Plugin("mad"), Plugin("mpcdec"), Plugin("opus"), Plugin("pcm"),
                Plugin("sndfile"), Plugin("vorbis"), Plugin("wavpack"), Plugin("hybrid_dsd"),
                Plugin("ffmpeg", Text("analyzeduration"), Text("probesize")),
                Plugin("dsdiff", Flag("lsbitfirst", "no")),
                Plugin("fluidsynth", Number("sample_rate", 1, fallback: "48000"), FilePath("soundfont", "/usr/share/sounds/sf2/FluidR3_GM.sf2"),
                    Real("gain"), Text("settings")),
                Plugin("gme", Flag("accuracy"), Number("default_fade", 0)),
                Plugin("mikmod", Flag("loop", "no"), Number("sample_rate", 1, fallback: "44100")),
                Plugin("modplug", Pick("resampling_mode", ["nearest", "linear", "spline", "fir"], "fir"), Number("loop_count", -1, fallback: "0")),
                Plugin("openmpt", Number("repeat_count", -1, fallback: "0"), Number("stereo_separation", 0, 200, "100"),
                    Pick("interpolation_filter", ["0", "1", "2", "4", "8"], "0"), Flag("override_mptm_interp_filter", "no"),
                    Number("volume_ramping", -1, 10, "-1"), Flag("sync_samples", "yes"), Pick("at_end", ["fadeout", "stop"]),
                    Flag("emulate_amiga", "yes"), Pick("emulate_amiga_type", ["auto", "a500", "a1200", "unfiltered"], "auto")),
                Plugin("mpg123", Flag("full_scan")),
                Plugin("sidplay", FilePath("songlength_database"), Number("default_songlength", 0), Text("default_genre"), Flag("filter"),
                    FilePath("kernal"), FilePath("basic")),
                Plugin("psgplay", Number("default_songlength", 0), Text("default_genre")),
                Plugin("vgmstream", Flag("ignore_loop"), Flag("force_loop"), Flag("really_force_loop"), Flag("ignore_fade"),
                    Number("loop_count", 0), Real("fade_time", 0), Real("fade_delay", 0)),
                Plugin("wildmidi", FilePath("config_file", "/etc/timidity/timidity.cfg")),
            ]),
        new("input", true, "plugin", [Enabled],
            [
                Plugin("alsa", Text("default_device"), Text("default_format"), Flag("auto_resample"), Flag("auto_channels"), Flag("auto_format")),
                Plugin("cdio_paranoia", Pick("default_byte_order", ["little_endian", "big_endian"]), Number("speed", 1),
                    Pick("mode", ["disable", "overlap", "full"]), Flag("skip")),
                Plugin("curl", Text("proxy"), Text("proxy_user"), Secret("proxy_password"), Flag("verify_peer", "yes"), Flag("verify_host", "yes"),
                    FilePath("cacert"), Number("connect_timeout", 0), Flag("verbose", "no"), Number("low_speed_limit", 0),
                    Number("low_speed_time", 0), Flag("tcp_keepalive", "no"), Number("tcp_keepidle", 0), Number("tcp_keepintvl", 0)),
                Plugin("qobuz", Text("app_id"), Secret("app_secret"), Text("username"), Secret("password"), Number("format_id", 0, fallback: "5")),
                Plugin("ffmpeg"), Plugin("file"), Plugin("mms"), Plugin("nfs"), Plugin("smbclient"),
            ]),
        new("input_cache", false, null, [Text("size")], []),
        new("archive_plugin", true, "name", [Enabled], [Plugin("bz2"), Plugin("zzip"), Plugin("iso")]),
        new("playlist_plugin", true, "name", [Enabled, Flag("as_directory")],
            [
                Plugin("asx"), Plugin("cue"), Plugin("embcue"), Plugin("m3u"), Plugin("extm3u"), Plugin("flac"), Plugin("pls"), Plugin("rss"),
                Plugin("xspf"), Plugin("soundcloud", Secret("apikey")),
            ]),
        new("resampler", false, "plugin", [],
            [
                Plugin("internal"),
                Plugin("libsamplerate", Text("type", "2")),
                Plugin("soxr", Pick("quality", ["very high", "high", "medium", "low", "quick", "custom"], "high"), Number("threads", 0, fallback: "1"),
                    Number("precision", 16, 32), Number("phase_response", 0, 100), Real("passband_end"), Real("stopband_begin"), Real("attenuation"),
                    Number("flags", 0)),
            ]),
        new("filter", true, "plugin", [Text("name")],
            [Plugin("ffmpeg", Text("graph")), Plugin("hdcd"), Plugin("normalize"), Plugin("null"), Plugin("route", Text("routes"))]),
        new("database", false, "plugin", [],
            [
                Plugin("simple", FilePath("path"), FilePath("cache_directory"), Flag("compress", "yes"), Flag("hide_playlist_targets", "yes")),
                Plugin("proxy", Text("host"), Number("port", 1, 65535), Secret("password"), Flag("keepalive", "no")),
                Plugin("upnp", Text("interface")),
            ]),
        new("neighbors", true, "plugin", [], [Plugin("smbclient"), Plugin("udisks"), Plugin("upnp")]),
        new("partition", true, null, [Text("name")], []),
    ];

    /// <summary>The encoders an <c>httpd</c>, <c>recorder</c> or <c>shout</c> output can use, with the settings each takes inside the output block.</summary>
    public static IReadOnlyList<MpdPluginDefinition> Encoders { get; } =
    [
        Plugin("vorbis", Real("quality", -1, 10, "3"), Number("bitrate", 0)),
        Plugin("opus", Text("bitrate", "auto"), Number("complexity", 0, 10), Pick("signal", ["auto", "voice", "music"], "auto"),
            Pick("vbr", ["yes", "no", "constrained"], "yes"), Number("packet_loss", 0, 100, "0"), Flag("opustags", "no")),
        Plugin("lame", Number("quality", 0, 9), Number("bitrate", 0)),
        Plugin("shine", Number("bitrate", 0)),
        Plugin("twolame", Number("quality", 0, 9), Number("bitrate", 0)),
        Plugin("flac", Number("compression", 0, 8), Flag("oggflac", "no"), Flag("oggchaining", "no")),
        Plugin("wave"),
        Plugin("null"),
    ];

    /// <summary>The keys that name the plugin in a block that has plugins. They are part of the block's identity, not of its plugin settings.</summary>
    public static MpdSettingDefinition PluginKeyDefinition(string key) => key == "plugin" ? PluginName : Text(key);

    public static MpdSettingDefinition? FindSetting(string name) => Settings.FirstOrDefault(item => item.Name == name);

    public static MpdBlockDefinition? FindBlock(string name) => Blocks.FirstOrDefault(item => item.Name == name);

    /// <summary>Choices for a yes/no setting.</summary>
    public static IReadOnlyList<string> BooleanChoices => YesNo;

    /// <summary>The settings a block offers for its current plugin: the common ones, then the plugin's own, then the encoder's when it has one.</summary>
    public static IReadOnlyList<MpdSettingDefinition> FieldsFor(MpdBlockDefinition definition, string? plugin, string? encoder = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        List<MpdSettingDefinition> fields = [.. definition.CommonFields];
        if (definition.FindPlugin(plugin) is { } selected)
            foreach (MpdSettingDefinition field in selected.Fields)
                if (fields.All(existing => existing.Name != field.Name)) fields.Add(field);
        if (definition.Name == "audio_output" && encoder is not null && Encoders.FirstOrDefault(item => item.Name == encoder) is { } chosen)
            foreach (MpdSettingDefinition field in chosen.Fields)
                if (fields.All(existing => existing.Name != field.Name)) fields.Add(field);
        return fields;
    }
}
