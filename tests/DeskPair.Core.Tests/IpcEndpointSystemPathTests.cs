using DeskPair.Core.Ipc;

namespace DeskPair.Core.Tests;

/// <summary>
/// An engine that is not root can still listen where the app looks first.
///
/// The Linux daemon starts its engine as an ordinary system account, and that engine used to bind under
/// a per-user runtime directory it did not have -- so it fell to /tmp/deskpair-deskpair/, a path on
/// nobody's connect list, and the app never found the engine it was installed to attach to.
/// </summary>
public class IpcEndpointSystemPathTests
{
    [Fact]
    public void Asked_to_listen_machine_wide_a_non_root_engine_binds_the_system_path()
    {
        IpcEndpoint endpoint = IpcEndpoint.ForTest() with { UseSystemPath = true };

        endpoint.ListenPath.ShouldBe(endpoint.SystemSocketPath);
        endpoint.ConnectPaths.ShouldContain(endpoint.ListenPath, "or nothing would ever connect to it");
    }

    [Fact]
    public void Otherwise_the_choice_is_whether_this_process_is_root()
    {
        IpcEndpoint endpoint = IpcEndpoint.ForTest();

        // This test does not run as root anywhere it is run.
        endpoint.UseSystemPath.ShouldBeFalse();
        endpoint.ListenPath.ShouldBe(endpoint.UserSocketPath);
    }
}
