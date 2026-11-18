using System.Net;
using System.Text;

namespace Aibell.Skins.Classic.Tests;

[TestFixture]
public sealed class SkinLibraryTests
{
    private string directory = null!;

    [SetUp]
    public void CreateFolder() => directory = Path.Combine(Path.GetTempPath(), "aibell-skins-" + Guid.NewGuid().ToString("N"));

    [TearDown]
    public void DeleteFolder()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    [Test]
    public void InstallsListsLoadsAndUninstalls()
    {
        SkinLibrary library = new(directory);
        Assert.That(library.List(), Is.EqualTo(new[] { SkinLibrary.BuiltIn }));
        InstalledSkin red = library.Install(SkinTestData.MinimalSkin(), "Red/../Skin?");
        Assert.That(red.Id, Is.EqualTo("Red_.._Skin_.wsz"));
        Assert.That(library.List(), Has.Count.EqualTo(2));
        Assert.That(library.Load(red.Id)[SkinSheet.Main][0, 0], Is.EqualTo(0xFFFF0000u));

        Assert.That(library.Install(SkinTestData.MinimalSkin(), "Other name"), Is.EqualTo(red), "the same skin is not installed twice");
        InstalledSkin second = library.Install(SkinTestData.MinimalSkin("x/"), "Red/../Skin?");
        Assert.That(second.Id, Is.EqualTo("Red_.._Skin_ (2).wsz"));

        library.Uninstall(red);
        Assert.That(library.List().Select(skin => skin.Id), Is.EqualTo(new[] { "", second.Id }));
        Assert.That(() => library.Uninstall(SkinLibrary.BuiltIn), Throws.InvalidOperationException);
    }

    [Test]
    public void InstallFromFileRejectsNonSkinsWithoutWritingAnything()
    {
        SkinLibrary library = new(directory);
        string file = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".wsz");
        File.WriteAllBytes(file, SkinTestData.Zip(("readme.txt", [1])));
        try
        {
            Assert.That(() => library.InstallFile(file), Throws.TypeOf<InvalidSkinException>());
            Assert.That(Directory.Exists(directory), Is.False);
            File.WriteAllBytes(file, SkinTestData.MinimalSkin());
            Assert.That(library.InstallFile(file).Name, Is.EqualTo(Path.GetFileNameWithoutExtension(file)));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Test]
    public void LoadFallsBackToTheBuiltInSkin()
    {
        SkinLibrary library = new(directory);
        Assert.That(library.Load(null), Is.SameAs(DefaultSkin.Skin));
        Assert.That(library.Load("missing.wsz"), Is.SameAs(DefaultSkin.Skin));
        Assert.That(library.Load("../escape.wsz"), Is.SameAs(DefaultSkin.Skin));
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "broken.wsz"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(directory, "notes.txt"), [1]);
        Assert.That(library.Load("broken.wsz"), Is.SameAs(DefaultSkin.Skin));
        Assert.That(library.List().Select(skin => skin.Name), Is.EqualTo(new[] { "Aibell", "broken" }));
    }

    [TestCase("", "Skin")]
    [TestCase("  ..  ", "Skin")]
    [TestCase("a:b*c", "a_b_c")]
    public void CleansNames(string name, string expected) => Assert.That(SkinLibrary.CleanName(name), Is.EqualTo(expected));

    [Test]
    public async Task GalleryBrowsesSearchesAndFiltersUnsafeOrForeignSkins()
    {
        const string page = """
            {"data":{"skins":{"nodes":[
              {"md5":"a","filename":"Good.wsz","screenshot_url":"https://r2.webampskins.org/screenshots/a.png","download_url":"https://r2.webampskins.org/skins/a.wsz","nsfw":false},
              {"md5":"b","filename":"Bad.wsz","screenshot_url":"https://r2.webampskins.org/screenshots/b.png","download_url":"https://r2.webampskins.org/skins/b.wsz","nsfw":true},
              {"md5":"c","filename":"Elsewhere.wsz","screenshot_url":"https://r2.webampskins.org/screenshots/c.png","download_url":"http://example.com/c.wsz","nsfw":false},
              {},
              null
            ]}}}
            """;
        FakeHandler handler = new(_ => Json(page));
        SkinGalleryClient client = new(new HttpClient(handler));
        IReadOnlyList<GallerySkin> skins = await client.GetPageAsync(null, 0, 3);
        Assert.That(skins.Select(skin => skin.Name), Is.EqualTo(new[] { "Good" }));
        Assert.That(handler.LastBody, Does.Contain("sort:MUSEUM").And.Not.Contain("\"q\""));

        handler.Respond = _ => Json("""{"data":{"search_classic_skins":[{"md5":"d","filename":"Neo.zip","screenshot_url":"https://r2.webampskins.org/s/d.png","download_url":"https://r2.webampskins.org/k/d.wsz"}]}}""");
        skins = await client.GetPageAsync(" neo ", 20, 10);
        Assert.That(skins.Single().FileName, Is.EqualTo("Neo.zip"));
        Assert.That(handler.LastBody, Does.Contain("nsfw:false").And.Contain("\"q\":\"neo\"").And.Contain("\"o\":20"));

        handler.Respond = _ => Json("""{"errors":[{"message":"nope"}]}""");
        Assert.That(async () => await client.GetPageAsync("x", 0, 1), Throws.TypeOf<HttpRequestException>().With.Message.EqualTo("nope"));
        Assert.That(async () => await client.GetPageAsync("x", -1, 1), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public async Task GalleryDownloadsOnlyFromTheMuseumAndWithinTheLimit()
    {
        FakeHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        SkinGalleryClient client = new(new HttpClient(handler));
        GallerySkin skin = new("a", "A.wsz", new Uri("https://r2.webampskins.org/s/a.png"), new Uri("https://r2.webampskins.org/k/a.wsz"));
        Assert.That(await client.DownloadAsync(skin), Is.EqualTo(new byte[] { 1, 2, 3 }));
        Assert.That(await client.DownloadScreenshotAsync(skin), Is.EqualTo(new byte[] { 1, 2, 3 }));

        GallerySkin foreign = skin with { DownloadUrl = new Uri("https://example.com/a.wsz") };
        Assert.That(async () => await client.DownloadAsync(foreign), Throws.TypeOf<HttpRequestException>());

        handler.Respond = _ =>
        {
            ByteArrayContent content = new(new byte[16]);
            content.Headers.ContentLength = SkinArchive.MaximumArchiveBytes + 1;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        };
        Assert.That(async () => await client.DownloadAsync(skin), Throws.TypeOf<InvalidSkinException>());
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = respond;

        public string LastBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            return Respond(request);
        }
    }
}
