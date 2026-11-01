using Aibell.Application.Settings;
using Aibell.Desktop.Services;
using Aibell.Infrastructure.Settings;
using NUnit.Framework;

namespace Aibell.Desktop.Tests;

[TestFixture]
public sealed class PlaybackEngineChooserTests
{
    private string directory = null!;
    private string SettingsPath => Path.Combine(directory, "settings.json");
    private string LogPath => Path.Combine(directory, "logs", "playback.log");

    [SetUp] public void SetUp() { directory = Path.Combine(Path.GetTempPath(), "aibell-engine-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); }

    [TearDown] public void TearDown() { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }

    private async Task Save(PlaybackEngineKind kind)
    {
        using JsonApplicationSettingsStore store = new(SettingsPath);
        await store.SaveAsync(ApplicationPreferences.Default with { PlaybackEngine = kind });
    }

    private static bool Available(out string? problem) { problem = null; return true; }

    private static bool Unavailable(out string? problem) { problem = "libaibell_audio.so could not be loaded"; return false; }

    [Test]
    public async Task SavedSoundFont_IsReadFromTheSettings()
    {
        string bank = Path.Combine(directory, "bank.sf2");
        using (JsonApplicationSettingsStore store = new(SettingsPath)) await store.SaveAsync(ApplicationPreferences.Default with { SoundFontPath = bank });

        Assert.That(PlaybackEngineChooser.SavedSoundFont(SettingsPath), Is.EqualTo(bank));
        Assert.That(PlaybackEngineChooser.SavedSoundFont(Path.Combine(directory, "none.json")), Is.Null);
    }

    [Test]
    public async Task Choose_BuiltInPlayerThatIsAvailable_IsUsed()
    {
        await Save(PlaybackEngineKind.Miniaudio);

        Assert.That(PlaybackEngineChooser.Choose(SettingsPath, directory, Available), Is.EqualTo(PlaybackEngineKind.Miniaudio));
        Assert.That(File.Exists(LogPath), Is.False);
    }

    [Test]
    public async Task Choose_BuiltInPlayerThatCannotLoad_FallsBackToVlcAndLogsWhy()
    {
        await Save(PlaybackEngineKind.Miniaudio);

        Assert.That(PlaybackEngineChooser.Choose(SettingsPath, directory, Unavailable), Is.EqualTo(PlaybackEngineKind.Vlc));
        Assert.That(File.ReadAllText(LogPath), Does.Contain("Using VLC because the built-in player is unavailable").And.Contain("libaibell_audio.so"));
        using JsonApplicationSettingsStore store = new(SettingsPath);
        Assert.That((await store.LoadAsync()).PlaybackEngine, Is.EqualTo(PlaybackEngineKind.Miniaudio), "the saved choice is left as the user made it");
    }

    [Test]
    public async Task Choose_SavedVlc_NeverAsksWhetherTheBuiltInPlayerLoads()
    {
        await Save(PlaybackEngineKind.Vlc);
        bool asked = false;

        PlaybackEngineKind chosen = PlaybackEngineChooser.Choose(SettingsPath, directory, (out string? problem) => { asked = true; problem = null; return false; });

        Assert.That(chosen, Is.EqualTo(PlaybackEngineKind.Vlc));
        Assert.That(asked, Is.False, "VLC users must not pay for, or log about, a library they do not use");
        Assert.That(File.Exists(LogPath), Is.False);
    }

    [Test]
    public void Choose_NoSettingsFile_UsesThePreferredPlayer()
    {
        // With nothing chosen, the platform's own choice is used: PipeWire on Linux.
        Assert.That(PlaybackEngineChooser.Choose(SettingsPath, directory, Available), Is.EqualTo(PlaybackEngineDefaults.Preferred));
    }

    [Test]
    public void Choose_CorruptSettingsFile_UsesThePreferredPlayer()
    {
        File.WriteAllText(SettingsPath, "{ this is not json");

        Assert.That(PlaybackEngineChooser.Choose(SettingsPath, directory, Available), Is.EqualTo(PlaybackEngineDefaults.Preferred));
    }

    [Test]
    public async Task Choose_UnwritableLogFolder_StillFallsBackToVlc()
    {
        await Save(PlaybackEngineKind.Miniaudio);
        string blocker = Path.Combine(directory, "not-a-folder");
        File.WriteAllText(blocker, "a file where the profile folder should be");

        // The reason cannot be written down, but the fallback to another player still happens.
        PlaybackEngineKind chosen = PlaybackEngineChooser.Choose(SettingsPath, blocker, Unavailable);

        Assert.That(chosen, Is.EqualTo(PlaybackEngineKind.Vlc));
    }
}
