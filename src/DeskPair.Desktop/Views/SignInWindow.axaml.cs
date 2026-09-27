using Avalonia.Controls;
using Avalonia.Input;
using DeskPair.Desktop.ViewModels.Settings;

namespace DeskPair.Desktop.Views;

/// <summary>
/// Signing in, in a window of its own.
///
/// It was a settings tab, which made the account a setting -- something you go and configure, alongside
/// the recording folder. It is not: it is who this copy belongs to, and the rest of the app asks about it
/// by name. So the rail says who is signed in, and clicking that opens this over whatever page is showing,
/// instead of navigating away from it.
/// </summary>
public partial class SignInWindow : Window
{
    /// <summary>
    /// InitializeComponent is the generated one, deliberately.
    ///
    /// Writing it here as AvaloniaXamlLoader.Load(this) -- which is what the pages with no named controls
    /// in them do -- loads the XAML but leaves every x:Name field null, because assigning them is the other
    /// half of the generated method. The build is clean and the window throws the moment it is opened.
    /// </summary>
    public SignInWindow() => InitializeComponent();

    public SignInWindow(AccountSettingsViewModel account)
        : this()
    {
        DataContext = account;

        // There is no close button: the title bar has one, and a modal dialog is expected to go away on
        // Escape. A button that only did what the window's own chrome already does was one more thing to
        // read on a window with four things on it.
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        };

        Opened += (_, _) =>
        {
            EmailBox.Focus();

            // The tab never asked the portal whether the stored link was still good, so an account revoked
            // from the web console still read as linked here until the app was restarted. Opening the
            // window is the moment to find out, and it is safe offline: what is known stays on screen.
            _ = account.RefreshAsync();

            // And whether this portal takes new accounts, which decides if there is a button for it.
            _ = account.LoadOptionsAsync();
        };
    }
}
