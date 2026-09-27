using DeskPair.Protocol.Crypto;
using DeskPair.Protocol.Rendezvous;

namespace DeskPair.Rendezvous.Core;

/// <summary>Holds the server's ECDSA signing key and signs peer identities with it.</summary>
public sealed class ServerKeys : IDisposable
{
    private readonly IdentityKey _key;

    public ServerKeys(IdentityKey key)
    {
        _key = key;
    }

    public byte[] PublicKeySpki => _key.PublicKeySpki;

    /// <summary>The value users paste as the "server key".</summary>
    public string PublicKeyBase64 => Convert.ToBase64String(_key.PublicKeySpki);

    public string Fingerprint => _key.Fingerprint();

    public static ServerKeys LoadOrCreate(string? keyPath, ILogger log)
    {
        if (string.IsNullOrEmpty(keyPath))
        {
            log.LogWarning("No KeyPath configured; using an ephemeral server key");
            return new ServerKeys(IdentityKey.Create());
        }

        if (File.Exists(keyPath))
        {
            return new ServerKeys(IdentityKey.FromPkcs8(File.ReadAllBytes(keyPath)));
        }

        string? dir = Path.GetDirectoryName(Path.GetFullPath(keyPath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        IdentityKey key = IdentityKey.Create();
        File.WriteAllBytes(keyPath, key.ExportPkcs8());
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        log.LogInformation("Generated new server key at {Path}", keyPath);
        return new ServerKeys(key);
    }

    public SignedPeerIdentity Sign(string id, ReadOnlySpan<byte> identityPk, DateTimeOffset now) =>
        SignedIdentity.Sign(_key, id, identityPk, now);

    /// <summary>Permission for one relay pairing, good until <paramref name="expires"/> at any relay that trusts this key.</summary>
    public RelayTicket IssueRelayTicket(string uuid, DateTimeOffset expires) =>
        RelayTickets.Issue(_key, uuid, string.Empty, expires);

    public void Dispose() => _key.Dispose();
}
