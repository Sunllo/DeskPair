using DeskPair.Core.Ipc;

namespace DeskPair.Core.Tests;

/// <summary>
/// Where the engine listens and where a client looks.
///
/// One path was enough while the engine was always the user's own process. A root daemon cannot use it:
/// the per-user socket lives in that user's runtime directory, which on Linux is /run/user/1000 and on
/// macOS is a per-user /var/folders sandbox at mode 0700, and root's own is a different directory that
/// the signed-in user cannot see into. Two processes on the same desk, a metre apart, each listening and
/// connecting correctly, and never finding each other.
///
/// So there are two, and the client tries both. These pin the order, because the order is the decision:
/// when a daemon and a user engine are both listening, the daemon is the one that can see the login
/// screen, and attaching to the other would look like everything working until the machine was locked.
/// </summary>
public class IpcEndpointPathTests
{
    private static readonly IpcEndpoint Endpoint = new("DeskPair");

    [Fact]
    public void A_client_tries_the_machine_wide_socket_first()
    {
        Endpoint.ConnectPaths.Count.ShouldBe(2);
        Endpoint.ConnectPaths[0].ShouldBe(Endpoint.SystemSocketPath);
        Endpoint.ConnectPaths[1].ShouldBe(Endpoint.UserSocketPath);
    }

    [Fact]
    public void The_two_paths_are_never_the_same_place()
    {
        Endpoint.SystemSocketPath.ShouldNotBe(Endpoint.UserSocketPath);
    }

    /// <summary>
    /// sun_path is 104 bytes on macOS and 108 on Linux, and a path over it is not rejected: it is
    /// truncated, so the bind succeeds against a name no client will ever ask for.
    /// </summary>
    [Fact]
    public void Both_paths_fit_in_a_unix_socket_address()
    {
        System.Text.Encoding.UTF8.GetByteCount(Endpoint.SystemSocketPath).ShouldBeLessThan(104);
        System.Text.Encoding.UTF8.GetByteCount(Endpoint.UserSocketPath).ShouldBeLessThan(104);
    }

    /// <summary>The name keeps tests and parallel installs apart, so it has to reach both paths.</summary>
    [Fact]
    public void The_instance_name_is_in_both()
    {
        var mine = new IpcEndpoint("DeskPair-test-abcd1234");

        mine.SystemSocketPath.ShouldContain("DeskPair-test-abcd1234");
        mine.UserSocketPath.ShouldContain("DeskPair-test-abcd1234");
    }

    [Fact]
    public void The_runtime_directory_is_used_when_there_is_one()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // XDG_RUNTIME_DIR is a Unix arrangement, and Windows listens on a pipe anyway.
        }

        string? before = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        try
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", "/run/user/4242");
            new IpcEndpoint("DeskPair").UserSocketPath.ShouldBe("/run/user/4242/deskpair/DeskPair.sock");
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", before);
        }
    }

    [Fact]
    public void The_machine_wide_path_is_where_root_can_put_it_on_each_system()
    {
        if (OperatingSystem.IsMacOS())
        {
            Endpoint.SystemSocketPath.ShouldBe("/Library/Application Support/Sunllo/DeskPair/DeskPair.sock");
        }
        else if (!OperatingSystem.IsWindows())
        {
            Endpoint.SystemSocketPath.ShouldBe("/run/deskpair/DeskPair.sock");
        }
    }
}
