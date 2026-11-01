using System.Globalization;
using NUnit.Framework;

namespace Aibell.Desktop.Tests;

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
    }
}
