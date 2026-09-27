using DeskPair.Desktop.Services;

namespace DeskPair.Desktop.Tests;

public class PasswordTextTests
{
    [Fact]
    public void Letters_digits_symbols_and_the_space_pass_through_as_the_same_string()
    {
        const string value = "Aa1!@# $%^&*()_+-=[]{}|;':\",./<>?`~";

        PasswordText.Ascii(value).ShouldBeSameAs(value);
    }

    [Fact]
    public void Anything_an_input_method_could_slip_in_is_dropped()
    {
        // The case that started this: a viewer with a Chinese input method open, typing a password the
        // host only ever saw in ASCII.
        PasswordText.Ascii("abc密碼123").ShouldBe("abc123");
        PasswordText.Ascii("ａｂｃ").ShouldBe(string.Empty);
        PasswordText.Ascii("xé’​\t").ShouldBe("x");
        PasswordText.Ascii(null).ShouldBe(string.Empty);
    }
}
