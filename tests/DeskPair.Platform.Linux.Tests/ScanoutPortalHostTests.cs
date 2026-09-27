using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Abstractions.Cursor;
using DeskPair.Platform.Abstractions.Input;
using DeskPair.Platform.Abstractions.Security;
using DeskPair.Platform.Linux.Wayland;
using DeskPair.Platform.Linux.Wayland.Agent;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeskPair.Platform.Linux.Tests;

/// <summary>
/// The unattended engine's screen: the hardware always, a signed-in user's desktop through the portal while it can
/// be had without asking anybody, and the hardware again the moment the portal closes -- which, on GNOME, is every
/// lock. The scanout and the agent's desktop are fakes; what is tested is which of them serves, and when.
/// </summary>
public sealed class ScanoutPortalHostTests
{
    private static readonly VirtualScreenRect Screen = new(0, 0, 1600, 600);

    [Fact]
    public async Task Without_an_agent_the_hardware_serves_and_opening_is_nothing()
    {
        await using var bed = Bed.Start();

        (await bed.Host.OpenAsync(_ => { }, CancellationToken.None)).ShouldBeNull("never a failure: the viewers see the hardware");
        bed.Host.IsOpen.ShouldBeFalse();
        bed.Host.GetDisplays().Select(d => d.Name).ShouldBe(["scanout-43"]);
        bed.Host.InjectMouse(new MouseInput(MouseAction.Move, MouseButtons.None, 10, 10, 0), Screen);
        bed.Scanout.Calls.ShouldBe(["mouse 10,10"]);
    }

    [Fact]
    public async Task With_the_permission_remembered_the_portal_opens_and_serves()
    {
        await using var bed = Bed.Start();
        await bed.RememberAsync(1000);
        FakeDesktop desktop = await bed.AgentArrivesAsync(1000, "ubuntu:GNOME");

        (await bed.Host.OpenAsync(_ => { }, CancellationToken.None)).ShouldBeNull();

        bed.Host.IsOpen.ShouldBeTrue();
        bed.Host.GetDisplays().Select(d => d.Name).ShouldBe(["wayland@0,0", "wayland@800,0"]);
        bed.Host.InjectMouse(new MouseInput(MouseAction.Move, MouseButtons.None, 1210, 310, 0), Screen);
        desktop.Calls.ShouldBe(["mouse 1210,310"]);
        bed.Scanout.Calls.ShouldBeEmpty();
        bed.Host.Create(bed.Host.GetDisplays()[1], false).Display.Name.ShouldBe("wayland@800,0");
        bed.Host.GetCurrentCursorId().ShouldBe(FakeDesktop.PointerId);
    }

    [Fact]
    public async Task An_agent_arriving_says_the_displays_can_be_opened_again()
    {
        await using var bed = Bed.Start();
        int reopenable = 0;
        bed.Host.Reopenable += () => reopenable++;

        await bed.AgentArrivesAsync(1000, "ubuntu:GNOME");

        reopenable.ShouldBe(1, "so viewers already watching the hardware move to the portal");
    }

    /// <summary>An unattended machine must not put a dialog in front of whoever sits at it because somebody connected.</summary>
    [Fact]
    public async Task Without_the_permission_GNOME_is_not_asked_but_KDE_which_never_asks_is_opened()
    {
        await using var bed = Bed.Start();
        FakeDesktop gnome = await bed.AgentArrivesAsync(1000, "ubuntu:GNOME");
        await bed.Host.OpenAsync(_ => { }, CancellationToken.None);
        gnome.OpenCalls.ShouldBe(0);
        bed.Host.IsOpen.ShouldBeFalse();

        FakeDesktop kde = await bed.AgentArrivesAsync(1001, "KDE");
        await bed.Host.OpenAsync(_ => { }, CancellationToken.None);
        kde.OpenCalls.ShouldBe(1);
        bed.Host.IsOpen.ShouldBeTrue();
    }

    /// <summary>GNOME ends every share when it locks: the hardware shows the lock screen, and nobody is told sharing stopped.</summary>
    [Fact]
    public async Task The_portal_closing_by_itself_hands_the_screen_back_to_the_hardware()
    {
        await using var bed = Bed.Start();
        await bed.RememberAsync(1000);
        FakeDesktop desktop = await bed.AgentArrivesAsync(1000, "ubuntu:GNOME");
        await bed.Host.OpenAsync(_ => { }, CancellationToken.None);
        int changes = 0;
        int reopenable = 0;
        bool closed = false;
        bed.Host.DisplaysChanged += (_, _) => changes++;
        bed.Host.Reopenable += () => reopenable++;
        bed.Host.Closed += _ => closed = true;

        desktop.PortalEnds("The remote computer's screen is locked.");

        changes.ShouldBe(1);
        closed.ShouldBeFalse();
        bed.Host.GetDisplays().Select(d => d.Name).ShouldBe(["scanout-43"]);
        bed.Host.InjectKey(new KeyInput(KeyInputMode.Translate, true, 0, ControlKey.None, "p"));
        bed.Scanout.Calls.ShouldBe(["key p"], "the password goes into the lock screen through the hardware");

        desktop.Unlocked();
        reopenable.ShouldBe(1);
        await bed.Host.OpenAsync(_ => { }, CancellationToken.None);
        bed.Host.IsOpen.ShouldBeTrue("and the portal opens again");
    }

    [Fact]
    public async Task A_portal_that_starts_asking_is_called_off_and_not_tried_again_until_a_new_agent()
    {
        await using var bed = Bed.Start();
        await bed.RememberAsync(1000);
        FakeDesktop desktop = await bed.AgentArrivesAsync(1000, "ubuntu:GNOME");
        desktop.WouldAsk = true;
        var told = new List<string>();

        (await bed.Host.OpenAsync(told.Add, CancellationToken.None)).ShouldBeNull();

        desktop.ClosedByHost.ShouldBeTrue("the dialog is taken down");
        told.ShouldBeEmpty("viewers are not told the machine is asking: they are looking at its screen already");
        (await new PortalTokens(bed.Store, 1000, NullLogger.Instance).LoadAsync()).ShouldBeNull("the permission that no longer holds is forgotten");
        await bed.RememberAsync(1000);
        await bed.Host.OpenAsync(_ => { }, CancellationToken.None);
        desktop.OpenCalls.ShouldBe(1, "not tried again for this agent");

        FakeDesktop next = await bed.AgentArrivesAsync(1000, "ubuntu:GNOME");
        await bed.Host.OpenAsync(_ => { }, CancellationToken.None);
        next.OpenCalls.ShouldBe(1);
    }

    [Fact]
    public async Task A_new_agent_replaces_the_last_and_a_gone_one_is_forgotten()
    {
        await using var bed = Bed.Start();
        await bed.RememberAsync(1000);
        FakeDesktop first = await bed.AgentArrivesAsync(1000, "ubuntu:GNOME");
        await bed.Host.OpenAsync(_ => { }, CancellationToken.None);
        int changes = 0;
        bed.Host.DisplaysChanged += (_, _) => changes++;

        FakeDesktop second = await bed.AgentArrivesAsync(1001, "ubuntu:GNOME");
        first.Disposed.ShouldBeTrue();
        changes.ShouldBe(1, "the open portal went with it");
        bed.Host.IsOpen.ShouldBeFalse();

        second.Leave();
        await WaitUntilAsync(() => second.Disposed);
        await bed.Host.OpenAsync(_ => { }, CancellationToken.None);
        second.OpenCalls.ShouldBe(0, "a desktop whose agent went is not something to open");
    }

    [Fact]
    public async Task Locking_is_always_the_daemons_and_releasing_is_everywhere()
    {
        await using var bed = Bed.Start();
        await bed.RememberAsync(1000);
        FakeDesktop desktop = await bed.AgentArrivesAsync(1000, "ubuntu:GNOME");
        await bed.Host.OpenAsync(_ => { }, CancellationToken.None);

        bed.Host.LockWorkstation();
        bed.Host.ReleaseAll();

        bed.Scanout.Calls.ShouldBe(["lock", "release"]);
        desktop.Calls.ShouldBe(["release"]);
    }

    /// <summary>The settings page asks the engine, which asks the user through their agent; the portal then opens without asking.</summary>
    [Fact]
    public async Task Asked_from_the_settings_page_the_user_allows_it_and_the_portal_opens()
    {
        await using var bed = Bed.Start();
        FakeDesktop desktop = await bed.AgentArrivesAsync(1000, "ubuntu:GNOME");
        (await bed.Host.SharingStateAsync(1000, CancellationToken.None)).ShouldBe(DesktopSharingState.NotYet);
        int reopenable = 0;
        bed.Host.Reopenable += () => reopenable++;

        (DesktopSharingOutcome outcome, _) = await bed.Host.AskSharingAsync(1000, CancellationToken.None);

        outcome.ShouldBe(DesktopSharingOutcome.Allowed);
        desktop.Asked.ShouldBe(1);
        (await bed.Host.SharingStateAsync(1000, CancellationToken.None)).ShouldBe(DesktopSharingState.Allowed);
        reopenable.ShouldBe(1, "anybody watching the hardware meanwhile moves to the portal");
        await bed.Host.OpenAsync(_ => { }, CancellationToken.None);
        bed.Host.IsOpen.ShouldBeTrue();
    }

    [Fact]
    public async Task Only_the_user_whose_desktop_it_is_is_answered_and_not_while_a_viewer_is_on_it()
    {
        await using var bed = Bed.Start();
        (await bed.Host.SharingStateAsync(1000, CancellationToken.None)).ShouldBe(DesktopSharingState.Unavailable, "no agent yet");
        await bed.RememberAsync(1000);
        FakeDesktop desktop = await bed.AgentArrivesAsync(1000, "ubuntu:GNOME");

        (await bed.Host.SharingStateAsync(1001, CancellationToken.None)).ShouldBe(DesktopSharingState.Unavailable, "another account's desktop is not theirs to allow");
        (await bed.Host.AskSharingAsync(1001, CancellationToken.None)).Outcome.ShouldBe(DesktopSharingOutcome.Failed);

        await bed.Host.OpenAsync(_ => { }, CancellationToken.None);
        (await bed.Host.AskSharingAsync(1000, CancellationToken.None)).Outcome.ShouldBe(DesktopSharingOutcome.Failed);
        desktop.Asked.ShouldBe(0, "the agent holds one session at a time, and a viewer has it");
    }

    [Fact]
    public async Task KDE_which_never_asks_counts_as_allowed()
    {
        await using var bed = Bed.Start();
        await bed.AgentArrivesAsync(1000, "KDE");

        (await bed.Host.SharingStateAsync(1000, CancellationToken.None)).ShouldBe(DesktopSharingState.Allowed);
    }

    [Fact]
    public async Task A_display_of_the_other_kind_is_captured_by_its_own_side()
    {
        await using var bed = Bed.Start();
        await bed.RememberAsync(1000);
        await bed.AgentArrivesAsync(1000, "ubuntu:GNOME");
        DisplayDescriptor scanout = bed.Host.GetDisplays()[0];
        await bed.Host.OpenAsync(_ => { }, CancellationToken.None);

        bed.Host.Create(scanout, false).Display.Name.ShouldBe("scanout-43", "a capturer started just before the switch still gets the hardware");

        await bed.Host.CloseAsync();
        Should.Throw<InvalidOperationException>(() => bed.Host.Create(FakeDesktop.Displays[0], false));
    }

    /// <summary>
    /// GNOME locked, so the hardware serves: its picture is the second monitor, and the virtual pointer spans both. The
    /// agent said where GNOME has each monitor, the daemon which connector the picture is, and the click lands on the
    /// second monitor where it was aimed.
    /// </summary>
    [Fact]
    public async Task On_the_hardware_a_click_goes_where_GNOME_has_the_pictures_monitor()
    {
        await using var bed = Bed.Start();
        bed.ScanoutConnector = (15, 2);
        FakeDesktop desktop = await bed.AgentArrivesAsync(1000, "ubuntu:GNOME");
        desktop.Layout = new DesktopLayout([new LayoutMonitor("Virtual-1", 0, 0, 800, 600), new LayoutMonitor("Virtual-2", 800, 0, 800, 600)]);
        var picture = new VirtualScreenRect(0, 0, 800, 600);

        bed.Host.InjectMouse(new MouseInput(MouseAction.Down, MouseButtons.Left, 10, 20, 0), picture);
        bed.Host.InjectMouse(new MouseInput(MouseAction.Wheel, MouseButtons.None, 0, 0, 1), picture);
        bed.Host.InjectMouse(new MouseInput(MouseAction.MoveRelative, MouseButtons.None, 3, 4, 0), picture);

        bed.Scanout.Calls.ShouldBe(["mouse 810,20", "mouse 0,0", "mouse 3,4"], "a wheel and a nudge are not places");
        bed.Scanout.Screens[0].ShouldBe(new VirtualScreenRect(0, 0, 1600, 600), "the desktop, which the pointer spans");
        bed.Scanout.Screens[1].ShouldBe(picture);
    }

    [Fact]
    public async Task Without_a_layout_that_has_the_pictures_monitor_the_picture_is_the_whole_desktop()
    {
        await using var bed = Bed.Start();
        bed.ScanoutConnector = (15, 3);
        FakeDesktop desktop = await bed.AgentArrivesAsync(1000, "KDE");
        var picture = new VirtualScreenRect(0, 0, 800, 600);

        bed.Host.InjectMouse(new MouseInput(MouseAction.Move, MouseButtons.None, 10, 20, 0), picture);
        desktop.Layout = new DesktopLayout([new LayoutMonitor("Virtual-1", 0, 0, 800, 600), new LayoutMonitor("Virtual-2", 800, 0, 800, 600)]);
        bed.Host.InjectMouse(new MouseInput(MouseAction.Move, MouseButtons.None, 11, 21, 0), picture);

        bed.Scanout.Calls.ShouldBe(["mouse 10,20", "mouse 11,21"], "no layout, then one without Virtual-3");
        bed.Scanout.Screens.ShouldAllBe(s => s == picture);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        condition().ShouldBeTrue();
    }

    private sealed class Bed : IAsyncDisposable
    {
        private readonly Queue<(int, uint, string)> _offers = new();
        private int _adopted;

        private Bed()
        {
            Host = new ScanoutPortalHost(
                Scanout, Scanout, Scanout, Scanout, Store, NullLogger.Instance, TakeAgent, Connect, TimeSpan.FromMilliseconds(5), () => ScanoutConnector);
        }

        /// <summary>The connector the hardware's picture is of, as the daemon says it: Virtual-1 unless a test says otherwise.</summary>
        public (uint Type, uint TypeId)? ScanoutConnector { get; set; } = (15, 1);

        public FakeScanout Scanout { get; } = new();

        public MemoryStore Store { get; } = new();

        public ScanoutPortalHost Host { get; }

        public List<FakeDesktop> Desktops { get; } = [];

        private string _nextDesktop = string.Empty;

        public static Bed Start() => new();

        public Task RememberAsync(uint uid) => new PortalTokens(Store, uid, NullLogger.Instance).SaveAsync("remembered", 3).AsTask();

        /// <summary>The daemon offers an agent for <paramref name="uid"/>; returns its desktop once the host has taken it on.</summary>
        public async Task<FakeDesktop> AgentArrivesAsync(uint uid, string desktop)
        {
            int before = Volatile.Read(ref _adopted);
            lock (_offers)
            {
                _nextDesktop = desktop;
                _offers.Enqueue((100 + before, uid, $"s{before}"));
            }

            await WaitUntilAsync(() => Volatile.Read(ref _adopted) > before);
            await Task.Delay(20); // past the adoption, to the Reopenable it raises
            return Desktops[^1];
        }

        private (int Socket, uint Uid, string Session)? TakeAgent()
        {
            lock (_offers)
            {
                return _offers.TryDequeue(out (int, uint, string) offer) ? offer : null;
            }
        }

        private ISharedDesktop Connect(int socket, uint uid)
        {
            var desktop = new FakeDesktop(uid, _nextDesktop) { OnAllowed = () => RememberAsync(uid) };
            Desktops.Add(desktop);
            Interlocked.Increment(ref _adopted);
            return desktop;
        }

        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }

    /// <summary>The daemon's scanout, virtual devices and arrow, recording what reaches them.</summary>
    private sealed class FakeScanout : IDisplayEnumerator, IScreenCapturerFactory, IInputInjector, ICursorProvider
    {
        public List<string> Calls { get; } = [];

        /// <summary>The virtual screen each mouse event came with.</summary>
        public List<VirtualScreenRect> Screens { get; } = [];

        public event EventHandler? DisplaysChanged
        {
            add { }
            remove { }
        }

        public IReadOnlyList<DisplayDescriptor> GetDisplays() =>
            [new DisplayDescriptor(0, "scanout-43", 0, 0, 800, 600, 1.0, FrameRotation.None, true, 43)];

        public IScreenCapturer Create(DisplayDescriptor display, bool preferGpu) => new NamedCapturer(display);

        public void EnsureInputDesktop()
        {
        }

        public void InjectMouse(in MouseInput input, in VirtualScreenRect virtualScreen)
        {
            Calls.Add($"mouse {input.X},{input.Y}");
            Screens.Add(virtualScreen);
        }

        public void InjectKey(in KeyInput input) => Calls.Add($"key {input.Text}");

        public LockKeyStates GetLockKeyStates() => default;

        public void SetLockKeyStates(LockKeyStates states)
        {
        }

        public void ReleaseAll() => Calls.Add("release");

        public void SendCtrlAltDel() => Calls.Add("cad");

        public void LockWorkstation() => Calls.Add("lock");

        public ulong GetCurrentCursorId() => 1;

        public CursorImage? GetCursorImage(ulong id) => null;

        public (int X, int Y)? GetCursorPosition() => null;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A user's desktop as a session agent serves it: two monitors, a pointer, and a portal that opens, asks, or ends.</summary>
    private sealed class FakeDesktop(uint uid, string desktop) : ISharedDesktop
    {
        public const ulong PointerId = 0xfeed;

        public static readonly DisplayDescriptor[] Displays =
        [
            new(0, "wayland@0,0", 0, 0, 800, 600, 1.0, FrameRotation.None, true, 62),
            new(1, "wayland@800,0", 800, 0, 800, 600, 1.0, FrameRotation.None, false, 58),
        ];

        private readonly TaskCompletionSource _gone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<bool>? _dialog;

        public uint Uid => uid;

        public string Desktop => desktop;

        public Task Gone => _gone.Task;

        public DesktopLayout? Layout { get; set; }

        public List<string> Calls { get; } = [];

        public int OpenCalls { get; private set; }

        public int Asked { get; private set; }

        /// <summary>Remembered by the fake portal when the user says yes, as the real one saves a token.</summary>
        public Func<Task>? OnAllowed { get; set; }

        public bool WouldAsk { get; set; }

        public bool ClosedByHost { get; private set; }

        public bool Disposed { get; private set; }

        public bool IsOpen { get; private set; }

        public event EventHandler? DisplaysChanged;

        public event Action<string>? Closed;

        public event Action? Reopenable;

        public async Task<string?> OpenAsync(Action<string> progress, CancellationToken ct)
        {
            OpenCalls++;
            if (WouldAsk)
            {
                _dialog = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                progress(PortalHost.Asking);
                await _dialog.Task;
                return "Screen sharing was not started: nobody is connected any more.";
            }

            IsOpen = true;
            return null;
        }

        public async Task<(DesktopSharingOutcome Outcome, string? Detail)> AskAsync(CancellationToken ct)
        {
            Asked++;
            if (OnAllowed is not null)
            {
                await OnAllowed();
            }

            return (DesktopSharingOutcome.Allowed, null);
        }

        public ValueTask CloseAsync()
        {
            ClosedByHost = true;
            IsOpen = false;
            _dialog?.TrySetResult(false);
            return ValueTask.CompletedTask;
        }

        public void PortalEnds(string reason)
        {
            IsOpen = false;
            DisplaysChanged?.Invoke(this, EventArgs.Empty);
            Closed?.Invoke(reason);
        }

        public void Unlocked() => Reopenable?.Invoke();

        public void Leave()
        {
            IsOpen = false;
            _gone.TrySetResult();
        }

        public IReadOnlyList<DisplayDescriptor> GetDisplays() => IsOpen ? Displays : [];

        public IScreenCapturer Create(DisplayDescriptor display, bool preferGpu) => new NamedCapturer(display);

        public void EnsureInputDesktop()
        {
        }

        public void InjectMouse(in MouseInput input, in VirtualScreenRect virtualScreen) => Calls.Add($"mouse {input.X},{input.Y}");

        public void InjectKey(in KeyInput input) => Calls.Add($"key {input.Text}");

        public LockKeyStates GetLockKeyStates() => default;

        public void SetLockKeyStates(LockKeyStates states)
        {
        }

        public void ReleaseAll() => Calls.Add("release");

        public void SendCtrlAltDel() => Calls.Add("cad");

        public void LockWorkstation() => Calls.Add("lock");

        public ulong GetCurrentCursorId() => PointerId;

        public CursorImage? GetCursorImage(ulong id) => null;

        public (int X, int Y)? GetCursorPosition() => (5, 5);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            IsOpen = false;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NamedCapturer(DisplayDescriptor display) : IScreenCapturer
    {
        public DisplayDescriptor Display => display;

        public bool SupportsGpuTexture => false;

        public GpuApi GpuApi => GpuApi.None;

        public ValueTask<CaptureResult> AcquireFrameAsync(TimeSpan timeout, CancellationToken ct) => ValueTask.FromResult(CaptureResult.TimedOut);

        public void ForceFallbackPath()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MemoryStore : ISecretStore
    {
        private readonly Dictionary<string, byte[]> _values = [];

        public ValueTask<byte[]?> GetAsync(string key, CancellationToken ct = default)
        {
            lock (_values)
            {
                return ValueTask.FromResult(_values.TryGetValue(key, out byte[]? value) ? value : null);
            }
        }

        public ValueTask SetAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct = default)
        {
            lock (_values)
            {
                _values[key] = value.ToArray();
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask RemoveAsync(string key, CancellationToken ct = default)
        {
            lock (_values)
            {
                _values.Remove(key);
            }

            return ValueTask.CompletedTask;
        }
    }
}
