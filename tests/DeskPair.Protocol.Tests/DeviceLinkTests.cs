using System.Text;
using DeskPair.Protocol.Crypto;

namespace DeskPair.Protocol.Tests;

/// <summary>
/// The bytes a device signs when linking to an account.
///
/// Pinned rather than merely exercised. Three implementations have to agree on them — the portal, the .NET
/// client and the Kotlin one — and a disagreement has exactly one symptom: linking fails with "this device
/// could not prove it owns its key", which reads like a key problem and is not one. A vector here is what
/// makes that a failing test instead of an afternoon.
/// </summary>
public class DeviceLinkTests
{
    [Fact]
    public void The_challenge_is_a_fixed_shape()
    {
        byte[] challenge = DeviceLink.Challenge("ABCD2345", "http://portal.test:21120");

        // Written out in full: a test that rebuilds the string the same way the code does proves nothing.
        Encoding.UTF8.GetString(challenge).ShouldBe("deskpair-device-link\nABCD2345\nhttp://portal.test:21120");
    }

    [Fact]
    public void The_audience_is_the_portal_without_a_trailing_slash()
    {
        // Otherwise a client that normalises and a portal that does not sign different messages.
        DeviceLink.Challenge("ABCD2345", "http://portal.test:21120/")
            .ShouldBe(DeviceLink.Challenge("ABCD2345", "http://portal.test:21120"));
    }

    [Fact]
    public void A_signature_from_one_portal_does_not_verify_at_another()
    {
        using IdentityKey device = IdentityKey.Create();
        byte[] signature = device.Sign(DeviceLink.Challenge("ABCD2345", "http://portal.one:21120"));

        IdentityKey.Verify(device.PublicKeySpki, DeviceLink.Challenge("ABCD2345", "http://portal.one:21120"), signature).ShouldBeTrue();
        IdentityKey.Verify(device.PublicKeySpki, DeviceLink.Challenge("ABCD2345", "http://portal.two:21120"), signature).ShouldBeFalse();
    }

    [Fact]
    public void A_signature_for_one_code_does_not_verify_for_another()
    {
        using IdentityKey device = IdentityKey.Create();
        byte[] signature = device.Sign(DeviceLink.Challenge("ABCD2345", "http://portal.test"));

        IdentityKey.Verify(device.PublicKeySpki, DeviceLink.Challenge("ABCD2346", "http://portal.test"), signature).ShouldBeFalse();
    }

    [Fact]
    public void Another_key_cannot_sign_for_this_one()
    {
        using IdentityKey mine = IdentityKey.Create();
        using IdentityKey theirs = IdentityKey.Create();
        byte[] challenge = DeviceLink.Challenge("ABCD2345", "http://portal.test");

        IdentityKey.Verify(mine.PublicKeySpki, challenge, theirs.Sign(challenge)).ShouldBeFalse();
    }

    [Theory]
    [InlineData("abcd2345")]
    [InlineData("ABCD-2345")]
    [InlineData(" abcd 2345 ")]
    [InlineData("abcd-2345\n")]
    public void A_code_is_read_the_way_a_person_types_it(string typed)
    {
        // The console prints ABCD-2345. People type back the separator, or drop it, or use lower case.
        DeviceLink.Normalise(typed).ShouldBe("ABCD2345");
    }

    [Fact]
    public void The_console_prints_a_code_in_two_groups()
    {
        DeviceLink.Format("ABCD2345").ShouldBe("ABCD-2345");

        // Anything that is not a code is left alone rather than sliced at index four.
        DeviceLink.Format("SHORT").ShouldBe("SHORT");
    }

    [Fact]
    public void The_code_alphabet_has_no_ambiguous_characters()
    {
        DeviceLink.CodeAlphabet.Length.ShouldBe(DeviceLink.CodeAlphabet.Distinct().Count());
        DeviceLink.CodeAlphabet.ShouldNotContain("0", Case.Sensitive);
        DeviceLink.CodeAlphabet.ShouldNotContain("O", Case.Sensitive);
        DeviceLink.CodeAlphabet.ShouldNotContain("1", Case.Sensitive);
        DeviceLink.CodeAlphabet.ShouldNotContain("I", Case.Sensitive);
    }
}
