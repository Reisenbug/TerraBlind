using System.Reflection;
using Terraria.ModLoader;

namespace TerraBlind
{
    // DragonLens integration: comment out this entire class when not debugging
    public static class FreezeSystem
    {
        private static FieldInfo _pausedField;
        private static bool _resolved;

        private static FieldInfo _stepReadyField;

        private static FieldInfo StepReadyField
        {
            get
            {
                if (!_resolved) { _ = PausedField; }
                if (_stepReadyField != null) return _stepReadyField;
                try
                {
                    if (!ModLoader.TryGetMod("DragonLens", out var dl)) return null;
                    var t = dl.Code.GetType("DragonLens.Content.Tools.Developer.FrameAdvanceSystem");
                    _stepReadyField = t?.GetField("stepReady", BindingFlags.Public | BindingFlags.Static);
                }
                catch { }
                return _stepReadyField;
            }
        }

        internal static FieldInfo PausedField
        {
            get
            {
                if (_resolved) return _pausedField;
                _resolved = true;
                try
                {
                    if (!ModLoader.TryGetMod("DragonLens", out var dl)) return null;
                    var t = dl.Code.GetType("DragonLens.Content.Tools.Developer.FrameAdvanceSystem");
                    _pausedField = t?.GetField("paused", BindingFlags.Public | BindingFlags.Static);
                }
                catch { }
                return _pausedField;
            }
        }

        public static bool IsAvailable => PausedField != null;

        public static bool IsFrozen
        {
            get
            {
                var f = PausedField;
                return f != null && (bool)f.GetValue(null);
            }
        }

        public static bool Freeze()
        {
            var f = PausedField;
            if (f == null) return false;
            f.SetValue(null, true);
            return true;
        }

        public static bool Unfreeze()
        {
            var f = PausedField;
            if (f == null) return false;
            f.SetValue(null, false);
            return true;
        }

        public static bool StepFrame()
        {
            var sf = StepReadyField;
            if (sf == null) return false;
            sf.SetValue(null, true);
            return true;
        }
    }
}
