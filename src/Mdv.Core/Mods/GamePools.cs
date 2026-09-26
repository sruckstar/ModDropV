using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Mdv.Core.Rpf;
using Mdv.Core.Util;

namespace Mdv.Core.Mods;

/// <summary>
/// The game's pool sizes in gameconfig.xml (<c>&lt;PoolName&gt;MetaDataStore&lt;/PoolName&gt;&lt;PoolSize value="3200"/&gt;</c>).
/// With every DLC up to 2026 the game's own files fill some of them to the last place — Rockstar's crash log
/// (<c>%LOCALAPPDATA%\Rockstar Games\GTAV\CrashLogs\crashcontext.log</c>, "ASSET STORES INFO") showed
/// MetaDataStore 3200 / 3200 on Legacy and 3199 / 3200 on Enhanced: one more .ymt (every add-on ped has one)
/// and the game crashes on loading. Such a pool is raised in the copy of gameconfig.xml under mods.
/// </summary>
public static partial class GamePools
{
    public const string GameConfig = "update/update.rpf/common/data/gameconfig.xml";
    public const string MetaDataStore = "MetaDataStore";
    /// <summary>What MetaDataStore is raised to: half as much again as the game's 3200.</summary>
    public const int MetaDataStoreTarget = 4800;

    private static Regex PoolRe(string pool) =>
        new($@"(<PoolName>\s*{Regex.Escape(pool)}\s*</PoolName>\s*<PoolSize\s+value\s*=\s*"")(\d+)("")", RegexOptions.IgnoreCase);

    public static int? Size(string xml, string pool) =>
        PoolRe(pool).Match(xml) is { Success: true } m && int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? n : null;

    /// <summary>The same gameconfig.xml with one pool's size changed; everything else stays byte for byte.</summary>
    public static string WithSize(string xml, string pool, int size) =>
        PoolRe(pool).Replace(xml, m => m.Groups[1].Value + size.ToString(CultureInfo.InvariantCulture) + m.Groups[3].Value, 1);

    /// <summary>A pool's size in the gameconfig.xml the game reads now (the copy in mods, else its own); null when unknown.</summary>
    public static int? Read(string gameDir, string pool)
    {
        try
        {
            return ModsOverlay.Load(gameDir).Read(GameConfig) is { } data ? Size(TextIo.DecodeUtf8Sig(data, strict: false), pool) : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or RpfFormatException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The edit for <see cref="RpfEditOp"/>: <paramref name="pool"/> raised to <paramref name="size"/>. When it is that big
    /// already, the file as it is — so every mod that needs the pool holds a version of it in the mods layer, and it stays
    /// raised until the last of them is removed. Null when there is no file or no such pool.
    /// </summary>
    public static byte[]? Raise(byte[]? data, string pool, int size, Action<string> log)
    {
        if (data is null) return null;
        var xml = TextIo.DecodeUtf8Sig(data, strict: false);
        if (Size(xml, pool) is not { } now) return null;
        if (now >= size) return data;
        log($"    gameconfig.xml: {pool} {now} → {size}.");
        return new UTF8Encoding(false).GetBytes(WithSize(xml, pool, size));
    }
}
