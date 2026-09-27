using System.Net;
using System.Security.Cryptography;
using DeskPair.Desktop.Services.Update;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeskPair.Desktop.Tests;

/// <summary>
/// The parts of the installer that decide whether a file is installed at all. The platform steps that
/// follow -- unzip, hdiutil, the finishing script -- run on real machines and are verified there.
/// </summary>
public class UpdateInstallerTests
{
    private sealed class Stub(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }

    private static string TempFile() => Path.Combine(Path.GetTempPath(), "deskpair-installer-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task A_download_whose_hash_is_the_manifests_is_kept_under_its_final_name()
    {
        byte[] body = new byte[200_000];
        Random.Shared.NextBytes(body);
        string hash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        string destination = TempFile();
        var installer = new UpdateInstaller(new HttpClient(new Stub(body)), NullLogger.Instance);
        var seen = new List<UpdateProgress>();

        try
        {
            await installer.DownloadAsync(new Uri("https://portal.test/downloads/x.zip"), hash, body.Length, destination, new Progress<UpdateProgress>(seen.Add), CancellationToken.None);

            File.ReadAllBytes(destination).ShouldBe(body);
            File.Exists(destination + ".part").ShouldBeFalse();
        }
        finally
        {
            File.Delete(destination);
        }
    }

    [Fact]
    public async Task A_download_whose_hash_is_not_the_manifests_is_deleted_and_refused()
    {
        // The portal, or whoever answered for it, sent something else. The bytes never get the name the
        // manifest gave them, so nothing later can mistake them for the release.
        byte[] body = "not the release"u8.ToArray();
        string destination = TempFile();
        var installer = new UpdateInstaller(new HttpClient(new Stub(body)), NullLogger.Instance);

        UpdateException refused = await Should.ThrowAsync<UpdateException>(() =>
            installer.DownloadAsync(new Uri("https://portal.test/downloads/x.zip"), new string('a', 64), body.Length, destination, null, CancellationToken.None));

        refused.Message.ShouldContain("SHA-256");
        File.Exists(destination).ShouldBeFalse();
        File.Exists(destination + ".part").ShouldBeFalse();
    }

    [Fact]
    public void The_programs_folder_is_found_inside_the_archives_top_level_folder()
    {
        string root = TempFile();
        Directory.CreateDirectory(Path.Combine(root, "DeskPair-0.2.1-win-x64"));
        File.WriteAllText(Path.Combine(root, "DeskPair-0.2.1-win-x64", "DeskPair.exe"), "x");
        try
        {
            UpdateInstaller.LocateStagedRoot(root, "DeskPair.exe").ShouldBe(Path.Combine(root, "DeskPair-0.2.1-win-x64"));
            Should.Throw<UpdateException>(() => UpdateInstaller.LocateStagedRoot(root, "Other.exe"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_bundle_is_recognised_only_from_inside_its_MacOS_folder()
    {
        string sep = Path.DirectorySeparatorChar.ToString();
        string inside = string.Join(sep, ["", "Applications", "DeskPair.app", "Contents", "MacOS", "DeskPair"]);
        UpdateInstaller.BundleOf(Path.GetFullPath(inside))!.ShouldEndWith("DeskPair.app");
        UpdateInstaller.BundleOf(Path.GetFullPath(string.Join(sep, ["", "usr", "local", "bin", "DeskPair"]))).ShouldBeNull();
        UpdateInstaller.BundleOf(string.Empty).ShouldBeNull();
    }
}
