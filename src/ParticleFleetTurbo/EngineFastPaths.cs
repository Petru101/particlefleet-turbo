using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ParticleFleetTurbo
{
    /// <summary>
    /// SpaceModule's beam target search, same result: the engine scans the cells within range ring by ring and keeps
    /// the first cell with the lowest key (distance² for enemy particles, distance² + 10000 for shootable plasma).
    /// Most ships find nothing, so this scans row by row in a tight loop instead and breaks ties by the cell's
    /// position in the engine's ring order. The cell checks are the engine's (pure) ones.
    /// </summary>
    [HarmonyPatch(typeof(SpaceModule), "FindNearestEnemy")]
    internal static class SpaceModuleTargetPatch
    {
        /// <summary>Developer check: also run the engine's search and count calls where the results differ.</summary>
        public static bool Verify;
        public static long Calls, Mismatches;

        [HarmonyReversePatch]
        [HarmonyPatch(typeof(SpaceModule), "FindNearestEnemy")]
        private static void Original(SpaceModule instance, Transform transform, int R, out int chosenX, out int chosenY) =>
            throw new System.NotImplementedException();

        private static bool Prefix(SpaceModule __instance, Transform transform, int R, out int chosenX, out int chosenY)
        {
            chosenX = -1;
            chosenY = -1;
            if (!Turbo.Enabled)
                return true;
            Find(__instance, transform, R, out chosenX, out chosenY);
            if (Verify)
            {
                Original(__instance, transform, R, out int x, out int y);
                Calls++;
                if (x != chosenX || y != chosenY)
                    Mismatches++;
            }
            return false;
        }

        private static void Find(SpaceModule module, Transform transform, int R, out int chosenX, out int chosenY)
        {
            chosenX = -1;
            chosenY = -1;
            int w = GameSpace.GRID_WIDTH;
            int h = GameSpace.GRID_HEIGHT;
            List<ParticleBase>[] grid = Particles.instance.particleGrid;
            int cx = (int)(transform.localPosition.x / (float)GameSpace.GRID_SIZE);
            int cy = (int)(transform.localPosition.y / (float)GameSpace.GRID_SIZE);
            int[] terrain = GameSpace.instance.plasma.terrainData;
            int[] decay = GameSpace.instance.plasma.terrainDecayData;
            bool enemy = module.enemy;
            SpaceModule.FIRE_MODE mode = module.commandModule.particleBeamsMode;
            bool checkParticles = mode == SpaceModule.FIRE_MODE.ON || mode == SpaceModule.FIRE_MODE.PARTICLES;
            bool checkPlasma = mode == SpaceModule.FIRE_MODE.ON || mode == SpaceModule.FIRE_MODE.PLASMA;
            if (!checkParticles && !checkPlasma)
                return;
            int r2 = R * R;
            int rmax = R - 1;
            int bestKey = int.MaxValue;
            int bestRank = int.MaxValue;
            int y0 = System.Math.Max(cy - rmax, 0);
            int y1 = System.Math.Min(cy + rmax, h - 1);
            for (int y = y0; y <= y1; y++)
            {
                int dy = y - cy;
                int rem = r2 - dy * dy;
                if (rem < 0)
                    continue;
                int dxMax = (int)System.Math.Sqrt(rem);
                while (dxMax * dxMax > rem)
                    dxMax--;
                while ((dxMax + 1) * (dxMax + 1) <= rem)
                    dxMax++;
                if (dxMax > rmax)
                    dxMax = rmax;
                int x0 = System.Math.Max(cx - dxMax, 0);
                int x1 = System.Math.Min(cx + dxMax, w - 1);
                int row = y * w;
                for (int x = x0; x <= x1; x++)
                {
                    int idx = row + x;
                    bool plasmaHit = false;
                    if (checkPlasma)
                    {
                        int t = terrain[idx];
                        plasmaHit = t != 0 && ((t <= 10 && enemy) || (t > 10 && !enemy) || decay[idx] > 0);
                    }
                    List<ParticleBase> list = null;
                    if (checkParticles)
                    {
                        list = grid[idx];
                        if (list.Count == 0)
                            list = null;
                    }
                    if (!plasmaHit && list == null)
                        continue;
                    int dx = x - cx;
                    if (dx == 0 && dy == 0)
                        continue;
                    int d = dx * dx + dy * dy;
                    int rank = Rank(dx, dy, R);
                    if (list != null && Better(d, rank, bestKey, bestRank) && ContainsEnemyParticles(list, !enemy))
                    {
                        bestKey = d;
                        bestRank = rank;
                        chosenX = x;
                        chosenY = y;
                    }
                    else if (plasmaHit && Better(d + 10000, rank, bestKey, bestRank))
                    {
                        bestKey = d + 10000;
                        bestRank = rank;
                        chosenX = x;
                        chosenY = y;
                    }
                }
            }
        }

        private static bool Better(int key, int rank, int bestKey, int bestRank) => key < bestKey || (key == bestKey && rank < bestRank);

        // Position in the engine's scan: ring i = max(|dx|, |dy|); within a ring first the top/bottom rows (corners
        // included) by x, top before bottom, then the side columns by y, left before right.
        private static int Rank(int dx, int dy, int R)
        {
            int ax = dx < 0 ? -dx : dx;
            int ay = dy < 0 ? -dy : dy;
            int i = ax > ay ? ax : ay;
            int phase, pos, side;
            if (ay == i)
            {
                phase = 0;
                pos = dx + i;
                side = dy < 0 ? 0 : 1;
            }
            else
            {
                phase = 1;
                pos = dy + i;
                side = dx < 0 ? 0 : 1;
            }
            return ((i * 2 + phase) * (2 * R + 1) + pos) * 2 + side;
        }

        // SpaceModule.ContainsEnemyParticles with untargeted: false.
        private static bool ContainsEnemyParticles(List<ParticleBase> particles, bool lookForEnemy)
        {
            if (particles.Count == 1)
            {
                Particle particle = particles[0] as Particle;
                return particle.enemy == lookForEnemy && particle.slowedCount == 0 && particle.IsMoving(0.1f);
            }
            foreach (Particle particle in particles)
            {
                if (particle.enemy == lookForEnemy && particle.slowedCount == 0 && particle.IsMoving(0.1f))
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Struc.GetNearestStruc and Land.GetNearestCoveredLand walk every 3x3 block in range (841 at the omni shot range)
    /// and call a per-block method that returns at once for an empty block. Same walk, but the empty check is inline
    /// and only occupied blocks call the engine's per-block search, so the result (first nearest in walk order) is
    /// unchanged.
    /// </summary>
    internal static class BlockSearchPatches
    {
        public static long Calls, Mismatches;

        [HarmonyPatch(typeof(Struc), nameof(Struc.GetNearestStruc))]
        internal static class StrucPatch
        {
            [HarmonyReversePatch]
            [HarmonyPatch(typeof(Struc), "GetNearestStrucLarge")]
            private static void Block(Struc instance, int lcx, int lcy, int startX, int startY, int R, bool built, ref int closestD2, ref int X, ref int Y) =>
                throw new System.NotImplementedException();

            [HarmonyReversePatch]
            [HarmonyPatch(typeof(Struc), nameof(Struc.GetNearestStruc))]
            private static void Original(Struc instance, int startX, int startY, int R, bool built, out int X, out int Y) =>
                throw new System.NotImplementedException();

            private static bool Prefix(Struc __instance, int startX, int startY, int R, bool built, out int X, out int Y)
            {
                X = Y = -1;
                if (!Turbo.Enabled)
                    return true;
                int[] large = __instance.strucBuiltLargeData;
                int closest = int.MaxValue, x = int.MaxValue, y = int.MaxValue;
                int n = R / GameSpace.LARGEGRID_CELLSIZE;
                int bx = startX / GameSpace.LARGEGRID_CELLSIZE, by = startY / GameSpace.LARGEGRID_CELLSIZE;
                int w = GameSpace.LARGEGRID_WIDTH, h = GameSpace.LARGEGRID_HEIGHT;
                for (int i = -n; i <= n; i++)
                {
                    int ly = by + i;
                    if (ly < 0 || ly >= h)
                        continue;
                    for (int j = -n; j <= n; j++)
                    {
                        int lx = bx + j;
                        if (lx >= 0 && lx < w && large[lx + ly * w] != 0)
                            Block(__instance, lx, ly, startX, startY, R, built, ref closest, ref x, ref y);
                    }
                }
                if (x != int.MaxValue)
                {
                    X = x;
                    Y = y;
                }
                if (SpaceModuleTargetPatch.Verify)
                {
                    Original(__instance, startX, startY, R, built, out int ox, out int oy);
                    Calls++;
                    if (ox != X || oy != Y)
                        Mismatches++;
                }
                return false;
            }
        }

        [HarmonyPatch(typeof(Land), nameof(Land.GetNearestCoveredLand))]
        internal static class CoveredLandPatch
        {
            [HarmonyReversePatch]
            [HarmonyPatch(typeof(Land), "GetNearestCoveredLandLarge")]
            private static void Block(Land instance, int lcx, int lcy, bool enemy, int startX, int startY, int R, int baseLand, ref int closestD2, ref int X, ref int Y) =>
                throw new System.NotImplementedException();

            [HarmonyReversePatch]
            [HarmonyPatch(typeof(Land), nameof(Land.GetNearestCoveredLand))]
            private static void Original(Land instance, bool enemy, int startX, int startY, int R, int baseLand, out int X, out int Y) =>
                throw new System.NotImplementedException();

            private static bool Prefix(Land __instance, bool enemy, int startX, int startY, int R, int baseLand, out int X, out int Y)
            {
                X = Y = -1;
                if (!Turbo.Enabled)
                    return true;
                int[] large = enemy ? __instance.landCoveredBlueLargeData : __instance.landCoveredRedLargeData;
                int closest = int.MaxValue, x = int.MaxValue, y = int.MaxValue;
                int n = R / GameSpace.LARGEGRID_CELLSIZE;
                int bx = startX / GameSpace.LARGEGRID_CELLSIZE, by = startY / GameSpace.LARGEGRID_CELLSIZE;
                int w = GameSpace.LARGEGRID_WIDTH, h = GameSpace.LARGEGRID_HEIGHT;
                for (int i = -n; i <= n; i++)
                {
                    int ly = by + i;
                    if (ly < 0 || ly >= h)
                        continue;
                    for (int j = -n; j <= n; j++)
                    {
                        int lx = bx + j;
                        if (lx >= 0 && lx < w && large[lx + ly * w] != 0)
                            Block(__instance, lx, ly, enemy, startX, startY, R, baseLand, ref closest, ref x, ref y);
                    }
                }
                if (x != int.MaxValue)
                {
                    X = x;
                    Y = y;
                }
                if (SpaceModuleTargetPatch.Verify)
                {
                    Original(__instance, enemy, startX, startY, R, baseLand, out int ox, out int oy);
                    Calls++;
                    if (ox != X || oy != Y)
                        Mismatches++;
                }
                return false;
            }
        }
    }

    /// <summary>
    /// LatheWeapon.FireAtLandUnit scans every cell in lathe range (~1250) through Unity null checks and GetCovered calls
    /// looking for a friendly land unit under construction; usually there is none. A tight pre-scan of the cheap,
    /// side-effect-free part of its condition skips the engine's scan when no cell can pass it (it would return false
    /// without doing anything).
    /// </summary>
    [HarmonyPatch(typeof(LatheWeapon), "FireAtLandUnit")]
    internal static class LatheLandUnitPatch
    {
        public static long Skipped, Mismatches;

        private static readonly AccessTools.FieldRef<LatheWeapon, float> Ammo = AccessTools.FieldRefAccess<LatheWeapon, float>("ammo");

        private static bool Prefix(LatheWeapon __instance, ref bool __state)
        {
            __state = false;
            if (!Turbo.Enabled)
                return true;
            float ammo = Ammo(__instance);
            if (!(ammo >= __instance.BUILD_RATE && (double)ammo >= 0.15) || AnyCandidate(__instance))
                return true;
            Skipped++;
            __state = true;
            return SpaceModuleTargetPatch.Verify;
        }

        private static void Postfix(bool __result, bool __state)
        {
            if (__state && __result)
                Mismatches++;
        }

        private static bool AnyCandidate(LatheWeapon lathe)
        {
            LandUnitManager[] units = Land.instance.landUnits;
            int[] covered = Land.instance.landCoveredData;
            int w = GameSpace.GRID_WIDTH, h = GameSpace.GRID_HEIGHT;
            int cx = lathe.cellX, cy = lathe.cellY;
            int r = lathe.FIRE_DIST_CELL, r2 = r * r;
            bool enemy = lathe.enemy;
            int y0 = System.Math.Max(cy - r, 0), y1 = System.Math.Min(cy + r, h - 1);
            int x0 = System.Math.Max(cx - r, 0), x1 = System.Math.Min(cx + r, w - 1);
            for (int y = y0; y <= y1; y++)
            {
                int dy = y - cy;
                int row = y * w;
                for (int x = x0; x <= x1; x++)
                {
                    LandUnitManager unit = units[row + x];
                    if ((object)unit == null)
                        continue;
                    int dx = x - cx;
                    if (dx * dx + dy * dy > r2)
                        continue;
                    int c = covered[row + x];
                    if (unit != null && enemy == unit.enemy && unit.isBuilding && unit.latheCanBuild && ((enemy && c >= 0) || (!enemy && c <= 0)))
                        return true;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// Land submeshes re-upload all four vertex arrays (~2 MB) whenever any quad was written, though mire spreading
    /// only changes colors. Track which arrays a write actually changed and upload only those.
    /// </summary>
    internal static class LandSubmeshUploads
    {
        private class Changed
        {
            public bool Vs, Uv, Uv1, Colors;
        }

        public struct Quad
        {
            public Vector3 V0, V1, V2, V3;
            public Vector2 U0, U1, U2, U3;
            public Color32 C0, C1, C2, C3;
        }

        private static readonly AccessTools.FieldRef<LandSubmesh, Vector3[]> Vs = AccessTools.FieldRefAccess<LandSubmesh, Vector3[]>("vs");
        private static readonly AccessTools.FieldRef<LandSubmesh, Vector2[]> Uv = AccessTools.FieldRefAccess<LandSubmesh, Vector2[]>("uv");
        private static readonly AccessTools.FieldRef<LandSubmesh, Vector2[]> Uv1 = AccessTools.FieldRefAccess<LandSubmesh, Vector2[]>("uv1");
        private static readonly AccessTools.FieldRef<LandSubmesh, Color32[]> Colors = AccessTools.FieldRefAccess<LandSubmesh, Color32[]>("colors");
        private static readonly AccessTools.FieldRef<LandSubmesh, Mesh> Lmesh = AccessTools.FieldRefAccess<LandSubmesh, Mesh>("lmesh");

        private static readonly Dictionary<int, Changed> ChangedBySubmesh = new Dictionary<int, Changed>();

        private static Changed For(LandSubmesh submesh)
        {
            int id = submesh.GetInstanceID();
            if (!ChangedBySubmesh.TryGetValue(id, out Changed changed))
                ChangedBySubmesh[id] = changed = new Changed();
            return changed;
        }

        private static bool Same(Vector3 a, Vector3 b) => a.x == b.x && a.y == b.y && a.z == b.z;
        private static bool Same(Vector2 a, Vector2 b) => a.x == b.x && a.y == b.y;
        private static bool Same(Color32 a, Color32 b) => a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;

        [HarmonyPatch(typeof(LandSubmesh), nameof(LandSubmesh.SetQuadNEW))]
        internal static class SetQuadPatch
        {
            private static void Prefix(LandSubmesh __instance, int i, int j, out Quad __state)
            {
                int n = (i + j * 126) * 4;
                Vector3[] vs = Vs(__instance);
                Vector2[] uv = Uv(__instance);
                Color32[] colors = Colors(__instance);
                __state = new Quad
                {
                    V0 = vs[n], V1 = vs[n + 1], V2 = vs[n + 2], V3 = vs[n + 3],
                    U0 = uv[n], U1 = uv[n + 1], U2 = uv[n + 2], U3 = uv[n + 3],
                    C0 = colors[n], C1 = colors[n + 1], C2 = colors[n + 2], C3 = colors[n + 3],
                };
            }

            private static void Postfix(LandSubmesh __instance, int i, int j, Quad __state)
            {
                int n = (i + j * 126) * 4;
                Vector3[] vs = Vs(__instance);
                Vector2[] uv = Uv(__instance);
                Color32[] colors = Colors(__instance);
                Changed changed = For(__instance);
                if (!(Same(__state.V0, vs[n]) && Same(__state.V1, vs[n + 1]) && Same(__state.V2, vs[n + 2]) && Same(__state.V3, vs[n + 3])))
                    changed.Vs = true;
                if (!(Same(__state.U0, uv[n]) && Same(__state.U1, uv[n + 1]) && Same(__state.U2, uv[n + 2]) && Same(__state.U3, uv[n + 3])))
                    changed.Uv = true;
                if (!(Same(__state.C0, colors[n]) && Same(__state.C1, colors[n + 1]) && Same(__state.C2, colors[n + 2]) && Same(__state.C3, colors[n + 3])))
                    changed.Colors = true;
            }
        }

        [HarmonyPatch(typeof(LandSubmesh), nameof(LandSubmesh.UpdateNormalTexture))]
        internal static class NormalTexturePatch
        {
            private static void Prefix(LandSubmesh __instance, int x, int y, out Quad __state)
            {
                int n = (x + y * 126) * 4;
                Vector2[] uv1 = Uv1(__instance);
                __state = new Quad { U0 = uv1[n], U1 = uv1[n + 1], U2 = uv1[n + 2], U3 = uv1[n + 3] };
            }

            private static void Postfix(LandSubmesh __instance, int x, int y, Quad __state)
            {
                int n = (x + y * 126) * 4;
                Vector2[] uv1 = Uv1(__instance);
                if (!(Same(__state.U0, uv1[n]) && Same(__state.U1, uv1[n + 1]) && Same(__state.U2, uv1[n + 2]) && Same(__state.U3, uv1[n + 3])))
                    For(__instance).Uv1 = true;
            }
        }

        [HarmonyPatch(typeof(LandSubmesh), nameof(LandSubmesh.UpdateMesh))]
        internal static class UpdateMeshPatch
        {
            private static bool Prefix(LandSubmesh __instance)
            {
                Changed changed = For(__instance);
                if (!Turbo.Enabled || !__instance.dirty)
                {
                    changed.Vs = changed.Uv = changed.Uv1 = changed.Colors = false;
                    return true;
                }
                Mesh mesh = Lmesh(__instance);
                if (changed.Vs)
                    mesh.vertices = Vs(__instance);
                if (changed.Uv)
                    mesh.uv = Uv(__instance);
                if (changed.Uv1)
                    mesh.uv2 = Uv1(__instance);
                if (changed.Colors)
                    mesh.colors32 = Colors(__instance);
                changed.Vs = changed.Uv = changed.Uv1 = changed.Colors = false;
                __instance.dirty = false;
                return false;
            }
        }
    }

    /// <summary>
    /// Plasma submeshes re-upload vertices and colors whenever a quad was written, though most writes store what was
    /// already there. Track which arrays a write actually changed and upload only those.
    /// </summary>
    internal static class PlasmaSubmeshUploads
    {
        private class Changed
        {
            public bool Vs, Colors;
        }

        private static readonly AccessTools.FieldRef<PlasmaSubmesh, Vector3[]> Vs = AccessTools.FieldRefAccess<PlasmaSubmesh, Vector3[]>("vs");
        private static readonly AccessTools.FieldRef<PlasmaSubmesh, Color32[]> Colors = AccessTools.FieldRefAccess<PlasmaSubmesh, Color32[]>("colors");
        private static readonly AccessTools.FieldRef<PlasmaSubmesh, Mesh> Lmesh = AccessTools.FieldRefAccess<PlasmaSubmesh, Mesh>("lmesh");

        private static readonly Dictionary<int, Changed> ChangedBySubmesh = new Dictionary<int, Changed>();

        private static Changed For(PlasmaSubmesh submesh)
        {
            int id = submesh.GetInstanceID();
            if (!ChangedBySubmesh.TryGetValue(id, out Changed changed))
                ChangedBySubmesh[id] = changed = new Changed();
            return changed;
        }

        private static void Before(PlasmaSubmesh submesh, int i, int j, out LandSubmeshUploads.Quad state)
        {
            int n = (i + j * 126) * 4;
            Vector3[] vs = Vs(submesh);
            Color32[] colors = Colors(submesh);
            state = new LandSubmeshUploads.Quad
            {
                V0 = vs[n], V1 = vs[n + 1], V2 = vs[n + 2], V3 = vs[n + 3],
                C0 = colors[n], C1 = colors[n + 1], C2 = colors[n + 2], C3 = colors[n + 3],
            };
        }

        private static void After(PlasmaSubmesh submesh, int i, int j, LandSubmeshUploads.Quad state)
        {
            int n = (i + j * 126) * 4;
            Vector3[] vs = Vs(submesh);
            Color32[] colors = Colors(submesh);
            Changed changed = For(submesh);
            if (!(Same(state.V0, vs[n]) && Same(state.V1, vs[n + 1]) && Same(state.V2, vs[n + 2]) && Same(state.V3, vs[n + 3])))
                changed.Vs = true;
            if (!(Same(state.C0, colors[n]) && Same(state.C1, colors[n + 1]) && Same(state.C2, colors[n + 2]) && Same(state.C3, colors[n + 3])))
                changed.Colors = true;
        }

        private static bool Same(Vector3 a, Vector3 b) => a.x == b.x && a.y == b.y && a.z == b.z;
        private static bool Same(Color32 a, Color32 b) => a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;

        [HarmonyPatch(typeof(PlasmaSubmesh), nameof(PlasmaSubmesh.SetQuadNEW))]
        internal static class SetQuadNewPatch
        {
            private static void Prefix(PlasmaSubmesh __instance, int i, int j, out LandSubmeshUploads.Quad __state) => Before(__instance, i, j, out __state);
            private static void Postfix(PlasmaSubmesh __instance, int i, int j, LandSubmeshUploads.Quad __state) => After(__instance, i, j, __state);
        }

        [HarmonyPatch(typeof(PlasmaSubmesh), nameof(PlasmaSubmesh.SetQuad))]
        internal static class SetQuadPatch
        {
            private static void Prefix(PlasmaSubmesh __instance, int i, int j, out LandSubmeshUploads.Quad __state) => Before(__instance, i, j, out __state);
            private static void Postfix(PlasmaSubmesh __instance, int i, int j, LandSubmeshUploads.Quad __state) => After(__instance, i, j, __state);
        }

        [HarmonyPatch(typeof(PlasmaSubmesh), nameof(PlasmaSubmesh.UpdateMesh))]
        internal static class UpdateMeshPatch
        {
            private static bool Prefix(PlasmaSubmesh __instance)
            {
                Changed changed = For(__instance);
                if (!Turbo.Enabled || !__instance.dirty)
                {
                    changed.Vs = changed.Colors = false;
                    return true;
                }
                Mesh mesh = Lmesh(__instance);
                if (changed.Vs)
                    mesh.vertices = Vs(__instance);
                if (changed.Colors)
                    mesh.colors32 = Colors(__instance);
                changed.Vs = changed.Colors = false;
                __instance.dirty = false;
                return false;
            }
        }
    }
}
