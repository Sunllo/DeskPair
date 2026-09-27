namespace DeskPair.Core.Tests;

/// <summary>
/// Every source file is text. A raw control character in a literal -- a NUL typed where <c>'\0'</c> was meant --
/// compiles, but it makes the whole file binary to git, grep and every diff viewer. Five files had one: none of
/// their changes had shown as a diff since, and a search for their code found nothing.
/// </summary>
public class SourceTextTests
{
    private static readonly HashSet<string> Extensions =
        [".cs", ".axaml", ".cshtml", ".proto", ".kt", ".kts", ".swift", ".c", ".h", ".m", ".mm"];

    private static readonly HashSet<string> BuildOutput = ["bin", "obj", "build", ".gradle", ".idea", "DerivedData", "node_modules", ".build", "Pods"];

    [Fact]
    public void No_source_file_holds_a_raw_control_character()
    {
        string root = RepoRoot();
        var offenders = new List<string>();
        int scanned = 0;
        foreach (string top in new[] { "src", "tests", "tools", "protos", "native", "mobile" })
        {
            string dir = Path.Combine(root, top);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(root, file);
                if (!Extensions.Contains(Path.GetExtension(file)) || relative.Split(Path.DirectorySeparatorChar).Any(BuildOutput.Contains))
                {
                    continue;
                }

                scanned++;
                byte[] bytes = File.ReadAllBytes(file);
                int at = Array.FindIndex(bytes, b => b < 0x20 && b is not (byte)'\t' and not (byte)'\n' and not (byte)'\r');
                if (at >= 0)
                {
                    offenders.Add($"{relative} at byte {at} (0x{bytes[at]:x2})");
                }
            }
        }

        scanned.ShouldBeGreaterThan(100, "the sources were found");
        offenders.ShouldBeEmpty("write the escape (\\0, \\t, \\x1b) instead of the character itself");
    }

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DeskPair.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("DeskPair.slnx not found above " + AppContext.BaseDirectory);
    }
}
