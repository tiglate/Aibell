using Avalonia.Markup.Xaml;

namespace Aibell.Desktop.Localization;

/// <summary>XAML access to <see cref="Loc"/>: <c>Content="{loc:T Artwork.Menu}"</c>.</summary>
public sealed class TExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.Get(Key);
}
