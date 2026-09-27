using DeskPair.Protocol.Crypto;

// Generates (or reads) a signing key and prints the public key clients must configure. The same key shape
// serves two jobs: the rendezvous server's, and the release key that signs release.json.
//
//   KeyGen [key-file]                  make or read a key, print its public half
//   KeyGen sign <key-file> <file>      write <file>.sig: the base64 P-256 signature over the file's bytes
if (args.Length == 3 && args[0] == "sign")
{
    using IdentityKey signer = IdentityKey.FromPkcs8(File.ReadAllBytes(args[1]));
    byte[] content = File.ReadAllBytes(args[2]);
    File.WriteAllText(args[2] + ".sig", Convert.ToBase64String(signer.Sign(content)) + "\n");
    Console.Error.WriteLine($"Signed {args[2]} ({content.Length} bytes) with {IdentityKey.Fingerprint(signer.PublicKeySpki)[..16]}…");
    return;
}

string path = args.Length > 0 ? args[0] : "server.key";
IdentityKey key;
if (File.Exists(path))
{
    key = IdentityKey.FromPkcs8(File.ReadAllBytes(path));
    Console.Error.WriteLine($"Read existing key from {path}");
}
else
{
    key = IdentityKey.Create();
    File.WriteAllBytes(path, key.ExportPkcs8());
    if (!OperatingSystem.IsWindows())
    {
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    Console.Error.WriteLine($"Wrote new key to {path}");
}

Console.WriteLine($"Public key (base64 SPKI): {Convert.ToBase64String(key.PublicKeySpki)}");
Console.WriteLine($"Fingerprint (SHA-256):    {key.Fingerprint()}");
key.Dispose();
