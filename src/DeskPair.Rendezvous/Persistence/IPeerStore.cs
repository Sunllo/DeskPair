namespace DeskPair.Rendezvous.Persistence;

public sealed record StoredPeer(string Id, byte[] Uuid, byte[] IdentityPk, DateTimeOffset CreatedUtc, DateTimeOffset LastSeenUtc, string Version);

public interface IPeerStore
{
    Task InitializeAsync(CancellationToken ct);

    Task<IReadOnlyList<StoredPeer>> LoadAllAsync(CancellationToken ct);

    Task<StoredPeer?> FindByUuidAsync(byte[] uuid, CancellationToken ct);

    Task UpsertAsync(IReadOnlyList<StoredPeer> peers, CancellationToken ct);
}

public sealed class NullPeerStore : IPeerStore
{
    public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<IReadOnlyList<StoredPeer>> LoadAllAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<StoredPeer>>([]);

    public Task<StoredPeer?> FindByUuidAsync(byte[] uuid, CancellationToken ct) => Task.FromResult<StoredPeer?>(null);

    public Task UpsertAsync(IReadOnlyList<StoredPeer> peers, CancellationToken ct) => Task.CompletedTask;
}
