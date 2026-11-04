using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace ParticleFleetTurbo
{
    /// <summary>
    /// Inclusive time per method (start to return, callees included), for methods listed in
    /// <c>[Profiler] TimedMethods</c>. Unlike <see cref="Sections"/>, untimed code is never charged to a timed method.
    /// </summary>
    internal static class MethodTimer
    {
        private class Stat
        {
            public long Ticks, Calls;
        }

        private static readonly Dictionary<MethodBase, Stat> Stats = new Dictionary<MethodBase, Stat>();

        public static bool Any => Stats.Count > 0;

        public static void Time(Harmony harmony, MethodBase method)
        {
            Stats[method] = new Stat();
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(MethodTimer), nameof(Prefix)), postfix: new HarmonyMethod(typeof(MethodTimer), nameof(Postfix)));
        }

        private static void Prefix(out long __state) => __state = Stopwatch.GetTimestamp();

        private static void Postfix(long __state, MethodBase __originalMethod)
        {
            if (!Profiler.Enabled)
                return;
            Stat stat = Stats[__originalMethod];
            stat.Ticks += Stopwatch.GetTimestamp() - __state;
            stat.Calls++;
        }

        public static string Report(int frames)
        {
            var sb = new StringBuilder("Timed methods, inclusive (ms/frame, calls/frame):\n");
            double msPerTick = 1000.0 / Stopwatch.Frequency;
            foreach (var kv in Stats.OrderByDescending(kv => kv.Value.Ticks))
                sb.AppendFormat("  {0,8:F3}  {1,8:F1}  {2}.{3}\n", kv.Value.Ticks * msPerTick / frames, kv.Value.Calls / (double)frames, kv.Key.DeclaringType.Name, kv.Key.Name);
            return sb.ToString();
        }

        public static void Reset()
        {
            foreach (Stat stat in Stats.Values)
                stat.Ticks = stat.Calls = 0;
        }
    }
}
