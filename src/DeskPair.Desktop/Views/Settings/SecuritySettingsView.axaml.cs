using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DeskPair.Desktop.Views.Settings;

public partial class SecuritySettingsView : UserControl
{
    public SecuritySettingsView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
