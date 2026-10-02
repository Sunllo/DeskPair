using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using DeskPair.Desktop.Localization;
using DeskPair.Desktop.Services;
using DeskPair.Desktop.ViewModels;
using DeskPair.Protocol.Messages;

namespace DeskPair.Desktop.Tests;

/// <summary>
/// When the remote computer is on a desktop DeskPair cannot read -- a UAC prompt on the secure desktop, or the lock
/// screen -- the viewer says so over the frozen picture, in the viewer's language, and clears it when it is over.
/// </summary>
[Collection("ProcessState")] // reads the string table, which the language tests move
public class SecureDesktopBannerTests
{
    [AvaloniaFact]
    public void A_uac_prompt_raises_the_banner_a_lock_changes_it_and_an_ordinary_desktop_clears_it()
    {
        var vm = new RemoteSessionViewModel("123456789", "me", new DesktopConfig(), NullLoggerFactory.Instance);
        vm.HasSecureDesktopNotice.ShouldBeFalse("nothing said yet");

        vm.OnSecureDesktop(new SecureDesktop { Kind = SecureDesktop.Types.Kind.SdUac });
        Dispatcher.UIThread.RunJobs();
        vm.HasSecureDesktopNotice.ShouldBeTrue();
        vm.SecureDesktopNotice.ShouldBe(Strings.Get("session.secureDesktop.uac"));

        vm.OnSecureDesktop(new SecureDesktop { Kind = SecureDesktop.Types.Kind.SdLocked });
        Dispatcher.UIThread.RunJobs();
        vm.SecureDesktopNotice.ShouldBe(Strings.Get("session.secureDesktop.locked"));

        vm.OnSecureDesktop(new SecureDesktop { Kind = SecureDesktop.Types.Kind.SdNone });
        Dispatcher.UIThread.RunJobs();
        vm.HasSecureDesktopNotice.ShouldBeFalse("back on an ordinary desktop: nothing to say");
    }
}
