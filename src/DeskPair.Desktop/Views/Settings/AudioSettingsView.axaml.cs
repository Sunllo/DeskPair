using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DeskPair.Desktop.Views.Settings;

public partial class AudioSettingsView : UserControl
{
    public AudioSettingsView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
