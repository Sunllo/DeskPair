using Microsoft.Extensions.Time.Testing;
using DeskPair.Core.Portal;
using DeskPair.Desktop.Services;

namespace DeskPair.Desktop.Tests;

/// <summary>
/// Where the controller half of this program looks for a signalling server.
///
/// The host has always asked the published directory. The controller never did, and with the settings
/// everybody has out of the box -- nothing configured, "use the published directory" on -- that left it
/// dialling an empty address. Nothing along the way treats that as wrong: the host/port split returns it
/// unchanged and DNS answers an empty name with this machine's own addresses, so every call went to
/// localhost and came back "connection refused". The same process, registered as a host, against the same
/// server, at the same moment.
/// </summary>
public class NetworkDirectoryCacheTests
{
    private static NetworkDirectory Found(string host) => new(host, "AAAA");

    [Fact]
    public async Task What_the_portal_says_is_what_comes_back()
    {
        var cache = new NetworkDirectoryCache((_, _) => Task.FromResult(Found("rdv.example:21116")));

        (await cache.GetAsync("")).Rendezvous.ShouldBe("rdv.example:21116");
    }

    /// <summary>A connection attempt is not the moment to make a web request.</summary>
    [Fact]
    public async Task It_is_fetched_once_and_then_remembered()
    {
        int fetches = 0;
        var time = new FakeTimeProvider();
        var cache = new NetworkDirectoryCache((_, _) => { fetches++; return Task.FromResult(Found("rdv.example:21116")); }, time);

        for (int i = 0; i < 5; i++)
        {
            await cache.GetAsync("");
        }

        fetches.ShouldBe(1);

        time.Advance(TimeSpan.FromMinutes(31));
        await cache.GetAsync("");
        fetches.ShouldBe(2, "a directory that has gone stale is worth asking about again");
    }

    /// <summary>
    /// A portal that was unreachable while the laptop was waking up must not keep this program unable to
    /// dial for half an hour. Failure is remembered for seconds, not for the full lifetime.
    /// </summary>
    [Fact]
    public async Task A_failed_fetch_is_retried_soon()
    {
        int fetches = 0;
        var time = new FakeTimeProvider();
        var cache = new NetworkDirectoryCache(
            (_, _) => { fetches++; return Task.FromResult(fetches == 1 ? NetworkDirectory.None : Found("rdv.example:21116")); },
            time);

        (await cache.GetAsync("")).Rendezvous.ShouldBeEmpty();

        time.Advance(TimeSpan.FromSeconds(25));
        (await cache.GetAsync("")).Rendezvous.ShouldBe("rdv.example:21116");
    }

    /// <summary>
    /// Called from the path that opens a session window, so a portal that throws must read as a portal
    /// that had nothing to say.
    /// </summary>
    [Fact]
    public async Task A_portal_that_throws_is_a_portal_with_nothing_to_say()
    {
        var cache = new NetworkDirectoryCache((_, _) => throw new HttpRequestException("no route"));

        (await cache.GetAsync("")).Rendezvous.ShouldBeEmpty();
    }

    /// <summary>The configured server always wins; the directory is only what to use when there is none.</summary>
    [Fact]
    public void A_configured_server_beats_the_directory()
    {
        var configured = new DesktopConfig { RendezvousServer = "rdv.mine:21116", ServerPublicKeyBase64 = "MINE" };

        var settings = configured.ToPeerSettings("0.2.0", null, Found("rdv.published:21116"));

        settings.RendezvousServer.ShouldBe("rdv.mine:21116");
        settings.ServerPublicKeyBase64.ShouldBe("MINE");
    }

    /// <summary>
    /// The case everybody is actually in: nothing configured. Before this, both of these were empty, and
    /// an empty address is the bug.
    /// </summary>
    [Fact]
    public void With_nothing_configured_the_directory_is_used_whole()
    {
        var settings = new DesktopConfig().ToPeerSettings("0.2.0", null, Found("rdv.published:21116"));

        settings.RendezvousServer.ShouldBe("rdv.published:21116");
        settings.ServerPublicKeyBase64.ShouldBe("AAAA", "a server address without its key cannot be verified");
    }

    /// <summary>No configuration and no directory still has to be an empty answer rather than a wrong one.</summary>
    [Fact]
    public void With_neither_it_stays_empty_for_the_caller_to_notice()
    {
        var settings = new DesktopConfig().ToPeerSettings("0.2.0", null, NetworkDirectory.None);

        settings.RendezvousServer.ShouldBeEmpty();
    }
}
