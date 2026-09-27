using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DeskPair.Desktop.Views.Settings;

public partial class NetworkSettingsView : UserControl
{
    public NetworkSettingsView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
