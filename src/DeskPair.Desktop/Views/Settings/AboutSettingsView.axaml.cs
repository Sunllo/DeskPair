using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DeskPair.Desktop.Views.Settings;

public partial class AboutSettingsView : UserControl
{
    public AboutSettingsView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
