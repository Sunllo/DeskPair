using DeskPair.Platform.Abstractions.Terminal;
using DeskPair.Protocol;
using DeskPair.Protocol.Messages;

namespace DeskPair.Core.Session.Host;

/// <summary>Host-side configuration that governs incoming sessions.</summary>
public sealed record HostPolicy
{
    public ApproveMode ApproveMode { get; init; } = ApproveMode.ApprovePassword;

    /// <summary>In click mode, still authorize immediately when a valid password proof is presented.</summary>
    public bool AllowPasswordInClickMode { get; init; } = true;

    public TimeSpan ApprovalTimeout { get; init; } = ProtocolConstants.ApprovalTimeout;

    public bool KeyboardEnabled { get; init; } = true;
    public bool ClipboardEnabled { get; init; } = true;
    public bool AudioEnabled { get; init; } = true;
    public bool FileTransferEnabled { get; init; } = true;
    public bool RestartEnabled { get; init; } = false;

    /// <summary>A shell on this computer. Off unless the owner turned it on; nothing a viewer sends can turn it on.</summary>
    public bool TerminalEnabled { get; init; } = false;

    public TerminalRunAs TerminalRunAs { get; init; } = TerminalRunAs.Highest;

    public int MaxLoginAttemptsPerConnection { get; init; } = 3;

    /// <summary>Delay before answering a failed login, so guessing stays slow even without the tracker.</summary>
    public TimeSpan FailedLoginDelay { get; init; } = TimeSpan.FromSeconds(1);

    public string HostName { get; init; } = Environment.MachineName;

    public string UserName { get; init; } = Environment.UserName;

    public string Platform { get; init; } = PlatformName.Current;

    public bool IsPermissionEnabled(Permission permission) => permission switch
    {
        Permission.PermKeyboard => KeyboardEnabled,
        Permission.PermClipboard => ClipboardEnabled,
        Permission.PermAudio => AudioEnabled,
        Permission.PermFile => FileTransferEnabled,
        Permission.PermRestart => RestartEnabled,
        Permission.PermTerminal => TerminalEnabled,
        _ => false,
    };
}

/// <summary>Effective permissions of one session: host policy ∧ controller options ∧ live toggles.</summary>
public sealed class PermissionSet
{
    private HostPolicy _policy;
    private readonly Dictionary<Permission, bool> _overrides = new();
    private SessionOptions _options = new();

    public PermissionSet(HostPolicy policy)
    {
        _policy = policy;
    }

    public event Action<Permission, bool>? Changed;

    /// <summary>The user changed the host settings mid-session; permissions are recomputed and pushed.</summary>
    public void UpdatePolicy(HostPolicy policy)
    {
        var before = Snapshot();
        _policy = policy;
        RaiseDiff(before);
    }

    public void ApplyOptions(SessionOptions options)
    {
        var before = Snapshot();
        _options = options;
        RaiseDiff(before);
    }

    /// <summary>Live toggle from the connection manager; null clears the override.</summary>
    public void SetOverride(Permission permission, bool? enabled)
    {
        var before = Snapshot();
        if (enabled is null)
        {
            _overrides.Remove(permission);
        }
        else
        {
            _overrides[permission] = enabled.Value;
        }

        RaiseDiff(before);
    }

    public bool Has(Permission permission)
    {
        if (!_policy.IsPermissionEnabled(permission))
        {
            return false;
        }

        if (_overrides.TryGetValue(permission, out bool o) && !o)
        {
            return false;
        }

        return permission switch
        {
            Permission.PermKeyboard => _options.DisableKeyboard != BoolOption.BoYes,
            Permission.PermClipboard => _options.DisableClipboard != BoolOption.BoYes,
            Permission.PermAudio => _options.DisableAudio != BoolOption.BoYes,
            _ => true,
        };
    }

    public IEnumerable<Permission> Granted => Enum.GetValues<Permission>().Where(Has);

    private Dictionary<Permission, bool> Snapshot() => Enum.GetValues<Permission>().ToDictionary(p => p, Has);

    private void RaiseDiff(Dictionary<Permission, bool> before)
    {
        foreach (Permission p in Enum.GetValues<Permission>())
        {
            bool now = Has(p);
            if (before[p] != now)
            {
                Changed?.Invoke(p, now);
            }
        }
    }
}
