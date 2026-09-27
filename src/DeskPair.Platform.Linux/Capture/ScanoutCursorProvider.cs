using DeskPair.Platform.Abstractions.Cursor;

namespace DeskPair.Platform.Linux.Capture;

/// <summary>
/// The pointer as the engine under the daemon can describe it: one arrow, and no position.
///
/// What a viewer needs from this is a shape for its own pointer to take -- that is what "not drawing the
/// remote pointer" means -- and the fake provider that stood here gave it a fully transparent one, so a
/// viewer's own pointer vanished over a Linux host. The arrow below is the ordinary one, and it is all this
/// can honestly give: the host's cursor is a compositor state, and without a cursor plane to read there is
/// nowhere to read it from. vmwgfx has none (eight planes, all primary, measured), so mutter draws the
/// pointer into the picture itself and the viewer sees it there, moving with whoever moves it.
///
/// Machines with a cursor plane could do better -- the plane's framebuffer is the shape and its CRTC_X/Y
/// the position -- and that is the next thing the daemon can serve. Not written until there is hardware
/// to measure it on: a cursor read wrongly is a viewer clicking beside what it sees.
/// </summary>
public sealed class ScanoutCursorProvider : ICursorProvider
{
    /// <summary>The one shape, with an id no real cursor source uses.</summary>
    public const ulong ArrowId = 1;

    // '#' black, '.' white, ' ' transparent. The classic left-pointing arrow with a white fill, hotspot at
    // the tip: the shape every desktop's default pointer is a variation of, so a viewer's pointer over a
    // Linux host looks like a pointer and not like a bug.
    private static readonly string[] Arrow =
    [
        "#           ",
        "##          ",
        "#.#         ",
        "#..#        ",
        "#...#       ",
        "#....#      ",
        "#.....#     ",
        "#......#    ",
        "#.......#   ",
        "#........#  ",
        "#.....#####",
        "#..#..#     ",
        "#.# #..#    ",
        "##  #..#    ",
        "#    #..#   ",
        "     #..#   ",
        "      ##    ",
    ];

    private static readonly CursorImage ArrowImage = Render();

    public ulong GetCurrentCursorId() => ArrowId;

    public CursorImage? GetCursorImage(ulong id) => id == ArrowId ? ArrowImage : null;

    /// <summary>Unknown: the compositor's pointer is not readable from here, and a guess would draw a second pointer somewhere wrong.</summary>
    public (int X, int Y)? GetCursorPosition() => null;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>BGRA, premultiplied, which for fully opaque and fully transparent pixels is the plain colour.</summary>
    private static CursorImage Render()
    {
        int width = Arrow.Max(row => row.Length);
        int height = Arrow.Length;
        byte[] bgra = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            string row = Arrow[y];
            for (int x = 0; x < row.Length; x++)
            {
                int i = (y * width + x) * 4;
                switch (row[x])
                {
                    case '#':
                        bgra[i + 3] = 255;
                        break;
                    case '.':
                        bgra[i] = bgra[i + 1] = bgra[i + 2] = bgra[i + 3] = 255;
                        break;
                }
            }
        }

        return new CursorImage(ArrowId, HotX: 0, HotY: 0, width, height, bgra);
    }
}
