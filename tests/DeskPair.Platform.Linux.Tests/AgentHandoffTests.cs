using System.Text;
using DeskPair.Platform.Linux.Capture.Drm;
using DeskPair.Platform.Linux.Native;
using DeskPair.Platform.Linux.Wayland.Agent;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeskPair.Platform.Linux.Tests;

/// <summary>
/// A session agent's socket gets from the daemon to the engine: left in the hand-off, asked for over the capture
/// channel, and arriving as the agent's own peer. The real daemon-side server, with no display hardware, and real
/// socketpairs. Linux only: descriptors cross by SCM_RIGHTS.
/// </summary>
public sealed class AgentHandoffTests
{
    [Fact]
    public void The_engine_takes_the_socket_the_daemon_left_and_talks_to_the_agent_through_it()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var agents = new AgentHandoff();
        (int engineEnd, int daemonEnd) = UnixSocketMsg.Pair();
        var server = new Thread(() => DrmCaptureServer.Run(daemonEnd, reader: null, NullLogger.Instance, CancellationToken.None, terminals: null, agents));
        server.Start();
        var channel = new DrmCaptureChannel(engineEnd);
        try
        {
            channel.TakeAgent().ShouldBeNull("nothing waits yet");

            (int forEngine, int agentEnd) = UnixSocketMsg.Pair();
            agents.Offer(forEngine, 1000, "65");
            (int Socket, uint Uid, string Session)? taken = channel.TakeAgent();
            taken.ShouldNotBeNull();
            (taken.Value.Uid, taken.Value.Session).ShouldBe((1000u, "65"));
            channel.TakeAgent().ShouldBeNull("taken once");

            UnixSocketMsg.Send(agentEnd, "hello from the agent"u8, []);
            byte[] got = new byte[64];
            int n = UnixSocketMsg.Receive(taken.Value.Socket, got, stackalloc int[UnixSocketMsg.MaxFds], out _);
            Encoding.ASCII.GetString(got, 0, n).ShouldBe("hello from the agent");

            channel.Poll().NoHardware.ShouldBeTrue("and the screen poll still answers on the same channel");
            _ = UnixSocketMsg.close(taken.Value.Socket);
            _ = UnixSocketMsg.close(agentEnd);
        }
        finally
        {
            channel.Dispose();
            server.Join(TimeSpan.FromSeconds(10)).ShouldBeTrue("the daemon's side ends with the engine's channel");
            _ = UnixSocketMsg.close(daemonEnd);
        }
    }

    [Fact]
    public void A_newer_agent_replaces_one_nobody_took_and_a_gone_one_is_withdrawn()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var agents = new AgentHandoff();
        (int first, int firstPeer) = UnixSocketMsg.Pair();
        (int second, int secondPeer) = UnixSocketMsg.Pair();
        agents.Offer(first, 1000, "2");
        agents.Offer(second, 1001, "5");

        EndOfFile(firstPeer).ShouldBeTrue("the older socket was closed, not leaked");
        agents.Take()!.Session.ShouldBe("5");
        agents.Take().ShouldBeNull();

        (int third, int thirdPeer) = UnixSocketMsg.Pair();
        agents.Offer(third, 1000, "7");
        agents.Withdraw(third);
        EndOfFile(thirdPeer).ShouldBeTrue("its agent went before the engine asked");
        agents.Take().ShouldBeNull();

        foreach (int fd in new[] { firstPeer, second, secondPeer, thirdPeer })
        {
            _ = UnixSocketMsg.close(fd);
        }
    }

    private static bool EndOfFile(int socket) =>
        UnixSocketMsg.Receive(socket, new byte[16], stackalloc int[UnixSocketMsg.MaxFds], out _) == 0;
}
