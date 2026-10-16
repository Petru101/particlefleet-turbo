using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ParticleFleetTurbo
{
    /// <summary>
    /// Caches GetCoresWithVar results. The engine scans every active script core and allocates a new set per call;
    /// maps that look up their "network" every frame call it hundreds of times per frame with the same arguments.
    /// A cached result is reused only while no script has written that variable name and the set of active cores
    /// is unchanged, so scripts see exactly what a fresh scan would return, in the same order.
    /// </summary>
    internal static class CoresWithVarCache
    {
        private class Entry
        {
            public HashSet<ICoreHolder> Result;
            public int NameVersion;
            public int GlobalVersion;
        }

        private static int globalVersion;
        // Only names that have been queried are tracked, so writes to other variables cost one failed lookup.
        private static readonly Dictionary<string, int> NameVersions = new Dictionary<string, int>();
        private static readonly Dictionary<string, Entry> Cache = new Dictionary<string, Entry>();

        public static void Invalidate()
        {
            globalVersion++;
            Cache.Clear();
        }

        private static void Touch(string name)
        {
            if (name != null && NameVersions.TryGetValue(name, out int version))
                NameVersions[name] = version + 1;
        }

        public static void HeapSet(Dictionary<string, CrplCore.Data> heap, string key, CrplCore.Data value)
        {
            heap[key] = value;
            Touch(key);
        }

        public static void HeapAdd(Dictionary<string, CrplCore.Data> heap, string key, CrplCore.Data value)
        {
            heap.Add(key, value);
            Touch(key);
        }

        public static bool HeapRemove(Dictionary<string, CrplCore.Data> heap, string key)
        {
            Touch(key);
            return heap.Remove(key);
        }

        public static void HeapClear(Dictionary<string, CrplCore.Data> heap)
        {
            globalVersion++;
            heap.Clear();
        }

        public static void CoreSetChanged() => globalVersion++;

        private static string Key(string varName, CrplCore.Data d)
        {
            switch (d.GetType())
            {
                case CrplCore.Data.TYPE.INT:
                    return varName + "\u0001i" + d.int_val;
                case CrplCore.Data.TYPE.FLOAT:
                    return varName + "\u0001f" + BitConverter.ToInt32(BitConverter.GetBytes(d.float_val), 0);
                case CrplCore.Data.TYPE.STRING:
                    return varName + "\u0001s" + d.string_val;
                case CrplCore.Data.TYPE.NULL:
                    return varName + "\u0001n";
                default:
                    return null;
            }
        }

        /// <summary>Replacement for CrplCore.GetCrplCoreHoldersWithVar; returns false when it produced the result.</summary>
        public static bool TryGet(string varName, CrplCore.Data d, out HashSet<ICoreHolder> result)
        {
            result = null;
            if (!Turbo.Enabled || varName == null || d == null)
                return false;
            string key = Key(varName, d);
            if (key == null)
                return false;

            if (!NameVersions.TryGetValue(varName, out int nameVersion))
                NameVersions[varName] = nameVersion = 0;
            if (Cache.TryGetValue(key, out Entry entry) && entry.NameVersion == nameVersion && entry.GlobalVersion == globalVersion)
            {
                result = entry.Result;
                return true;
            }

            // Same scan as the original. Lists can be mutated in place without a heap write, so a result that
            // depended on any list-valued variable is returned but not cached.
            result = new HashSet<ICoreHolder>();
            bool cacheable = true;
            foreach (CrplCore core in GameSpace.instance.activeCrplCores)
            {
                ICoreHolder holder = core.baseUnit;
                if (holder == null)
                    continue;
                CrplCore.Data value = core.GetVar(varName);
                if (value.GetType() == CrplCore.Data.TYPE.LIST)
                    cacheable = false;
                if (value.Equals(d))
                    result.Add(holder);
            }

            if (cacheable)
            {
                if (Cache.Count >= 4096)
                    Cache.Clear();
                Cache[key] = new Entry { Result = result, NameVersion = nameVersion, GlobalVersion = globalVersion };
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(CrplCore), "GetCrplCoreHoldersWithVar")]
    internal static class GetCrplCoreHoldersWithVarPatch
    {
        private static bool Prefix(string varname, CrplCore.Data d, ref HashSet<ICoreHolder> __result)
        {
            if (!CoresWithVarCache.TryGet(varname, d, out HashSet<ICoreHolder> result))
                return true;
            __result = result;
            return false;
        }
    }

    [HarmonyPatch(typeof(CrplCore), nameof(CrplCore.baseUnit), MethodType.Setter)]
    internal static class BaseUnitSetterPatch
    {
        private static void Postfix() => CoresWithVarCache.CoreSetChanged();
    }

    /// <summary>Routes every write to a script's variable table through <see cref="CoresWithVarCache"/>.</summary>
    [HarmonyPatch]
    internal static class HeapWritePatch
    {
        private static readonly Type HeapType = typeof(Dictionary<string, CrplCore.Data>);

        private static readonly Dictionary<string, MethodInfo> Replacements = new Dictionary<string, MethodInfo>
        {
            ["set_Item"] = AccessTools.Method(typeof(CoresWithVarCache), nameof(CoresWithVarCache.HeapSet)),
            ["Add"] = AccessTools.Method(typeof(CoresWithVarCache), nameof(CoresWithVarCache.HeapAdd)),
            ["Remove"] = AccessTools.Method(typeof(CoresWithVarCache), nameof(CoresWithVarCache.HeapRemove)),
            ["Clear"] = AccessTools.Method(typeof(CoresWithVarCache), nameof(CoresWithVarCache.HeapClear)),
        };

        private static MethodInfo ReplacementFor(CodeInstruction instruction)
        {
            if ((instruction.opcode == OpCodes.Callvirt || instruction.opcode == OpCodes.Call) &&
                instruction.operand is MethodInfo m && m.DeclaringType == HeapType &&
                Replacements.TryGetValue(m.Name, out MethodInfo replacement))
                return replacement;
            return null;
        }

        private static IEnumerable<MethodBase> TargetMethods()
        {
            var types = new[] { typeof(CrplCore) }.Concat(typeof(CrplCore).GetNestedTypes(AccessTools.all));
            foreach (Type type in types)
            {
                foreach (MethodBase method in type.GetMethods(AccessTools.allDeclared).Cast<MethodBase>()
                             .Concat(type.GetConstructors(AccessTools.allDeclared).Cast<MethodBase>()))
                {
                    if (method.IsAbstract || method.ContainsGenericParameters)
                        continue;
                    var body = PatchProcessor.GetOriginalInstructions(method);
                    if (body.Any(i => ReplacementFor(i) != null))
                        yield return method;
                }
            }
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var instruction in instructions)
            {
                MethodInfo replacement = ReplacementFor(instruction);
                if (replacement != null)
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = replacement;
                }
                yield return instruction;
            }
        }
    }
}
