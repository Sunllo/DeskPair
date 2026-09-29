using DeskPair.Platform.Abstractions.Security;
using DeskPair.Platform.Linux.Native;
using DeskPair.Platform.Linux.Wayland;
using DeskPair.Platform.Linux.Wayland.Agent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace DeskPair.Platform.Linux.Tests;

/// <summary>
/// The engine's end of a session agent against the agent itself, over a real socketpair: only the portal is a fake,
/// standing where the user's session bus would be. Linux only: descriptors cross by SCM_RIGHTS.
/// </summary>
public sealed class SessionAgentTests
{
    private static readonly PortalStream Left = new(62, "0", 0, 0, 800, 600, true, 1, null);
    private static readonly PortalStream Right = new(58, "1", 800, 0, 800, 600, true, 1, null);

    [Fact]
    public async Task A_session_opens_through_the_agent_and_the_permission_is_kept_by_the_engine()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        await using var pair = await Pair.StartAsync();
        var store = new MemoryStore();
        var tokens = new PortalTokens(store, 1000, NullLogger.Instance);
        await tokens.SaveAsync("remembered", 3);

        await using IPortalSession session = await pair.Engine.OpenAsync(tokens, NullLogger.Instance, CancellationToken.None);

        pair.Portal.Offered.ShouldBe(["remembered"], "the engine's permission went to the portal");
        session.Streams.ShouldBe([Left, Right]);
        session.Devices.ShouldBe(3u);
        session.RemoteControl.ShouldBeTrue();
        (await tokens.LoadAsync()).ShouldBe("replaced-1", "and the portal's new one came back to the engine's store");
        (await pair.Engine.Hello).Uid.ShouldBe(LibC.geteuid());
    }

    [Fact]
    public async Task A_pipewire_connection_arrives_as_a_descriptor_of_the_same_file()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        await using var pair = await Pair.StartAsync();
        await using IPortalSession session = await pair.OpenAsync();

        using SafeFileHandle remote = await session.OpenPipeWireRemoteAsync();
        using var stream = new FileStream(remote, FileAccess.Read);
        new StreamReader(stream).ReadToEnd().ShouldBe("the portal's pipewire socket");
    }

    [Fact]
    public async Task Input_reaches_the_portal_as_the_calls_it_was_made_as()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        await using var pair = await Pair.StartAsync();
        await using IPortalSession session = await pair.OpenAsync();

        session.PointerMotionAbsolute(58, 410.5, 310);
        session.PointerButton(0x110, true);
        session.PointerButton(0x110, false);
        session.PointerMotion(-3, 4);
        session.PointerAxisDiscrete(0, 2);
        session.KeyboardKeycode(30, true);
        session.KeyboardKeysym(0x20ac, false);

        await WaitUntilAsync(() => pair.Portal.Session?.Calls.Count >= 7);
        pair.Portal.Session!.Calls.ShouldBe([
            "motion 58 410.5 310", "button 272 down", "button 272 up", "move -3 4", "axis 0 2", "keycode 30 down", "keysym 8364 up",
        ]);
    }

    [Fact]
    public async Task The_portal_ending_the_session_ends_it_at_the_engine()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        await using var pair = await Pair.StartAsync();
        await using IPortalSession session = await pair.OpenAsync();

        pair.Portal.Session!.End();

        await session.Closed.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => pair.Portal.Session!.Disposed);
    }

    [Fact]
    public async Task Closing_at_the_engine_closes_the_portal_session()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        await using var pair = await Pair.StartAsync();
        IPortalSession session = await pair.OpenAsync();

        await session.DisposeAsync();

        await WaitUntilAsync(() => pair.Portal.Session!.Disposed);
        (await pair.OpenAsync()).ShouldNotBeNull("and another can be opened after it");
    }

    [Fact]
    public async Task A_refusal_arrives_as_the_portal_said_it()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        await using var pair = await Pair.StartAsync();
        pair.Portal.Refuse = new PortalException(PortalFailure.Refused, "The person at the machine said no.");

        PortalException e = await Should.ThrowAsync<PortalException>(pair.OpenAsync());
        (e.Reason, e.Message).ShouldBe((PortalFailure.Refused, "The person at the machine said no."));
    }

    [Fact]
    public async Task Calling_off_an_open_takes_the_dialog_down()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        await using var pair = await Pair.StartAsync();
        pair.Portal.Ask = true;
        using var giveUp = new CancellationTokenSource();
        Task<IPortalSession> opening = pair.Engine.OpenAsync(pair.Tokens, NullLogger.Instance, giveUp.Token);
        await WaitUntilAsync(() => pair.Portal.Asking);

        // As the portal host calls it off (PortalHost.CloseAsync): from the thread pool, where no synchronization context
        // keeps a continuation from running inline. Cancelled from this test's own thread, the one that took the dialog
        // down only lost a race now and then; from the pool it never ran.
        await giveUp.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(opening);
        await WaitUntilAsync(() => pair.Portal.DialogDown, "the agent called off the portal's request");
    }

    [Fact]
    public async Task The_agent_going_ends_the_session_and_every_open_after_it()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        await using var pair = await Pair.StartAsync();
        IPortalSession session = await pair.OpenAsync();

        await pair.StopAgentAsync();

        await pair.Engine.Gone.WaitAsync(TimeSpan.FromSeconds(10));
        await session.Closed.WaitAsync(TimeSpan.FromSeconds(10));
        PortalException e = await Should.ThrowAsync<PortalException>(pair.OpenAsync());
        e.Reason.ShouldBe(PortalFailure.Unavailable);
    }

    /// <summary>GNOME's arrangement of monitors reaches the engine unasked, and again when it changes -- once for each change.</summary>
    [Fact]
    public async Task The_desktops_layout_reaches_the_engine_and_again_when_it_changes()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var twoMonitors = new DesktopLayout([new LayoutMonitor("Virtual-1", 0, 0, 800, 600), new LayoutMonitor("Virtual-2", 800, 0, 800, 600)]);
        var oneMonitor = new DesktopLayout([new LayoutMonitor("Virtual-1", 0, 0, 1280, 768)]);
        var changes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pair = await Pair.StartAsync(async (report, _, ct) =>
        {
            report(twoMonitors);
            report(new DesktopLayout([.. twoMonitors.Monitors]));
            await changes.Task.WaitAsync(ct);
            report(oneMonitor);
            await Task.Delay(Timeout.Infinite, ct);
        });

        await WaitUntilAsync(() => pair.Engine.Layout is not null, "the first layout arrived");
        pair.Engine.Layout!.Monitors.ShouldBe(twoMonitors.Monitors);

        changes.SetResult();
        await WaitUntilAsync(() => pair.Engine.Layout!.Monitors.Count == 1, "the new layout arrived");
        pair.Engine.Layout!.Monitors.ShouldBe(oneMonitor.Monitors);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what = "the condition")
    {
        for (int i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(20);
        }

        condition().ShouldBeTrue(what);
    }

    /// <summary>An agent on one end of a socketpair and the engine's <see cref="AgentPortal"/> on the other.</summary>
    private sealed class Pair : IAsyncDisposable
    {
        private int _agentEnd;
        private readonly CancellationTokenSource _stop = new();
        private Task _agent = Task.CompletedTask;

        private Pair(int engineEnd, int agentEnd)
        {
            _agentEnd = agentEnd;
            Engine = new AgentPortal(engineEnd, NullLogger.Instance);
        }

        public AgentPortal Engine { get; }

        public FakePortal Portal { get; } = new();

        public PortalTokens Tokens { get; } = new(new MemoryStore(), 1000, NullLogger.Instance);

        public static Task<Pair> StartAsync(LayoutWatcher? layouts = null)
        {
            (int engine, int agent) = UnixSocketMsg.Pair();
            var pair = new Pair(engine, agent);
            var sessionAgent = new SessionAgent(agent, pair.Portal.OpenAsync, NullLogger.Instance, "test", layouts);
            pair._agent = sessionAgent.RunAsync(pair._stop.Token);
            return Task.FromResult(pair);
        }

        public Task<IPortalSession> OpenAsync() => Engine.OpenAsync(Tokens, NullLogger.Instance, CancellationToken.None);

        public async Task StopAgentAsync()
        {
            await _stop.CancelAsync();
            await _agent.WaitAsync(TimeSpan.FromSeconds(10));
            CloseAgentEnd();
        }

        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            await _stop.CancelAsync();
            await _agent.WaitAsync(TimeSpan.FromSeconds(10));
            CloseAgentEnd();
            _stop.Dispose();
        }

        /// <summary>
        /// Once only. A descriptor's number belongs to the process, not to this test: a test that stopped the agent
        /// and was then disposed closed it twice, and by the second time another test running alongside could have
        /// been given the same number -- its socket was closed instead. The likeliest way a CI run's agent hand-off
        /// test found its daemon waiting on a socket nobody would ever close, and the test run never ended.
        /// </summary>
        private void CloseAgentEnd()
        {
            int fd = Interlocked.Exchange(ref _agentEnd, -1);
            if (fd >= 0)
            {
                _ = UnixSocketMsg.close(fd);
            }
        }
    }

    /// <summary>The user's portal as the agent sees it: a remembered permission in, a new one out, a session with two monitors.</summary>
    private sealed class FakePortal
    {
        private int _opened;

        public List<string?> Offered { get; } = [];

        public FakeSession? Session { get; private set; }

        public PortalException? Refuse { get; set; }

        /// <summary>Show a dialog nobody answers, until called off.</summary>
        public bool Ask { get; set; }

        public bool Asking { get; private set; }

        public bool DialogDown { get; private set; }

        public async Task<IPortalSession> OpenAsync(PortalTokens tokens, bool offerRestoreToken, ILogger log, CancellationToken ct)
        {
            Offered.Add(offerRestoreToken ? await tokens.LoadAsync(ct) : null);
            if (Refuse is not null)
            {
                throw Refuse;
            }

            if (Ask)
            {
                Asking = true;
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    DialogDown = true;
                    throw;
                }
            }

            await tokens.SaveAsync($"replaced-{Interlocked.Increment(ref _opened)}", 3, ct);
            Session = new FakeSession([Left, Right]);
            return Session;
        }
    }

    private sealed class FakeSession(IReadOnlyList<PortalStream> streams) : IPortalSession
    {
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Calls { get; } = [];

        public bool Disposed { get; private set; }

        public IReadOnlyList<PortalStream> Streams => streams;

        public uint Devices => 3;

        public bool RemoteControl => true;

        public bool ClipboardEnabled => false;

        public TimeSpan StartTook => TimeSpan.FromMilliseconds(8);

        public Task Closed => _closed.Task;

        public bool Pointer => true;

        public bool Keyboard => true;

        public void End() => _closed.TrySetResult();

        public Task<SafeFileHandle> OpenPipeWireRemoteAsync(CancellationToken ct = default)
        {
            string path = Path.Combine(Path.GetTempPath(), "deskpair-agent-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(path, "the portal's pipewire socket");
            SafeFileHandle handle = File.OpenHandle(path);
            File.Delete(path);
            return Task.FromResult(handle);
        }

        public void PointerMotionAbsolute(uint stream, double x, double y) => Record(FormattableString.Invariant($"motion {stream} {x} {y}"));

        public void PointerMotion(double dx, double dy) => Record(FormattableString.Invariant($"move {dx} {dy}"));

        public void PointerButton(int button, bool pressed) => Record($"button {button} {(pressed ? "down" : "up")}");

        public void PointerAxisDiscrete(uint axis, int steps) => Record($"axis {axis} {steps}");

        public void KeyboardKeycode(int keycode, bool pressed) => Record($"keycode {keycode} {(pressed ? "down" : "up")}");

        public void KeyboardKeysym(int keysym, bool pressed) => Record($"keysym {keysym} {(pressed ? "down" : "up")}");

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }

        private void Record(string call)
        {
            lock (Calls)
            {
                Calls.Add(call);
            }
        }
    }

    private sealed class MemoryStore : ISecretStore
    {
        private readonly Dictionary<string, byte[]> _values = [];

        public ValueTask<byte[]?> GetAsync(string key, CancellationToken ct = default) =>
            ValueTask.FromResult(_values.TryGetValue(key, out byte[]? value) ? value : null);

        public ValueTask SetAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct = default)
        {
            _values[key] = value.ToArray();
            return ValueTask.CompletedTask;
        }

        public ValueTask RemoveAsync(string key, CancellationToken ct = default)
        {
            _values.Remove(key);
            return ValueTask.CompletedTask;
        }
    }
}
