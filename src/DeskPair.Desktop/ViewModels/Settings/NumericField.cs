using CommunityToolkit.Mvvm.ComponentModel;
using DeskPair.Desktop.Localization;

namespace DeskPair.Desktop.ViewModels.Settings;

/// <summary>
/// A number the user is typing. <see cref="Text"/> is whatever is in the box, <see cref="Value"/> is the last
/// text that parsed inside the allowed range. Settings save as you type, so a half-typed number ("", "2" on the
/// way to "20") must not be treated as an error and must never reach the stored settings.
/// </summary>
public sealed partial class NumericField : ObservableObject
{
    private readonly int _min;
    private readonly int _max;
    private readonly string _problemKey;
    private bool _resetting;

    public NumericField(int min, int max, int value, string problemKey)
    {
        _min = min;
        _max = max;
        _problemKey = problemKey;
        Value = Math.Clamp(value, min, max);
        Text = Value.ToString();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem))]
    public partial string Text { get; set; }

    public int Value { get; private set; }

    /// <summary>A message while the box holds something that is not a usable number; null while it is fine.</summary>
    public string? Problem { get; private set; }

    /// <summary>Raised only when the usable value actually changed.</summary>
    public event Action? ValueChanged;

    /// <summary>Puts a stored value back in the box without raising a change.</summary>
    public void Reset(int value)
    {
        Value = Math.Clamp(value, _min, _max);
        Problem = null;
        _resetting = true;
        Text = Value.ToString();
        _resetting = false;
        OnPropertyChanged(nameof(Problem));
    }

    partial void OnTextChanged(string value)
    {
        if (_resetting)
        {
            return;
        }

        if (int.TryParse(value.Trim(), out int parsed) && parsed >= _min && parsed <= _max)
        {
            Problem = null;
            if (parsed != Value)
            {
                Value = parsed;
                ValueChanged?.Invoke();
            }

            return;
        }

        // Empty or out of range: keep the last good value and say why, without blocking anything else.
        Problem = value.Trim().Length == 0 ? null : Strings.Format(_problemKey, _min, _max);
    }
}
