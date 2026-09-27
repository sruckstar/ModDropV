using CodeWalker.GameFiles;

namespace Mdv.Core.Rpf;

/// <summary>
/// Decryption of the game's own archives (update.rpf and friends ship NG-encrypted, both
/// in Legacy and Enhanced). Keys come from the game executable exactly like CodeWalker
/// does it: the AES key is located in GTA5.exe / GTA5_Enhanced.exe by its SHA1 hash,
/// then it unlocks CodeWalker's bundled NG key tables. Nothing secret ships with the app.
/// Both editions share the same keys, so they are loaded once per process.
/// </summary>
public sealed class GameCrypto
{
    public const uint EncNone = 0;
    public const uint EncAes = 0x0FFFFFF9;
    public const uint EncNg = 0x0FEFFFFF;

    private static readonly Lock Gate = new();
    private static GameCrypto? _loaded;

    private GameCrypto() { }

    private static string? _keySource;
    private static bool _searched;

    /// <summary>
    /// The game folder whose executable unlocks mods' archives that come encrypted the game's
    /// way (some tools save a dlc.rpf NG-encrypted). The keys are read only when such an archive
    /// turns up.
    /// </summary>
    public static void UseGame(string? gameDir)
    {
        lock (Gate)
        {
            if (!string.IsNullOrWhiteSpace(gameDir)) _keySource = gameDir.Trim();
        }
    }

    /// <summary>
    /// Keys for an encrypted archive opened without any: the ones already read, else from the
    /// game named by <see cref="UseGame"/>, else from a game found on this PC; null when there is none.
    /// </summary>
    internal static GameCrypto? Fallback()
    {
        lock (Gate)
        {
            if (_loaded is not null) return _loaded;
            if (_keySource is { } dir && TryFor(dir) is { } k) return k;
            if (_searched) return null;
            _searched = true;
            foreach (var g in GameLocator.Find())
                if (TryFor(g.Path) is { } found) return found;
            return null;
        }
    }

    private static GameCrypto? TryFor(string gameDir)
    {
        try { return ForGame(gameDir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Keys from the executable in <paramref name="gameDir"/> (either edition's).</summary>
    /// <exception cref="FileNotFoundException">no GTA5.exe / GTA5_Enhanced.exe in the folder</exception>
    /// <exception cref="InvalidOperationException">the executable doesn't hold the keys</exception>
    public static GameCrypto ForGame(string gameDir, Action<string>? log = null)
    {
        lock (Gate)
        {
            if (_loaded is not null) return _loaded;
            var edition = GameEditions.Detect(gameDir);
            if (edition is null)
            {
                if (File.Exists(Path.Combine(gameDir, GameEditions.EnhancedExe))) edition = GameEdition.Enhanced;
                else if (File.Exists(Path.Combine(gameDir, GameEditions.LegacyExe))) edition = GameEdition.Legacy;
                else
                    throw new FileNotFoundException(
                        $"The game's archives are encrypted and the keys are read from the game itself, " +
                        $"but neither {GameEditions.LegacyExe} nor {GameEditions.EnhancedExe} is in {gameDir}.");
            }
            log?.Invoke($"    Reading the archive keys from {edition.Value.ExeName()}…");
            try
            {
                GTA5Keys.LoadFromPath(Path.TrimEndingDirectorySeparator(gameDir), edition == GameEdition.Enhanced);
            }
            catch (Exception ex) when (ex is not IOException and not UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    $"Could not read the archive keys from {edition.Value.ExeName()} ({ex.Message}).", ex);
            }
            if (GTA5Keys.PC_AES_KEY is null || GTA5Keys.PC_NG_KEYS is null)
                throw new InvalidOperationException(
                    $"{edition.Value.ExeName()} doesn't contain the archive keys — is it the original game executable?");
            return _loaded = new GameCrypto();
        }
    }

    /// <summary>Decrypt a TOC / names block of an archive (<paramref name="archiveName"/> is its file name).</summary>
    public byte[] DecryptArchiveBlock(byte[] data, uint encryption, string archiveName, uint archiveSize) =>
        encryption == EncAes ? GTACrypto.DecryptAES(data) : GTACrypto.DecryptNG(data, archiveName, archiveSize);

    /// <summary>Decrypt an encrypted file entry (CodeWalker: AES archives use AES, everything else NG).</summary>
    public byte[] DecryptEntry(byte[] data, uint archiveEncryption, string entryName, uint uncompressedSize) =>
        archiveEncryption == EncAes ? GTACrypto.DecryptAES(data) : GTACrypto.DecryptNG(data, entryName, uncompressedSize);
}
