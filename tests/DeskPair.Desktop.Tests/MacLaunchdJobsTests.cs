using System.Xml.Linq;
using DeskPair.Desktop.Engine.MacService;
using DeskPair.Desktop.Services;

namespace DeskPair.Desktop.Tests;

/// <summary>
/// The launchd job that puts an engine in front of the login window.
///
/// Everything that makes it work is one key or one argument, and each of them was learned the expensive
/// way -- by logging a Mac out and watching what happened. They are pinned here because none of them will
/// look important to whoever reads the plist next.
/// </summary>
public class MacLaunchdJobsTests
{
    private const string Exe = "/Applications/DeskPair.app/Contents/MacOS/DeskPair";

    private static XElement Job() => Root(MacLaunchdJobs.Agent(Exe, UnattendedInstall.MacDataDirectory));

    /// <summary>The plist has to parse, so the test reads it the way launchd does rather than by string.</summary>
    private static XElement Root(string plist) => XDocument.Parse(plist).Root!.Element("dict")!;

    private static XElement? Value(XElement dict, string key)
    {
        List<XElement> children = dict.Elements().ToList();
        for (int i = 0; i < children.Count - 1; i++)
        {
            if (children[i].Name == "key" && children[i].Value == key)
            {
                return children[i + 1];
            }
        }

        return null;
    }

    private static List<string> Strings(XElement? array) => array?.Elements("string").Select(e => e.Value).ToList() ?? [];

    [Fact]
    public void It_is_a_plist_launchd_could_read()
    {
        Job().ShouldNotBeNull();
        Value(Job(), "Label")!.Value.ShouldBe(UnattendedInstall.MacAgentLabel);
    }

    /// <summary>
    /// The one key the whole feature rests on. Without it the job is an ordinary agent that only exists
    /// once somebody has signed in -- which is every case except the one this is for.
    /// </summary>
    [Fact]
    public void It_is_limited_to_the_login_window_and_the_desktop()
    {
        Strings(Value(Job(), "LimitLoadToSessionType")).ShouldBe(["LoginWindow", "Aqua"]);
    }

    /// <summary>
    /// The same job runs as root at the login window and as the signed-in user afterwards, and
    /// DefaultDataDir answers differently for those two. Left to the default, one job would be two
    /// machines: two identities, two ids at the rendezvous server, two of everything.
    /// </summary>
    [Fact]
    public void The_data_directory_is_always_spelled_out()
    {
        List<string> argv = Strings(Value(Job(), "ProgramArguments"));

        argv.ShouldContain("--data");
        argv[argv.IndexOf("--data") + 1].ShouldBe(UnattendedInstall.MacDataDirectory);
        argv[0].ShouldBe(Exe);
        argv.ShouldContain("--server");
    }

    /// <summary>
    /// The executable, not a shell that runs it. A wrapper makes sh the program launchd started and the
    /// program TCC is asked about, which throws away the screen-recording consent the app was granted --
    /// and that consent is the difference between a picture of the login screen and nothing at all.
    /// </summary>
    [Fact]
    public void Nothing_is_wrapped_in_a_shell()
    {
        List<string> argv = Strings(Value(Job(), "ProgramArguments"));

        argv[0].ShouldBe(Exe);
        argv.ShouldNotContain("/bin/sh");
        argv.ShouldNotContain("-c");
    }

    /// <summary>
    /// The engine writes its own log under the data directory. A second copy owned by root and readable by
    /// everyone is somewhere a peer id or a reset link sits where it was not meant to.
    /// </summary>
    [Fact]
    public void It_does_not_ask_launchd_for_a_second_log()
    {
        Value(Job(), "StandardOutPath").ShouldBeNull();
        Value(Job(), "StandardErrorPath").ShouldBeNull();
    }

    /// <summary>
    /// So the job appears under DeskPair in Login Items rather than as an unexplained background item.
    /// A remote-access job somebody cannot find and turn off is the wrong thing to ship.
    /// </summary>
    [Fact]
    public void It_says_which_app_it_belongs_to()
    {
        Strings(Value(Job(), "AssociatedBundleIdentifiers")).ShouldBe([UnattendedInstall.MacBundleIdentifier]);
    }

    /// <summary>
    /// Restarted when it fails, left alone when it finishes cleanly.
    ///
    /// The second half is the one that matters. The engine exits 0 on purpose when the machine-wide store
    /// is not its to read, which is what a second account's session looks like after a fast user switch.
    /// A plain KeepAlive would turn that into a restart every few seconds for ever, which is a loop
    /// Windows has already produced once for a different reason and which nobody found quickly.
    /// </summary>
    [Fact]
    public void A_clean_exit_is_not_restarted()
    {
        Value(Job(), "RunAtLoad")!.Name.LocalName.ShouldBe("true");

        XElement keepAlive = Value(Job(), "KeepAlive")!;
        keepAlive.Name.LocalName.ShouldBe("dict");
        Value(keepAlive, "SuccessfulExit")!.Name.LocalName.ShouldBe("false");
    }

    /// <summary>An app in a folder with an ampersand in it must not produce a plist launchd cannot parse.</summary>
    [Fact]
    public void A_path_with_xml_in_it_is_escaped_rather_than_pasted()
    {
        string odd = "/Users/a&b/Apps/Desk<Pair>.app/Contents/MacOS/DeskPair";

        List<string> argv = Strings(Value(Root(MacLaunchdJobs.Agent(odd, "/tmp/x")), "ProgramArguments"));

        argv[0].ShouldBe(odd);
    }
}
