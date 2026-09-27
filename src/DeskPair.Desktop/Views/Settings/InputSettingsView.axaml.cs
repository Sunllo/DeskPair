using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DeskPair.Desktop.Views.Settings;

public partial class InputSettingsView : UserControl
{
    public InputSettingsView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
