using DeskPair.Desktop.Engine.LinuxService;
using DeskPair.Platform.Linux.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace DeskPair.Desktop.Tests;

/// <summary>
/// The Linux supervisor's one rule, driven a tick at a time: the engine does not follow the session.
///
/// The Windows supervisor had to move its engine between sessions and the move kept landing on somebody
/// typing a password. Here the engine reads the screen through a descriptor and types through another,
/// so a lock, a sign-out or a user switch is nothing to it; the only thing that follows the person at the
/// screen is where their copy of the app finds the IPC token.
/// </summary>
public class SessionEngineSupervisorTests
{
    private sealed class World : IEngineWorld
    {
        public ActiveSession? Session { get; set; }

        public bool CanLaunch { get; set; } = true;

        public List<Engine> Launched { get; } = [];

        public bool CanLaunchAgent { get; set; } = true;

        public bool DesktopStarted { get; set; } = true;

        public List<Agent> Agents { get; } = [];

        public List<uint> TokensPublished { get; } = [];

        public int Releases { get; private set; }

        public ActiveSession? ActiveSession() => Session;

        public IEngineHandle? Launch()
        {
            if (!CanLaunch)
            {
                return null;
            }

            var engine = new Engine(Launched.Count + 1);
            Launched.Add(engine);
            return engine;
        }

        public bool DesktopUp(ActiveSession session) => DesktopStarted;

        public IAgentHandle? LaunchAgent(ActiveSession session)
        {
            if (!CanLaunchAgent)
            {
                return null;
            }

            var agent = new Agent(100 + Agents.Count, session.Id);
            Agents.Add(agent);
            return agent;
        }

        public void PublishToken(uint uid) => TokensPublished.Add(uid);

        public void ReleaseInput() => Releases++;
    }

    private sealed class Engine(int pid) : IEngineHandle
    {
        public bool IsAlive { get; set; } = true;

        public int ExitCode { get; set; }

        public int ProcessId => pid;

        public bool Stopped { get; private set; }

        public void Stop() => Stopped = true;

        public void Dispose()
        {
        }
    }

    private sealed class Agent(int pid, string session) : IAgentHandle
    {
        public bool IsAlive { get; set; } = true;

        public int ExitCode { get; set; }

        public int ProcessId => pid;

        public string Session => session;

        public bool Stopped { get; private set; }

        public void Stop() => Stopped = true;

        public void Dispose()
        {
        }
    }

    private static ActiveSession User(string id, uint uid) => new(id, uid, SessionKind.Wayland, SessionRole.User, null);

    private static ActiveSession X11User(string id, uint uid) => new(id, uid, SessionKind.X11, SessionRole.User, null);

    private static ActiveSession Greeter() => new("c1", 120, SessionKind.Wayland, SessionRole.Greeter, null);

    private static (SessionEngineSupervisor, World, FakeTimeProvider) Create(ActiveSession? session)
    {
        var world = new World { Session = session };
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        return (new SessionEngineSupervisor(world, NullLogger.Instance) { Time = time }, world, time);
    }

    [Fact]
    public void The_first_tick_starts_the_engine()
    {
        (SessionEngineSupervisor supervisor, World world, _) = Create(Greeter());

        supervisor.Tick();

        world.Launched.Count.ShouldBe(1);
        supervisor.EnginePid.ShouldBe(1);
    }

    /// <summary>The whole point: a session change, of any kind, does not touch the engine.</summary>
    [Fact]
    public void Somebody_signing_in_at_the_greeter_does_not_restart_the_engine()
    {
        (SessionEngineSupervisor supervisor, World world, _) = Create(Greeter());
        supervisor.Tick();

        world.Session = User("2", 1000);
        supervisor.Tick();
        world.Session = User("5", 1001); // fast user switch
        supervisor.Tick();
        world.Session = Greeter();        // signed out
        supervisor.Tick();

        world.Launched.Count.ShouldBe(1);
        world.Launched[0].Stopped.ShouldBeFalse();
        supervisor.EnginePid.ShouldBe(1);
    }

    /// <summary>The token goes to whoever arrives at the screen, and never to the greeter's account.</summary>
    [Fact]
    public void The_token_is_published_for_each_person_who_arrives_and_not_for_gdm()
    {
        (SessionEngineSupervisor supervisor, World world, _) = Create(Greeter());
        supervisor.Tick();
        world.Session = User("2", 1000);
        supervisor.Tick();
        supervisor.Tick(); // same person, no second copy
        world.Session = User("5", 1001);
        supervisor.Tick();

        world.TokensPublished.ShouldBe([1000u, 1001u]);
    }

    [Fact]
    public void A_dead_engine_is_replaced_after_a_pause_and_its_keys_are_released()
    {
        (SessionEngineSupervisor supervisor, World world, FakeTimeProvider time) = Create(Greeter());
        supervisor.Tick();
        world.Launched[0].IsAlive = false;
        world.Launched[0].ExitCode = 1;

        supervisor.Tick();

        world.Releases.ShouldBe(1, "whatever the engine left pressed is lifted the moment it is gone");
        world.Launched.Count.ShouldBe(1, "not restarted in the same tick");
        supervisor.EnginePid.ShouldBeNull();

        time.Advance(SessionEngineSupervisor.AfterFailure - TimeSpan.FromMilliseconds(1));
        supervisor.Tick();
        world.Launched.Count.ShouldBe(1, "still inside the pause");

        time.Advance(TimeSpan.FromMilliseconds(1));
        supervisor.Tick();
        world.Launched.Count.ShouldBe(2);
        supervisor.EnginePid.ShouldBe(2);
    }

    [Fact]
    public void A_launch_that_fails_is_retried_after_the_same_pause()
    {
        (SessionEngineSupervisor supervisor, World world, FakeTimeProvider time) = Create(Greeter());
        world.CanLaunch = false;

        supervisor.Tick();
        supervisor.Tick();
        world.CanLaunch = true;
        supervisor.Tick();
        world.Launched.ShouldBeEmpty("no retry before the pause");

        time.Advance(SessionEngineSupervisor.AfterFailure);
        supervisor.Tick();
        world.Launched.Count.ShouldBe(1);
    }

    [Fact]
    public void Disposing_stops_the_engine_and_releases_its_keys()
    {
        (SessionEngineSupervisor supervisor, World world, _) = Create(Greeter());
        supervisor.Tick();

        supervisor.Dispose();

        world.Launched[0].Stopped.ShouldBeTrue();
        world.Releases.ShouldBe(1);
    }

    /// <summary>
    /// A signed-in Wayland desktop gets a session agent, which the engine shares it through while it is unlocked; the
    /// engine comes first, and each tick does at most one thing to the agent.
    /// </summary>
    [Fact]
    public void A_signed_in_Wayland_desktop_gets_an_agent_once_the_engine_runs()
    {
        (SessionEngineSupervisor supervisor, World world, _) = Create(User("2", 1000));

        supervisor.Tick();
        world.Agents.ShouldBeEmpty("the engine starts first");

        supervisor.Tick();
        supervisor.Tick();
        world.Agents.Select(a => a.Session).ShouldBe(["2"]);
        supervisor.AgentPid.ShouldBe(100);
    }

    [Fact]
    public void No_agent_for_the_greeter_an_X11_desktop_or_nobody()
    {
        foreach (ActiveSession? session in new[] { Greeter(), X11User("3", 1000), null })
        {
            (SessionEngineSupervisor supervisor, World world, _) = Create(session);
            supervisor.Tick();
            supervisor.Tick();
            world.Agents.ShouldBeEmpty(session?.ToString() ?? "no session");
        }
    }

    /// <summary>The agent follows the session, where the engine does not.</summary>
    [Fact]
    public void A_user_switch_replaces_the_agent_and_signing_out_stops_it()
    {
        (SessionEngineSupervisor supervisor, World world, _) = Create(User("2", 1000));
        supervisor.Tick();
        supervisor.Tick();

        world.Session = User("5", 1001);
        supervisor.Tick();
        world.Agents[0].Stopped.ShouldBeTrue("session 2 is not on the screen now");
        supervisor.Tick();
        world.Agents.Select(a => a.Session).ShouldBe(["2", "5"]);

        world.Session = Greeter();
        supervisor.Tick();
        world.Agents[1].Stopped.ShouldBeTrue();
        supervisor.AgentPid.ShouldBeNull();
        world.Launched.Count.ShouldBe(1, "and the engine is the one it was");
    }

    /// <summary>
    /// A session is on the screen a moment before its desktop has started; an agent started then had none of its
    /// environment, and KDE, which the engine knows by name, was not known.
    /// </summary>
    [Fact]
    public void The_agent_waits_for_the_desktop_and_starts_within_a_second_of_it()
    {
        (SessionEngineSupervisor supervisor, World world, FakeTimeProvider time) = Create(User("2", 1000));
        world.DesktopStarted = false;
        supervisor.Tick();
        supervisor.Tick();
        world.Agents.ShouldBeEmpty("no desktop yet");

        world.DesktopStarted = true;
        supervisor.Tick();
        world.Agents.ShouldBeEmpty("looked at again after a second, not every tick");

        time.Advance(SessionEngineSupervisor.DesktopWait);
        supervisor.Tick();
        world.Agents.Select(a => a.Session).ShouldBe(["2"]);
    }

    [Fact]
    public void A_dead_agent_is_replaced_after_the_pause()
    {
        (SessionEngineSupervisor supervisor, World world, FakeTimeProvider time) = Create(User("2", 1000));
        supervisor.Tick();
        supervisor.Tick();
        world.Agents[0].IsAlive = false;

        supervisor.Tick();
        supervisor.Tick();
        world.Agents.Count.ShouldBe(1, "not straight away");

        time.Advance(SessionEngineSupervisor.AfterFailure);
        supervisor.Tick();
        world.Agents.Count.ShouldBe(2);
    }

    [Fact]
    public void An_agent_that_cannot_start_is_retried_after_the_pause()
    {
        (SessionEngineSupervisor supervisor, World world, FakeTimeProvider time) = Create(User("2", 1000));
        world.CanLaunchAgent = false;
        supervisor.Tick();
        supervisor.Tick();
        world.CanLaunchAgent = true;
        supervisor.Tick();
        world.Agents.ShouldBeEmpty();

        time.Advance(SessionEngineSupervisor.AfterFailure);
        supervisor.Tick();
        world.Agents.Count.ShouldBe(1);
    }

    /// <summary>The agent's socket belongs to the engine that took it, so a new engine needs a new agent.</summary>
    [Fact]
    public void An_engine_that_dies_takes_its_agent_with_it()
    {
        (SessionEngineSupervisor supervisor, World world, FakeTimeProvider time) = Create(User("2", 1000));
        supervisor.Tick();
        supervisor.Tick();
        world.Launched[0].IsAlive = false;

        supervisor.Tick();
        world.Agents[0].Stopped.ShouldBeTrue();

        time.Advance(SessionEngineSupervisor.AfterFailure);
        supervisor.Tick();
        supervisor.Tick();
        world.Launched.Count.ShouldBe(2);
        world.Agents.Count.ShouldBe(2);
        world.Agents[1].Stopped.ShouldBeFalse();
    }

    [Fact]
    public void Disposing_stops_the_agent_with_the_engine()
    {
        (SessionEngineSupervisor supervisor, World world, _) = Create(User("2", 1000));
        supervisor.Tick();
        supervisor.Tick();

        supervisor.Dispose();

        world.Agents[0].Stopped.ShouldBeTrue();
        world.Launched[0].Stopped.ShouldBeTrue();
    }

    [Fact]
    public void No_session_at_all_still_runs_the_engine()
    {
        (SessionEngineSupervisor supervisor, World world, _) = Create(session: null);

        supervisor.Tick();

        world.Launched.Count.ShouldBe(1, "a text console or a machine still booting is not a reason to have nothing listening");
        world.TokensPublished.ShouldBeEmpty();
    }
}
