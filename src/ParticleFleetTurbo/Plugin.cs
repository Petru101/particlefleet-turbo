using System.Collections.Generic;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ParticleFleetTurbo
{
    [BepInPlugin("petru.particlefleet.turbo", "Particle Fleet Turbo", "0.1.0")]
    public class Plugin : BaseUnityPlugin
    {
        private ConfigEntry<bool> turboEnabled;
        private ConfigEntry<KeyboardShortcut> turboKey;
        private ConfigEntry<bool> profilerEnabled;
        private ConfigEntry<bool> statementTiming;
        private ConfigEntry<KeyboardShortcut> selfTestKey;
        private ConfigEntry<int> selfTestTicks;
        private ConfigEntry<bool> selfTestBrokenCacheRun;
        private ConfigEntry<KeyboardShortcut> overlayKey;
        private ConfigEntry<float> reportSeconds;

        private bool showOverlay;
        private float nextReport;
        private string overlayText = "";

        private void Awake()
        {
            turboEnabled = Config.Bind("Turbo", "Enabled", true, "Enable the engine optimizations for map scripts.");
            turboKey = Config.Bind("Turbo", "ToggleKey", new KeyboardShortcut(KeyCode.F10), "Toggle the optimizations in-game (for A/B comparison).");
            selfTestKey = Config.Bind("SelfTest", "Key", KeyboardShortcut.Empty, "Developer tool (e.g. F11): replay the current mission with Turbo off/off/on and save each result for comparison.");
            selfTestTicks = Config.Bind("SelfTest", "Ticks", 300, "Game ticks per self-test run.");
            selfTestBrokenCacheRun = Config.Bind("SelfTest", "BrokenCacheRun", false,
                "Add a run with cache invalidation disabled; its save should differ, proving the test can catch stale results.");
            profilerEnabled = Config.Bind("Profiler", "Enabled", false, "Always measure time spent in PRPL scripts and log a report periodically (it is also on while the overlay is shown).");
            statementTiming = Config.Bind("Profiler", "StatementTiming", false, "Also time each PRPL command type (adds overhead to cheap commands).");
            overlayKey = Config.Bind("Profiler", "OverlayKey", new KeyboardShortcut(KeyCode.F9), "Toggle the on-screen profiler overlay.");
            reportSeconds = Config.Bind("Profiler", "ReportSeconds", 10f, "Seconds between profiler reports in the BepInEx log.");
        }

        // Patching touches Assembly-CSharp; doing it from Awake loads that assembly while Unity 5.3 is
        // still registering scripts, which leaves every UI MonoBehaviour "missing" (black screen).
        private void Start()
        {
            Profiler.Enabled = profilerEnabled.Value;
            Profiler.StatementTiming = statementTiming.Value;
            Turbo.Set(turboEnabled.Value);
            new Harmony("petru.particlefleet.turbo").PatchAll(typeof(Plugin).Assembly);
            Logger.LogInfo("Particle Fleet Turbo patches applied");
        }

        private void Update()
        {
            if (overlayKey.Value.IsDown())
            {
                showOverlay = !showOverlay;
                Profiler.Enabled = profilerEnabled.Value || showOverlay;
                Profiler.Reset();
                overlayText = "Measuring...";
            }
            if (selfTestKey.Value.IsDown())
                SelfTest.Start(Logger, System.IO.Path.Combine(Paths.BepInExRootPath, "turbo-selftest"), selfTestTicks.Value, selfTestBrokenCacheRun.Value);
            if (turboKey.Value.IsDown() && !SelfTest.Busy)
            {
                Turbo.Set(!Turbo.Enabled);
                Profiler.Reset();
                nextReport = Time.realtimeSinceStartup + reportSeconds.Value;
                overlayText = "Turbo " + (Turbo.Enabled ? "ON" : "OFF") + " - measuring...";
                Logger.LogInfo(overlayText);
            }

            if (!Profiler.Enabled || SelfTest.Busy || Time.realtimeSinceStartup < nextReport)
                return;
            nextReport = Time.realtimeSinceStartup + reportSeconds.Value;
            if (Profiler.Frames == 0)
                return;
            overlayText = "Turbo " + (Turbo.Enabled ? "ON" : "OFF") + " | " + Profiler.Report(12, 20);
            Logger.LogInfo("Profiler report\n" + overlayText);
            Profiler.Reset();
        }

        private void OnGUI()
        {
            if (!showOverlay)
                return;
            GUI.Box(new Rect(10, 10, 620, 560), "");
            GUI.Label(new Rect(20, 15, 600, 550), overlayText);
        }
    }

    [HarmonyPatch(typeof(CrplCore), nameof(CrplCore.GameUpdate))]
    internal static class CrplCoreGameUpdatePatch
    {
        private static void Prefix() => Profiler.CoreBegin();
        private static void Finalizer(CrplCore __instance) => Profiler.CoreEnd(__instance);
    }

    [HarmonyPatch(typeof(GameSpace), "Update")]
    internal static class GameSpaceUpdatePatch
    {
        private static void Prefix() => Profiler.UpdateBegin();
        private static void Postfix(GameSpace __instance)
        {
            Profiler.UpdateEnd(Time.unscaledDeltaTime);
            SelfTest.AfterGameUpdate(__instance);
        }
    }

    [HarmonyPatch(typeof(CrplCore), "ProcessCommands")]
    internal static class ProcessCommandsPatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var statementField = AccessTools.Field(typeof(CrplCore.Command), nameof(CrplCore.Command.statement));
            var onStatement = AccessTools.Method(typeof(Profiler), nameof(Profiler.OnStatement));
            bool patched = false;
            foreach (var instruction in instructions)
            {
                yield return instruction;
                if (!patched && instruction.LoadsField(statementField))
                {
                    yield return new CodeInstruction(OpCodes.Dup);
                    yield return new CodeInstruction(OpCodes.Call, onStatement);
                    patched = true;
                }
            }
        }
    }
}
