using DeskPair.Core.Config;
using DeskPair.Core.Session.Host;
using DeskPair.Core.Session.Host.Auth;
using DeskPair.Platform.Abstractions.Security;
using DeskPair.Protocol.Crypto;
using DeskPair.Protocol.Messages;

namespace DeskPair.Core.Tests;

/// <summary>The host settings file and the password rules the settings screen drives.</summary>
public class HostSettingsTests
{
    [Fact]
    public void New_settings_round_trip_and_keep_their_defaults()
    {
        var config = new HostConfig
        {
            DeviceName = "Reception PC",
            TemporaryPasswordEnabled = false,
            TemporaryPasswordLength = 10,
            TemporaryRotationThreshold = 3,
            RotateTemporaryAfterSession = true,
            ForceRelay = true,
            ApprovalTimeoutSeconds = 45,
            AllowPasswordInClickMode = false,
            SecureDesktopForListedOnly = true,
        };

        HostConfig back = HostConfig.FromJson(config.ToJson());
        back.ShouldBe(config);

        // A file written before the setting existed reads it as off, not on.
        HostConfig.FromJson("""{"rendezvousServer":"example:21116"}""").SecureDesktopForListedOnly.ShouldBeFalse();

        // A file written by an older build has none of these keys.
        HostConfig legacy = HostConfig.FromJson("""{"rendezvousServer":"example:21116"}""");
        legacy.DeviceName.ShouldBe(string.Empty);
        legacy.KeyboardEnabled.ShouldBeTrue(); // a setting missing from the file keeps its default, it is not "off"
        legacy.TemporaryPasswordEnabled.ShouldBeTrue();
        legacy.TemporaryPasswordLength.ShouldBe(6);
        legacy.TemporaryRotationThreshold.ShouldBe(10);
        legacy.ApprovalTimeoutSeconds.ShouldBe(30);
        legacy.AllowPasswordInClickMode.ShouldBeTrue();
        legacy.ForceRelay.ShouldBeFalse();
    }

    [Fact]
    public void Policy_carries_the_approval_settings_and_the_device_name()
    {
        var config = new HostConfig { DeviceName = "Reception PC", ApprovalTimeoutSeconds = 45, AllowPasswordInClickMode = false };
        HostPolicy policy = config.ToPolicy();
        policy.HostName.ShouldBe("Reception PC");
        policy.ApprovalTimeout.ShouldBe(TimeSpan.FromSeconds(45));
        policy.AllowPasswordInClickMode.ShouldBeFalse();

        new HostConfig().ToPolicy().HostName.ShouldBe(Environment.MachineName);
        // Out-of-range values from a hand-edited file are clamped rather than trusted.
        new HostConfig { ApprovalTimeoutSeconds = 5000 }.ToPolicy().ApprovalTimeout.ShouldBe(TimeSpan.FromSeconds(300));
    }

    [Theory]
    [InlineData("RendezvousServer", true)]
    [InlineData("ServerPublicKeyBase64", true)]
    [InlineData("DirectAccessPort", true)]
    [InlineData("DirectAccessEnabled", true)]
    [InlineData("UdpMedia", true)]
    [InlineData("CodecPreference", true)]
    [InlineData("ApproveMode", false)]
    [InlineData("KeyboardEnabled", false)]
    [InlineData("TemporaryPasswordLength", false)]
    [InlineData("ForceRelay", false)]
    [InlineData("DeviceName", false)]
    // The allowlist is deliberately live: a machine that has just been locked down must be locked down now,
    // not after somebody remembers to restart it.
    [InlineData("AllowlistEnabled", false)]
    [InlineData("AllowedPeers", false)]
    [InlineData("RefuseRelayed", false)]
    [InlineData("TerminalEnabled", false)]
    [InlineData("TerminalRunsAs", false)]
    [InlineData("MaxDisplaysPerViewer", false)]
    [InlineData("MaxConcurrentStreams", false)]
    [InlineData("AllowVirtualDisplay", false)]
    [InlineData("SecureDesktopForListedOnly", false)]
    public void Only_transport_settings_need_the_engine_restarted(string field, bool expected)
    {
        var before = new HostConfig();
        HostConfig after = field switch
        {
            "RendezvousServer" => before with { RendezvousServer = "example:21116" },
            "ServerPublicKeyBase64" => before with { ServerPublicKeyBase64 = "AAAA" },
            "DirectAccessPort" => before with { DirectAccessPort = 21200 },
            "DirectAccessEnabled" => before with { DirectAccessEnabled = false },
            "UdpMedia" => before with { UdpMedia = false },
            "CodecPreference" => before with { CodecPreference = "h265" },
            "ApproveMode" => before with { ApproveMode = ApproveMode.ApproveClick },
            "KeyboardEnabled" => before with { KeyboardEnabled = false },
            "TemporaryPasswordLength" => before with { TemporaryPasswordLength = 10 },
            "ForceRelay" => before with { ForceRelay = true },
            "AllowlistEnabled" => before with { AllowlistEnabled = true },
            "AllowedPeers" => before with { AllowedPeers = ["192.168.1.0/24"] },
            "RefuseRelayed" => before with { RefuseRelayed = true },
            "TerminalEnabled" => before with { TerminalEnabled = true },
            "TerminalRunsAs" => before with { TerminalRunsAs = "user" },
            "MaxDisplaysPerViewer" => before with { MaxDisplaysPerViewer = 3 },
            "MaxConcurrentStreams" => before with { MaxConcurrentStreams = 6 },
            "AllowVirtualDisplay" => before with { AllowVirtualDisplay = true },
            "SecureDesktopForListedOnly" => before with { SecureDesktopForListedOnly = true },
            "DeviceName" => before with { DeviceName = "Reception PC" },
            _ => throw new ArgumentException($"no case for {field}", nameof(field)),
        };

        after.ShouldNotBe(before, $"the case for {field} must change something");
        HostConfig.RequiresEngineRestart(before, after).ShouldBe(expected);
    }

    /// <summary>
    /// A shell is the whole machine, so it is off until the owner turns it on -- including for a settings file
    /// written before the switch existed, which must not read as anything but off.
    /// </summary>
    [Fact]
    public void The_terminal_is_off_until_the_owner_turns_it_on()
    {
        new HostConfig().TerminalEnabled.ShouldBeFalse();
        new HostConfig().ToPolicy().IsPermissionEnabled(Permission.PermTerminal).ShouldBeFalse();
        HostConfig.FromJson("""{"keyboardEnabled": true}""").TerminalEnabled.ShouldBeFalse();
        HostConfig.FromJson("""{"terminalEnabled": true}""").ToPolicy().IsPermissionEnabled(Permission.PermTerminal).ShouldBeTrue();
    }

    [Fact]
    public async Task A_chosen_temporary_password_works_and_survives_wrong_attempts()
    {
        using var store = new TempSecrets();
        HostPasswords passwords = await HostPasswords.LoadAsync(store.Store);
        await passwords.SetTemporaryAsync("meeting24");

        passwords.TemporaryPassword.ShouldBe("meeting24");
        passwords.TemporaryPinned.ShouldBeTrue();
        Match(passwords, "meeting24").ShouldBe(PasswordMatch.Temporary);

        GuessFromEverywhere(passwords, 20);

        passwords.TemporaryPassword.ShouldBe("meeting24"); // a password the user picked is never replaced

        // It is also still there after a restart, which is the point of choosing one.
        HostPasswords reloaded = await HostPasswords.LoadAsync(store.Store);
        reloaded.TemporaryPassword.ShouldBe("meeting24");

        await reloaded.SetTemporaryAsync(null);
        reloaded.TemporaryPinned.ShouldBeFalse();
        reloaded.TemporaryPassword.ShouldNotBe("meeting24");
    }

    [Fact]
    public async Task A_short_temporary_password_is_refused()
    {
        using var store = new TempSecrets();
        HostPasswords passwords = await HostPasswords.LoadAsync(store.Store);
        await Should.ThrowAsync<ArgumentException>(() => passwords.SetTemporaryAsync("abc"));
    }

    [Fact]
    public async Task Disabling_the_temporary_password_refuses_it_and_keeps_the_permanent_one()
    {
        using var store = new TempSecrets();
        HostPasswords passwords = await HostPasswords.LoadAsync(store.Store);
        await passwords.SetPermanentAsync("permanent1");
        string temporary = passwords.TemporaryPassword;

        passwords.Configure(length: 6, rotationThreshold: 10, enabled: false);
        Match(passwords, temporary).ShouldBe(PasswordMatch.None);
        Match(passwords, "permanent1").ShouldBe(PasswordMatch.Permanent);

        passwords.Configure(length: 6, rotationThreshold: 10, enabled: true);
        Match(passwords, temporary).ShouldBe(PasswordMatch.Temporary);
    }

    [Fact]
    public async Task Changing_the_length_generates_a_new_password_of_that_length()
    {
        using var store = new TempSecrets();
        HostPasswords passwords = await HostPasswords.LoadAsync(store.Store);
        passwords.TemporaryPassword.Length.ShouldBe(6);

        passwords.Configure(length: 10, rotationThreshold: 4, enabled: true);
        passwords.TemporaryPassword.Length.ShouldBe(10);

        // Three addresses guessing wrong are tolerated; the fourth replaces the password.
        string before = passwords.TemporaryPassword;
        GuessFromEverywhere(passwords, 3);
        passwords.TemporaryPassword.ShouldBe(before);
        GuessFromEverywhere(passwords, 4);

        passwords.TemporaryPassword.ShouldNotBe(before);
    }

    [Fact]
    public async Task The_link_password_is_its_own_secret_and_is_spent_when_it_is_used()
    {
        using var store = new TempSecrets();
        HostPasswords passwords = await HostPasswords.LoadAsync(store.Store);

        string link = passwords.LinkPassword;
        string temporary = passwords.TemporaryPassword;

        // Not the password on screen. If these were ever the same, a photograph of the code would be the
        // password the user reads aloud, and spending one would move the other.
        link.ShouldNotBe(temporary);
        link.Length.ShouldBe(12);
        Match(passwords, link).ShouldBe(PasswordMatch.Link);

        passwords.SpendLink();

        // The code has been used, so it no longer works — and the one on the desk has not moved, because
        // somebody who read it aloud is still expected to be able to open a second session with it.
        Match(passwords, link).ShouldBe(PasswordMatch.None);
        passwords.LinkPassword.ShouldNotBe(link);
        passwords.TemporaryPassword.ShouldBe(temporary);
        Match(passwords, passwords.LinkPassword).ShouldBe(PasswordMatch.Link);
    }

    [Fact]
    public async Task A_pinned_temporary_password_does_not_pin_the_link_password()
    {
        using var store = new TempSecrets();
        HostPasswords passwords = await HostPasswords.LoadAsync(store.Store);
        await passwords.SetTemporaryAsync("meeting24");
        passwords.Configure(length: 6, rotationThreshold: 4, enabled: true);

        string link = passwords.LinkPassword;
        GuessFromEverywhere(passwords, 4);

        // Pinning is a decision about the password the user reads out. Nobody reads this one, so guessing
        // moves it — otherwise a pinned temporary password would leave the code guessable indefinitely.
        passwords.LinkPassword.ShouldNotBe(link);
        passwords.TemporaryPassword.ShouldBe("meeting24");
    }

    [Fact]
    public async Task Switching_temporary_passwords_off_switches_the_link_password_off_too()
    {
        using var store = new TempSecrets();
        HostPasswords passwords = await HostPasswords.LoadAsync(store.Store);
        await passwords.SetPermanentAsync("permanent1");
        string link = passwords.LinkPassword;

        // "Off" has to mean the permanent password and nothing else. A link password that kept working
        // would be a second generated password still letting people in after they turned generated
        // passwords off.
        passwords.Configure(length: 6, rotationThreshold: 10, enabled: false);
        Match(passwords, link).ShouldBe(PasswordMatch.None);
        Match(passwords, "permanent1").ShouldBe(PasswordMatch.Permanent);
    }

    private static PasswordMatch Match(HostPasswords passwords, string password, string source = "203.0.113.1")
    {
        byte[] challenge = PasswordProof.NewSalt();
        byte[] h1 = PasswordProof.ComputeH1(password, passwords.Salt, passwords.Kdf, passwords.Iterations);
        return passwords.Verify(challenge, PasswordProof.ComputeProof(h1, challenge), source);
    }

    /// <summary>A wrong guess from a different address each time.</summary>
    private static void GuessFromEverywhere(HostPasswords passwords, int addresses)
    {
        for (int i = 0; i < addresses; i++)
        {
            Match(passwords, "wrong", $"198.51.100.{i + 1}").ShouldBe(PasswordMatch.None);
        }
    }

    [Fact]
    public async Task One_address_guessing_wrong_does_not_move_the_password_under_the_person_reading_it_out()
    {
        using var store = new TempSecrets();
        HostPasswords passwords = await HostPasswords.LoadAsync(store.Store);
        passwords.Configure(length: 6, rotationThreshold: 4, enabled: true);
        string before = passwords.TemporaryPassword;
        string link = passwords.LinkPassword;

        for (int i = 0; i < 10; i++)
        {
            Match(passwords, "wrong", "203.0.113.7").ShouldBe(PasswordMatch.None);
        }

        // The backoff is what holds that address off; the password on the desk stays where it was.
        passwords.TemporaryPassword.ShouldBe(before);
        passwords.LinkPassword.ShouldBe(link);

        // A fourth distinct address guessing is the threshold, so three more from elsewhere still do not move it.
        GuessFromEverywhere(passwords, 2);
        passwords.TemporaryPassword.ShouldBe(before);
        Match(passwords, "wrong", "198.51.100.3").ShouldBe(PasswordMatch.None);
        passwords.TemporaryPassword.ShouldNotBe(before);
    }

    private sealed class TempSecrets : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "sunllo-secrets-" + Guid.NewGuid().ToString("N"));

        public TempSecrets()
        {
            Store = new FileSecretStore(_dir);
        }

        public ISecretStore Store { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
