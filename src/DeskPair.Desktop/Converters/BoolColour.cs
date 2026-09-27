using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace DeskPair.Desktop.Converters;

/// <summary>A true/false flag as one of two brushes, for the small state dots.</summary>
public sealed class BoolColour(string whenTrue, string whenFalse) : IValueConverter
{
    /// <summary>Green once a connection is in, amber while it is still being checked.</summary>
    public static BoolColour GreenAmber { get; } = new("#4CAF50", "#C9A227");

    /// <summary>The account avatar: the app's own blue when somebody is signed in, grey when nobody is.</summary>
    public static BoolColour BlueGrey { get; } = new("#2E6BD6", "#39414D");

    private readonly IBrush _true = SolidColorBrush.Parse(whenTrue);
    private readonly IBrush _false = SolidColorBrush.Parse(whenFalse);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? _true : _false;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Avalonia.Data.BindingOperations.DoNothing;
}
