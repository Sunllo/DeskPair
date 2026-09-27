using System.Net;
using DeskPair.Core.Config;
using DeskPair.Core.Session.Host.Auth;

namespace DeskPair.Core.Tests;

/// <summary>
/// Who may connect at all. Every test here guards a way of locking the wrong people out -- or the wrong
/// people in -- which is the only kind of mistake this feature can make.
/// </summary>
public class PeerAllowlistTests
{
    private static PeerAllowlist List(params string[] entries) => PeerAllowlist.Create(true, entries, out _);

    private static IPAddress Ip(string text) => IPAddress.Parse(text);

    [Fact]
    public void Off_allows_everyone()
    {
        PeerAllowlist.Off.Enabled.ShouldBeFalse();
        PeerAllowlist.Off.MayAccept(Ip("198.51.100.9")).ShouldBeTrue();
        PeerAllowlist.Off.Allows(Ip("198.51.100.9"), "123456789").ShouldBeTrue();
        PeerAllowlist.Off.Allows(null, null).ShouldBeTrue("a peer whose address is unknown is not refused by a list that is not in use");
    }

    /// <summary>Turning it on with nothing in it is a deliberate "only me", not a misconfiguration to forgive.</summary>
    [Fact]
    public void On_and_empty_refuses_everybody()
    {
        PeerAllowlist list = List();

        list.IsEmpty.ShouldBeTrue();
        list.MayAccept(Ip("127.0.0.1")).ShouldBeFalse();
        list.Allows(Ip("127.0.0.1"), "123456789").ShouldBeFalse();
    }

    [Fact]
    public void One_address_matches_only_itself()
    {
        PeerAllowlist list = List("203.0.113.5");

        list.MayAccept(Ip("203.0.113.5")).ShouldBeTrue();
        list.MayAccept(Ip("203.0.113.6")).ShouldBeFalse();
        list.MayAccept(null).ShouldBeFalse("a connection whose address is unknown cannot match an address");
    }

    [Theory]
    [InlineData("192.168.1.0/24", "192.168.1.0", true)]
    [InlineData("192.168.1.0/24", "192.168.1.255", true)]
    [InlineData("192.168.1.0/24", "192.168.2.1", false)]
    [InlineData("192.168.1.0/32", "192.168.1.1", false)]
    [InlineData("192.168.1.1/32", "192.168.1.1", true)]
    [InlineData("10.0.0.0/8", "10.255.255.254", true)]
    [InlineData("10.0.0.0/8", "11.0.0.1", false)]
    [InlineData("0.0.0.0/0", "8.8.8.8", true)]
    [InlineData("203.0.113.128/25", "203.0.113.200", true)]
    [InlineData("203.0.113.128/25", "203.0.113.127", false)]
    [InlineData("2001:db8::/32", "2001:db8:1234::1", true)]
    [InlineData("2001:db8::/32", "2001:db9::1", false)]
    [InlineData("2001:db8::/128", "2001:db8::", true)]
    public void Ranges_match_at_their_edges(string entry, string address, bool expected) =>
        List(entry).MayAccept(Ip(address)).ShouldBe(expected);

    /// <summary>
    /// The direct listener is dual-mode, so a v4 peer arrives as ::ffff:192.168.1.7. Somebody who typed a v4
    /// range means that peer, and a list that did not unwrap it would lock them out of their own network.
    /// </summary>
    [Fact]
    public void An_ipv4_peer_arriving_over_ipv6_still_matches_an_ipv4_range()
    {
        PeerAllowlist list = List("192.168.1.0/24");

        list.MayAccept(Ip("192.168.1.7").MapToIPv6()).ShouldBeTrue();
        list.MayAccept(Ip("192.168.2.7").MapToIPv6()).ShouldBeFalse();
    }

    /// <summary>The families do not mix: a v6 peer is not inside a v4 range, and the other way round.</summary>
    [Fact]
    public void Families_do_not_cross()
    {
        List("0.0.0.0/0").MayAccept(Ip("2001:db8::1")).ShouldBeFalse();
        List("::/0").MayAccept(Ip("8.8.8.8")).ShouldBeFalse("a real v6 address is a different family from a v4 one");
        List("::/0").MayAccept(Ip("8.8.8.8").MapToIPv6()).ShouldBeFalse("and a mapped v4 peer is unwrapped before it is matched");
    }

    /// <summary>
    /// An id can only be judged once the peer has said who it is, so a list holding one has to let the
    /// handshake happen. That is the whole reason there are two questions rather than one.
    /// </summary>
    [Fact]
    public void An_id_entry_is_decided_at_login_not_at_accept()
    {
        PeerAllowlist list = List("id:123456789");

        list.MayAccept(Ip("198.51.100.9")).ShouldBeTrue("the address alone cannot refuse this list");
        list.Allows(Ip("198.51.100.9"), "123456789").ShouldBeTrue();
        list.Allows(Ip("198.51.100.9"), "987654321").ShouldBeFalse();
        list.Allows(Ip("198.51.100.9"), null).ShouldBeFalse();
    }

    [Fact]
    public void An_address_and_an_id_are_two_ways_in()
    {
        PeerAllowlist list = List("192.168.1.0/24", "id:123456789");

        list.Allows(Ip("192.168.1.7"), "987654321").ShouldBeTrue("the address is enough");
        list.Allows(Ip("198.51.100.9"), "123456789").ShouldBeTrue("the id is enough");
        list.Allows(Ip("198.51.100.9"), "987654321").ShouldBeFalse();
    }

    /// <summary>A typo must not silently become "refuse everybody"; it is dropped and named.</summary>
    [Fact]
    public void Entries_that_cannot_be_read_are_dropped_and_reported()
    {
        PeerAllowlist list = PeerAllowlist.Create(true, ["192.168.1.0/24", "not an address", "10.0.0.0/33", "id:", "  "], out IReadOnlyList<string> rejected);

        list.Count.ShouldBe(1);
        rejected.ShouldBe(["not an address", "10.0.0.0/33", "id:"]);
        list.MayAccept(Ip("192.168.1.7")).ShouldBeTrue("the entries that were understood still work");
    }

    [Fact]
    public void The_settings_page_can_check_one_entry()
    {
        PeerAllowlist.IsValidEntry("192.168.1.0/24").ShouldBeTrue();
        PeerAllowlist.IsValidEntry(" 2001:db8::1 ").ShouldBeTrue();
        PeerAllowlist.IsValidEntry("id:123456789").ShouldBeTrue();
        PeerAllowlist.IsValidEntry("192.168.1.0/33").ShouldBeFalse();
        PeerAllowlist.IsValidEntry("nonsense").ShouldBeFalse();
        PeerAllowlist.IsValidEntry("id:").ShouldBeFalse();
    }

    [Fact]
    public void The_config_builds_the_list_and_stays_comparable()
    {
        var config = new HostConfig { AllowlistEnabled = true, AllowedPeers = ["192.168.1.0/24", "id:123456789"], RefuseRelayed = true };

        PeerAllowlist list = config.BuildAllowlist(out IReadOnlyList<string> rejected);
        list.Enabled.ShouldBeTrue();
        list.Count.ShouldBe(2);
        rejected.ShouldBeEmpty();

        HostConfig read = HostConfig.FromJson(config.ToJson());
        read.AllowedPeers.ShouldBe(["192.168.1.0/24", "id:123456789"]);
        read.AllowlistEnabled.ShouldBeTrue();
        read.RefuseRelayed.ShouldBeTrue();
        read.ShouldBe(config, "the list is NUL-joined so the record keeps value equality");

        new HostConfig().BuildAllowlist(out _).Enabled.ShouldBeFalse("off by default");
    }
}
