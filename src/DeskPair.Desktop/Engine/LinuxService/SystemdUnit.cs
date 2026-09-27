using System.Text;
using DeskPair.Desktop.Services;

namespace DeskPair.Desktop.Engine.LinuxService;

/// <summary>
/// The systemd unit that makes this desk reachable when nobody is signed in and when the screen is locked.
///
/// Generated rather than shipped as a template, for the reason <c>MacLaunchdJobs</c> gives: the path, the
/// argv and the reasons stay together, and every directive below can be pinned by a test on a machine
/// with no systemd.
///
/// It is a root daemon, and on Linux that is not optional. Neither the login screen nor the lock screen
/// is in X: both are drawn by the compositor straight into the framebuffer, and reading that framebuffer
/// out from under a compositor takes CAP_SYS_ADMIN. The daemon does that, creates the virtual keyboard
/// and pointer, hands the descriptors to an engine that runs as an ordinary account, and nothing else.
/// </summary>
internal static class SystemdUnit
{
    /// <summary>The unit text for the daemon at <paramref name="executable"/>.</summary>
    public static string Daemon(string executable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);

        var text = new StringBuilder();
        text.AppendLine("[Unit]");
        text.AppendLine("Description=Sunllo DeskPair unattended access");

        // logind answers "who is at the screen", and the seat has to exist before the first look means
        // anything. modules-load is here because /dev/uinput does not exist until the module is in.
        text.AppendLine("After=systemd-logind.service systemd-user-sessions.service systemd-modules-load.service");
        text.AppendLine("Wants=systemd-logind.service");

        // Deliberately not After=display-manager.service. gdm is a session this watches for, not a
        // dependency: the machine most in need of being reached remotely is the one whose display manager
        // did not come up.

        // Five failures in five minutes, then stop: a start-limit-hit is a legible state, a core pinned for
        // ever by a crash loop is not. Windows gives up after three tries for the same reason. Recovery is
        // `systemctl reset-failed sunllo-deskpair`.
        text.AppendLine("StartLimitIntervalSec=300");
        text.AppendLine("StartLimitBurst=5");
        text.AppendLine();

        text.AppendLine("[Service]");
        text.AppendLine("Type=simple");

        // The executable directly, never wrapped in /bin/sh -c: a wrapper makes sh the program systemd
        // started and the program any future signature check sees.
        text.AppendLine($"ExecStart={Quote(executable)} --service");

        // root on purpose, and only for what needs it. The engine this starts gives root up in
        // PrivilegeDrop before it opens a single file.
        text.AppendLine("User=root");

        // /run/deskpair for the socket and the per-user token directories; created on start, cleaned on
        // stop, and kept across a restart so an attached app does not lose the token it is holding.
        text.AppendLine("RuntimeDirectory=deskpair");
        text.AppendLine("RuntimeDirectoryMode=0755");
        text.AppendLine("RuntimeDirectoryPreserve=restart");

        // Not the hardening block from deploy/install.sh. That one is for the rendezvous and relay servers,
        // which serve the public internet and need nothing from this machine, and two of its lines break a
        // desktop daemon outright:
        //
        //   ProtectHome=true   the X cookie lives under the user's home on LightDM, SDDM and startx
        //                      (~/.Xauthority). GDM on the verification machine keeps it in /run/user/
        //                      instead, so this would pass there and fail on the first machine that is not
        //                      it, which is the worst place to find out.
        //   PrivateTmp=true    /tmp/.X11-unix/X0 is the X socket. A private /tmp is an empty one.
        //
        // ProtectSystem=full keeps /usr, /boot and /etc read-only -- the unit file cannot be rewritten by
        // the thing it starts -- while /var and /run, which is everything this owns, stay writable.
        text.AppendLine("ProtectSystem=full");
        text.AppendLine("ProtectKernelTunables=true");
        text.AppendLine("ProtectControlGroups=true");
        text.AppendLine("RestrictSUIDSGID=true");

        // No NoNewPrivileges=true, and this was measured, not reasoned: with it set, the engine's
        // setresuid(deskpair) fails with EPERM and the daemon restarts it every five seconds for ever.
        // The plan had it as safe -- "no_new_privs only stops privilege being gained through execve, and
        // giving it up is unaffected" -- and the bisect on the verification machine said otherwise: every
        // other directive here removed one at a time changed nothing, and removing this one alone made
        // the engine start. Whatever the kernel's reason, a daemon whose engine cannot give up root is
        // worse than one without this flag, so it is left out until somebody understands it.

        // on-failure, never always. The engine exits 0 on purpose when the secrets are not its to read;
        // Restart=always turns that explained, permanent failure into a restart every five seconds with
        // the reason in a log nobody has been given a reason to open. Windows produced one loop of exactly
        // this shape, and MacLaunchdJobs writes the same rule as KeepAlive={SuccessfulExit:false}.
        text.AppendLine("Restart=on-failure");
        text.AppendLine("RestartSec=5");

        // The supervisor's own finally stops the child; this bounds it, matching the ten seconds
        // HostService.OnStop allows on Windows.
        text.AppendLine("TimeoutStopSec=10");

        // The journal, not a file. The engine inherits stdout and stderr from the daemon, so one
        // `journalctl -u sunllo-deskpair -f` shows both with one clock. /run is tmpfs and gone at boot,
        // and a file there would be the one copy of why the machine could not be reached after a reboot.
        text.AppendLine("SyslogIdentifier=deskpair");
        text.AppendLine();

        text.AppendLine("[Install]");

        // multi-user, not graphical: this has to run on a machine whose display manager is down.
        text.AppendLine("WantedBy=multi-user.target");
        return text.ToString();
    }

    /// <summary>The unit's name, so the installer and the daemon agree without either spelling it.</summary>
    public static string Name => UnattendedInstall.SystemdUnit;

    /// <summary>systemd's quoting for ExecStart: double quotes around a path with spaces, backslashes escaped.</summary>
    private static string Quote(string path) =>
        path.Any(c => c == ' ' || c == '"' || c == '\\')
            ? "\"" + path.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : path;
}
