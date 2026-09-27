using DeskPair.Core.Config;

namespace DeskPair.Core.Tests;

public sealed class ConnectLinkTests
{
    // A real P-256 SPKI, so the base64url round trip is exercised on something the right length and with
    // the padding a 91-byte key actually produces.
    private const string ServerKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEWjZBRYqlg374F9psEQ4ql93Qt8r7jhV3KXVO4U38xFU/dbdXpJH9l7fHFGe0NBWtNXS3uw1yYkH7i0pIYkmGwQ==";

    private static ConnectLink Sample => new()
    {
        Id = "123456789",
        RendezvousServer = "203.0.113.10:21116",
        ServerPublicKeyBase64 = ServerKey,
        DeviceName = "Office PC",
        Password = "t4nq7zkp2xmr",
    };

    [Fact]
    public void A_link_survives_a_round_trip()
    {
        ConnectLink.TryParse(Sample.ToString(), out ConnectLink? read, out string? problem).ShouldBeTrue(problem);
        read.ShouldBe(Sample);
    }

    [Fact]
    public void The_key_travels_as_base64url_so_the_code_stays_small()
    {
        // '+' and '/' would each have to be percent-encoded, which is three characters instead of one.
        string text = Sample.ToString();
        text.ShouldNotContain("%2B");
        text.ShouldNotContain("%2F");
        text.ShouldContain("&k=");
    }

    [Fact]
    public void The_password_a_link_carries_survives_the_round_trip()
    {
        // A code on a screen is photographed, screen-shared and left up, so what it carries has to stop
        // working: this field holds the host's one-time link password, which the first connection to use it
        // spends (HostPasswordTests covers that end). Losing it in transit would not fail loudly — the
        // phone would simply ask for a password whose only copy was in the code it just read.
        Sample.ToString().ShouldContain("&p=t4nq7zkp2xmr");

        ConnectLink.TryParse(Sample.ToString(), out ConnectLink? read, out string? problem).ShouldBeTrue(problem);
        read!.Password.ShouldBe("t4nq7zkp2xmr");
    }

    [Fact]
    public void A_host_offering_no_password_writes_no_field()
    {
        // What a desk with temporary passwords switched off produces. The phone must read it as a good
        // code that simply has no password in it, and ask.
        var quiet = Sample with { Password = string.Empty };
        quiet.ToString().ShouldNotContain("&p=");

        ConnectLink.TryParse(quiet.ToString(), out ConnectLink? read, out string? problem).ShouldBeTrue(problem);
        read!.Password.ShouldBeEmpty();
    }

    [Fact]
    public void An_id_on_its_own_is_a_valid_link()
    {
        var minimal = new ConnectLink { Id = "123456789" };
        ConnectLink.TryParse(minimal.ToString(), out ConnectLink? read, out string? problem).ShouldBeTrue(problem);
        read!.Id.ShouldBe("123456789");
        read.RendezvousServer.ShouldBeEmpty();
        read.ServerPublicKeyBase64.ShouldBeEmpty();
    }

    [Fact]
    public void A_newer_version_is_refused_rather_than_guessed_at()
    {
        ConnectLink.TryParse("sunllo://connect?v=2&id=123456789", out ConnectLink? read, out string? problem)
            .ShouldBeFalse();
        read.ShouldBeNull();
        problem.ShouldNotBeNull();
        problem.ShouldContain("v2");

        // And says where to go. It used to say "Update this app." and name nowhere, which is advice that
        // cannot be followed.
        problem.ShouldContain("deskpair.app");
    }

    [Fact]
    public void A_link_with_no_version_is_refused()
    {
        ConnectLink.TryParse("sunllo://connect?id=123456789", out _, out string? problem).ShouldBeFalse();
        problem.ShouldNotBeNull();
    }

    [Fact]
    public void A_link_with_no_id_is_refused()
    {
        ConnectLink.TryParse("sunllo://connect?v=1&rs=example.com", out _, out string? problem).ShouldBeFalse();
        problem.ShouldNotBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("https://example.com/?v=1&id=123456789")]
    [InlineData("rustdesk://connection/new/123456789")]
    public void Something_that_is_not_a_DeskPair_code_is_refused(string text)
    {
        ConnectLink.TryParse(text, out _, out string? problem).ShouldBeFalse();
        problem.ShouldNotBeNull();
    }

    [Fact]
    public void A_damaged_key_is_caught_at_the_scanner_rather_than_at_the_next_connection()
    {
        ConnectLink.TryParse("sunllo://connect?v=1&id=123456789&k=!!!not-base64!!!", out _, out string? problem)
            .ShouldBeFalse();
        problem.ShouldNotBeNull();
    }

    [Fact]
    public void Fields_can_arrive_in_any_order_and_unknown_ones_are_ignored()
    {
        // Forward compatibility: a newer build adding a field must not stop this one reading the basics.
        const string text = "sunllo://connect?id=123456789&future=whatever&v=1&rs=example.com%3A21116";
        ConnectLink.TryParse(text, out ConnectLink? read, out string? problem).ShouldBeTrue(problem);
        read!.Id.ShouldBe("123456789");
        read.RendezvousServer.ShouldBe("example.com:21116");
    }

    [Fact]
    public void A_device_name_with_spaces_and_non_ascii_survives()
    {
        var link = new ConnectLink { Id = "123456789", DeviceName = "辦公室 主機" };
        ConnectLink.TryParse(link.ToString(), out ConnectLink? read, out string? problem).ShouldBeTrue(problem);
        read!.DeviceName.ShouldBe("辦公室 主機");
    }
}
