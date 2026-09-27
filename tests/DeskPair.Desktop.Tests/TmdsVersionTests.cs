using System.Reflection;

namespace DeskPair.Desktop.Tests;

/// <summary>
/// The app gets one copy of Tmds.DBus.Protocol, and Avalonia uses it too. When the portal code asked for a newer one,
/// the newer one replaced Avalonia's, and the Linux app died at start-up in X11DBusImeHelper on a type the newer
/// version had renamed -- while the headless engine, which never loads Avalonia, went on working. The two have to
/// agree on the version, and this is where that is checked rather than on somebody's desktop.
/// </summary>
public class TmdsVersionTests
{
    [Fact]
    public void The_portal_uses_the_dbus_library_avalonia_was_built_against()
    {
        Version? avalonia = Referenced(Assembly.Load(new AssemblyName("Avalonia.FreeDesktop")));
        Version? portal = Referenced(typeof(DeskPair.Platform.Linux.Wayland.PortalSession).Assembly);

        avalonia.ShouldNotBeNull("Avalonia.FreeDesktop still talks D-Bus through Tmds.DBus.Protocol");
        portal.ShouldBe(avalonia, "move Tmds.DBus.Protocol only together with Avalonia (Directory.Packages.props)");
    }

    private static Version? Referenced(Assembly assembly) =>
        assembly.GetReferencedAssemblies().SingleOrDefault(a => a.Name == "Tmds.DBus.Protocol")?.Version;
}
