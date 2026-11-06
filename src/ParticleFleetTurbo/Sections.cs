using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using HarmonyLib;

namespace ParticleFleetTurbo
{
    /// <summary>
    /// Splits engine time (not scripts) into sections: a transpiler puts a mark before every game/Unity call in the
    /// instrumented methods, and the time until the next mark goes to that call site. Parameterless virtual calls
    /// (unit.GameUpdate()) are split further by the receiver's type.
    /// </summary>
    internal static class Sections
    {
        public static bool Enabled;

        /// <summary>Also charge managed heap growth to the sections (<c>[Profiler] AllocTracking</c>). The heap grows in blocks, so only totals over many frames mean anything.</summary>
        public static bool TrackAllocs;

        private static readonly List<string> Labels = new List<string> { "GameSpace.Update (before first mark)", "outside GameSpace.Update" };
        private static readonly List<Dictionary<Type, int>> TypedIds = new List<Dictionary<Type, int>> { null, null };
        private static long[] ticks = new long[256];
        private static long[] calls = new long[256];
        private static long[] bytes = new long[256];
        private static long lastHeap;
        private static int current = -1;
        private static long last;

        public static void Begin()
        {
            if (!Enabled || !Profiler.Enabled)
                return;
            ChargeAllocs(1);
            current = 0;
            last = Stopwatch.GetTimestamp();
        }

        private static void ChargeAllocs(int section)
        {
            if (!TrackAllocs)
                return;
            long heap = GC.GetTotalMemory(false);
            if (heap > lastHeap)
                bytes[section] += heap - lastHeap;
            lastHeap = heap;
        }

        public static void End()
        {
            if (!Enabled || current < 0)
                return;
            ticks[current] += Stopwatch.GetTimestamp() - last;
            ChargeAllocs(current);
            current = -1;
        }

        public static void Mark(int id)
        {
            if (!Enabled || current < 0)
                return;
            long now = Stopwatch.GetTimestamp();
            ticks[current] += now - last;
            ChargeAllocs(current);
            calls[id]++;
            current = id;
            last = now;
        }

        public static void MarkTyped(object receiver, int site)
        {
            if (!Enabled || current < 0)
                return;
            Type type = receiver == null ? typeof(object) : receiver.GetType();
            Dictionary<Type, int> ids = TypedIds[site];
            if (!ids.TryGetValue(type, out int id))
                ids[type] = id = Add(Labels[site] + " [" + type.Name + "]", null);
            Mark(id);
        }

        private static int Add(string label, Dictionary<Type, int> typed)
        {
            Labels.Add(label);
            TypedIds.Add(typed);
            if (Labels.Count > ticks.Length)
            {
                Array.Resize(ref ticks, ticks.Length * 2);
                Array.Resize(ref calls, calls.Length * 2);
                Array.Resize(ref bytes, bytes.Length * 2);
            }
            return Labels.Count - 1;
        }

        public static void Instrument(Harmony harmony, MethodBase method)
        {
            Owner = method.DeclaringType.Name + "." + method.Name;
            harmony.Patch(method, transpiler: new HarmonyMethod(typeof(Sections), nameof(Transpiler)));
        }

        private static string Owner;

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var mark = AccessTools.Method(typeof(Sections), nameof(Mark));
            var markTyped = AccessTools.Method(typeof(Sections), nameof(MarkTyped));
            Assembly game = typeof(GameSpace).Assembly, unity = typeof(UnityEngine.Object).Assembly;
            var seen = new Dictionary<string, int>();
            foreach (var instruction in instructions)
            {
                if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && instruction.operand is MethodInfo callee &&
                    !callee.IsSpecialName && (callee.DeclaringType.Assembly == game || callee.DeclaringType.Assembly == unity))
                {
                    string name = callee.DeclaringType.Name + "." + callee.Name;
                    seen.TryGetValue(name, out int n);
                    seen[name] = n + 1;
                    string label = Owner + ": " + name + (n > 0 ? "#" + (n + 1) : "");
                    bool typed = instruction.opcode == OpCodes.Callvirt && callee.IsVirtual && callee.GetParameters().Length == 0;
                    int id = Add(label, typed ? new Dictionary<Type, int>() : null);
                    var first = typed ? new CodeInstruction(OpCodes.Dup) : new CodeInstruction(OpCodes.Ldc_I4, id);
                    first.labels.AddRange(instruction.labels);
                    instruction.labels.Clear();
                    yield return first;
                    if (typed)
                        yield return new CodeInstruction(OpCodes.Ldc_I4, id);
                    yield return new CodeInstruction(OpCodes.Call, typed ? markTyped : mark);
                }
                yield return instruction;
            }
        }

        public static string Report(int frames, int top)
        {
            var sb = new StringBuilder("Engine sections (ms/frame, calls/frame):\n");
            double msPerTick = 1000.0 / Stopwatch.Frequency;
            foreach (int i in Enumerable.Range(0, Labels.Count).Where(i => ticks[i] > 0).OrderByDescending(i => ticks[i]).Take(top))
                sb.AppendFormat("  {0,8:F3}  {1,8:F0}  {2}\n", ticks[i] * msPerTick / frames, calls[i] / (double)frames, Labels[i]);
            if (TrackAllocs)
            {
                sb.AppendFormat("Managed allocations (KB/frame), total {0:F1}:\n", bytes.Sum() / 1024.0 / frames);
                foreach (int i in Enumerable.Range(0, Labels.Count).Where(i => bytes[i] > 0).OrderByDescending(i => bytes[i]).Take(top))
                    sb.AppendFormat("  {0,8:F1}  {1}\n", bytes[i] / 1024.0 / frames, Labels[i]);
            }
            return sb.ToString();
        }

        public static void Reset()
        {
            Array.Clear(ticks, 0, ticks.Length);
            Array.Clear(calls, 0, calls.Length);
            Array.Clear(bytes, 0, bytes.Length);
        }
    }
}
