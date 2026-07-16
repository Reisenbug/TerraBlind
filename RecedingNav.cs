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
        // outcome of the last run, for the HTTP bridge: null while running/never-ran,
        // "done" | "walled_in" | "loop_unresolved" | "stopped" after it ends.
        public static string LastStop;
        static int _goalWx, _goalWy;
        // EXACT goal: don't snap the goal to a standable cell, and count arrival as "the goal tile is gone" (mined out)
        // rather than "the body stands on it". Used for mining: the target is a solid ore INSIDE rock — the body (2x3)
        // can never stand on that exact cell, so the field digs a shaft down to it and arrival = the ore tile removed.
        static bool _exact;
        const float GoalDistPx = 24f;
        static (int, int)? _lastFrom;    // cell the last edge started FROM (to key the attention mismatch report)
        static (int, int)? _lastTarget;  // cell the last edge planned to land on (compared to the real landing)
        static bool _haveLast;

        // LOOP DETECTOR + SHOCK ESCAPE. H is the one progress signal every loop shape must stall: if the lowest H
        // ever reached hasn't improved for LoopStallReplans, we are cycling — even when every step HITs its plan
        // (the loops attention is structurally blind to). Response is two-tier:
        //   soft — shock: one large decaying penalty on every edge the ring traversed; Bellman re-routes onto the
        //          next-best alternative. Finite + decaying = not a ban, so no rule is violated.
        //   hard — after MaxShocks shocks with still no new best-H, this is a real model gap (a transition the field
        //          prices but no action realizes): stop, dump the trail, shout. Code has to change; hiding it doesn't.
        const int LoopStallReplans = 20;                 // ~4 laps of the widest loop seen (5 replans/lap)
        const float ShockPenalty = 200f;                 // big enough to flip any H-margin a loop can trap in (seen: 3~50)
        const int MaxShocks = 3;
        // single-cycle H regression above this = involuntary displacement (fall/knockback into a worse basin), not a
        // loop. Sized between the largest possible ring-internal H spread (ring ≤ ~8 cells × ≤45/cell ≈ tens; observed
        // ≤50) and the smallest catastrophic fall (tens of cells × climb pricing; observed +500..+1300).
        const int DisplacementRebase = 200;
        static int _bestH; static int _replansSinceBest; static int _shocks;
        static readonly System.Collections.Generic.List<(int fx, int fy, int tx, int ty, int h)> _ring = new();
        const int RingLen = 24;

        public static void Toggle()
        {
            if (Active) { Stop(); Main.NewText("[TerraBlind] receding nav OFF"); return; }
            int mx = (int)((Main.mouseX + Main.screenPosition.X) / 16f);
            int my = (int)((Main.mouseY + Main.screenPosition.Y) / 16f);
            Start(mx, my);
        }

        static volatile bool _fieldReady;
        public static void Start(int goalWx, int goalWy, bool exact = false)
        {
            StateSpacePlanner.StopNav();
            _exact = exact;
            if (!exact)
                goalWy = StateSpacePlanner.SnapGoalToStandable(goalWx, goalWy);   // clicked air → fall to ground (same as navwand)
            _goalWx = goalWx; _goalWy = goalWy; Active = true; LastStop = null; _haveLast = false; _lastTarget = null; _lastFrom = null;
            _bestH = int.MaxValue; _replansSinceBest = 0; _shocks = 0; _ring.Clear();
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
            if (Active && LastStop == null) LastStop = "stopped";
            if (Active)   // only fire on an actual running→stopped transition
                HttpServerSystem.PushEvent("nav_done", "{\"result\":\"" + (LastStop ?? "stopped") + "\"}");
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

            // EXACT (mining): the goal is a solid ore the body can't stand on — arrival is the tile being MINED OUT.
            // The field digs a shaft toward it; the moment that cell is no longer a block, we've reached (dug) it.
            if (_exact)
            {
                var gt = Main.tile[_goalWx, _goalWy];
                if (!gt.HasTile || !Main.tileSolid[gt.TileType])
                { DiagLog.Write("[recede] exact goal mined out"); LastStop = "done"; Stop(); Main.NewText("[TerraBlind] receding nav done (mined)"); return; }
            }
            else
            {
                float gx = _goalWx * 16f + 8f, gy = (_goalWy + 1) * 16f;
                float cx = p.Center.X, fy = p.position.Y + p.height;
                if (System.Math.Abs(cx - gx) <= GoalDistPx && System.Math.Abs(fy - gy) <= GoalDistPx)
                { DiagLog.Write("[recede] reached goal"); LastStop = "done"; Stop(); Main.NewText("[TerraBlind] receding nav done"); return; }
            }

            if (StateSpacePlanner.ExecRunning) return;        // current action still executing
            if (p.velocity.Y != 0f) return;                   // wait until landed + settled

            // one label function everywhere: same rounding AND same body-fit snap as the planner's landing labels.
            var cell = StateSpacePlanner.StandCell(p.position.X, p.position.Y);
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
            { DiagLog.Write($"[recede] STOP at {cell}: no physics edge at all (unbreakable seal — a human couldn't pass either)"); LastStop = "walled_in"; Stop(); Main.NewText("[TerraBlind] receding: walled in"); return; }

            // DISPLACEMENT RE-BASELINE: bestH must measure progress within the current basin, not all-time. After a
            // catastrophic involuntary displacement (a missed sky jump dropping 47 cells raised H by ~500), the honest
            // route DOWN from the crash site cannot beat the pre-fall bestH for dozens of replans — the detector then
            // shocked a perfectly descending path, its +200 penalties bent selection into a real wander-loop, and 3
            // "failed" shocks hard-stopped the run. A single-cycle H jump far above any loop ring's internal spread
            // (rings span a few cells × step costs ≈ ≤50; observed falls jump +500..+1300) is a basin change, not a
            // loop symptom → reset the baseline and the shock budget to judge progress from here.
            if (_bestH != int.MaxValue && res.CurH > _bestH + DisplacementRebase)
            {
                DiagLog.Write($"[recede] DISPLACED: H {_bestH}→{res.CurH} (+{res.CurH - _bestH}) — re-baseline, shocks reset");
                _bestH = res.CurH; _replansSinceBest = 0; _shocks = 0; _ring.Clear();
            }
            if (res.CurH < _bestH) { _bestH = res.CurH; _replansSinceBest = 0; _shocks = 0; }
            else _replansSinceBest++;
            _ring.Add((cell.Item1, cell.Item2, res.GoalWx, res.GoalWy, res.CurH));
            if (_ring.Count > RingLen) _ring.RemoveAt(0);
            if (_replansSinceBest >= LoopStallReplans)
            {
                string trail = string.Join(" ", _ring.ConvertAll(r => $"({r.fx},{r.fy})H{r.h}→({r.tx},{r.ty})"));
                if (_shocks < MaxShocks)
                {
                    _shocks++;
                    DiagLog.Write($"[recede] LOOP DETECTED at {cell}: bestH={_bestH} unimproved for {_replansSinceBest} replans → SHOCK {_shocks}/{MaxShocks}. Trail: {trail}");
                    Main.NewText($"[TerraBlind] loop at ({cell.Item1},{cell.Item2}) — shock {_shocks}/{MaxShocks}, re-routing");
                    var edges = new System.Collections.Generic.HashSet<(int, int, int, int)>();
                    foreach (var r in _ring) edges.Add((r.fx, r.fy, r.tx, r.ty));
                    StateSpacePlanner.PenalizeEdges(edges, ShockPenalty);
                    _replansSinceBest = 0;   // fresh window to let the re-route prove itself
                }
                else
                {
                    DiagLog.Write($"[recede] LOOP UNRESOLVED at {cell}: {MaxShocks} shocks, bestH={_bestH} still stuck. Trail: {trail}");
                    LastStop = "loop_unresolved";
                    Stop();
                    Main.NewText($"[TerraBlind] LOOP at ({cell.Item1},{cell.Item2}) — {MaxShocks} shocks failed, nav stopped, see jump_trace.log");
                    return;
                }
            }

            _lastFrom = cell; _lastTarget = (res.GoalWx, res.GoalWy); _haveLast = true;
            StateSpacePlanner.DispatchPlan(res);
        }
    }
}
