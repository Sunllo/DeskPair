using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using DeskPair.Platform.Linux.Native;

namespace DeskPair.Platform.Linux.Tests;

/// <summary>
/// The shim's structs are read by offset, so the offsets are the contract. These are computed here from
/// <c>native/linux/SunlloWaylandShim/shim.h</c> itself, with the C rules for x86-64 and arm64 Linux, and compared
/// with the C# mirror -- a test that restated the numbers would prove nothing. The shim also reports its sizes at
/// run time (<c>dp_pw_abi</c>), which catches a stale library; this catches the two declarations drifting apart.
/// </summary>
public partial class WaylandShimLayoutTests
{
    private static readonly Dictionary<string, (int Size, int Align)> CTypes = new()
    {
        ["const uint8_t *"] = (8, 8),
        ["int32_t"] = (4, 4),
        ["uint32_t"] = (4, 4),
        ["int64_t"] = (8, 8),
        ["uint64_t"] = (8, 8),
    };

    [Theory]
    [InlineData("dp_frame", typeof(WaylandShim.DpFrame))]
    [InlineData("dp_cursor", typeof(WaylandShim.DpCursor))]
    public void Every_field_sits_where_the_header_puts_it(string cName, Type mirror)
    {
        (List<(string Name, int Offset)> fields, int size) = Layout(cName);

        fields.Select(f => Pascal(f.Name)).ShouldBe(mirror.GetFields().Select(f => f.Name), $"{cName}'s fields, in order");
        foreach ((string name, int offset) in fields)
        {
            ((int)Marshal.OffsetOf(mirror, Pascal(name))).ShouldBe(offset, $"{cName}.{name}");
        }

        Marshal.SizeOf(mirror).ShouldBe(size, $"sizeof({cName})");
    }

    [Fact]
    public void The_constants_are_the_headers()
    {
        Dictionary<string, int> defines = Defines();

        defines["DP_PW_ABI"].ShouldBe(WaylandShim.Abi);
        defines["DP_FORMAT_BGRX"].ShouldBe(WaylandShim.FormatBgrx);
        defines["DP_FORMAT_RGBX"].ShouldBe(WaylandShim.FormatRgbx);
        defines["DP_FORMAT_BGRA"].ShouldBe(WaylandShim.FormatBgra);
        defines["DP_FORMAT_RGBA"].ShouldBe(WaylandShim.FormatRgba);
        defines["DP_STATE_CONNECTING"].ShouldBe(WaylandShim.StateConnecting);
        defines["DP_STATE_PAUSED"].ShouldBe(WaylandShim.StatePaused);
        defines["DP_STATE_STREAMING"].ShouldBe(WaylandShim.StateStreaming);
        defines["DP_STATE_ERROR"].ShouldBe(WaylandShim.StateError);
        defines["DP_STATE_CLOSED"].ShouldBe(WaylandShim.StateClosed);
        defines["DP_FRAME_BORROWABLE"].ShouldBe(WaylandShim.FrameBorrowable);
    }

    private static (List<(string Name, int Offset)> Fields, int Size) Layout(string cName)
    {
        Match body = Regex.Match(Header(), $@"typedef struct {cName} \{{(?<body>.*?)\}} {cName};", RegexOptions.Singleline);
        body.Success.ShouldBeTrue($"shim.h declares {cName}");

        var fields = new List<(string, int)>();
        int offset = 0;
        int widest = 1;
        foreach (Match field in FieldPattern().Matches(body.Groups["body"].Value))
        {
            string type = field.Groups["type"].Value.Trim();
            CTypes.TryGetValue(type, out (int Size, int Align) c).ShouldBeTrue($"a type this test knows: {type}");
            offset = (offset + c.Align - 1) / c.Align * c.Align;
            fields.Add((field.Groups["name"].Value, offset));
            offset += c.Size;
            widest = Math.Max(widest, c.Align);
        }

        return (fields, (offset + widest - 1) / widest * widest);
    }

    private static Dictionary<string, int> Defines() =>
        DefinePattern().Matches(Header()).ToDictionary(m => m.Groups["name"].Value, m => int.Parse(m.Groups["value"].Value, System.Globalization.CultureInfo.InvariantCulture));

    private static string Pascal(string snake) => string.Concat(snake.Split('_').Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

    private static string Header()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DeskPair.slnx")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull("the tests run inside the repository");
        return File.ReadAllText(Path.Combine(dir.FullName, "native", "linux", "SunlloWaylandShim", "shim.h"));
    }

    [GeneratedRegex(@"^\s*(?<type>const uint8_t \*|u?int(32|64)_t)\s*(?<name>\w+);", RegexOptions.Multiline)]
    private static partial Regex FieldPattern();

    [GeneratedRegex(@"^#define (?<name>DP_[A-Z_]+) (?<value>-?\d+)\s*$", RegexOptions.Multiline)]
    private static partial Regex DefinePattern();
}
