using Microsoft.Xna.Framework;
using Terraria;

namespace TerraBlind
{
    // Receding-horizon nav (MVP, branch experiment). Instead of planning the whole route open-loop, each cycle plans
    // only a SHORT window from the player's REAL position toward the final goal (big field as h, tiny exp budget →
    // returns the furthest standable cell it reached = the window), executes it, then re-plans from the new real
    // position. Drift can't accumulate (every window starts from reality); no切片, no接力 realign needed beyond the
    // one DispatchPlan does. Window A* is still complete within its budget (not greedy) — it can back up locally.
    public static class RecedingNav
    {
        public static bool Active;
        static int _goalWx, _goalWy;
        const float GoalDistPx = 24f;
        const int NoProgressMax = 6;     // distinct actions tried from one cell, all failing to move us → genuinely stuck
        static (int, int) _lastCell;
        static (int, int)? _lastTarget;  // cell the last action aimed at (to blacklist if it didn't move us)
        static bool _haveLast;
        static int _sameCell;

        public static void Toggle()
        {
            if (Active) { Stop(); Main.NewText("[TerraBlind] receding nav OFF"); return; }
            int mx = (int)((Main.mouseX + Main.screenPosition.X) / 16f);
            int my = (int)((Main.mouseY + Main.screenPosition.Y) / 16f);
            Start(mx, my);
        }

        public static void Start(int goalWx, int goalWy)
        {
            StateSpacePlanner.StopNav();
            _goalWx = goalWx; _goalWy = goalWy; Active = true; _sameCell = 0; _haveLast = false; _lastTarget = null; _lastCell = (int.MinValue, int.MinValue);
            MazeWand.GetField(goalWx, goalWy);   // warm the cached big field (h source + heatmap)
            RecedingVis.SetField(goalWx, goalWy);
            DiagLog.Write($"[recede] start goal=({goalWx},{goalWy})");
            Main.NewText($"[TerraBlind] receding nav → ({goalWx},{goalWy})");
        }

        public static void Stop()
        {
            Active = false;
            StateSpacePlanner.StopNav();
            RecedingVis.Clear();
        }

        // per-frame driver (called from SetControls). When the current window finishes (or none running), plan the
        // next window from the real position and dispatch it. The existing TickSteps/ApplyControls execute it.
        public static void Tick()
        {
            if (!Active) return;
            var p = Main.LocalPlayer;
            if (p == null || !p.active) { Stop(); return; }

            float gx = _goalWx * 16f + 8f, gy = (_goalWy + 1) * 16f;
            float cx = p.Center.X, fy = p.position.Y + p.height;
            if (System.Math.Abs(cx - gx) <= GoalDistPx && System.Math.Abs(fy - gy) <= GoalDistPx)
            { DiagLog.Write("[recede] reached goal"); Stop(); Main.NewText("[TerraBlind] receding nav done"); return; }

            if (StateSpacePlanner.ExecRunning) return;        // current action still executing
            if (p.velocity.Y != 0f) return;                   // wait until landed + settled

            // 撞墙感知: did the last action actually move us off the cell we issued it from? If we're still on (roughly)
            // the same cell, that action hit a wall / couldn't execute → blacklist its target this round so we pick a
            // DIFFERENT action instead of re-choosing the same dead one. If we did move, clear the block.
            var cell = ((int)(cx / 16f), (int)(fy / 16f));
            (int, int)? blocked = null;
            if (_haveLast && cell == _lastCell && _lastTarget.HasValue)
            {
                blocked = _lastTarget;
                _sameCell++;
            }
            else _sameCell = 0;
            if (_sameCell >= NoProgressMax)   // tried several different actions, still stuck on this cell → genuinely stuck
            { DiagLog.Write($"[recede] no progress at {cell} after {_sameCell} tries → stop"); Stop(); Main.NewText("[TerraBlind] receding: stuck"); return; }

            // ONE action that best descends the field, skipping the just-blocked target.
            var res = StateSpacePlanner.StepAlongField(_goalWx, _goalWy, blocked);
            if (res == null || res.Steps.Count == 0)
            { DiagLog.Write("[recede] no descending action → stop"); Stop(); Main.NewText("[TerraBlind] receding: stuck"); return; }
            _lastCell = cell; _lastTarget = (res.GoalWx, res.GoalWy); _haveLast = true;
            StateSpacePlanner.DispatchPlan(res);
        }
    }
}
