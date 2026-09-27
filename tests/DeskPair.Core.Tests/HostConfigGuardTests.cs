using DeskPair.Core.Config;

namespace DeskPair.Core.Tests;

/// <summary>Covers <see cref="HostConfig.BuildFileTransferGuard"/>, the file-transfer allow-list.</summary>
public sealed class HostConfigGuardTests
{
    private static readonly string Base = Path.Combine(Path.GetTempPath(), "fd-guard");
    private static readonly string Root = Path.Combine(Base, "allowed");

    [Fact]
    public void No_roots_means_unrestricted()
    {
        new HostConfig().BuildFileTransferGuard().ShouldBeNull();
        new HostConfig { FileTransferRoots = ["  ", ""] }.BuildFileTransferGuard().ShouldBeNull();
    }

    [Fact]
    public void A_root_allows_itself_and_its_descendants()
    {
        Func<string, bool> guard = new HostConfig { FileTransferRoots = [Root] }.BuildFileTransferGuard()!;

        guard(Root).ShouldBeTrue();
        guard(Path.Combine(Root, "file.bin")).ShouldBeTrue();
        guard(Path.Combine(Root, "sub", "deep", "x")).ShouldBeTrue();
    }

    [Fact]
    public void A_root_refuses_everything_outside_it()
    {
        Func<string, bool> guard = new HostConfig { FileTransferRoots = [Root] }.BuildFileTransferGuard()!;

        guard(Path.Combine(Base, "other", "y")).ShouldBeFalse();
        guard(Base).ShouldBeFalse();                          // the parent is not inside the root
        guard(Root + "X").ShouldBeFalse();                    // a sibling that shares the prefix is not inside
        guard(Path.Combine(Root, "..", "escape")).ShouldBeFalse(); // traversal resolves out of the root
    }

    [Fact]
    public void Multiple_roots_are_each_honoured()
    {
        string second = Path.Combine(Base, "also");
        Func<string, bool> guard = new HostConfig { FileTransferRoots = [Root, second] }.BuildFileTransferGuard()!;

        guard(Path.Combine(Root, "a")).ShouldBeTrue();
        guard(Path.Combine(second, "b")).ShouldBeTrue();
        guard(Path.Combine(Base, "neither", "c")).ShouldBeFalse();
    }

    [Fact]
    public void Roots_survive_a_json_round_trip()
    {
        var config = new HostConfig { FileTransferRoots = [Root] };
        HostConfig back = HostConfig.FromJson(config.ToJson());

        back.FileTransferRoots.ShouldBe([Root]);
        back.BuildFileTransferGuard()!(Path.Combine(Root, "z")).ShouldBeTrue();
    }
}
