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
        static (int, int)? _lastFrom;    // cell the last edge started FROM (to key the attention mismatch report)
        static (int, int)? _lastTarget;  // cell the last edge planned to land on (compared to the real landing)
        static bool _haveLast;

        public static void Toggle()
        {
            if (Active) { Stop(); Main.NewText("[TerraBlind] receding nav OFF"); return; }
            int mx = (int)((Main.mouseX + Main.screenPosition.X) / 16f);
            int my = (int)((Main.mouseY + Main.screenPosition.Y) / 16f);
            Start(mx, my);
        }

        static volatile bool _fieldReady;
        public static void Start(int goalWx, int goalWy)
        {
            StateSpacePlanner.StopNav();
            goalWy = StateSpacePlanner.SnapGoalToStandable(goalWx, goalWy);   // clicked air → fall to ground (same as navwand)
            _goalWx = goalWx; _goalWy = goalWy; Active = true; _haveLast = false; _lastTarget = null; _lastFrom = null;
            StateSpacePlanner.ResetLineProgress();
            // build the big field (110万格 Dijkstra ≈ 1.5s) OFF the main thread so the keypress doesn't freeze the game.
            // Tick waits on _fieldReady; the player just stands a moment until the compass is built.
            _fieldReady = false;
            int gx = goalWx, gy = goalWy;
            System.Threading.Tasks.Task.Run(() =>
            {
                try { MazeWand.GetField(gx, gy); RecedingVis.SetField(gx, gy); _fieldReady = true; }
                catch (System.Exception e) { DiagLog.Write($"[recede] field build EXC {e.Message}"); _fieldReady = true; }
            });
            DiagLog.Write($"[recede] start goal=({goalWx},{goalWy}) building field off-thread");
            Main.NewText($"[TerraBlind] receding nav → ({goalWx},{goalWy}) (building field…)");
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
            if (!_fieldReady) return;          // field still building off-thread → wait (player stands a moment)
            var p = Main.LocalPlayer;
            if (p == null || !p.active) { Stop(); return; }

            float gx = _goalWx * 16f + 8f, gy = (_goalWy + 1) * 16f;
            float cx = p.Center.X, fy = p.position.Y + p.height;
            if (System.Math.Abs(cx - gx) <= GoalDistPx && System.Math.Abs(fy - gy) <= GoalDistPx)
            { DiagLog.Write("[recede] reached goal"); Stop(); Main.NewText("[TerraBlind] receding nav done"); return; }

            if (StateSpacePlanner.ExecRunning) return;        // current action still executing
            if (p.velocity.Y != 0f) return;                   // wait until landed + settled

            // MUST match StandCell's rounding (the -1f), else cell reads one tile below the planner's landing.
            var cell = ((int)((p.position.X + p.width / 2f) / 16f), (int)((p.position.Y + p.height - 1f) / 16f));
            // ATTENTION feedback: report how the last edge actually turned out (did the real landing reach the cell the
            // edge planned for?). StepAlongField turns that into a continuous per-edge mismatch weight that softly
            // down-weights edges physics keeps failing to honour. Then decay the whole table one cycle so memory fades —
            // no hard blacklist, no backtrack ban; a penalized edge always recovers in time and can be chosen again.
            if (_haveLast && _lastFrom.HasValue && _lastTarget.HasValue)
            {
                var f = _lastFrom.Value; var t = _lastTarget.Value;
                int dxc = cell.Item1 - t.Item1, dyc = cell.Item2 - t.Item2;
                DiagLog.Write($"[recede-exec] from=({f.Item1},{f.Item2}) expected→({t.Item1},{t.Item2}) actual→({cell.Item1},{cell.Item2}) d=({dxc},{dyc}) {(dxc == 0 && dyc == 0 ? "HIT" : "MISS")}");
                StateSpacePlanner.ReportEdge(f.Item1, f.Item2, t.Item1, t.Item2, cell.Item1, cell.Item2);
            }
            StateSpacePlanner.DecayMiss();

            // NO stuck triggers. The field guarantees a lower-H neighbour everywhere but the goal, and StepAlongField's
            // attention-weighted pick always takes a reachable edge toward it — so we keep moving and eventually clear any
            // awkward spot. The ONLY stop is StepAlongField returning null = Expand produced no edge = truly walled in.
            var res = StateSpacePlanner.StepAlongField(_goalWx, _goalWy);
            if (res == null || res.Steps.Count == 0)
            { DiagLog.Write($"[recede] STOP at {cell}: no physics edge at all (unbreakable seal — a human couldn't pass either)"); Stop(); Main.NewText("[TerraBlind] receding: walled in"); return; }
            _lastFrom = cell; _lastTarget = (res.GoalWx, res.GoalWy); _haveLast = true;
            StateSpacePlanner.DispatchPlan(res);
        }
    }
}
