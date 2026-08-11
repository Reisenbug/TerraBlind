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
        // 三种目标语义:
        //   Snap  — 悬空目标掉到下面的地面,到达=人站在那格上。走路默认。
        //   Mine  — 不 snap,到达=目标格被挖空。挖矿:目标是岩石里的矿,身体(2x3)根本站不上去。
        //   Stand — 不 snap,到达=人站在那格上。房址:目标本来就悬空,必须真站上去。
        public enum Mode { Snap, Mine, Stand }
        static Mode _mode;
        const float GoalDistPx = 24f;
        const float StandDistPx = 8f;    // 建房契约要求脚踩准那一格,±24px 会站到隔壁列
        const int StandSwitch = 20;      // 离目标这么近就把最后一段交给 A*
        const int StandMaxTries = 3;     // A* 连着搜不到就认输,别让 greedy 在这地形上空转
        static int _standTries;
        static (int, int)? _lastFrom;    // cell the last edge started FROM (to key the attention mismatch report)
        static (int, int)? _lastTarget;  // cell the last edge planned to land on (compared to the real landing)
        static bool _haveLast;

        // 打转由 StateSpacePlanner 的进度地板在选边时掐掉;这里只留 _bestH/_ring 供日志读
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

        // 场会过期:自己挖/放的多了、换镐、人走出 flood box。三者都触发后台重建,旧场先顶着用
        const int RebuildAltered = 40;      // our altered tiles before a background re-flood (coarse; big-方向 shifts need dozens of tiles)
        static int _altered;
        static volatile bool _rebuilding;
        // 换目标=换场,建一次 ~500ms/70万格。GetField 会在主线程内联建 → 每周期一次可见卡顿,所以丢后台
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
            => Start(goalWx, goalWy, exact ? Mode.Mine : Mode.Snap);

        public static void Start(int goalWx, int goalWy, Mode mode)
        {
            StateSpacePlanner.StopNav();
            _mode = mode;
            _standTries = 0;
            if (mode == Mode.Snap)
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
            EventLog.W(Ev.Goal, $"new goal ({goalWx},{goalWy}) — building field");
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

            // 蜘蛛网压身体会把移动拖到爬(原版要 20-100 tick 才顶开),一镐就没,所以主动砸
            SmashWeb(p);
            // 抢同一帧的 controlUseItem,网优先(网拖慢移动,罐子晚一帧无所谓)
            if (!p.controlUseItem) SmashPot(p);

            // EXACT (mining): the goal is a solid ore the body can't stand on — arrival is the tile being MINED OUT.
            // The field digs a shaft toward it; the moment that cell is no longer a block, we've reached (dug) it.
            if (_mode == Mode.Mine)
            {
                var gt = Main.tile[_goalWx, _goalWy];
                if (!gt.HasTile || !Main.tileSolid[gt.TileType])
                { DiagLog.Write("[recede] exact goal mined out"); LastStop = "done"; Stop(); Main.NewText("[TerraBlind] receding nav done (mined)"); return; }
            }
            else
            {
                float gx = _goalWx * 16f + 8f, gy = (_goalWy + 1) * 16f;
                float cx = p.Center.X, fy = p.position.Y + p.height;
                // Stand 的契约是"脚踩着那一格开工",±24px 会让人站在隔壁列上就报到达 —— 建房那边整套
                // 局部坐标就全偏一格。收到半格,并且要求真落地。
                float tol = _mode == Mode.Stand ? StandDistPx : GoalDistPx;
                if (System.Math.Abs(cx - gx) <= tol && System.Math.Abs(fy - gy) <= tol
                    && (_mode != Mode.Stand || p.velocity.Y == 0f))
                {
                    DiagLog.Write($"[recede] reached goal mode={_mode} goal=({_goalWx},{_goalWy}) dx={cx - gx:0.#} dy={fy - gy:0.#} body=({ActExecutor.OriginCx(p)},{ActExecutor.OriginCy(p)})");
                    LastStop = "done"; Stop(); Main.NewText("[TerraBlind] receding nav done"); return;
                }
            }

            // 每帧判卡死(不只在重规划边界):~0.5s 内走安全步,真平线 6-8s 才放弃这一段。它挪的时候占用本帧控制
            if (StuckSentinel.Tick(p, _goalWx, _goalWy))
            {
                EventLog.W(Ev.Sentinel, $"GIVE-UP H平线 放弃这一段 goal=({_goalWx},{_goalWy})");
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
            // 上一条边真落到哪了 → 变成连续的失配权重,软性压低老是落空的边。随后整表衰减:不做黑名单、不禁回头
            if (_haveLast && _lastFrom.HasValue && _lastTarget.HasValue)
            {
                var f = _lastFrom.Value; var t = _lastTarget.Value;
                int dxc = cell.Item1 - t.Item1, dyc = cell.Item2 - t.Item2;
                // 带上原始 px/py 和脚下踩的东西:格号是取整后的结论,差一行可能是真没到,也可能只是
                // 两把尺子读数不同。今天四个误判有三个当场就能证伪 —— 如果日志里有这些原始值。
                int feetRow = (int)((p.position.Y + p.height) / 16f);
                var (bl, br) = Predicates.BodyCols(p);
                string under = "";
                for (int c = bl; c <= br; c++)
                    if (Predicates.IsGround(c, feetRow)) under += $"{c}:{Main.tile[c, feetRow].TileType} ";
                EventLog.W(Ev.Exec, $"({f.Item1},{f.Item2})→({t.Item1},{t.Item2}) actual=({cell.Item1},{cell.Item2}) "
                    + $"{(StateSpacePlanner.Arrived(t.Item1, t.Item2, cell.Item1, cell.Item2) ? "HIT" : $"MISS d=({dxc},{dyc})")} "
                    + $"px={p.position.X:0.#},{p.position.Y:0.#} cols[{bl}..{br}] feet={feetRow} under={(under == "" ? "空" : under.Trim())}");
                StateSpacePlanner.ReportEdge(f.Item1, f.Item2, t.Item1, t.Item2, cell.Item1, cell.Item2);
            }
            StateSpacePlanner.DecayMiss();

            // STAND 末段交给 A*:H 场是格子 Dijkstra,不知道悬空格竖直跳不上去,梯度会把人吸到正下方打转
            if (_mode == Mode.Stand && System.Math.Abs(cell.Item1 - _goalWx) <= StandSwitch
                                    && System.Math.Abs(cell.Item2 - _goalWy) <= StandSwitch)
            {
                // goalSnapCap:0 —— 目标本来就悬空,一旦被 snap 拉到地面,人会踩着地面报"到了",
                // 建房整套坐标全错。宁可 fail fast。
                var ap = StateSpacePlanner.Plan(_goalWx, _goalWy, goalSnapCap: 0);
                if (ap.Found && ap.Steps.Count > 0)
                {
                    DiagLog.Write($"[recede] STAND A* from {cell} → ({_goalWx},{_goalWy}) steps={ap.Steps.Count} exp={ap.Expansions}");
                    // 往上垫平台/pillar 时人几乎不横移、H 也几乎不降 —— 正好是 sentinel 判"卡死"的特征。
                    // A* 每次真给出一条路就把它的计时清零,否则爬到一半就被当成卡住放弃。
                    StuckSentinel.Reset();
                    _standTries = 0;
                    _lastFrom = cell; _lastTarget = (_goalWx, _goalWy); _haveLast = true;
                    StateSpacePlanner.DispatchPlan(ap);
                    return;
                }
                if (ap.Partial && ap.Steps.Count > 0)
                {
                    // 搜不到终点但有更近的落脚点 —— 走过去换个角度再搜。人真的动了,不算一次失败。
                    StuckSentinel.Reset();
                    _standTries = 0;
                    _lastFrom = cell; _lastTarget = (ap.GoalWx, ap.GoalWy); _haveLast = true;
                    StateSpacePlanner.DispatchPlan(ap);
                    return;
                }
                // 一步都给不出来。别退回 greedy —— 它在这地形上只会打转到 sentinel 报卡死。
                DiagLog.Write($"[recede] STAND A* no plan at {cell} → ({_goalWx},{_goalWy}) exp={ap.Expansions} try={_standTries + 1}/{StandMaxTries}");
                if (++_standTries >= StandMaxTries)
                {
                    LastStop = "unreachable"; Stop();
                    Main.NewText("[TerraBlind] receding: can't stand on goal");
                    return;
                }
                return;
            }

            var res = StateSpacePlanner.StepAlongField(_goalWx, _goalWy);
            if (res == null || res.Steps.Count == 0)
            { DiagLog.Write($"[recede] STOP at {cell}: no physics edge at all (unbreakable seal — a human couldn't pass either)"); LastStop = "walled_in"; Stop(); Main.NewText("[TerraBlind] receding: walled in"); return; }

            // bestH 只在当前盆地内衡量进度:摔一次能让 H 跳 +500~1300(循环环内才 ≤50),那是换盆地不是打转
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
