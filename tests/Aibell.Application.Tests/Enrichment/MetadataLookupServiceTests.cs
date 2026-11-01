using Aibell.Application.Discs;
using Aibell.Application.Enrichment;
using NUnit.Framework;

namespace Aibell.Application.Tests.Enrichment;

[TestFixture]
public sealed class MetadataLookupServiceTests
{
    private static readonly DiscToc Toc = new([150, 15363], 30000);
    private static readonly ReleaseCandidate Release = new("fake", "1", "Album", "Artist", 2000, "rock", null, 1, []);

    private static MetadataLookupService Create(FakeProvider? provider, Func<bool>? allowed = null, TimeSpan? timeout = null) =>
        new(provider is null ? [] : [provider], provider is null ? [] : [provider], allowed ?? (() => true), timeout);

    [Test]
    public async Task FindDiscAsync_UserHasNotAllowedLookups_NeverCallsProvider()
    {
        FakeProvider provider = new() { Releases = [Release] };
        MetadataLookupResult<ReleaseCandidate> result = await Create(provider, () => false).FindDiscAsync(Toc);

        Assert.That(result.Status, Is.EqualTo(MetadataLookupStatus.Disabled));
        Assert.That(provider.Calls, Is.Zero);
    }

    [Test]
    public async Task FindDiscAsync_NoPlugin_ReportsNoProvider() =>
        Assert.That((await Create(null).FindDiscAsync(Toc)).Status, Is.EqualTo(MetadataLookupStatus.NoProvider));

    [Test]
    public async Task FindDiscAsync_ProviderAnswers_ReturnsItems()
    {
        MetadataLookupResult<ReleaseCandidate> result = await Create(new FakeProvider { Releases = [Release] }).FindDiscAsync(Toc);
        Assert.That(result.Status, Is.EqualTo(MetadataLookupStatus.Found));
        Assert.That(result.Items, Is.EqualTo(new[] { Release }));
    }

    [Test]
    public async Task FindDiscAsync_NothingKnown_ReportsNotFound() =>
        Assert.That((await Create(new FakeProvider()).FindDiscAsync(Toc)).Status, Is.EqualTo(MetadataLookupStatus.NotFound));

    [Test]
    public async Task FindDiscAsync_ProviderThrows_ReportsFailedWithMessage()
    {
        MetadataLookupResult<ReleaseCandidate> result = await Create(new FakeProvider { Failure = new InvalidOperationException("boom") }).FindDiscAsync(Toc);
        Assert.That(result.Status, Is.EqualTo(MetadataLookupStatus.Failed));
        Assert.That(result.Detail, Is.EqualTo("boom"));
    }

    [Test]
    public async Task FindDiscAsync_ProviderHangs_TimesOut()
    {
        MetadataLookupResult<ReleaseCandidate> result = await Create(new FakeProvider { Hang = true }, timeout: TimeSpan.FromMilliseconds(50)).FindDiscAsync(Toc);
        Assert.That(result.Status, Is.EqualTo(MetadataLookupStatus.Failed));
        Assert.That(result.Detail, Does.Contain("timed out"));
    }

    [Test]
    public async Task FindDiscAsync_CallerCancels_Propagates()
    {
        using CancellationTokenSource source = new();
        FakeProvider provider = new() { Hang = true };
        Task<MetadataLookupResult<ReleaseCandidate>> pending = Create(provider).FindDiscAsync(Toc, source.Token);
        await source.CancelAsync();
        await Assert.ThatAsync(() => pending, Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public async Task FindArtistAsync_Match_ReturnsInfo()
    {
        ArtistInfo info = new("fake", "9", "Artist");
        MetadataLookupResult<ArtistInfo> result = await Create(new FakeProvider { Artist = info }).FindArtistAsync("Artist");
        Assert.That(result.Items, Is.EqualTo(new[] { info }));
    }

    [Test]
    public async Task FindArtistAsync_NoMatch_ReportsNotFound() =>
        Assert.That((await Create(new FakeProvider()).FindArtistAsync("Artist")).Status, Is.EqualTo(MetadataLookupStatus.NotFound));

    [Test]
    public void IsAvailable_RequiresPermissionAndAProvider()
    {
        bool allowed = false;
        MetadataLookupService service = Create(new FakeProvider(), () => allowed);
        Assert.That(service.IsAvailable, Is.False);
        allowed = true;
        Assert.That(service.IsAvailable, Is.True);
        Assert.That(Create(null).IsAvailable, Is.False);
    }

    private sealed class FakeProvider : IReleaseLookupProvider, IArtistInfoProvider
    {
        public int Calls { get; private set; }
        public IReadOnlyList<ReleaseCandidate> Releases { get; init; } = [];
        public ArtistInfo? Artist { get; init; }
        public Exception? Failure { get; init; }
        public bool Hang { get; init; }

        public EnrichmentProviderDescriptor Descriptor { get; } = new("fake", "Fake", false, true);

        public Task<IReadOnlyList<ReleaseCandidate>> LookupDiscAsync(DiscToc toc, CancellationToken cancellationToken = default) => Run(Releases, cancellationToken);

        public Task<IReadOnlyList<ReleaseCandidate>> LookupAlbumAsync(string album, string? artist, bool includeTracks = true, CancellationToken cancellationToken = default) => Run(Releases, cancellationToken);

        public async Task<ArtistInfo?> LookupArtistAsync(string artistName, CancellationToken cancellationToken = default)
        {
            await Run(Releases, cancellationToken);
            return Artist;
        }

        private async Task<IReadOnlyList<ReleaseCandidate>> Run(IReadOnlyList<ReleaseCandidate> result, CancellationToken cancellationToken)
        {
            Calls++;
            if (Failure is not null) throw Failure;
            if (Hang) await Task.Delay(Timeout.Infinite, cancellationToken);
            return result;
        }
    }
}
