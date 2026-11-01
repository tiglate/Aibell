using Aibell.Desktop.Localization;
using Aibell.Domain.Radio;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aibell.Desktop.ViewModels;

/// <summary>One card of the Radio grid: a saved station ("My stations") or a curated catalog entry.</summary>
public sealed partial class RadioCardViewModel : ObservableObject
{
    private static readonly string[] Palette = ["#3D7DD8", "#C2568B", "#2E9E83", "#D9822B", "#7B61C9", "#C0504D", "#3F8F9E", "#6E8B3D"];

    public RadioCardViewModel(RadioStation station, bool isCatalogCard, bool isSaved, int order, RadioFormatNotice? notice = null)
    {
        Notice = notice;
        Station = station;
        IsCatalogCard = isCatalogCard;
        this.isSaved = isSaved;
        Order = order;
        Initials = InitialsOf(station.Name);
        TileBrush = new SolidColorBrush(Color.Parse(Palette[ColorIndex(station.Name)]));
    }

    public RadioStation Station { get; }

    /// <summary>Set when the playback engine in use cannot play this station's audio format, so the card says what to do about it.</summary>
    public RadioFormatNotice? Notice { get; }
    public bool HasNotice => Notice is not null;
    public string NoticeText => Notice is null ? "" : Loc.Format("Radio.FormatChip", Notice.Format);
    public string NoticeTip => Notice switch
    {
        null => "",
        { WindowsAlternative: true } => Loc.Format("Radio.FormatTipVlcOrWindows", Notice.Format),
        _ => Loc.Format("Radio.FormatTipVlc", Notice.Format)
    };
    public bool IsCatalogCard { get; }
    /// <summary>Position in the curated list, which is its popularity order; saved stations use their name order.</summary>
    public int Order { get; }
    public Guid Id => Station.Id;
    public string Name => Station.Name;
    public string Subtitle => Station.Creator;
    public bool HasSubtitle => Station.Creator.Length > 0;
    public string Description => Station.Description;
    public string Genre => Station.Genre;
    public bool HasGenre => Station.Genre.Length > 0;
    public string Country => Station.Country ?? "";
    public string CountryCode => Station.CountryCode ?? "";
    public bool HasCountry => Country.Length > 0 || CountryCode.Length > 0;
    public Uri? LogoUri => Station.LogoUri;
    public string Initials { get; }
    public IBrush TileBrush { get; }
    public bool LogoRequested { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave), nameof(IsNotSaved), nameof(StarTip), nameof(CanUnsave))]
    private bool isSaved;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLogo), nameof(ShowInitials))]
    private Bitmap? logo;

    public bool IsNotSaved => !IsSaved;
    public string StarTip => IsSaved ? Loc.Get("Radio.RemoveFromMine") : Loc.Get("Radio.SaveToMine");
    public bool CanUnsave => IsCatalogCard && IsSaved;
    public bool CanSave => IsCatalogCard && !IsSaved;
    public bool HasLogo => Logo is not null;
    public bool ShowInitials => Logo is null;

    // A name's first letters (up to two words), or its first characters when it starts with digits only.
    internal static string InitialsOf(string name)
    {
        char[] letters = [.. name.Split([' ', '-', '.', '/', '&'], StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.FirstOrDefault(char.IsLetterOrDigit)).Where(character => character != default).Take(2)];
        string text = new(letters);
        return (text.Length == 0 ? name.Trim().Take(2).Aggregate("", (sum, character) => sum + character) : text).ToUpperInvariant();
    }

    private static int ColorIndex(string name)
    {
        int sum = 0;
        foreach (char character in name) sum = unchecked(sum * 31 + character);
        return (int)((uint)sum % (uint)Palette.Length);
    }
}
