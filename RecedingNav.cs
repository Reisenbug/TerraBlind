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

        // LOOP DETECTOR + SHOCK ESCAPE. H is the one progress signal every loop shape must stall. PRIMARY trigger is
        // human-eye fast: REVISIT — coming BACK to a replan cell already stood on since the last best-H improvement
        // (arriving from elsewhere, i.e. an A…B…A cycle, not an in-place stall — those are _miss/sentinel territory)
        // means one full lap is proven, verdict within seconds. FALLBACK is the stall counter: if best-H hasn't
        // improved for LoopStallReplans we are cycling even if no exact cell repeated (drifting loops). Response is
        // the same two-tier for both:
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
        // replan cells stood on since the last best-H improvement (revisit detection) + the previous replan cell
        // (to tell a travelled-away-and-back lap from an in-place stall, which is not a loop).
        static readonly System.Collections.Generic.HashSet<(int, int)> _sinceBest = new();
        static (int, int)? _prevCell;

        public static void Toggle()
        {
            if (Active) { Stop(); Main.NewText("[TerraBlind] receding nav OFF"); return; }
            int mx = (int)((Main.mouseX + Main.screenPosition.X) / 16f);
            int my = (int)((Main.mouseY + Main.screenPosition.Y) / 16f);
            Start(mx, my);
        }

        // FIELD FRESHNESS — the field is a snapshot; three things rot it: our own digs/places accumulating (H prices
        // a world that no longer exists), a pick upgrade (dig prices captured at build), and the player leaving the
        // flood box (no H at all). Each triggers an off-thread swap-rebuild anchored at the CURRENT position; the old
        // field keeps serving until the new one swaps in (only the off-field case must wait — it has no compass).
        const int RebuildAltered = 40;      // our altered tiles before a background re-flood (coarse; big-方向 shifts need dozens of tiles)
        static int _altered;
        static volatile bool _rebuilding;
        static void RebuildFieldAsync(string why)
        {
            if (_rebuilding) return;
            _rebuilding = true; _altered = 0;
            int gx = _goalWx, gy = _goalWy;
            var p = Main.LocalPlayer;
            int sx = p != null ? (int)(p.Center.X / 16f) : gx;
            int sy = p != null ? (int)((p.position.Y + p.height) / 16f) - 1 : gy;
            DiagLog.Write($"[recede] field REBUILD ({why}) anchor=({sx},{sy})");
            System.Threading.Tasks.Task.Run(() =>
            {
                try { MazeWand.Rebuild(gx, gy, sx, sy); }
                catch (System.Exception e) { DiagLog.Write($"[recede] rebuild EXC {e.Message}"); }
                finally { _rebuilding = false; }
            });
        }

        static void SmashWeb(Player p)
        {
            int x0 = (int)(p.position.X / 16f), x1 = (int)((p.position.X + p.width) / 16f);
            int y0 = (int)(p.position.Y / 16f), y1 = (int)((p.position.Y + p.height) / 16f);
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                {
                    if (x < 0 || y < 0 || x >= Main.maxTilesX || y >= Main.maxTilesY) continue;
                    var t = Main.tile[x, y];
                    if (!t.HasTile || t.TileType != Terraria.ID.TileID.Cobweb) continue;
                    int slot = -1, bp = 0;
                    for (int i = 0; i < 10; i++)
                    { var it = p.inventory[i]; if (it != null && !it.IsAir && it.pick > bp) { bp = it.pick; slot = i; } }
                    if (slot < 0) return;
                    p.selectedItem = slot;
                    Main.SmartCursorWanted_Mouse = false;
                    Main.mouseX = (int)(x * 16f + 8f - Main.screenPosition.X);
                    Main.mouseY = (int)(y * 16f + 8f - Main.screenPosition.Y);
                    if (p.itemTime == 0) p.controlUseItem = true;
                    return;
                }
        }

        static volatile bool _fieldReady;
        public static void Start(int goalWx, int goalWy, bool exact = false)
        {
            StateSpacePlanner.StopNav();
            _exact = exact;
            if (!exact)
                goalWy = StateSpacePlanner.SnapGoalToStandable(goalWx, goalWy);   // clicked air → fall to ground (same as navwand)
            _goalWx = goalWx; _goalWy = goalWy; Active = true; LastStop = null; _haveLast = false; _lastTarget = null; _lastFrom = null;
            _bestH = int.MaxValue; _replansSinceBest = 0; _shocks = 0; _ring.Clear(); _sinceBest.Clear(); _prevCell = null;
            StuckSentinel.Reset();
            StateSpacePlanner.ResetLineProgress();
            _altered = 0;
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

            // WEB REFLEX: a cobweb overlapping the body slows the walk to a crawl until vanilla's push-through
            // counter breaks it (20-100 ticks/web). A pick swing kills it in one hit — smash it actively instead
            // of wading. Runs every frame while nav is active, whatever step is executing.
            SmashWeb(p);

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

            // FAST STUCK SENTINEL — every frame, not just at replan boundaries. It watches the four progress
            // signals (displacement, H, dig damage, nearby tiles) and runs the response ladder itself: safe
            // step within ~0.5s, abandon the leg after ~6-8s of true flatline. While it nudges, it owns the
            // controls for this frame.
            if (StuckSentinel.Tick(p, _goalWx, _goalWy))
            {
                DiagLog.Write($"[recede] SENTINEL give-up at H-flatline goal=({_goalWx},{_goalWy})");
                LastStop = "stuck"; Stop();
                Main.NewText("[TerraBlind] receding: stuck (sentinel) — abandoning leg");
                return;
            }
            if (StuckSentinel.Nudging) return;

            if (StateSpacePlanner.ExecRunning) return;        // current action still executing
            if (p.velocity.Y != 0f) return;                   // wait until landed + settled

            // one label function everywhere: same rounding AND same body-fit snap as the planner's landing labels.
            var cell = StateSpacePlanner.StandCell(p.position.X, p.position.Y);
            // freshness triggers (see RebuildFieldAsync). Off-field must wait for the swap — no compass here, and
            // letting StepAlongField run would fake a "walled_in" out of a coverage hole.
            if (MazeWand.FieldPickStale())
                RebuildFieldAsync("pick change");
            if (!MazeWand.GetField(_goalWx, _goalWy).ContainsKey(cell))
            {
                RebuildFieldAsync("off-field");
                return;
            }
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
                _bestH = res.CurH; _replansSinceBest = 0; _shocks = 0; _ring.Clear(); _sinceBest.Clear();
            }
            if (res.CurH < _bestH) { _bestH = res.CurH; _replansSinceBest = 0; _shocks = 0; _sinceBest.Clear(); }
            else _replansSinceBest++;
            _ring.Add((cell.Item1, cell.Item2, res.GoalWx, res.GoalWy, res.CurH));
            if (_ring.Count > RingLen) _ring.RemoveAt(0);
            // REVISIT: back on a cell already stood on since the last best-H improvement, having travelled away in
            // between (prev replan cell differs) — one lap proven, no need to wait out the stall counter.
            bool revisit = _sinceBest.Contains(cell) && _prevCell.HasValue && _prevCell.Value != cell;
            _sinceBest.Add(cell);
            _prevCell = cell;
            if (revisit || _replansSinceBest >= LoopStallReplans)
            {
                string why = revisit ? $"revisit of ({cell.Item1},{cell.Item2})" : $"bestH={_bestH} unimproved for {_replansSinceBest} replans";
                string trail = string.Join(" ", _ring.ConvertAll(r => $"({r.fx},{r.fy})H{r.h}→({r.tx},{r.ty})"));
                if (_shocks < MaxShocks)
                {
                    _shocks++;
                    DiagLog.Write($"[recede] LOOP DETECTED at {cell}: {why} → SHOCK {_shocks}/{MaxShocks}. Trail: {trail}");
                    Main.NewText($"[TerraBlind] loop at ({cell.Item1},{cell.Item2}) — shock {_shocks}/{MaxShocks}, re-routing");
                    var edges = new System.Collections.Generic.HashSet<(int, int, int, int)>();
                    foreach (var r in _ring) edges.Add((r.fx, r.fy, r.tx, r.ty));
                    StateSpacePlanner.PenalizeEdges(edges, ShockPenalty);
                    _replansSinceBest = 0;   // fresh window to let the re-route prove itself
                    _sinceBest.Clear(); _prevCell = null;
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

            _altered += res.Altered;
            if (_altered >= RebuildAltered)
                RebuildFieldAsync($"altered {_altered} tiles");

            _lastFrom = cell; _lastTarget = (res.GoalWx, res.GoalWy); _haveLast = true;
            StateSpacePlanner.DispatchPlan(res);
        }
    }
}
