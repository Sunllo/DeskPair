using DeskPair.Desktop.ViewModels;

namespace DeskPair.Desktop.Tests;

/// <summary>
/// How a caller's address is written on the notice that says somebody is connecting.
///
/// It is only shown for the moment before the caller says who it is, and for that moment it should look
/// like an address a person recognises. A dual-stack listener reports every IPv4 caller as an IPv4-mapped
/// IPv6 address, so what actually reached the screen was "::ffff:203.0.113.243" -- which reads as a
/// stranger and more alarming kind of address than the one it is, on a notice whose whole job is to say
/// who is at the door.
/// </summary>
public class IncomingAddressTests
{
    [Fact]
    public void An_ipv4_caller_is_written_as_ipv4()
    {
        IncomingConnectionsViewModel.Readable("::ffff:203.0.113.243").ShouldBe("203.0.113.243");
    }

    /// <summary>
    /// The port is the ephemeral one the caller happened to get; it says nothing about them.
    ///
    /// The bracketed form is what an endpoint actually prints -- IPEndPoint.ToString writes
    /// "[::ffff:203.0.113.243]:56939" -- and the unbracketed one is genuinely ambiguous, since the last
    /// colon could belong to the address. Ambiguous input is left as it arrived rather than guessed at.
    /// </summary>
    [Fact]
    public void The_port_is_left_off()
    {
        var endPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Parse("::ffff:203.0.113.243"), 56939);

        IncomingConnectionsViewModel.Readable(endPoint.ToString()).ShouldBe("203.0.113.243");
        IncomingConnectionsViewModel.Readable("192.168.0.4:21118").ShouldBe("192.168.0.4");
    }

    /// <summary>A caller that really is on IPv6 is left as it is; that address is the truth about them.</summary>
    [Fact]
    public void A_real_ipv6_caller_is_left_alone()
    {
        IncomingConnectionsViewModel.Readable("2001:db8::1").ShouldBe("2001:db8::1");
        IncomingConnectionsViewModel.Readable("[2001:db8::1]:443").ShouldBe("2001:db8::1");
    }

    /// <summary>Anything that is not an address at all passes through rather than becoming blank.</summary>
    [Fact]
    public void Something_that_is_not_an_address_is_shown_as_it_arrived()
    {
        IncomingConnectionsViewModel.Readable("relay").ShouldBe("relay");
        IncomingConnectionsViewModel.Readable("  192.168.0.4  ").ShouldBe("192.168.0.4");
        IncomingConnectionsViewModel.Readable(string.Empty).ShouldBeEmpty();
    }
}
