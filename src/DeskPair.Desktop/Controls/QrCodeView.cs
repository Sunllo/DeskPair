using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Net.Codecrete.QrCodeGenerator;

namespace DeskPair.Desktop.Controls;

/// <summary>
/// A QR code, drawn rather than rasterised.
/// </summary>
/// <remarks>
/// The generator hands back a matrix of modules, so this draws rectangles straight into the drawing context:
/// nothing is ever a bitmap, which means no image codec, nothing to scale, and a code that stays sharp on a
/// high-DPI screen at whatever size the layout gives it. It also means the quiet zone is part of the control
/// rather than something a caller has to remember to leave room for — a code printed flush to its border is
/// one a phone will refuse to read.
/// </remarks>
public sealed class QrCodeView : Control
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<QrCodeView, string?>(nameof(Text));

    public static readonly StyledProperty<IBrush> ForegroundProperty =
        AvaloniaProperty.Register<QrCodeView, IBrush>(nameof(Foreground), Brushes.Black);

    public static readonly StyledProperty<IBrush> BackgroundProperty =
        AvaloniaProperty.Register<QrCodeView, IBrush>(nameof(Background), Brushes.White);

    /// <summary>Modules of white space around the code. Four is what the specification asks for.</summary>
    private const int QuietZone = 4;

    private QrCode? _code;
    private string? _encoded;

    static QrCodeView()
    {
        AffectsRender<QrCodeView>(TextProperty, ForegroundProperty, BackgroundProperty);
    }

    /// <summary>What the code says. Null or empty draws nothing at all rather than an empty frame.</summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public IBrush Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public IBrush Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        QrCode? code = Resolve();
        if (code is null || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        int across = code.Size + (QuietZone * 2);

        // Whole pixels per module, and the leftover split evenly. A fractional module size leaves seams
        // between the squares that some scanners read as damage.
        double scale = Math.Floor(Math.Min(Bounds.Width, Bounds.Height) / across);
        if (scale < 1)
        {
            return;
        }

        double drawn = scale * across;
        double left = (Bounds.Width - drawn) / 2;
        double top = (Bounds.Height - drawn) / 2;

        context.FillRectangle(Background, new Rect(left, top, drawn, drawn));

        for (int y = 0; y < code.Size; y++)
        {
            // Runs rather than single squares: a typical code is a few thousand dark modules, and one
            // rectangle per horizontal run is a fraction of the draw calls with the same picture.
            int x = 0;
            while (x < code.Size)
            {
                if (!code.GetModule(x, y))
                {
                    x++;
                    continue;
                }

                int start = x;
                while (x < code.Size && code.GetModule(x, y))
                {
                    x++;
                }

                context.FillRectangle(
                    Foreground,
                    new Rect(
                        left + ((start + QuietZone) * scale),
                        top + ((y + QuietZone) * scale),
                        (x - start) * scale,
                        scale));
            }
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        QrCode? code = Resolve();
        if (code is null)
        {
            return default;
        }

        // Square, and never smaller than one pixel per module: below that a code is unreadable, so asking
        // for less should make the layout give way rather than quietly produce something useless.
        double side = code.Size + (QuietZone * 2);
        double wanted = Math.Max(side, Math.Min(availableSize.Width, availableSize.Height));
        return double.IsInfinity(wanted) ? new Size(side, side) : new Size(wanted, wanted);
    }

    private QrCode? Resolve()
    {
        string? text = Text;
        if (string.IsNullOrEmpty(text))
        {
            _code = null;
            _encoded = null;
            return null;
        }

        if (_code is not null && _encoded == text)
        {
            return _code;
        }

        // Medium correction: a code on a screen is not going to be creased or printed badly, and the lower
        // levels keep it smaller and therefore easier to read across a room.
        _code = QrCode.EncodeText(text, QrCode.Ecc.Medium);
        _encoded = text;
        return _code;
    }
}
