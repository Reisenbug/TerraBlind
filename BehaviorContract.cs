using System.Collections.Generic;

namespace TerraBlind
{
    public static class BehaviorContract
    {
        public struct ExitTolerance
        {
            public int Dx, Dy;
        }

        public struct PreCondition
        {
            public bool RequireGrounded;
            public int MaxDistToLaunchPx;
            public int RequireVxBucket;
        }

        public static readonly Dictionary<string, ExitTolerance> ExitTol = new()
        {
            ["move"]        = new ExitTolerance { Dx = 4, Dy = 6 },
            ["jump"]        = new ExitTolerance { Dx = 3, Dy = 2 },
            ["fall"]        = new ExitTolerance { Dx = 2, Dy = 3 },
            ["bridge"]      = new ExitTolerance { Dx = 3, Dy = 2 },
            ["bridge_fall"] = new ExitTolerance { Dx = 3, Dy = 3 },
            ["pillar"]      = new ExitTolerance { Dx = 2, Dy = 4 },
        };

        public static readonly Dictionary<string, PreCondition> PreCond = new()
        {
            ["jump"]   = new PreCondition { RequireGrounded = true, MaxDistToLaunchPx = 16, RequireVxBucket = -2 },
            ["pillar"] = new PreCondition { RequireGrounded = true, MaxDistToLaunchPx = 16, RequireVxBucket = -2 },
            ["bridge"] = new PreCondition { RequireGrounded = true, MaxDistToLaunchPx = -1, RequireVxBucket = -2 },
            ["fall"]   = new PreCondition { RequireGrounded = true, MaxDistToLaunchPx = -1, RequireVxBucket = -2 },
            ["move"]   = new PreCondition { RequireGrounded = true, MaxDistToLaunchPx = -1, RequireVxBucket = -2 },
        };

        public static bool Deviated(string action, int dx, int dy)
        {
            if (!ExitTol.TryGetValue(action, out var tol)) return false;
            return System.Math.Abs(dx) > tol.Dx || System.Math.Abs(dy) > tol.Dy;
        }

        public static (bool passed, string reason, string severity) CheckPrecondition(
            string action, bool grounded, float distToLaunchPx, int vxBucket)
        {
            if (!PreCond.TryGetValue(action, out var pc)) return (true, null, null);

            if (pc.RequireGrounded && !grounded)
                return (false, "not_grounded", "abort");

            if (pc.MaxDistToLaunchPx >= 0 && System.Math.Abs(distToLaunchPx) > 32)
                return (false, $"dist_far:{distToLaunchPx.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}", "abort");

            if (pc.MaxDistToLaunchPx >= 0 && System.Math.Abs(distToLaunchPx) > pc.MaxDistToLaunchPx)
                return (false, $"dist_warn:{distToLaunchPx.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}", "warn");

            if (pc.RequireVxBucket > -2 && vxBucket != pc.RequireVxBucket)
                return (false, $"vx_bucket:{vxBucket}", "warn");

            return (true, null, null);
        }

        public static int VxBucket(float vx)
        {
            if (vx >= 2.0f) return 1;
            if (vx <= -2.0f) return -1;
            return 0;
        }
    }
}
