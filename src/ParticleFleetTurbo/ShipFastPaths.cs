using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ParticleFleetTurbo
{
    /// <summary>
    /// CommandModule.SetAttractionFields adds or removes a ship's pull on ~2000 module-grid cells whenever the ship
    /// moves a cell, computing Atan2, Sqrt, Cos and Sin per cell although the pull depends only on the offset. Same
    /// loop with the per-offset vectors from a table. The table is built with the engine's expressions on first use,
    /// inside a game tick, so the floating-point state matches the engine's.
    /// </summary>
    [HarmonyPatch(typeof(CommandModule), "SetAttractionFields")]
    internal static class AttractionFieldPatch
    {
        public static long Calls, Mismatches;

        private const int N = 25;
        private const int Width = 2 * N;
        private static Vector3[] table;
        private static bool[] inRange;

        private static readonly AccessTools.FieldRef<CommandModule, bool> Deployed = AccessTools.FieldRefAccess<CommandModule, bool>("attractionFieldDeployed");
        private static readonly AccessTools.FieldRef<CommandModule, int> DeployedX = AccessTools.FieldRefAccess<CommandModule, int>("attractionFieldDeployedX");
        private static readonly AccessTools.FieldRef<CommandModule, int> DeployedY = AccessTools.FieldRefAccess<CommandModule, int>("attractionFieldDeployedY");

        [HarmonyReversePatch]
        [HarmonyPatch(typeof(CommandModule), "SetAttractionFields")]
        private static void Original(CommandModule instance, int x, int y, bool deploy) => throw new System.NotImplementedException();

        private static void BuildTable()
        {
            table = new Vector3[(2 * N + 1) * Width];
            inRange = new bool[table.Length];
            int num = N;
            for (int i = -num; i <= num; i++)
            {
                for (int j = -num; j < num; j++)
                {
                    float f = Mathf.Atan2(i, j);
                    float num4 = Mathf.Sqrt(j * j + i * i);
                    if (num4 <= (float)num)
                    {
                        float num5 = 0.6f + (1f - num4 / (float)num) * 0.4f;
                        int k = (i + N) * Width + j + N;
                        table[k] = new Vector3((0f - Mathf.Cos(f)) * 0.03f * num5, (0f - Mathf.Sin(f)) * 0.03f * num5, 0f);
                        inRange[k] = true;
                    }
                }
            }
        }

        private static bool Prefix(CommandModule __instance, int x, int y, bool deploy)
        {
            if (!Turbo.Enabled)
                return true;
            if (Deployed(__instance) == deploy)
                return false;
            if (table == null)
                BuildTable();
            Vector3[] field = __instance.enemy ? GameSpace.instance.fields.redModuleForceVectorField : GameSpace.instance.fields.blueModuleForceVectorField;
            Vector3[] check = null;
            if (SpaceModuleTargetPatch.Verify)
            {
                check = (Vector3[])field.Clone();
                bool wasDeployed = Deployed(__instance);
                int wasX = DeployedX(__instance), wasY = DeployedY(__instance);
                Original(__instance, x, y, deploy);
                Deployed(__instance) = wasDeployed;
                DeployedX(__instance) = wasX;
                DeployedY(__instance) = wasY;
                Vector3[] engine = field;
                field = check;
                check = engine;
            }
            if (deploy)
            {
                DeployedX(__instance) = x;
                DeployedY(__instance) = y;
            }
            else
            {
                DeployedX(__instance) = -1;
                DeployedY(__instance) = -1;
            }
            Deployed(__instance) = deploy;
            int w = GameSpace.GRID_MODULEGRIDWIDTH, h = GameSpace.GRID_MODULEGRIDHEIGHT;
            for (int i = -N; i <= N; i++)
            {
                int num3 = y + i;
                if (num3 < 0 || num3 >= h)
                    continue;
                for (int j = -N; j < N; j++)
                {
                    int num2 = x + j;
                    int k = (i + N) * Width + j + N;
                    if (num2 < 0 || num2 >= w || !inRange[k])
                        continue;
                    int idx = num2 + num3 * w;
                    Vector3 vector2 = field[idx];
                    if (deploy)
                        vector2 += table[k];
                    else
                        vector2 -= table[k];
                    if ((double)vector2.x > -1E-06 && (double)vector2.x < 1E-06)
                        vector2 = (!((double)vector2.y > -1E-06) || !((double)vector2.y < 1E-06)) ? new Vector3(0f, vector2.y, 0f) : Vector3.zero;
                    else if ((double)vector2.y > -1E-06 && (double)vector2.y < 1E-06)
                        vector2 = new Vector3(vector2.x, 0f, 0f);
                    field[idx] = vector2;
                }
            }
            if (check != null)
            {
                // The engine wrote the real field; ours went to a copy. Compare, keep the engine's.
                Calls++;
                for (int n = 0; n < check.Length; n++)
                {
                    if (check[n].x != field[n].x || check[n].y != field[n].y || check[n].z != field[n].z)
                    {
                        Mismatches++;
                        break;
                    }
                }
            }
            return false;
        }
    }

    /// <summary>
    /// Ships rebuild their weapon range circles (a mesh) every tick a module is built or destroyed, though the
    /// circles are only visible while the ship is selected or hovered. Defer the rebuild while they are hidden and do
    /// it the moment they are shown, from the module list the ship last passed.
    /// </summary>
    internal static class RangeCirclePatches
    {
        private class Pending
        {
            public List<Module> Modules;
            public CommandModule Ship;
        }

        private static readonly Dictionary<CombinedCircles, Pending> Deferred = new Dictionary<CombinedCircles, Pending>();
        private static bool refreshing;

        [HarmonyPatch(typeof(ModuleRangeIndicator), nameof(ModuleRangeIndicator.RefreshModuleRangeIndicators))]
        internal static class RefreshPatch
        {
            private static bool Prefix(ModuleRangeIndicator __instance, List<Module> scannedModules, CommandModule commandModule)
            {
                CombinedCircles circles = __instance.combinedCircles;
                if (refreshing || !Turbo.Enabled || !__instance.dirtyRanges || circles == null || circles.gameObject.activeSelf)
                {
                    if (circles != null)
                        Deferred.Remove(circles);
                    return true;
                }
                if (Deferred.Count > 256 && !Deferred.ContainsKey(circles))
                    ForgetDestroyed();
                Deferred[circles] = new Pending { Modules = scannedModules, Ship = commandModule };
                return false;
            }
        }

        private static void ForgetDestroyed()
        {
            var dead = new List<CombinedCircles>();
            foreach (CombinedCircles circles in Deferred.Keys)
                if (circles == null)
                    dead.Add(circles);
            foreach (CombinedCircles circles in dead)
                Deferred.Remove(circles);
        }

        [HarmonyPatch(typeof(CombinedCircles), nameof(CombinedCircles.Show))]
        internal static class ShowPatch
        {
            private static void Postfix(CombinedCircles __instance)
            {
                if (!__instance.gameObject.activeSelf || !Deferred.TryGetValue(__instance, out Pending pending))
                    return;
                Deferred.Remove(__instance);
                if (pending.Ship == null)
                    return;
                refreshing = true;
                try
                {
                    pending.Ship.moduleRangeIndicators.RefreshModuleRangeIndicators(pending.Modules, pending.Ship);
                }
                finally
                {
                    refreshing = false;
                }
            }
        }
    }

    /// <summary>
    /// Rebuilding a ship's range circles tests each of the ~200 end points of every circle against every other circle
    /// through CombinedCircles.IsWithinRadius: about 6 million checks and 130 ms for ~180 modules, twice whenever a
    /// visible ship gains or loses one. Same answer from per-type arrays, trying the circle that covered the
    /// previous point first (almost every point is covered, and the next one usually by the same circle).
    /// </summary>
    [HarmonyPatch(typeof(CombinedCircles), "IsWithinRadius")]
    internal static class CircleOverlapPatch
    {
        public static bool Verify;
        public static long Calls, Mismatches;

        private class Group
        {
            public readonly List<float> X = new List<float>(), Y = new List<float>(), R = new List<float>();
            public float[] Xs, Ys, Rs;
            public int LastHit;
        }

        private static readonly AccessTools.FieldRef<CombinedCircles, List<CombinedCircles.RangeCircle>> Circles =
            AccessTools.FieldRefAccess<CombinedCircles, List<CombinedCircles.RangeCircle>>("circles");
        private static List<CombinedCircles.RangeCircle> cachedList;
        private static int cachedCount;
        private static Group[] groups = new Group[0];

        [HarmonyReversePatch]
        [HarmonyPatch(typeof(CombinedCircles), "IsWithinRadius")]
        private static bool Original(CombinedCircles instance, float x, float y, CombinedCircles.RangeCircle source) => throw new System.NotImplementedException();

        private static void Build(List<CombinedCircles.RangeCircle> circles)
        {
            cachedList = circles;
            cachedCount = circles.Count;
            System.Array.Clear(groups, 0, groups.Length);
            foreach (CombinedCircles.RangeCircle circle in circles)
            {
                int type = (int)circle.moduleType;
                if (type >= groups.Length)
                    System.Array.Resize(ref groups, type + 1);
                Group group = groups[type] ?? (groups[type] = new Group());
                group.X.Add(circle.x);
                group.Y.Add(circle.y);
                group.R.Add(circle.r);
            }
            foreach (Group group in groups)
            {
                if (group != null)
                {
                    group.Xs = group.X.ToArray();
                    group.Ys = group.Y.ToArray();
                    group.Rs = group.R.ToArray();
                }
            }
        }

        private static bool Fast(float x, float y, CombinedCircles.RangeCircle source)
        {
            Group group = groups[(int)source.moduleType];
            float[] xs = group.Xs, ys = group.Ys, rs = group.Rs;
            float sx = source.x, sy = source.y, sr = source.r;
            int count = xs.Length;
            int hit = group.LastHit;
            for (int n = 0; n < count; n++)
            {
                int i = hit + n;
                if (i >= count)
                    i -= count;
                float cx = xs[i], cy = ys[i], r = rs[i];
                if (cx == sx && cy == sy && r == sr)
                    continue;
                float num = x - cx;
                float num2 = y - cy;
                if (num * num + num2 * num2 <= r * r)
                {
                    group.LastHit = i;
                    return true;
                }
            }
            return false;
        }

        private static bool Prefix(CombinedCircles __instance, float x, float y, CombinedCircles.RangeCircle source, ref bool __result)
        {
            if (!Turbo.Enabled)
                return true;
            List<CombinedCircles.RangeCircle> circles = Circles(__instance);
            if (circles != cachedList || circles.Count != cachedCount)
                Build(circles);
            __result = Fast(x, y, source);
            if (Verify)
            {
                Calls++;
                if (__result != Original(__instance, x, y, source))
                    Mismatches++;
            }
            return false;
        }
    }

    /// <summary>
    /// Every new ship gets a bump texture painted pixel by pixel from its hull plan (ShipPlan.GetBaseHeightTexture:
    /// edge distances, height map, normal map, then a large upload). That took 100-500 ms per spawn on map #743,
    /// whose spawners build the same few ships over and over: a freeze each time. The texture depends only on the
    /// hull plan and nothing changes or destroys it later (the engine never frees them: each spawn left ~10 MB behind),
    /// so ships with the same plan (compared cell by cell) share one. Hulls the map's spawners will build are painted
    /// when the mission loads.
    /// </summary>
    [HarmonyPatch(typeof(ShipPlan), nameof(ShipPlan.GetBaseHeightTexture))]
    internal static class HullTextureCachePatch
    {
        public static bool Verify;
        public static long Hits, Misses, Mismatches;

        private const int MaxEntries = 64;

        private class Entry
        {
            public int[,] Plan;
            public Texture2D Texture;
        }

        private static readonly List<Entry> Entries = new List<Entry>();

        [HarmonyReversePatch]
        [HarmonyPatch(typeof(ShipPlan), nameof(ShipPlan.GetBaseHeightTexture))]
        private static Texture2D Original(ShipPlan instance) => throw new System.NotImplementedException();

        private static GameSpace prewarmedFor;

        /// <summary>
        /// Once per loaded mission (first GameSpace.Update), paints the hulls the map's ship spawners will build, the
        /// way ShipSpawner.CreateShip picks them, so their first spawn doesn't freeze either. Random picks are skipped.
        /// </summary>
        public static void PrewarmSpawners(GameSpace game, BepInEx.Logging.ManualLogSource log)
        {
            if (game == prewarmedFor || !Turbo.Enabled)
                return;
            prewarmedFor = game;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            int painted = 0;
            foreach (ShipSpawner spawner in Object.FindObjectsOfType<ShipSpawner>())
            {
                if (spawner == null || spawner.shipType == null)
                    continue;
                string type = spawner.shipType.ToUpper();
                int[,] plan;
                if (type == "CUSTOM")
                    plan = spawner.bsd?.hullPlan;
                else if (type == "MIRROR" || type == "RANDOM" || type == "RAND_MED" || type == "RAND_SMALL")
                    continue;
                else
                    ShipBrowserPanel.GetShipData(spawner.shipType, out plan, out _, out _);
                if (plan == null || plan.Length == 0 || Find(plan) != null)
                    continue;
                new ShipPlan(plan, 0, 0).GetBaseHeightTexture();
                painted++;
            }
            if (painted > 0)
                log.LogInfo($"Painted {painted} spawner hull textures in {watch.ElapsedMilliseconds} ms");
        }

        private static bool SamePlan(int[,] a, int[,] b)
        {
            if (a.GetLength(0) != b.GetLength(0) || a.GetLength(1) != b.GetLength(1))
                return false;
            int w = a.GetLength(0), h = a.GetLength(1);
            for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                    if (a[x, y] != b[x, y])
                        return false;
            return true;
        }

        private static Entry Find(int[,] plan)
        {
            for (int i = 0; i < Entries.Count; i++)
            {
                Entry entry = Entries[i];
                if (!SamePlan(entry.Plan, plan))
                    continue;
                if (entry.Texture == null)
                {
                    // Destroyed (e.g. unloaded with a scene): paint it again.
                    Entries.RemoveAt(i);
                    return null;
                }
                return entry;
            }
            return null;
        }

        private static bool Prefix(ShipPlan __instance, ref Texture2D __result)
        {
            if (!Turbo.Enabled)
                return true;
            int[,] plan = __instance.hullPlan;
            if (plan == null)
                return true;
            Entry entry = Find(plan);
            if (entry != null)
            {
                Hits++;
                __result = entry.Texture;
                if (Verify)
                {
                    Texture2D fresh = Original(__instance);
                    Color32[] a = fresh.GetPixels32(), b = __result.GetPixels32();
                    bool same = a.Length == b.Length && fresh.width == __result.width && fresh.height == __result.height;
                    for (int k = 0; same && k < a.Length; k++)
                        same = a[k].r == b[k].r && a[k].g == b[k].g && a[k].b == b[k].b && a[k].a == b[k].a;
                    if (!same)
                        Mismatches++;
                    Object.Destroy(fresh);
                }
                return false;
            }
            Misses++;
            __result = Original(__instance);
            if (Entries.Count >= MaxEntries)
                Entries.RemoveAt(0);
            Entries.Add(new Entry { Plan = (int[,])plan.Clone(), Texture = __result });
            return false;
        }
    }
}
