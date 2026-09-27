using DeskPair.Core.Config;

namespace DeskPair.Core.Tests;

public class PeerSettingsTests
{
    private const string Key = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEWjZBRYqlg374F9psEQ4ql93Qt8r7jhV3KXVO4U38xFU/dbdXpJH9l7fHFGe0NBWtNXS3uw1yYkH7i0pIYkmGwQ==";

    [Theory]
    [InlineData(Key)]
    [InlineData("  " + Key + "\r\n")]
    [InlineData("\"" + Key + "\"")]
    [InlineData("key: " + Key)]
    [InlineData("MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEWjZBRYqlg374F9psEQ4ql93Qt8r7jhV3KXVO4U38xFU/\ndbdXpJH9l7fHFGe0NBWtNXS3uw1yYkH7i0pIYkmGwQ==")]
    public void Pasted_keys_are_cleaned(string pasted)
    {
        var settings = new PeerSettings { RendezvousServer = "x", ServerPublicKeyBase64 = pasted };
        settings.ServerPublicKey.ShouldBe(Convert.FromBase64String(Key));
    }

    [Fact]
    public void Garbage_key_gives_a_readable_error()
    {
        var settings = new PeerSettings { RendezvousServer = "x", ServerPublicKeyBase64 = "{\"service\":\"rendezvous\"}" };
        FormatException e = Should.Throw<FormatException>(() => settings.ServerPublicKey);
        e.Message.ShouldContain("/key");
    }

    [Fact]
    public void Empty_key_means_unverified()
    {
        new PeerSettings { RendezvousServer = "x", ServerPublicKeyBase64 = "  " }.ServerPublicKey.ShouldBeNull();
    }
}
