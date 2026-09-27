using Microsoft.Extensions.Logging.Abstractions;
using DeskPair.Desktop.Localization;
using DeskPair.Desktop.Services;
using DeskPair.Desktop.ViewModels;

namespace DeskPair.Desktop.Tests;

/// <summary>Saving a recent connection into the device list, from the home screen.</summary>
[Collection("ProcessState")] // touches process-wide state (Strings.Language, Toasts.Current), so never alongside another test that does
public class HomeViewModelTests : IDisposable
{
    /// <summary>Never the user's own device list, which is what these tests would otherwise overwrite.</summary>
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sunllo-home-" + Guid.NewGuid().ToString("N"));

    private HomeViewModel Create() => new(new HostLink("ui", NullLogger.Instance), Path.Combine(_dir, "devices.json"));

    private DeviceBook Saved() => DeviceBook.Load(Path.Combine(_dir, "devices.json"));

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

    [Fact]
    public void A_recent_connection_can_be_saved_to_the_device_list()
    {
        HomeViewModel vm = Create();

        vm.AddToDevicesCommand.Execute(new RecentPeer("123456789", "Reception", DateTimeOffset.UnixEpoch));

        SavedDevice device = Saved().Devices.ShouldHaveSingleItem();
        device.Target.ShouldBe("123456789");
        device.Alias.ShouldBe("Reception");
        vm.Notice.ShouldBe(Strings.Get("home.addedToDevices"));
    }

    [Fact]
    public void Saving_the_same_one_twice_says_so_rather_than_adding_it_again()
    {
        HomeViewModel vm = Create();
        var peer = new RecentPeer("123456789", "Reception", DateTimeOffset.UnixEpoch);
        vm.AddToDevicesCommand.Execute(peer);

        vm.AddToDevicesCommand.Execute(peer);

        Saved().Devices.ShouldHaveSingleItem();
        vm.Notice.ShouldBe(Strings.Get("home.alreadyInDevices"));
    }

    [Fact]
    public void Saving_a_device_already_in_the_list_under_a_different_name_leaves_it_alone()
    {
        string path = Path.Combine(_dir, "devices.json");
        new DeviceBook().With(new SavedDevice { Target = "123456789", Alias = "Front desk", Group = "Taipei" }).Save(path);
        HomeViewModel vm = Create();

        vm.AddToDevicesCommand.Execute(new RecentPeer("123456789", "Reception", DateTimeOffset.UnixEpoch));

        SavedDevice device = Saved().Devices.ShouldHaveSingleItem();
        device.Alias.ShouldBe("Front desk");
        device.Group.ShouldBe("Taipei");
    }

    [Fact]
    public void Nothing_to_save_does_nothing()
    {
        HomeViewModel vm = Create();

        vm.AddToDevicesCommand.Execute(null);

        Saved().Devices.ShouldBeEmpty();
    }
}
