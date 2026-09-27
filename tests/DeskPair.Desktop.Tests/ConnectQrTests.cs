using Net.Codecrete.QrCodeGenerator;
using DeskPair.Core.Config;
using DeskPair.Platform.Abstractions.Imaging;

namespace DeskPair.Desktop.Tests;

/// <summary>
/// The pairing code, from the link to the modules a camera has to read.
/// </summary>
/// <remarks>
/// The interesting failure here is not "the library is broken" — it is a payload that encodes into
/// something no phone will read across a desk: too dense because a field grew, or empty because the id had
/// not arrived yet. Those are what these check.
/// </remarks>
public sealed class ConnectQrTests
{
    private const string ServerKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEWjZBRYqlg374F9psEQ4ql93Qt8r7jhV3KXVO4U38xFU/dbdXpJH9l7fHFGe0NBWtNXS3uw1yYkH7i0pIYkmGwQ==";

    private static ConnectLink Sample => new()
    {
        Id = "123456789",
        RendezvousServer = "203.0.113.10:21116",
        ServerPublicKeyBase64 = ServerKey,
        DeviceName = "Studio-Mac-mini",
    };

    [Fact]
    public void A_full_link_stays_small_enough_to_scan_across_a_desk()
    {
        QrCode code = QrCode.EncodeText(Sample.ToString(), QrCode.Ecc.Medium);

        // Version 10 is 57 modules. Past that the modules get small enough on a laptop screen that a phone
        // held at arm's length starts to struggle, so this is the budget a new field has to fit inside.
        code.Size.ShouldBeLessThanOrEqualTo(57);
        code.Size.ShouldBeGreaterThan(20);
    }

    [Fact]
    public void An_id_on_its_own_makes_a_much_smaller_code()
    {
        QrCode full = QrCode.EncodeText(Sample.ToString(), QrCode.Ecc.Medium);
        QrCode bare = QrCode.EncodeText(new ConnectLink { Id = "123456789" }.ToString(), QrCode.Ecc.Medium);
        bare.Size.ShouldBeLessThan(full.Size);
    }

    /// <summary>
    /// Writes the code as a PNG so a real scanner can be pointed at it.
    /// </summary>
    /// <remarks>
    /// A camera cannot be driven from a simulator, and asserting that our own encoder agrees with itself
    /// proves nothing. This leaves a file that an independent decoder — macOS Vision, say — can be run
    /// over, which is the only way to find out whether what we draw is actually readable.
    /// </remarks>
    [Fact]
    public void The_code_can_be_written_out_for_an_independent_decoder()
    {
        QrCode code = QrCode.EncodeText(Sample.ToString(), QrCode.Ecc.Medium);

        const int scale = 8;
        const int quiet = 4;
        int side = (code.Size + (quiet * 2)) * scale;

        byte[] bgra = new byte[side * side * 4];
        Array.Fill(bgra, (byte)0xFF); // white, opaque

        for (int y = 0; y < code.Size; y++)
        {
            for (int x = 0; x < code.Size; x++)
            {
                if (!code.GetModule(x, y))
                {
                    continue;
                }

                for (int dy = 0; dy < scale; dy++)
                {
                    int row = (((y + quiet) * scale) + dy) * side;
                    for (int dx = 0; dx < scale; dx++)
                    {
                        int offset = (row + ((x + quiet) * scale) + dx) * 4;
                        bgra[offset] = 0;
                        bgra[offset + 1] = 0;
                        bgra[offset + 2] = 0;
                    }
                }
            }
        }

        byte[] png = PngCodec.Encode(bgra, side * 4, side, side);
        string path = Path.Combine(Path.GetTempPath(), "sunllo-connect-qr.png");
        File.WriteAllText(Path.ChangeExtension(path, ".txt"), Sample.ToString());
        File.WriteAllBytes(path, png);

        png.Length.ShouldBeGreaterThan(0);
        PngCodec.Decode(png).Width.ShouldBe(side);
    }
}
