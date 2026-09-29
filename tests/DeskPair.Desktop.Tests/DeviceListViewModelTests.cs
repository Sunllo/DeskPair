using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using DeskPair.Core.Transport;
using DeskPair.Desktop.Services;
using DeskPair.Desktop.ViewModels;

namespace DeskPair.Desktop.Tests;

/// <summary>What dropping a device on a group does, which is the part of dragging that is not pointer work.</summary>
[Collection("ProcessState")] // touches process-wide state (Strings.Language, Toasts.Current), so never alongside another test that does
public class DeviceListViewModelTests : IDisposable
{
    /// <summary>The view model saves whenever something changes; it must never be the user's own file.</summary>
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sunllo-devices-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    private string BookPath => Path.Combine(_dir, "devices.json");

    private DeviceListViewModel Create(DeviceBook book) => new(
        book,
        presence: (_, _) => Task.FromResult(new Dictionary<string, PeerOnlineState>()),
        post: action => action(),
        path: BookPath);

    private static List<string> Targets(DeviceListViewModel vm) => [.. vm.Groups.SelectMany(g => g.Devices).Select(d => d.Target)];

    private static DeviceBook Book() => new DeviceBook()
        .WithGroup("Taipei")
        .WithGroup("Kaohsiung")
        .With(new SavedDevice { Target = "111111111", Alias = "Reception", Group = "Taipei" })
        .With(new SavedDevice { Target = "222222222", Alias = "Spare" });

    private static DeviceGroupViewModel Group(DeviceListViewModel vm, string name) =>
        vm.Groups.First(g => g.Name == name);

    [Fact]
    public void A_group_the_user_made_is_listed_even_while_it_is_empty()
    {
        DeviceListViewModel vm = Create(Book());

        vm.Groups.Select(g => g.Name).ShouldContain("Kaohsiung");
        Group(vm, "Kaohsiung").IsEmpty.ShouldBeTrue();
        Group(vm, "Kaohsiung").CanEdit.ShouldBeTrue();
    }

    [Fact]
    public void Ungrouped_holds_the_devices_with_no_group_and_cannot_be_edited()
    {
        DeviceListViewModel vm = Create(Book());

        DeviceGroupViewModel ungrouped = vm.Groups.First(g => !g.CanEdit);
        ungrouped.Devices.ShouldHaveSingleItem().Target.ShouldBe("222222222");
    }

    [Fact]
    public void Dropping_a_device_on_a_group_moves_it_there()
    {
        DeviceListViewModel vm = Create(Book());

        vm.MoveToGroup("222222222", Group(vm, "Kaohsiung"));

        Group(vm, "Kaohsiung").Devices.ShouldHaveSingleItem().Target.ShouldBe("222222222");
        vm.Groups.ShouldNotContain(g => !g.CanEdit); // nothing left ungrouped, so that section is gone
    }

    [Fact]
    public void Dropping_a_device_on_ungrouped_clears_its_group()
    {
        DeviceListViewModel vm = Create(Book());
        DeviceGroupViewModel ungrouped = vm.Groups.First(g => !g.CanEdit);

        vm.MoveToGroup("111111111", ungrouped);

        Group(vm, "Taipei").IsEmpty.ShouldBeTrue();
        vm.Groups.First(g => !g.CanEdit).Devices.Select(d => d.Target).ShouldContain("111111111");
    }

    [Fact]
    public void A_device_cannot_be_dropped_on_the_group_it_is_already_in()
    {
        DeviceListViewModel vm = Create(Book());

        vm.CanDropOn("111111111", Group(vm, "Taipei")).ShouldBeFalse();
        vm.CanDropOn("111111111", Group(vm, "Kaohsiung")).ShouldBeTrue();
        vm.CanDropOn("222222222", vm.Groups.First(g => !g.CanEdit)).ShouldBeFalse();
    }

    [Fact]
    public void Dropping_something_that_is_not_a_saved_device_does_nothing()
    {
        DeviceListViewModel vm = Create(Book());

        vm.CanDropOn("999999999", Group(vm, "Taipei")).ShouldBeFalse();
        vm.MoveToGroup("999999999", Group(vm, "Taipei"));
        vm.MoveToGroup("111111111", null);

        Group(vm, "Taipei").Devices.ShouldHaveSingleItem().Target.ShouldBe("111111111");
    }

    [Fact]
    public void Folding_a_group_hides_its_devices_without_losing_them()
    {
        DeviceListViewModel vm = Create(Book());
        DeviceGroupViewModel taipei = Group(vm, "Taipei");

        vm.ToggleGroupCommand.Execute(taipei);

        taipei.IsExpanded.ShouldBeFalse();
        taipei.Devices.ShouldHaveSingleItem(); // still there, the view just does not show them
        taipei.CountText.ShouldContain("1");
    }

    [Fact]
    public void A_group_comes_back_folded_the_way_it_was_left()
    {
        DeviceBook book = Book();
        DeviceListViewModel vm = Create(book);
        vm.ToggleGroupCommand.Execute(Group(vm, "Taipei"));

        // A change that rebuilds the list must not quietly open it again.
        vm.MoveToGroup("222222222", Group(vm, "Kaohsiung"));

        Group(vm, "Taipei").IsExpanded.ShouldBeFalse();
        Group(vm, "Kaohsiung").IsExpanded.ShouldBeTrue();
    }

    [Fact]
    public void Dropping_into_a_folded_group_opens_it()
    {
        DeviceListViewModel vm = Create(Book());
        vm.ToggleGroupCommand.Execute(Group(vm, "Kaohsiung"));
        Group(vm, "Kaohsiung").IsExpanded.ShouldBeFalse();

        vm.MoveToGroup("222222222", Group(vm, "Kaohsiung"));

        Group(vm, "Kaohsiung").IsExpanded.ShouldBeTrue();
        Group(vm, "Kaohsiung").Devices.ShouldHaveSingleItem();
    }

    [Fact]
    public void The_chevron_says_which_way_the_group_will_go()
    {
        DeviceListViewModel vm = Create(Book());
        DeviceGroupViewModel taipei = Group(vm, "Taipei");

        string open = taipei.ToggleGlyph;
        vm.ToggleGroupCommand.Execute(taipei);

        taipei.ToggleGlyph.ShouldNotBe(open);
        taipei.ToggleHint.ShouldBe(Localization.Strings.Get("devices.expand"));
    }

    [Fact]
    public void The_editor_titles_say_whether_something_is_being_added_or_changed()
    {
        DeviceListViewModel vm = Create(Book());

        vm.BeginAddCommand.Execute(null);
        vm.IsEditorOpen.ShouldBeTrue();
        vm.DeviceEditorTitle.ShouldBe(Localization.Strings.Get("devices.add"));

        vm.BeginEditCommand.Execute(Group(vm, "Taipei").Devices[0]);
        vm.DeviceEditorTitle.ShouldBe(Localization.Strings.Get("devices.editDevice"));

        vm.BeginAddGroupCommand.Execute(null);
        vm.IsGroupEditorOpen.ShouldBeTrue();
        vm.GroupEditorTitle.ShouldBe(Localization.Strings.Get("devices.addGroup"));

        vm.BeginRenameGroupCommand.Execute(Group(vm, "Taipei"));
        vm.GroupEditorTitle.ShouldBe(Localization.Strings.Get("devices.renameGroup"));
    }

    [Fact]
    public void An_editor_that_was_cancelled_closes()
    {
        DeviceListViewModel vm = Create(Book());

        vm.BeginAddCommand.Execute(null);
        vm.CancelEditCommand.Execute(null);
        vm.IsEditorOpen.ShouldBeFalse();

        vm.BeginAddGroupCommand.Execute(null);
        vm.CancelGroupEditCommand.Execute(null);
        vm.IsGroupEditorOpen.ShouldBeFalse();
    }

    [Fact]
    public void An_editor_stays_open_while_what_it_holds_is_not_usable()
    {
        DeviceListViewModel vm = Create(Book());

        vm.BeginAddCommand.Execute(null);
        vm.SaveDeviceCommand.Execute(null);
        vm.IsEditorOpen.ShouldBeTrue();
        vm.Notice.ShouldBe(Localization.Strings.Get("devices.targetRequired"));

        vm.BeginAddGroupCommand.Execute(null);
        vm.SaveGroupCommand.Execute(null);
        vm.IsGroupEditorOpen.ShouldBeTrue();
        vm.Notice.ShouldBe(Localization.Strings.Get("devices.groupRequired"));
    }

    /// <summary>
    /// A device another computer of the account added reaches this one's file by a sync, and has to reach the list on
    /// screen too. Nothing listened for that: the file had the device, the open list did not, until the page was left
    /// and opened again.
    /// </summary>
    [Fact]
    public void What_a_sync_brings_in_shows_on_an_open_list_and_a_hidden_list_reads_it_when_shown()
    {
        Book().Save(BookPath);
        DeviceListViewModel vm = Create(Book());
        vm.Activate();

        // What the sync does when another computer added a device: writes the file under the list.
        DeviceBook.Load(BookPath).With(new SavedDevice { Target = "333333333", Alias = "Added elsewhere" }).Save(BookPath);
        vm.ReloadAfterSync();
        Targets(vm).ShouldContain("333333333");

        vm.Deactivate();
        DeviceBook.Load(BookPath).With(new SavedDevice { Target = "444444444" }).Save(BookPath);
        vm.ReloadAfterSync();
        Targets(vm).ShouldNotContain("444444444", "a list not on screen is left alone");

        vm.Activate();
        Targets(vm).ShouldContain("444444444", "and reads the file when it is shown");
        vm.Deactivate();
    }

    /// <summary>
    /// Closed to the tray the window only hides, and a device list left selected went on asking who is online every
    /// fifteen seconds -- and, now that an open list also asks the account what changed, would have done that too.
    /// </summary>
    [Fact]
    public void A_hidden_window_stops_the_device_list_and_its_return_restarts_it()
    {
        Book().Save(BookPath);
        DeviceListViewModel devices = Create(Book());
        var host = new HostLink("ui", NullLogger.Instance);
        var window = new MainWindowViewModel(
            new HomeViewModel(host, BookPath),
            devices,
            new IncomingConnectionsViewModel(host),
            new ConnectionHistoryViewModel(host),
            new SettingsViewModel(host, post: action => action(), time: new FakeTimeProvider(DateTimeOffset.UnixEpoch)),
            new UpdateNoticeViewModel(() => string.Empty, _ => { }));

        window.SelectedSection = MainWindowViewModel.DevicesSection;
        devices.IsActive.ShouldBeTrue();

        window.WindowHidden();
        devices.IsActive.ShouldBeFalse();

        window.WindowShown();
        devices.IsActive.ShouldBeTrue();

        window.SelectedSection = MainWindowViewModel.HomeSection;
        window.WindowShown();
        devices.IsActive.ShouldBeFalse("another page is on screen");
    }
}
