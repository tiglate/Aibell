using System.Globalization;
using NUnit.Framework;

namespace Aibell.Desktop.UiTests;

/// <summary>Tests assert the English texts, so they must not follow the language of the machine that runs them.</summary>
[SetUpFixture]
public sealed class EnglishTestCulture
{
    [OneTimeSetUp]
    public void UseEnglish()
    {
        CultureInfo english = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = english;
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = english;
        // The classic player's default layout follows the desktop (one window under a tiling compositor); tests expect the
        // classic separate windows wherever they run.
        Classic.ClassicPlayerWindow.SeparateWindowsByDefault = true;
    }
}
