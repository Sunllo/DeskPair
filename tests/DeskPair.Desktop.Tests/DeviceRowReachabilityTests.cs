using DeskPair.Core.Transport;
using DeskPair.Desktop.Services;
using DeskPair.Desktop.ViewModels;

namespace DeskPair.Desktop.Tests;

public class DeviceRowReachabilityTests
{
    [Fact]
    public void Only_an_offline_device_loses_its_connect_button()
    {
        // Unknown keeps it: "the server did not answer" is not "the machine is off", and a direct address on
        // a LAN with no server is unknown for ever.
        var row = new DeviceRowViewModel(new SavedDevice { Target = "123456789" });

        row.CanConnect.ShouldBeTrue();
        row.State = PeerOnlineState.Online;
        row.CanConnect.ShouldBeTrue();
        row.State = PeerOnlineState.Offline;
        row.CanConnect.ShouldBeFalse();
        row.State = PeerOnlineState.Unknown;
        row.CanConnect.ShouldBeTrue();
    }

    [Fact]
    public void The_button_is_told_when_the_state_moves()
    {
        var row = new DeviceRowViewModel(new SavedDevice { Target = "123456789" });
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        row.State = PeerOnlineState.Offline;

        changed.ShouldContain(nameof(DeviceRowViewModel.CanConnect));
    }
}
