using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Mdv.Core;

/// <summary>A language the interface is translated into (codes as the game's own languages).</summary>
public sealed record UiLanguage(string Code, string NativeName);

/// <summary>
/// Interface text translation. The English text is the key: <c>L.T("Install")</c>,
/// <c>L.T($"Copy {file} to {dest}")</c> (the key is <c>"Copy {0} to {1}"</c>). Catalogs are
/// <c>data/lang/&lt;code&gt;.json</c> (English → translation); anything missing stays English.
/// </summary>
public static class L
{
    public static readonly IReadOnlyList<UiLanguage> Languages =
    [
        new("en-US", "English"),
        new("fr-FR", "Français"),
        new("de-DE", "Deutsch"),
        new("it-IT", "Italiano"),
        new("es-ES", "Español (España)"),
        new("pt-BR", "Português (Brasil)"),
        new("pl-PL", "Polski"),
        new("ru-RU", "Русский"),
        new("ko-KR", "한국어"),
        new("zh-TW", "繁體中文"),
        new("ja-JP", "日本語"),
        new("es-MX", "Español (México)"),
        new("zh-CN", "简体中文"),
    ];

    private static Dictionary<string, string> _catalog = new(StringComparer.Ordinal);

    /// <summary>The current language code ("en-US" until <see cref="Use"/> picks another).</summary>
    public static string Current { get; private set; } = "en-US";

    /// <summary>The culture of the current language (dates, numbers).</summary>
    public static CultureInfo Culture { get; private set; } = CultureInfo.InvariantCulture;

    /// <summary>Raised after the language changed.</summary>
    public static event Action? Changed;

    /// <summary>Folder with the catalogs; by default <c>data/lang</c> next to the program.</summary>
    public static string Folder { get; set; } = Path.Combine(AppContext.BaseDirectory, "data", "lang");

    /// <summary>The supported language closest to a culture name ("de-AT" → de-DE, "es-AR" → es-MX, "zh-HK" → zh-TW).</summary>
    public static string Match(string? culture)
    {
        if (string.IsNullOrEmpty(culture)) return "en-US";
        var exact = Languages.FirstOrDefault(l => l.Code.Equals(culture, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact.Code;
        var lang = culture.Split('-')[0].ToLowerInvariant();
        return lang switch
        {
            "zh" => culture.Contains("Hant", StringComparison.OrdinalIgnoreCase) ||
                    culture.EndsWith("-HK", StringComparison.OrdinalIgnoreCase) ||
                    culture.EndsWith("-MO", StringComparison.OrdinalIgnoreCase) ? "zh-TW" : "zh-CN",
            "es" => culture.EndsWith("-ES", StringComparison.OrdinalIgnoreCase) ? "es-ES" : "es-MX",
            "pt" => "pt-BR",
            _ => Languages.FirstOrDefault(l => l.Code.StartsWith(lang + "-", StringComparison.Ordinal))?.Code ?? "en-US",
        };
    }

    // the app runs with invariant globalization: no culture data, so the invariant culture then
    private static CultureInfo CultureOf(string code)
    {
        try { return CultureInfo.GetCultureInfo(code); }
        catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; }
    }

    /// <summary>A short date in the current language ("12 Sep 2026"; year-month-day where month names aren't known).</summary>
    public static string Date(DateTime d) =>
        Current == "en-US" || !Equals(Culture, CultureInfo.InvariantCulture)
            ? d.ToString("d MMM yyyy", Current == "en-US" ? CultureInfo.InvariantCulture : Culture)
            : d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The language for the system's interface culture.</summary>
    public static string SystemLanguage()
    {
        // invariant globalization leaves CurrentUICulture empty: ask Windows for the UI language
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var buf = new char[85];
                int n = LCIDToLocaleName(GetUserDefaultUILanguage(), buf, buf.Length, 0);
                if (n > 1) return Match(new string(buf, 0, n - 1));
            }
            catch (Exception) { /* fall through */ }
        }
        return Match(CultureInfo.CurrentUICulture.Name);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern ushort GetUserDefaultUILanguage();

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int LCIDToLocaleName(uint locale, [System.Runtime.InteropServices.Out] char[] name, int size, uint flags);

    /// <summary>Switches to a language (a code from <see cref="Languages"/>; anything else → English).</summary>
    public static void Use(string? code)
    {
        code = Match(code);
        var catalog = new Dictionary<string, string>(StringComparer.Ordinal);
        if (code != "en-US")
        {
            try
            {
                var file = Path.Combine(Folder, code + ".json");
                if (File.Exists(file))
                    foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? [])
                        if (!string.IsNullOrEmpty(v)) catalog[k] = v;
            }
            catch (Exception) { /* a broken catalog leaves the text English */ }
        }
        _catalog = catalog;
        Current = code;
        Culture = CultureOf(code);
        Changed?.Invoke();
    }

    /// <summary>Marks a text for the catalogs without translating it (static tables; translated where shown).</summary>
    public static string N(string text) => text;

    /// <summary>Translates a plain text.</summary>
    public static string T(string text) => _catalog.TryGetValue(text, out var t) ? t : text;

    /// <summary>Translates an interpolated text: the translation keeps the holes as {0}, {1}…</summary>
    public static string T(LocText text)
    {
        var key = text.Key;
        if (!_catalog.TryGetValue(key, out var format)) return text.English;
        try { return string.Format(CultureInfo.CurrentCulture, format, text.Args); }
        catch (FormatException) { return text.English; }
    }
}

/// <summary>Collects an interpolated string as a format key ("{0}", "{1:N0}"…) plus its values.</summary>
[InterpolatedStringHandler]
public ref struct LocText
{
    private readonly StringBuilder _key;
    private readonly StringBuilder _english;
    private readonly List<object?> _args;

    public LocText(int literalLength, int formattedCount)
    {
        _key = new StringBuilder(literalLength + formattedCount * 4);
        _english = new StringBuilder(literalLength + formattedCount * 8);
        _args = new List<object?>(formattedCount);
    }

    public void AppendLiteral(string s)
    {
        _key.Append(s.Replace("{", "{{").Replace("}", "}}"));
        _english.Append(s);
    }

    public void AppendFormatted<T>(T value) => Hole(value, 0, null);
    public void AppendFormatted<T>(T value, string? format) => Hole(value, 0, format);
    public void AppendFormatted<T>(T value, int alignment) => Hole(value, alignment, null);
    public void AppendFormatted<T>(T value, int alignment, string? format) => Hole(value, alignment, format);
    public void AppendFormatted(string? value) => Hole(value, 0, null);

    private void Hole(object? value, int alignment, string? format)
    {
        _key.Append('{').Append(_args.Count);
        if (alignment != 0) _key.Append(',').Append(alignment);
        if (format is not null) _key.Append(':').Append(format);
        _key.Append('}');
        var one = "{0" + (alignment != 0 ? "," + alignment : "") + (format is not null ? ":" + format : "") + "}";
        _english.Append(string.Format(CultureInfo.CurrentCulture, one, value));
        _args.Add(value);
    }

    internal readonly string Key => _key.ToString();
    internal readonly string English => _english.ToString();
    internal readonly object?[] Args => _args.ToArray();
}
