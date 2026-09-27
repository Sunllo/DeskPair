using System.Text;
using DeskPair.Core.Terminal;

namespace DeskPair.Core.Tests;

/// <summary>The bytes each key sends. The three front ends share this table, so one test covers all of them.</summary>
public class TerminalKeysTests
{
    private static string Key(TerminalKey key, TerminalModifiers mods = TerminalModifiers.None, bool app = false) =>
        Encoding.ASCII.GetString(TerminalKeys.Key(key, mods, app));

    [Theory]
    [InlineData(TerminalKey.Up, false, "\u001b[A")]
    [InlineData(TerminalKey.Up, true, "\u001bOA")]
    [InlineData(TerminalKey.Left, true, "\u001bOD")]
    [InlineData(TerminalKey.Home, false, "\u001b[H")]
    [InlineData(TerminalKey.Delete, false, "\u001b[3~")]
    [InlineData(TerminalKey.PageDown, false, "\u001b[6~")]
    [InlineData(TerminalKey.F1, false, "\u001bOP")]
    [InlineData(TerminalKey.F5, false, "\u001b[15~")]
    [InlineData(TerminalKey.F12, false, "\u001b[24~")]
    [InlineData(TerminalKey.Enter, false, "\r")]
    [InlineData(TerminalKey.Backspace, false, "\u007f")]
    [InlineData(TerminalKey.Tab, false, "\t")]
    public void Special_keys(TerminalKey key, bool applicationCursor, string expected) =>
        Key(key, app: applicationCursor).ShouldBe(expected);

    /// <summary>With a modifier the arrows use the CSI 1;m form even in application mode; that is what readline binds word moves to.</summary>
    [Fact]
    public void Modifiers_use_the_xterm_parameter()
    {
        Key(TerminalKey.Right, TerminalModifiers.Control, app: true).ShouldBe("\u001b[1;5C");
        Key(TerminalKey.Up, TerminalModifiers.Shift).ShouldBe("\u001b[1;2A");
        Key(TerminalKey.Delete, TerminalModifiers.Control | TerminalModifiers.Alt).ShouldBe("\u001b[3;7~");
        Key(TerminalKey.Tab, TerminalModifiers.Shift).ShouldBe("\u001b[Z");
    }

    [Theory]
    [InlineData("c", TerminalModifiers.Control, new byte[] { 0x03 })]
    [InlineData("C", TerminalModifiers.Control, new byte[] { 0x03 })]
    [InlineData("d", TerminalModifiers.Control, new byte[] { 0x04 })]
    [InlineData("[", TerminalModifiers.Control, new byte[] { 0x1B })]
    [InlineData(" ", TerminalModifiers.Control, new byte[] { 0x00 })]
    [InlineData("b", TerminalModifiers.Alt, new byte[] { 0x1B, (byte)'b' })]
    [InlineData("x", TerminalModifiers.Control | TerminalModifiers.Alt, new byte[] { 0x1B, 0x18 })]
    [InlineData("é", TerminalModifiers.None, new byte[] { 0xC3, 0xA9 })]
    public void Text_with_modifiers(string text, TerminalModifiers mods, byte[] expected) =>
        TerminalKeys.Text(text, mods).ShouldBe(expected);

    [Fact]
    public void A_paste_uses_CR_and_is_framed_in_bracketed_mode()
    {
        Encoding.UTF8.GetString(TerminalKeys.Paste("a\r\nb\nc", bracketed: false)).ShouldBe("a\rb\rc");
        Encoding.UTF8.GetString(TerminalKeys.Paste("ls", bracketed: true)).ShouldBe("\u001b[200~ls\u001b[201~");
    }

    /// <summary>Pasted text that contains the end marker must not be able to end the frame and run the rest as typed.</summary>
    [Fact]
    public void A_paste_cannot_close_its_own_frame()
    {
        string pasted = Encoding.UTF8.GetString(TerminalKeys.Paste("echo safe\u001b[201~\nrm -rf ~\n", bracketed: true));

        pasted.ShouldStartWith("\u001b[200~");
        pasted.ShouldEndWith("\u001b[201~");
        pasted[6..^6].ShouldNotContain("\u001b[201~");
    }

    [Theory]
    [InlineData("ls", false)]
    [InlineData("ls\n", false)]
    [InlineData("ls\nrm x", true)]
    [InlineData("a\r\nb", true)]
    public void Multi_line_pastes_are_recognised(string text, bool expected) =>
        TerminalKeys.IsMultiLine(text).ShouldBe(expected);
}
