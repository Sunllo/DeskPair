using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using DeskPair.Desktop.Services;
using DeskPair.Desktop.ViewModels;
using DeskPair.Desktop.ViewModels.Settings;

namespace DeskPair.Desktop.Tests;

/// <summary>
/// The account sits at the foot of the navigation rail, and this computer is named in one place.
///
/// It used to take two screens to answer one question each: who this copy is signed in as was written
/// only on the account tab, and the name this computer goes by was asked for twice -- once on the general
/// tab as "the name shown to visitors" and again on the account tab as "the name for this computer".
/// Two fields, two stores, and nothing on screen to say which of the two names anybody else saw.
///
/// The account tab is gone: the rail says who is signed in, and clicking it opens a window.
/// </summary>
public class AccountRailTests
{
    /// <summary>
    /// A settings screen whose clock never moves, which is not a detail.
    ///
    /// Settings save as they are typed: a change starts a 500 ms timer, and when it fires the screen
    /// writes App.Config to %APPDATA%\Sunllo\DeskPair\desktop.json -- the real one, on whatever machine
    /// the tests are running on. With the system clock these tests reached in and set the developer's own
    /// portal address to "portal.example:21120", which was then found by someone trying to sign in.
    ///
    /// A fake clock that is never advanced means the timer is armed and never fires, so the save path is
    /// exercised no further than the product arms it, and nothing is written anywhere.
    /// </summary>
    private static SettingsViewModel Create() =>
        new(new HostLink("ui", NullLogger.Instance),
            post: action => action(),
            time: new FakeTimeProvider(DateTimeOffset.UnixEpoch));

    /// <summary>
    /// The account tab asks the general tab for the name rather than keeping one of its own.
    ///
    /// Deleting that one line leaves a build that passes and a product that silently registers every
    /// machine under whatever the OS calls it, which is exactly the symptom nobody reports.
    /// </summary>
    [Fact]
    public void Linking_uses_the_name_set_on_the_general_tab()
    {
        SettingsViewModel settings = Create();
        settings.General.DeviceName = "Front desk";

        settings.Account.DeviceName.ShouldNotBeNull();
        settings.Account.DeviceName!().ShouldBe("Front desk");
    }

    /// <summary>
    /// The account is not a settings tab any more, and nothing may quietly put it back.
    ///
    /// Two doors to the same room is the failure this replaced: a tab and a window, each able to sign in,
    /// each having to be found before the other could be trusted.
    /// </summary>
    [Fact]
    public void The_account_is_not_one_of_the_settings_tabs()
    {
        IEnumerable<string> headers = SettingsXaml().Descendants()
            .Where(e => e.Name.LocalName == "TabItem")
            .Select(e => (string?)e.Attribute("Header") ?? string.Empty);

        headers.ShouldNotContain(h => h.Contains("settings.tab.account", StringComparison.Ordinal));
    }

    /// <summary>
    /// The portal address is saved from the Network tab, where it now lives.
    ///
    /// It moved off the sign-in window because it read as something to fill in before signing in, which it
    /// is not: empty means the official portal. What must not happen in the move is that it stops being
    /// written anywhere -- a field that works until the app is restarted.
    /// </summary>
    [Fact]
    public void The_portal_address_is_saved_from_the_network_tab()
    {
        SettingsViewModel settings = Create();
        settings.Network.PortalServer = "portal.example:21120";

        settings.Network.Apply(new DesktopConfig()).PortalServer.ShouldBe("portal.example:21120");
    }

    /// <summary>
    /// A private portal survives the self-hosted switch being turned off.
    ///
    /// The rendezvous address and its key are deliberately cleared there, so a stored one cannot silently
    /// beat what the directory answers. The portal is not the same thing: a device already linked to one
    /// goes on syncing with it, and clearing the address would point the next sign-in somewhere else
    /// without saying so.
    /// </summary>
    [Fact]
    public void Turning_self_hosting_off_does_not_forget_which_portal_the_account_is_on()
    {
        SettingsViewModel settings = Create();
        settings.Network.PortalServer = "portal.example:21120";
        settings.Network.SelfHosted = false;

        DesktopConfig saved = settings.Network.Apply(new DesktopConfig());

        saved.PortalServer.ShouldBe("portal.example:21120");
        saved.RendezvousServer.ShouldBeEmpty();
    }

    /// <summary>
    /// Nothing on the sign-in window is a stored setting, and one of its fields is a password.
    ///
    /// Every section turns a property change into a save, and a save writes config.json. While the portal
    /// address lived here, this section deliberately let changes through -- so the rule has to be the
    /// other way round now, and stay that way.
    /// </summary>
    [Fact]
    public void Nothing_typed_into_the_sign_in_window_is_written_to_disk()
    {
        SettingsViewModel settings = Create();
        var saves = 0;
        ((ISettingsSection)settings.Account).Changed += () => saves++;

        settings.Account.Email = "someone@example.test";
        settings.Account.Password = "correct-horse-battery-staple";

        saves.ShouldBe(0);

        // The same object back, not an equal one: this section has nothing to write, so it does not copy
        // the config to say so. (Two equal DesktopConfigs are not equal anyway -- Recent is a list, and
        // record equality compares it by reference.)
        var config = new DesktopConfig();
        settings.Account.Apply(config).ShouldBeSameAs(config);
    }

    /// <summary>
    /// The create-account button is offered until a portal says it takes no new accounts.
    ///
    /// It was the other way round, and the first person to open the window saw no such button: the portal
    /// was unreachable, unreachable was read as "no", and the only way to make an account disappeared at
    /// exactly the moment somebody wanted one. A portal that cannot be reached has not said no.
    /// </summary>
    [Fact]
    public void Making_an_account_is_offered_before_any_portal_has_been_asked()
    {
        Create().Account.CanCreateAccount.ShouldBeTrue();
    }

    /// <summary>
    /// And not offered to somebody who has just signed in to one.
    ///
    /// The window bound the button to "this portal takes new accounts" alone, because a binding cannot say
    /// "and" -- so it went on offering to create an account underneath a panel saying who was signed in.
    /// </summary>
    [Fact]
    public void Making_an_account_is_not_offered_to_somebody_already_signed_in()
    {
        SettingsViewModel settings = Create();
        settings.Account.CanRegister = true;
        List<string?> announced = [];
        settings.Account.PropertyChanged += (_, e) => announced.Add(e.PropertyName);

        settings.Account.IsLinked = true;

        settings.Account.CanCreateAccount.ShouldBeFalse();

        // And said out loud. A derived property that is right but silent leaves the button on screen,
        // which is indistinguishable from not having derived it at all.
        announced.ShouldContain(nameof(AccountSettingsViewModel.CanCreateAccount));
    }

    /// <summary>The settings screen as it is on disk; the row of tabs is not readable any other way.</summary>
    private static XDocument SettingsXaml()
    {
        string here = AppContext.BaseDirectory;
        string? repo = here;
        while (repo is not null && !Directory.Exists(Path.Combine(repo, "src")))
        {
            repo = Path.GetDirectoryName(repo);
        }

        return XDocument.Load(Path.Combine(repo ?? here, "src", "DeskPair.Desktop", "Views", "SettingsView.axaml"));
    }
}
