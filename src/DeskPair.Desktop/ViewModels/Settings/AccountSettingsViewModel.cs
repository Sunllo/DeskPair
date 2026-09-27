using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskPair.Core.Config;
using DeskPair.Core.Portal;
using DeskPair.Desktop.Engine;
using DeskPair.Desktop.Localization;
using DeskPair.Desktop.Services;
using DeskPair.Platform.Abstractions.Security;

namespace DeskPair.Desktop.ViewModels.Settings;

/// <summary>
/// Signing this installation in to a portal account.
///
/// The password is typed here and goes no further than the one request that spends it: the portal answers
/// with a short-lived code, the app signs that code with the ECDSA P-256 identity key it already holds,
/// and what it keeps is a device token the account holder can revoke from the web console one machine at
/// a time. So nothing an attacker reads off this machine gets them the account.
///
/// It used to ask for the code itself, which meant opening the website, signing in there and copying
/// eight characters across before the app would do anything. The code is still the mechanism; it is just
/// no longer somebody's job.
/// </summary>
public partial class AccountSettingsViewModel : SettingsSectionBase
{
    private readonly Func<Task<AccountLink>> _link;

    public AccountSettingsViewModel(Func<Task<AccountLink>>? link = null)
    {
        _link = link ?? DefaultLinkAsync;
    }

    /// <summary>
    /// Where the portal is, asked for rather than stored here.
    ///
    /// It is a network address, and it lives with the other ones on the Network tab, behind the switch
    /// that says this install is self-hosted. Empty means the official portal, which is what almost
    /// everybody has and what nobody should have to type.
    /// </summary>
    public Func<string>? PortalServer { get; set; }

    /// <summary>The account. Kept on screen only; what is stored is the device token the portal issues.</summary>
    [ObservableProperty]
    public partial string Email { get; set; } = string.Empty;

    /// <summary>Never stored, never logged, and gone from here the moment it has been spent.</summary>
    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    /// <summary>
    /// Whether to offer making an account. Shown until a portal says it does not take them.
    ///
    /// Not the other way round: a portal that cannot be reached has not said no, and hiding the button
    /// then leaves somebody staring at a sign-in for an account they were about to create.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreateAccount))]
    public partial bool CanRegister { get; set; } = true;

    /// <summary>
    /// Whether to show the button, which is both halves: this portal takes accounts, and there is nobody
    /// signed in here yet.
    ///
    /// Derived rather than left to the view, because a binding cannot say "and" -- so the window bound the
    /// half it could, and offered to make an account to somebody who had just signed in to one.
    /// </summary>
    public bool CanCreateAccount => CanRegister && !IsLinked;

    /// <summary>
    /// What this machine is called, for the console's device list.
    ///
    /// Asked for rather than stored, because this screen is not where the name lives. A computer has one
    /// name -- the one on the General page, under "This computer" -- and asking for it a second time here
    /// produced two names that could disagree, with nothing on screen to say which one anybody else saw.
    /// </summary>
    public Func<string>? DeviceName { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreateAccount))]
    public partial bool IsLinked { get; set; }

    [ObservableProperty]
    public partial string AccountText { get; set; } = string.Empty;



    /// <summary>This page says both kinds of thing, and they are shown differently.</summary>
    protected override bool NoticeIsProblem => !NoticeIsGood;

    [ObservableProperty]
    public partial bool NoticeIsGood { get; set; }

    [ObservableProperty]
    public partial bool Busy { get; set; }

    /// <summary>
    /// Nothing here is a stored setting, so nothing here may trigger a save.
    ///
    /// It matters more than it reads: a password is one of these properties, and a save writes config.json.
    /// </summary>
    protected override bool IsTransient(string propertyName) => true;

    public override void Load(DesktopConfig desktop, HostConfig? host)
    {
        ArgumentNullException.ThrowIfNull(desktop);
    }

    /// <summary>Reads the stored link, then asks the portal whether it is still good. Safe to call when offline.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            AccountLink link = await _link();
            LinkState state = await link.CurrentAsync(ct);
            Show(state);
            if (!state.IsLinked)
            {
                return;
            }

            // The stored copy is shown first so the tab is never blank while this is in flight, and a portal
            // that cannot be reached leaves what we knew rather than claiming the device is unlinked.
            Show(await link.RefreshAsync(ct));
        }
        catch (PortalException e)
        {
            Fail(PortalMessage.For(e, Portal()));
        }
    }

    /// <summary>Asks the portal once whether it takes new accounts, so the window knows what to offer.</summary>
    public async Task LoadOptionsAsync(CancellationToken ct = default) =>
        CanRegister = await AccountLink.TakesNewAccountsAsync(Portal(), http: null, ct).ConfigureAwait(false) ?? true;

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (!Complete())
        {
            return;
        }

        await RunAsync(async (link, ct) =>
        {
            AccountSignIn result = await link.SignInAsync(Portal(), Email, Password, MachineName(), ct);
            Spent();
            Done(result);
        });
    }

    [RelayCommand]
    private async Task RegisterAsync()
    {
        if (!Complete())
        {
            return;
        }

        await RunAsync(async (link, ct) =>
        {
            AccountSignIn result = await link.RegisterAsync(Portal(), Email, Password, Strings.Language, MachineName(), ct);
            Spent();
            Done(result);
        });
    }

    /// <summary>
    /// Both halves have to be there before anything is sent.
    ///
    /// Checked here rather than by disabling the button: a button that is grey for a reason nobody stated
    /// is the same as a button that does not work.
    /// </summary>
    private bool Complete()
    {
        if (Email.Trim().Length == 0 || Password.Length == 0)
        {
            Fail(Strings.Get("settings.account.needBoth"));
            return false;
        }

        return true;
    }

    /// <summary>The password has done its work; it does not stay on screen or in memory afterwards.</summary>
    private void Spent() => Password = string.Empty;

    private void Done(AccountSignIn result)
    {
        if (result.NeedsVerification)
        {
            // The account exists. There is simply no code to spend until somebody opens the email, and
            // saying "that did not work" here would be a lie that sends people to support.
            Notice = Strings.Get("settings.account.checkEmail");
            NoticeIsGood = true;
            return;
        }

        // No notice: the window has just switched to "signed in as ...", and a line underneath repeating
        // the address is the same sentence twice. Unlinking still says so, because nothing else does.
        Show(result.State);
        Notice = string.Empty;
    }

    /// <summary>Where to ask. Empty is the official portal, resolved in Core so every caller agrees.</summary>
    private string Portal() => PortalServer?.Invoke() ?? string.Empty;

    [RelayCommand]
    private async Task UnlinkAsync() =>
        await RunAsync(async (link, ct) =>
        {
            PortalException? failure = await link.UnlinkAsync(ct);
            Show(LinkState.Unlinked);

            // The local token is gone either way. Say so plainly when the portal did not hear about it, so
            // the person knows to revoke it from the console as well.
            Notice = failure is null
                ? Strings.Get("settings.account.unlinked")
                : Strings.Format("settings.account.unlinkedLocally", PortalMessage.For(failure, Portal()));
            NoticeIsGood = failure is null;
        });

    private async Task RunAsync(Func<AccountLink, CancellationToken, Task> work)
    {
        if (Busy)
        {
            return;
        }

        Busy = true;
        Notice = string.Empty;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await work(await _link(), cts.Token);
        }
        catch (PortalException e)
        {
            Fail(PortalMessage.For(e, Portal()));
        }
        catch (OperationCanceledException)
        {
            Fail(Strings.Get("settings.account.timedOut"));
        }
        finally
        {
            Busy = false;
        }
    }

    private void Show(LinkState state)
    {
        IsLinked = state.IsLinked;
        Current = state;

        // Told to whoever else is showing something that depends on it -- the device list and the account
        // line along the bottom of the navigation rail, both on pages this screen knows nothing about.
        LinkChanged?.Invoke(state);
        AccountText = state.IsLinked
            ? Strings.Format("settings.account.linkedAs", state.Account, state.Alias.Length > 0 ? state.Alias : MachineName())
            : string.Empty;
    }

    /// <summary>The name for this computer, falling back to the one the OS gives it.</summary>
    private string MachineName()
    {
        string name = DeviceName?.Invoke().Trim() ?? string.Empty;
        return name.Length > 0 ? name : Environment.MachineName;
    }

    private void Fail(string message)
    {
        Notice = message;
        NoticeIsGood = false;
    }

    /// <summary>
    /// The identity key this app already has, read straight out of the keystore.
    ///
    /// Not the engine's instance: the engine may be a different process altogether when this copy adopted
    /// one that was already listening. It is the same data directory and therefore the same key, so reading
    /// it here is reading what the engine uses, not a second identity.
    /// </summary>
    /// <summary>
    /// Raised when this install links to an account or stops being linked.
    ///
    /// Static because the thing that changed is not a property of one settings screen: the device list is
    /// the account's list, and it is on another page that was built before this one existed and will
    /// outlive it. Without this, signing in leaves that page still saying you are not signed in until the
    /// window is reopened.
    /// </summary>
    public static event Action<LinkState>? LinkChanged;

    /// <summary>The last thing anybody found out, so a window opening later does not start out blank.</summary>
    public static LinkState Current { get; private set; } = LinkState.Unlinked;

    /// <summary>Who this install belongs to, without a settings screen in the way.</summary>
    public static async Task<LinkState> CurrentAsync(CancellationToken ct = default)
    {
        try
        {
            AccountLink link = await DefaultLinkAsync().ConfigureAwait(false);
            Current = await link.CurrentAsync(ct).ConfigureAwait(false);
            return Current;
        }
        catch (Exception e) when (e is IOException or HttpRequestException or InvalidOperationException)
        {
            // Unreachable portal, unreadable store: not linked as far as anything on screen can tell, and
            // saying so is better than a page that never resolves.
            return LinkState.Unlinked;
        }
    }

    /// <summary>
    /// The account link for this machine.
    ///
    /// Read, never created. Minting an identity is the engine's alone: this page used to call
    /// LoadOrCreateAsync, and on a machine that had no identity yet it would race the engine on the way
    /// up and decide what the machine was. A Mac carrying its identity over from the product's previous
    /// name lost it exactly that way, to a key written in the same second the window opened.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// This machine has no identity yet, which callers already treat as "not linked": there is nothing to
    /// link an account to until the engine has started once.
    /// </exception>
    private static async Task<AccountLink> DefaultLinkAsync()
    {
        ISecretStore secrets = PlatformServices.SecretStoreFor(ServerRole.DefaultDataDir());
        PeerIdentityStore identity = await PeerIdentityStore.LoadAsync(secrets, new NullMachineId())
            ?? throw new InvalidOperationException("This computer has no identity yet; the host engine makes one when it first starts.");
        return new AccountLink(identity, secrets);
    }

    /// <summary>Only the signing key is wanted here; the machine id belongs to the engine's registration.</summary>
    private sealed class NullMachineId : IMachineIdProvider
    {
        public byte[] GetStableMachineId() => [];
    }
}
