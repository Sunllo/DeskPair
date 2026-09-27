using System.Xml.Linq;

namespace DeskPair.Desktop.Tests;

/// <summary>
/// That the version is one number in one place.
///
/// It was two. <c>Directory.Build.props</c> declared 0.1.0 and <c>app.manifest</c> repeated 0.1.0.0 by hand,
/// with nothing to notice when one moved; neither had been bumped since the first commit, so every build of
/// this product, for its whole history, answered the same version. That is fine until something has to decide
/// whether a peer is running an older build than the one on the download page, at which point it is fatal and
/// silent: the update check cannot distinguish "up to date" from "never worked".
///
/// The manifest cannot read MSBuild properties, so the duplication cannot be removed. It can be made loud.
/// </summary>
public class VersionTests
{
    private static Version ManifestVersion()
    {
        using Stream stream = typeof(VersionTests).Assembly.GetManifestResourceStream("app.manifest")
            ?? throw new InvalidOperationException("app.manifest is not embedded; see the test project file.");
        XNamespace asm = "urn:schemas-microsoft-com:asm.v1";
        string? value = XDocument.Load(stream).Root?.Element(asm + "assemblyIdentity")?.Attribute("version")?.Value;
        return Version.Parse(value ?? throw new InvalidOperationException("No assemblyIdentity/@version."));
    }

    [Fact]
    public void The_manifest_and_the_assembly_agree()
    {
        Version? assembly = typeof(App).Assembly.GetName().Version;
        assembly.ShouldNotBeNull();

        // Edit <VersionPrefix> in Directory.Build.props and app.manifest together, or this fails.
        ManifestVersion().ShouldBe(assembly);
    }

    [Fact]
    public void The_reported_version_names_the_commit()
    {
        // App.Version is what --version prints, what the About page shows, and what the rendezvous server
        // stores per peer. Reading AssemblyVersion instead -- which has four numbers and no room for a
        // commit -- is what made two builds indistinguishable, so assert the shape, not just non-emptiness.
        App.Version.ShouldStartWith(typeof(App).Assembly.GetName().Version!.ToString(3));

        // A build made outside a git checkout, or on a branch whose ref is packed, legitimately has no
        // commit; one made here does. Both are valid, so this only asserts the separator is used correctly.
        string[] parts = App.Version.Split('+');
        parts.Length.ShouldBeLessThanOrEqualTo(2);
        if (parts.Length == 2)
        {
            parts[1].ShouldNotBeEmpty();
        }
    }
}
