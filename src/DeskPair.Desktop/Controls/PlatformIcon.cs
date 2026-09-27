using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DeskPair.Desktop.Controls;

/// <summary>
/// The logo of whatever a peer is running, drawn from the platform string it reported. Every icon in
/// Assets/PlatformIcons.axaml is a single shape, so the colour is this control's rather than the artwork's
/// and the icons sit in the interface instead of on top of it.
/// </summary>
public sealed class PlatformIcon : Control
{
    public static readonly StyledProperty<string?> PlatformProperty =
        AvaloniaProperty.Register<PlatformIcon, string?>(nameof(Platform));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextBlock.ForegroundProperty.AddOwner<PlatformIcon>();

    private Geometry? _geometry;

    static PlatformIcon()
    {
        AffectsRender<PlatformIcon>(PlatformProperty, ForegroundProperty);
        AffectsMeasure<PlatformIcon>(PlatformProperty);
    }

    /// <summary>What the peer called itself: "Windows", "macOS", "Ubuntu 24.04", "Android", and so on.</summary>
    public string? Platform
    {
        get => GetValue(PlatformProperty);
        set => SetValue(PlatformProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        Geometry? geometry = Resolve();
        if (geometry is null || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        Rect source = geometry.Bounds;
        if (source.Width <= 0 || source.Height <= 0)
        {
            return;
        }

        // Every icon comes from its own viewBox, so each is fitted to this control rather than trusted to
        // arrive at a common size.
        double scale = Math.Min(Bounds.Width / source.Width, Bounds.Height / source.Height);
        var transform = Matrix.CreateTranslation(-source.X, -source.Y)
            * Matrix.CreateScale(scale, scale)
            * Matrix.CreateTranslation(
                (Bounds.Width - (source.Width * scale)) / 2,
                (Bounds.Height - (source.Height * scale)) / 2);

        using (context.PushTransform(transform))
        {
            context.DrawGeometry(Foreground ?? Brushes.Gray, null, geometry);
        }
    }

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? 16 : availableSize.Width,
        double.IsInfinity(availableSize.Height) ? 16 : availableSize.Height);

    private Geometry? Resolve()
    {
        if (_geometry is not null && _cachedFor == Platform)
        {
            return _geometry;
        }

        _cachedFor = Platform;
        _geometry = null;
        if (PlatformIcons.KeyFor(Platform) is { } key
            && Application.Current?.TryFindResource(key, out object? found) == true
            && found is Geometry geometry)
        {
            _geometry = geometry;
        }

        return _geometry;
    }

    private string? _cachedFor;
}

/// <summary>Maps the platform string a peer reports onto one of the icons in Assets/PlatformIcons.axaml.</summary>
public static class PlatformIcons
{
    /// <summary>
    /// The resource key for a platform, or null when there is no icon for it — an unrecognised string shows
    /// nothing rather than something misleading. Order matters: the Linux distributions are matched before
    /// plain Linux, so "Ubuntu 24.04" gets its own logo and "Linux 6.8" gets the penguin.
    /// </summary>
    public static string? KeyFor(string? platform)
    {
        if (string.IsNullOrWhiteSpace(platform))
        {
            return null;
        }

        string p = platform.ToLowerInvariant();
        return
            p.Contains("windows") ? "icon.windows" :
            p.Contains("android") ? "icon.android" :
            p.Contains("ios") || p.Contains("iphone") || p.Contains("ipad") ? "icon.ios" :
            p.Contains("mac") || p.Contains("darwin") || p.Contains("osx") ? "icon.macos" :
            p.Contains("ubuntu") ? "icon.ubuntu" :
            p.Contains("debian") || p.Contains("raspbian") ? "icon.debian" :
            p.Contains("red hat") || p.Contains("redhat") || p.Contains("rhel") || p.Contains("fedora")
                || p.Contains("centos") || p.Contains("rocky") || p.Contains("almalinux") ? "icon.redhat" :
            p.Contains("linux") ? "icon.linux" :
            null;
    }
}
