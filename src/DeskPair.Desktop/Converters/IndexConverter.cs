using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace DeskPair.Desktop.Converters;

/// <summary>
/// Ties one radio button to one value of an index property: the button is checked while the index equals the
/// converter parameter, and checking it sets the index. Being unchecked says nothing, because the button that
/// was just checked is what carries the new value.
/// </summary>
public sealed class IndexConverter : IValueConverter
{
    public static IndexConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int index && Parse(parameter) == index;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Parse(parameter) : BindingOperations.DoNothing;

    private static int Parse(object? parameter) => parameter switch
    {
        int i => i,
        string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) => parsed,
        _ => -1,
    };
}
