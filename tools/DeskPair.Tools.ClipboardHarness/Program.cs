using Microsoft.Extensions.Logging;
using DeskPair.Platform.Abstractions.Clipboard;
using DeskPair.Platform.Abstractions.Imaging;

// Drives the real system clipboard on macOS and Linux.
//
// Neither has a single automated clipboard test, and neither can get one that runs unattended: both need a
// live desktop session, and a test that passes would have replaced whatever the person at the machine had
// copied. This is the manual counterpart — published self-contained, so it runs on a Mac with no .NET.
//
//   read  [--out FILE]        print what is on the clipboard; write any image to FILE
//   write-text TEXT           put text on the clipboard
//   write-image FILE.png      put a PNG on the clipboard (what a pasted image from a phone looks like)
//     --hold N                stay alive N seconds afterwards; on X11 a selection dies with its owner
//   watch [--seconds N]       print changes as they arrive, the way a host publishes them
//
//   dotnet publish tools/DeskPair.Tools.ClipboardHarness -r osx-arm64 --self-contained
if (args.Length == 0)
{
    Console.Error.WriteLine("verbs: read | write-text | write-image | watch");
    return 1;
}

using ILoggerFactory logs = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Debug));
Dictionary<string, string> o = Parse(args.Skip(1));

await using IClipboard clipboard = Open(logs.CreateLogger("clipboard"));
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

switch (args[0])
{
    case "read":
    {
        IReadOnlyList<ClipboardItem> items = await clipboard.ReadAsync(cts.Token);
        Describe(items, o.GetValueOrDefault("out"));
        return items.Count == 0 ? 2 : 0;
    }

    case "write-text":
    {
        string text = args.Length > 1 ? args[1] : string.Empty;
        await clipboard.WriteAsync([new ClipboardItem(ClipboardItemFormat.Text, System.Text.Encoding.UTF8.GetBytes(text))], cts.Token);
        Console.WriteLine($"wrote {text.Length} character(s)");
        await HoldAsync(o);
        return 0;
    }

    case "write-image":
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("write-image needs a path to a PNG");
            return 1;
        }

        byte[] png = await File.ReadAllBytesAsync(args[1], cts.Token);
        var items = new List<ClipboardItem> { new(ClipboardItemFormat.ImagePng, png) };
        if (o.TryGetValue("text", out string? caption))
        {
            items.Add(new ClipboardItem(ClipboardItemFormat.Text, System.Text.Encoding.UTF8.GetBytes(caption)));
        }

        await clipboard.WriteAsync(items, cts.Token);
        Console.WriteLine($"wrote {png.Length} byte(s) of PNG{(caption is null ? string.Empty : " and a caption")}");
        await HoldAsync(o);
        return 0;
    }

    case "watch":
    {
        int seconds = int.TryParse(o.GetValueOrDefault("seconds"), out int s) ? s : 10;
        Console.WriteLine($"watching for {seconds}s…");
        using var watch = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        try
        {
            await foreach (IReadOnlyList<ClipboardItem> items in clipboard.Changes.ReadAllAsync(watch.Token))
            {
                Console.WriteLine("-- change --");
                Describe(items, null);
            }
        }
        catch (OperationCanceledException)
        {
        }

        return 0;
    }

    default:
        Console.Error.WriteLine($"unknown verb {args[0]}");
        return 1;
}

// Stays alive so someone else can read the selection. An X11 selection belongs to a window and dies with
// the process that owns it, so a harness that wrote and exited would leave nothing for xclip to fetch.
// macOS has no such rule, but holding there is harmless and keeps one set of commands working on both.
static async Task HoldAsync(Dictionary<string, string> o)
{
    if (!o.TryGetValue("hold", out string? value) || !int.TryParse(value, out int seconds) || seconds <= 0)
    {
        return;
    }

    Console.WriteLine($"holding the selection for {seconds}s…");
    await Task.Delay(TimeSpan.FromSeconds(seconds));
}

static void Describe(IReadOnlyList<ClipboardItem> items, string? imageOut)
{
    if (items.Count == 0)
    {
        Console.WriteLine("clipboard is empty (or holds nothing this platform understands)");
        return;
    }

    foreach (ClipboardItem item in items)
    {
        switch (item.Format)
        {
            case ClipboardItemFormat.Text:
                Console.WriteLine($"text: {item.Payload.Length} byte(s): {Preview(item)}");
                break;

            case ClipboardItemFormat.ImagePng:
            {
                string size;
                try
                {
                    BgraImage image = PngCodec.Decode(item.Payload.Span.ToArray());
                    size = $"{image.Width}x{image.Height}";
                }
                catch (Exception e)
                {
                    // Worth printing rather than swallowing: a PNG our own decoder refuses is exactly the
                    // interesting failure, and it would otherwise look like a successful read.
                    size = $"undecodable ({e.GetType().Name})";
                }

                Console.WriteLine($"image: {item.Payload.Length} byte(s), {size}, sha256={Sha(item)}");
                if (imageOut is not null)
                {
                    File.WriteAllBytes(imageOut, item.Payload.ToArray());
                    Console.WriteLine($"  saved to {imageOut}");
                }

                break;
            }

            default:
                Console.WriteLine($"{item.Format}: {item.Payload.Length} byte(s)");
                break;
        }
    }
}

static string Preview(ClipboardItem item)
{
    string text = System.Text.Encoding.UTF8.GetString(item.Payload.Span);
    return text.Length <= 60 ? text : text[..60] + "…";
}

static string Sha(ClipboardItem item) =>
    Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(item.Payload.Span));

static IClipboard Open(ILogger log)
{
    if (OperatingSystem.IsMacOS())
    {
        return new DeskPair.Platform.MacOS.Clipboard.MacClipboard(log);
    }

    if (OperatingSystem.IsLinux())
    {
        return new DeskPair.Platform.Linux.Clipboard.X11Clipboard(log);
    }

    throw new PlatformNotSupportedException("This harness exists for macOS and Linux; Windows has real tests.");
}

static Dictionary<string, string> Parse(IEnumerable<string> rest)
{
    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    string? key = null;
    foreach (string token in rest)
    {
        if (token.StartsWith("--", StringComparison.Ordinal))
        {
            key = token[2..];
            map[key] = string.Empty;
        }
        else if (key is not null)
        {
            map[key] = token;
            key = null;
        }
    }

    return map;
}
