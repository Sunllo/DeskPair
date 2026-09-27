using DeskPair.Core.Config;
using DeskPair.Platform.Abstractions.Security;
using DeskPair.Platform.Linux.Wayland;
using Microsoft.Extensions.Logging.Abstractions;
using Tmds.DBus.Protocol;

namespace DeskPair.Platform.Linux.Tests;

/// <summary>
/// The parts of the Wayland portal conversation that need no bus: where answers appear, what to ask for, what
/// <c>Start</c> said, and where the permission is remembered. The conversation itself was run against GNOME 46 on
/// the lab machine (docs/architecture.md, section 8, Linux Wayland).
/// </summary>
public class PortalTests
{
    [Theory]
    [InlineData(":1.234", "1_234")]
    [InlineData(":1.5", "1_5")]
    [InlineData(":12.3456", "12_3456")]
    public void The_sender_token_is_the_unique_name_without_its_colon_and_dots(string uniqueName, string expected) =>
        Portal.SenderToken(uniqueName).ShouldBe(expected);

    [Fact]
    public void A_request_is_answered_where_the_convention_says_so_it_can_be_watched_before_the_call()
    {
        Portal.RequestPath(":1.234", "deskpair3").ShouldBe("/org/freedesktop/portal/desktop/request/1_234/deskpair3");
        Portal.SessionPath(":1.234", "deskpair1").ShouldBe("/org/freedesktop/portal/desktop/session/1_234/deskpair1");
    }

    [Theory]
    [InlineData(7u, Portal.CursorMetadata)]
    [InlineData(5u, Portal.CursorMetadata)]
    [InlineData(3u, Portal.CursorEmbedded)]
    [InlineData(1u, Portal.CursorHidden)]
    [InlineData(0u, Portal.CursorHidden)]
    public void The_cursor_travels_beside_the_picture_when_the_portal_can_send_it_so(uint available, uint expected) =>
        Portal.ChooseCursorMode(available).ShouldBe(expected);

    [Fact]
    public void Start_is_read_as_GNOME_46_answered_it()
    {
        // What the Linux test machine (Ubuntu 24.04, xdg-desktop-portal 1.18.4, xdg-desktop-portal-gnome 46.2) answered on 2026-09-26.
        var results = new Dictionary<string, VariantValue>
        {
            ["devices"] = 3u,
            ["clipboard_enabled"] = false,
            ["restore_token"] = "76732b42-9d0e-4c43-9ad4-3c3b0c1f0a11",
            ["streams"] = Streams(
                (55u, Properties(
                    ("id", "0"),
                    ("position", VariantValue.Struct(0, 0)),
                    ("size", VariantValue.Struct(1280, 800)),
                    ("source_type", 1u),
                    ("mapping_id", "4e88587a-4db5-4c86-b229-0027373742f0")))),
        };

        PortalStart start = Portal.ParseStart(results);

        start.Devices.ShouldBe(Portal.DeviceKeyboard | Portal.DevicePointer);
        start.ClipboardEnabled.ShouldBeFalse();
        start.RestoreToken.ShouldBe("76732b42-9d0e-4c43-9ad4-3c3b0c1f0a11");
        start.Streams.ShouldBe([new PortalStream(55, "0", 0, 0, 1280, 800, true, Portal.SourceMonitor, "4e88587a-4db5-4c86-b229-0027373742f0")]);
    }

    [Fact]
    public void Two_monitors_keep_the_portals_order_and_their_positions()
    {
        var results = new Dictionary<string, VariantValue>
        {
            ["streams"] = Streams(
                (61u, Properties(("position", VariantValue.Struct(0, 0)), ("size", VariantValue.Struct(2560, 1440)), ("source_type", 1u))),
                (62u, Properties(("position", VariantValue.Struct(2560, 180)), ("size", VariantValue.Struct(1920, 1080)), ("source_type", 1u)))),
        };

        IReadOnlyList<PortalStream> streams = Portal.ParseStart(results).Streams;

        streams.Select(s => (s.NodeId, s.X, s.Y, s.Width, s.Height)).ShouldBe([(61u, 0, 0, 2560, 1440), (62u, 2560, 180, 1920, 1080)]);
    }

    [Fact]
    public void What_an_older_or_terser_portal_leaves_out_comes_back_empty_rather_than_invented()
    {
        // No id (before ScreenCast 4), no mapping id (before 5), no position (a window), no token, no devices.
        var results = new Dictionary<string, VariantValue>
        {
            ["streams"] = Streams((70u, Properties(("size", VariantValue.Struct(800, 600)), ("source_type", 2u), ("a_key_from_the_future", "ignored")))),
        };

        PortalStart start = Portal.ParseStart(results);

        start.Devices.ShouldBe(0u);
        start.RestoreToken.ShouldBeNull();
        start.Streams.ShouldBe([new PortalStream(70, null, 0, 0, 800, 600, false, 2, null)]);
    }

    [Fact]
    public void An_empty_restore_token_is_no_token()
    {
        Portal.ParseStart(new Dictionary<string, VariantValue> { ["restore_token"] = string.Empty }).RestoreToken.ShouldBeNull();
    }

    [Fact]
    public void A_value_still_wrapped_in_a_variant_is_unwrapped()
    {
        var results = new Dictionary<string, VariantValue>
        {
            ["devices"] = VariantValue.Variant(VariantValue.Variant(2u)),
            ["restore_token"] = VariantValue.Variant("t"),
        };

        PortalStart start = Portal.ParseStart(results);

        start.Devices.ShouldBe(Portal.DevicePointer);
        start.RestoreToken.ShouldBe("t");
    }

    [Fact]
    public void Each_account_has_its_own_token_under_a_key_the_file_store_accepts()
    {
        using var directory = new TempDirectory();
        var store = new FileSecretStore(directory.Path);

        PortalTokens.KeyFor(1000).ShouldNotBe(PortalTokens.KeyFor(0));
        Should.NotThrow(async () => await store.SetAsync(PortalTokens.KeyFor(1000), new byte[] { 1 }));
    }

    [Fact]
    public async Task A_token_is_kept_for_next_time_and_forgotten_when_the_portal_gives_none()
    {
        using var directory = new TempDirectory();
        var store = new FileSecretStore(directory.Path);
        var mine = new PortalTokens(store, 1000, NullLogger.Instance);
        var root = new PortalTokens(store, 0, NullLogger.Instance);

        (await mine.LoadAsync()).ShouldBeNull();
        await mine.SaveAsync("first");
        await root.SaveAsync("root's");
        (await mine.LoadAsync()).ShouldBe("first");

        await mine.SaveAsync("second");
        (await mine.LoadAsync()).ShouldBe("second");

        await mine.SaveAsync(null);
        (await mine.LoadAsync()).ShouldBeNull();
        (await root.LoadAsync()).ShouldBe("root's");
    }

    [Fact]
    public async Task A_token_that_cannot_be_read_is_never_written_over()
    {
        var store = new UnreadableStore();
        var tokens = new PortalTokens(store, 1000, NullLogger.Instance);

        (await tokens.LoadAsync()).ShouldBeNull();
        await tokens.SaveAsync("new");
        await tokens.SaveAsync(null);

        store.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task The_devices_are_remembered_beside_the_token_and_forgotten_with_it()
    {
        using var directory = new TempDirectory();
        var tokens = new PortalTokens(new FileSecretStore(directory.Path), 1000, NullLogger.Instance);

        (await tokens.LoadDevicesAsync()).ShouldBeNull();
        await tokens.SaveAsync("token", Portal.DevicePointer);
        (await tokens.LoadDevicesAsync()).ShouldBe(Portal.DevicePointer);

        await tokens.SaveAsync(null);
        (await tokens.LoadDevicesAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task The_settings_page_can_tell_allowed_from_watch_only_from_not_yet()
    {
        using var directory = new TempDirectory();
        var store = new FileSecretStore(directory.Path);
        var tokens = new PortalTokens(store, 1000, NullLogger.Instance);

        (await PortalPermission.StateAsync(store, 1000, NullLogger.Instance)).ShouldBe(PortalPermissionState.NotYet);

        await tokens.SaveAsync("token", Portal.DevicePointer);
        (await PortalPermission.StateAsync(store, 1000, NullLogger.Instance)).ShouldBe(PortalPermissionState.WatchOnly, "no keyboard: the person left interaction off");

        await tokens.SaveAsync("token", Portal.DeviceKeyboard | Portal.DevicePointer);
        (await PortalPermission.StateAsync(store, 1000, NullLogger.Instance)).ShouldBe(PortalPermissionState.Allowed);
    }

    [Theory]
    [InlineData(PortalFailure.Refused, PortalAskOutcome.Declined)]
    [InlineData(PortalFailure.TimedOut, PortalAskOutcome.Unanswered)]
    [InlineData(PortalFailure.Unavailable, PortalAskOutcome.Failed)]
    [InlineData(PortalFailure.Failed, PortalAskOutcome.Failed)]
    public void Asking_from_the_settings_ends_in_one_of_the_ways_the_page_can_say(PortalFailure failure, PortalAskOutcome expected)
    {
        (PortalAskOutcome outcome, string? detail) = PortalPermission.OutcomeOf(new PortalException(failure, "the portal's words"));

        outcome.ShouldBe(expected);
        (detail is null).ShouldBe(expected != PortalAskOutcome.Failed, "only an unforeseen ending carries the portal's words");
    }

    private static VariantValue Streams(params (uint Node, Dict<string, VariantValue> Properties)[] streams)
    {
        var array = new Array<Struct<uint, Dict<string, VariantValue>>>();
        foreach ((uint node, Dict<string, VariantValue> properties) in streams)
        {
            array.Add(new Struct<uint, Dict<string, VariantValue>>(node, properties));
        }

        return array.AsVariantValue();
    }

    private static Dict<string, VariantValue> Properties(params (string Key, VariantValue Value)[] entries)
    {
        var dict = new Dict<string, VariantValue>();
        foreach ((string key, VariantValue value) in entries)
        {
            dict.Add(key, value);
        }

        return dict;
    }

    private sealed class UnreadableStore : ISecretStore
    {
        public int Writes { get; private set; }

        public ValueTask<byte[]?> GetAsync(string key, CancellationToken ct = default) => throw new SecretUnreadableException(key, "owned by another account");

        public ValueTask SetAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct = default)
        {
            Writes++;
            return ValueTask.CompletedTask;
        }

        public ValueTask RemoveAsync(string key, CancellationToken ct = default)
        {
            Writes++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("dp-portal-").FullName;

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
