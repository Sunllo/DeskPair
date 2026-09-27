using Microsoft.Extensions.Logging;
using DeskPair.Core.Config;
using DeskPair.Desktop.Engine;
using DeskPair.Core.Ipc;
using DeskPair.Protocol.Ipc;

namespace DeskPair.Desktop.Services;

/// <summary>
/// The UI's link to the local host engine over IPC. Reconnects in the background and republishes
/// the id, one-time password and configuration whenever they change.
/// </summary>
public sealed class HostLink : IAsyncDisposable
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(2);

    private readonly string _role;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<string?> _tokenOverride;
    private IpcClient? _client;
    private Task? _loop;

    public HostLink(string role, ILogger log, string? tokenOverride = null)
        : this(role, log, () => tokenOverride)
    {
    }

    /// <summary>
    /// Asks <paramref name="tokenOverride"/> for the token on every attempt to connect: the engine can move between
    /// this app and the service while the app is open, and each has its own.
    /// </summary>
    public HostLink(string role, ILogger log, Func<string?> tokenOverride)
    {
        _role = role;
        _log = log;
        _tokenOverride = tokenOverride;
    }

    public event Action<bool>? ConnectedChanged;

    public event Action<string>? IdChanged;

    /// <summary>Whether the engine's rendezvous server is answering it; see <see cref="Core.Transport.RendezvousLinkState"/>.</summary>
    public event Action<Core.Transport.RendezvousLinkState>? ServerStateChanged;

    public Core.Transport.RendezvousLinkState ServerState { get; private set; } = Core.Transport.RendezvousLinkState.Connecting;

    public event Action<string>? TempPasswordChanged;

    public event Action<HostConfig>? ConfigChanged;

    /// <summary>Host password state (permanent set, temporary enabled/pinned) pushed whenever it changes.</summary>
    public event Action<PasswordState>? PasswordStateChanged;

    /// <summary>Every push that is not one of the typed events above (connection lifecycle, chat, approvals…).</summary>
    public event Action<IpcMessage>? Pushed;

    public bool IsConnected => _client?.IsConnected == true;

    public string Id { get; private set; } = string.Empty;

    public string TempPassword { get; private set; } = string.Empty;

    public HostConfig? Config { get; private set; }

    public PasswordState? PasswordState { get; private set; }

    /// <summary>True when the last saved settings only take effect after the host service restarts.</summary>
    public bool LastSaveNeedsRestart { get; private set; }

    public void Start() => _loop ??= Task.Run(LoopAsync);

    public async Task<IpcMessage> RequestAsync(IpcMessage request, CancellationToken ct = default)
    {
        IpcClient client = _client ?? throw new InvalidOperationException("Host service is not connected.");
        return await client.RequestAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>A request whose answer may take as long as <paramref name="timeout"/>: one a person has to answer first.</summary>
    public async Task<IpcMessage> RequestAsync(IpcMessage request, TimeSpan timeout, CancellationToken ct = default)
    {
        IpcClient client = _client ?? throw new InvalidOperationException("Host service is not connected.");
        return await client.RequestAsync(request, ct, timeout).ConfigureAwait(false);
    }

    public Task SendAsync(IpcMessage message, CancellationToken ct = default)
    {
        IpcClient client = _client ?? throw new InvalidOperationException("Host service is not connected.");
        return client.SendAsync(message, ct);
    }

    public async Task<bool> SetPermanentPasswordAsync(string password, CancellationToken ct = default)
    {
        IpcMessage reply = await RequestAsync(new IpcMessage { SetPermanentPassword = new SetPermanentPassword { Password = password } }, ct).ConfigureAwait(false);
        return reply.PasswordAck?.Ok == true;
    }

    /// <summary>Generates a new random temporary password on the host.</summary>
    public async Task RotateTemporaryPasswordAsync(CancellationToken ct = default)
    {
        IpcMessage reply = await RequestAsync(new IpcMessage { RotateTemporaryPassword = new RotateTemporaryPassword() }, ct).ConfigureAwait(false);
        ApplyPasswordState(reply);
    }

    public async Task<HostConfig> SaveConfigAsync(HostConfig config, CancellationToken ct = default)
    {
        IpcMessage reply = await RequestAsync(new IpcMessage { SetConfig = new SetConfig { Json = config.ToJson() } }, ct).ConfigureAwait(false);
        Config = HostConfig.FromJson(reply.ConfigSnapshot.Json);
        LastSaveNeedsRestart = reply.ConfigSnapshot.RestartRequired;
        return Config;
    }

    /// <summary>Token precedence: explicit, <c>SUNLLO_IPC_TOKEN</c>, then the <c>ipc.token</c> file the engine writes.</summary>
    public static byte[]? ResolveToken(string? tokenOverride)
    {
        string? hex = tokenOverride ?? Environment.GetEnvironmentVariable("SUNLLO_IPC_TOKEN");
        if (string.IsNullOrEmpty(hex))
        {
            foreach (string dir in TokenDirectories())
            {
                string path = Path.Combine(dir, "ipc.token");
                try
                {
                    if (File.Exists(path))
                    {
                        hex = File.ReadAllText(path).Trim();
                        break;
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        try
        {
            return string.IsNullOrEmpty(hex) ? null : Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Everywhere an engine might have left its token, machine-wide engine first.
    ///
    /// The order is the same decision the socket paths make: when a daemon and this user's own engine are
    /// both running, the daemon is the one that can see the login screen, so its token is the one to read.
    ///
    /// On Linux the daemon's engine runs as the session user and cannot write into /var/lib/deskpair,
    /// which is root's and mode 0700 -- so it is handed a path under the runtime directory instead, one
    /// per user, which is where this looks first.
    /// </summary>
    private static IEnumerable<string> TokenDirectories()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Sunllo", "DeskPair");
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/Library/Application Support/Sunllo/DeskPair";
        }
        else
        {
            yield return $"/run/deskpair/{NativeUser.Geteuid()}";
            yield return "/var/lib/deskpair";
        }

        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sunllo", "DeskPair");
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            byte[]? token = ResolveToken(_tokenOverride());
            if (token is null)
            {
                await Task.Delay(ReconnectDelay, _cts.Token).ConfigureAwait(false);
                continue;
            }

            IpcClient? client = null;
            try
            {
                client = await IpcClient.ConnectAsync(IpcEndpoint.Default, token, _role, _log, TimeSpan.FromSeconds(3), _cts.Token).ConfigureAwait(false);
                var gone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                client.Pushed += OnPushed;
                client.Disconnected += _ => gone.TrySetResult();
                _client = client;
                ConnectedChanged?.Invoke(true);
                await RefreshAsync(client).ConfigureAwait(false);
                await gone.Task.WaitAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                _log.LogDebug(e, "Host link unavailable");
            }
            finally
            {
                if (client is not null)
                {
                    _client = null;
                    ConnectedChanged?.Invoke(false);
                    await client.DisposeAsync().ConfigureAwait(false);
                }
            }

            try
            {
                await Task.Delay(ReconnectDelay, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RefreshAsync(IpcClient client)
    {
        IpcMessage id = await client.RequestAsync(new IpcMessage { GetId = new GetId() }, _cts.Token).ConfigureAwait(false);
        OnPushed(id);
        IpcMessage pw = await client.RequestAsync(new IpcMessage { GetTempPassword = new GetTempPassword() }, _cts.Token).ConfigureAwait(false);
        OnPushed(pw);
        IpcMessage cfg = await client.RequestAsync(new IpcMessage { GetConfig = new GetConfig() }, _cts.Token).ConfigureAwait(false);
        OnPushed(cfg);
        IpcMessage state = await client.RequestAsync(new IpcMessage { GetPasswordState = new GetPasswordState() }, _cts.Token).ConfigureAwait(false);
        OnPushed(state);
        IpcMessage server = await client.RequestAsync(new IpcMessage { GetServerState = new GetServerState() }, _cts.Token).ConfigureAwait(false);
        OnPushed(server);
    }

    private void ApplyPasswordState(IpcMessage message)
    {
        if (message.UnionCase != IpcMessage.UnionOneofCase.PasswordState)
        {
            return;
        }

        PasswordState = message.PasswordState;
        if (PasswordState.TemporaryEnabled && PasswordState.TemporaryPassword.Length > 0 && PasswordState.TemporaryPassword != TempPassword)
        {
            TempPassword = PasswordState.TemporaryPassword;
            TempPasswordChanged?.Invoke(TempPassword);
        }

        PasswordStateChanged?.Invoke(PasswordState);
    }

    /// <summary>
    /// Takes <paramref name="message"/> as though the engine had pushed it, with no engine: for the screenshot tool
    /// and tests, which bring the id, password and configuration a picture or an assertion needs.
    /// </summary>
    internal void Deliver(IpcMessage message) => OnPushed(message);

    private void OnPushed(IpcMessage message)
    {
        switch (message.UnionCase)
        {
            case IpcMessage.UnionOneofCase.IdChanged:
                Id = message.IdChanged.Id;
                IdChanged?.Invoke(Id);
                break;
            case IpcMessage.UnionOneofCase.ServerState:
                ServerState = (Core.Transport.RendezvousLinkState)message.ServerState.State;
                ServerStateChanged?.Invoke(ServerState);
                break;
            case IpcMessage.UnionOneofCase.TempPassword:
                TempPassword = message.TempPassword.Password;
                TempPasswordChanged?.Invoke(TempPassword);
                break;
            case IpcMessage.UnionOneofCase.ConfigSnapshot:
                Config = HostConfig.FromJson(message.ConfigSnapshot.Json);
                ConfigChanged?.Invoke(Config);
                break;
            case IpcMessage.UnionOneofCase.PasswordState:
                ApplyPasswordState(message);
                break;
            default:
                Pushed?.Invoke(message);
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cts.Dispose();
    }
}
