namespace DeskPair.Desktop.Services;

/// <summary>
/// What a host password may be made of: printable ASCII -- letters, digits, the symbols on a US keyboard,
/// and the space.
///
/// A password is typed on the host in one place and on the viewer in another, and the two places have
/// different keyboards: a viewer with a Chinese input method open turns the same key sequence into
/// something the host never saw. Restricting both ends to ASCII is what makes a password portable. The
/// password fields apply this as the text changes and keep the input method off, so a character it slipped
/// in is gone before it can be sent; the phones do the same in the shared module's <c>PasswordText</c>.
/// </summary>
public static class PasswordText
{
    /// <summary>The value with everything outside printable ASCII removed; the same instance when nothing was.</summary>
    public static string Ascii(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        bool clean = true;
        foreach (char c in value)
        {
            if (c is < ' ' or > '~')
            {
                clean = false;
                break;
            }
        }

        if (clean)
        {
            return value;
        }

        var kept = new System.Text.StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (c is >= ' ' and <= '~')
            {
                kept.Append(c);
            }
        }

        return kept.ToString();
    }
}
