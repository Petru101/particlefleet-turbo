using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace ParticleFleetTurbo
{
    /// <summary>Accumulates per-script and per-PRPL-statement time over a reporting window.</summary>
    internal static class Profiler
    {
        private class ScriptStat
        {
            public long Ticks;
            public long Calls;
        }

        public static bool Enabled;
        /// <summary>Per-statement timing adds a timestamp per PRPL command, inflating cheap commands; off by default.</summary>
        public static bool StatementTiming;

        private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;
        private static readonly int StatementCount = Enum.GetValues(typeof(CrplCore.STATEMENT)).Cast<int>().Max() + 1;

        private static readonly Dictionary<string, ScriptStat> Scripts = new Dictionary<string, ScriptStat>();
        private static readonly long[] StatementTicks = new long[StatementCount];
        private static readonly long[] StatementCalls = new long[StatementCount];

        private static int depth;
        private static long coreStart;
        private static int lastStatement = -1;
        private static long lastTimestamp;

        private static long updateStart;
        private static long updateTicks;
        private static long crplTicks;
        private static float frameSeconds;
        private static int frames;

        public static int Frames => frames;
        public static double FrameMs => frames == 0 ? 0 : frameSeconds * 1000 / frames;
        public static double UpdateMs => frames == 0 ? 0 : updateTicks * MsPerTick / frames;
        public static double ScriptMs => frames == 0 ? 0 : crplTicks * MsPerTick / frames;

        public static void OnStatement(int statement)
        {
            if (!Enabled || !StatementTiming || depth != 1)
                return;
            long now = Stopwatch.GetTimestamp();
            if (lastStatement >= 0)
                StatementTicks[lastStatement] += now - lastTimestamp;
            StatementCalls[statement]++;
            lastStatement = statement;
            lastTimestamp = now;
        }

        public static void CoreBegin()
        {
            if (!Enabled)
                return;
            if (++depth != 1)
                return;
            coreStart = lastTimestamp = Stopwatch.GetTimestamp();
            lastStatement = -1;
        }

        public static void CoreEnd(CrplCore core)
        {
            if (!Enabled || depth == 0)
                return;
            if (--depth != 0)
                return;
            long now = Stopwatch.GetTimestamp();
            if (lastStatement >= 0)
                StatementTicks[lastStatement] += now - lastTimestamp;
            lastStatement = -1;

            string name = core.scriptName ?? "?";
            if (!Scripts.TryGetValue(name, out ScriptStat stat))
                Scripts[name] = stat = new ScriptStat();
            stat.Ticks += now - coreStart;
            stat.Calls++;
            crplTicks += now - coreStart;
        }

        public static void UpdateBegin()
        {
            if (Enabled)
                updateStart = Stopwatch.GetTimestamp();
        }

        public static void UpdateEnd(float unscaledDeltaTime)
        {
            if (!Enabled)
                return;
            updateTicks += Stopwatch.GetTimestamp() - updateStart;
            frameSeconds += unscaledDeltaTime;
            frames++;
        }

        public static string Report(int topScripts, int topStatements)
        {
            if (frames == 0)
                return "No game frames recorded yet.";
            double f = frames;
            var sb = new StringBuilder();
            sb.AppendFormat("{0} frames | real frame {1:F1} ms ({2:F1} fps, target 30) | GameSpace.Update {3:F2} ms | scripts {4:F2} ms\n",
                frames, frameSeconds * 1000 / f, f / frameSeconds, updateTicks * MsPerTick / f, crplTicks * MsPerTick / f);
            sb.AppendFormat("Managed heap {0} MB\n", GC.GetTotalMemory(false) >> 20);

            sb.Append("Scripts (ms/frame, runs/frame):\n");
            foreach (var kv in Scripts.OrderByDescending(kv => kv.Value.Ticks).Take(topScripts))
                sb.AppendFormat("  {0,8:F3}  {1,6:F0}  {2}\n", kv.Value.Ticks * MsPerTick / f, kv.Value.Calls / f, kv.Key);

            if (!StatementTiming)
                return sb.ToString();
            sb.Append("Statements (ms/frame, calls/frame, us/call):\n");
            foreach (int s in Enumerable.Range(0, StatementCount).Where(s => StatementCalls[s] > 0).OrderByDescending(s => StatementTicks[s]).Take(topStatements))
                sb.AppendFormat("  {0,8:F3}  {1,8:F0}  {2,7:F2}  {3}\n", StatementTicks[s] * MsPerTick / f, StatementCalls[s] / f,
                    StatementTicks[s] * MsPerTick * 1000 / StatementCalls[s], (CrplCore.STATEMENT)s);
            return sb.ToString();
        }

        public static void Reset()
        {
            Scripts.Clear();
            Array.Clear(StatementTicks, 0, StatementTicks.Length);
            Array.Clear(StatementCalls, 0, StatementCalls.Length);
            updateTicks = crplTicks = 0;
            frameSeconds = 0;
            frames = 0;
        }
    }
}
