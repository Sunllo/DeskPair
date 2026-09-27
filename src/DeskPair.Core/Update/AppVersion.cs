using System.Globalization;

namespace DeskPair.Core.Update;

/// <summary>
/// A release number that can be compared: three numbers and an optional pre-release tag.
///
/// This exists because <c>App.Version</c> is a label. It reads <c>0.2.0+8ec4feb</c>, and the commit on the
/// end is build metadata: two builds of the same release differ there and are the same release. Comparing
/// the strings would make every development build look newer than itself.
/// </summary>
public readonly record struct AppVersion(int Major, int Minor, int Patch, string PreRelease)
    : IComparable<AppVersion>
{
    /// <summary>
    /// Reads a version, or says it could not.
    ///
    /// Accepts what this product actually produces and what a manifest might reasonably carry: an optional
    /// leading <c>v</c>, one to three numbers, an optional <c>-pre.release</c> tag, and build metadata
    /// after a <c>+</c> which is discarded.
    /// </summary>
    public static bool TryParse(string? text, out AppVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        ReadOnlySpan<char> span = text.AsSpan().Trim();

        // Build metadata is never compared, which is what makes 0.2.0+a and 0.2.0+b the same release.
        int plus = span.IndexOf('+');
        if (plus >= 0)
        {
            span = span[..plus];
        }

        if (span.Length > 0 && (span[0] == 'v' || span[0] == 'V'))
        {
            span = span[1..];
        }

        ReadOnlySpan<char> pre = default;
        int dash = span.IndexOf('-');
        if (dash >= 0)
        {
            pre = span[(dash + 1)..];
            span = span[..dash];
            if (pre.IsEmpty || !IsIdentifierList(pre))
            {
                return false;
            }
        }

        Span<int> numbers = [0, 0, 0];
        int index = 0;
        foreach (Range part in Split(span))
        {
            if (index == 3)
            {
                // A fourth component is a Windows file version, not a release. Refuse rather than guess
                // which three of the four were meant.
                return false;
            }

            ReadOnlySpan<char> value = span[part];

            // NumberStyles.None so "+1", " 1" and "1_0" are all refused rather than quietly accepted.
            if (value.IsEmpty
                || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number)
                || number < 0)
            {
                return false;
            }

            numbers[index++] = number;
        }

        if (index == 0)
        {
            return false;
        }

        version = new AppVersion(numbers[0], numbers[1], numbers[2], pre.IsEmpty ? string.Empty : pre.ToString());
        return true;
    }

    /// <summary>
    /// True only when both parse and <paramref name="candidate"/> is strictly newer.
    ///
    /// Anything unreadable is not newer. That direction is deliberate and is the opposite of the intuitive
    /// default: a server answering nonsense should produce silence, not a notice the user can never clear.
    /// </summary>
    public static bool IsNewer(string? candidate, string? current) =>
        TryParse(candidate, out AppVersion newer)
        && TryParse(current, out AppVersion running)
        && newer.CompareTo(running) > 0;

    public int CompareTo(AppVersion other)
    {
        int result = Major.CompareTo(other.Major);
        if (result != 0)
        {
            return result;
        }

        result = Minor.CompareTo(other.Minor);
        if (result != 0)
        {
            return result;
        }

        result = Patch.CompareTo(other.Patch);
        if (result != 0)
        {
            return result;
        }

        // A release outranks its own pre-releases: 0.3.0 is newer than 0.3.0-rc.1.
        if (PreRelease.Length == 0)
        {
            return other.PreRelease.Length == 0 ? 0 : 1;
        }

        return other.PreRelease.Length == 0 ? -1 : ComparePreRelease(PreRelease, other.PreRelease);
    }

    private static int ComparePreRelease(string left, string right)
    {
        string[] a = left.Split('.');
        string[] b = right.Split('.');

        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            bool aNumeric = int.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out int an);
            bool bNumeric = int.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out int bn);

            int result = (aNumeric, bNumeric) switch
            {
                // rc.2 before rc.10, which is the whole reason this is not a string comparison.
                (true, true) => an.CompareTo(bn),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(a[i], b[i]),
            };

            if (result != 0)
            {
                return result;
            }
        }

        // rc.1.1 after rc.1, when everything they share is equal.
        return a.Length.CompareTo(b.Length);
    }

    private static bool IsIdentifierList(ReadOnlySpan<char> value)
    {
        foreach (Range part in Split(value))
        {
            ReadOnlySpan<char> identifier = value[part];
            if (identifier.IsEmpty)
            {
                return false;
            }

            foreach (char c in identifier)
            {
                if (!char.IsAsciiLetterOrDigit(c) && c != '-')
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static List<Range> Split(ReadOnlySpan<char> value)
    {
        var parts = new List<Range>();
        int start = 0;
        for (int i = 0; i <= value.Length; i++)
        {
            if (i == value.Length || value[i] == '.')
            {
                parts.Add(new Range(start, i));
                start = i + 1;
            }
        }

        return parts;
    }

    public override string ToString() =>
        PreRelease.Length == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}")
            : string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}-{PreRelease}");

    public static bool operator <(AppVersion left, AppVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(AppVersion left, AppVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(AppVersion left, AppVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(AppVersion left, AppVersion right) => left.CompareTo(right) >= 0;
}
