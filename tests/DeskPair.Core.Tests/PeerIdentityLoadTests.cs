using DeskPair.Core.Config;
using DeskPair.Platform.Abstractions.Security;

namespace DeskPair.Core.Tests;

/// <summary>
/// Who is allowed to decide what this machine is.
///
/// Minting an identity key is how a machine gets its name: its id at the rendezvous server, the
/// fingerprint every device that trusts it has saved, and the salt its permanent password is keyed to.
/// The engine does that, once. Everything else -- the settings page, the address-book sync -- is asking
/// about a machine that already exists.
///
/// They all used to call LoadOrCreateAsync, and the difference only shows on a machine that has no
/// identity yet, where they run in parallel with the engine on the way up and whichever gets there first
/// wins. It was watched happening on a Mac carrying its identity over from the product's previous name:
/// the address-book sync wrote a fresh key in the same second the window opened, the carry-over then
/// correctly declined to overwrite it, and the machine kept its old id with a key the rendezvous server
/// had never signed. Every connection after that failed at the handshake, truthfully and uselessly.
/// </summary>
public class PeerIdentityLoadTests
{
    private sealed class NoMachineId : IMachineIdProvider
    {
        public byte[] GetStableMachineId() => [];
    }

    private sealed class Memory : ISecretStore
    {
        public Dictionary<string, byte[]> Items { get; } = new(StringComparer.Ordinal);

        public int Writes { get; private set; }

        public ValueTask<byte[]?> GetAsync(string key, CancellationToken ct = default) =>
            ValueTask.FromResult(Items.TryGetValue(key, out byte[]? value) ? value : null);

        public ValueTask SetAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct = default)
        {
            Writes++;
            Items[key] = value.ToArray();
            return ValueTask.CompletedTask;
        }

        public ValueTask RemoveAsync(string key, CancellationToken ct = default)
        {
            Items.Remove(key);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>The property everything above rests on: asking does not create.</summary>
    [Fact]
    public async Task Reading_an_identity_that_is_not_there_writes_nothing()
    {
        var store = new Memory();

        PeerIdentityStore? identity = await PeerIdentityStore.LoadAsync(store, new NoMachineId());

        identity.ShouldBeNull();
        store.Writes.ShouldBe(0);
        store.Items.ShouldBeEmpty("a machine with no identity must still have none after being asked about");
    }

    [Fact]
    public async Task An_identity_that_is_there_is_read_back_whole()
    {
        var store = new Memory();
        using PeerIdentityStore made = await PeerIdentityStore.LoadOrCreateAsync(store, new NoMachineId());
        await made.SetIdAsync("123456789");

        using PeerIdentityStore? read = await PeerIdentityStore.LoadAsync(store, new NoMachineId());

        read.ShouldNotBeNull();
        read!.Id.ShouldBe("123456789");
        read.Key.Fingerprint().ShouldBe(made.Key.Fingerprint());
    }

    /// <summary>
    /// The engine's call still mints, because something has to. What matters is that it is the only one.
    /// </summary>
    [Fact]
    public async Task The_engines_call_still_makes_one_when_there_is_none()
    {
        var store = new Memory();

        using PeerIdentityStore identity = await PeerIdentityStore.LoadOrCreateAsync(store, new NoMachineId());

        identity.ShouldNotBeNull();
        store.Items.ShouldContainKey("identity-key");
        identity.IsLocalId.ShouldBeTrue("no server has assigned one yet");
    }

    /// <summary>
    /// Reading twice must not produce two machines, which is the failure the whole distinction exists to
    /// prevent -- and the one that is invisible, because both halves work perfectly on their own.
    /// </summary>
    [Fact]
    public async Task Reading_repeatedly_never_changes_what_the_machine_is()
    {
        var store = new Memory();
        using PeerIdentityStore made = await PeerIdentityStore.LoadOrCreateAsync(store, new NoMachineId());
        string fingerprint = made.Key.Fingerprint();
        int writes = store.Writes;

        for (int i = 0; i < 5; i++)
        {
            using PeerIdentityStore? read = await PeerIdentityStore.LoadAsync(store, new NoMachineId());
            read!.Key.Fingerprint().ShouldBe(fingerprint);
        }

        store.Writes.ShouldBe(writes);
    }
}
