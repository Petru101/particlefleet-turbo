using System.Collections.Generic;
using System.IO;
using BepInEx.Logging;

namespace ParticleFleetTurbo
{
    /// <summary>
    /// Determinism check: snapshots the current mission, then replays it from the snapshot for a fixed number of
    /// ticks with Turbo off, off again (to measure the game's own run-to-run variation) and on, saving each result.
    /// tools/compare_saves.py diffs the saves.
    /// </summary>
    internal static class SelfTest
    {
        private enum State { Idle, Loading, Running }

        private static ManualLogSource log;
        private static string dir;
        private static int ticks;
        private static bool turboAfter;
        private static bool profilerAfter;

        private static State state = State.Idle;
        private struct Run
        {
            public bool Turbo;
            public bool BrokenCache;
        }

        private static readonly Queue<Run> Runs = new Queue<Run>();
        private static int runIndex;
        private static GameSpace previousInstance;
        private static int startCount;

        public static bool Busy => state != State.Idle;

        public static void Start(ManualLogSource logger, string outputDir, int tickCount, bool brokenCacheRun)
        {
            GameSpace game = GameSpace.instance;
            if (Busy || game == null || GameSpace.editMode)
            {
                logger.LogWarning("Self-test needs a running (non-editor) mission and no test in progress.");
                return;
            }
            log = logger;
            dir = outputDir;
            ticks = tickCount;
            turboAfter = Turbo.Enabled;
            profilerAfter = Profiler.Enabled;
            Profiler.Enabled = true;
            Directory.CreateDirectory(dir);
            File.Delete(Path.Combine(dir, "results.txt"));
            game.paused = true;
            SaveGameManager.SaveMission(SnapshotPath, includeGUID: true);
            log.LogInfo($"Self-test: snapshot at tick {game.updateCount} -> {SnapshotPath}");

            Runs.Clear();
            Runs.Enqueue(new Run { Turbo = false });
            Runs.Enqueue(new Run { Turbo = false });
            Runs.Enqueue(new Run { Turbo = true });
            // Sanity check of the test itself: with cache invalidation disabled the saves should differ.
            if (brokenCacheRun)
                Runs.Enqueue(new Run { Turbo = true, BrokenCache = true });
            runIndex = 0;
            NextRun();
        }

        private static string SnapshotPath => Path.Combine(dir, "snapshot.pf1");

        private static void NextRun()
        {
            if (Runs.Count == 0)
            {
                Turbo.Set(turboAfter);
                CoresWithVarCache.BreakInvalidation = false;
                Profiler.Enabled = profilerAfter;
                state = State.Idle;
                log.LogInfo($"Self-test finished: results in {dir}");
                return;
            }
            Run run = Runs.Dequeue();
            bool turbo = run.Turbo;
            runIndex++;
            Turbo.Set(turbo);
            CoresWithVarCache.BreakInvalidation = run.BrokenCache;
            previousInstance = GameSpace.instance;
            state = State.Loading;
            log.LogInfo($"Self-test run {runIndex}: Turbo {(turbo ? "ON" : "OFF")}{(run.BrokenCache ? " (cache invalidation disabled)" : "")}, loading snapshot");
            LoadingScreen.LoadGame(SnapshotPath, embeddedLoad: false, editMode: false, GameSpace.launchCategory, GameSpace.heapID);
        }

        /// <summary>Called after every GameSpace.Update.</summary>
        public static void AfterGameUpdate(GameSpace game)
        {
            switch (state)
            {
                case State.Loading:
                    if (game == previousInstance)
                        return;
                    // Loaded missions wait on the fleet/launch panel; press Launch the way the player would.
                    if (game.openingPanel != null && game.openingPanel.gameObject.activeSelf)
                    {
                        game.openingPanel.MainLaunchClicked();
                        return;
                    }
                    startCount = game.updateCount;
                    Profiler.Reset();
                    game.SPEED_MULTIPLE = 1f;
                    game.paused = false;
                    state = State.Running;
                    break;
                case State.Running:
                    if (game.updateCount - startCount < ticks)
                        return;
                    game.paused = true;
                    string label = Turbo.Enabled ? (CoresWithVarCache.BreakInvalidation ? "on-brokencache" : "on") : "off";
                    string path = Path.Combine(dir, $"run{runIndex}-turbo-{label}.pf1");
                    SaveGameManager.SaveMission(path, includeGUID: true);
                    string timing = $"run {runIndex} Turbo {label}: real frame {Profiler.FrameMs:F1} ms, " +
                                    $"GameSpace.Update {Profiler.UpdateMs:F1} ms, scripts {Profiler.ScriptMs:F1} ms";
                    File.AppendAllText(Path.Combine(dir, "results.txt"), timing + "\n");
                    log.LogInfo($"Self-test {timing}; ran {game.updateCount - startCount} ticks -> {path}");
                    NextRun();
                    break;
            }
        }
    }
}
