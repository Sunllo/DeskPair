using Microsoft.Extensions.Logging.Abstractions;
using DeskPair.Desktop.Services;
using DeskPair.Protocol.Ipc;

namespace DeskPair.Desktop.Tests;

/// <summary>The app's side of being told no by the engine.</summary>
public class HostLinkTests
{
    /// <summary>
    /// A refusal is remembered as what it is -- this account is not the owner here -- and not as a lost engine, which
    /// is what the silence that came before it was taken for: the home page said "host service not running" while the
    /// service ran fine.
    /// </summary>
    [Fact]
    public void A_refusal_says_this_window_is_not_the_owner()
    {
        var host = new HostLink("ui", NullLogger.Instance);
        var heard = new List<bool>();
        host.OwnerChanged += heard.Add;
        host.IsOwner.ShouldBeTrue("nothing has said otherwise yet");

        host.Deliver(new IpcMessage { Refused = new IpcRefused { Reason = "signed in to session 2, which is not the console" } });
        host.Deliver(new IpcMessage { Refused = new IpcRefused { Reason = "signed in to session 2, which is not the console" } });

        host.IsOwner.ShouldBeFalse();
        heard.ShouldBe([false], "said once, when it changed");
    }

    /// <summary>
    /// What the link was shown as the owner -- on an earlier connection, before the engine restarted -- does not stay
    /// on the screen of an account the engine has since refused.
    /// </summary>
    [Fact]
    public void A_refusal_takes_away_what_only_the_owner_may_see()
    {
        var host = new HostLink("ui", NullLogger.Instance);
        host.Deliver(new IpcMessage { TempPassword = new TempPassword { Password = "k7q2m9" } });
        host.Deliver(new IpcMessage { ConfigSnapshot = new ConfigSnapshot { Json = new Core.Config.HostConfig().ToJson() } });
        host.Deliver(new IpcMessage { PasswordState = new PasswordState { TemporaryEnabled = true, TemporaryPassword = "k7q2m9" } });
        string? shown = null;
        host.TempPasswordChanged += password => shown = password;

        host.Deliver(new IpcMessage { Refused = new IpcRefused { Reason = "signed in to session 2, which is not the console" } });

        host.TempPassword.ShouldBeEmpty();
        shown.ShouldBe(string.Empty, "the screen showing the password is told it is gone");
        host.Config.ShouldBeNull();
        host.PasswordState.ShouldBeNull();
    }
}
