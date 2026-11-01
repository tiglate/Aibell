using Avalonia;
using Avalonia.Headless;
using Aibell.Desktop;

[assembly: AvaloniaTestApplication(typeof(Aibell.Desktop.UiTests.TestAppBuilder))]

namespace Aibell.Desktop.UiTests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
