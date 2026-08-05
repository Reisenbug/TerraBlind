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

        // 打转不在这里处理了。以前是"事后检测 + 罚边/换目标",试过 shock、jiggle、softmax、承诺,全失败:
        // 罚边罚死过唯一出口,随机在平坦地形上纯添乱,承诺又要另建一套目标选择。根因不在检测,在选边那一刻
        // g+H 没有任何"我到底有没有靠近终点"的记忆。现在由 StateSpacePlanner 的进度地板在选边时直接掐掉。
        // 这里只留 _bestH/_ring 供日志和快照读,不驱动任何决策。
        static int _bestH;
        static readonly System.Collections.Generic.List<(int fx, int fy, int tx, int ty, int h)> _ring = new();
        const int RingLen = 24;
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
        // Switching which cell we route to means a DIFFERENT field, and building one costs ~500ms over ~700k cells.
        // GetField would do it inline on the main thread — a visible freeze every cycle. Clear the ready flag and
        // build off-thread instead, exactly like nav startup does: the body simply stands still for a moment.
        static void SwitchFieldAsync(int gx, int gy, string why)
        {
            _fieldReady = false;
            DiagLog.Write($"[recede] field SWITCH ({why}) → ({gx},{gy})");
            System.Threading.Tasks.Task.Run(() =>
            {
                try { MazeWand.GetField(gx, gy); RecedingVis.SetField(gx, gy); }
                catch (System.Exception e) { DiagLog.Write($"[recede] switch EXC {e.Message}"); }
                finally { _fieldReady = true; }
            });
        }

        static void RebuildFieldAsync(string why)
        {
            if (_rebuilding) return;
            _rebuilding = true; _altered = 0;
            // 重建的 box 锚在当前位置,格子的 H 会整体变(有的格子新进场,有的出场)。地板记的是旧场的数,
            // 换了尺子还留着就会把正常的一步误判成"没前进" → 一路 PUSH。所以重建时清掉。
            StateSpacePlanner.ResetFloor();
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

        // 顺路砸罐子(出绳子/金币/火把,赶路白捡)。和 SmashWeb 同套路,只是范围不同:
        // 网必须重叠身体才碍事,罐子是够得到就砸,所以用原版 gate 挖掘的 IsInTileInteractionRange。
        static void SmashPot(Player p)
        {
            int cx = (int)((p.position.X + p.width / 2f) / 16f);
            int cy = (int)((p.position.Y + p.height / 2f) / 16f);
            const int scan = 8;
            for (int x = cx - scan; x <= cx + scan; x++)
                for (int y = cy - scan; y <= cy + scan; y++)
                {
                    if (x < 0 || y < 0 || x >= Main.maxTilesX || y >= Main.maxTilesY) continue;
                    var t = Main.tile[x, y];
                    if (!t.HasTile || t.TileType != Terraria.ID.TileID.Pots) continue;
                    if (!p.IsInTileInteractionRange(x, y, Terraria.DataStructures.TileReachCheckSettings.Simple)) continue;
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
            _bestH = int.MaxValue; _ring.Clear(); _prevCell = null;
            StateSpacePlanner.ResetFloor();   // 换目标=换场,旧地板的 H 是另一把尺子上的数,留着会误判
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
            // 抢同一帧的 controlUseItem,网优先(网拖慢移动,罐子晚一帧无所谓)
            if (!p.controlUseItem) SmashPot(p);

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

            // NO stuck triggers, NO loop detection, NO commitment. 不再需要:打转的根因(每步 g+H 最优,合起来
            // 原地不动)由 StateSpacePlanner 的进度地板在选边那一刻就掐掉了 —— 选出来的落点压不低历史最低 H,
            // 当场关掉 g 改选 H 最低的候选。地板单调下降且有下界 0,所以"一直不前进"结构上不可能发生,
            // 不用事后检测、不用罚边、不用换目标。唯一的停机仍然是 StepAlongField 返回 null(真被封死)。
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
            if (res.CurH < _bestH) _bestH = res.CurH;
            _ring.Add((cell.Item1, cell.Item2, res.GoalWx, res.GoalWy, res.CurH));
            if (_ring.Count > RingLen) _ring.RemoveAt(0);
            _prevCell = cell;

            _altered += res.Altered;
            if (_altered >= RebuildAltered)
                RebuildFieldAsync($"altered {_altered} tiles");

            _lastFrom = cell; _lastTarget = (res.GoalWx, res.GoalWy); _haveLast = true;
            StateSpacePlanner.DispatchPlan(res);
        }
    }
}
