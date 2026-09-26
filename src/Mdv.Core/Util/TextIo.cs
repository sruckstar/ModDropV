using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mdv.Core.Util;

/// <summary>
/// File text I/O with the semantics the original builder relied on
/// (Python's <c>read_text(encoding="utf-8-sig")</c> / <c>write_text(encoding="utf-8")</c>):
/// a leading BOM is dropped, newlines are normalised to <c>\n</c> on read and written
/// as the platform newline, output carries no BOM.
/// </summary>
public static class TextIo
{
    public static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    /// <summary>Read a text file (utf-8-sig, universal newlines). Invalid UTF-8 is replaced.</summary>
    public static string ReadText(string path) => NormalizeNewlines(DecodeUtf8Sig(File.ReadAllBytes(path), strict: false));

    /// <summary>Like <see cref="ReadText"/> but throws <see cref="DecoderFallbackException"/> on invalid UTF-8.</summary>
    public static string ReadTextStrict(string path) => NormalizeNewlines(DecodeUtf8Sig(File.ReadAllBytes(path), strict: true));

    /// <summary><c>bytes.decode("utf-8-sig")</c> — no newline translation.</summary>
    public static string DecodeUtf8Sig(ReadOnlySpan<byte> data, bool strict = true)
    {
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            data = data[3..];
        return (strict ? StrictUtf8 : Utf8NoBom).GetString(data);
    }

    public static string NormalizeNewlines(string s) =>
        s.Contains('\r') ? s.Replace("\r\n", "\n").Replace('\r', '\n') : s;

    /// <summary>Write text as UTF-8 (no BOM) with platform newlines.</summary>
    public static void WriteText(string path, string text)
    {
        var norm = NormalizeNewlines(text);
        if (Environment.NewLine != "\n") norm = norm.Replace("\n", Environment.NewLine);
        File.WriteAllText(path, norm, Utf8NoBom);
    }

    // ------------------------------------------------------------------ JSON

    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary><c>json.dumps(obj, indent=2, ensure_ascii=False)</c>.</summary>
    public static string ToJson<T>(T value, bool indented = true) =>
        JsonSerializer.Serialize(value, indented ? Pretty : Compact);

    public static string ToJson(JsonNode node, bool indented = true) =>
        node.ToJsonString(indented ? Pretty : Compact);

    public static T? FromJson<T>(string json) => JsonSerializer.Deserialize<T>(json, Pretty);

    public static void WriteJson<T>(string path, T value) => WriteText(path, ToJson(value));
}
