using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskPair.Protocol.Crypto;

namespace DeskPair.Core.Update;

/// <summary>One downloadable file, as the release manifest lists it. <see cref="Name"/> is a file name, never a path.</summary>
public sealed record ReleaseFile(string Platform, string Arch, string Name, long Size, string Sha256, bool Signed = false)
{
    /// <summary>Whether this file is the one for the machine this code is running on.</summary>
    public bool IsForThisMachine() =>
        string.Equals(Platform, ThisPlatform, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Arch, ThisArch, StringComparison.OrdinalIgnoreCase)
        && Name.Length > 0
        && Path.GetFileName(Name) == Name;

    public static string ThisPlatform =>
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsLinux() ? "linux" : "other";

    public static string ThisArch => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        var other => other.ToString().ToLowerInvariant(),
    };
}

/// <summary>The release manifest the portal publishes at <c>/downloads/release.json</c>, as far as an installer needs it.</summary>
public sealed record SignedReleaseManifest(string Channel, string Version, IReadOnlyList<ReleaseFile> Files)
{
    /// <summary>The file for this machine, or null when the release has none for it.</summary>
    public ReleaseFile? FileForThisMachine() => Files.FirstOrDefault(f => f.IsForThisMachine());
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(SignedReleaseManifest))]
internal sealed partial class SignedReleaseJson : JsonSerializerContext;

/// <summary>
/// A release manifest the app may act on, as opposed to one it may only tell somebody about.
///
/// The update notice needs no signature: it opens a download page built from the configured portal, so a
/// forged manifest can at most claim a version that does not exist. Installing is different. An installer
/// that fetched a file named by an unsigned manifest and ran it would let anyone who can answer for the
/// portal -- a captive portal, a mistaken DNS entry, a compromised host -- put a program on this machine.
/// So the bytes of <c>release.json</c> are signed offline, on the release machine, with a key the portal
/// never holds; the app carries the public half and verifies the exact bytes before it parses them. The
/// hashes inside then name the files, and a file is installed only when its hash matches.
/// </summary>
public static class SignedRelease
{
    /// <summary>What the manifest's signature file holds: the base64 of a P-256 signature over the manifest's bytes.</summary>
    public const string SignatureFileName = "release.json.sig";

    public const string ManifestFileName = "release.json";

    /// <summary>Whether <paramref name="signatureBase64"/> is <paramref name="publicKeySpki"/>'s signature over exactly <paramref name="manifest"/>.</summary>
    public static bool Verify(ReadOnlySpan<byte> manifest, string? signatureBase64, ReadOnlySpan<byte> publicKeySpki)
    {
        if (string.IsNullOrWhiteSpace(signatureBase64) || publicKeySpki.IsEmpty || manifest.IsEmpty)
        {
            return false;
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(signatureBase64.Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        return IdentityKey.Verify(publicKeySpki, manifest, signature);
    }

    /// <summary>The signing side, for the release tool: the base64 to write beside the manifest.</summary>
    public static string Sign(IdentityKey key, ReadOnlySpan<byte> manifest)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Convert.ToBase64String(key.Sign(manifest));
    }

    /// <summary>Parses verified bytes. Never call this on bytes that did not verify.</summary>
    public static SignedReleaseManifest? Parse(ReadOnlySpan<byte> manifest)
    {
        try
        {
            SignedReleaseManifest? read = JsonSerializer.Deserialize(manifest, SignedReleaseJson.Default.SignedReleaseManifest);
            if (read is null)
            {
                return null;
            }

            // A name with a path in it would become a path on this machine; a hash that is not one cannot
            // be checked. Neither is a file this will download.
            var files = read.Files?
                .Where(f => f is not null && f.Name.Length > 0 && Path.GetFileName(f.Name) == f.Name
                    && f.Sha256.Length == 64 && f.Sha256.All(Uri.IsHexDigit))
                .Select(f => f with { Sha256 = f.Sha256.ToLowerInvariant() })
                .ToList() ?? [];
            return read with { Channel = read.Channel ?? "stable", Version = read.Version ?? string.Empty, Files = files };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Which key a manifest has to be signed with.
///
/// The official one is a constant: the private half lives on the release machine and nowhere else, and
/// the portal serves signatures it could not have produced. A self-hosted portal that wants its own
/// installs to be one click supplies its own public key in the settings; without one, its manifests are
/// news and the button opens the download page, which is what an unsigned manifest is for.
/// </summary>
public static class ReleaseSigning
{
    /// <summary>The release key's public half, base64 SPKI. Fingerprint D0721541DE0065F9964C9E6460A2321B1255680E90B331A1924BA3D44880FC18.</summary>
    public const string OfficialPublicKeyBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEtQrNtoT8U4fcOF/9xl4r1/TGxwXFMwpe8F3ZzBizW7Af+kE5kNPUSmcgx/LvSstMyoTb3Orc85k83TEO9cP94w==";

    /// <summary>
    /// The key to verify with: the official one for the official portal, the configured one for any other,
    /// and none -- notify only -- for a self-hosted portal that configured nothing.
    /// </summary>
    public static byte[]? KeyFor(string? portal, string? configuredPublicKeyBase64)
    {
        string resolved = UpdateEndpoints.PortalFor(portal);
        if (string.Equals(resolved, UpdateEndpoints.OfficialPortal, StringComparison.OrdinalIgnoreCase))
        {
            return Convert.FromBase64String(OfficialPublicKeyBase64);
        }

        if (string.IsNullOrWhiteSpace(configuredPublicKeyBase64))
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(configuredPublicKeyBase64.Trim());
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
