using Avalonia.Threading;
using Mdv.App.ViewModels;
using Mdv.Core;
using Mdv.Core.Index;
using Mdv.Core.Mods;

namespace Mdv.App.Services;

/// <summary>
/// Builds / refreshes the file index of the selected game in the background, so the first
/// real use (finding the files a replace mod swaps) doesn't wait for it. Only the cache is
/// kept — the index itself is dropped once written. A new game folder cancels the previous run.
/// Archive copies in mods that went stale with a game update are reported to the log; the
/// number of DLC packs the game mounts goes to the game status. After an install the index
/// is brought up to date again (only the changed archives are re-read).
/// </summary>
public static class GameIndexWarmup
{
    private static readonly Lock Gate = new();
    private static CancellationTokenSource? _cts;
    private static string _current = "";
    private static MainViewModel? _vm;

    public static void Attach(MainViewModel vm)
    {
        _vm = vm;
        lock (Gate) _current = "";   // a new window (language switch) gets the game's state again
        static bool IsGame(string game) =>
            game.Length > 0 && (GameEditions.Detect(game) is not null || GameEditions.IsAmbiguous(game));

        void Check()
        {
            var game = vm.IsPlayer ? vm.GameFolder.Trim() : "";
            Start(IsGame(game) ? game : "");
        }

        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.GameFolder) or nameof(MainViewModel.IsPlayer)) Check();
        };
        vm.GameFilesChanged += game =>
        {
            if (IsGame(game)) Start(game, again: true);
        };
        Check();
    }

    private static void Start(string game, bool again = false)
    {
        CancellationTokenSource cts;
        lock (Gate)
        {
            if (!again && string.Equals(game, _current, StringComparison.OrdinalIgnoreCase)) return;
            _cts?.Cancel();
            _current = game;
            _cts = null;
            if (game.Length == 0) return;
            _cts = cts = new CancellationTokenSource();
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);    // let the startup / a picked folder settle
                int lastTenth = -1;
                var index = GameIndex.Open(game, GameIndexCache.DefaultRoot, p =>
                {
                    int tenth = p.Done * 10 / Math.Max(1, p.Total);
                    if (!p.FromCache && tenth != Interlocked.Exchange(ref lastTenth, tenth))
                        AppLog.Info($"game index: {tenth * 10}% ({p.Done}/{p.Total} archives)");
                }, msg => AppLog.Info("game index: " + msg.Trim()), cts.Token);
                AppLog.Info($"game index ready: {game} — {index.ExeVersion}, {index.Archives.Count} archives, " +
                            $"{index.FileCount} files ({index.Scanned} read, {index.Reused} cached) in " +
                            $"{index.Elapsed.TotalSeconds:0.0}s; {index.LoadedDlcs.Count} DLC packs mounted");
                var dlcs = MountedDlcs.Of(index);
                Dispatcher.UIThread.Post(() => _vm?.OnGameIndexReady(game, dlcs));
                foreach (var s in ModsOverlay.Load(game).Status().Where(s => s.Stale is not null))
                    AppLog.Info($"mods/{s.Archive} is stale: {s.Stale} ({s.Owned} changed file(s) to carry over)");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                AppLog.Error($"game index failed for {game}", ex);
            }
        });
    }
}
