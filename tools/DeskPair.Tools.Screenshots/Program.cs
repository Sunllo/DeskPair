using System.Globalization;
using Avalonia.Headless;
using Avalonia.Threading;
using DeskPair.Desktop;
using DeskPair.Desktop.Localization;
using DeskPair.Desktop.Services;
using DeskPair.Desktop.ViewModels;
using DeskPair.Tools.Screenshots;

// Pictures of the desktop app for the README, from the app's own windows filled with sample data:
//
//   dotnet run --project tools/DeskPair.Tools.Screenshots [-- --out docs/images/screenshots]
//
// Skia draws them into memory, so no window opens, and nothing of the machine running this -- its ID, devices,
// account, settings or files -- is read into a picture: every window is built around Samples instead.

string output = args.SkipWhile(a => a != "--out").Skip(1).FirstOrDefault() ?? Path.Combine("docs", "images", "screenshots");
Directory.CreateDirectory(output);

// The user's own settings and device list, which must be exactly as they were when this finishes.
string userSettings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sunllo", "DeskPair");
Dictionary<string, DateTime> untouched = Stamps(userSettings);

// Before the application exists: from here on "the settings file" is one in a scratch folder, so the app neither
// reads the user's nor has anywhere to save but the scratch copy.
string scratch = Directory.CreateTempSubdirectory("deskpair-screenshots-").FullName;
DesktopConfig.PathOverride = Path.Combine(scratch, "desktop.json");

// The session windows dial as they open. These show sessions that were never started, and must reach no server,
// portal or peer.
SessionViewModelBase.Dials = false;

// English, dates included, whichever machine makes them.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture =
    CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");

DeskPair.Desktop.Program.BuildAvaloniaApp()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();
Dispatcher.UIThread.RunJobs();

try
{
    App.Config = Samples.Config(scratch);
    Strings.Language = App.Config.Language;
    new Shots(output, scratch).All();
}
finally
{
    Directory.Delete(scratch, recursive: true);
}

// Belt and braces: had anything written the user's files after all, say so rather than leave it to be found.
if (!Stamps(userSettings).OrderBy(p => p.Key).SequenceEqual(untouched.OrderBy(p => p.Key)))
{
    Console.Error.WriteLine($"Something under {userSettings} changed while the pictures were made. Check it.");
    return 1;
}

return 0;

static Dictionary<string, DateTime> Stamps(string folder) => Directory.Exists(folder)
    ? Directory.GetFiles(folder).ToDictionary(f => f, File.GetLastWriteTimeUtc)
    : [];
