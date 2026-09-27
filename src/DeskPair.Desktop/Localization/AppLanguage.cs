using DeskPair.Desktop.Localization.Translations;

namespace DeskPair.Desktop.Localization;

/// <summary>One language the app is published in: the code the config stores and the name the menu shows.</summary>
public sealed record AppLanguage(string Code, string NativeName);

/// <summary>
/// The languages the desktop app speaks, and the rule that picks one from what the operating system says.
///
/// The codes are BCP-47 as the platforms spell them (<c>zh-TW</c>, <c>zh-Hans</c>, <c>pt-BR</c>); the
/// website uses lower-case URL segments for the same languages, and the glossaries under
/// <c>docs/translations/</c> are named after those. The list is the same ten, in the same order, so the
/// menu here and the menu on the site read alike.
/// </summary>
public static class AppLanguages
{
    public const string En = "en";
    public const string ZhTw = "zh-TW";
    public const string ZhHans = "zh-Hans";
    public const string PtBr = "pt-BR";

    public static readonly IReadOnlyList<AppLanguage> All =
    [
        new(En, "English"),
        new(ZhTw, "繁體中文"),
        new(ZhHans, "简体中文"),
        new("ja", "日本語"),
        new("ko", "한국어"),
        new("de", "Deutsch"),
        new("fr", "Français"),
        new("es", "Español"),
        new(PtBr, "Português (Brasil)"),
        new("ru", "Русский"),
    ];

    public static bool IsKnown(string? code) => code is not null && All.Any(l => string.Equals(l.Code, code, StringComparison.Ordinal));

    /// <summary>
    /// The language for an operating-system culture name, the same way the website reads
    /// <c>Accept-Language</c> (<c>PortalCulture.Match</c>): by primary subtag, with two exceptions.
    /// Chinese is two written languages under one subtag, told apart by script or, failing that, by
    /// where -- Simplified for <c>Hans</c>, mainland China, Singapore and Malaysia; Traditional for
    /// everything else, including a bare <c>zh</c>. Any Portuguese is Brazilian, because that is the
    /// Portuguese there is. Anything unpublished is English.
    ///
    /// Before this the desktop asked one question -- does the culture start with <c>zh</c> -- and a
    /// zh-CN user got Traditional Chinese.
    /// </summary>
    public static string Match(string? cultureName)
    {
        string[] parts = (cultureName ?? string.Empty).Trim().ToLowerInvariant().Split('-', '_');
        switch (parts[0])
        {
            case "zh":
                bool simplified = parts.Contains("hans")
                    || (!parts.Contains("hant") && parts.Skip(1).Any(p => p is "cn" or "sg" or "my"));
                return simplified ? ZhHans : ZhTw;

            case "pt":
                return PtBr;

            default:
                AppLanguage? known = All.FirstOrDefault(l => string.Equals(l.Code, parts[0], StringComparison.Ordinal));
                return known?.Code ?? En;
        }
    }

    /// <summary>The translation table for a code, or null for English (the source) and for a language with no table yet.</summary>
    internal static IReadOnlyDictionary<string, string>? Table(string code) => code switch
    {
        ZhTw => ZhHant.Strings,
        ZhHans => Translations.ZhHans.Strings,
        "ja" => Ja.Strings,
        "ko" => Ko.Strings,
        "de" => De.Strings,
        "fr" => Fr.Strings,
        "es" => Es.Strings,
        PtBr => Translations.PtBr.Strings,
        "ru" => Ru.Strings,
        _ => null,
    };
}
