using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace ParticleFleetTurbo
{
    /// <summary>
    /// Scripts commonly re-apply the same image, position, scale, rotation and colour to custom unit images every
    /// frame. Each of those calls is expensive in the engine (regex, ToUpper, allocations, mesh/transform writes),
    /// so these patches skip work whose result would be identical.
    /// </summary>
    [HarmonyPatch(typeof(CommonUnit), nameof(CommonUnit.SetImage))]
    internal static class SetImagePatch
    {
        private static bool Prefix(CommonUnit __instance, string image, string slot, ref CommonUnit.ImageData __result,
            Dictionary<string, CommonUnit.ImageData> ___subImages)
        {
            // Over 1023 images the engine refuses every call, even for existing slots; let it.
            if (!Turbo.Enabled || image == null || ___subImages == null || ___subImages.Count > 1023)
                return true;
            string key = string.IsNullOrEmpty(slot) ? "MAIN" : Turbo.ToUpper(slot);
            if (!___subImages.TryGetValue(key, out CommonUnit.ImageData existing) || existing.image != image)
                return true;

            if (Turbo.ToUpper(image) == "NONE")
            {
                if (existing.go != null)
                    return true;
                __result = existing;
                return false;
            }
            if (existing.go == null)
                return true;
            // Same image already in the slot: UVs and material would be re-applied unchanged.
            bool visible = __instance.visible;
            if (existing.go.activeSelf != visible)
                existing.go.SetActive(visible);
            __result = existing;
            return false;
        }
    }

    [HarmonyPatch(typeof(CommonUnit), nameof(CommonUnit.IsAlphaNum))]
    internal static class IsAlphaNumPatch
    {
        // Equivalent to Regex.IsMatch(s, "^[a-zA-Z0-9_]+$"); in .NET '$' also matches before a final '\n'.
        private static bool Prefix(string s, ref bool __result)
        {
            if (!Turbo.Enabled || s == null)
                return true;
            int end = s.Length > 0 && s[s.Length - 1] == '\n' ? s.Length - 1 : s.Length;
            bool ok = end > 0;
            for (int i = 0; ok && i < end; i++)
            {
                char c = s[i];
                ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';
            }
            __result = ok;
            return false;
        }
    }

    [HarmonyPatch(typeof(CommonUnit), nameof(CommonUnit.GetImageData))]
    internal static class GetImageDataPatch
    {
        private static bool Prefix(string slot, ref CommonUnit.ImageData __result, Dictionary<string, CommonUnit.ImageData> ___subImages)
        {
            if (!Turbo.Enabled || slot == null)
                return true;
            if (___subImages == null || !___subImages.TryGetValue(Turbo.ToUpper(slot), out CommonUnit.ImageData data) || data.go == null)
                __result = null;
            else
                __result = data;
            return false;
        }
    }

    [HarmonyPatch(typeof(CustomImageManager), nameof(CustomImageManager.SetColor))]
    internal static class SetColorPatch
    {
        // _color starts as the colour Awake writes to the mesh and is updated on every SetColor, so it always
        // matches the mesh.
        private static bool Prefix(Color color, Color ____color)
        {
            return !Turbo.Enabled || !TransformWrites.Same(color, ____color);
        }
    }

    /// <summary>Skips transform writes that would not change anything.</summary>
    internal static class TransformWrites
    {
        public static bool Same(Vector3 a, Vector3 b) => a.x == b.x && a.y == b.y && a.z == b.z;
        public static bool Same(Color a, Color b) => a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;
        private static bool Same(Quaternion a, Quaternion b) => a.x == b.x && a.y == b.y && a.z == b.z && a.w == b.w;

        public static void SetLocalPosition(Transform t, Vector3 v)
        {
            if (!Turbo.Enabled || !Same(t.localPosition, v))
                t.localPosition = v;
        }

        public static void SetLocalScale(Transform t, Vector3 v)
        {
            if (!Turbo.Enabled || !Same(t.localScale, v))
                t.localScale = v;
        }

        public static void SetEulerAngles(Transform t, Vector3 v)
        {
            // The eulerAngles setter stores rotation = Quaternion.Euler(v); compare in that form.
            if (!Turbo.Enabled || !Same(t.rotation, Quaternion.Euler(v)))
                t.eulerAngles = v;
        }
    }

    [HarmonyPatch]
    internal static class TransformWritePatch
    {
        private static readonly Dictionary<MethodInfo, MethodInfo> Replacements = new Dictionary<MethodInfo, MethodInfo>
        {
            [AccessTools.PropertySetter(typeof(Transform), nameof(Transform.localPosition))] =
                AccessTools.Method(typeof(TransformWrites), nameof(TransformWrites.SetLocalPosition)),
            [AccessTools.PropertySetter(typeof(Transform), nameof(Transform.localScale))] =
                AccessTools.Method(typeof(TransformWrites), nameof(TransformWrites.SetLocalScale)),
            [AccessTools.PropertySetter(typeof(Transform), nameof(Transform.eulerAngles))] =
                AccessTools.Method(typeof(TransformWrites), nameof(TransformWrites.SetEulerAngles)),
        };

        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(CrplCore), "ProcessCommands");
            yield return AccessTools.Method(typeof(CommonUnit), nameof(CommonUnit.SetScale));
            yield return AccessTools.Method(typeof(CommonUnit), nameof(CommonUnit.SetScaleX));
            yield return AccessTools.Method(typeof(CommonUnit), nameof(CommonUnit.SetScaleY));
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var instruction in instructions)
            {
                if ((instruction.opcode == OpCodes.Callvirt || instruction.opcode == OpCodes.Call) &&
                    instruction.operand is MethodInfo m && Replacements.TryGetValue(m, out MethodInfo replacement))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = replacement;
                }
                yield return instruction;
            }
        }
    }
}
