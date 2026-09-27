using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using DeskPair.Desktop.Tests;

// One headless application for the whole assembly, set up by the test framework before the first
// [AvaloniaFact] runs and torn down after the last.
//
// It used to be each test class's own business: a Lazy holding SetupWithoutStarting, copied into the
// second class that needed a platform. Avalonia may be initialised once per process, and xUnit runs
// classes in parallel, so the copy turned thirteen passing tests into thirteen failures the moment there
// were two of them.
[assembly: AvaloniaTestApplication(typeof(HeadlessApp))]

namespace DeskPair.Desktop.Tests;

/// <summary>
/// A bare application, not the product's own.
///
/// DeskPair's App reads configuration, starts an engine and puts an icon in the notification area on the
/// way up; none of that is wanted here, and all of it would have to be undone afterwards. What a window
/// needs to be built is a platform, which is what this is -- plus the product's theme, without which a
/// button has no template (a click lands on whatever is behind it) and a menu has nowhere to open.
/// </summary>
public static class HeadlessApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<ThemedApplication>().UseHeadless(new AvaloniaHeadlessPlatformOptions());

    private sealed class ThemedApplication : Application
    {
        public override void Initialize() => Styles.Add(new FluentTheme());
    }
}
