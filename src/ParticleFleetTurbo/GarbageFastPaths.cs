using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace ParticleFleetTurbo
{
    /// <summary>
    /// VirtualParticle.SetPosition runs for every hull cell of every ship every tick and copies its grid cell's list
    /// (which at least holds the particle itself) to an array before looking for an enemy ship's hull to collide
    /// with. That was ~400 KB of garbage a frame on map #1990, a 40 ms collection every 6 s. The copy only protects
    /// the loop from a collision changing the list, so when no entry can collide the loop gets an empty array.
    /// </summary>
    [HarmonyPatch(typeof(VirtualParticle), nameof(VirtualParticle.SetPosition))]
    internal static class VirtualParticleCopyPatch
    {
        private static readonly ParticleBase[] Empty = new ParticleBase[0];

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var toArray = AccessTools.Method(typeof(List<ParticleBase>), nameof(List<ParticleBase>.ToArray));
            var snapshot = AccessTools.Method(typeof(VirtualParticleCopyPatch), nameof(Snapshot));
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(toArray))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(instruction);
                    yield return new CodeInstruction(OpCodes.Call, snapshot);
                }
                else
                    yield return instruction;
            }
        }

        private static ParticleBase[] Snapshot(List<ParticleBase> list, VirtualParticle self)
        {
            if (!Turbo.Enabled)
                return list.ToArray();
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] is VirtualParticle other && other.enemy != self.enemy && other.holder is HullSection)
                    return list.ToArray();
            }
            return Empty;
        }
    }

    /// <summary>
    /// ParticleSystem.Simulate allocates a closure, a delegate and (with children) a transform enumerator around one
    /// native call, for every running effect every tick. An effect without child transforms has nothing else to
    /// simulate, so it makes that native call directly.
    /// </summary>
    [HarmonyPatch(typeof(ParticleSystemManager), nameof(ParticleSystemManager.GameUpdate))]
    internal static class EffectSimulatePatch
    {
        private delegate bool InternalSimulate(ParticleSystem self, float t, bool restart);

        private static readonly InternalSimulate Native = (InternalSimulate)System.Delegate.CreateDelegate(typeof(InternalSimulate),
            AccessTools.Method(typeof(ParticleSystem), "Internal_Simulate"));

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var simulate = AccessTools.Method(typeof(ParticleSystem), nameof(ParticleSystem.Simulate), new[] { typeof(float), typeof(bool), typeof(bool) });
            var replacement = AccessTools.Method(typeof(EffectSimulatePatch), nameof(Simulate));
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(simulate))
                    yield return new CodeInstruction(OpCodes.Call, replacement).MoveLabelsFrom(instruction);
                else
                    yield return instruction;
            }
        }

        private static void Simulate(ParticleSystem ps, float t, bool withChildren, bool restart)
        {
            if (Turbo.Enabled && ps.transform.childCount == 0)
                Native(ps, t, restart);
            else
                ps.Simulate(t, withChildren, restart);
        }
    }

    /// <summary>
    /// ShipPlan.ScanHull walks a ship's hull every tick with a new grid, list and queue (which regrows as it fills).
    /// Its only caller, CommandModule.BuildHull, drops them before the next scan, so one set of buffers is reused.
    /// </summary>
    [HarmonyPatch(typeof(ShipPlan), nameof(ShipPlan.ScanHull))]
    internal static class HullScanBuffersPatch
    {
        private static readonly List<HullSection> List = new List<HullSection>();
        private static readonly Queue<HullSection> Queue = new Queue<HullSection>();
        private static readonly Dictionary<long, bool[,]> Grids = new Dictionary<long, bool[,]>();

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction instruction in instructions)
            {
                string replacement = null;
                if (instruction.opcode == OpCodes.Newobj && instruction.operand is ConstructorInfo ctor)
                {
                    if (ctor.DeclaringType == typeof(bool[,]))
                        replacement = nameof(NewGrid);
                    else if (ctor.DeclaringType == typeof(List<HullSection>))
                        replacement = nameof(NewList);
                    else if (ctor.DeclaringType == typeof(Queue<HullSection>))
                        replacement = nameof(NewQueue);
                }
                if (replacement != null)
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(HullScanBuffersPatch), replacement)).MoveLabelsFrom(instruction);
                else
                    yield return instruction;
            }
        }

        private static bool[,] NewGrid(int width, int height)
        {
            if (!Turbo.Enabled)
                return new bool[width, height];
            long key = ((long)width << 32) | (uint)height;
            if (Grids.TryGetValue(key, out bool[,] grid))
                System.Array.Clear(grid, 0, grid.Length);
            else
                Grids[key] = grid = new bool[width, height];
            return grid;
        }

        private static List<HullSection> NewList()
        {
            if (!Turbo.Enabled)
                return new List<HullSection>();
            List.Clear();
            return List;
        }

        private static Queue<HullSection> NewQueue()
        {
            if (!Turbo.Enabled)
                return new Queue<HullSection>();
            Queue.Clear();
            return Queue;
        }
    }
}
