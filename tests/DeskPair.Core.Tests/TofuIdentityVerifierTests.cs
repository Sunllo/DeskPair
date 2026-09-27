using DeskPair.Core.Transport;
using DeskPair.Protocol;
using DeskPair.Protocol.Crypto;

namespace DeskPair.Core.Tests;

public class TofuIdentityVerifierTests
{
    [Fact]
    public void First_key_is_pinned_and_a_changed_key_is_rejected()
    {
        var store = new InMemoryKnownHostsStore();
        var verifier = new TofuIdentityVerifier(store, "192.168.1.10:21118");
        using IdentityKey first = IdentityKey.Create();
        using IdentityKey second = IdentityKey.Create();

        verifier.Verify("host", first.PublicKeySpki);
        store.Get("192.168.1.10:21118").ShouldBe(TofuIdentityVerifier.Fingerprint(first.PublicKeySpki));
        Should.NotThrow(() => verifier.Verify("host", first.PublicKeySpki));
        Should.Throw<HandshakeException>(() => verifier.Verify("host", second.PublicKeySpki));

        store.Remove("192.168.1.10:21118");
        Should.NotThrow(() => verifier.Verify("host", second.PublicKeySpki));
    }

    [Fact]
    public void File_store_round_trips()
    {
        string path = Path.Combine(Path.GetTempPath(), "sunllo-known-hosts-" + Guid.NewGuid().ToString("N"), "known_hosts.json");
        try
        {
            var store = new FileKnownHostsStore(path);
            byte[] pin = new byte[32];
            Random.Shared.NextBytes(pin);
            store.Set("desk.local:21118", pin);
            new FileKnownHostsStore(path).Get("desk.local:21118").ShouldBe(pin);
            new FileKnownHostsStore(path).Get("other").ShouldBeNull();
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}
