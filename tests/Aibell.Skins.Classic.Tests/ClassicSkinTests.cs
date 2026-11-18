namespace Aibell.Skins.Classic.Tests;

[TestFixture]
public sealed class ClassicSkinTests
{
    [Test]
    public void TheDefaultSkinHasEverySheetAtItsFullSize()
    {
        ClassicSkin skin = DefaultSkin.Skin;
        Assert.That(skin.Name, Is.EqualTo("Aibell"));
        Assert.That(skin[SkinSheet.Main].Width, Is.EqualTo(275));
        Assert.That(skin[SkinSheet.Main].Height, Is.EqualTo(116));
        Assert.That(skin[SkinSheet.Volume].Height, Is.EqualTo(433));
        Assert.That(skin[SkinSheet.Playlist].Width, Is.EqualTo(280));
        Assert.That(skin.HasExtendedNumbers, Is.True);
        Assert.That(skin.MinusSign, Is.EqualTo(Sprites.MinusExtended));
        Assert.That(skin.TextBackground, Is.EqualTo(DefaultSkin.Lcd));
        Assert.That(skin[SkinSheet.Main].Pixels, Has.All.GreaterThanOrEqualTo(0xFF000000u), "every pixel is opaque");
    }

    [Test]
    public void TheDefaultDigitsLightTheRightSegments()
    {
        SkinImage numbers = DefaultSkin.Skin[SkinSheet.Numbers];
        uint Middle(int digit) => numbers[digit * 9 + 4, 6];
        Assert.Multiple(() =>
        {
            Assert.That(Middle(0), Is.EqualTo(DefaultSkin.Lcd), "a zero has no middle bar");
            Assert.That(Middle(8), Is.EqualTo(DefaultSkin.LcdText));
            Assert.That(numbers[0 * 9 + 4, 0], Is.EqualTo(DefaultSkin.LcdText), "but it has a top");
            Assert.That(numbers[1 * 9 + 4, 0], Is.EqualTo(DefaultSkin.Lcd), "a one has no top");
        });
    }

    [Test]
    public void LoadsFromASubfolderAndFillsTheRestFromTheFallback()
    {
        ClassicSkin skin = ClassicSkin.Load(SkinArchive.Read(SkinTestData.MinimalSkin("my skin/")), DefaultSkin.Skin, "Red");
        Assert.That(skin.Name, Is.EqualTo("Red"));
        Assert.That(skin[SkinSheet.Main][10, 10], Is.EqualTo(0xFFFF0000u));
        Assert.That(skin[SkinSheet.ControlButtons], Is.SameAs(DefaultSkin.Skin[SkinSheet.ControlButtons]));
        Assert.That(skin.Playlist, Is.EqualTo(DefaultSkin.Skin.Playlist));
        Assert.That(skin.Visualizer, Is.SameAs(DefaultSkin.Skin.Visualizer));
        Assert.That(skin.Regions.Main, Is.Empty);
    }

    [Test]
    public void ASmallerBitmapIsLaidOverTheFallback()
    {
        byte[] archive = SkinTestData.Zip(
            ("main.bmp", SkinTestData.Bmp24(275, 116, (_, _) => 0)),
            ("titlebar.bmp", SkinTestData.Bmp24(10, 10, (_, _) => 0x00FF00)));
        ClassicSkin skin = ClassicSkin.Load(SkinArchive.Read(archive), DefaultSkin.Skin, "Partial");
        Assert.That(skin[SkinSheet.TitleBar].Width, Is.EqualTo(DefaultSkin.Skin[SkinSheet.TitleBar].Width));
        Assert.That(skin[SkinSheet.TitleBar][5, 5], Is.EqualTo(0xFF00FF00u));
        Assert.That(skin[SkinSheet.TitleBar][300, 50], Is.EqualTo(DefaultSkin.Skin[SkinSheet.TitleBar][300, 50]));
    }

    [Test]
    public void ClassicDigitsUseTheirOwnMinusSignAndBalanceFallsBackToVolume()
    {
        byte[] archive = SkinTestData.Zip(
            ("main.bmp", SkinTestData.Bmp24(275, 116, (_, _) => 0)),
            ("numbers.bmp", SkinTestData.Bmp24(99, 13, (_, _) => 0x123456)),
            ("volume.bmp", SkinTestData.Bmp24(68, 433, (_, _) => 0x654321)),
            ("eqmain.bmp", [1, 2, 3]));
        ClassicSkin skin = ClassicSkin.Load(SkinArchive.Read(archive), DefaultSkin.Skin, "Old");
        Assert.That(skin.HasExtendedNumbers, Is.False);
        Assert.That(skin.MinusSign, Is.EqualTo(Sprites.MinusClassic));
        Assert.That(skin[SkinSheet.Balance][20, 20], Is.EqualTo(0xFF654321u));
        Assert.That(skin[SkinSheet.EqualizerMain], Is.SameAs(DefaultSkin.Skin[SkinSheet.EqualizerMain]), "an undecodable bitmap falls back");
    }

    [Test]
    public void ReadsTheConfigurationFiles()
    {
        byte[] archive = SkinTestData.Zip(
            ("Main.bmp", SkinTestData.Bmp24(275, 116, (_, _) => 0)),
            ("PLEDIT.TXT", SkinTestData.Text("[Text]\r\nNormal=#112233\r\nCurrent=445566 ; comment\r\nnormalbg=#000000\r\nSelectedBG=#0000FF\r\nFont=Tahoma\r\n")),
            ("viscolor.txt", SkinTestData.Text("1,2,3, // background\n4, 5, 6\n")),
            ("region.txt", SkinTestData.Text("[Normal]\nNumPoints=4\nPointList=0,0, 275,0, 275,116, 0,116\n")));
        ClassicSkin skin = ClassicSkin.Load(SkinArchive.Read(archive), DefaultSkin.Skin, "Configured");
        Assert.That(skin.Playlist.Normal, Is.EqualTo(0xFF112233u));
        Assert.That(skin.Playlist.Current, Is.EqualTo(0xFF445566u));
        Assert.That(skin.Playlist.FontName, Is.EqualTo("Tahoma"));
        Assert.That(skin.Visualizer.Background, Is.EqualTo(0xFF010203u));
        Assert.That(skin.Visualizer.Dots, Is.EqualTo(0xFF040506u));
        Assert.That(skin.Visualizer.Peak, Is.EqualTo(VisualizerColors.Default.Peak), "missing lines keep the defaults");
        Assert.That(skin.Regions.Main, Has.Count.EqualTo(1));
        Assert.That(skin.Regions.Main[0], Is.EqualTo(new[] { (0, 0), (275, 0), (275, 116), (0, 116) }));
    }

    [Test]
    public void RejectsArchivesThatAreNotClassicSkins()
    {
        Assert.That(() => ClassicSkin.Load(SkinArchive.Read(SkinTestData.Zip(("readme.txt", [1]))), DefaultSkin.Skin, "x"),
            Throws.TypeOf<InvalidSkinException>().With.Property(nameof(InvalidSkinException.Reason)).EqualTo(InvalidSkinReason.NotAClassicSkin));
        Assert.That(() => ClassicSkin.Load(SkinArchive.Read(SkinTestData.Zip(("skin.xml", [1]))), DefaultSkin.Skin, "x"),
            Throws.TypeOf<InvalidSkinException>().With.Property(nameof(InvalidSkinException.Reason)).EqualTo(InvalidSkinReason.ModernSkin));
        Assert.That(() => ClassicSkin.Load(SkinArchive.Read(SkinTestData.Zip(("main.bmp", [1, 2]))), DefaultSkin.Skin, "x"),
            Throws.TypeOf<InvalidSkinException>());
        Assert.That(() => SkinArchive.Read([1, 2, 3]),
            Throws.TypeOf<InvalidSkinException>().With.Property(nameof(InvalidSkinException.Reason)).EqualTo(InvalidSkinReason.NotAnArchive));
        Assert.That(() => SkinArchive.Read(new byte[SkinArchive.MaximumArchiveBytes + 1]),
            Throws.TypeOf<InvalidSkinException>().With.Property(nameof(InvalidSkinException.Reason)).EqualTo(InvalidSkinReason.TooLarge));
    }

    [Test]
    public void TheShallowestCopyOfAFileWinsAndMacMetadataIsIgnored()
    {
        byte[] archive = SkinTestData.Zip(
            ("extra/deep/main.bmp", SkinTestData.Bmp24(275, 116, (_, _) => 0x0000FF)),
            ("main.bmp", SkinTestData.Bmp24(275, 116, (_, _) => 0x00FF00)),
            ("__MACOSX/._main.bmp", [0]),
            ("folder/", []));
        SkinArchive files = SkinArchive.Read(archive);
        Assert.That(files.FileNames, Is.EquivalentTo(new[] { "main.bmp" }));
        Assert.That(BmpDecoder.Decode(files.Get("MAIN.BMP"))[0, 0], Is.EqualTo(0xFF00FF00u));
        Assert.That(files.GetText("missing.txt"), Is.Null);
    }

    [Test]
    public void ReadsADirectoryAndFromFiles()
    {
        string directory = Path.Combine(Path.GetTempPath(), "aibell-skin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "MAIN.BMP"), SkinTestData.Bmp24(275, 116, (_, _) => 0));
            Assert.That(SkinArchive.ReadDirectory(directory).Has("main.bmp"), Is.True);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
        Assert.That(SkinArchive.FromFiles(new Dictionary<string, byte[]> { ["a/Text.bmp"] = [1] }).Has("text.bmp"), Is.True);
    }

    [Test]
    public void PlaylistStyleAndRegionsTolerateJunk()
    {
        Assert.That(PlaylistStyle.Parse(null), Is.SameAs(PlaylistStyle.Default));
        PlaylistStyle style = PlaylistStyle.Parse("[Other]\nNormal=#FFFFFF\n[Text]\nNormal=nonsense\nFont=  \n");
        Assert.That(style.Normal, Is.EqualTo(PlaylistStyle.Default.Normal));
        Assert.That(style.FontName, Is.EqualTo(PlaylistStyle.Default.FontName));
        Assert.That(SkinRegions.Parse(null), Is.SameAs(SkinRegions.None));
        SkinRegions regions = SkinRegions.Parse("[WindowShade]\nNumPoints=3, 9\nPointList=0 0 1 1 2 2 5 5\n[Equalizer]\nNumPoints=4\n");
        Assert.That(regions.MainShade, Has.Count.EqualTo(1), "a polygon without enough points is dropped");
        Assert.That(regions.Equalizer, Is.Empty);
        Assert.That(regions.EqualizerShade, Is.Empty);
        Assert.That(VisualizerColors.Parse("").Colors, Is.EqualTo(VisualizerColors.Default.Colors));
        Assert.That(VisualizerColors.Default.Spectrum(99), Is.EqualTo(VisualizerColors.Default.Colors[17]));
    }

    [TestCase('a', 0, 0)]
    [TestCase('Z', 0, 25)]
    [TestCase('7', 1, 7)]
    [TestCase('<', 1, 22)]
    [TestCase('}', 1, 23)]
    [TestCase('ö', 2, 1)]
    [TestCase('é', 0, 4)]
    [TestCase('中', 0, 30)]
    public void MapsCharactersToFontCells(char character, int row, int column) =>
        Assert.That(TextFont.Cell(character), Is.EqualTo((row, column)));

    [Test]
    public void SpritesFollowTheClassicLayout()
    {
        Assert.That(Sprites.Character('b'), Is.EqualTo(new Sprite(SkinSheet.Text, 5, 0, 5, 6)));
        Assert.That(Sprites.EqualizerSliderFrame(15), Is.EqualTo(new Sprite(SkinSheet.EqualizerMain, 28, 229, 14, 63)));
        Assert.That(Sprites.VolumeFrame(99), Is.EqualTo(Sprites.VolumeFrame(27)));
        Assert.That(Sprites.Shuffle(true, true).Y, Is.EqualTo(45));
        Assert.That(Sprites.PlaylistToggle(true, true), Is.EqualTo(new Sprite(SkinSheet.ShuffleRepeat, 69, 73, 23, 12)));
        Assert.That(Sprites.EqualizerOn(true, true).X, Is.EqualTo(187));
        Assert.That(Sprites.Highlighted(Sprites.AddFile).X, Is.EqualTo(23));
        Assert.That(Sprites.Digit(-3), Is.EqualTo(Sprites.Digit(0)));
    }

    [Test]
    public void SkinImageClipsAndPads()
    {
        SkinImage image = new(2, 2);
        image[5, 5] = 1;
        Assert.That(image[-1, 0], Is.Zero);
        Assert.That(image.AtLeast(1, 1), Is.SameAs(image));
        Assert.That(image.AtLeast(3, 1).Width, Is.EqualTo(3));
        Assert.That(() => new SkinImage(0, 1), Throws.TypeOf<ArgumentOutOfRangeException>());
        Assert.That(() => new SkinImage(1, 1, new uint[2]), Throws.ArgumentException);
    }

    /// <summary>Opt-in check against real skins: set AIBELL_CLASSIC_SKINS to a folder of .wsz files.</summary>
    [Test]
    public void LoadsRealSkinsWhenAFolderIsGiven()
    {
        string? folder = Environment.GetEnvironmentVariable("AIBELL_CLASSIC_SKINS");
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) Assert.Ignore("Set AIBELL_CLASSIC_SKINS to a folder of skins.");
        string[] files = Directory.GetFiles(folder, "*.wsz");
        Assert.That(files, Is.Not.Empty);
        foreach (string file in files)
        {
            ClassicSkin skin = ClassicSkin.Load(SkinArchive.Read(File.ReadAllBytes(file)), DefaultSkin.Skin, Path.GetFileName(file));
            Assert.That(skin[SkinSheet.Main].Width, Is.GreaterThanOrEqualTo(275), file);
        }
    }
}
