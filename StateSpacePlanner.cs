using System;
using System.Collections.Generic;
using Terraria;

namespace TerraBlind
{
    // State-space physics search: node = real physics state, expansion = enumerate inputs × simulate.
    // Standalone prototype; does not touch the grid A*.
    public static class StateSpacePlanner
    {
        const float VxQuant = 0.5f;
        const float WalkStridePx = 112f; // one walk edge spans ~a jump's horizontal reach (maxRun*jumpHeight/gravity) so walk/jump edges are cost-comparable; 24px died in the accel ramp and made walk look slow
        const int   MaxExpansions = 20000;
        const int   LegSubgoalCells = 80; // rolling: each leg targets a SUBGOAL this many cells ahead along the field gradient (reachable → A* finds it fast, like a manual single-point nav), not the far final goal
        const int   MaxSegFrames = 1200; // high enough that slow water descents reach the floor; still a fuse vs non-terminating edges
        const int   MaxPlanSpanCells = 200; // refuse to plan if goal is farther than this in x or y — BuildField would hang
        const int   HoldStep = 2;
        // weighted A*: f = g + w·h. w>1 trades a little path optimality for far fewer expansions,
        // which is what makes the deep climb plans (exp~5000) affordable.
        const float HeuristicWeight = 1.0f;

        // ASSUMPTION: Player.jumpHeight is the hold-frame cap and accessories raise it.
        // Reading it (vs hardcoding 15) keeps planning correct as gear changes.
        static int[] BuildHoldOptions()
        {
            int maxHold = Player.jumpHeight > 0 ? Player.jumpHeight : 15;
            var opts = new List<int> { 0 };
            for (int h = HoldStep; h < maxHold; h += HoldStep) opts.Add(h);
            opts.Add(maxHold);
            return opts.ToArray();
        }

        public struct SSNode
        {
            public float Px, Py, Vx, Vy;
            public bool Grounded;
        }

        struct CellKey : IEquatable<CellKey>
        {
            public int Cx, Cy; public bool G;
            public bool Equals(CellKey o) => Cx == o.Cx && Cy == o.Cy && G == o.G;
            public override int GetHashCode() => HashCode.Combine(Cx, Cy, G);
        }

        // 挖/放的落点只能用这个:那些格子还没被改,StandCell 的 BodyFits 会拿改造前的世界去判它。
        internal static (int cx, int cy) RawCell(float px, float py)
            => ((int)((px + PhysicsSimulator.PlayerW / 2f) / 16f), (int)((py + PhysicsSimulator.PlayerH - 1f) / 16f));

        // 站哪一格 = 脚占的那个空气格。中心列得身体真放得下才算数:0.7px 探进头顶实心的那列就被标成站在里面,
        // 而那格 H 是按挖算的,跳跃白嫖了它 → (800,937)↔(801,937) 边界震荡。
        internal static (int cx, int cy) StandCell(float px, float py)
        {
            int cy = (int)((py + PhysicsSimulator.PlayerH - 1f) / 16f);
            int cx = (int)((px + PhysicsSimulator.PlayerW / 2f) / 16f);
            int leftCol = (int)(px / 16f);
            int rightCol = (int)((px + PhysicsSimulator.PlayerW - 1f) / 16f);
            int other = cx == leftCol ? rightCol : leftCol;
            // 站在砖的边缘时中心列可能是悬空那一列:身体压在隔壁列的砖上,人站得住,可这个格号一报出去,
            // 找支撑的代码全查空气,一条边都发不出来 → "walled in"((3082,805) 脚下的砖其实在 3081)。
            if (BodyFits(cx, cy) && (HasSupport(cx, cy + 1) || other == cx || !HasSupport(other, cy + 1)))
                return (cx, cy);
            // 只吸附到既放得下身体、脚下又有支撑的列。挖的落点(规划时砖还在,中心列判"放不下")要是被吸到
            // 隔壁没地板的悬崖列就废了 —— 那种情况保留中心列。
            if (other != cx && BodyFits(other, cy) && HasSupport(other, cy + 1)) return (other, cy);
            return (cx, cy);
        }

        // 和场的 StepCost 用同一套身体包络:脚那行可以是斜坡/半砖(DigSolid 曾把每次站斜坡都误判成不合身),
        // 上面几行任何实心都挡,脚陷进斜坡 6-16px 时头会顶到第 4 行(42+6 > 48)。
        static bool BodyFits(int c, int cy)
        {
            if (PathPlanner.IsBlockPublic(c, cy)) return false;
            if (DigSolid(c, cy - 1) || DigSolid(c, cy - 2)) return false;
            if (DigSolid(c, cy) && DigSolid(c, cy - 3)) return false;   // DigSolid but not IsBlock = slope/half footing
            return true;
        }

        // 推进到【下次重规划真正会读到的那个状态】:RecedingNav 在帧跑完且 vy==0 后的第一 tick 重规划,中间至少一个 idle 步。
        // 标最后一帧会撒谎 —— 残余 vx 把人滑过格子边界,计划"到达"了一个事后永远读不到的格 ((800,937) 幽灵震荡)。
        const int SettleMaxFrames = 30;
        static SSNode SettleNode(SSNode n, PhysicsSimulator.Params ph)
        {
            var s = new PhysicsSimulator.State { Px = n.Px, Py = n.Py, Vx = n.Vx, Vy = n.Vy, Grounded = n.Grounded };
            var idle = new PhysicsSimulator.ControlInput();
            for (int f = 0; f < SettleMaxFrames; f++)
            {
                s = PhysicsSimulator.Step(s, idle, ph);
                if (s.Grounded) break;
            }
            return new SSNode { Px = s.Px, Py = s.Py, Vx = s.Vx, Vy = s.Vy, Grounded = s.Grounded };
        }

        // support includes platforms (solidTop), unlike DigSolid
        static bool HasSupport(int x, int y)
        {
            if (x < 0 || y < 0 || x >= Main.maxTilesX || y >= Main.maxTilesY) return false;
            var t = Main.tile[x, y];
            return t.HasTile && Main.tileSolid[t.TileType];
        }

        static CellKey Cell(SSNode s)
        {
            var (cx, cy) = StandCell(s.Px, s.Py);
            return new CellKey { Cx = cx, Cy = cy, G = s.Grounded };
        }

        struct Label { public float G, Vx, Vy; }

        // TEMP scan profiler: which edge type eats the search. reset per Plan, dumped after jptally. T<name>(()=>expr).
        static readonly Dictionary<string, (int n, long ticks)> _prof = new();
        static T Prof<T>(string k, System.Func<T> f)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var r = f();
            var c = _prof.TryGetValue(k, out var v) ? v : (0, 0L);
            _prof[k] = (c.Item1 + 1, c.Item2 + System.Diagnostics.Stopwatch.GetTimestamp() - t0);
            return r;
        }
        static void DumpProf()
        {
            double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            var sb = new System.Text.StringBuilder("[ss-prof]");
            foreach (var kv in _prof) sb.Append($" {kv.Key}={kv.Value.n}x/{kv.Value.ticks * f:0}ms");
            DiagLog.Write(sb.ToString());
        }

        // 同格里 g 不更贵、可用速度不更小的标签支配掉对方,这是防止一格囤几百个 vx 变体的关键。
        // 别把落地格的 vx 变体压成"只留最便宜":斜坡连滑要靠上一步的残速接力,试过,会切断链子。
        static bool Dominated(List<Label> labels, float g, float vx, float vy)
        {
            // vx 按桶量化:平地走出来的 vx 是连续谱且互不支配(vx 越高 g 越贵),一格能囤几百个变体 → 直走重展开 8844 次。
            // 同桶同号更便宜就支配;斜坡连滑要的那个高速桶仍然活着。
            int vb = (int)(MathF.Abs(vx) / VxQuant);
            foreach (var l in labels)
            {
                if (l.G <= g + 0.01f && MathF.Sign(l.Vx) == MathF.Sign(vx)
                    && (int)(MathF.Abs(l.Vx) / VxQuant) >= vb && MathF.Abs(l.Vy - vy) < VxQuant)
                    return true;
            }
            return false;
        }

        // one candidate action considered at a decision point (for the receding viz). Cx/Cy = landing cell.
        public struct Cand { public int Cx, Cy, H, Cost; public string Kind; public bool Descends; }

        public class SSResult
        {
            public bool Found;
            public int Expansions;
            public double Millis;
            public List<(float px, float py)> Path = new();
            public List<PathSeg> Segments = new();
            public List<PhysicsSimulator.ControlInput> ExecFrames = new();
            public float BestPx, BestPy, BestDx, BestDy;
            public float StartPx, StartPy; // the player position this plan was computed from (for lookahead frame realignment)
            public bool Partial;           // true = this leg stops at the nearest grounded node, NOT the final goal (roll again from there)
            public List<(float px, float py)> Explored = new();
            public int GoalWx, GoalWy; // goal after snapping to a standable cell
            public List<ExecStep> Steps = new(); // ordered edges for edge-by-edge execution (frame replay or pillar macro)
            public float CostFrames;       // this action's cost in frames (walk/jump frame count, or pillar/dig frame-equivalent) — caller uses it as the time denominator for progress-efficiency stuck detection
            public int CurH;               // field H at the cell this plan started from (loop-detector progress signal)
            public int Altered;            // tiles this edge digs/places/pillars — field-staleness accumulator input
        }

        // one path edge to execute: a frame-replay move (Frames!=null) or the pillar macro (Pillar=true → drive
        // SkillExecutor.StartPillarJump to TargetCy). TargetCx/Cy = the landing cell.
        public class ExecStep
        {
            public bool Pillar;
            public bool Dig;
            public MineDir DigDir;
            public int TargetCx, TargetCy;
            public float LandPx, LandPy;   // 规划落点的像素值:格号是取整后的结论,查落点偏差要看这个
            public List<PhysicsSimulator.ControlInput> Frames;
            public List<(int wx, int wy)> MineTiles;
        }

        // edge type for per-edge偏离 grouping in logs (move/jump/jumpPlace/dig/pillar): grep one label, awk by kind.
        static string EdgeKind(ExecStep st)
        {
            if (st.Pillar) return "pillar";
            if (st.Dig) return $"dig{st.DigDir}";
            if (st.Frames == null || st.Frames.Count == 0) return "empty";
            bool place = st.Frames.Exists(fr => fr.Place);
            bool jump = st.Frames.Exists(fr => fr.Jump);
            return place ? "jumpPlace" : jump ? "jump" : "move";
        }

        // 给执行看门狗的耗时估计。常数一律往宽了取 —— 余量交给看门狗自己的 margin。
        static float EstStepFrames(ExecStep st, Player p)
        {
            if (st.Pillar)
            {
                int feet = (int)((p.position.Y + p.height) / 16f);
                int cells = System.Math.Max(2, feet - st.TargetCy);
                return cells * 21.5f + 45f;
            }
            if (st.Dig)
            {
                float c = 0f;
                if (st.MineTiles != null) foreach (var t in st.MineTiles) c += System.Math.Min(DigTable.CostFrames(t.wx, t.wy), 600);
                return System.MathF.Max(120f, c + 60f);
            }
            if (st.Frames != null && st.Frames.Count > 0)
            {
                if (!st.Frames.Exists(fr => fr.Place || fr.Jump || fr.Down))   // closed-loop walk: distance-based
                    return System.MathF.Abs(st.TargetCx * 16f + 8f - p.Center.X) / 2f + 30f;
                return st.Frames.Count;
            }
            return 300f;
        }

        public class PathSeg
        {
            public bool IsJump;
            public int Hold;
            public int FrameCount;
            public List<(float px, float py)> Trail = new();
        }

        // 每次规划的临时状态。原来是全局 static,等于把"同时只能有一个规划"焊进了 ~50 个调用点;
        // 改成显式传递后可重入:执行器跨帧持一个 ctx,后台 lookahead 用自己的。
        public class PlanCtx
        {
            public Dictionary<(int, int), int> DistField;
            public Dictionary<(int, int), int> BlockH;
            public int JpNoSpot, JpNoLand, JpFellThrough, JpSlidOff, JpOk;
        }

        // startOverride:从给定状态起规划(lookahead 用 —— 边走边算下一段,到点零停顿)。
        // goalWx/Wy 是 A* 搜的格(滚动时是近子目标),fieldGoal 是缓存罗盘场的键(最终目标),分开才不会每段重建百万格场(卡死)。
        public static SSResult Plan(int goalWx, int goalWy, (float px, float py, float vx)? startOverride = null, int fieldGoalWx = -1, int fieldGoalWy = -1, int maxExp = MaxExpansions, int goalSnapCap = int.MaxValue)
        {
            var ctx = new PlanCtx();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var res = new SSResult();
            var p = Main.LocalPlayer;
            if (p == null || !p.active) return res;
            var ph = PhysicsSimulator.Params.FromPlayer(p);
            var holdOptions = BuildHoldOptions();

            // 向下无限扫是 navwand 的点击语义,内部重规划不能继承:世界一变能把目标传送到深渊。
            // cap=0 = 【不吸附】,不是"吸附完不许变" —— 空中目标必然吸得远,stand 模式会永远进不了门。
            int requestedWy = goalWy;
            if (goalSnapCap > 0)
            {
                goalWy = SnapGoalToStandable(goalWx, goalWy);
                if (System.Math.Abs(goalWy - requestedWy) > goalSnapCap)
                {
                    DiagLog.Write($"[ss-plan] goal snap ({goalWx},{requestedWy})→({goalWx},{goalWy}) exceeds cap {goalSnapCap} → fail fast");
                    return res;
                }
            }
            res.GoalWx = goalWx; res.GoalWy = goalWy;
            float goalCx = goalWx * 16f + 8f;
            float goalFeetY = (goalWy + 1) * 16f;

            int platformSlot = NavCoordinator.FindPlatformSlot(p);
            int platformTile = platformSlot >= 0 ? p.inventory[platformSlot].createTile : -1;
            bool hasPickaxe = false;
            for (int i = 0; i < 10; i++) { var it = p.inventory[i]; if (it != null && !it.IsAir && it.pick > 0) { hasPickaxe = true; break; } }

            float startPx = startOverride?.px ?? p.position.X;
            float startPy = startOverride?.py ?? p.position.Y;
            float startVx = startOverride?.vx ?? p.velocity.X;
            res.StartPx = startPx; res.StartPy = startPy;
            var (spx, spy) = StandCell(startPx, startPy);
            // 滚动:复用按最终目标缓存的大罗盘,建一次全段共享。单点(navwand):在 start↔goal 周围建小盒场,几十 ms。
            // 单点绝不能走大场 —— 那是 1.4s 的构建,近距离导航会卡死。
            if (fieldGoalWx >= 0 && fieldGoalWy >= 0)
                ctx.DistField = MazeWand.GetField(fieldGoalWx, fieldGoalWy);
            else
                ctx.DistField = MazeWand.BuildField(goalWx, goalWy, spx, spy);
            ctx.BlockH = null;

            // DIAGNOSTIC: dump the maze-field H up the start column to answer "why does A* go DOWN into the pit". if a
            // lower cell has LOWER H than a higher one, the field itself rewards descending. x = cell not in field.
            {
                var hb = new System.Text.StringBuilder($"[ss-mazeH] start=({spx},{spy}) goal=({goalWx},{goalWy}) col={spx} (y:H):");
                for (int yy = spy + 4; yy >= spy - 30; yy--)
                    hb.Append(ctx.DistField.TryGetValue((spx, yy), out int d) ? $" {yy}:{d}" : $" {yy}:x");
                DiagLog.Write(hb.ToString());
            }

            var start = new SSNode
            {
                Px = startPx, Py = startPy,
                Vx = startVx, Vy = 0f, Grounded = true,
            };

            var labels = new Dictionary<CellKey, List<Label>>();
            var came = new Dictionary<SSNode, (SSNode prev, List<PhysicsSimulator.ControlInput> frames, float g, bool pillar, List<(int, int)> digTiles)>();
            var open = new PriorityQueue<SSNode, float>();
            labels[Cell(start)] = new List<Label> { new Label { G = 0f, Vx = start.Vx, Vy = start.Vy } };
            came[start] = (start, null, 0f, false, null);
            open.Enqueue(start, Heuristic(ctx, start, goalCx, goalFeetY, ph));

            _prof.Clear();
            int expansions = 0;
            SSNode goalNode = default; bool found = false;
            float bestDist = float.MaxValue;
            // 滚动(部分)规划:记住见过的最近的【落地】状态。目标超出预算时返回到它而不是失败,调用方走过去再重规划。
            // 必须落地:一段路以站定结束,下一段才有合法起点。
            SSNode bestGroundedNode = start; bool haveBestGrounded = false; float bestGroundedDist = float.MaxValue;

            while (open.Count > 0 && expansions < maxExp)
            {
                var cur = open.Dequeue();
                float curG = came.TryGetValue(cur, out var ce) ? ce.g : float.MaxValue;

                {
                    float ccx = cur.Px + PhysicsSimulator.PlayerW / 2f;
                    float cfy = cur.Py + PhysicsSimulator.PlayerH;
                    float dist = MathF.Abs(ccx - goalCx) + MathF.Abs(cfy - goalFeetY);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        res.BestPx = cur.Px; res.BestPy = cur.Py;
                        res.BestDx = ccx - goalCx; res.BestDy = cfy - goalFeetY;
                    }
                    if (cur.Grounded && dist < bestGroundedDist && !cur.Equals(start))
                    {
                        bestGroundedDist = dist; bestGroundedNode = cur; haveBestGrounded = true;
                    }
                }

                if (ReachedGoal(cur, goalCx, goalFeetY))
                {
                    found = true; goalNode = cur; break;
                }

                expansions++;
                if (res.Explored.Count < 3000) res.Explored.Add((cur.Px, cur.Py));
                foreach (var (next, frames, cost, pillar, digTiles) in Expand(ctx, cur, ph, goalCx, goalFeetY, holdOptions, platformTile, hasPickaxe))
                {
                    float ng = curG + cost;
                    var ck = Cell(next);
                    if (!labels.TryGetValue(ck, out var list)) { list = new List<Label>(); labels[ck] = list; }
                    if (F_Dominance && Dominated(list, ng, next.Vx, next.Vy)) continue;
                    list.RemoveAll(l => l.G >= ng - 0.01f && MathF.Abs(l.Vx) <= MathF.Abs(next.Vx) + 0.01f && MathF.Sign(l.Vx) == MathF.Sign(next.Vx) && MathF.Abs(l.Vy - next.Vy) < VxQuant);
                    list.Add(new Label { G = ng, Vx = next.Vx, Vy = next.Vy });
                    came[next] = (cur, frames, ng, pillar, digTiles);
                    open.Enqueue(next, ng + HeuristicWeight * Heuristic(ctx, next, goalCx, goalFeetY, ph));
                }
            }

            sw.Stop();
            res.Expansions = expansions;
            res.Millis = sw.Elapsed.TotalMilliseconds;
            res.Found = found;
            // 目标超出本次预算 → 退回最近的落地节点当部分段。haveBestGrounded 防的是"根本没动"(起点是唯一落地点 = 真卡住)。
            bool retrace = found;
            if (!found && haveBestGrounded)
            {
                goalNode = bestGroundedNode;
                res.Partial = true;
                retrace = true;
                DiagLog.Write($"[ss-partial] exp={expansions}/{MaxExpansions} → leg to grounded best {StandCell(bestGroundedNode.Px, bestGroundedNode.Py)} (goal still {bestDist:0.#}px away)");
            }
            if (!found && !haveBestGrounded)
            {
                DiagLog.Write($"[ss-fail] exp={expansions}/{MaxExpansions} openLeft={open.Count} bestCell={StandCell(res.BestPx, res.BestPy)} bestDx={res.BestDx:0.#} bestDy={res.BestDy:0.#}");
                DumpTerrain(start, goalWx, goalWy, res.Explored);
            }
            if (retrace)
            {
                var k = goalNode;
                var revPts = new List<(float, float)>();
                var revSegs = new List<PathSeg>();
                var revSteps = new List<ExecStep>();
                while (came.TryGetValue(k, out var e) && !e.prev.Equals(k))
                {
                    revPts.Add((k.Px, k.Py));
                    var (kcx, kcy) = StandCell(k.Px, k.Py);
                    if (e.pillar && e.digTiles != null)
                    {
                        // dig-up composite: expand into alternating "mine up 2 rows" / "pillar +2" sub-steps.
                        // revSteps is reversed afterwards, so append the forward sequence backwards.
                        var (prevCx, prevCy) = StandCell(e.prev.Px, e.prev.Py);
                        var sub = new List<ExecStep>();
                        for (int feetY = prevCy - 2; feetY >= kcy; feetY -= 2)
                        {
                            sub.Add(new ExecStep { Dig = true, DigDir = MineDir.Up, TargetCx = kcx, TargetCy = feetY });
                            sub.Add(new ExecStep { Pillar = true, TargetCx = kcx, TargetCy = feetY });
                        }
                        for (int si = sub.Count - 1; si >= 0; si--) revSteps.Add(sub[si]);
                    }
                    else if (e.pillar)
                    {
                        revSteps.Add(new ExecStep { Pillar = true, TargetCx = kcx, TargetCy = kcy, Frames = null });
                    }
                    else if (e.digTiles != null)
                    {
                        var (prevCx, prevCy) = StandCell(e.prev.Px, e.prev.Py);
                        MineDir d = kcy > prevCy ? MineDir.Down
                                  : kcx > prevCx ? MineDir.Right : MineDir.Left;
                        revSteps.Add(new ExecStep { Dig = true, DigDir = d, TargetCx = kcx, TargetCy = kcy, MineTiles = e.digTiles });
                    }
                    else if (e.frames != null)
                    {
                        var seg = new PathSeg();
                        int hold = 0; bool counting = true;
                        foreach (var fr in e.frames)
                        {
                            seg.IsJump |= fr.Jump;
                            if (counting && fr.Jump) hold++; else counting = false;
                            seg.Trail.Add((fr.Px, fr.Py));
                        }
                        seg.Hold = hold;
                        seg.FrameCount = e.frames.Count;
                        revSegs.Add(seg);
                        revSteps.Add(new ExecStep { Pillar = false, TargetCx = kcx, TargetCy = kcy, Frames = e.frames });
                    }
                    k = e.prev;
                }
                revPts.Reverse();
                revSegs.Reverse();
                revSteps.Reverse();
                res.Path = revPts;
                res.Segments = revSegs;
                res.Steps = revSteps;
                foreach (var st in revSteps) if (st.Frames != null) res.ExecFrames.AddRange(st.Frames);

                var segDesc = new System.Text.StringBuilder();
                foreach (var st in revSteps)
                {
                    if (st.Pillar) { segDesc.Append($" pillar->({st.TargetCx},{st.TargetCy})"); continue; }
                    if (st.Dig) { segDesc.Append($" dig({st.DigDir})->({st.TargetCx},{st.TargetCy})"); continue; }
                    bool hasPlace = st.Frames.Exists(fr => fr.Place);
                    bool jumped = st.Frames.Count > 0 && st.Frames[0].Jump;
                    string kind = !hasPlace ? "move" : (jumped ? "JPLACE" : "BRIDGE");
                    segDesc.Append($" {kind}->({st.TargetCx},{st.TargetCy}){st.Frames.Count}f");
                }
                if (!_silentPath) DiagLog.Write($"[ss-path] steps={revSteps.Count}{segDesc}");

                // 持久 clip 检查:扫每条移动边的帧,看玩家箱有没有和实心格重叠 —— 即【规划出的轨迹穿墙】。
                // 这个响了说明模拟器/边生成器在产出物理上不可能的路径,不是执行漂移。
                for (int si = 0; si < revSteps.Count; si++)
                {
                    var st = revSteps[si];
                    if (st.Frames == null) continue;
                    for (int fi = 0; fi < st.Frames.Count; fi++)
                    {
                        var fr = st.Frames[fi];
                        int x0 = (int)(fr.Px / 16f), x1 = (int)((fr.Px + PhysicsSimulator.PlayerW - 1) / 16f);
                        int y0 = (int)(fr.Py / 16f), y1 = (int)((fr.Py + PhysicsSimulator.PlayerH - 1) / 16f);
                        bool clip = false; int bx = 0, by = 0;
                        for (int yy = y0; yy <= y1 && !clip; yy++)
                            for (int xx = x0; xx <= x1 && !clip; xx++)
                            {
                                if (xx < 0 || yy < 0 || xx >= Main.maxTilesX || yy >= Main.maxTilesY) continue;
                                var t = Main.tile[xx, yy];
                                if (t.HasTile && Main.tileSolid[t.TileType] && !Main.tileSolidTop[t.TileType]) { clip = true; bx = xx; by = yy; }
                            }
                        if (clip)
                        {
                            DiagLog.Write($"[ss-clip] step#{si} frame#{fi}/{st.Frames.Count} pos=({fr.Px:0.#},{fr.Py:0.#}) box=[{x0}..{x1}]x[{y0}..{y1}] INSIDE solid ({bx},{by})");
                            break;
                        }
                    }
                }
            }
            DiagLog.Write($"[ss-jptally] ok={ctx.JpOk} noSpot={ctx.JpNoSpot} noLand={ctx.JpNoLand} fellThrough={ctx.JpFellThrough} slidOff={ctx.JpSlidOff}");
            DumpProf();
            DumpPlanTrace(res, start, goalWx, goalWy);
            return res;
        }

        // Dump the planned trajectory + terrain to ss_trace.json for offline matplotlib inspection.
        static void DumpPlanTrace(SSResult res, SSNode start, int goalWx, int goalWy)
        {
            try
            {
                var (sx, sy) = StandCell(start.Px, start.Py);
                int minX = Math.Min(sx, goalWx) - 8, maxX = Math.Max(sx, goalWx) + 8;
                int minY = Math.Min(sy, goalWy) - 8, maxY = Math.Max(sy, goalWy) + 8;

                var sb = new System.Text.StringBuilder();
                sb.Append("{");
                sb.Append($"\"found\":{(res.Found ? "true" : "false")},");
                sb.Append($"\"start\":[{sx},{sy}],\"goal\":[{goalWx},{goalWy}],");
                sb.Append($"\"region\":[{minX},{minY},{maxX},{maxY}],");

                sb.Append("\"traj\":[");
                for (int i = 0; i < res.ExecFrames.Count; i++)
                {
                    var f = res.ExecFrames[i];
                    if (i > 0) sb.Append(",");
                    sb.Append($"[{f.Px:0.#},{f.Py:0.#},{(f.Place ? 1 : 0)}]");
                }
                sb.Append("],");

                sb.Append("\"place\":[");
                bool firstP = true;
                foreach (var f in res.ExecFrames)
                    if (f.Place) { if (!firstP) sb.Append(","); sb.Append($"[{f.PlaceCx},{f.PlaceCy}]"); firstP = false; }
                sb.Append("],");

                sb.Append("\"explored\":[");
                for (int i = 0; i < res.Explored.Count; i++)
                {
                    var e = res.Explored[i];
                    if (i > 0) sb.Append(",");
                    sb.Append($"[{(e.px + PhysicsSimulator.PlayerW / 2f) / 16f:0.#},{(e.py + PhysicsSimulator.PlayerH) / 16f:0.#}]");
                }
                sb.Append("],");

                sb.Append("\"tiles\":[");
                bool firstT = true;
                for (int y = minY; y <= maxY; y++)
                    for (int x = minX; x <= maxX; x++)
                    {
                        if (x < 0 || y < 0 || x >= Main.maxTilesX || y >= Main.maxTilesY) continue;
                        var t = Main.tile[x, y];
                        if (!t.HasTile) continue;
                        int kind = Terraria.ID.TileID.Sets.Platforms[t.TileType] ? 2 : (((int)t.Slope != 0 || t.IsHalfBlock) ? 3 : 1);
                        if (!firstT) sb.Append(",");
                        sb.Append($"[{x},{y},{kind}]");
                        firstT = false;
                    }
                sb.Append("]}");

                string dir = System.IO.Path.Combine(Main.SavePath, "TerraBlindLogs");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "ss_trace.json"), sb.ToString());
            }
            catch { }
        }

        static IEnumerable<(SSNode next, List<PhysicsSimulator.ControlInput> frames, float cost, bool pillar, List<(int, int)> digTiles)> Expand(
            PlanCtx ctx, SSNode cur, PhysicsSimulator.Params ph, float goalCx, float goalFeetY, int[] holdOptions, int platformTile, bool hasPickaxe)
        {
            if (!cur.Grounded) yield break;

            float curH = Heuristic(ctx, cur, goalCx, goalFeetY, ph);

            // 先发普通 walk/jump,记录有没有哪条真降低了(带竖直权重的)启发值。朝墙横向蹭能降 x 距离但降不了 h,
            // 不算真进展。放置很贵,只有走跳都卡住时才建。
            bool anyProgress = false;
            bool vertProgress = false; // a plain jump that lands the player on a HIGHER cell (climbs a natural ledge)
            int dirToGoal = goalCx >= cur.Px ? 1 : -1;
            var (_, dcy) = StandCell(cur.Px, cur.Py);
            // dir 0 = 原地竖直跳:直接跳上头顶正上方的台阶,人在走不上去的坎前就这么干。
            // 对位还没做,身体本来就对齐时才成;没对齐时模拟自己会失败,边被跳过,无害。
            foreach (int dir in new[] { dirToGoal, -dirToGoal, 0 })
            {
                foreach (int hold in holdOptions)
                {
                    if (dir == 0 && hold == 0) continue;   // standing still, not a move
                    var seg = Prof(hold == 0 ? "walk" : "jump", () => SimulateSegment(cur, dir, hold, ph));
                    if (!seg.HasValue) continue;
                    // 进展用【原始逐格】场判,不用块粗化的 Heuristic:8x8 块内部 H 是平的,块内任何移动都读成"没进展",
                    // 于是普通跳能过的矮坎也会触发挖掘。
                    if (RawProgress(ctx, cur, seg.Value.node)) anyProgress = true;
                    var (_, segFeetCy) = StandCell(seg.Value.node.Px, seg.Value.node.Py);
                    if (segFeetCy < dcy) vertProgress = true;
                    yield return (seg.Value.node, seg.Value.frames, seg.Value.frames.Count, false, null);
                    foreach (var sp in SplitFall(cur, seg.Value.frames, platformTile))
                        yield return (sp.node, sp.frames, sp.cost, false, null);
                }
            }

            // 站平台(solidTop)上按住 Down 会掉下去。SimulateSegment 把平台当地板(Grounded),没这条就没有任何向下的边,
            // 目标在下方的平台格成死路(重规划风暴)。只在平台真撑着脚时发,否则和普通下落重复。
            {
                // 支撑判据看【两个碰撞箱列】,不是中心格:20px 的身体能踩在平台边缘而中心列悬空
                // ((3393,700) 神庙竖井卡死:中心支撑=空气 → 不生成 drop 边 → 只剩 H 上升的边 → 被 shock 打死)。
                var (fcx, fcy) = StandCell(cur.Px, cur.Py);
                int dropLc = (int)(cur.Px / 16f);
                int dropRc = (int)((cur.Px + PhysicsSimulator.PlayerW - 1f) / 16f);
                bool anyPlat = PathPlanner.PlatformPublic(dropLc, fcy + 1) || PathPlanner.PlatformPublic(dropRc, fcy + 1);
                bool anySolid = DigSolid(dropLc, fcy + 1) || DigSolid(dropRc, fcy + 1);
                bool plat = anyPlat && !anySolid;
                // 无条件打日志:SegDiag 只在【没有任何下降候选】时才开,而 (988,551) 那种平台蹭是有下降候选的,
                // 于是"为什么没生成 drop 边"在全部 trace 里根本没有答案。
                if (!plat) DiagLog.Trc($"[ss-drop] NULL: support plat={anyPlat} solid={anySolid} cols[{dropLc}..{dropRc}] row={fcy + 1}");
                if (plat)
                {
                    // 人从平台上下来是按住 Down 加一个方向,一路滑到真正的地板,不是停在下面一格。
                    // 左/右/直三种都发,让 A* 挑那条顺着斜缝滑到底的。
                    foreach (int ddir in new[] { dirToGoal, -dirToGoal, 0 })
                    {
                        var drop = Prof("drop", () => SimulateDrop(cur, ddir, ph));
                        if (drop.HasValue)
                            yield return (drop.Value.node, drop.Value.frames, drop.Value.frames.Count, false, null);
                        else
                            DiagLog.Trc($"[ss-drop] SIM-NULL dir={ddir} from cols[{dropLc}..{dropRc}] row={fcy + 1}");
                        if (drop.HasValue)
                            foreach (var sp in SplitFall(cur, drop.Value.frames, platformTile))
                                yield return (sp.node, sp.frames, sp.cost, false, null);
                    }
                }
            }

            // 平台不是到处枚举的,只在"场想去但物理挡住"的地方生成:沿梯度找第一个障碍,朝它另一侧发【一条】平台边。
            // pillar 是最末选择 —— 普通跳够得着的自然台阶(vertProgress)绝不该生柱子,人是徒手爬上去的。
            if (platformTile >= 0 || hasPickaxe)
                foreach (var pe in OnDemandPlatformEdges(ctx, cur, ph, platformTile, vertProgress, hasPickaxe, anyProgress))
                    yield return pe;
        }

        // MAX_SCAN = 一次跳的水平射程(格),从实时属性算(≈ maxRun × jumpHeight / gravity ≈ 7-8),随装备变,不写死 8。
        static int MaxScan(PhysicsSimulator.Params ph)
        {
            int jh = Player.jumpHeight > 0 ? Player.jumpHeight : 15;
            float g = ph.Gravity > 0 ? ph.Gravity : 0.4f;
            return (int)System.Math.Ceiling(ph.MaxRun * jh / g / 16f);
        }

        static IEnumerable<(SSNode next, List<PhysicsSimulator.ControlInput> frames, float cost, bool pillar, List<(int, int)> digTiles)> OnDemandPlatformEdges(
            PlanCtx ctx, SSNode cur, PhysicsSimulator.Params ph, int platformTile, bool vertProgress, bool hasPickaxe, bool anyProgress)
        {
            var (ccx, ccy) = StandCell(cur.Px, cur.Py);
            int curH = ctx.DistField != null && ctx.DistField.TryGetValue((ccx, ccy), out int h0) ? h0 : int.MaxValue;
            int hl = ctx.DistField != null && ctx.DistField.TryGetValue((ccx - 1, ccy), out int a) ? a : int.MaxValue;
            int hr = ctx.DistField != null && ctx.DistField.TryGetValue((ccx + 1, ccy), out int b) ? b : int.MaxValue;
            int gdir = hl < hr ? -1 : 1;                 // gradient-descent horizontal direction (toward lower maze H)
            int targetDir = gdir;
            int maxScan = MaxScan(ph);

            // 向上优先【原地跳放】:竖直跳起,弧顶拍一块平台,落上去,一次能升好几格;够不到 VertPlaceMinRise 才退回 pillar。
            // 向上无条件发,不加 upH<curH 的门:坑底正上方那格 H 本来就不低(得先升起来再走出去),加了门就把人钉死在坑底。
            if (platformTile >= 0 && MathF.Abs(cur.Vx) < VerticalJumpVxMax && ctx.DistField != null)
            {
                bool anyVertJumpPlace = false;
                foreach (int hold in BuildHoldOptions())
                {
                    var jp = Prof("jplaceV", () => JumpPlace(ctx, cur, 0, hold, ph, platformTile));
                    if (!jp.HasValue) continue;
                    var (jcx, jcy) = StandCell(jp.Value.node.Px, jp.Value.node.Py);
                    if (ccy - jcy < VertPlaceMinRise) continue; // too short → pillar does it cheaper
                    anyVertJumpPlace = true;
                    yield return (jp.Value.node, jp.Value.frames, jp.Value.frames.Count + JumpPlaceCost, false, null);
                }
                // 只发【爬到顶】这一条,不再按"每跳 2 格"铺一串中间落点:一跳升几格由地形定(1~3),
                // 承诺了就兑现不了 —— 上一轮 50 条 pillar 边 45 条 MISS,31 条差的正是 ±2 行。
                if (!anyVertJumpPlace && !vertProgress && SkillExecutor.CanPillarFrom(ccx, ccy, out int topFeetY) && topFeetY < ccy)
                {
                    float npx = ccx * 16f + 8f - PhysicsSimulator.PlayerW / 2f;
                    float npy = (topFeetY + 1) * 16f - PhysicsSimulator.PlayerH;
                    var node = new SSNode { Px = npx, Py = npy, Vx = 0f, Vy = 0f, Grounded = true };
                    yield return (node, null, ((ccy - topFeetY) / 2f) * 43f, true, null);
                }
            }

            // 横向跳放也无条件发。原先只在 isWall 分支里有,而斜坡永远不判 wall(StepUp 让走路继续推进),
            // 于是"朝坡面跳一下垫一级"这个人类爬坡动作从来没生成过,唯一的上升手段只剩挖上去。
            bool anyLateralJp = false;
            if (platformTile >= 0 && ctx.DistField != null)
                foreach (int ldir in new[] { gdir, -gdir })
                    foreach (int hold in BuildHoldOptions())
                    {
                        var jp = Prof("jplaceL", () => JumpPlace(ctx, cur, ldir, hold, ph, platformTile));
                        if (jp.HasValue) { anyLateralJp = true; yield return (jp.Value.node, jp.Value.frames, jp.Value.frames.Count + JumpPlaceCost, false, null); }
                    }

            // 从崖边自由落体:脚下那列是空的(悬崖/竖井而非墙)就一路掉到真地板,和人一样(mined=0)。
            // DigDown 只探 DigMaxScan 深且必须挖,24 格的崖探不到底就作废;这条边覆盖任意深度,且排在 DigDown 前面。
            {
                var fall = Prof("fall", () => FreeFall(cur, gdir, ph));
                if (fall.HasValue)
                    yield return (fall.Value.node, fall.Value.frames, fall.Value.frames.Count, false, null);
            }

            // VERTICAL DOWN: worth-it test is inside DigDown (H drop >= margin AND no lateral walk reaches an
            // equally-low cell), not !anyProgress — the latter made dig a last resort so A* detoured first.
            if (hasPickaxe && ctx.DistField != null)
            {
                var dd = Prof("digdown", () => DigDown(ctx, cur, ccx, ccy, curH, gdir, maxScan));
                if (dd.HasValue)
                    yield return (dd.Value.node, null, dd.Value.cost, false, dd.Value.tiles);
            }

            // 横挖无条件发。挂在 isWall 分类后面(依赖脆弱的 walkprobe)时,姿势一别扭(脚踩半砖、头顶天花板)分类就误判,
            // 一条挖掘边都不生成,Expand 空了人就"卡住",而人只需侧身挖一下。
            if (hasPickaxe && ctx.DistField != null)
                foreach (int ddir in new[] { gdir, -gdir })
                {
                    var dw = Prof("digwall", () => DigThroughWall(ctx, ddir, ccx, ccy, curH));
                    if (dw.HasValue)
                        yield return (dw.Value.node, null, dw.Value.cost, false, dw.Value.tiles);
                }

            // 向上挖也改成无条件(原先门在 !anyProgress && Vx<max,别扭姿势下会把它压掉)。
            // 仍然要 platformTile —— 那是物理必需(得有砖垫脚),不是启发式的门。
            if (hasPickaxe && platformTile >= 0 && ctx.DistField != null)
            {
                var du = Prof("digup", () => DigUp(ctx, cur, ccx, ccy, curH));
                if (du.HasValue)
                    yield return (du.Value.node, null, du.Value.cost, true, du.Value.tiles);
            }

            // 障碍 = 【普通行走再也推不动】的地方。Step 里含 Collision.StepUp,所以它诚实地爬半砖/缓坡/一格台阶,只在真过不去时停。
            // 静态 IsBlockPublic 扫描看不见斜坡半砖,会报 obsX=none,于是死在那儿 —— 让走路的停止点定义障碍。
            int obsX;
            {
                var walk = Prof("walkprobe", () => SimulateSegment(cur, gdir, 0, ph));
                int walkCx = walk.HasValue ? StandCell(walk.Value.node.Px, walk.Value.node.Py).cx : ccx;
                // if the plain walk advanced past where the maze wants (toward goal), there's no blocking obstacle
                if ((gdir > 0 && walkCx > ccx) || (gdir < 0 && walkCx < ccx))
                {
                    // walk made ground; the obstacle (if any) is the first cell beyond where it stopped
                    obsX = walkCx + gdir;
                }
                else
                {
                    obsX = ccx + gdir; // walk couldn't move at all → obstacle is right at the foot
                }
            }
            if (obsX < 0 || obsX >= Main.maxTilesX) yield break;
            // 深谷覆盖:掉进深谷的走路会沿谷底一路"推进",把远处的墙报成障碍(22 格外,跳放够不着 → 一条桥候选都没有)。
            // (3277,1024) 因此每个候选都是 30 格翻滚,而场的路线是从空中往东飘 —— 前方某列探不到底就把那个崖边当【缺口】。
            for (int c = ccx + gdir; c != obsX && c >= 0 && c < Main.maxTilesX; c += gdir)
            {
                if (DigSolid(c, ccy) || DigSolid(c, ccy - 1) || DigSolid(c, ccy - 2)) break;   // wall first → wall branch
                bool landing = false;
                for (int ry = ccy + 1; ry <= ccy + ChasmProbeDepth; ry++)
                    if (PathPlanner.IsFloorPublic(c, ry)) { landing = true; break; }
                if (!landing) { obsX = c; break; }
            }
            // classify: a cell with a collision body (full / half-brick / slope, via DigSolid) is a WALL; otherwise
            // (no floor under it) it's a GAP. matches what actually stopped the walk.
            bool isWall = DigSolid(obsX, ccy) || DigSolid(obsX, ccy - 1) || DigSolid(obsX, ccy - 2);
            bool isGap = !isWall && !PathPlanner.IsFloorPublic(obsX, ccy + 1);
            DiagLog.Trc($"[ss-bridge-dir] from=({ccx},{ccy}) gdir={gdir} targetDir={targetDir} obsX={obsX} wall={isWall} gap={isGap} maxScan={maxScan}");
            if (!isWall && !isGap) yield break;          // walk can pass freely → plain walk/jump handles it

            if (isWall)
            {
                // 墙:跳放候选上面已经无条件发过了,这里只剩兜底 —— 一个 hold 都找不到落点(纯陡壁)才退回 pillar。
                if (platformTile >= 0)
                {
                    if (!anyLateralJp && !vertProgress && SkillExecutor.CanPillarFrom(ccx, ccy, out int topFeetY) && topFeetY < ccy)
                    {
                        float npx = ccx * 16f + 8f - PhysicsSimulator.PlayerW / 2f;
                        for (int fy = ccy - 2; fy >= topFeetY; fy -= 2)
                        {
                            float npy = (fy + 1) * 16f - PhysicsSimulator.PlayerH;
                            var node = new SSNode { Px = npx, Py = npy, Vx = 0f, Vy = 0f, Grounded = true };
                            yield return (node, null, ((ccy - fy) / 2) * 43f, true, null);
                        }
                    }
                }
                // (horizontal dig now emitted unconditionally above, both directions — not gated on this isWall branch)
            }
            else if (platformTile >= 0)
            {
                // 缺口:优先【移动跳放横穿】(朝对岸跳,下降弧上垫一块,落上去),这是人的做法。
                // 一个 hold 都跨不过去(缺口太宽)才退回原地搭桥。两者标价相同,由可达性决定,不由价格。
                bool anyAcross = false;
                foreach (int hold in BuildHoldOptions())
                {
                    var jp = Prof("jplaceX", () => JumpPlaceAcross(ctx, cur, gdir, hold, ph, platformTile, curH));
                    if (jp.HasValue) { anyAcross = true; yield return (jp.Value.node, jp.Value.frames, jp.Value.frames.Count + JumpPlaceCost, false, null); }
                }
                if (!anyAcross)
                {
                    var br = Prof("bridge", () => BridgePlace(cur, gdir, ph, platformTile));
                    if (br.HasValue)
                        yield return (br.Value.node, br.Value.frames, br.Value.frames.Count + BridgeCost, false, null);
                }
            }
        }

        const int ChasmProbeDepth = 6;   // a column with no landing within this many rows below the stance is a chasm ledge (≈ max jump-back-out height)
        const int DigMaxScan = 12;   // a wall this many tiles wide stops dig (mining wider isn't worth it vs routing around)
        const int DigWorthMargin = 4; // dig-down only when the landing's H is at least this much lower (clearly worth it)

        // 实心【含】斜坡半砖:IsBlock 故意排除它们(走路逻辑当可通行),但斜半砖照样撑住人 —— 竖井下降就卡在这儿。
        // 任何能托住碰撞箱的东西都得进挖掘清单。
        static bool DigSolid(int x, int y)
        {
            if (x < 0 || y < 0 || x >= Main.maxTilesX || y >= Main.maxTilesY) return false;
            var t = Main.tile[x, y];
            return t.HasTile && Main.tileSolid[t.TileType] && !Main.tileSolidTop[t.TileType];
        }

        // 只挖脚下这一格(人 20px 宽 = 两列),不挖到落点的竖井:深descent 靠每周期重规划自然长出来。
        // 竖井版有 "12 格内没落点就 null" 的死角,厚墙前把人钉住过。
        static (SSNode node, List<(int wx, int wy)> tiles, float cost)? DigDown(PlanCtx ctx, SSNode cur, int ccx, int ccy, int curH, int gdir, int maxScan)
        {
            float centerPx = cur.Px + PhysicsSimulator.PlayerW / 2f;
            int c2 = centerPx > ccx * 16f + 8f ? ccx + 1 : ccx - 1;
            int y = ccy + 1;   // the single row directly under the feet
            var tiles = new List<(int, int)>();
            float cost = 0f;
            foreach (int c in new[] { ccx, c2 })
                if (DigSolid(c, y))
                {
                    int fc = DigTable.CostFrames(c, y);
                    if (fc >= DigTable.Unmineable) { if (SegDiag) DiagLog.Write($"[ss-digdown] NULL: unmineable ({c},{y})"); return null; }   // unbreakable (attached object / pick too weak) → route around
                    cost += fc;
                    tiles.Add((c, y));
                }
            if (tiles.Count == 0) { if (SegDiag) DiagLog.Write("[ss-digdown] NULL: nothing solid underfoot"); return null; }   // free fall / walk handles it
            // landing = standing in the just-dug cell; the row below (ccy+2, still undug rock) is the floor. Only worth
            // it if that cell is lower H (toward goal). If ccy+2 is ALSO open, the next cycle's free-fall/dig continues.
            bool hasH = ctx.DistField.TryGetValue((ccx, y), out int lh);
            if (!(hasH && lh < curH)) { if (SegDiag) DiagLog.Write($"[ss-digdown] NULL: landing H {(hasH ? lh.ToString() : "off-field")} !< {curH}"); return null; }
            float npx = ccx * 16f + 8f - PhysicsSimulator.PlayerW / 2f;
            float npy = (y + 1) * 16f - PhysicsSimulator.PlayerH;
            var node = new SSNode { Px = npx, Py = npy, Vx = 0f, Vy = 0f, Grounded = true };
            return (node, tiles, cost);
        }

        // 挖穿封死的天花板:每周期挖头顶两行(两列,和 DigDown 同样的身体宽度理由),再 pillar 跳两格上去。
        // 只在天花板真封死(第一周期挖到东西)且突破格 H 更低时才产出 —— 头顶本来就空的归跳跃/跳放/pillar 管。
        static (SSNode node, List<(int wx, int wy)> tiles, float cost)? DigUp(PlanCtx ctx, SSNode cur, int ccx, int ccy, int curH)
        {
            // 必须和 SkillExecutor 实时检查的列【完全一致】,否则 DigUp 挖的是执行器根本不看的格,
            // 而执行器要看的没挖 → 爬到一半撞上"已挖过"的天花板 ((3242,299)↔(3242,300) 卡死:一个按格心偏移取列,一个按实时像素取列)。
            int leftCol = (int)(cur.Px / 16f);
            int rightCol = (int)((cur.Px + PhysicsSimulator.PlayerW - 1) / 16f);
            var tiles = new List<(int, int)>();
            float cost = 0f;
            // 挖满 [feetY-2, ccy-3] 整段,不跳着挖:pillar 一跳升几格是随地形变的(1~3格),
            // 按"每跳2格"只挖 ccy-1-2k/ccy-2-2k,人升 3 格时 ccy-5 没挖 → 撞上没挖的天花板卡死。
            int prevTop = ccy - 2;
            for (int k = 1; k * 2 <= DigMaxScan; k++)
            {
                int newTop = ccy - 2 * k - 2;
                for (int y = prevTop - 1; y >= newTop; y--)
                    foreach (int c in new[] { leftCol, rightCol })
                        if (DigSolid(c, y))
                        {
                            int fc = DigTable.CostFrames(c, y);
                            if (fc >= DigTable.Unmineable) { if (SegDiag) DiagLog.Write($"[ss-digup] NULL: unmineable ({c},{y})"); return null; }
                            cost += fc;
                            tiles.Add((c, y));
                        }
                prevTop = newTop;
                if (k == 1 && tiles.Count == 0) { if (SegDiag) DiagLog.Write("[ss-digup] NULL: ceiling already open"); return null; }
                int feetY = ccy - 2 * k;
                cost += 43f;
                if (ctx.DistField.TryGetValue((ccx, feetY), out int lh) && lh < curH)
                {
                    float npx = ccx * 16f + 8f - PhysicsSimulator.PlayerW / 2f;
                    float npy = (feetY + 1) * 16f - PhysicsSimulator.PlayerH;
                    return (new SSNode { Px = npx, Py = npy, Vx = 0f, Vy = 0f, Grounded = true }, tiles, cost);
                }
            }
            return null;
        }

        // 原子横挖:只挖相邻那一列的 3 个身体行,踏进去。不挖到远处落点的隧道 —— 闭环每周期重规划,厚墙靠 dig→dig→dig 逐格破,
        // 没有 "12 格内没落点就 null" 的死角 ((744,998) 卡死),也不会挖出下一周期发现多余的隧道。
        static (SSNode node, List<(int wx, int wy)> tiles, float cost)? DigThroughWall(PlanCtx ctx, int dir, int ccx, int ccy, int curH)
        {
            int x = ccx + dir;
            var tiles = new List<(int, int)>();
            float cost = 0f;
            foreach (int y in new[] { ccy, ccy - 1, ccy - 2 })
                if (DigSolid(x, y))
                {
                    int fc = DigTable.CostFrames(x, y);
                    if (fc >= DigTable.Unmineable) { DiagLog.Trc($"[ss-digwall] from=({ccx},{ccy}) dir={dir} UNMINEABLE at ({x},{y}) → null"); return null; }
                    cost += fc;
                    tiles.Add((x, y));
                }
            if (tiles.Count == 0) return null;   // adjacent column already clear → plain walk handles it, not a dig
            bool toward = ctx.DistField != null && ctx.DistField.TryGetValue((x, ccy), out int hx) && hx < curH;
            if (!toward) return null;
            float npx = x * 16f + 8f - PhysicsSimulator.PlayerW / 2f;
            float npy = (ccy + 1) * 16f - PhysicsSimulator.PlayerH;
            var node = new SSNode { Px = npx, Py = npy, Vx = 0f, Vy = 0f, Grounded = true };
            return (node, tiles, cost);
        }

        const float VerticalJumpVxMax = 0.5f;
        const int   VertPlaceMinRise = 3;   // vertical jump-place below this many tiles isn't worth it → pillar instead
        const int   PlatformMaxDropTiles = 4; // scan this many tiles below the arc apex for a placeable+landable spot
        const float HProgressEps = 1.5f;

        // temporary bisection switches: flip one off, rebuild, see which filter was killing valid plans
        const bool F_Gate = true;        // anyProgress gating of placement
        const bool F_Dominance = true;   // velocity dominance pruning
        const bool F_Brake = false;      // reject jump-place when brake can't settle
        const bool F_LandOnPlat = false; // true killed ~all jump-place (fellThrough) → pillar overuse; 6/2 working ver had no such check
        const bool F_DescentOnly = true; // place only during descent (vy>0)
        const bool F_Trend = true;       // two-phase up-then-left heuristic bias
        const bool F_PillarNeedNoLateral = true; // pillar only when no lateral walk-out exists (false = old behavior)

        const float JumpPlaceCost = 30f; // bias: prefer plain walk/jump; place only when it opens a path
        const float BridgeCost = 30f;    // same as jump-place: consumes a platform, use only to open a path

        // 跳起来放一块:扫弧线找第一个"脚下格空 + 紧邻真支撑"的帧,放平台,落上去。
        // 放置的砖【不】存进节点(节点保持纯物理),落点只是站在新平台顶上。纯空中垒柱交给宏。
        static bool AnyPlaceableInReach(SSNode cur, int dir, PhysicsSimulator.Params ph)
        {
            var (ccx, cfy) = StandCell(cur.Px, cur.Py);
            int reach = MaxScan(ph);
            int apex = (Player.jumpHeight > 0 ? Player.jumpHeight : 15) + 1;
            for (int dx = 0; dx <= reach; dx++)
            {
                int x = ccx + dir * dx;
                for (int y = cfy - apex; y <= cfy + PlatformMaxDropTiles; y++)
                    if (CanPlaceReal(x, y)) return true;
                // dir=0 walks x nowhere — cover the neighbour columns the placement scan now tries
                if (dir == 0 && dx == 0)
                    for (int sx = -1; sx <= 1; sx += 2)
                        for (int y = cfy - apex; y <= cfy + PlatformMaxDropTiles; y++)
                            if (CanPlaceReal(ccx + sx, y)) return true;
            }
            return false;
        }

        static (SSNode node, List<PhysicsSimulator.ControlInput> frames)? JumpPlace(
            PlanCtx ctx, SSNode cur, int dir, int hold, PhysicsSimulator.Params ph, int platformTile)
        {
            if (hold == 0) return null; // need to leave the ground

            // O(1) 早退:整个跳跃射程盒里一个可放格都没有,下面的弧线模拟必然 noSpot,直接跳过。
            // 盒扫得比真弧宽,所以绝不会误杀一条真实存在的跳放。
            if (!AnyPlaceableInReach(cur, dir, ph)) { ctx.JpNoSpot++; return null; }

            // simulate the free arc to find where to place
            var s = new PhysicsSimulator.State
            {
                Px = cur.Px, Py = cur.Py, Vx = cur.Vx, Vy = cur.Vy,
                Grounded = true, JumpFramesLeft = hold,
            };
            // find the arc apex (highest point): the platform must go AT or BELOW the apex foot — a platform above
            // the apex blocks the ascent / can't be reached. simulate the free arc, track the apex foot cell.
            int apexFootCx = int.MinValue, apexFootCy = 0;
            float minPy = float.MaxValue;
            for (int f = 0; f < MaxSegFrames; f++)
            {
                var input = new PhysicsSimulator.ControlInput { Right = dir > 0, Left = dir < 0, Jump = f < hold };
                s = PhysicsSimulator.Step(s, input, ph);
                if (s.Py < minPy)
                {
                    minPy = s.Py;
                    apexFootCx = (int)((s.Px + PhysicsSimulator.PlayerW / 2f) / 16f);
                    apexFootCy = (int)((s.Py + PhysicsSimulator.PlayerH) / 16f);
                }
                if (s.Vy > 0f && s.Py > minPy + 1f) break; // past apex and descending — apex is locked
            }
            if (apexFootCx == int.MinValue) { ctx.JpNoSpot++; return null; }

            // 从弧顶脚下【那一格的下面】往下扫(平台放在弧顶脚所在格接不住人,人会穿过去掉回原地)。
            // 列的顺序:弧顶列优先,再左右邻列 —— 斜坡上能锚住的格通常偏向坡面一列,只扫自己那列会一无所获。
            int startFootCy = (int)((cur.Py + PhysicsSimulator.PlayerH) / 16f);
            int placeCx = int.MinValue, placeCy = 0;
            (SSNode node, List<PhysicsSimulator.ControlInput> frames)? seg = null;
            foreach (int pcxTry in new[] { apexFootCx, apexFootCx + 1, apexFootCx - 1 })
            {
                for (int py = apexFootCy + 1; py <= apexFootCy + PlatformMaxDropTiles; py++)
                {
                    if (!CanPlaceReal(pcxTry, py)) continue;
                    if (!ClearOfBody(minPy, pcxTry, py)) continue;
                    var trySeg = SimulateWithPlatform(cur, dir, hold, ph, pcxTry, py, platformTile);
                    if (!trySeg.HasValue || !trySeg.Value.node.Grounded) continue;
                    int landFc = (int)((trySeg.Value.node.Py + PhysicsSimulator.PlayerH) / 16f);
                    if (landFc >= startFootCy) continue; // landed at/below start = fell back, not a rise
                    placeCx = pcxTry; placeCy = py; seg = trySeg; break;
                }
                if (placeCx != int.MinValue) break;
            }
            if (placeCx == int.MinValue) { ctx.JpNoSpot++; return null; }
            float probeVy = 0f, probeFootPy = 0f;
            // 必须真的落【在】那块平台上。穿过去落到别处的"放了但没接住"边毫无用处,放进来还会用廉价空操作淹没搜索(指数爆炸)。
            int landFeetCy = (int)((seg.Value.node.Py + PhysicsSimulator.PlayerH) / 16f);
            if (F_LandOnPlat && landFeetCy != placeCy)
            {
                if (ctx.JpFellThrough < 12)
                    DiagLog.Write($"[ss-ft] place=({placeCx},{placeCy}) hold={hold} dir={dir} probeVy={probeVy:0.#} probeFootPy={probeFootPy:0.#} platTopPy={placeCy * 16} landFeetCy={landFeetCy}");
                ctx.JpFellThrough++; return null;
            }
            if (!MarkPlaceFrame(seg.Value.frames, placeCx, placeCy)) { ctx.JpNoSpot++; return null; } // unreachable placement

            // 落在一格宽的平台上带着残余 vx 下一帧就滑下去了 —— 追加一段反向按键刹车,落点节点取真正停稳的位置。
            // 只有滑到脱离地面(掉下去)才判这条边无效。
            if (F_Brake)
            {
                var braked = AppendBrake(seg.Value.node, seg.Value.frames, ph);
                if (braked == null) { ctx.JpSlidOff++; return null; }
                ctx.JpOk++;
                return braked.Value;
            }
            ctx.JpOk++;
            return seg.Value;
        }

        // 移动跳放横穿:和 JumpPlace(只收比起点高的落点)不同,这里同高/更低也收,只要落点 H 真的降。
        // 放置砖不存进节点(纯物理键,避免组合爆炸 —— 118ab5f 的教训)。H 门限住扇出。
        static (SSNode node, List<PhysicsSimulator.ControlInput> frames)? JumpPlaceAcross(
            PlanCtx ctx, SSNode cur, int dir, int hold, PhysicsSimulator.Params ph, int platformTile, int curH)
        {
            if (hold == 0 || dir == 0) return null;
            if (!AnyPlaceableInReach(cur, dir, ph)) { ctx.JpNoSpot++; return null; }   // O(1) skip: no drop spot in reach

            var s = new PhysicsSimulator.State
            {
                Px = cur.Px, Py = cur.Py, Vx = cur.Vx, Vy = cur.Vy,
                Grounded = true, JumpFramesLeft = hold,
            };
            // walk the free arc; once descending, the FIRST placeable+supported foot cell is the spot.
            for (int f = 0; f < MaxSegFrames; f++)
            {
                var input = new PhysicsSimulator.ControlInput { Right = dir > 0, Left = dir < 0, Jump = f < hold };
                s = PhysicsSimulator.Step(s, input, ph);
                if (s.Vy <= 0f) continue; // ascending/apex — a platform here can't catch the player
                int fcx = (int)((s.Px + PhysicsSimulator.PlayerW / 2f) / 16f);
                int fcy = (int)((s.Py + PhysicsSimulator.PlayerH + 1f) / 16f);
                if (!CanPlaceReal(fcx, fcy)) continue;
                var seg = SimulateWithPlatform(cur, dir, hold, ph, fcx, fcy, platformTile);
                if (!seg.HasValue || !seg.Value.node.Grounded) continue;
                var (lcx, lcy) = StandCell(seg.Value.node.Px, seg.Value.node.Py);
                if (!(ctx.DistField != null && ctx.DistField.TryGetValue((lcx, lcy), out int lh) && lh < curH)) return null;
                if (!MarkPlaceFrame(seg.Value.frames, fcx, fcy)) continue; // unreachable placement → try a different landing
                return seg.Value;
            }
            return null;
        }

        const float BrakeVxEps = 0.3f;   // |Vx| below this counts as settled
        const int   BrakeMaxFrames = 30;

        // 落在窄平台上带残速要反压刹车。落点节点取【实际】停稳的位置 —— 墙或平台边缘挡住滑动也是合法站位,
        // 只有滑到失去地面接触才算这条边废了。
        static (SSNode node, List<PhysicsSimulator.ControlInput> frames)? AppendBrake(
            SSNode land, List<PhysicsSimulator.ControlInput> frames, PhysicsSimulator.Params ph)
        {
            var s = new PhysicsSimulator.State { Px = land.Px, Py = land.Py, Vx = land.Vx, Vy = land.Vy, Grounded = true };
            for (int k = 0; k < BrakeMaxFrames && MathF.Abs(s.Vx) > BrakeVxEps; k++)
            {
                var input = new PhysicsSimulator.ControlInput { Left = s.Vx > 0f, Right = s.Vx < 0f };
                s = PhysicsSimulator.Step(s, input, ph);
                input.Px = s.Px; input.Py = s.Py;
                frames.Add(input);
                if (!s.Grounded) return null; // slid off and started falling — not a valid stand
            }
            var node = new SSNode { Px = s.Px, Py = s.Py, Vx = s.Vx, Vy = s.Vy, Grounded = true };
            return (node, frames);
        }

        // 目标格是空的(或可砍) + 至少一个真邻居提供支撑(方块或背景墙)。
        // 跳到最高点时那格得在身体下面【留够一格】:贴着箱底算的落点,起跳差几像素人还在箱子里,执行侧一律否决 (1346,192)。
        static bool ClearOfBody(float apexPy, int wx, int wy)
        {
            int bodyBottomRow = (int)((apexPy + PhysicsSimulator.PlayerH - 1) / 16f);
            return wy > bodyBottomRow + 1;
        }

        static bool CanPlaceReal(int wx, int wy)
        {
            if (wx < 0 || wy < 0 || wx >= Main.maxTilesX || wy >= Main.maxTilesY) return false;
            var t = Main.tile[wx, wy];
            if (t.HasTile && !Main.tileCut[t.TileType]) return false;
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    int nx = wx + dx, ny = wy + dy;
                    if (nx < 0 || ny < 0 || nx >= Main.maxTilesX || ny >= Main.maxTilesY) continue;
                    var nb = Main.tile[nx, ny];
                    if (nb.HasTile || nb.WallType > 0) return true;
                }
            return false;
        }

        // 临时把一块平台写进 Main.tile,模拟,再还原 —— 那块砖不进节点,只为这一段的落地物理存在。
        // 搭桥要精确停在新砖那一格(一块砖只有一格站立空间,走完整段会滑出去掉下)。一次一块,贪心每步重挑。
        static (SSNode node, List<PhysicsSimulator.ControlInput> frames)? BridgePlace(
            SSNode cur, int dir, PhysicsSimulator.Params ph, int platformTile)
        {
            // 没有背景墙时锚点必须紧邻已有支撑,所以 placeCx 只可能是 baseCol±1,没有第二种选择。
            var (lcol, rcol) = Predicates.BodyCols(cur.Px, PhysicsSimulator.PlayerW);
            int footRow = (int)((cur.Py + PhysicsSimulator.PlayerH) / 16f);
            int baseCol = int.MinValue;
            if (dir > 0) { for (int c = rcol; c >= lcol; c--) if (PathPlanner.IsFloorPublic(c, footRow)) { baseCol = c; break; } }
            else { for (int c = lcol; c <= rcol; c++) if (PathPlanner.IsFloorPublic(c, footRow)) { baseCol = c; break; } }
            if (baseCol == int.MinValue) return null;          // no supported foot column — nothing to extend from
            int placeCx = baseCol + dir, placeCy = footRow;
            int scy = footRow - 1;                              // standing (head) row above the support
            if (PathPlanner.IsBlockPublic(placeCx, placeCy)) return null;       // already solid there
            if (PathPlanner.IsBlockPublic(placeCx, scy)) return null;          // walk target (head) blocked
            DiagLog.Write($"[bridge-dbg] dir={dir} px={cur.Px:0.#} footCols[{lcol}..{rcol}] baseCol={baseCol} placeCx={placeCx} placeCy={placeCy}");

            var t = Main.tile[placeCx, placeCy];
            bool oHad = t.HasTile; ushort oType = t.TileType; bool oHalf = t.IsHalfBlock; var oSlope = t.Slope;
            t.HasTile = true; t.TileType = (ushort)platformTile; t.IsHalfBlock = false; t.Slope = Terraria.ID.SlopeType.Solid;
            try
            {
                var s = new PhysicsSimulator.State { Px = cur.Px, Py = cur.Py, Vx = cur.Vx, Vy = cur.Vy, Grounded = true };
                // 走到【刚好不再压住 baseCol】,不是走到新砖格心:格心那个位置身体还骑在原来那列上,
                // 落点标签没变被自环删掉,而 bridge 的意义正是脱离原列(腾出干净的头顶去 pillar/跳放)。
                float standPxTarget = dir > 0 ? (baseCol + 1) * 16f : baseCol * 16f - PhysicsSimulator.PlayerW;
                float targetCenterPx = standPxTarget + PhysicsSimulator.PlayerW / 2f;
                var frames = new List<PhysicsSimulator.ControlInput>();
                for (int f = 0; f < MaxSegFrames; f++)
                {
                    float centerNow = s.Px + PhysicsSimulator.PlayerW / 2f;
                    bool past = dir > 0 ? centerNow >= targetCenterPx : centerNow <= targetCenterPx;
                    // approach: press dir until center reaches target cell; then brake (press opposite) to settle.
                    var input = new PhysicsSimulator.ControlInput
                    {
                        Right = !past ? dir > 0 : s.Vx < -0.05f,
                        Left = !past ? dir < 0 : s.Vx > 0.05f,
                    };
                    s = PhysicsSimulator.Step(s, input, ph);
                    input.Px = s.Px; input.Py = s.Py;
                    frames.Add(input);
                    if (past && MathF.Abs(s.Vx) < 0.1f) break; // settled on the new tile
                }
                if (frames.Count == 0) return null;
                // 落点是【几何上确定】的:平台放在 (placeCx,placeCy),人就站在它上面,不需要相信近似模拟器的 Grounded。
                // 老的 "!s.Grounded → null" 会在模拟器把某帧误读成腾空时,杀掉坑边一条完全好用的桥。
                var f0 = frames[0];                            // place on the first frame (before stepping over)
                f0.Place = true; f0.PlaceCx = placeCx; f0.PlaceCy = placeCy;
                frames[0] = f0;
                float standPx = standPxTarget;
                float standPy = placeCy * 16f - PhysicsSimulator.PlayerH;   // feet on top of the new tile (row placeCy)
                var node = new SSNode { Px = standPx, Py = standPy, Vx = 0f, Vy = 0f, Grounded = true };
                return (node, frames);
            }
            finally { t.HasTile = oHad; t.TileType = oType; t.IsHalfBlock = oHalf; t.Slope = oSlope; }
        }

        static (SSNode node, List<PhysicsSimulator.ControlInput> frames)? SimulateWithPlatform(
            SSNode cur, int dir, int hold, PhysicsSimulator.Params ph, int cx, int cy, int platformTile)
        {
            var t = Main.tile[cx, cy];
            bool oHad = t.HasTile; ushort oType = t.TileType; bool oHalf = t.IsHalfBlock;
            var oSlope = t.Slope;
            // 脆:保留原生 slope,【不】强制 Solid。Solid 平台会挡住原地竖直跳放的上升 —— 人永远够不到,落回原地成自环。
            // 真平台是 solidTop:上升穿过去,下降接住。
            t.HasTile = true; t.TileType = (ushort)platformTile; t.IsHalfBlock = false;
            try { return SimulateSegment(cur, dir, hold, ph); }
            finally { t.HasTile = oHad; t.TileType = oType; t.IsHalfBlock = oHalf; t.Slope = oSlope; }
        }

        // Vanilla placement reach (Player tileRangeX/Y, static). A tile (cx,cy) is reachable from a player whose
        // top-left is at (px,py) iff it lies in this rectangle. Used to pick the placement frame.
        const int TileRangeX = 5, TileRangeY = 4;
        static bool CanReachTile(float px, float py, int cx, int cy)
        {
            int loX = (int)(px / 16f) - TileRangeX;
            int hiX = (int)((px + PhysicsSimulator.PlayerW) / 16f) + TileRangeX - 1;
            int loY = (int)(py / 16f) - TileRangeY;
            int hiY = (int)((py + PhysicsSimulator.PlayerH) / 16f) + TileRangeY - 2;
            return cx >= loX && cx <= hiX && cy >= loY && cy <= hiY;
        }

        // 硬规则:放置必须在弧顶【或之后】(vy >= 0),绝不在上升段 —— 上升时人还在穿过那一行,砖要么没支撑要么被穿过。
        // 弧顶常常够不到远/低的落点(够不着 → UseItem 静默失败 → 腾空僵住 → 摔),所以等下降把人带进射程再放。
        static bool MarkPlaceFrame(List<PhysicsSimulator.ControlInput> frames, int cx, int cy)
        {
            int apex = 0;
            float minPy = float.MaxValue;
            for (int i = 0; i < frames.Count; i++)
                if (frames[i].Py < minPy) { minPy = frames[i].Py; apex = i; }
            for (int i = apex; i < frames.Count; i++)
            {
                if (!CanReachTile(frames[i].Px, frames[i].Py, cx, cy)) continue;
                var fr = frames[i];
                fr.Place = true; fr.PlaceCx = cx; fr.PlaceCy = cy;
                frames[i] = fr;
                return true;
            }
            return false;
        }

        // 人从平台上下来:按住 Down 直到离开起始平台(这样才能落在下面的平台/地板而不是一路穿过去),方向键全程按住。
        static (SSNode node, List<PhysicsSimulator.ControlInput> frames)? SimulateDrop(SSNode cur, int dir, PhysicsSimulator.Params ph)
        {
            var s = new PhysicsSimulator.State { Px = cur.Px, Py = cur.Py, Vx = cur.Vx, Vy = cur.Vy, Grounded = true };
            var frames = new List<PhysicsSimulator.ControlInput>();
            float startFeetY = cur.Py + PhysicsSimulator.PlayerH;
            bool leftStart = false; // must clear the start platform before a grounded frame counts as landing
            for (int f = 0; f < MaxSegFrames; f++)
            {
                bool stillOnStart = (s.Py + PhysicsSimulator.PlayerH) < startFeetY + 16f;
                var input = new PhysicsSimulator.ControlInput { Down = stillOnStart, Left = dir < 0, Right = dir > 0 };
                s = PhysicsSimulator.Step(s, input, ph);
                input.Px = s.Px; input.Py = s.Py; input.Vx = s.Vx; input.Vy = s.Vy;
                frames.Add(input);
                if (!s.Grounded) leftStart = true;
                if (s.Grounded && leftStart) break; // landed below after clearing the platform
            }
            if (frames.Count == 0) return null;
            var node = new SSNode { Px = s.Px, Py = s.Py, Vx = s.Vx, Vy = s.Vy, Grounded = s.Grounded };
            if (!node.Grounded) return null;
            if (MathF.Abs(node.Py - cur.Py) < 1f) return null; // didn't drop
            // 不再复查 IsFloorPublic:物理 Grounded 就是权威落点。StandCell 把亚像素的 py 向上取整时这个检查会误杀真实下落
            // (和自由落体那个 bug 同源,commit dc2a9e6) → drop 边消失 → 只能手动挖掉平台才能继续。
            return (node, frames);
        }

        // 评分不用落点那一格的 H,用"从落点能望到的最低 H":跨多格的坎能骗过 greedy —— (4854,379) 往下掉涨 30、
        // 回头只涨 2 于是选回头,可回头是来路;真相是再走一步就降 78。半径 18 是实测:出口在 14 格外,留余量。
        const int LookaheadRadius = 18;
        const int LookaheadBudget = 400;   // 望多少格封顶,保证不随地形爆炸

        static int LookaheadH(System.Collections.Generic.Dictionary<(int, int), int> field,
            int sx, int sy, int landH,
            System.Collections.Generic.Dictionary<(int, int), int> cache)
        {
            if (cache.TryGetValue((sx, sy), out int hit)) return hit;
            int best = landH;
            var seen = new System.Collections.Generic.HashSet<(int, int)> { (sx, sy) };
            var q = new System.Collections.Generic.Queue<(int x, int y)>();
            q.Enqueue((sx, sy));
            int visited = 0;
            while (q.Count > 0 && visited < LookaheadBudget)
            {
                var (cx0, cy0) = q.Dequeue();
                visited++;
                for (int i = 0; i < 4; i++)
                {
                    int nx = cx0 + (i == 0 ? 1 : i == 1 ? -1 : 0);
                    int ny = cy0 + (i == 2 ? 1 : i == 3 ? -1 : 0);
                    if (System.Math.Abs(nx - sx) + System.Math.Abs(ny - sy) > LookaheadRadius) continue;
                    if (!seen.Add((nx, ny))) continue;
                    // 只在身体过得去的格子上漫延,不然会穿墙望见墙那边的低 H(那边根本走不过去)
                    if (!Predicates.IsPassable(nx, ny)) continue;
                    if (!field.TryGetValue((nx, ny), out int nh)) continue;
                    if (nh < best) best = nh;
                    q.Enqueue((nx, ny));
                }
            }
            cache[(sx, sy)] = best;
            return best;
        }

        const int DropSplitMin = 6;      // 落差小于这个不值得拆
        static IEnumerable<(SSNode node, List<PhysicsSimulator.ControlInput> frames, float cost)> SplitFall(
            SSNode cur, List<PhysicsSimulator.ControlInput> frames, int platformTile)
        {
            if (platformTile < 0 || frames == null || frames.Count == 0) yield break;
            int startCy = (int)((cur.Py + PhysicsSimulator.PlayerH) / 16f);
            var last = frames[frames.Count - 1];
            int endCy = (int)((last.Py + PhysicsSimulator.PlayerH) / 16f);
            if (endCy - startCy < DropSplitMin) yield break;
            var emitted = new HashSet<int>();
            for (int i = 0; i < frames.Count; i++)
            {
                var fr = frames[i];
                if (fr.Vy <= 0f) continue;                    // 只在下落段接
                int feetCy = (int)((fr.Py + PhysicsSimulator.PlayerH) / 16f);
                int fell = feetCy - startCy;
                if (fell < 3) continue;
                if (feetCy >= endCy - 1) break;               // 已经快到底了,不如让它自己落
                if (!emitted.Add(fell)) continue;
                int cx = (int)((fr.Px + PhysicsSimulator.PlayerW / 2f) / 16f);
                int cy = feetCy + 2;                          // 脚下第二格
                if (cx < 1 || cy < 1 || cx >= Main.maxTilesX - 1 || cy >= Main.maxTilesY - 1) break;
                if (!Predicates.Vacant(cx, cy)) continue;
                if (!MazeWand.PlatformAnchor(cx, cy)) continue;
                var take = frames.GetRange(0, i + 1);
                if (!MarkPlaceFrame(take, cx, cy)) continue;
                var node = new SSNode
                {
                    Px = cx * 16f + 8f - PhysicsSimulator.PlayerW / 2f,
                    Py = cy * 16f - PhysicsSimulator.PlayerH,
                    Vx = 0f, Vy = 0f, Grounded = true,
                };
                yield return (node, take, take.Count);
            }
        }

        static bool SegDiag;   // temp: when set, SimulateSegment logs why each walk/jump returned null (diagnose EXPAND-EMPTY)
        static (SSNode node, List<PhysicsSimulator.ControlInput> frames)? SimulateSegment(
            SSNode cur, int dir, int hold, PhysicsSimulator.Params ph)
        {
            var s = new PhysicsSimulator.State
            {
                Px = cur.Px, Py = cur.Py, Vx = cur.Vx, Vy = cur.Vy,
                Grounded = true, JumpFramesLeft = hold,
            };
            var frames = new List<PhysicsSimulator.ControlInput>();
            bool everAirborne = false;
            float startPx = s.Px;
            for (int f = 0; f < MaxSegFrames; f++)
            {
                var input = new PhysicsSimulator.ControlInput
                {
                    Right = dir > 0, Left = dir < 0, Jump = f < hold,
                };
                float prevPx = s.Px;
                s = PhysicsSimulator.Step(s, input, ph);
                input.Px = s.Px; input.Py = s.Py; input.Vx = s.Vx; input.Vy = s.Vy;
                frames.Add(input);
                if (!s.Grounded) everAirborne = true;
                if (s.Grounded && everAirborne)
                {
                    // 泰拉的跳跃碰地后不能立刻起跳,至少在地上待一帧,带着残余 vx 滑行。
                    // 规划器原先在触地帧就结束边,漏掉这一帧滑行 → 每条边的落点都比执行短 ~vx*1帧(~3px) → 接缝漂移累积。
                    var settle = PhysicsSimulator.Step(s, input, ph);
                    var sf = input; sf.Jump = false; sf.Px = settle.Px; sf.Py = settle.Py; sf.Vx = settle.Vx; sf.Vy = settle.Vy;
                    frames.Add(sf);
                    s = settle;
                    break;
                }
                if (s.Grounded && hold == 0)
                {
                    // 脚悬在空中时别结束走路边(比如刚走下一格宽的平台):模拟器还会读到一帧 Grounded,
                    // 在这儿结束会产出被拒的假站位,把"走下崖"这条边杀掉。继续模拟,让人真的落到下面的地板。
                    var (wcx, wcy) = StandCell(s.Px, s.Py);
                    bool footSupported = PathPlanner.IsFloorPublic(wcx, wcy + 1);
                    // 走满一个完整步幅再结束,不是 24px。24px 时边死在加速斜坡里(0.08/帧,要 37 帧才到 maxRun),
                    // 于是每条走路边平均 ~1px/帧,看起来比跳慢得多,A* 在平地上就一路跳。步幅取一次跳的射程,两者才可比。
                    if (footSupported && MathF.Abs(s.Px - startPx) >= WalkStridePx) break;
                    if (footSupported && MathF.Abs(s.Px - prevPx) < 0.05f && f >= 2) break; // wall: not advancing
                }
            }
            if (frames.Count == 0) { if (SegDiag) DiagLog.Trc($"[ss-seg] dir={dir} hold={hold} NULL: no frames"); return null; }
            var node = new SSNode { Px = s.Px, Py = s.Py, Vx = s.Vx, Vy = s.Vy, Grounded = s.Grounded };
            if (MathF.Abs(node.Px - cur.Px) < 1f && MathF.Abs(node.Py - cur.Py) < 1f) { if (SegDiag) DiagLog.Trc($"[ss-seg] dir={dir} hold={hold} NULL: no move (dpx={node.Px - cur.Px:0.#} dpy={node.Py - cur.Py:0.#}) gnd={node.Grounded}"); return null; } // no self-loops
            // 脆:水里重力太弱,人浮在空格上方模拟器仍读 Grounded=true。落地点的两个脚列下面【没有真地板】就是假站位 —— 拒掉,
            // 逼 A* 去放平台,而不是"走"过开阔水面然后循环。只管落地边,腾空的下落/跳跃边不受影响。
            if (node.Grounded)
            {
                var (ncx, ncy) = StandCell(node.Px, node.Py);
                // 斜坡/半砖撑得住人但 IsFloorPublic 不认 → 被误判成假站位,把每一条从半砖起跳/起步的边都杀了(EXPAND-EMPTY 死穴)。
                // DigSolid 认斜坡半砖为支撑,所以两者取其一即可。
                if (!PathPlanner.IsFloorPublic(ncx, ncy + 1) && !DigSolid(ncx, ncy + 1))
                {
                    if (SegDiag)
                    {
                        var bt = Main.tile[ncx, ncy + 1];
                        DiagLog.Trc($"[ss-seg] dir={dir} hold={hold} NULL: fake-stand at ({ncx},{ncy}); below ({ncx},{ncy + 1}) type={bt.TileType} hasTile={bt.HasTile} slope={(int)bt.Slope} half={bt.IsHalfBlock} solid={Main.tileSolid[bt.TileType]} solidTop={Main.tileSolidTop[bt.TileType]}");
                    }
                    return null;
                } // reported stand cell has no floor = fake
            }
            if (SegDiag) DiagLog.Trc($"[ss-seg] dir={dir} hold={hold} OK -> ({StandCell(node.Px,node.Py).Item1},{StandCell(node.Px,node.Py).Item2}) gnd={node.Grounded}");
            return (node, frames);
        }

        // 走下崖边顺着重力落到真地板 —— 不挖,任意深度,全程按住 gdir(人下落时就是一直按着方向)。
        // 人根本没离地(这儿没崖,普通走路已覆盖)或引信内没落地,返回 null。
        const int FallMinDropPx = 32;   // must drop >=2 tiles, else it's a step plain walk/jump handles
        static (SSNode node, List<PhysicsSimulator.ControlInput> frames)? FreeFall(SSNode cur, int gdir, PhysicsSimulator.Params ph)
        {
            var s = new PhysicsSimulator.State { Px = cur.Px, Py = cur.Py, Vx = cur.Vx, Vy = cur.Vy, Grounded = true };
            var frames = new List<PhysicsSimulator.ControlInput>();
            bool everAirborne = false;
            float startPy = s.Py;
            for (int f = 0; f < MaxSegFrames; f++)
            {
                // hold the direction only until the feet leave the ledge; once airborne, release it so the player
                // drops vertically (a human doesn't keep steering mid-fall — holding it sails far past the target).
                bool airborne = !s.Grounded;
                var input = new PhysicsSimulator.ControlInput { Right = !airborne && gdir > 0, Left = !airborne && gdir < 0 };
                s = PhysicsSimulator.Step(s, input, ph);
                input.Px = s.Px; input.Py = s.Py; input.Vx = s.Vx; input.Vy = s.Vy;
                frames.Add(input);
                if (!s.Grounded) everAirborne = true;
                else if (everAirborne) break;   // landed
            }
            if (!everAirborne || !s.Grounded) return null;          // no cliff, or never landed
            if (s.Py - startPy < FallMinDropPx) return null;        // shallow step, not a fall
            // 掉进岩浆这把就完了,得重开 —— 所以这条边【不发】。物理 Step 把岩浆当空气,
            // 落点和整条下落轨迹都要查:穿过岩浆再落到对岸石头上,人已经死了。
            if (FallHitsLava(frames)) return null;
            // 不复查 IsFloorPublic:真实下落后物理 Step 返回的 Grounded 就是权威落点。
            // 假站位守卫会在 StandCell 把亚像素 py 向上取整时误杀真实的竖直下落 ((2944,364) 的 bug)。
            var node = new SSNode { Px = s.Px, Py = s.Py, Vx = s.Vx, Vy = s.Vy, Grounded = true };
            return (node, frames);
        }

        // 站不上去的目标格 = 不可达,搜索会烧光整个预算。navwand 点击有两种:目标浮在空中,或点进了实心块。
        // 身子扫过的每一格都查岩浆。只查落点不够:高速下落一帧走十几像素,能直接跨过整层岩浆。
        static bool FallHitsLava(List<PhysicsSimulator.ControlInput> frames)
        {
            foreach (var f in frames)
            {
                int x0 = (int)(f.Px / 16f), x1 = (int)((f.Px + PhysicsSimulator.PlayerW - 1) / 16f);
                int y0 = (int)(f.Py / 16f), y1 = (int)((f.Py + PhysicsSimulator.PlayerH - 1) / 16f);
                for (int x = x0; x <= x1; x++)
                    for (int y = y0; y <= y1; y++)
                        if (Predicates.IsLava(x, y)) return true;
            }
            return false;
        }

        // 在同一列里按距离【双向】找最近可站格:向上是从块里爬到表面,向下是把悬空目标落到地板。
        const int GoalSnapMaxDrop = 40;
        // 岩浆里放不了任何东西,人去了也白去 —— 所以"能站"必须排除泡在岩浆里和踩在岩浆面上。
        // 原来只问地板和方块,于是空中目标一路下落、落进岩浆池就当成落脚点,等于主动往岩浆里导航。
        static bool Standable(int gx, int gy)
            => PathPlanner.IsFloorPublic(gx, gy + 1) && !PathPlanner.IsBlockPublic(gx, gy)
               && !Predicates.IsLava(gx, gy) && !Predicates.IsLava(gx, gy - 1) && !Predicates.IsLava(gx, gy + 1);
        public static int SnapGoalToStandable(int gx, int gy)
        {
            if (Standable(gx, gy)) return gy;
            // 点进方块 → 向上爬到表面(有界,表面就在几格内)。点在空中 → 一路往下落,多深都行 —— 
            // "点空中"的意思就是"去它下面的地",给下落设上限会把目标留在深坑上方飘着,A* 烧光预算也够不到。
            if (PathPlanner.IsBlockPublic(gx, gy))
            {
                for (int d = 1; d <= GoalSnapMaxDrop; d++)
                    if (Standable(gx, gy - d)) return gy - d;
                return gy;
            }
            for (int y = gy + 1; y < Main.maxTilesY - 1; y++)
                if (Standable(gx, y)) return y;
            return gy;
        }

        // 临时失败诊断:start↔goal 区域的 ASCII 图叠上已探索前沿,用来看"本该存在的路为什么没找到"。
        // '@'=起点 'G'=目标 '#'=实心 '='=平台 '/'=斜坡半砖 '*'=已探索空气 '.'=空气
        static void DumpTerrain(SSNode start, int goalWx, int goalWy, List<(float px, float py)> explored)
        {
            var (sx, sy) = StandCell(start.Px, start.Py);
            int minX = Math.Min(sx, goalWx) - 6, maxX = Math.Max(sx, goalWx) + 6;
            int minY = Math.Min(sy, goalWy) - 4, maxY = Math.Max(sy, goalWy) + 4;
            if (maxX - minX > 80) maxX = minX + 80;
            if (maxY - minY > 40) maxY = minY + 40;

            var exp = new HashSet<(int, int)>();
            foreach (var (px, py) in explored)
                exp.Add(StandCell(px, py));

            DiagLog.Write($"[ss-map] FAIL start=({sx},{sy}) goal=({goalWx},{goalWy}) region x[{minX},{maxX}] y[{minY},{maxY}]");
            for (int y = minY; y <= maxY; y++)
            {
                var sb = new System.Text.StringBuilder();
                for (int x = minX; x <= maxX; x++)
                {
                    char c;
                    if (x == sx && y == sy) c = '@';
                    else if (x == goalWx && y == goalWy) c = 'G';
                    else if (PathPlanner.IsBlockPublic(x, y)) c = '#';
                    else if (x < 0 || y < 0 || x >= Main.maxTilesX || y >= Main.maxTilesY) c = ' ';
                    else
                    {
                        var t = Main.tile[x, y];
                        if (t.HasTile && Terraria.ID.TileID.Sets.Platforms[t.TileType]) c = '=';
                        else if (t.HasTile && ((int)t.Slope != 0 || t.IsHalfBlock)) c = '/';
                        else if (exp.Contains((x, y))) c = '*';
                        else c = '.';
                    }
                    sb.Append(c);
                }
                DiagLog.Write($"[ss-map] {y,5} {sb}");
            }
        }

        static bool ReachedGoal(SSNode s, float goalCx, float goalFeetY)
        {
            float cx = s.Px + PhysicsSimulator.PlayerW / 2f;
            float feetY = s.Py + PhysicsSimulator.PlayerH;
            return s.Grounded && MathF.Abs(cx - goalCx) <= 12f && MathF.Abs(feetY - goalFeetY) <= 12f;
        }

        const float TrendClimbDyWeight = 4f;
        const float TrendNearDyTiles = 2f;
        const float DistStepCost = 8f; // h per coarse-BFS step (≈ frames to traverse one cell)

        // 迷宫场是无记忆的 2D 代价格,只看总代价、看不见【顺序】,于是"先上后右"和"先右后上" H 相同,
        // 而物理上天差地别(水平速度喂给跳跃)。粗化成 N×N 块后块内 H 是平的,A* 不再追逐逐格竖直梯度爬直柱。
        const int HBlockSize = 8;

        static int BlockMinH(PlanCtx ctx, int cx, int cy)
        {
            int bx = (cx < 0 ? cx - HBlockSize + 1 : cx) / HBlockSize;
            int by = (cy < 0 ? cy - HBlockSize + 1 : cy) / HBlockSize;
            if (ctx.BlockH != null && ctx.BlockH.TryGetValue((bx, by), out int cached)) return cached;
            int best = int.MaxValue;
            int x0 = bx * HBlockSize, y0 = by * HBlockSize;
            for (int x = x0; x < x0 + HBlockSize; x++)
                for (int y = y0; y < y0 + HBlockSize; y++)
                    if (ctx.DistField.TryGetValue((x, y), out int v) && v < best) best = v;
            ctx.BlockH ??= new Dictionary<(int, int), int>();
            ctx.BlockH[(bx, by)] = best;
            return best;
        }

        // is there a standable cell along gdir (within reach) with lower maze H than here — i.e. a walk-out route.
        static bool HasLateralProgress(PlanCtx ctx, int ccx, int ccy, int gdir, int curH, int maxScan)
        {
            for (int d = 1; d <= maxScan; d++)
            {
                int x = ccx + gdir * d;
                if (PathPlanner.IsBlockPublic(x, ccy)) break; // wall blocks the walk-out
                if (!CoarseStand(x, ccy)) continue;
                if (ctx.DistField.TryGetValue((x, ccy), out int hx) && hx < curH) return true;
            }
            return false;
        }

        // 用【原始逐格】场判进展,不用块粗化的:粗化后块内是平的,块内移动会被误报成没进展,于是不该挖的地方也挖。
        static bool RawProgress(PlanCtx ctx, SSNode from, SSNode to)
        {
            if (ctx.DistField == null) return false;
            var (fcx, fcy) = StandCell(from.Px, from.Py);
            var (tcx, tcy) = StandCell(to.Px, to.Py);
            if (!ctx.DistField.TryGetValue((fcx, fcy), out int fh)) return false;
            if (!ctx.DistField.TryGetValue((tcx, tcy), out int th)) return false;
            return th < fh;
        }

        static float Heuristic(PlanCtx ctx, SSNode s, float goalCx, float goalFeetY, PhysicsSimulator.Params ph)
        {
            if (ctx.DistField != null)
            {
                var (cx, cy) = StandCell(s.Px, s.Py);
                int h = HBlockSize <= 1
                    ? (ctx.DistField.TryGetValue((cx, cy), out int d0) ? d0 : int.MaxValue)
                    : BlockMinH(ctx, cx, cy);
                if (h != int.MaxValue)
                    return h * DistStepCost;
            }
            float ccx = s.Px + PhysicsSimulator.PlayerW / 2f;
            float feetY = s.Py + PhysicsSimulator.PlayerH;
            float dx = MathF.Abs(ccx - goalCx);
            float dy = MathF.Abs(feetY - goalFeetY);
            float dyW = (F_Trend && dy > TrendNearDyTiles * 16f) ? TrendClimbDyWeight : (1f / 5f);
            return dx / MathF.Max(ph.MaxRun, 0.1f) + dy * dyW;
        }

        static bool CoarseStand(int cx, int cy)
        {
            if (cx < 0 || cy < 0 || cx >= Main.maxTilesX || cy + 1 >= Main.maxTilesY) return false;
            if (PathPlanner.IsBlockPublic(cx, cy)) return false;
            return PathPlanner.IsFloorPublic(cx, cy + 1);
        }


        public static void Visualize(SSResult res, int goalWx, int goalWy)
        {
            const float CX = PhysicsSimulator.PlayerW / 2f, FY = PhysicsSimulator.PlayerH;
            var trail = new List<(float, float, bool)>();
            foreach (var seg in res.Segments)
                foreach (var (px, py) in seg.Trail)
                    trail.Add((px + CX, py + FY, seg.IsJump));
            var explored = new List<(float, float)>();
            foreach (var (px, py) in res.Explored) explored.Add((px + CX, py + FY));
            var placed = new List<(int, int)>();
            foreach (var fr in res.ExecFrames) if (fr.Place) placed.Add((fr.PlaceCx, fr.PlaceCy));
            var mineTiles = new List<(int, int)>();
            if (res.Steps != null)
                foreach (var st in res.Steps) if (st.Dig && st.MineTiles != null) mineTiles.AddRange(st.MineTiles);
            float goalPx = res.GoalWx * 16f + 8f;
            float goalPy = (res.GoalWy + 1) * 16f;
            PathVisSystem.SetSSPath(trail, explored, goalPx, goalPy, placed, mineTiles, ttlFrames: 1200);
        }

        // ── execution ──
        static List<PhysicsSimulator.ControlInput> _execFrames;
        static int _execIdx;
        static int _execGoalWx, _execGoalWy;
        // 真正的终点,执行开始时设一次,绝不被单步目标覆盖。重规划必须瞄这里 —— 瞄单步目标(旧的 _execGoal)会把人送到路径中间某格,
        // 这正是当初错误地禁用重规划、进而开环漂移掉进坑里的原因。
        static int _finalGoalWx, _finalGoalWy;

        // 滚动导航:一次 Plan 只够一个预算的距离,部分段就从新位置朝【最终目标】再规划,一段接一段。
        // 连续几段都不朝目标推进(局部极小 —— 封死的坑之类)就放弃。
        static bool _rolling;
        static int _rollFinalWx, _rollFinalWy;
        static float _rollPrevDist;          // goal distance at the end of the previous leg (to detect "not advancing")
        static int _rollStuckLegs;
        const int RollMaxStuckLegs = 3;      // consecutive legs without progress → genuinely stuck → give up
        const float RollProgressPx = 16f;    // a leg must close at least this much distance to count as progress

        // lookahead:当前段在走的时候,线程池里按本段的预测落点规划下一段,到点时不用付同步 Plan 的主线程卡顿。
        // 到达时缓存计划的起点和真实落点吻合就零停顿派发,否则现算。
        static volatile System.Threading.Tasks.Task _rollBgTask;
        static volatile SSResult _rollBgResult;
        static int _rollBgFromCx, _rollBgFromCy;   // predicted landing the bg leg planned from (for arrival validation)
        const int RollLandMatchTol = 2;

        const float ReplanDriftPx = 24f;
        const int ReplanCooldown = 10;
        static int _replanCooldownLeft;
        // 空中自救:执行偏离弧线【且】人在下落(脱轨俯冲 —— 放置失败/踩空),不等落地。
        // 像人踩空时往脚下拍一块平台一样,放一块止住下落,再从那儿重规划。
        const float RescueFallVy = 1.0f;   // vy above which we count as genuinely descending (not apex jitter)
        const float PlungeBelowPx = 24f;   // real player this far BELOW the planned frame (+still falling) = off-arc plunge
        const int RescueCooldown = 20;     // frames between rescue attempts so we don't spam-place every tick
        static int _rescueCooldownLeft;
        // 卡住 = 速度偏差:计划说该在动(|pf.Vx| 够大)而真身几乎不动(|vx| 很小)且没推进 —— "想动没动成"(撞墙/卡坡)。
        // 这是偏差的速度轴,按位置距离判的检查看不见它,因为人几乎没位移。
        const float VelDevExpect = 1.5f;   // plan expected at least this |Vx|
        const float VelDevReal = 0.4f;     // but real |Vx| is below this = blocked
        const int StuckFrames = 18;        // consecutive blocked frames before declaring stuck
        static int _stuckFrames;

        // 本体感觉:不去和(可能过期的)计划帧比位置,而是从上一帧真实状态+上一帧输入预测【一帧裸玩家】该到哪,再和真实结果比。
        // 这个失配与计划对错无关,直接量的是"我的身体没有按物理响应我的指令",一个信号覆盖穿透/卡住/击退/入水。
        struct RealState { public float Px, Py, Vx, Vy; public bool Grounded, Valid; }
        static RealState _lastReal;
        const float ProprioMismatchPx = 6f;   // per-frame predicted-vs-actual gap that flags a control anomaly
        const float TeleportPx = 160f;         // one-frame jump beyond any possible physics = teleport/yank → abort nav
        static int _replanCount;
        static bool _silentPath;   // suppress the full [ss-path] dump during replan (storms flood the log); the [ss-replan] summary line carries the delta instead
        const int MaxReplans = 40;
        static int _placeStall;
        // 绕路允许 H 升这么多才算"没前进"。实测 35 次 PUSH 分两坨:+3~15 是绕路(11 次),
        // +60 是 (2667,208) 同一格反复弹(18 次),中间是空的。取 20 把两者分开。
        const int PushSlack = 20;
        static int _stallIdx = -1, _stallFrames;
        const int StallReport = 20;   // 1/3 秒推不动一帧 = 有问题,比 sentinel 的 30 tick 早发现
        const int PlaceStallMax = 60;

        public static bool IsActive => (_execFrames != null && _execIdx < _execFrames.Count) || _walkActive;

        // 闭环走路:不开环重放计划帧(起点偏了会平移整条边),而是朝目标 X 按键、到位即止,自我纠正。
        // 到位【不】刹车:vx 要带进下一条边(跳跃需要助跑速度,别归零)。
        static bool _walkActive;
        static int _walkTargetCx, _walkDir;

        public static void StopExec() { _execFrames = null; _execIdx = 0; _walkActive = false; }

        // full stop of the step/rolling executor (J pause, or any external cancel): kill the current leg's frames,
        // the step list, and the rolling loop so it doesn't auto-plan another leg.
        public static void StopNav() { _rolling = false; _rollBgResult = null; _replanPending = false; _replanSeq++; StopSteps(); StopExec(); DiagLog.EndRun(); }

        // 执行状态机(和 NavCoordinator.Done/IsActive/FailCode 对齐),这样 HTTP /nav 能用同一套方式驱动新规划器。
        static bool _execDone;
        static string _execFailCode;     // null while running/ok; set on any failure exit
        public static bool ExecDone => _execDone;
        public static string ExecFailCode => _execFailCode;
        static SSResult _lastExecResult;
        public static SSResult LastExecResult => _lastExecResult;   // the plan the current/last leg dispatched (for lookahead landing prediction)
        // running iff a route is dispatched and not yet ended (steps drive edges; _execFrames is one edge's replay).
        public static bool ExecRunning => StepsActive || IsActive || _greedyActive || _replanPending || _asyncPending;

        // 逐边执行:帧步重放自己模拟出的帧(规划即执行),pillar 步交给 SkillExecutor 的宏。
        // 每一步都要等上一个执行器空闲、人落地站稳(干净静止态)才开始。
        static List<ExecStep> _ssSteps;
        static int _ssStepIdx;
        static bool _ssDispatched;
        static uint _stepStartTick;        // watchdog soft clock: start of the current NO-MOTION window (slides while moving)
        static uint _stepDispatchTick;     // watchdog hard clock: when the step was dispatched (never slides)
        static long _stepTimeoutTicks;     // soft deadline (est × 1.75 + 60, capped 1min) — frozen position
        static long _stepHardTicks;        // hard deadline (est × 4 + 300, capped 2min) — even while moving
        static float _stepEstFrames;       // the step's own time estimate (for the announcement)
        static float _stepLastPx, _stepLastPy;   // last observed position (motion slides the soft window)
        static ExecStep _ssPrevStep;       // the edge being executed, for plan-vs-exec frame-count diagnosis
        static int _lastExecFrameCount;    // how many frames ApplyControls replayed for the current edge
        public static bool StepsActive => _ssSteps != null && _ssStepIdx < _ssSteps.Count;

        public static void StopSteps() { _ssSteps = null; _ssStepIdx = 0; _ssDispatched = false; }

        static void StartSteps(List<ExecStep> steps)
        {
            StopExec();
            _ssSteps = steps; _ssStepIdx = 0; _ssDispatched = false;
            DiagLog.Write($"[ss-steps] start n={steps.Count}");
        }

        static void TickSteps()
        {
            if (!StepsActive) return;
            var p = Main.LocalPlayer;
            if (p == null || !p.active) { StopSteps(); return; }
            bool busy = IsActive || SkillExecutor.IsActive || MineCoordinator.IsActive;

            // 看门狗:每个动作都有死线。所有计划层的免疫机制(miss/revisit/shock/循环检测)都活在重规划周期里,而重规划要等 ExecRunning 清零
            // —— 一个永不终止的执行器(77s 的 PillarWait)就能饿死整套免疫系统。软钟(动就续)防误杀,硬钟(绝对)防步内自嗨死循环。
            if (_ssDispatched)
            {
                float moved = System.MathF.Abs(p.position.X - _stepLastPx) + System.MathF.Abs(p.position.Y - _stepLastPy);
                if (moved > 2f) _stepStartTick = Main.GameUpdateCount;
                _stepLastPx = p.position.X; _stepLastPy = p.position.Y;
            }
            bool softOut = _ssDispatched && Main.GameUpdateCount - _stepStartTick > _stepTimeoutTicks;
            bool hardOut = _ssDispatched && Main.GameUpdateCount - _stepDispatchTick > _stepHardTicks;
            if (softOut || hardOut)
            {
                var tst = _ssPrevStep;
                string tkind = tst != null ? EdgeKind(tst) : "?";
                string clock = hardOut ? "hard" : "soft";
                long ran = Main.GameUpdateCount - _stepDispatchTick;
                DiagLog.Write($"[timeout] {clock} step {tkind} →({(tst?.TargetCx ?? -1)},{(tst?.TargetCy ?? -1)}) est={_stepEstFrames:0}f soft={_stepTimeoutTicks}f hard={_stepHardTicks}f ran={ran}f — abort, back to closed loop");
                Main.NewText($"[TerraBlind] TIMEOUT({clock}) {tkind}→({tst?.TargetCx},{tst?.TargetCy}) est {_stepEstFrames:0}f ran {ran}f — replanning");
                SkillExecutor.Stop(); MineCoordinator.Stop(); StopExec(); StopSteps();
                DiagLog.EndRun();
                return;
            }

            if (_ssDispatched)
            {
                if (busy) return;
                if (p.velocity.Y != 0f) return;     // wait until landed + settled before advancing
                // 诊断:计划帧数 vs 执行实际重放了多少帧。差 ~1 说明落地/推进的时机差一帧(= 那 ~3px = vx*1帧 的接缝漂移)。
                if (_ssPrevStep != null && !_ssPrevStep.Pillar && !_ssPrevStep.Dig)
                {
                    var lf = _ssPrevStep.Frames[_ssPrevStep.Frames.Count - 1];
                    DiagLog.Trc($"[ss-framecmp] kind={EdgeKind(_ssPrevStep)} planFrames={_ssPrevStep.Frames.Count} execFrames={_lastExecFrameCount} planLand=({lf.Px:0.##},{lf.Py:0.##}) execLand=({p.position.X:0.##},{p.position.Y:0.##}) d(px={(p.position.X - lf.Px):0.##} py={(p.position.Y - lf.Py):0.##}) planVx={lf.Vx:0.###} execVx={p.velocity.X:0.###} dVx={(p.velocity.X - lf.Vx):0.###}");
                }
                _ssStepIdx++;
                _ssDispatched = false;
                if (!StepsActive)
                {
                    DiagLog.Write("[ss-steps] done");
                    StopSteps();
                    if (_rolling && RollNextLeg(p)) return;   // partial leg finished → plan & dispatch the next one
                    _execDone = true; DiagLog.EndRun(); return;
                }
            }
            if (busy || p.velocity.Y != 0f) return; // start each step from rest on the ground

            var st = _ssSteps[_ssStepIdx];
            int ccx = (int)(p.Center.X / 16f);
            DiagLog.Write($"[ss-steps] #{_ssStepIdx}/{_ssSteps.Count} {EdgeKind(st)} ->({st.TargetCx},{st.TargetCy})");
            _ssDispatched = true;
            _stepEstFrames = EstStepFrames(st, p);
            _stepTimeoutTicks = (long)System.Math.Min(_stepEstFrames * 1.75f + 60f, 3600f);
            _stepHardTicks = (long)System.Math.Min(_stepEstFrames * 4f + 300f, 7200f);
            _stepStartTick = Main.GameUpdateCount;
            _stepDispatchTick = Main.GameUpdateCount;
            _stepLastPx = p.position.X; _stepLastPy = p.position.Y;
            _ssPrevStep = st; _lastExecFrameCount = 0;
            _execGoalWx = st.TargetCx; _execGoalWy = st.TargetCy;

            // 列必须传下去:边的落点写死在 TargetCx,执行器不传就每跳按脚下现找,两边能差一列。
            if (st.Pillar)
                SkillExecutor.StartPillarJump(st.TargetCx >= ccx, st.TargetCy, true, st.TargetCx);
            else if (st.Dig)
            {
                int sfeet = (int)((p.position.Y + p.height) / 16f) - 1;
                MineCoordinator.Start(new MineRequest { Dir = st.DigDir, StartWx = ccx, StartWy = sfeet, TargetWx = st.TargetCx, TargetWy = st.TargetCy, MineTiles = st.MineTiles });
            }
            // 同列的边也排除:WalkTick 只比 x,目标列就是脚下这列时 dx=0,第一帧自称到达,那几行垂直位移一步没做。
            // 同列纵向边 271 条错 114 条(42%),是其他边的 2.3 倍。排除 Down 同理 —— 闭环不会按下键。
            else if (st.Frames != null && st.Frames.Count > 0 && st.TargetCx != StandCell(p.position.X, p.position.Y).cx
                     && !st.Frames.Exists(fr => fr.Place || fr.Jump || fr.Down))
            {
                _walkActive = true; _walkTargetCx = st.TargetCx; _walkDir = st.TargetCx >= ccx ? 1 : -1;
            }
            else if (st.Frames != null && st.Frames.Count > 0)
            {
                // 诊断:玩家真实起点和这条边规划时的起点对不对得上?有缝 = 从错误原点开环重放 → 累积 → 块边缘俯冲。
                // 相位坑:Frames[0] 是执行完第 0 帧【之后】的状态,而真人还【没】执行第 0 帧,直接比会看到 dPy=4.61 的幻影差。
                var f0 = st.Frames[0];
                var ph0 = PhysicsSimulator.Params.FromPlayer(p);
                var rstart = new PhysicsSimulator.State { Px = p.position.X, Py = p.position.Y, Vx = p.velocity.X, Vy = p.velocity.Y, Grounded = p.velocity.Y == 0f, JumpFramesLeft = f0.Jump ? Player.jumpHeight : 0 };
                var rAfter = PhysicsSimulator.Step(rstart, f0, ph0);
                DiagLog.Write($"[ss-startgap] step#{_ssStepIdx} kind={EdgeKind(st)} planStart=({f0.Px:0.##},{f0.Py:0.##}) realStart=({rAfter.Px:0.##},{rAfter.Py:0.##}) dPx={(rAfter.Px - f0.Px):0.##} dPy={(rAfter.Py - f0.Py):0.##}");
                _execFrames = st.Frames; _execIdx = 0;
            }
            else
                DiagLog.Write("[ss-steps] step has no frames — skip");
        }

        // 贪心单步驱动:场给全局趋势,每步只前向模拟几个候选动作,按落点格的场代价打分,执行最好的那一个。
        // 没有搜索树 → 不会爆炸。都不改善时(竖井:跳放被挡)退回一个 pillar 周期 —— "低收益就爬"是涌现的,不是写死的。
        static bool _greedyActive;
        static PlanCtx _greedyCtx;   // greedy runs across frames on the game thread: ctx built in ExecBlocks, read each TickBlocks
        static int _greedyGoalWx, _greedyGoalWy;
        static readonly List<(float, float, bool)> _greedyTrail = new();
        static bool _prevReplayJump;
        static readonly HashSet<(int, int)> _greedyVisited = new();

        public static bool GreedyActive => _greedyActive;

        public static void StopGreedy()
        {
            _greedyActive = false;
            StopExec();
            if (SkillExecutor.IsActive) SkillExecutor.Stop();
        }

        public static void ExecBlocks(int goalWx, int goalWy)
        {
            StopGreedy();
            var p = Main.LocalPlayer;
            if (p == null || !p.active) return;
            goalWy = SnapGoalToStandable(goalWx, goalWy);
            var (spx, spy) = StandCell(p.position.X, p.position.Y);
            _greedyCtx = new PlanCtx();
            _greedyCtx.DistField = MazeWand.BuildField(goalWx, goalWy, spx, spy);
            _greedyCtx.BlockH = null;
            if (!_greedyCtx.DistField.ContainsKey((spx, spy))) { DiagLog.Write($"[ss-greedy] start ({spx},{spy}) not in field"); return; }
            _greedyActive = true; _greedyGoalWx = goalWx; _greedyGoalWy = goalWy;
            _greedyTrail.Clear(); _greedyVisited.Clear();
            DiagLog.Write($"[ss-greedy] start=({spx},{spy}) goal=({goalWx},{goalWy}) field={_greedyCtx.DistField.Count}");
        }

        const float GreedyGoalDistPx = 12f;

        // HTTP sets this; consumed on the player thread (RunPendingTest) to avoid cross-thread tile/player reads.
        static volatile string _pendingTestName;
        static volatile int _pendingTestDir;
        public static void RequestTestAction(string name, int dir) { _pendingTestDir = dir; _pendingTestName = name; }
        static void RunPendingTest()
        {
            var n = _pendingTestName;
            if (n == null) return;
            _pendingTestName = null;
            TestAction(n, _pendingTestDir);
        }

        // Isolated single-action test: from the player's current state, run ONE action generator (no greedy, no
        // field). Replays the produced frames + visualizes. For debugging a single move type via HTTP /test_action.
        public static void TestAction(string name, int dir)
        {
            StopGreedy();
            var p = Main.LocalPlayer;
            if (p == null || !p.active) { DiagLog.Write("[ss-test] no player"); return; }
            var ph = PhysicsSimulator.Params.FromPlayer(p);
            int platformTile = -1;
            int platformSlot = NavCoordinator.FindPlatformSlot(p);
            if (platformSlot >= 0) platformTile = p.inventory[platformSlot].createTile;
            var cur = new SSNode { Px = p.position.X, Py = p.position.Y, Vx = p.velocity.X, Vy = 0f, Grounded = true };
            var (scx, scy) = StandCell(cur.Px, cur.Py);

            (SSNode node, List<PhysicsSimulator.ControlInput> frames)? r = name switch
            {
                "bridge" => BridgePlace(cur, dir, ph, platformTile),
                "jumpplace" => JumpPlace(new PlanCtx(), cur, dir, BuildHoldOptions()[^1], ph, platformTile),
                "jump" => SimulateSegment(cur, dir, BuildHoldOptions()[^1], ph),
                "walk" => SimulateSegment(cur, dir, 0, ph),
                _ => null,
            };

            if (r == null) { DiagLog.Write($"[ss-test] {name} dir={dir} from=({scx},{scy}) → NULL (action produced nothing)"); return; }
            var (node, frames) = r.Value;
            var (ncx, ncy) = StandCell(node.Px, node.Py);
            DiagLog.Write($"[ss-test] {name} dir={dir} from=({scx},{scy}) -> ({ncx},{ncy}) frames={frames.Count} place={(frames.Exists(fr => fr.Place))}");

            var trail = new List<(float, float, bool)>();
            foreach (var fr in frames) trail.Add((fr.Px + PhysicsSimulator.PlayerW / 2f, fr.Py + PhysicsSimulator.PlayerH, fr.Jump));
            PathVisSystem.SetSSPath(trail, new List<(float, float)>(), node.Px + PhysicsSimulator.PlayerW / 2f, node.Py + PhysicsSimulator.PlayerH);

            _execFrames = frames; _execIdx = 0;
            _execGoalWx = ncx; _execGoalWy = ncy;
            _replanCooldownLeft = 0; _replanCount = 0; _placeStall = 0;
        }

        // RECEDING / follow-the-line. Each call picks ONE Expand edge that advances furthest along the DescendPath line
        // (line = global route, gives direction; Expand landings = physics-valid cells, give a body-doable step).

        static string _lastParamsSig;   // last logged [ss-params] signature (log on change only)
        public static void ResetLineProgress() { _miss.Clear(); _recent.Clear(); }

        // 注意力失配记忆 —— 【连续】的逐边权重,不是硬禁。真实落点比模拟落点差多少就记多少罚分(曼哈顿格,和 g/H 同单位)。
        // 永不为 ∞、永不删除候选(卡死在结构上仍不可能),且每周期衰减 —— 这是它不退化成禁退的关键:被罚的边总会恢复。
        static readonly System.Collections.Generic.Dictionary<(int, int, int, int), float> _miss = new();
        const float MissDecayTick = 0.93f;   // per-cycle decay → half-life ~10 cycles (a typical pit fall+climb loop)
        const float MissForgiveHit = 0.3f;   // an edge that DID reach its target this time is largely forgiven
        const int NoMoveMissFloor = 10;      // a zero-move failure charges at least this (≫ tie-break scale, ≪ shock)
        public static void DecayMiss()
        {
            if (_miss.Count == 0) return;
            var keys = new System.Collections.Generic.List<(int, int, int, int)>(_miss.Keys);
            foreach (var k in keys) { float v = _miss[k] * MissDecayTick; if (v < 0.5f) _miss.Remove(k); else _miss[k] = v; }
        }
        // report the last edge's outcome: did the real landing match the cell the edge planned for?
        // 踩的是不是同一块砖 —— 不受取整影响的那个客观事实。中间隔着砖就是两个落脚处,不是读数差异。
        public static bool SameFooting(int cx, int ay, int by)
        {
            int hi = System.Math.Max(ay, by);
            return Predicates.IsGround(cx, hi + 1) && !Predicates.IsGround(cx, hi);
        }

        // 到没到:格号差一行、同一列、踩同一块砖 = 到了。日志和罚分必须用同一个判据,否则日志说 MISS 罚分说 HIT。
        // pillar 只承诺【往上爬】,不承诺爬到哪一行(一跳 1~3 格由地形定):同一列、升上去了就算到。
        public static bool Arrived(int planCx, int planCy, int realCx, int realCy, bool pillar = false)
            => (planCx == realCx && planCy == realCy)
            || (planCx == realCx && System.Math.Abs(planCy - realCy) == 1 && SameFooting(realCx, realCy, planCy))
            || (pillar && planCx == realCx && realCy <= planCy + 1);

        public static void ReportEdge(int fromCx, int fromCy, int planCx, int planCy, int realCx, int realCy, bool pillar = false)
        {
            var key = (fromCx, fromCy, planCx, planCy);
            int miss = System.Math.Abs(realCx - planCx) + System.Math.Abs(realCy - planCy);
            // 差一行但踩着同一块砖 = 到了,只是两把尺子读数不同(StandCell 取整 vs 物理落点)。
            // 按格号判 miss 会把这种情况罚成失败,边被压价、下轮不选、换条边又是同样的偏差 —— (1998,196)→(1999,198) 13 次。
            if (Arrived(planCx, planCy, realCx, realCy, pillar)) miss = 0;
            // 零位移地板:落回起点格是对边的承诺最彻底的违背 —— 模拟说"这步能推进",现实说"你压根没动"。
            // 曼哈顿只给它 1-2 分,比"超了两格"还轻,于是有点微弱优势的斜坡边被反复重试。给它一个地板价。
            if (miss > 0 && realCx == fromCx && realCy == fromCy) miss = System.Math.Max(miss, NoMoveMissFloor);
            if (miss == 0) { if (_miss.ContainsKey(key)) _miss[key] *= MissForgiveHit; }
            else _miss[key] = _miss.GetValueOrDefault(key) + miss;

            // 重访罚分:同一套连续机制,用来抓【每步都命中(miss=0)却哪也没去】的等高线蹭 —— 落点是刚站过的格就罚那条边。
            // 不靠卡住计数器靠记忆。像 _miss 一样衰减,所以以后正当地重走同一条路不会被禁。
            var landed = (realCx, realCy);
            int recency = _recent.IndexOf(landed);
            if (recency >= 0)
            {
                // 递增:每圈固定 +12 要 ~3 轮 shock(+10s)才压过一条只赢几分的蹭边(树台阶陷阱:蹭 t404 vs 跳放 t411)。
                // 每次至少加上它【当前】的罚分 = 每重复一次翻倍(12→24→48…),第二圈就完成了原来要靠 shock 磨出来的修正。
                var ekey = (fromCx, fromCy, realCx, realCy);
                float cur = _miss.GetValueOrDefault(ekey);
                float inc = System.MathF.Max(RevisitPenalty * (_recent.Count - recency), cur);
                _miss[ekey] = System.MathF.Min(cur + inc, RevisitCap);
            }
            _recent.Add(landed);
            if (_recent.Count > RecentLen) _recent.RemoveAt(0);
        }
        static readonly System.Collections.Generic.List<(int, int)> _recent = new();
        const int RecentLen = 6;              // how many past landings to remember for revisit detection
        const float RevisitPenalty = 12f;     // base penalty for an edge landing on a recently-visited cell; repeats double it (see ReportEdge)
        const float RevisitCap = 200f;        // escalation ceiling = one shock's worth — revisit alone can do what a shock did, but no more

        // 最近站过的格子。只用来在 PUSH 时把"去过的"排到后面,不做循环判定、不禁止重访。
        const int VisitedLen = 40;
        static readonly System.Collections.Generic.HashSet<(int, int)> _visited = new();
        static readonly System.Collections.Generic.Queue<(int, int)> _visitedQ = new();
        public static void ResetFloor() { _visited.Clear(); _visitedQ.Clear(); _recent.Clear(); }
        public static void RequestJiggle() { }

        // 区域内累积的边罚分 —— 让循环无法复现的隐藏状态,也是"一条好边反而全场最贵"的原因。
        static List<Cand> _lastCands;
        static (int cx, int cy, int h) _lastAt;
        static (int gx, int gy) _lastGoal;
        public static string CaptureStuck(string why, string trail) =>
            StuckSnapshot.Capture(_lastAt.cx, _lastAt.cy, _lastAt.h, _lastGoal.gx, _lastGoal.gy, _lastCands, why, trail);

        public static string PenaltyJson(int x0, int y0, int x1, int y1)
        {
            var sb = new System.Text.StringBuilder();
            bool first = true;
            foreach (var kv in _miss)
            {
                var (fx, fy, tx, ty) = kv.Key;
                if (fx < x0 || fx > x1 || fy < y0 || fy > y1) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"from\":[").Append(fx).Append(',').Append(fy).Append("],\"to\":[")
                  .Append(tx).Append(',').Append(ty).Append("],\"p\":").Append(kv.Value.ToString("0.#")).Append('}');
            }
            return sb.ToString();
        }

        internal static void PenalizeEdges(System.Collections.Generic.IEnumerable<(int fx, int fy, int tx, int ty)> edges, float amount)
        {
            foreach (var e in edges)
            {
                var key = (e.fx, e.fy, e.tx, e.ty);
                _miss[key] = _miss.GetValueOrDefault(key) + amount;
                DiagLog.Trc($"[recede-shock] edge ({e.fx},{e.fy})→({e.tx},{e.ty}) +{amount}");
            }
        }

        static bool IsLavaCell(int x, int y)
        {
            if (x < 0 || y < 0 || x >= Main.maxTilesX || y >= Main.maxTilesY) return false;
            var t = Main.tile[x, y];
            return t.LiquidAmount > 0 && t.LiquidType == Terraria.ID.LiquidID.Lava;
        }

        public static SSResult StepAlongField(int goalWx, int goalWy)
        {
            var p = Main.LocalPlayer;
            if (p == null || !p.active) return null;
            var field = MazeWand.GetField(goalWx, goalWy);
            var ctx = new PlanCtx { DistField = field };
            var ph = PhysicsSimulator.Params.FromPlayer(p);
            // 向日葵漂移取证:Happy! buff 的加速本该已经在实时读的 maxRun/accRun 里。若落点在向日葵附近漂,
            // 说明这些值没跟上 buff 或者 buff 在边执行中途翻转。只在变化时打。
            {
                string sig = $"maxRun={ph.MaxRun:0.###} accRunSpd={ph.AccRunSpeed:0.###} accRun={ph.AccRun:0.####} sunflower={Main.SceneMetrics.HasSunflower}";
                if (sig != _lastParamsSig) { _lastParamsSig = sig; DiagLog.Trc($"[ss-params] {sig}"); }
            }
            int platformTile = -1;
            int slot = NavCoordinator.FindPlatformSlot(p);
            if (slot >= 0) platformTile = p.inventory[slot].createTile;
            bool hasPick = false;
            for (int i = 0; i < 10; i++) { var it = p.inventory[i]; if (it != null && !it.IsAir && it.pick > 0) { hasPick = true; break; } }

            var cur = new SSNode { Px = p.position.X, Py = p.position.Y, Vx = p.velocity.X, Vy = 0f, Grounded = true };
            var (curCx, curCy) = StandCell(cur.Px, cur.Py);
            int curH = field.TryGetValue((curCx, curCy), out int ch) ? ch : int.MaxValue;
            float gx = goalWx * 16f + 8f, gy = (goalWy + 1) * 16f;
            // 四邻真相行:场想从这儿往哪降,那里物理上到底是什么。Dijkstra 保证必有一个邻居 H 更低;
            // 当没有候选够得到它,这一行就当场定罪那个静默拒绝的生成器,不用再考古一轮。
            {
                var nb = new System.Text.StringBuilder($"[recede-nbrs] at=({curCx},{curCy})H={curH}");
                foreach (var (tag, nx, ny) in new[] { ("E", curCx + 1, curCy), ("W", curCx - 1, curCy), ("U", curCx, curCy - 1), ("D", curCx, curCy + 1) })
                {
                    string hs = field.TryGetValue((nx, ny), out int nh) ? nh.ToString() : "—";
                    string ts = DigSolid(nx, ny) ? $"sol{DigTable.CostFrames(nx, ny)}f"
                        : PathPlanner.PlatformPublic(nx, ny) ? "plat" : "air";
                    nb.Append($" {tag}:H{hs}/{ts}");
                }
                DiagLog.Write(nb.ToString());
            }

            // 线每次从【当前格】重新描。老做法冻结 start→goal 再用 _lineIdx 单调投影,那是能扛过现实变化的记忆:
            // 摔一跤/被击退/传送之后,投影还咬定人在它上次看见的地方,dev 项就把人往过期路线上拽。
            var line = MazeWand.TraceFrom(field, curCx, curCy, goalWx, goalWy);
            var dS = LineDir(line, 0, ArcShort);
            var dM = LineDir(line, 0, ArcMid);
            var dL = LineDir(line, 0, ArcLong);
            // 脚下的望值。g 必须和落点价值用同一把尺子量,否则恒等式 total≡当前值 不成立 —— 见下面 laH。

            (SSNode node, List<PhysicsSimulator.ControlInput> frames, float cost, bool pillar, List<(int,int)> dig)? best = null;
            float bestTotal = float.MaxValue; (int, int) bestCell = (curCx, curCy);
            var cands = new List<Cand>();
            // 所有候选,连 total 一起留着 —— 选边要按 total 采样,不是取最小(见下面的 softmax)
            var jigglePool = new List<((SSNode node, List<PhysicsSimulator.ControlInput> frames, float cost, bool pillar, List<(int, int)> dig) edge, (int, int) cell, int h, float total)>();
            var _candLog = new System.Text.StringBuilder();
            var _swCycle = System.Diagnostics.Stopwatch.StartNew();
            foreach (var (next, frames, cost, pillar, digTiles) in Expand(ctx, cur, ph, gx, gy, BuildHoldOptions(), platformTile, hasPick))
            {
                // 落点按【停稳后】的状态标记,不按最后一帧计划:跳跃可能停在格内 0.7px 处、残余 vx 又把人滑回边界另一侧,
                // 计划"到达"了一个静止态永远不占的格 ((800,937) 幽灵)。但改造地形的边必须【不】做自由落体沉降 —— 它们的砖还没挖/放。
                bool alters = digTiles != null || pillar || (frames != null && frames.Exists(f => f.Place));
                var landed = alters ? next : SettleNode(next, ph);
                var (ncx, ncy) = alters ? RawCell(landed.Px, landed.Py) : StandCell(landed.Px, landed.Py);
                if (ncx == curCx && ncy == curCy) continue;   // self-loop (no real move)
                if (IsLavaCell(ncx, ncy)) continue;           // never step into lava (deadly, not drift)
                if (!field.TryGetValue((ncx, ncy), out int nH)) continue;   // off the field → can't value it
                // g+h 两段同尺(帧):g=走这条边的帧数,h=落点场值。弃用 LookaheadH —— 它取周围 18 格最小 H,
                // 望得见走不到:(2581,318)H1097 旁边 H1141 被望成 t862,来回八趟 200 帧。
                bool isPlace = !pillar && digTiles == null && frames != null && frames.Exists(f => f.Place);
                float g = cost * DigFramesToH;
                // Bellman 基础分 g+V(落点),再加这条边的注意力失配权重:反复落空的乐观边被软性降权,可靠的替代就赢了。
                // 纯 g+H 本来就允许 H 暂时升高(走进浅坑再爬出),罚分只针对物理反复不兑现的边。
                float pen = _miss.GetValueOrDefault((curCx, curCy, ncx, ncy));
                // 大方向对齐:这一步的位移和多尺度线向量有多同向,从 total 里减掉。这是用来区分【等 H 格】的:
                // 1680↔1682 那种蹭是垂直于走廊的(align≈0 无奖励),真正朝走廊方向走的才拿奖励。在线的拐弯处衰减到 0。
                float ddx = ncx - curCx, ddy = ncy - curCy;
                float dlen = MathF.Sqrt(ddx * ddx + ddy * ddy);
                float align = 0f;
                if (dlen >= 0.5f)
                {
                    float ux = ddx / dlen, uy = ddy / dlen;
                    align = WShort * (ux * dS.x + uy * dS.y) + WMid * (ux * dM.x + uy * dM.y) + WLong * (ux * dL.x + uy * dL.y);
                }
                // 偏离罚分:落点离线(场认定的最优路线)多远。按每格距离收费,所以走进线在上面飘过去的坑越深越贵,
                // 贴着线走几乎不收费。单向下坠进爬不回来的陷阱会无界累积 → 坑边那条边转而选桥。
                var (_, devDist) = NearestLineIdx(line, ncx, ncy, 0);
                // 距离的超线性:贴着线蹭几乎不花钱,但陡增得快,离线很多格(跳进线在上方飘过的坑壁)会被重重压价。
                // 同一项也把已经掉进去的人拉出来:越深距离越大罚得越狠,朝线爬(缩小距离)就赢过继续下钻。dist^1.5,比线性陡但不像平方那样爆。
                float dev = DeviCost * devDist * MathF.Sqrt(devDist);
                float total = g + nH + pen - AlignScale * align + dev;
                string kind = pillar ? "pillar" : digTiles != null ? "dig"
                    : isPlace ? "place"
                    : (frames != null && frames.Exists(f => f.Jump)) ? "jump" : "walk";
                cands.Add(new Cand { Cx = ncx, Cy = ncy, H = nH, Cost = (int)g, Kind = kind, Descends = nH < curH });
                _candLog.Append($" {kind}→({ncx},{ncy})H{nH}g{g:0.#}t{total:0.#}{(nH < curH ? "↓" : "")}");
                if (total < bestTotal)
                { bestTotal = total; best = (next, frames, cost, pillar, digTiles); bestCell = (ncx, ncy); }
                jigglePool.Add(((next, frames, cost, pillar, digTiles), (ncx, ncy), nH, total));
            }
            // 原判据是"落点 H 有没有低于历史最低",错的:人为绕路离开最低点后,之后每一步都 ≥ 历史最低,PUSH 就永久触发。
            // 改成跟【脚下】比:greedy 挑的落点不比现在近才算没进展。绕路(H 暂时升高)允许,只有真原地弹才触发。
            if (jigglePool.Count > 0 && best != null)
            {
                int bestH = -1;
                foreach (var c in jigglePool) if (c.cell == bestCell) { bestH = c.h; break; }
                // 升幅不是判据,A→B→A 才是:(1998,196)↔(1999,197) 弹 13 次,H 差只有 18,slack 放过去了。
                // greedy 在降 H 就别拦 —— 窄地形里 _recent 覆盖了所有邻居,每步都判回头,PUSH 一路选更差的 (H 132→146→155→161)。
                bool bounce = _recent.Contains(bestCell) && bestH >= curH;
                if (bestH > curH + PushSlack || bounce)
                {
                    // 管子里 H 最低的候选常常就是来路 —— (4854,379) 的候选去重后只有 10 格,7 格在刚走过的 4×4 里,H 最低的正是回头那格。
                    // 人一眼看出"左右是墙只能往下"靠的不是比 H,是【哪边没去过】。所以先在没去过的里挑,用完了才退回全体(排后面,不是禁止)。
                    bool anyFresh = false;
                    foreach (var c in jigglePool) if (!_visited.Contains(c.cell)) { anyFresh = true; break; }
                    // 按 total 排,不按落点 H:H 里没有"这一步多贵",挖 11 格到 H441 就这么赢了走一步到 H453。
                    // total=g+laH 含真实挖掘费,便宜的下降边自然靠前;真只剩挖掘时它还是唯一候选,选得中,卡不死。
                    var push = jigglePool[0];
                    bool have = false;
                    foreach (var c in jigglePool)
                    {
                        if (anyFresh && _visited.Contains(c.cell)) continue;
                        if (bounce && c.cell == bestCell) continue;   // 别把刚判定为回头的那格又选回来
                        if (!have || c.total < push.total) { push = c; have = true; }
                    }
                    // 一个替代都挑不出来(候选只剩回头那格):保持 greedy 的选择,别退回 jigglePool[0] ——
                    // 那是任意一条边,可能比 greedy 差得多。走回去总比乱走强,下一轮 _recent 变了会重选。
                    if (!have) push = (best.Value, bestCell, bestH, bestTotal);
                    if (push.cell != bestCell)
                        EventLog.W(Ev.Plan, $"PUSH ({curCx},{curCy})H{curH} greedy→({bestCell.Item1},{bestCell.Item2})H{bestH}t{bestTotal:0} 没前进,改走 ({push.cell.Item1},{push.cell.Item2})H{push.h}t{push.total:0} {(bounce ? "回头" : "H涨")} fresh={anyFresh}");
                    best = push.edge; bestCell = push.cell; bestTotal = push.total;
                }
            }
            if (_visited.Add((curCx, curCy))) _visitedQ.Enqueue((curCx, curCy));
            while (_visitedQ.Count > VisitedLen) _visited.Remove(_visitedQ.Dequeue());
            _lastCands = cands; _lastAt = (curCx, curCy, curH); _lastGoal = (goalWx, goalWy);
            RecedingVis.SetDecision(curCx, curCy, curH, goalWx, goalWy, cands, best != null ? bestCell : ((int, int)?)null, best != null ? curH - bestTotal : 0f, dS, dM, dL);
            DiagLog.Trc($"[recede-cands] from=({curCx},{curCy})H={curH} n={cands.Count} expandMs={_swCycle.Elapsed.TotalMilliseconds:0.0}:{_candLog}");

            // 饥饿 Expand:没有任何下降候选时(正是静默拒绝的生成器要命的那些周期),开着 SegDiag 重跑一次 Expand,
            // 让每个 null 都说出理由。一次运行定罪,不用再考古 ((2959,262) 那次 n=1 只剩 place 的循环,走西边物理上存在却从没生成过)。
            if (best != null && (cands.Count <= 2 || !cands.Exists(c => c.Descends)))
            {
                SegDiag = true;
                var _swRe = System.Diagnostics.Stopwatch.StartNew();
                foreach (var _ in Expand(ctx, cur, ph, gx, gy, BuildHoldOptions(), platformTile, hasPick)) { }
                DiagLog.Trc($"[expand-cost] bare re-run at ({curCx},{curCy}) ms={_swRe.Elapsed.TotalMilliseconds:0.0}");
                SegDiag = false;
            }

            if (best == null)   // Expand yielded no edge on the field at all
            {
                EventLog.W(Ev.Fail, $"EXPAND-EMPTY ({curCx},{curCy})H{curH} 一条边都没生成");
                SegDiag = true;
                foreach (var _ in Expand(ctx, cur, ph, gx, gy, BuildHoldOptions(), platformTile, hasPick)) { }
                SegDiag = false;
                // 安全逃逸步(硬规则:卡死必须在结构上不可能 —— 选不出来就挪一格重选,绝不停在能站的姿势上)。
                // 接受【任何】真实位移,不管场和 H(这不是进展,这是脱困):能走进来的身体就能走出去,下一周期由场的诚实定价接管。
                foreach (int edir in new[] { -1, 1 })
                    foreach (int ehold in new[] { 0, 8 })
                    {
                        var esc = SimulateSegment(cur, edir, ehold, ph);
                        if (!esc.HasValue) continue;
                        var (ecx, ecy) = StandCell(esc.Value.node.Px, esc.Value.node.Py);
                        if (IsLavaCell(ecx, ecy)) continue;
                        DiagLog.Write($"[recede] ESCAPE-STEP dir={edir} hold={ehold} -> ({ecx},{ecy})");
                        var eres = new SSResult { Found = true, GoalWx = ecx, GoalWy = ecy, StartPx = cur.Px, StartPy = cur.Py, CostFrames = esc.Value.frames.Count, CurH = curH };
                        eres.Steps = EdgeToSteps(cur, esc.Value.node, esc.Value.frames, false, null);
                        foreach (var st in eres.Steps) if (st.Frames != null) eres.ExecFrames.AddRange(st.Frames);
                        return eres;
                    }
                return null;   // cannot move a single pixel either way → true seal (a human couldn't either)
            }
            var pickCell = bestCell;
            var b = best.Value;
            var res = new SSResult { Found = true, GoalWx = pickCell.Item1, GoalWy = pickCell.Item2, StartPx = cur.Px, StartPy = cur.Py, CostFrames = b.cost, CurH = curH };
            res.Altered = (b.dig?.Count ?? 0) + (b.pillar ? 1 : 0) + (b.frames != null && b.frames.Exists(f => f.Place) ? 1 : 0);
            res.Steps = EdgeToSteps(cur, b.node, b.frames, b.pillar, b.dig);
            foreach (var st in res.Steps) if (st.Frames != null) res.ExecFrames.AddRange(st.Frames);
            int landH = field.TryGetValue(pickCell, out int lh) ? lh : -1;
            EventLog.W(Ev.Plan, $"({curCx},{curCy})H{curH} → ({pickCell.Item1},{pickCell.Item2})H{landH} {(b.pillar ? "pillar" : b.dig != null && b.dig.Count > 0 ? $"dig×{b.dig.Count}" : "move")}{(b.dig == null || b.dig.Count == 0 ? "" : " " + string.Join(",", b.dig.ConvertAll(d => $"({d.Item1},{d.Item2})")))}");
            // 选了要挖的边就把左右两边的账一起打出来 —— "旁边明明能走却偏要挖"每次都得回答这个
            if (b.dig != null && b.dig.Count > 0)
                DiagLog.Write($"[costcmp] 挖{b.dig.Count}格 vs 横向: 西({curCx - 1})cost={MazeWand.StepCostPublic(curCx - 1, curCy, curCx, curCy)}"
                    + $" 东({curCx + 1})cost={MazeWand.StepCostPublic(curCx + 1, curCy, curCx, curCy)}"
                    + $" 上cost={MazeWand.StepCostPublic(curCx, curCy - 1, curCx, curCy)}");
            return res;
        }

        // 离 (cx,cy) 最近的线格,在 near 附近的窗口里找。严格小于(取最先/最小 idx 的那个最小值):
        // 用 <= 会让并列时留下最大 idx,于是离整个窗口都远的落点吸附到窗口末端 → 每个落点都读成巨大进展 → 原地蹭。
        static (int idx, int dist) NearestLineIdx(List<(int, int)> line, int cx, int cy, int near)
        {
            if (line == null || line.Count == 0) return (0, int.MaxValue);
            int lo = System.Math.Max(0, near - 120), hi = System.Math.Min(line.Count - 1, near + 120);
            int bestI = lo, bestD = int.MaxValue;
            for (int i = lo; i <= hi; i++)
            {
                int d = System.Math.Abs(line[i].Item1 - cx) + System.Math.Abs(line[i].Item2 - cy);
                if (d < bestD) { bestD = d; bestI = i; }
            }
            return (bestI, bestD);
        }

        // 从 idx 出发沿线走,累计【曼哈顿弧长】到 arc 格为止的单位方向(不是走 idx 步:线是斜的,一个 idx ≈ 1-2 格)。
        // 这是标量场 H 表达不了的"大方向":H 说多远,向量说走廊实际朝哪拐 —— 用来区分等 H 格,也奖励暂时升 H 但方向对的一步。
        static (float x, float y) LineDir(List<(int, int)> line, int idx, int arc)
        {
            if (line == null || idx < 0 || idx >= line.Count) return (0f, 0f);
            int j = idx, acc = 0;
            while (j + 1 < line.Count && acc < arc)
            {
                acc += System.Math.Abs(line[j + 1].Item1 - line[j].Item1) + System.Math.Abs(line[j + 1].Item2 - line[j].Item2);
                j++;
            }
            float dx = line[j].Item1 - line[idx].Item1, dy = line[j].Item2 - line[idx].Item2;
            float len = MathF.Sqrt(dx * dx + dy * dy);
            return len < 0.5f ? (0f, 0f) : (dx / len, dy / len);
        }

        // 多尺度弧长 + 混合权重:中尺度是主力,短的修近处障碍,长的防中尺度绕远。三个点积互相校验,拐弯处彼此矛盾 → 对齐值缩小 → 退回纯 g+H。
        // AlignScale 故意取小:对齐是【近似平局】时的仲裁,不是能推翻明显 H 下降的力。取 120 时它能否决 H 低 42 的落点,把人钉在等 H 蹭里。
        const int ArcShort = 6, ArcMid = 20, ArcLong = 80;
        const float WShort = 0.3f, WMid = 1.0f, WLong = 0.4f, AlignScale = 18f;
        // 落点离线每格的单价。调到:偏几格几乎不花钱(允许短暂外出),但深偏(10+ 格进坑)明显被压价。
        const float DeviCost = 0.5f;   // coefficient of the super-linear (dist^1.5) line-deviation penalty — TIE-BREAKER size (must lose to a real H descent, else it vetoes a big-drop walk in favor of a one-cell dig)
        // 改造地形的逐格附加费。Bellman 只看落点的价值、看不见"你是怎么到的" —— "走过去掉下来"和"直接挖下去"到同一格完全平手。
        // 但改造地形真的贵得多(时间、毁掉的方块),V 编码不了,这笔费就是那个"怎么到的"。帧数→H:MoveSide=3 每 ~5.3 帧一格。
        const float DigFramesToH = 0.5f;

        // one Expand edge → its ExecStep(s). Mirrors the retrace conversion: pillar-composite (dig-up), pillar, dig,
        // or frame edge. dig-up composite splits into alternating mine/pillar sub-steps the executor can drive.
        static List<ExecStep> EdgeToSteps(SSNode from, SSNode to, List<PhysicsSimulator.ControlInput> frames, bool pillar, List<(int,int)> dig)
        {
            // 挖/放/柱的 to 节点描述的是【改造后】的世界:StandCell 的合身吸附会拿还没改的砖去判它,把它relabel 回当前格
            // —— 那次翻转了挖掘方向(往东挖变成"往左挖到自己",第二次 (981,435) 循环)。这些用 RawCell,from 用真实的。
            var (tcx, tcy) = (dig != null || pillar) ? RawCell(to.Px, to.Py) : StandCell(to.Px, to.Py);
            var (fcx, fcy) = StandCell(from.Px, from.Py);
            var steps = new List<ExecStep>();
            if (pillar && dig != null)
            {
                for (int feetY = fcy - 2; feetY >= tcy; feetY -= 2)
                {
                    steps.Add(new ExecStep { Dig = true, DigDir = MineDir.Up, TargetCx = tcx, TargetCy = feetY });
                    steps.Add(new ExecStep { Pillar = true, TargetCx = tcx, TargetCy = feetY });
                }
            }
            else if (pillar)
                steps.Add(new ExecStep { Pillar = true, TargetCx = tcx, TargetCy = tcy, Frames = null });
            else if (dig != null)
            {
                MineDir d = tcy > fcy ? MineDir.Down : tcx > fcx ? MineDir.Right : MineDir.Left;
                steps.Add(new ExecStep { Dig = true, DigDir = d, TargetCx = tcx, TargetCy = tcy, MineTiles = dig });
            }
            else if (frames != null)
                steps.Add(new ExecStep { Pillar = false, TargetCx = tcx, TargetCy = tcy, Frames = TrimFrozenTail(frames), LandPx = to.Px, LandPy = to.Py });
            return steps;
        }

        // 帧计划就是对下一段时间的位置预测,而计划可以预测出垃圾:BridgePlace 的"走到格心"在墙后根本到不了,
        // 于是循环朝墙按到 1200 帧引信,执行器忠实重放了 ~9s 的原地不动。预测位置不再变化的开环尾巴按定义是死重,砍掉只会更早交还闭环。
        const int FreezeTailMin = 20;   // only cut when the frozen run is clearly dead weight (>⅓s)
        const int FreezeTailKeep = 3;   // frames of the frozen run kept so the settle still registers
        static List<PhysicsSimulator.ControlInput> TrimFrozenTail(List<PhysicsSimulator.ControlInput> frames)
        {
            if (frames == null || frames.Count < FreezeTailMin) return frames;
            float ex = frames[frames.Count - 1].Px, ey = frames[frames.Count - 1].Py;
            int i = frames.Count - 1;
            while (i > 0 && MathF.Abs(frames[i - 1].Px - ex) < 0.01f && MathF.Abs(frames[i - 1].Py - ey) < 0.01f) i--;
            int keep = System.Math.Min(frames.Count, i + FreezeTailKeep);
            if (frames.Count - keep < FreezeTailMin) return frames;
            DiagLog.Write($"[ss-trim] frozen tail cut {frames.Count}→{keep} frames");
            return frames.GetRange(0, keep);
        }

        // chosen each time both executors are idle. Picks the candidate whose landing cell has the lowest maze cost.
        public static void TickBlocks()
        {
            RunPendingTest();
            PollReplan();
            TickSteps();
            if (!_greedyActive) return;
            if (IsActive || SkillExecutor.IsActive) return; // a step is running

            var p = Main.LocalPlayer;
            if (p == null || !p.active) { StopGreedy(); return; }
            // each step must start from rest on the ground: picking mid-air gives a wrong start state and the next
            // jump can't edge-trigger (controlJump never released). wait until landed and settled.
            if (p.velocity.Y != 0f) return;

            float gx = _greedyGoalWx * 16f + 8f, gy = (_greedyGoalWy + 1) * 16f;
            float ccx = p.position.X + p.width / 2f, cfy = p.position.Y + p.height;
            if (MathF.Abs(ccx - gx) <= GreedyGoalDistPx && MathF.Abs(cfy - gy) <= GreedyGoalDistPx)
            { DiagLog.Write("[ss-greedy] reached goal"); StopGreedy(); return; }

            var ph = PhysicsSimulator.Params.FromPlayer(p);
            int platformTile = -1;
            int platformSlot = NavCoordinator.FindPlatformSlot(p);
            if (platformSlot >= 0) platformTile = p.inventory[platformSlot].createTile;

            var cur = new SSNode { Px = p.position.X, Py = p.position.Y, Vx = p.velocity.X, Vy = 0f, Grounded = true };
            var (curCx, curCy) = StandCell(cur.Px, cur.Py);
            int curCost = _greedyCtx.DistField.TryGetValue((curCx, curCy), out int cc) ? cc : int.MaxValue;

            _greedyVisited.Add((curCx, curCy));

            _greedyCtx.JpNoSpot = _greedyCtx.JpNoLand = _greedyCtx.JpFellThrough = _greedyCtx.JpSlidOff = _greedyCtx.JpOk = 0;
            // 不回头:绝不踏上已访问格。在未访问的可达候选里挑场代价最低的 —— 这逼着人走出局部极小的井
            // (封死口袋的低代价地板已经访问过 → 只能横向伸进新格,哪怕代价暂时上升)。全都访问过了才真是无处可去。
            List<PhysicsSimulator.ControlInput> chosen = null;
            int chosenCost = int.MaxValue, chosenFC = int.MaxValue;
            var cand = new System.Text.StringBuilder();
            int candN = 0;
            foreach (var (next, frames, _, _, _) in Expand(_greedyCtx, cur, ph, gx, gy, BuildHoldOptions(), platformTile, false))
            {
                if (frames == null) continue; // greedy can't drive the pillar macro; skip those edges
                var (ncx, ncy) = StandCell(next.Px, next.Py);
                bool inField = _greedyCtx.DistField.TryGetValue((ncx, ncy), out int ncost);
                bool plc = frames.Count > 0 && frames[frames.Count - 1].Place;
                if (candN++ < 16) cand.Append($" [{ncx},{ncy}{(plc ? "P" : "")}c{(inField ? ncost.ToString() : "∞")}f{frames.Count}]");
                if (!inField || _greedyVisited.Contains((ncx, ncy))) continue;
                if (ncost < chosenCost || (ncost == chosenCost && frames.Count < chosenFC))
                { chosenCost = ncost; chosen = frames; chosenFC = frames.Count; }
            }

            if (chosen == null)
            {
                DiagLog.Write($"[ss-greedy] stuck at ({curCx},{curCy}) cost={curCost} (no unvisited candidate) cands(n={candN}):{cand}");
                StopGreedy(); return;
            }

            DiagLog.Write($"[ss-greedy] step ({curCx},{curCy})cost={curCost} -> cost={chosenCost} frames={chosen.Count}");
            foreach (var fr in chosen) _greedyTrail.Add((fr.Px + PhysicsSimulator.PlayerW / 2f, fr.Py + PhysicsSimulator.PlayerH, fr.Jump));
            PathVisSystem.SetSSPath(new List<(float, float, bool)>(_greedyTrail), new List<(float, float)>(), gx, gy);
            _execFrames = chosen; _execIdx = 0;
            _execGoalWx = _greedyGoalWx; _execGoalWy = _greedyGoalWy;
            _replanCooldownLeft = 0; _replanCount = 0; _placeStall = 0;
        }

        // 沿缓存场的梯度下山走 ~LegSubgoalCells 格取一个子目标。这就是让每一段"和手动单点导航一样快"的原因:
        // A* 追的是【附近可达】的格(几十次展开就找到),而不是朝着远目标穷举。
        static (int gx, int gy) SubgoalToward(int sx, int sy, int finalWx, int finalWy)
        {
            var field = MazeWand.GetField(finalWx, finalWy);
            var cur = (x: sx, y: sy);
            if (!field.ContainsKey(cur)) return (finalWx, finalWy); // off-field → just aim at final, A* will partial
            var seen = new HashSet<(int, int)>();
            for (int step = 0; step < LegSubgoalCells; step++)
            {
                if (cur == (finalWx, finalWy)) break;
                if (System.Math.Abs(cur.x - finalWx) + System.Math.Abs(cur.y - finalWy) <= 8) return (finalWx, finalWy);
                if (!seen.Add(cur)) break;
                int bestD = field[cur]; var best = cur;
                foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    var n = (cur.x + dx, cur.y + dy);
                    if (field.TryGetValue(n, out int dn) && dn < bestD) { bestD = dn; best = n; }
                }
                if (best == cur) break;
                cur = best;
            }
            return cur;
        }

        // 块导航:把缓存场的梯度路径【一次性】切成 ~BlockCells 的块,每块当成普通单点导航跑(target==goal,盒场,h 精确)。
        // 这是"不可能卡死"的设计:每一块都恰好是那个已经很快的 navwand 场景。
        const int BlockCells = 70;
        static readonly List<(int x, int y)> _blockQueue = new();
        static int _blockIdx;
        static bool _blockActive;
        static int _blockGoalWx, _blockGoalWy;

        public static bool BlockNavActive => _blockActive;

        public static void BlockNavStart(int goalWx, int goalWy)
        {
            StopNav(); StopGreedy();
            _blockQueue.Clear(); _blockIdx = 0; _blockActive = false;
            _blockGoalWx = goalWx; _blockGoalWy = goalWy;
            var p = Main.LocalPlayer;
            if (p == null || !p.active) return;
            var (sx, sy) = StandCell(p.position.X, p.position.Y);
            var field = MazeWand.GetField(goalWx, goalWy);
            if (!field.ContainsKey((sx, sy))) { DiagLog.Write($"[block] start ({sx},{sy}) off field → abort"); Main.NewText("[TerraBlind] start off nav field"); return; }

            // walk the gradient from start to goal, emitting a waypoint every BlockCells cells (and the final goal).
            var cur = (x: sx, y: sy);
            var seen = new HashSet<(int, int)>();
            int sinceCut = 0;
            for (int step = 0; step < 20000; step++)
            {
                if (cur == (goalWx, goalWy)) break;
                if (!seen.Add(cur)) break;
                int bestD = field[cur]; var best = cur;
                foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    var n = (cur.x + dx, cur.y + dy);
                    if (field.TryGetValue(n, out int dn) && dn < bestD) { bestD = dn; best = n; }
                }
                if (best == cur) break;
                cur = best;
                // cut ≥BlockCells apart, but only on a standable cell ON the path. Air stretches extend the cut to the
                // next landable cell — never snap off-path (that floated 60 tiles up and A* pillared to it = freeze).
                sinceCut++;
                if (sinceCut >= BlockCells && Standable(cur.x, cur.y)) { _blockQueue.Add((cur.x, cur.y)); sinceCut = 0; }
            }
            if (_blockQueue.Count == 0 || _blockQueue[_blockQueue.Count - 1] != (goalWx, goalWy))
                _blockQueue.Add((goalWx, goalWy));
            _blockActive = true; _blockIdx = 0;
            DiagLog.Write($"[block] start=({sx},{sy}) goal=({goalWx},{goalWy}) blocks={_blockQueue.Count}");
            DispatchBlock();
        }

        public static void BlockNavStop()
        {
            _blockActive = false; _blockQueue.Clear(); _blockIdx = 0;
            StopNav();
            DiagLog.Write("[block] stop");
        }

        static void DispatchBlock()
        {
            if (_blockIdx >= _blockQueue.Count) { _blockActive = false; DiagLog.Write("[block] all blocks done"); return; }
            var (bx, by) = _blockQueue[_blockIdx];
            DiagLog.Write($"[block] leg {_blockIdx + 1}/{_blockQueue.Count} → ({bx},{by})");
            ExecuteAsync(bx, by);   // off-thread Plan; each leg's A* no longer stutters the main thread
        }

        // per-frame driver: when the current block's single-point nav finishes, advance to the next block.
        public static void BlockNavTick()
        {
            if (!_blockActive) return;
            if (ExecRunning) return;        // current block still navigating
            if (ExecDone)
            {
                _blockIdx++;
                if (_blockIdx >= _blockQueue.Count) { _blockActive = false; DiagLog.Write("[block] reached goal"); Main.NewText("[TerraBlind] block nav done"); return; }
                DispatchBlock();
            }
            else
            {
                // block failed (unreachable/etc.) → stop the whole run
                DiagLog.Write($"[block] leg {_blockIdx + 1} failed ({ExecFailCode}) → stop");
                _blockActive = false;
            }
        }

        // rolling=false(navwand 单点):用快速盒场一次直达目标 —— 原本就快的近距离导航,不走大缓存场也不走分段循环。
        // rolling=true(长路线):大缓存罗盘 + 子目标分段 + lookahead。
        public static SSResult Execute(int goalWx, int goalWy, bool rolling = false)
        {
            StopGreedy(); StopSteps();
            _execDone = false; _execFailCode = null;
            _rolling = rolling; _rollFinalWx = goalWx; _rollFinalWy = goalWy;
            _rollPrevDist = float.MaxValue; _rollStuckLegs = 0;
            _rollBgResult = null;
            var pStart = Main.LocalPlayer;
            var (rsx, rsy) = StandCell(pStart.position.X, pStart.position.Y);
            DiagLog.StartRun($"{rsx}_{rsy}__{goalWx}_{goalWy}");
            DiagLog.Write($"[run] ss_exec start=({rsx},{rsy}) goal=({goalWx},{goalWy}) rolling={rolling}");

            SSResult res;
            int targetWx, targetWy;
            if (rolling)
            {
                var (sgx, sgy) = SubgoalToward(rsx, rsy, goalWx, goalWy);
                res = Plan(sgx, sgy, null, goalWx, goalWy);
                targetWx = sgx; targetWy = sgy;
            }
            else
            {
                res = Plan(goalWx, goalWy);   // one-shot, box field
                targetWx = goalWx; targetWy = goalWy;
            }
            ExecDispatch(res, targetWx, targetWy, goalWx, goalWy, rolling);
            return res;
        }

        // Main-thread dispatch tail shared by sync Execute and async ExecuteAsync. Visualize + StartSteps touch
        // Main rendering/input, so this must run on the main thread; only Plan (above / in the bg task) is heavy.
        static void ExecDispatch(SSResult res, int targetWx, int targetWy, int goalWx, int goalWy, bool rolling)
        {
            _lastExecResult = res;
            Visualize(res, targetWx, targetWy);
            DiagLog.Write($"[ss-plan] target=({targetWx},{targetWy}) final=({goalWx},{goalWy}) found={res.Found} partial={res.Partial} exp={res.Expansions} ms={res.Millis:0.#} steps={res.Steps.Count}");
            if (!res.Found && !res.Partial || res.Steps.Count == 0)
            {
                _rolling = false;
                _execFailCode = "unreachable";
                StopSteps();
                return;
            }
            _rollPrevDist = MathF.Abs(res.BestDx) + MathF.Abs(res.BestDy);
            _finalGoalWx = res.GoalWx; _finalGoalWy = res.GoalWy;  // true destination; replan aims here, never a step target
            _execGoalWx = res.GoalWx; _execGoalWy = res.GoalWy;
            _replanCooldownLeft = 0;
            _replanCount = 0;
            _placeStall = 0;
            _rescueCooldownLeft = 0;
            _stuckFrames = 0;
            _lastReal.Valid = false;
            StartSteps(res.Steps);   // edge-by-edge: frame replay + pillar macro
            if (rolling) LaunchRollLookahead(res);   // background-plan the next leg while this one walks
        }

        // Async Execute: run the heavy Plan (BuildField + A*) on a bg thread so a slow plan never stutters the game.
        // The result is stashed and ExecDispatch runs on the main thread via PollAsyncExec. Non-rolling (navwand) only.
        static volatile SSResult _asyncRes;
        static volatile bool _asyncPending;
        static int _asyncGoalWx, _asyncGoalWy, _asyncSeq;
        public static void ExecuteAsync(int goalWx, int goalWy)
        {
            StopGreedy(); StopSteps();
            _execDone = false; _execFailCode = null;
            _rolling = false; _rollFinalWx = goalWx; _rollFinalWy = goalWy;
            _asyncGoalWx = goalWx; _asyncGoalWy = goalWy;
            int seq = ++_asyncSeq;
            _asyncRes = null; _asyncPending = true; _asyncExecMode = true;
            var (rsx, rsy) = StandCell(Main.LocalPlayer.position.X, Main.LocalPlayer.position.Y);
            DiagLog.StartRun($"{rsx}_{rsy}__{goalWx}_{goalWy}");
            DiagLog.Write($"[run] ss_exec_async start=({rsx},{rsy}) goal=({goalWx},{goalWy})");
            System.Threading.Tasks.Task.Run(() =>
            {
                try { var r = Plan(goalWx, goalWy); if (seq == _asyncSeq) _asyncRes = r; }
                catch (System.Exception e) { DiagLog.Write($"[ss-async] EXC {e.Message}"); if (seq == _asyncSeq) { _asyncRes = null; _asyncPending = false; } }
            });
        }

        // preview-only async: same bg Plan, but main-thread tail just Visualizes (no StartSteps). _asyncExec
        // distinguishes the two so PollAsyncExec knows whether to run the leg or only draw it.
        static bool _asyncExecMode;
        public static void PlanAsync(int goalWx, int goalWy)
        {
            int seq = ++_asyncSeq;
            _asyncGoalWx = goalWx; _asyncGoalWy = goalWy;
            _asyncRes = null; _asyncPending = true; _asyncExecMode = false;
            var (rsx, rsy) = StandCell(Main.LocalPlayer.position.X, Main.LocalPlayer.position.Y);
            DiagLog.StartRun($"preview_{rsx}_{rsy}__{goalWx}_{goalWy}");   // own run file so "latest left-click log" is unique
            System.Threading.Tasks.Task.Run(() =>
            {
                try { var r = Plan(goalWx, goalWy); if (seq == _asyncSeq) _asyncRes = r; }
                catch (System.Exception e) { DiagLog.Write($"[ss-async] preview EXC {e.Message}"); if (seq == _asyncSeq) { _asyncRes = null; _asyncPending = false; } }
            });
        }

        // main thread: when the bg plan lands, dispatch it (Visualize + StartSteps), or just Visualize for preview.
        public static void PollAsyncExec()
        {
            if (!_asyncPending) return;
            var r = _asyncRes;
            if (r == null) return;
            _asyncPending = false; _asyncRes = null;
            if (_asyncExecMode)
                ExecDispatch(r, _asyncGoalWx, _asyncGoalWy, _asyncGoalWx, _asyncGoalWy, false);
            else
                Visualize(r, _asyncGoalWx, _asyncGoalWy);
        }

        // 滚动:一个部分段刚跑完。到最终目标就收工,否则从真实位置朝【真正的终点】规划下一段(复用缓存罗盘)。
        // 连续几段都没缩短距离(局部极小)就放弃。
        static bool RollNextLeg(Player p)
        {
            float gx = _rollFinalWx * 16f + 8f, gy = (_rollFinalWy + 1) * 16f;
            float ccx = p.position.X + p.width / 2f, cfy = p.position.Y + p.height;
            float dist = MathF.Abs(ccx - gx) + MathF.Abs(cfy - gy);
            if (dist <= GreedyGoalDistPx) { DiagLog.Write($"[ss-roll] reached final goal ({_rollFinalWx},{_rollFinalWy})"); _rolling = false; return false; }

            // GUARDRAIL: the cached field only covers a box around the goal. If the player has drifted/run outside it
            // (no field value at the current cell) we can't steer — stop instead of rebuilding a giant field or guessing.
            var fcell = StandCell(p.position.X, p.position.Y);
            if (!MazeWand.GetField(_rollFinalWx, _rollFinalWy).ContainsKey(fcell))
            {
                DiagLog.Write($"[ss-roll] off-field at {fcell} → stop (player left the cached field box)");
                Main.NewText("[TerraBlind] left nav field — stopped");
                _execFailCode = "off_field"; _rolling = false; return false;
            }

            // progress check: did the leg we just finished close the gap? if not for too many legs, we're stuck.
            if (_rollPrevDist - dist < RollProgressPx)
            {
                _rollStuckLegs++;
                if (_rollStuckLegs >= RollMaxStuckLegs)
                {
                    DiagLog.Write($"[ss-roll] stuck {_rollStuckLegs} legs at dist={dist:0.#} → give up");
                    _execFailCode = "roll_stuck"; _rolling = false; return false;
                }
            }
            else _rollStuckLegs = 0;
            _rollPrevDist = dist;

            // LOOKAHEAD HIT: the bg task already planned this leg from the predicted landing; if the real landing
            // matches, dispatch it with realign (zero synchronous Plan). Otherwise fall through and plan fresh.
            var cached = _rollBgResult; _rollBgResult = null;
            var (rcx, rcy) = StandCell(p.position.X, p.position.Y);
            if (cached != null && (cached.Found || cached.Partial) && cached.Steps.Count > 0
                && System.Math.Abs(rcx - _rollBgFromCx) <= RollLandMatchTol && System.Math.Abs(rcy - _rollBgFromCy) <= RollLandMatchTol)
            {
                DiagLog.Write($"[ss-roll] HIT cached leg → ({cached.GoalWx},{cached.GoalWy}) dist={dist:0.#}");
                DispatchPlan(cached);
                LaunchRollLookahead(cached);
                return true;
            }

            var (sgx, sgy) = SubgoalToward(rcx, rcy, _rollFinalWx, _rollFinalWy);
            var res = Plan(sgx, sgy, null, _rollFinalWx, _rollFinalWy);
            DiagLog.Write($"[ss-roll] next leg subgoal=({sgx},{sgy}) found={res.Found} partial={res.Partial} steps={res.Steps.Count} dist={dist:0.#} stuck={_rollStuckLegs}");
            if ((!res.Found && !res.Partial) || res.Steps.Count == 0) { _execFailCode = "roll_noplan"; _rolling = false; return false; }
            DispatchLeg(res);
            LaunchRollLookahead(res);
            return true;
        }

        // shared dispatch tail for a rolling leg planned synchronously (vs DispatchPlan which realigns a cached one).
        static void DispatchLeg(SSResult res)
        {
            _lastExecResult = res;
            Visualize(res, res.GoalWx, res.GoalWy);
            _finalGoalWx = res.GoalWx; _finalGoalWy = res.GoalWy;
            _execGoalWx = res.GoalWx; _execGoalWy = res.GoalWy;
            _replanCooldownLeft = 0; _replanCount = 0; _placeStall = 0; _rescueCooldownLeft = 0; _stuckFrames = 0;
            _lastReal.Valid = false;
            StartSteps(res.Steps);
        }

        // Kick off ONE bg task that plans the NEXT leg from THIS leg's predicted landing, toward the next subgoal.
        // Result cached for RollNextLeg to pick up on arrival. Exceptions swallowed = treated as "no cache".
        static void LaunchRollLookahead(SSResult curLeg)
        {
            _rollBgResult = null;
            var (px, py, vx) = RollPredictedLanding(curLeg);
            int fromCx = curLeg.GoalWx, fromCy = curLeg.GoalWy;
            int finalWx = _rollFinalWx, finalWy = _rollFinalWy;
            _rollBgFromCx = fromCx; _rollBgFromCy = fromCy;
            _rollBgTask = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var (sgx, sgy) = SubgoalToward(fromCx, fromCy, finalWx, finalWy);
                    var r = Plan(sgx, sgy, (px, py, vx), finalWx, finalWy);
                    _rollBgResult = r;
                }
                catch (System.Exception e) { DiagLog.Write($"[ss-roll] bg EXC {e.Message}"); _rollBgResult = null; }
            });
        }

        static (float px, float py, float vx) RollPredictedLanding(SSResult res)
        {
            for (int i = res.Steps.Count - 1; i >= 0; i--)
            {
                var f = res.Steps[i].Frames;
                if (f != null && f.Count > 0) { var last = f[f.Count - 1]; return (last.Px, last.Py, last.Vx); }
            }
            float px = res.GoalWx * 16f + 8f - PhysicsSimulator.PlayerW / 2f;
            float py = (res.GoalWy + 1) * 16f - PhysicsSimulator.PlayerH;
            return (px, py, 0f);
        }

        // 派发一个早先算好的计划(lookahead 在上一段执行时后台算的)。和 Execute 尾部相同但跳过规划 —— 调用方已经验证过真实位置匹配。
        public static void DispatchPlan(SSResult res)
        {
            StopGreedy(); StopSteps();
            _execDone = false; _execFailCode = null;
            var pStart = Main.LocalPlayer;

            // 重对齐:计划是从上一段的【预测】落点算的,真人差几像素。开环重放会把整段平移那个差值(恒定 dPy=-8 的接缝)。
            // 把每帧的绝对位置整体平移到人真正所在处。速度与位置无关(不动),Place 的格坐标是格对齐的(亚格平移不影响)。
            float offX = pStart.position.X - res.StartPx;
            float offY = pStart.position.Y - res.StartPy;
            if (res.Steps != null && (MathF.Abs(offX) > 0.01f || MathF.Abs(offY) > 0.01f))
            {
                foreach (var st in res.Steps)
                {
                    if (st.Frames == null) continue;
                    for (int i = 0; i < st.Frames.Count; i++)
                    {
                        var fr = st.Frames[i];
                        fr.Px += offX; fr.Py += offY;
                        st.Frames[i] = fr;
                    }
                }
                DiagLog.Write($"[ss-realign] lookahead leg shifted by ({offX:0.##},{offY:0.##}) to match real start");
            }
            _lastExecResult = res;
            var (rsx, rsy) = StandCell(pStart.position.X, pStart.position.Y);
            DiagLog.StartRun($"{rsx}_{rsy}__{res.GoalWx}_{res.GoalWy}");
            DiagLog.Write($"[run] ss_exec(lookahead) start=({rsx},{rsy}) goal=({res.GoalWx},{res.GoalWy}) steps={res.Steps.Count}");
            Visualize(res, res.GoalWx, res.GoalWy);
            _finalGoalWx = res.GoalWx; _finalGoalWy = res.GoalWy;
            _execGoalWx = res.GoalWx; _execGoalWy = res.GoalWy;
            _replanCooldownLeft = 0;
            _replanCount = 0;
            _placeStall = 0;
            _rescueCooldownLeft = 0;
            _stuckFrames = 0;
            _lastReal.Valid = false;
            StartSteps(res.Steps);
        }

        // 容差只包含标签/地形量化:StandCell 取整(±1)加上执行中途挖掘让落点最多陷进未挖岩石 2 行。
        // 超过 2 行就是【另一个地方】,不是本段的落点 → 判这段失败(便宜:重选一次)而不是去追一个被传送的目标(灾难:126 格俯冲)。
        const int ReplanGoalSnapCap = 2;

        static volatile SSResult _replanRes;
        static volatile bool _replanPending;
        static int _replanSeq;
        static string _replanReason;

        // 后台重规划:停执行(人刹住站一会儿),在别的线程算修正,好了再派发。偏离的那一刻旧计划就失效了,
        // 等几帧比同步 Plan 冻住整个游戏强。
        static bool Replan(string reason)
        {
            if (_replanPending) return true;   // a replan is already cooking; keep waiting
            if (_replanCount >= MaxReplans) { DiagLog.Write("[ss-replan] max replans hit → stop"); _execFailCode = "replan_storm"; return false; }
            _replanCount++;
            _replanReason = reason;
            var p = Main.LocalPlayer;
            float sx = p.position.X, sy = p.position.Y;   // snapshot; player brakes to rest while we plan
            bool rolling = _rolling;
            int finalWx = _finalGoalWx, finalWy = _finalGoalWy;
            int rollWx = _rollFinalWx, rollWy = _rollFinalWy;
            StopExec(); StopSteps();
            _replanPending = true; _replanRes = null;
            int seq = ++_replanSeq;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    _silentPath = true;
                    SSResult res;
                    if (rolling)
                    {
                        var (rcx, rcy) = StandCell(sx, sy);
                        var (sgx, sgy) = SubgoalToward(rcx, rcy, rollWx, rollWy);
                        res = Plan(sgx, sgy, (sx, sy, 0f), rollWx, rollWy);
                    }
                    else res = Plan(finalWx, finalWy, (sx, sy, 0f), goalSnapCap: ReplanGoalSnapCap);
                    _silentPath = false;
                    if (seq == _replanSeq) _replanRes = res;
                }
                catch (System.Exception e) { DiagLog.Write($"[ss-replan] bg EXC {e.Message}"); if (seq == _replanSeq) { _replanRes = null; _replanPending = false; } }
            });
            return true;
        }

        // main thread: dispatch the background replan when it lands.
        static void PollReplan()
        {
            if (!_replanPending) return;
            var res = _replanRes;
            if (res == null) return;
            _replanPending = false; _replanRes = null;
            Visualize(res, _finalGoalWx, _finalGoalWy);
            DiagLog.Write($"[ss-replan] reason={_replanReason} #{_replanCount} goal=({_finalGoalWx},{_finalGoalWy}) found={res.Found} exp={res.Expansions} ms={res.Millis:0.#} steps={res.Steps.Count}");
            // found + zero steps = already AT the goal (drift fired right as we arrived). Not a failure — mark done so
            // block-nav advances to the next leg instead of aborting the whole run.
            if (res.Found && res.Steps.Count == 0) { _execDone = true; return; }
            if ((!res.Found && !res.Partial) || res.Steps.Count == 0) { _execFailCode = "replan_noplan"; return; }
            _replanCooldownLeft = ReplanCooldown;
            _placeStall = 0;
            StartSteps(res.Steps);
        }

        // 闭环走路驱动:朝目标列按键直到身体中心到达,然后【不刹车】结束,让助跑速度带进下一条边(跳跃需要)。
        // 自我纠正:每帧都瞄真实目标,起点错了只是多走几步少走几步,不会整条边平移。
        const float WalkArrivePx = 4f;
        static void WalkTick()
        {
            var p = Main.LocalPlayer;
            if (p == null || !p.active) { _walkActive = false; return; }
            float targetX = _walkTargetCx * 16f + 8f;
            float dx = targetX - p.Center.X;
            if (_walkDir > 0 ? dx <= WalkArrivePx : dx >= -WalkArrivePx) { _walkActive = false; return; }  // arrived, no brake
            if (_walkDir > 0) p.controlRight = true; else p.controlLeft = true;
        }

        // 空中自救:像人往脚下拍一块平台一样,放一块止住,落地后再重规划。
        // 脚下【两】格 —— 人 42px,vy 可达 10/帧,放在脚下一格会被一帧穿过去。
        static bool TryPlungeRescue(Player p, string why)
        {
            if (_greedyActive || _rescueCooldownLeft != 0) return false;
            int fcx = (int)((p.position.X + PhysicsSimulator.PlayerW / 2f) / 16f);
            int feetCy = (int)((p.position.Y + PhysicsSimulator.PlayerH) / 16f);
            int fcy = feetCy + 2;
            if (!CanPlaceReal(fcx, fcy)) return false;
            DiagLog.Write($"[ss-rescue] plunge {why} realVy={p.velocity.Y:0.#} feet={feetCy} → place ({fcx},{fcy})");
            EmitPlace(p, fcx, fcy);
            _rescueCooldownLeft = RescueCooldown;
            return true;
        }

        // 计划管不到的自由落体也要有人看着:救援原先整段住在帧重放里,而 jumpPlace 放完平台帧就用尽了 ——
        // 人没落上去就一路掉到底 (3186,502) 掉了 18 格无人过问。走闭环/无计划时同样是盲区。
        static void WatchUnplannedFall()
        {
            var p = Main.LocalPlayer;
            if (p == null || !p.active || !RecedingNav.Active) return;
            if (_rescueCooldownLeft > 0) _rescueCooldownLeft--;
            // 只在【已经掉得比一跳还深】时才拍砖:走路边故意从台阶上迈下去是正常的,不该拦。
            _fallFrames = p.velocity.Y > RescueFallVy ? _fallFrames + 1 : 0;
            if (_fallFrames >= UnplannedFallFrames) TryPlungeRescue(p, $"unplanned {_fallFrames}f");
        }
        static int _fallFrames;
        const int UnplannedFallFrames = 14;   // ~14 帧自由落体 ≈ 4 格,超过一次跳跃的正常落差

        public static void ApplyControls()
        {
            if (_walkActive) { WatchUnplannedFall(); WalkTick(); return; }
            if (_execFrames == null) { WatchUnplannedFall(); return; }
            var p = Main.LocalPlayer;
            if (p == null || !p.active) { StopExec(); return; }
            if (_execIdx >= _execFrames.Count)
            {
                float cx = p.position.X + p.width / 2f, fy = p.position.Y + p.height;
                float gx = _execGoalWx * 16f + 8f, gy = (_execGoalWy + 1) * 16f;
                DiagLog.Write($"[ss-land] goal=({_execGoalWx},{_execGoalWy}) actual_px=({cx:0.#},{fy:0.#}) dx={(cx-gx):0.#} dy={(fy-gy):0.#}");
                StopExec();
                WatchUnplannedFall();   // 帧用尽的那一刻人可能正在半空往下掉,别在这儿撒手
                return;
            }
            var f = _execFrames[_execIdx];
            float dxp = p.position.X - f.Px;
            float dyp = p.position.Y - f.Py;
            float drift = MathF.Sqrt(dxp * dxp + dyp * dyp);

            // 完整的计划 vs 执行分歧追踪:玩家【现在】的状态反映的是 idx-1 帧设下的控制,所以要和【前一个】计划帧比。
            // 第一个分歧帧就是偏差源头:vx 分歧=加速度/摩擦不符,只有 py 分歧=StepUp/斜坡/半砖不符,N 帧后全分歧=漏了某个逐帧物理。
            if (_execIdx > 0)
            {
                var pf = _execFrames[_execIdx - 1];
                DiagLog.Trc($"[ss-cmp] i={_execIdx - 1} plan(px={pf.Px:0.##} py={pf.Py:0.##} vx={pf.Vx:0.##} vy={pf.Vy:0.##} L={(pf.Left?1:0)}R={(pf.Right?1:0)}J={(pf.Jump?1:0)}) exec(px={p.position.X:0.##} py={p.position.Y:0.##} vx={p.velocity.X:0.##} vy={p.velocity.Y:0.##}) d(px={(p.position.X-pf.Px):0.##} py={(p.position.Y-pf.Py):0.##} vx={(p.velocity.X-pf.Vx):0.##} vy={(p.velocity.Y-pf.Vy):0.##})");
            }

            if (_execIdx % 15 == 0)
                DiagLog.Trc($"[ss-exec] frame={_execIdx}/{_execFrames.Count} expect=({f.Px:0.#},{f.Py:0.#}) actual=({p.position.X:0.#},{p.position.Y:0.#}) drift={drift:0.#}");

            // 执行器自报没进展:同一帧号停住 = 推进条件永远满足不了(jumpPlace 放完人掉到新平台上,
            // 却还在等放置【前】的位置,卡了 45 帧靠 sentinel 从外面救)。哨兵是兜底,不该是唯一的发现者。
            if (_execIdx == _stallIdx) _stallFrames++;
            else { _stallIdx = _execIdx; _stallFrames = 0; }
            if (_stallFrames == StallReport)
                EventLog.W(Ev.Fail, $"执行器卡在 frame {_execIdx}/{_execFrames.Count} 已 {StallReport} 帧 drift={drift:0.#} 人=({p.position.X:0.#},{p.position.Y:0.#}) 期望=({f.Px:0.#},{f.Py:0.#})");

            if (_replanCooldownLeft > 0) _replanCooldownLeft--;
            if (_rescueCooldownLeft > 0) _rescueCooldownLeft--;

            // 本体感觉:从上一帧真实状态按上一帧输入预测一帧,和现在的真实结果比。失配 = 身体没按物理听话(穿透/卡住/被推/入水)。
            // 与计划无关,所以计划帧过期了它照样有效。
            float predVy = p.velocity.Y, mismatch = 0f;
            if (_lastReal.Valid && _execIdx > 0)
            {
                var pin = _execFrames[_execIdx - 1];
                var ph0 = PhysicsSimulator.Params.FromPlayer(p);
                var prev = new PhysicsSimulator.State { Px = _lastReal.Px, Py = _lastReal.Py, Vx = _lastReal.Vx, Vy = _lastReal.Vy, Grounded = _lastReal.Grounded, JumpFramesLeft = pin.Jump ? 1 : 0 };
                var pred = PhysicsSimulator.Step(prev, pin, ph0);
                predVy = pred.Vy;
                mismatch = MathF.Abs(p.position.X - pred.Px) + MathF.Abs(p.position.Y - pred.Py);
            }
            // 俯冲检测:自由落体逐帧看是物理【正确】的(vy 每 tick +grav),单帧本体感觉失配 ~0,看不见它;vy 差也滞后。
            // 最早最干净的信号是【位置】:人明显低于计划帧该在的地方且还在下降,这个差在脱离计划弧线的瞬间出现并单调增长。
            float belowPlan = p.position.Y - f.Py; // +ve = real player is lower than the plan
            bool falling = p.velocity.Y > RescueFallVy && belowPlan > PlungeBelowPx;
            if (mismatch > ProprioMismatchPx)
                DiagLog.Trc($"[ss-proprio] mismatch={mismatch:0.#} realVy={p.velocity.Y:0.#} predVy={predVy:0.#} falling={(falling?1:0)} pos=({(int)(p.Center.X/16f)},{(int)((p.position.Y+p.height)/16f)})");
            // 传送中止:一帧内的位移是任何物理都产生不了的(回忆药水/镜子/被拽走),说明这次导航从新位置看毫无意义。
            // 不救援不重规划(从出生点重规划到旧目标可能几百格,会把规划器撑爆),直接停死,人想去哪自己再下指令。
            if (_lastReal.Valid && mismatch > TeleportPx)
            {
                DiagLog.Write($"[ss-teleport] mismatch={mismatch:0.#} → abort nav");
                _execFailCode = "cancelled";  // teleported away (recall/mirror) — this nav is meaningless now
                StopExec(); StopSteps(); DiagLog.EndRun();
                return;
            }
            // record THIS frame's real state for next frame's prediction (before any early return below)
            _lastReal = new RealState { Px = p.position.X, Py = p.position.Y, Vx = p.velocity.X, Vy = p.velocity.Y, Grounded = p.velocity.Y == 0f, Valid = true };

            if (falling && TryPlungeRescue(p, $"belowPlan={belowPlan:0.#}")) return;

            // 卡住(速度偏差):计划要人这帧在动而真身被挡住(该有 |pf.Vx|,实际 |vx| ~0)—— 按位置距离判的检查看不见的那条轴。
            // 数连续被挡的帧,超过 StuckFrames 就从人真正所在的地方重规划。覆盖撞墙/卡坡这类否则会一路重规划风暴到 MaxReplans 的情况。
            if (!_greedyActive && _execIdx > 0)
            {
                var spf = _execFrames[_execIdx - 1];
                bool blocked = MathF.Abs(spf.Vx) >= VelDevExpect && MathF.Abs(p.velocity.X) < VelDevReal && !(f.Place);
                _stuckFrames = blocked ? _stuckFrames + 1 : 0;
                if (_stuckFrames >= StuckFrames && _replanCooldownLeft == 0)
                {
                    DiagLog.Trc($"[ss-dev] cls=stuck velDev={MathF.Abs(spf.Vx - p.velocity.X):0.#} planVx={spf.Vx:0.#} realVx={p.velocity.X:0.#} → replan");
                    _stuckFrames = 0;
                    if (Replan("stuck")) return;
                }
            }

            // 只在落地时重规划:腾空态不是展开点,跳到一半重规划没有意义。
            // 贪心逐步自我纠正所以跳过这里;逐边执行【要】它 —— 当初把人摔进坑的正是开环漂移。
            if (!_greedyActive && drift > ReplanDriftPx && _replanCooldownLeft == 0 && p.velocity.Y == 0f)
            {
                if (Replan("drift")) return;
            }

            if (f.Place && !TilePlaced(f.PlaceCx, f.PlaceCy))
            {
                if (_placeStall == 0) DiagLog.Trc($"[ss-place] frame={_execIdx} tile=({f.PlaceCx},{f.PlaceCy})");
                _placeStall++;
                if (_placeStall <= PlaceStallMax)
                {
                    // pin the player while waiting for the platform: do NOT press the frame's move keys (they'd
                    // walk it off into the gap before the tile exists). brake residual Vx so it stays put.
                    if (p.velocity.Y == 0f)
                    {
                        if (p.velocity.X > 0.1f) p.controlLeft = true;
                        else if (p.velocity.X < -0.1f) p.controlRight = true;
                    }
                    EmitPlace(p, f.PlaceCx, f.PlaceCy);
                    return; // stall here until the platform exists
                }
                {
                    int fcx = (int)((p.position.X + p.width / 2f) / 16f), fcy = (int)((p.position.Y + p.height - 1f) / 16f);
                    bool nbr = false;
                    for (int ax = -1; ax <= 1; ax++) for (int ay = -1; ay <= 1; ay++) if (Main.tile[f.PlaceCx + ax, f.PlaceCy + ay].HasTile) nbr = true;
                    EventLog.W(Ev.Place, $"FAILED ({f.PlaceCx},{f.PlaceCy}) from=({fcx},{fcy}) nbrSupport={nbr} hasTile={Main.tile[f.PlaceCx, f.PlaceCy].HasTile} → replan");
                }
                _placeStall = 0;
                // greedy re-picks from real position next TickBlocks, so just abort this frame loop. edge-by-edge
                // replans toward the true goal (closed-loop), same as drift.
                if (_greedyActive) { StopExec(); return; }
                if (Replan("place_failed")) return;
                StopExec();
                return;
            }
            if (f.Place) { EventLog.W(Ev.Place, $"OK ({f.PlaceCx},{f.PlaceCy})"); _placeStall = 0; }

            if (f.Left) p.controlLeft = true;
            if (f.Right) p.controlRight = true;
            if (f.Jump) p.controlJump = true;
            if (f.Down) p.controlDown = true;
            // full per-frame replay trace: every frame's intent vs reality. lets one run reveal jump edges,
            // drift, ground state, vx/vy without re-building to add more logging.
            DiagLog.Trc($"[ss-frame] idx={_execIdx}/{_execFrames.Count} J={(f.Jump ? 1 : 0)} L={(f.Left ? 1 : 0)} R={(f.Right ? 1 : 0)} P={(f.Place ? 1 : 0)} cJ={(p.controlJump ? 1 : 0)} vx={p.velocity.X:0.##} vy={p.velocity.Y:0.##} gnd={(p.velocity.Y == 0f ? 1 : 0)} pos=({p.position.X:0.#},{p.position.Y:0.#}) exp=({f.Px:0.#},{f.Py:0.#}) drift={drift:0.#}");
            _prevReplayJump = f.Jump;
            _execIdx++;
            _lastExecFrameCount++;
        }

        // "放好了" = 真有能站的支撑(实心或平台),和模拟当初承诺落点时用的是同一把尺子。光看 HasTile 会撒谎:
        // 目标格里一株草/藤/蛛网就读成"已完成",放置挥空,重放的跳跃差一格 —— (1379,224) 那撮草钉住了整个循环。
        static bool TilePlaced(int cx, int cy)
        {
            if (cx < 0 || cy < 0 || cx >= Main.maxTilesX || cy >= Main.maxTilesY) return false;
            var t = Main.tile[cx, cy];
            return t.HasTile && (Main.tileSolid[t.TileType] || Main.tileSolidTop[t.TileType]);
        }

        static void EmitPlace(Player p, int cx, int cy)
        {
            // the two silent-return paths made a never-materializing platform undiagnosable (the (2959,262) 16-leg
            // stall: 60 ticks/leg of TilePlaced=false with zero telemetry). Log the reason once per stall period.
            int slot = NavCoordinator.FindPlatformSlot(p);
            if (slot < 0)
            {
                if (_placeStall == 1) EventLog.W(Ev.Place, $"STALL ({cx},{cy}) 热键栏没有平台");
                return;
            }
            p.selectedItem = slot;
            Main.SmartCursorWanted_Mouse = false; // SmartCursor would retarget the cursor away from PlaceCx/Cy
            if (p.itemTime > 0)
            {
                if (_placeStall == 1) DiagLog.Write($"[ss-place] STALL-WHY tile=({cx},{cy}) itemTime={p.itemTime} slot={slot} stack={p.inventory[slot].stack}");
                return; // mid-swing; wait for cooldown before re-firing
            }
            if (_placeStall == 1) DiagLog.Write($"[ss-place] emit tile=({cx},{cy}) slot={slot} stack={p.inventory[slot].stack} item={p.inventory[slot].Name}");
            Main.mouseX = (int)(cx * 16f + 8f - Main.screenPosition.X);
            Main.mouseY = (int)(cy * 16f + 8f - Main.screenPosition.Y);
            p.controlUseItem = true;
        }
    }
}
