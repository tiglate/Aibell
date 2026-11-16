using Aibell.Mpd.Client;
using Aibell.Mpd.Config;

namespace Aibell.Mpd.Tests;

[TestFixture]
[Platform("Linux", Reason = "The expectations use Unix socket paths.")]
public sealed class MpdEndpointTests
{
    private static Func<string, string?> Env(params (string Key, string Value)[] values) => name => values.FirstOrDefault(item => item.Key == name).Value;

    private static MpdEndpoint Resolve(Func<string, string?> env, string? config = null, string? runtime = null, params string[] existing) =>
        MpdEndpoint.Resolve(env, config is null ? null : MpdConfigDocument.Parse(config), "/home/u", runtime, existing.Contains);

    [Test]
    public void Environment_WinsAndCarriesAPassword()
    {
        Assert.That(Resolve(Env(("MPD_HOST", "secret@music.lan"), ("MPD_PORT", "6601"))), Is.EqualTo(new MpdEndpoint("music.lan", 6601, "secret")));
        Assert.That(Resolve(Env(("MPD_HOST", "~/mpd.sock"))), Is.EqualTo(new MpdEndpoint("/home/u/mpd.sock")));
        Assert.That(Resolve(Env(("MPD_HOST", "@abstract"))), Is.EqualTo(new MpdEndpoint("@abstract")));
        Assert.That(Resolve(Env(("MPD_HOST", "~"))).Host, Is.EqualTo("/home/u"));
        Assert.That(Resolve(Env(("MPD_HOST", "host"), ("MPD_PORT", "junk"))).Port, Is.EqualTo(6600));
    }

    [Test]
    public void Configuration_ChoosesTheLocalSocketThenTheAddress()
    {
        const string both = "bind_to_address \"localhost\"\nbind_to_address \"~/.mpd/socket\"\nport \"6602\"\n";
        Assert.That(Resolve(Env(), both, null, "/home/u/.mpd/socket"), Is.EqualTo(new MpdEndpoint("/home/u/.mpd/socket", 6602)));
        Assert.That(Resolve(Env(), both), Is.EqualTo(new MpdEndpoint("localhost", 6602)));
        Assert.That(Resolve(Env(), "bind_to_address \"@mpd\"\n").Host, Is.EqualTo("@mpd"));
        Assert.That(Resolve(Env(), "bind_to_address \"192.168.1.5:6700\"\n"), Is.EqualTo(new MpdEndpoint("192.168.1.5", 6700)));
        Assert.That(Resolve(Env(), "bind_to_address \"[::1]:6701\"\n"), Is.EqualTo(new MpdEndpoint("::1", 6701)));
        Assert.That(Resolve(Env(), "bind_to_address \"[::1]\"\nport \"6000\"\n"), Is.EqualTo(new MpdEndpoint("::1", 6000)));
        Assert.That(Resolve(Env(), "bind_to_address \"::1\"\n").Host, Is.EqualTo("::1"));
        Assert.That(Resolve(Env(), "bind_to_address \"any\"\n"), Is.EqualTo(new MpdEndpoint("localhost")));
        Assert.That(Resolve(Env(("MPD_PORT", "7000")), "bind_to_address \"host\"\nport \"1\"\n"), Is.EqualTo(new MpdEndpoint("host", 7000)));
    }

    [Test]
    public void Fallbacks_AreTheRuntimeSocketThenLocalhost()
    {
        Assert.That(Resolve(Env(), null, "/run/user/1", "/run/user/1/mpd/socket"), Is.EqualTo(new MpdEndpoint("/run/user/1/mpd/socket")));
        Assert.That(Resolve(Env(), "port \"6600\"\n", "/run/user/1"), Is.EqualTo(new MpdEndpoint("localhost")));
        Assert.That(Resolve(Env()), Is.EqualTo(new MpdEndpoint("localhost")));
        Assert.Throws<ArgumentNullException>(() => MpdEndpoint.Resolve(null!, null, "", null, _ => false));
        Assert.Throws<ArgumentNullException>(() => MpdEndpoint.Resolve(Env(), null, "", null, null!));
    }

    [Test]
    public void ToString_ShowsTheAddress()
    {
        Assert.That(new MpdEndpoint("host", 1).ToString(), Is.EqualTo("host:1"));
        Assert.That(new MpdEndpoint("::1", 2).ToString(), Is.EqualTo("[::1]:2"));
        Assert.That(new MpdEndpoint("/run/x").ToString(), Is.EqualTo("/run/x"));
        Assert.That(new MpdEndpoint("/run/x").IsLocalSocket, Is.True);
    }
}
