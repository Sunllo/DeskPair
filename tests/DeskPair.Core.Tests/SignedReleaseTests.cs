using System.Text;
using DeskPair.Core.Update;
using DeskPair.Protocol.Crypto;

namespace DeskPair.Core.Tests;

/// <summary>
/// The line between "there is an update" and "install it". The first needs no signature; the second
/// installs a program, so every way the manifest could be not ours has to be refused here.
/// </summary>
public class SignedReleaseTests
{
    private const string Manifest = """
        { "channel": "stable", "version": "0.2.1", "published": "2026-09-22",
          "files": [
            { "platform": "windows", "arch": "x64", "name": "DeskPair-0.2.1-win-x64.zip", "size": 10,
              "sha256": "63a901f700d76e942ce0ad6a5280c13e8b76857fafb0e714ebcb653779303cff", "signed": false },
            { "platform": "macos", "arch": "arm64", "name": "DeskPair-0.2.1-arm64.dmg", "size": 20,
              "sha256": "44D43A4B17746137CBD769A724AB000F3B3DE29BAAD4CBCF1147496BFDF35499", "signed": true },
            { "platform": "linux", "arch": "x64", "name": "../etc/passwd", "size": 1,
              "sha256": "d2eb34e9b94820d4a228ecbd209d96e71fc69304356bbc74d040984017e74b95" },
            { "platform": "linux", "arch": "arm64", "name": "DeskPair-0.2.1-linux-arm64.tar.gz", "size": 1,
              "sha256": "not-a-hash" }
          ],
          "history": [] }
        """;

    [Fact]
    public void The_signature_verifies_only_over_the_exact_bytes_with_the_right_key()
    {
        using IdentityKey key = IdentityKey.Create();
        using IdentityKey other = IdentityKey.Create();
        byte[] bytes = Encoding.UTF8.GetBytes(Manifest);
        string signature = SignedRelease.Sign(key, bytes);

        SignedRelease.Verify(bytes, signature, key.PublicKeySpki).ShouldBeTrue();

        // One byte moved: a version bumped, a hash swapped, a trailing newline added by an editor.
        byte[] tampered = (byte[])bytes.Clone();
        tampered[^3] ^= 1;
        SignedRelease.Verify(tampered, signature, key.PublicKeySpki).ShouldBeFalse();
        SignedRelease.Verify(bytes.AsSpan()[..^1], signature, key.PublicKeySpki).ShouldBeFalse();

        SignedRelease.Verify(bytes, signature, other.PublicKeySpki).ShouldBeFalse();
        SignedRelease.Verify(bytes, "not base64 !!", key.PublicKeySpki).ShouldBeFalse();
        SignedRelease.Verify(bytes, "", key.PublicKeySpki).ShouldBeFalse();
        SignedRelease.Verify(bytes, null, key.PublicKeySpki).ShouldBeFalse();
        SignedRelease.Verify(bytes, signature, []).ShouldBeFalse();
    }

    [Fact]
    public void Parsing_keeps_only_files_that_can_be_named_on_disk_and_checked()
    {
        SignedReleaseManifest manifest = SignedRelease.Parse(Encoding.UTF8.GetBytes(Manifest)).ShouldNotBeNull();

        manifest.Version.ShouldBe("0.2.1");
        manifest.Channel.ShouldBe("stable");

        // The path-shaped name and the non-hash are gone; the upper-case hash is normalised.
        manifest.Files.Select(f => f.Name).ShouldBe(["DeskPair-0.2.1-win-x64.zip", "DeskPair-0.2.1-arm64.dmg"]);
        manifest.Files[1].Sha256.ShouldBe("44d43a4b17746137cbd769a724ab000f3b3de29baad4cbcf1147496bfdf35499");
    }

    [Fact]
    public void Garbage_parses_to_nothing_rather_than_throwing()
    {
        SignedRelease.Parse("<html>captive portal</html>"u8).ShouldBeNull();
        SignedRelease.Parse("{ \"files\": 5 }"u8).ShouldBeNull();
    }

    [Fact]
    public void The_file_for_this_machine_is_chosen_by_platform_and_architecture()
    {
        var files = new List<ReleaseFile>
        {
            new("windows", "x64", "w.zip", 1, new string('a', 64)),
            new("macos", "arm64", "m.dmg", 1, new string('b', 64)),
            new("linux", "x64", "l.tar.gz", 1, new string('c', 64)),
        };
        var manifest = new SignedReleaseManifest("stable", "0.2.1", files);

        ReleaseFile? mine = manifest.FileForThisMachine();
        string expected = ReleaseFile.ThisPlatform switch
        {
            "windows" when ReleaseFile.ThisArch == "x64" => "w.zip",
            "macos" when ReleaseFile.ThisArch == "arm64" => "m.dmg",
            "linux" when ReleaseFile.ThisArch == "x64" => "l.tar.gz",
            _ => string.Empty,
        };
        (mine?.Name ?? string.Empty).ShouldBe(expected);
    }

    [Theory]
    [InlineData("deskpair_0.4.3_amd64.deb", true)]
    [InlineData("deskpair-0.4.3-1.x86_64.rpm", true)]
    [InlineData("deskpair-0.4.3-1-aarch64.pkg.tar.zst", true)]
    [InlineData("DESKPAIR_0.4.3_ARMHF.DEB", true)]
    [InlineData("DeskPair-0.4.3-linux-x64.tar.gz", false)]
    [InlineData("DeskPair-0.4.3-win-arm64.zip", false)]
    [InlineData("DeskPair-0.4.3-x86_64.dmg", false)]
    [InlineData("notes.tar.zst", false)]
    public void Deb_rpm_and_Arch_packages_are_the_system_package_managers_to_install(string name, bool package)
    {
        new ReleaseFile("linux", "x64", name, 1, new string('a', 64)).IsSystemPackage.ShouldBe(package);
    }

    [Fact]
    public void The_app_installs_the_archive_even_when_a_package_for_this_machine_is_listed_before_it()
    {
        string platform = ReleaseFile.ThisPlatform;
        string arch = ReleaseFile.ThisArch;
        var packagesFirst = new SignedReleaseManifest("stable", "0.4.3",
        [
            new(platform, arch, "deskpair_0.4.3_amd64.deb", 1, new string('a', 64)),
            new(platform, arch, "deskpair-0.4.3-1.x86_64.rpm", 1, new string('b', 64)),
            new(platform, arch, "deskpair-0.4.3-1-x86_64.pkg.tar.zst", 1, new string('c', 64)),
            new(platform, arch, "DeskPair-0.4.3-archive.tar.gz", 1, new string('d', 64)),
        ]);
        packagesFirst.FileForThisMachine().ShouldNotBeNull().Name.ShouldBe("DeskPair-0.4.3-archive.tar.gz");

        // A release with nothing but packages for this machine has nothing the app can install: it says so and
        // sends the reader to the download page.
        var packagesOnly = new SignedReleaseManifest("stable", "0.4.3", packagesFirst.Files.Take(3).ToList());
        packagesOnly.FileForThisMachine().ShouldBeNull();
    }

    [Fact]
    public void The_official_portal_uses_the_built_in_key_and_a_self_hosted_one_needs_its_own()
    {
        ReleaseSigning.KeyFor(null, null).ShouldNotBeNull();
        ReleaseSigning.KeyFor("https://deskpair.app", "").ShouldBe(Convert.FromBase64String(ReleaseSigning.OfficialPublicKeyBase64));

        // A private portal with nothing configured gets news, not installs; with a key, installs.
        ReleaseSigning.KeyFor("https://portal.example", null).ShouldBeNull();
        ReleaseSigning.KeyFor("https://portal.example", "!!not base64").ShouldBeNull();
        using IdentityKey own = IdentityKey.Create();
        ReleaseSigning.KeyFor("https://portal.example", Convert.ToBase64String(own.PublicKeySpki)).ShouldBe(own.PublicKeySpki);
    }
}
