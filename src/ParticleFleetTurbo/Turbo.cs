using System.Collections.Generic;

namespace ParticleFleetTurbo
{
    /// <summary>Global switch for the engine optimizations, so they can be A/B tested live (F10).</summary>
    internal static class Turbo
    {
        public static bool Enabled;

        public static void Set(bool enabled)
        {
            Enabled = enabled;
            CoresWithVarCache.Invalidate();
        }

        private static readonly Dictionary<string, string> UpperCache = new Dictionary<string, string>();

        /// <summary>Memoized <c>s.ToUpper()</c>; the engine upper-cases image slot names on every image call.</summary>
        public static string ToUpper(string s)
        {
            if (UpperCache.TryGetValue(s, out string upper))
                return upper;
            if (UpperCache.Count >= 8192)
                UpperCache.Clear();
            upper = s.ToUpper();
            UpperCache[s] = upper;
            return upper;
        }
    }
}
