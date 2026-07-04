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

        // The standing cell is the AIR cell the player's feet occupy (one above the support), matching the
        // distance field's CoarseStand convention. -1 pulls a foot resting exactly on a block top (feetY at the
        // block's top edge) up into that air cell instead of the block row itself.
        // HONEST LABEL: the center-point column is only the label if the 3-row body actually FITS there. The
        // 20px hitbox straddles two columns; a landing 0.7px inside a column whose head rows are solid used to be
        // labeled as standing IN that column — a cell only reachable by digging, whose H the field prices as a dig.
        // The free jump then "stole" that H, next tick the center slid back over the boundary and the truth
        // reasserted → the (800,937)↔(801,937) boundary oscillation. If the center column can't hold the body,
        // the label snaps to the other straddled column; if neither fits (mid-dig, clipped) keep the center
        // (fallback, never unlabeled).
        // raw center rounding, no body-fit snap — the ONLY correct label for planned terrain-altering landings, whose
        // tiles aren't modified yet (StandCell's fit check would judge them against the pre-dig/pre-place world).
        internal static (int cx, int cy) RawCell(float px, float py)
            => ((int)((px + PhysicsSimulator.PlayerW / 2f) / 16f), (int)((py + PhysicsSimulator.PlayerH - 1f) / 16f));

        internal static (int cx, int cy) StandCell(float px, float py)
        {
            int cy = (int)((py + PhysicsSimulator.PlayerH - 1f) / 16f);
            int cx = (int)((px + PhysicsSimulator.PlayerW / 2f) / 16f);
            if (BodyFits(cx, cy)) return (cx, cy);
            // snap only to a column that both fits the body AND has support under the feet — a planned dig landing
            // (tiles still solid at plan time, so its center column "doesn't fit") must NOT get relabeled onto an
            // open neighbor column that has no floor (cliff edge); it keeps its center label via the fallback.
            int leftCol = (int)(px / 16f);
            int rightCol = (int)((px + PhysicsSimulator.PlayerW - 1f) / 16f);
            int other = cx == leftCol ? rightCol : leftCol;
            if (other != cx && BodyFits(other, cy) && HasSupport(other, cy + 1)) return (other, cy);
            return (cx, cy);
        }

        // Same envelope geometry as the field's StepCost body clearance: feet row may hold a slope/half-brick
        // (valid footing — DigSolid there used to mislabel every slope stand as "unfit"), upper rows block on ANY
        // solid shape, and partial footing (feet 6-16px up the slope) pushes the head into the 4th row (42+6 > 48).
        static bool BodyFits(int c, int cy)
        {
            if (PathPlanner.IsBlockPublic(c, cy)) return false;
            if (DigSolid(c, cy - 1) || DigSolid(c, cy - 2)) return false;
            if (DigSolid(c, cy) && DigSolid(c, cy - 3)) return false;   // DigSolid but not IsBlock = slope/half footing
            return true;
        }

        // advance the node with idle input to the state the NEXT replan will actually read: RecedingNav replans on the
        // first tick after the frames end with vy==0, so that's ≥1 idle step, more if the edge ends airborne. Labeling
        // the last planned frame instead lied whenever residual vx slid the player over a cell boundary in that gap —
        // the plan "reached" a cell no post-action read ever sees (the (800,937) phantom 3-point descent, re-picked
        // forever ↔ oscillation). Capped: if still airborne after the cap, use the last state (fallback — the closed
        // loop replans from wherever it really lands).
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

        // a candidate is dominated if some existing label reached the same cell no costlier (g) and with at
        // least as much usable speed (same-direction |vx|, and vy). dominated states can do nothing the
        // dominator can't, so they're pruned — this is what stops one cell soaking up hundreds of vx variants.
        // NOTE: do NOT collapse grounded-cell vx variants to "cheapest only" — the residual landing vx is needed
        // to chain continuous diagonal slides down a sloped seam (each step rides the prior step's velocity).
        // tried it; it broke otherwise-solvable descents by severing that chain.
        static bool Dominated(List<Label> labels, float g, float vx, float vy)
        {
            // quantize vx into VxQuant buckets: flat-ground walk produces a continuum of vx, none strictly dominating
            // (higher vx costs more g), so a cell hoarded hundreds of variants → 8844 re-expansions on a straight walk.
            // same bucket + same sign + cheaper g dominates; the high-vx bucket a slope-slide chain needs still survives.
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
        }

        // one path edge to execute: a frame-replay move (Frames!=null) or the pillar macro (Pillar=true → drive
        // SkillExecutor.StartPillarJump to TargetCy). TargetCx/Cy = the landing cell.
        public class ExecStep
        {
            public bool Pillar;
            public bool Dig;
            public MineDir DigDir;
            public int TargetCx, TargetCy;
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

        // per-step time estimate for the execution watchdog. frame edges know their exact length; walk edges run
        // closed-loop so estimate from distance at cruise; pillar ≈ 43f per 2-cell cycle; dig = the mining table's
        // per-tile frames. Deliberately generous-side constants — the watchdog's margin handles the rest.
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

        // Per-plan scratch state (distance field + caches + jump-place tally). Was global static, which welded a
        // "only one plan at a time" assumption into ~50 call sites. Now passed explicitly so plans are re-entrant:
        // the live executor holds one ctx across frames while a background lookahead plan uses its own.
        public class PlanCtx
        {
            public Dictionary<(int, int), int> DistField;
            public Dictionary<(int, int), int> BlockH;
            public int JpNoSpot, JpNoLand, JpFellThrough, JpSlidOff, JpOk;
        }

        // startOverride: plan from a GIVEN start state (px,py,vx) instead of the live player. used by lookahead
        // pre-planning — compute the next leg from the CURRENT leg's predicted landing while still walking, so the
        // next leg can dispatch with zero stop-and-replan stall. null = plan from the real player (normal path).
        // goalWx/Wy = the cell A* searches to (a near SUBGOAL during rolling). fieldGoalWx/Wy = the cell the cached
        // compass field is keyed on (the FINAL goal) — kept separate so rolling's per-leg subgoals don't each rebuild
        // the million-cell field (the freeze). Pass (-1,-1) [default] to key the field on goalWx/Wy itself (single
        // point nav / non-rolling callers).
        public static SSResult Plan(int goalWx, int goalWy, (float px, float py, float vx)? startOverride = null, int fieldGoalWx = -1, int fieldGoalWy = -1, int maxExp = MaxExpansions, int goalSnapCap = int.MaxValue)
        {
            var ctx = new PlanCtx();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var res = new SSResult();
            var p = Main.LocalPlayer;
            if (p == null || !p.active) return res;
            var ph = PhysicsSimulator.Params.FromPlayer(p);
            var holdOptions = BuildHoldOptions();

            // goalSnapCap: SnapGoalToStandable's unbounded down-scan is navwand-click semantics ("a click in air
            // means the floor below it"). An INTERNAL replan re-targeting a stale edge landing must NOT inherit
            // that: when the world changed under the old landing (a place edge whose platform ended up one column
            // over), the scan can teleport the goal down an open air column — the (2907,223) drift-replan became
            // goal (2907,349) and the planner dutifully planned a 126-cell dive through the rescue platform into
            // an unmineable chasm. If the snap moves the goal further than the caller allows, the leg is
            // meaningless — fail fast, the receding loop re-selects from the real position.
            int requestedWy = goalWy;
            goalWy = SnapGoalToStandable(goalWx, goalWy);
            if (System.Math.Abs(goalWy - requestedWy) > goalSnapCap)
            {
                DiagLog.Write($"[ss-plan] goal snap ({goalWx},{requestedWy})→({goalWx},{goalWy}) exceeds cap {goalSnapCap} → fail fast");
                return res;
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
            // h source depends on caller:
            //  - ROLLING (fieldGoal passed): reuse the CACHED big compass keyed on the final goal — built once, every
            //    leg shares it, no per-leg rebuild.
            //  - SINGLE-POINT (no fieldGoal, e.g. navwand /nav): build a small box field around start↔goal, fast (tens
            //    of ms). Do NOT route these through the big cached field — that's a 1.4s build that froze near nav.
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
            // ROLLING (partial) plan: the nearest GROUNDED state seen. If the goal is out of one budget's reach, we
            // return the path to this instead of failing — the caller walks it and re-plans from there. Must be
            // grounded (a leg ends standing still, so the next leg starts from a valid grounded state).
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
            // ROLLING: goal out of this budget's reach → fall back to the nearest grounded node as a PARTIAL leg, so
            // the caller walks it and re-plans from there. Only when that node is genuinely closer than the start
            // (haveBestGrounded guards "never moved" / start is the only grounded node = truly stuck).
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

                // PERSISTENT clip check: scan every move edge's frames for a player box overlapping a solid tile —
                // i.e. the PLANNED trajectory passes through a wall. if this fires, the simulator/edge is producing a
                // physically impossible path (not an execution drift). reports first clip per step.
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

            // First emit all plain walk/jump edges, tracking whether ANY of them meaningfully reduces the
            // (vertical-aware) heuristic. Horizontal shuffling toward a wall lowers x-distance but not h once
            // blocked; only real progress counts. Placement is expensive, so only build when walk/jump is stuck.
            bool anyProgress = false;
            bool vertProgress = false; // a plain jump that lands the player on a HIGHER cell (climbs a natural ledge)
            int dirToGoal = goalCx >= cur.Px ? 1 : -1;
            var (_, dcy) = StandCell(cur.Px, cur.Py);
            // dir 0 = IN-PLACE vertical jump (no horizontal input): hop straight up onto a ledge directly overhead — the
            // "align then jump up" escape a human uses at a step it can't walk up. Only with hold>0 (hold==0 dir==0 is
            // standing still, a no-op). Alignment is not handled yet (step a): this works where the body already lines up
            // under the ledge; where it doesn't the sim just fails and the edge is skipped (harmless).
            foreach (int dir in new[] { dirToGoal, -dirToGoal, 0 })
            {
                foreach (int hold in holdOptions)
                {
                    if (dir == 0 && hold == 0) continue;   // standing still, not a move
                    var seg = Prof(hold == 0 ? "walk" : "jump", () => SimulateSegment(cur, dir, hold, ph));
                    if (!seg.HasValue) continue;
                    // progress uses the RAW per-cell field, not the block-coarsened Heuristic: inside an 8x8 block
                    // the coarsened H is flat, so every in-block move reads "no progress" and dig fires even where a
                    // plain jump clears a low step. raw field still drops cell-by-cell toward the goal.
                    if (RawProgress(ctx, cur, seg.Value.node)) anyProgress = true;
                    var (_, segFeetCy) = StandCell(seg.Value.node.Px, seg.Value.node.Py);
                    if (segFeetCy < dcy) vertProgress = true;
                    yield return (seg.Value.node, seg.Value.frames, seg.Value.frames.Count, false, null);
                }
            }

            // DROP THROUGH PLATFORM: if standing on a platform (solidTop), holding Down falls through it.
            // SimulateSegment treats platforms as floor (Grounded), so without this no downward edge exists
            // and a platform-floored cell with the goal below is a dead end (replan storm). only emit when a
            // platform actually supports the feet, else this duplicates a plain fall.
            {
                // support is judged over BOTH hitbox columns, not the center cell: the 20px body can rest on a
                // platform EDGE with its center column over open air (the (3393,700) temple-shaft stuck: center
                // support = air → no drop edge generated → only H-rising edges left → shock death). A drop is
                // physically possible when at least one straddled column stands on a platform and NO column
                // stands on a solid block (a solid support holds the body up through a Down-press).
                var (fcx, fcy) = StandCell(cur.Px, cur.Py);
                int dropLc = (int)(cur.Px / 16f);
                int dropRc = (int)((cur.Px + PhysicsSimulator.PlayerW - 1f) / 16f);
                bool anyPlat = PathPlanner.PlatformPublic(dropLc, fcy + 1) || PathPlanner.PlatformPublic(dropRc, fcy + 1);
                bool anySolid = DigSolid(dropLc, fcy + 1) || DigSolid(dropRc, fcy + 1);
                bool plat = anyPlat && !anySolid;
                if (SegDiag && !plat) DiagLog.Write($"[ss-drop] NULL: support plat={anyPlat} solid={anySolid} cols[{dropLc}..{dropRc}] row={fcy + 1}");
                if (plat)
                {
                    // a human drops off a platform by holding Down (+ a direction) and rides the fall all the way
                    // to the real floor, not stopping one tile below. emit drop edges for hold-left / -right /
                    // -straight so A* can pick the one that rides the diagonal seam down to the bottom.
                    foreach (int ddir in new[] { dirToGoal, -dirToGoal, 0 })
                    {
                        var drop = Prof("drop", () => SimulateDrop(cur, ddir, ph));
                        if (drop.HasValue)
                            yield return (drop.Value.node, drop.Value.frames, drop.Value.frames.Count, false, null);
                    }
                }
            }

            // ON-DEMAND PLATFORM (see memory project_ondemand_platform): platforms are NOT enumerated everywhere.
            // they exist only where the maze gradient wants to go but physics blocks it. find the first obstacle
            // along the gradient direction, then generate ONE platform edge toward the standable cell on its far
            // side. obstacle type picks the platform type. this collapses ~dozen platform edges/cell to ~1-2.
            // jump-place stays a first-class option (A* picks it by cost). but PILLAR is bottom-tier: only when a
            // plain jump can't climb either. pass vertProgress so OnDemandPlatformEdges can gate pillar on it —
            // a natural ledge a plain jump reaches (vertProgress) must NOT spawn a pillar (human climbs it bare).
            if (platformTile >= 0 || hasPickaxe)
                foreach (var pe in OnDemandPlatformEdges(ctx, cur, ph, platformTile, vertProgress, hasPickaxe, anyProgress))
                    yield return pe;
        }

        // MAX_SCAN = a jump's horizontal reach in tiles, from live stats (≈ maxRun × jumpHeight / gravity ≈ 7-8).
        // beyond this a plain jump can't cross, so an obstacle within range needs a platform. self-documenting,
        // scales with gear instead of a magic 8.
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

            // --- VERTICAL: maze wants UP and a plain jump can't reach. prefer in-place VERTICAL JUMP-PLACE (跳放):
            // jump straight up (dir=0), drop ONE platform at the arc top, land on it — gains several tiles at once
            // when a foothold (e.g. a tree) lets the tile stick. only fall back to PILLAR (原地一格格垒) when no
            // jump-place clears VertPlaceMinRise tiles (a short hop isn't worth the jump/land overhead).
            // VERTICAL UP — UNCONDITIONAL (no upH<curH gate). Climbing up is the only escape from a pit wall, where the
            // cell straight up is NOT immediately lower H (you must rise then walk out), so the old gate suppressed every
            // up-move and stranded the bot at the bottom. Like the unconditional digs: always emit, let g+H + the line-
            // deviation penalty decide — in a pit, rising shrinks deviation so it wins; on flat ground its cost/H lose to
            // a plain walk so it's harmlessly ignored. In-place vertical jump-place (跳放) lifts the bot a tile at a time,
            // turning a far overhead obstacle into an adjacent one (then a plain step/dig clears it).
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
                if (!anyVertJumpPlace && !vertProgress && SkillExecutor.CanPillarFrom(ccx, ccy, out int topFeetY) && topFeetY < ccy)
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

            // FREE FALL off a ledge: when the foot column is open below (a cliff/shaft, not a wall), ride it down to
            // the real floor — exactly what a human does (mined=0). DigDown only reaches DigMaxScan deep and needs to
            // mine, so a 24-tile cliff探不到底就作废; this no-dig edge covers any depth. emit before DigDown so A*
            // prefers falling over digging a shaft.
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

            // HORIZONTAL DIG — UNCONDITIONAL, both directions. Dig must always be an option: gating it behind obstacle
            // classification (isWall, which relies on a fragile walkprobe) meant that in an awkward stance — feet on a
            // half-brick, head jammed on the ceiling — the classifier misfired and NO dig edge was generated, so Expand
            // came up empty and the bot "stuck" though a human just steps/digs aside. DigThroughWall already mines the
            // full 3-row body column (handles player width/height) and only returns toward-goal landings, so emitting it
            // unconditionally is safe; cost+field selection still demotes it when a plain walk works.
            if (hasPickaxe && ctx.DistField != null)
                foreach (int ddir in new[] { gdir, -gdir })
                {
                    var dw = Prof("digwall", () => DigThroughWall(ctx, ddir, ccx, ccy, curH));
                    if (dw.HasValue)
                        yield return (dw.Value.node, null, dw.Value.cost, false, dw.Value.tiles);
                }

            // VERTICAL UP — now unconditional too (was gated on !anyProgress && Vx<max, which suppressed the up-dig in
            // awkward stances). Mine 2 rows above the head + pillar up, until breaking into a lower-H cell. Still needs
            // platformTile (blocks to pillar on — a physical necessity, not a heuristic gate). DigUp returns null unless
            // the ceiling above leads to lower H, so emitting it always is safe; selection demotes it when cheaper moves exist.
            if (hasPickaxe && platformTile >= 0 && ctx.DistField != null)
            {
                var du = Prof("digup", () => DigUp(ctx, cur, ccx, ccy, curH));
                if (du.HasValue)
                    yield return (du.Value.node, null, du.Value.cost, true, du.Value.tiles);
            }

            // --- HORIZONTAL: the obstacle is wherever a PLAIN WALK can no longer advance. SimulateSegment's Step
            // includes Collision.StepUp, so it truthfully climbs half-bricks / shallow slopes / 1-tile ledges and
            // stops only at something it genuinely can't pass. a static IsBlockPublic scan was blind to slopes/half-
            // bricks (Slope==0 && !IsHalfBlock) — it reported obsX=none at a slope half-brick the walk couldn't clear,
            // so no dig/jump-place edge was generated and A* dead-ended there. let the walk's stop define the obstacle.
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
            // CHASM override: the walk probe defines the obstacle by where the walk STOPPED — but a walk that falls
            // into a deep chasm keeps "advancing" along its bottom and reports the far wall as the obstacle (22 cells
            // away, out of jump-place reach → no bridge candidate at all). At (3277,1024) that made every candidate a
            // 30-cell tumble to the valley floor while the field's route floated east over the gap: climb, tumble,
            // re-climb. If, before the reported obstacle, some column has NO landing within ChasmProbeDepth rows below
            // the current stance, that ledge IS the obstacle and it is a GAP — the bridge machinery gets its chance
            // (candidates only; cost still decides bridge vs descend).
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
            DiagLog.Write($"[ss-bridge-dir] from=({ccx},{ccy}) gdir={gdir} targetDir={targetDir} obsX={obsX} wall={isWall} gap={isGap} maxScan={maxScan}");
            if (!isWall && !isGap) yield break;          // walk can pass freely → plain walk/jump handles it

            if (isWall)
            {
                // WALL: prefer JUMP-PLACE (跳放) — jump and, when the descending arc's foot has a real placement spot
                // (CanPlaceReal inside JumpPlace), drop ONE platform and land on it, gaining several tiles at once.
                // this is what a human does at a wall with a foothold. only when NO hold finds a spot (pure sheer
                // wall, no placement point) fall back to PILLAR (原地垒). this is the 2a-vs-2b distinction.
                bool anyJumpPlace = false;
                bool pillarGen = false;
                if (platformTile >= 0)
                {
                    foreach (int hold in BuildHoldOptions())
                    {
                        var jp = Prof("jplace", () => JumpPlace(ctx, cur, gdir, hold, ph, platformTile));
                        if (jp.HasValue) { anyJumpPlace = true; yield return (jp.Value.node, jp.Value.frames, jp.Value.frames.Count + JumpPlaceCost, false, null); }
                    }
                    if (!anyJumpPlace && !vertProgress && SkillExecutor.CanPillarFrom(ccx, ccy, out int topFeetY) && topFeetY < ccy)
                    {
                        pillarGen = true;
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
                // GAP: prefer JUMP-PLACE-ACROSS (移动跳放横穿) — jump toward the far side, drop one platform on the
                // descending arc, land on it. This is what a human does: hop across, dropping footholds. Only when
                // NO hold finds an across-landing (gap too wide for one jump) fall back to BridgePlace (原地搭桥).
                // BridgeCost == JumpPlaceCost so neither is preferred on price; reachability decides.
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

        // solid INCLUDING slopes/half-bricks: IsBlock deliberately excludes them (walk logic treats them as
        // passable), but a sloped half-tile still supports the player — exactly what strands a shaft descent.
        // anything that can hold the hitbox must be on the mining list.
        static bool DigSolid(int x, int y)
        {
            if (x < 0 || y < 0 || x >= Main.maxTilesX || y >= Main.maxTilesY) return false;
            var t = Main.tile[x, y];
            return t.HasTile && Main.tileSolid[t.TileType] && !Main.tileSolidTop[t.TileType];
        }

        // Dig a shaft straight down through the floor. The player (20px) can't fall through a 1-tile hole,
        // so the shaft is TWO columns wide: the standing column + the neighbor the player's center leans
        // toward. Stops at the first cell below with floor under either column; only yields when that
        // landing cell has lower maze H than here (digging down must be progress toward the goal —
        // covers sealed cave goals, where the surface isn't in the field at all and curH==MaxValue).
        // worth digging down to a landing of maze-H lh: clearly closer along the field (curH - lh >= margin) and
        // no lateral walk reaches an equally-low cell (else route around instead of mining). follows the maze
        // field, which already plans the cheapest rock-penetrating route to the goal.
        // ATOMIC down-dig: mine ONLY the one cell directly underfoot (the player is 20px wide so that's two columns,
        // the standing column + the neighbor the center leans toward) and step down into it. Not a shaft to a landing —
        // just one cell. The closed loop re-plans from the new real position each cycle, so a deep descent emerges as
        // dig→dig→dig (or dig→fall once a cavity opens) rather than one pre-planned tunnel. This kills the DigMaxScan
        // "no landing within 12 → null" dead-end that stranded the bot at a thick wall, and aligns plan with execution
        // (one cell per edge, no over-mined tunnel that the next cycle finds unnecessary).
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

        // Dig upward through a sealed ceiling: per cycle mine 2 rows above the head (2 columns, same body-width
        // reason as DigDown), then pillar-jump 2 tiles onto placed blocks. Yields only when the ceiling is
        // actually sealed (first cycle mines something — open headroom belongs to jump/jump-place/pillar) and
        // the breakout cell has lower maze H.
        static (SSNode node, List<(int wx, int wy)> tiles, float cost)? DigUp(PlanCtx ctx, SSNode cur, int ccx, int ccy, int curH)
        {
            // MUST match SkillExecutor's live head-check columns exactly (leftCol/rightCol from p.position.X), else
            // DigUp can clear cells the pillar executor never looks at while leaving unchecked the ones it does —
            // pillar then aborts mid-climb on a "blocked" ceiling this plan believed was already mined (the
            // (3242,299)↔(3242,300) stuck loop: DigUp mined (ccx,c2) by cell-center lean, pillar checked
            // (leftCol,rightCol) by live pixel position — different columns).
            int leftCol = (int)(cur.Px / 16f);
            int rightCol = (int)((cur.Px + PhysicsSimulator.PlayerW - 1) / 16f);
            var tiles = new List<(int, int)>();
            float cost = 0f;
            for (int k = 1; k * 2 <= DigMaxScan; k++)
            {
                foreach (int y in new[] { ccy - 1 - 2 * k, ccy - 2 - 2 * k })
                    foreach (int c in new[] { leftCol, rightCol })
                        if (DigSolid(c, y))
                        {
                            int fc = DigTable.CostFrames(c, y);
                            if (fc >= DigTable.Unmineable) { if (SegDiag) DiagLog.Write($"[ss-digup] NULL: unmineable ({c},{y})"); return null; }
                            cost += fc;
                            tiles.Add((c, y));
                        }
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

        // ATOMIC horizontal dig: mine ONLY the one adjacent column (its 3 body rows) along dir and step into it. Not a
        // tunnel to a far standable cell — just one cell. Same reason as the atomic down-dig: the closed loop re-plans
        // each cycle, so a thick wall is breached as dig→dig→dig (横竖组合 emerges across cycles), with no DigMaxScan
        // "no landing within 12 → null" dead-end (the (744,998) stuck) and no over-mined tunnel the next cycle finds
        // unnecessary. If the cell underfoot in the entered column is open, the bot falls in — next cycle handles the
        // landing from the real position; we only commit to mining this one column.
        static (SSNode node, List<(int wx, int wy)> tiles, float cost)? DigThroughWall(PlanCtx ctx, int dir, int ccx, int ccy, int curH)
        {
            int x = ccx + dir;
            var tiles = new List<(int, int)>();
            float cost = 0f;
            foreach (int y in new[] { ccy, ccy - 1, ccy - 2 })
                if (DigSolid(x, y))
                {
                    int fc = DigTable.CostFrames(x, y);
                    if (fc >= DigTable.Unmineable) { DiagLog.Write($"[ss-digwall] from=({ccx},{ccy}) dir={dir} UNMINEABLE at ({x},{y}) → null"); return null; }
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

        // "Jump and place one platform": jump (hold), scan the arc for the FIRST frame where the foot cell is
        // empty + adjacent to real support (cliff/wall), place a platform there, and land on it. One placement
        // per jump, placed tile is NOT stored in the node (node stays pure physics) — the landing node simply
        // stands on the new platform's top, supported by real terrain. Covers "hug a wall/block and jump-place
        // upward". Pure open-air pillaring (placement supported only by prior placements) is left to the macro.
        // Is there ANY placeable cell within one jump's reach toward dir? Conservative box: dir × MaxScan wide,
        // from one jump's apex height down to PlatformMaxDropTiles below the feet. Wider than any real arc, so a
        // false "none" is impossible — only used to skip a jump-place that provably has nowhere to drop a platform.
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
            }
            return false;
        }

        static (SSNode node, List<PhysicsSimulator.ControlInput> frames)? JumpPlace(
            PlanCtx ctx, SSNode cur, int dir, int hold, PhysicsSimulator.Params ph, int platformTile)
        {
            if (hold == 0) return null; // need to leave the ground

            // O(1) early-out: if NO placeable cell exists anywhere in the jump's reach box, the arc sim + drop scan
            // below are guaranteed to noSpot — skip them. CanPlaceReal is a tile query; the box is one jump's reach.
            // Conservative (scans wider than the real arc) so it never rejects a jump-place that could actually exist.
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

            // scan downward from BELOW the apex foot (a platform in the apex foot's own cell can't catch the player —
            // they descend through it back to origin). pick the highest cell that is placeable AND lands the player
            // ABOVE the start (a real rise, not a fall-back). not pinned to a fixed offset.
            int startFootCy = (int)((cur.Py + PhysicsSimulator.PlayerH) / 16f);
            int placeCx = int.MinValue, placeCy = 0;
            (SSNode node, List<PhysicsSimulator.ControlInput> frames)? seg = null;
            for (int py = apexFootCy + 1; py <= apexFootCy + PlatformMaxDropTiles; py++)
            {
                if (!CanPlaceReal(apexFootCx, py)) continue;
                var trySeg = SimulateWithPlatform(cur, dir, hold, ph, apexFootCx, py, platformTile);
                if (!trySeg.HasValue || !trySeg.Value.node.Grounded) continue;
                int landFc = (int)((trySeg.Value.node.Py + PhysicsSimulator.PlayerH) / 16f);
                if (landFc >= startFootCy) continue; // landed at/below start = fell back, not a rise
                placeCx = apexFootCx; placeCy = py; seg = trySeg; break;
            }
            if (placeCx == int.MinValue) { ctx.JpNoSpot++; return null; }
            float probeVy = 0f, probeFootPy = 0f;
            // must actually land ON the placed platform — otherwise the player passed through it and landed
            // elsewhere (often back on the ground). Such "place but fall through" edges are useless and, when
            // admitted, flood the search with cheap no-op placements (exp blowup). Reject them.
            int landFeetCy = (int)((seg.Value.node.Py + PhysicsSimulator.PlayerH) / 16f);
            if (F_LandOnPlat && landFeetCy != placeCy)
            {
                if (ctx.JpFellThrough < 12)
                    DiagLog.Write($"[ss-ft] place=({placeCx},{placeCy}) hold={hold} dir={dir} probeVy={probeVy:0.#} probeFootPy={probeFootPy:0.#} platTopPy={placeCy * 16} landFeetCy={landFeetCy}");
                ctx.JpFellThrough++; return null;
            }
            if (!MarkPlaceFrame(seg.Value.frames, placeCx, placeCy)) { ctx.JpNoSpot++; return null; } // unreachable placement

            // Landing on a 1-tile platform with residual Vx slides the player off next frame. Append a brake:
            // counter-press to decelerate; the landing node takes the actual settled position. Only a slide
            // that loses ground contact (falls off) invalidates the edge.
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

        // "Jump and place ONE platform to cross a gap" (移动跳放横穿). Unlike JumpPlace (which only accepts
        // landings HIGHER than the start — climbing), this accepts same-height / lower landings as long as the
        // landing cell's maze H drops below here (real progress toward goal). One placement per (dir,hold): scan
        // the descending arc for the FIRST foot cell that is placeable + adjacent to real support, drop a
        // platform, land on it. Placed tile NOT stored in node (pure-physics key, no combinatorial blowup —
        // the 118ab5f lesson). H-gate caps fan-out: an across-place that doesn't reduce H is never yielded.
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

        // After landing on a narrow platform with residual Vx, counter-press to decelerate. The landing node
        // takes the ACTUAL settled position — a wall or platform edge may stop the slide, that's a valid stand.
        // Only a slide that loses ground contact (falls off) invalidates the edge.
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

        // target cell empty (or cuttable) + at least one real neighbor gives support (block or background wall)
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

        // Temp-write one platform tile into Main.tile, simulate the jump, restore. The placed tile is not kept
        // in the node — it exists only for this segment's landing physics (the new node simply stands on top).
        // 3-1 bridge place: place ONE platform on the support row one column toward dir, step ONTO it, and brake to
        // a stop on that exact cell (don't overshoot — a single tile only holds one cell of standing room; walking
        // a full segment slides off the end and falls). One tile per call; greedy re-picks each step.
        static (SSNode node, List<PhysicsSimulator.ControlInput> frames)? BridgePlace(
            SSNode cur, int dir, PhysicsSimulator.Params ph, int platformTile)
        {
            // basis = the foot column that actually has floor support, NOT the center column. the 20px base spans
            // 2-3 cols; the center can land on a column the player merely overhangs (no support there), making the
            // new tile adjacent to empty space → game rejects it. extend from the supported column toward dir.
            int lcol = (int)(cur.Px / 16f), rcol = (int)((cur.Px + PhysicsSimulator.PlayerW - 1) / 16f);
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
                float targetCenterPx = placeCx * 16f + 8f;     // stop with player center over the new tile
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
                // landing is GEOMETRICALLY certain: a solid platform was placed at (placeCx,placeCy), so the bot stands on
                // it at (placeCx, placeCy-1) — no need to trust the approximate sim's Grounded/end-cell to validate it.
                // The old "if(!s.Grounded) return null" + "if(lcx!=placeCx) return null" killed a perfectly good bridge at
                // a pit edge whenever the imperfect PhysicsSimulator misread a frame as airborne mid-step. The sim is kept
                // only to PRODUCE the drive frames; its imprecision no longer vetoes the edge. Drift is absorbed by the
                // closed loop replanning from the real position next cycle.
                var f0 = frames[0];                            // place on the first frame (before stepping over)
                f0.Place = true; f0.PlaceCx = placeCx; f0.PlaceCy = placeCy;
                frames[0] = f0;
                float standPx = placeCx * 16f + 8f - PhysicsSimulator.PlayerW / 2f;
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
            // FRAGILE: keep native slope (NOT forced Solid). a Solid platform blocks the ascent of an in-place
            // vertical jump-place → player never clears it, falls back to origin (selfloop). real platforms are
            // solidTop: pass-through going up, catch on descent.
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

        // Tag the placement frame. HARD RULE: placement must happen AT or AFTER the apex (vy >= 0) — never on the
        // ascent. On the ascent the player is still rising through the target row and a platform there is either
        // unsupported or gets passed through. Among apex-and-later frames, place on the FIRST one whose tileRange
        // covers the landing cell: the apex is the highest+leftmost point and often can't reach a far/low landing
        // (out of reach → UseItem silently fails → airborne stall → fall), so we wait for the descent to bring the
        // player into reach, then drop straight onto the just-placed platform.
        // returns false if NO apex-or-later frame can reach it (caller must reject the edge — it's unexecutable).
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

        // A human drops off a platform by holding Down (+ a direction) and rides the fall all the way to the real
        // floor — exactly the recorded path here: hold Down to clear the platform, hold a direction, ride the
        // diagonal down to the bottom. Down is held only until clear of the start platform (so the player can land
        // on a lower platform/floor instead of phasing through everything); the direction is held the whole way.
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
            // NO IsFloorPublic re-check: physics Grounded after the drop IS the authoritative landing. The check
            // misfired when StandCell rounds the sub-pixel landing py up a tile, killing a real drop (same bug as the
            // free-fall fix, commit dc2a9e6) → drop edge vanished → had to hand-mine the platform to proceed.
            return (node, frames);
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
                    // a jump in Terraria cannot re-launch the instant it touches ground — it spends (at least) one
                    // frame ON the ground first, sliding with its residual vx. the planner used to end the edge AT
                    // the touch frame and start the next edge there, omitting that grounded slide → every edge's
                    // landing was ~vx*1frame (~3px) short of where execution actually is → seam drift accumulated.
                    // model that one grounded settle frame so the planned landing matches the real one.
                    var settle = PhysicsSimulator.Step(s, input, ph);
                    var sf = input; sf.Jump = false; sf.Px = settle.Px; sf.Py = settle.Py; sf.Vx = settle.Vx; sf.Vy = settle.Vy;
                    frames.Add(sf);
                    s = settle;
                    break;
                }
                if (s.Grounded && hold == 0)
                {
                    // don't end the walk while the feet are over empty space (e.g. stepped off a 1-wide
                    // platform/ledge): the sim still reads Grounded for a frame, but ending here yields a
                    // fakeStand that gets rejected, killing the walk-off-ledge edge. keep simulating so the
                    // player actually falls to the real floor below.
                    var (wcx, wcy) = StandCell(s.Px, s.Py);
                    bool footSupported = PathPlanner.IsFloorPublic(wcx, wcy + 1);
                    // walk a full stride before ending the edge, not 24px. At 24px the edge died in the acceleration
                    // ramp (0.08/frame, ~37 frames to reach maxRun) so every walk edge averaged ~1px/frame — making
                    // walk look far slower per cell than a jump (which yields one long edge), so A* picked jumps on
                    // flat ground. WalkStridePx ≈ a jump's horizontal reach, so walk and jump edges span comparable
                    // distance and their per-cell cost is comparable; A* then chooses by real cost, not edge length.
                    if (footSupported && MathF.Abs(s.Px - startPx) >= WalkStridePx) break;
                    if (footSupported && MathF.Abs(s.Px - prevPx) < 0.05f && f >= 2) break; // wall: not advancing
                }
            }
            if (frames.Count == 0) { if (SegDiag) DiagLog.Write($"[ss-seg] dir={dir} hold={hold} NULL: no frames"); return null; }
            var node = new SSNode { Px = s.Px, Py = s.Py, Vx = s.Vx, Vy = s.Vy, Grounded = s.Grounded };
            if (MathF.Abs(node.Px - cur.Px) < 1f && MathF.Abs(node.Py - cur.Py) < 1f) { if (SegDiag) DiagLog.Write($"[ss-seg] dir={dir} hold={hold} NULL: no move (dpx={node.Px - cur.Px:0.#} dpy={node.Py - cur.Py:0.#}) gnd={node.Grounded}"); return null; } // no self-loops
            // FRAGILE: in water gravity is so weak the sim still reads Grounded=true while floating over empty cells
            // (the player hasn't sunk enough to register a non-ground frame). a grounded landing whose foot columns
            // have NO real floor below is a fake stand — reject it so A* must place a platform instead of "walking"
            // across open water and looping. only applies to grounded landings (airborne fall/jump edges are fine).
            if (node.Grounded)
            {
                var (ncx, ncy) = StandCell(node.Px, node.Py);
                // a slope / half-brick supports the player but IsFloorPublic excludes it → it wrongly read as a fake
                // stand and killed every walk/jump off a half-brick tile (EXPAND-EMPTY death穴). DigSolid认 slopes/
                // half-bricks as支撑 (see its comment), so accept either as real floor below the landing.
                if (!PathPlanner.IsFloorPublic(ncx, ncy + 1) && !DigSolid(ncx, ncy + 1))
                {
                    if (SegDiag)
                    {
                        var bt = Main.tile[ncx, ncy + 1];
                        DiagLog.Write($"[ss-seg] dir={dir} hold={hold} NULL: fake-stand at ({ncx},{ncy}); below ({ncx},{ncy + 1}) type={bt.TileType} hasTile={bt.HasTile} slope={(int)bt.Slope} half={bt.IsHalfBlock} solid={Main.tileSolid[bt.TileType]} solidTop={Main.tileSolidTop[bt.TileType]}");
                    }
                    return null;
                } // reported stand cell has no floor = fake
            }
            if (SegDiag) DiagLog.Write($"[ss-seg] dir={dir} hold={hold} OK -> ({StandCell(node.Px,node.Py).Item1},{StandCell(node.Px,node.Py).Item2}) gnd={node.Grounded}");
            return (node, frames);
        }

        // Walk off a ledge and ride gravity to the real floor — no dig, any depth. Holds gdir the whole way (a human
        // keeps the direction held while falling). Returns null if the player never leaves the ground (no cliff here,
        // plain walk already covers it) or never lands within the fuse.
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
            // NO IsFloorPublic re-check: physics Step returning Grounded after a real fall IS the authoritative
            // landing. The fake-stand guard (IsFloorPublic on ncy+1) misfired when StandCell rounds the sub-pixel
            // landing py up a tile, killing a genuine vertical fall (the (2944,364) bug).
            var node = new SSNode { Px = s.Px, Py = s.Py, Vx = s.Vx, Vy = s.Vy, Grounded = true };
            return (node, frames);
        }

        // A goal cell the player can't stand on is unreachable, and the search burns its whole budget trying.
        // Two cases a navwand click hits: the goal floats in air (no floor under it), or it lands INSIDE a solid
        // block (mis-click into terrain). Snap to the nearest standable cell in the same column — searching BOTH
        // ways by distance: up climbs out of a block to its top surface, down drops a floating goal to the floor.
        const int GoalSnapMaxDrop = 40;
        static bool Standable(int gx, int gy) => PathPlanner.IsFloorPublic(gx, gy + 1) && !PathPlanner.IsBlockPublic(gx, gy);
        public static int SnapGoalToStandable(int gx, int gy)
        {
            if (Standable(gx, gy)) return gy;
            // clicked INTO a block → climb up to its surface (bounded: a surface is a few tiles up). clicked in AIR →
            // fall down to the ground, however deep — a click in air means "go to the floor below it", and capping the
            // drop left the goal floating mid-air over a deep pit, which A* burned its whole budget failing to reach.
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

        // Temporary failure diagnostic: ASCII map of the start↔goal region with the explored frontier overlaid,
        // to see why a plan that should exist wasn't found. '@'=start 'G'=goal '#'=solid '='=platform
        // '/'=slope/halfbrick '*'=explored-air '.'=air
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

        // The maze field is a memoryless 2D cost grid: it scores a path only by total weighted cost, blind to the
        // ORDER of moves. So "up-then-right" and "right-then-up" get identical H even though the player's physics
        // make them very different (horizontal speed feeds the jump). Its per-cell gradient down the start column
        // tricks A* into climbing straight up (pillar) instead of walking out and jumping diagonally like a human.
        // Fix: coarsen H to N×N blocks (min field value in the block). The block's interior has a FLAT H, so A*
        // no longer chases the per-cell vertical gradient — it explores move order freely via physics Expand, and
        // the field only steers the coarse region-to-region direction. HBlockSize=1 disables (per-cell = old behavior).
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

        // progress on the RAW per-cell maze field (not block-coarsened): landing cell's H lower than the current
        // cell's. used to decide "a plain move already advances → don't dig"; the coarsened Heuristic is flat
        // inside a block and would wrongly report no progress for in-block moves.
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
        // the TRUE destination, set once when execution starts and never overwritten by a per-step target. replan
        // must aim here — replanning toward the local step target (the old _execGoal during edge exec) sent the bot
        // to a mid-path cell, which is why replan was wrongly disabled and open-loop drift then dropped it in a pit.
        static int _finalGoalWx, _finalGoalWy;

        // ROLLING nav (budget-limited A* over a long route): a single Plan only reaches one budget's worth; when a leg
        // is PARTIAL we re-Plan from the new position toward the TRUE final goal, leg after leg, until we arrive. If
        // several legs in a row stop making progress toward the goal (local minimum — sealed pit, etc.) we bail.
        static bool _rolling;
        static int _rollFinalWx, _rollFinalWy;
        static float _rollPrevDist;          // goal distance at the end of the previous leg (to detect "not advancing")
        static int _rollStuckLegs;
        const int RollMaxStuckLegs = 3;      // consecutive legs without progress → genuinely stuck → give up
        const float RollProgressPx = 16f;    // a leg must close at least this much distance to count as progress

        // LOOKAHEAD: while the current leg walks, a thread-pool task plans the NEXT leg from this leg's predicted
        // landing, so arrival doesn't pay a synchronous Plan (the per-leg main-thread hitch). On arrival, if the
        // cached plan's start matches the real landing it dispatches with zero stall; otherwise we plan fresh.
        static volatile System.Threading.Tasks.Task _rollBgTask;
        static volatile SSResult _rollBgResult;
        static int _rollBgFromCx, _rollBgFromCy;   // predicted landing the bg leg planned from (for arrival validation)
        const int RollLandMatchTol = 2;

        const float ReplanDriftPx = 24f;
        const int ReplanCooldown = 10;
        static int _replanCooldownLeft;
        // airborne self-rescue: when execution drifts off-arc AND the player is falling (off-track plunge — e.g. a
        // failed placement / missed platform), don't wait to hit bottom. like a human who steps into air and slaps a
        // platform under their feet, drop a platform just below the feet to arrest the fall, then replan from there.
        const float RescueFallVy = 1.0f;   // vy above which we count as genuinely descending (not apex jitter)
        const float PlungeBelowPx = 24f;   // real player this far BELOW the planned frame (+still falling) = off-arc plunge
        const int RescueCooldown = 20;     // frames between rescue attempts so we don't spam-place every tick
        static int _rescueCooldownLeft;
        // STUCK = velocity deviation: the plan expected the player to be moving (|pf.Vx| >= VelDevExpect) but the real
        // body is nearly still (|vx| < VelDevReal) and not advancing — "wanted to move, didn't" (wall / slope jam).
        // this is the velocity axis of the unified deviation: position-distance checks miss it because the player
        // barely moves. after StuckFrames such frames, replan from the real spot.
        const float VelDevExpect = 1.5f;   // plan expected at least this |Vx|
        const float VelDevReal = 0.4f;     // but real |Vx| is below this = blocked
        const int StuckFrames = 18;        // consecutive blocked frames before declaring stuck
        static int _stuckFrames;

        // PROPRIOCEPTION: instead of comparing position to the (possibly stale) planned frame, predict where ONE
        // bare-player frame should land from last frame's real state under last frame's input, and compare to the
        // real result. the mismatch is independent of whether the plan is right — it directly measures "my body did
        // not respond to my command as physics says it should". this one signal covers every off-physics surprise:
        //   real vy >> expected   → falling through (missed/failed platform)
        //   real move << expected → stuck (cobweb / honey)
        //   real vx flipped       → knockback / shoved
        //   wet mismatch          → unexpected water
        struct RealState { public float Px, Py, Vx, Vy; public bool Grounded, Valid; }
        static RealState _lastReal;
        const float ProprioMismatchPx = 6f;   // per-frame predicted-vs-actual gap that flags a control anomaly
        const float TeleportPx = 160f;         // one-frame jump beyond any possible physics = teleport/yank → abort nav
        static int _replanCount;
        static bool _silentPath;   // suppress the full [ss-path] dump during replan (storms flood the log); the [ss-replan] summary line carries the delta instead
        const int MaxReplans = 40;
        static int _placeStall;
        const int PlaceStallMax = 60;

        public static bool IsActive => (_execFrames != null && _execIdx < _execFrames.Count) || _walkActive;

        // CLOSED-LOOP walk: instead of open-loop replaying the planned frames (which平移s the whole edge if the start
        // is off), press toward the target X and finish when the body reaches it — self-correcting, absorbs the接力
        // drift. NO brake on arrival: vx carries into the next edge (a jump needs the run speed, don't zero it).
        static bool _walkActive;
        static int _walkTargetCx, _walkDir;

        public static void StopExec() { _execFrames = null; _execIdx = 0; _walkActive = false; }

        // full stop of the step/rolling executor (J pause, or any external cancel): kill the current leg's frames,
        // the step list, and the rolling loop so it doesn't auto-plan another leg.
        public static void StopNav() { _rolling = false; _rollBgResult = null; _replanPending = false; _replanSeq++; StopSteps(); StopExec(); DiagLog.EndRun(); }

        // ===== execution status machine (parity with NavCoordinator.Done/IsActive/FailCode) so HTTP /nav + /nav_done
        // can drive the NEW StateSpacePlanner the same way the old NavCoordinator did. set by Execute / the step loop /
        // failure exits; read by HttpServerSystem. "running" = a route is in flight (steps or frames active).
        static bool _execDone;
        static string _execFailCode;     // null while running/ok; set on any failure exit
        public static bool ExecDone => _execDone;
        public static string ExecFailCode => _execFailCode;
        static SSResult _lastExecResult;
        public static SSResult LastExecResult => _lastExecResult;   // the plan the current/last leg dispatched (for lookahead landing prediction)
        // running iff a route is dispatched and not yet ended (steps drive edges; _execFrames is one edge's replay).
        public static bool ExecRunning => StepsActive || IsActive || _greedyActive || _replanPending || _asyncPending;

        // ===== Action-graph path executor: run ActionGraphPlanner.Plan's path edge-by-edge. Jump edges REPLAY the
        // edge's own forward-simulated frames (planned trajectory == executed trajectory). pillar/bridge/dig go to
        // their state-machine executors. each edge starts only when the player is landed + at rest (clean state).
        // Edge-by-edge executor for a state-space Plan path. frame steps replay their own simulated frames (planned
        // == executed); pillar steps drive SkillExecutor.StartPillarJump (the macro climb). each step starts only
        // when the previous executor is idle and the player is landed + settled (clean rest state).
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

            // WATCHDOG — every action gets a deadline. All the plan-level defenses (miss, revisit, shock, loop
            // detector) live in the replan cycle, and the replan cycle waits for ExecRunning to clear — so ONE
            // executor that never terminates (the 77s PillarWait) starves the entire immune system. Deadline =
            // its own estimated frames × margin (margin scales with the estimate: long actions get more slack)
            // + a floor for tiny actions. On breach: announce, kill every executor, hand control back to the
            // closed loop — the next replan retries from reality and attention prices repeated failures.
            // two watchdog clocks. SOFT (slides while moving): a long free-fall is physics working, not a hang — only
            // a frozen position runs this clock down (the 77s PillarWait fires fast). HARD (absolute, never slides):
            // an in-step motion loop (bouncing around a target it never satisfies) would reset the soft clock forever —
            // the hard cap bounds the step no matter how lively it looks. Between-step loops are the replan-level
            // detector's job (best-H stall → shock), unaffected here.
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
                // DIAGNOSTIC: planned frame count vs how many frames execution actually replayed before this edge
                // ended. if they differ by ~1, the landing/advance timing is off by a frame (= the ~3px = vx*1frame
                // seam drift). _execFrames is null here (consumed); _lastExecFrameCount captured it at consume time.
                if (_ssPrevStep != null && !_ssPrevStep.Pillar && !_ssPrevStep.Dig)
                {
                    var lf = _ssPrevStep.Frames[_ssPrevStep.Frames.Count - 1];
                    DiagLog.Write($"[ss-framecmp] kind={EdgeKind(_ssPrevStep)} planFrames={_ssPrevStep.Frames.Count} execFrames={_lastExecFrameCount} planLand=({lf.Px:0.##},{lf.Py:0.##}) execLand=({p.position.X:0.##},{p.position.Y:0.##}) d(px={(p.position.X - lf.Px):0.##} py={(p.position.Y - lf.Py):0.##}) planVx={lf.Vx:0.###} execVx={p.velocity.X:0.###} dVx={(p.velocity.X - lf.Vx):0.###}");
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

            if (st.Pillar)
                SkillExecutor.StartPillarJump(st.TargetCx >= ccx, st.TargetCy);
            else if (st.Dig)
            {
                int sfeet = (int)((p.position.Y + p.height) / 16f) - 1;
                MineCoordinator.Start(new MineRequest { Dir = st.DigDir, StartWx = ccx, StartWy = sfeet, TargetWx = st.TargetCx, TargetWy = st.TargetCy, MineTiles = st.MineTiles });
            }
            else if (st.Frames != null && st.Frames.Count > 0 && !st.Frames.Exists(fr => fr.Place || fr.Jump || fr.Down))
            {
                // pure WALK edge (no Place/Jump/Down) → closed-loop: press toward target X, finish on arrival (no
                // brake, vx carries on). Down is excluded because WalkTick only presses left/right — a drop-through-
                // platform edge needs Down held, which closed-loop wouldn't do, so it ran in place左右横跳. Those go
                // open-loop below (frame replay presses the recorded Down).
                _walkActive = true; _walkTargetCx = st.TargetCx; _walkDir = st.TargetCx >= ccx ? 1 : -1;
            }
            else if (st.Frames != null && st.Frames.Count > 0)
            {
                // DIAGNOSTIC: does the player's REAL start match the start this edge's frames were planned from?
                // any gap here = open-loop replay from a wrong origin → accumulates → edge-of-block plunge.
                // PHASE FIX: Frames[0] is the state AFTER executing frame 0 (jump already moved py up ~4.61), but the
                // real player here is still AT the edge start (frame 0 not yet executed). comparing them directly shows
                // a phantom one-frame gap (dPy=4.61 on every jump edge). step the real start one frame under f0's input
                // so both sides are "after frame 0" — same phase as ss-cmp. residual = the TRUE seam misalignment.
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

        // GREEDY single-step driver (route 2): maze field gives the global trend; each step we forward-sim only a
        // few candidate actions (walk/jump segments + jump-place left/right/up), score each landing cell by its
        // maze cost, and execute the single best. No search tree → no blowup. When no candidate improves (shaft:
        // jump-place blocked), fall back to one vertical pillar cycle — "low-gain → climb" emerges, not hardcoded.
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
        static int _lineIdx;       // player's tracked projection onto the line (viz only now)
        public static void ResetLineProgress() { _lineIdx = 0; _miss.Clear(); _recent.Clear(); }

        // ATTENTION mismatch memory — a CONTINUOUS per-edge weight, NOT a hard ban. An edge keyed by (fromCell→toCell):
        // when the bot's real landing falls short of an edge's optimistic simulated landing (a jump the physics couldn't
        // make, sliding into a pit), that edge accrues a penalty proportional to how far off it landed (manhattan cells,
        // same unit as g/H). The penalty is ADDED to g+H at selection, so a repeatedly-failing optimistic edge is softly
        // down-weighted and a reliable alternative (place/bridge/walk-down) wins — behavior emerges from the weight, no
        // if-else. It is NEVER ∞ and NEVER removes a candidate: if a penalized edge is still the only option it is still
        // chosen (stuck stays structurally impossible). It DECAYS every cycle (half-life ≈ a pit-fall-and-climb-back loop),
        // so memory fades — this is what keeps it from becoming a backtrack ban: a penalized edge always recovers in time.
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
        public static void ReportEdge(int fromCx, int fromCy, int planCx, int planCy, int realCx, int realCy)
        {
            var key = (fromCx, fromCy, planCx, planCy);
            int miss = System.Math.Abs(realCx - planCx) + System.Math.Abs(realCy - planCy);
            // ZERO-MOVE floor: landing back on the start cell is the most total breach of an edge's promise — the sim
            // said "this advances" and reality said "you didn't move at all" (half-brick/slope collision optimism).
            // Manhattan alone prices it at 1-2, weaker than "overshot by two cells", so a slope edge with a few points
            // of advantage got retried for cycles. Floor it so one failed try out-prices any tie-break-scale advantage.
            if (miss > 0 && realCx == fromCx && realCy == fromCy) miss = System.Math.Max(miss, NoMoveMissFloor);
            if (miss == 0) { if (_miss.ContainsKey(key)) _miss[key] *= MissForgiveHit; }
            else _miss[key] = _miss.GetValueOrDefault(key) + miss;

            // REVISIT penalty — the SAME continuous mechanism, extended to catch a shuffle that HITS every step (miss=0)
            // yet goes nowhere: a contour-line loop where each move lands exactly on its target but the target is a cell
            // we were just on. Detect it not with a stuck counter but by memory: if the real landing is one we've stood on
            // in the last few steps, the edge (from→landing) that led here accrues a penalty. Cycling the same 2-3 cells
            // keeps re-penalizing those edges until one of them out-costs the escape edge (e.g. the lower-H jump the align
            // term had been vetoing) and the bot leaves. Decays like _miss, so a legitimate re-tread later is not banned.
            var landed = (realCx, realCy);
            int recency = _recent.IndexOf(landed);
            if (recency >= 0)
            {
                var ekey = (fromCx, fromCy, realCx, realCy);
                _miss[ekey] = _miss.GetValueOrDefault(ekey) + (RevisitPenalty * (_recent.Count - recency));
            }
            _recent.Add(landed);
            if (_recent.Count > RecentLen) _recent.RemoveAt(0);
        }
        static readonly System.Collections.Generic.List<(int, int)> _recent = new();
        const int RecentLen = 6;              // how many past landings to remember for revisit detection
        const float RevisitPenalty = 12f;     // per-step penalty added to an edge that lands on a recently-visited cell (6 was too weak to outweigh typical H margins before the shock tier kicked in)

        // LOOP SHOCK — the universal escape (see PROJECT_STATE.md): when the detector sees best-H stall, every edge
        // the loop traversed gets one large decaying penalty. Loops exist because the cost structure lies somewhere;
        // making the lying edges expensive re-routes Bellman onto the next-best alternative without banning anything
        // (the penalty is finite and DecayMiss forgets it, so a legitimate re-tread later is not forbidden).
        internal static void PenalizeEdges(System.Collections.Generic.IEnumerable<(int fx, int fy, int tx, int ty)> edges, float amount)
        {
            foreach (var e in edges)
            {
                var key = (e.fx, e.fy, e.tx, e.ty);
                _miss[key] = _miss.GetValueOrDefault(key) + amount;
                DiagLog.Write($"[recede-shock] edge ({e.fx},{e.fy})→({e.tx},{e.ty}) +{amount}");
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
            // sunflower-drift evidence: the Happy! buff (+move speed, active in a 169x124-tile rect around any
            // sunflower) should already be inside the live-read maxRun/accRun — if landings drift near sunflowers,
            // either these values don't track the buff or the buff flips mid-edge. Log on change only.
            {
                string sig = $"maxRun={ph.MaxRun:0.###} accRunSpd={ph.AccRunSpeed:0.###} accRun={ph.AccRun:0.####} sunflower={Main.SceneMetrics.HasSunflower}";
                if (sig != _lastParamsSig) { _lastParamsSig = sig; DiagLog.Write($"[ss-params] {sig}"); }
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
            // 4-neighbour truth line: where does the FIELD want to descend from here, and what is physically there?
            // Dijkstra guarantees some neighbour has lower H; when no candidate reaches it, this line convicts the
            // generator that silently refused (unmineable? platform? H-missing?) without another archaeology session.
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

            // PURE BELLMAN. The field H is the value function V(s) = min cost-to-goal (Dijkstra built it with StepCost),
            // so the optimal action is the one minimizing  g(this step) + H(landing)  — exactly V(s)=cost(s→s')+V(s').
            // No line, no target, no backtrack bans, no per-terrain special-case. H (already weights dig/up/lava as
            // expensive) makes the choice route around walls/lava and prefer cheap moves on its own; loops are impossible
            // because H strictly decreases each step (Bellman optimality), so no禁退 is needed. Closed loop: every cycle
            // recomputes from the REAL position, so physics imprecision is absorbed (we never assume we reached s').
            // CRITICAL: g must be in the SAME UNIT as H (StepCost), not frames — else the sum is meaningless. So g is
            // recomputed in StepCost units: travelled cells × MoveSide + dug cells × DigSide + pillared cells × MoveUp.
            var line = MazeWand.GetPath(goalWx, goalWy);   // kept only for the viz overlay; NOT used for the decision
            var (myIdx, _) = NearestLineIdx(line, curCx, curCy, _lineIdx);
            _lineIdx = myIdx;
            // multi-scale big-direction vectors from the player's line projection (unit; (0,0) if line too short)
            var dS = LineDir(line, myIdx, ArcShort);
            var dM = LineDir(line, myIdx, ArcMid);
            var dL = LineDir(line, myIdx, ArcLong);

            (SSNode node, List<PhysicsSimulator.ControlInput> frames, float cost, bool pillar, List<(int,int)> dig)? best = null;
            float bestTotal = float.MaxValue; (int, int) bestCell = (curCx, curCy);
            var cands = new List<Cand>();
            var _candLog = new System.Text.StringBuilder();
            foreach (var (next, frames, cost, pillar, digTiles) in Expand(ctx, cur, ph, gx, gy, BuildHoldOptions(), platformTile, hasPick))
            {
                // label the landing by its SETTLED state, not the last planned frame. A jump can end 0.7px inside a
                // cell with residual vx that slides the player back over the boundary before the next replan reads the
                // position — the plan "reached" a cell no rest state occupies (the (800,937) phantom: a 3-point H drop
                // selected forever, each time settling back into the start cell → oscillation). Settling costs a few
                // sim frames; dig/pillar/place nodes are constructed at rest (vx=0, grounded) so they no-op. Terrain-
                // altering landings must NOT free-fall settle here (their tiles aren't dug/placed yet, the sim would
                // drop them through the still-open/solid world) — they're rest states by construction anyway.
                // terrain-altering edges (dig/pillar/place) describe a FUTURE world — their tiles aren't dug/placed
                // yet, so both the settle sim AND StandCell's body-fit snap would judge them against the wrong world
                // (a side-dig landing failed the fit on its still-solid tiles and got snapped back onto the CURRENT
                // cell → self-loop filter silently deleted the only descending edge → the (981,435) loop). Their nodes
                // are constructed dead-center on the intended cell, so the raw center rounding is exact — use it.
                bool alters = digTiles != null || pillar || (frames != null && frames.Exists(f => f.Place));
                var landed = alters ? next : SettleNode(next, ph);
                var (ncx, ncy) = alters ? RawCell(landed.Px, landed.Py) : StandCell(landed.Px, landed.Py);
                if (ncx == curCx && ncy == curCy) continue;   // self-loop (no real move)
                if (IsLavaCell(ncx, ncy)) continue;           // never step into lava (deadly, not drift)
                if (!field.TryGetValue((ncx, ncy), out int nH)) continue;   // off the field → can't value it
                // g = the TRUE extra cost of terrain-altering actions only; plain travel (walk/jump distance) is NOT
                // charged. Reason: the field H is built from move/dig weights but has NO concept of place/dig-from-here,
                // so a place/dig landing often sits 1-2 cells lower in H than a walk/jump landing and a manhattan-based g
                // couldn't out-price it → the bot dug/bridged for a few cells of H it could have walked to. Charging
                // travel distance also virtually-inflated far jumps (large manhattan) so cheap飞-in-place小跳 won → jitter.
                // Fix both: walk/jump g≈0 (total≈H, pure field descent, far jumps not penalized), while dig/pillar/place
                // carry their real StepCost-unit price so they're only chosen when H drops enough to be worth it.
                // GOLDEN RULE: g is the SAME cost that defined H, not a second hand-authored one. H (Dijkstra) already is
                // the per-cell cost accumulated to goal, so the ideal cost of the multi-cell action s→s' is exactly
                // H(s)−H(s'). Using that guarantees g and H share one cost function at one granularity (Bellman is then
                // exact). Hand-coded per-action costs (place=120, pillar×9, dig weights) were a SECOND, mismatched cost
                // at a different granularity — the root of the pit loops (a bridge priced 120 while H valued the same
                // float at ~26, so it always lost to a free step into the pit). With g=ΔH, total=g+H(s')≡H(s) for every
                // candidate, so H alone can't rank them — the alignment/deviation terms below break the tie, which is
                // legitimate: they ARE the "how far this action strays from the field-optimal (line)" part that H folded
                // away. clamp negative ΔH (a landing with HIGHER H) to 0 cost — the deviation term handles the penalty.
                float g = MathF.Max(0f, curH - nH);
                bool isPlace = !pillar && digTiles == null && frames != null && frames.Exists(f => f.Place);   // for kind label only
                // g=ΔH is right for choosing among reachable landings, but it dropped one true cost H can't see: altering
                // terrain (dig/place/pillar) takes real TIME standing still that moving to the same spot doesn't. So the
                // surcharge is that time itself: the edge's cost field already carries the actual frames (DigTable mining
                // frames by hardness+pick for digs, 43/2-cell cycle for pillar, jump+place frames for place), converted
                // to H units (MoveSide=3 per ~5.3-frame cell run ≈ 0.5 H/frame). Self-scaling where a constant failed
                // both ways (40 killed the only escape at (3242,299)/(801,937); 3 let every near-tie dig through): dirt
                // digs stay cheap, hard rock is routed around when a walk is close. CAPPED so a necessary dig can never
                // be starved: the cap keeps the surcharge below typical real-descent H drops, so when digging is the
                // only descending edge it still beats any H-rising shuffle.
                int altered = (digTiles?.Count ?? 0) + (pillar ? 1 : 0) + (isPlace ? 1 : 0);
                if (altered > 0) g += MathF.Min(AlterSurchargeCap, cost * DigFramesToH);
                // Bellman base score g(step)+V(landing), PLUS the attention mismatch weight for this exact edge: an edge
                // whose real landing has repeatedly fallen short of its optimistic simulated landing carries a penalty
                // (manhattan cells it missed by, decayed over cycles), softly down-weighting it so a reliable alternative
                // wins. Pure g+H already allows a transient H rise (walk/jump down into a shallow pit then climb the far
                // side); the penalty only kicks in for edges physics keeps failing to honour — the optimistic jump that
                // slides into a pit. Penalty is finite and decays, never removes the candidate (stuck stays impossible).
                float pen = _miss.GetValueOrDefault((curCx, curCy, ncx, ncy));
                // big-direction alignment: how well this step's displacement points along the multi-scale line vectors.
                // Subtracted from total (a well-aligned step is cheaper), scaled to H's unit. This is what disambiguates
                // equal-H cells: the 1680↔1682 shuffle moves perpendicular to the corridor (align≈0, no reward) while
                // the pillar/walk that actually heads up-corridor gets rewarded and wins. It also blesses a V-pit
                // downslope (transient H rise but aligned). Bounded (±~AlignScale·Σw), decays to 0 at line bends where
                // the scales disagree, never removes a candidate → stuck stays structurally impossible.
                float ddx = ncx - curCx, ddy = ncy - curCy;
                float dlen = MathF.Sqrt(ddx * ddx + ddy * ddy);
                float align = 0f;
                if (dlen >= 0.5f)
                {
                    float ux = ddx / dlen, uy = ddy / dlen;
                    align = WShort * (ux * dS.x + uy * dS.y) + WMid * (ux * dM.x + uy * dM.y) + WLong * (ux * dL.x + uy * dL.y);
                }
                // deviation penalty: how far this landing sits from the line (the field-optimal route). The line is the
                // cheapest path the field found; a landing far off it is drifting away from that route. Charged per cell
                // of distance, so a step that strays (walk down INTO a pit the line floats over) costs more the deeper it
                // strays, while a landing that hugs the line (bridge across at line height) is barely charged. Applied
                // each cycle from the real position, so a transient excursion that returns to the line (the désert V-pit,
                // already fine after the air-cost fix) nets little, but a one-way descent into a /_/ trap that can't climb
                // back accrues unboundedly → the pit edge picks the bridge instead. Uses the line-distance search.
                var (_, devDist) = NearestLineIdx(line, ncx, ncy, _lineIdx);
                // SUPER-LINEAR in distance: small strays (hugging the line, skimming a shallow V-pit) cost almost
                // nothing, but the penalty steepens fast so a landing many cells off the line (jumping into a pit wall the
                // line floats over) is heavily out-priced — the pit edge then refuses the descent. Same term pulls a bot
                // that DID fall in back out: deeper in the pit = larger distance = steeper penalty, so climbing toward the
                // line (shrinking distance) beats burrowing deeper. dist^1.5 grows past linear without dist²'s blow-up.
                float dev = DeviCost * devDist * MathF.Sqrt(devDist);
                float total = g + nH + pen - AlignScale * align + dev;
                string kind = pillar ? "pillar" : digTiles != null ? "dig"
                    : isPlace ? "place"
                    : (frames != null && frames.Exists(f => f.Jump)) ? "jump" : "walk";
                cands.Add(new Cand { Cx = ncx, Cy = ncy, H = nH, Cost = (int)g, Kind = kind, Descends = nH < curH });
                _candLog.Append($" {kind}→({ncx},{ncy})H{nH}g{g:0.#}t{total:0.#}{(nH < curH ? "↓" : "")}");
                if (total < bestTotal)
                { bestTotal = total; best = (next, frames, cost, pillar, digTiles); bestCell = (ncx, ncy); }
            }
            RecedingVis.SetDecision(curCx, curCy, curH, goalWx, goalWy, cands, best != null ? bestCell : ((int, int)?)null, best != null ? curH - bestTotal : 0f, dS, dM, dL);
            DiagLog.Write($"[recede-cands] from=({curCx},{curCy})H={curH} n={cands.Count}:{_candLog}");

            // STARVED EXPAND (Phase A: generator rejections must be visible): when no descending candidate exists —
            // the cycles where a silently-refusing generator matters — re-run Expand once with SegDiag on so every
            // walk/jump/dig null logs its reason. Convicts the missing edge in one run instead of an archaeology
            // session (the (2959,262) n=1 place-only loop: walk-west existed physically, never generated, no trace).
            if (best != null && (cands.Count <= 2 || !cands.Exists(c => c.Descends)))
            {
                SegDiag = true;
                foreach (var _ in Expand(ctx, cur, ph, gx, gy, BuildHoldOptions(), platformTile, hasPick)) { }
                SegDiag = false;
            }

            if (best == null)   // Expand yielded no edge on the field at all
            {
                DiagLog.Write($"[recede] EXPAND-EMPTY at ({curCx},{curCy}) H={curH}: no edge generated.");
                SegDiag = true;
                foreach (var _ in Expand(ctx, cur, ph, gx, gy, BuildHoldOptions(), platformTile, hasPick)) { }
                SegDiag = false;
                // SAFE ESCAPE STEP (hard rule: stuck must be structurally impossible — when nothing selects, move one
                // cell and re-select; never stop on a reachable stance). Expand goes empty in wedged stances the
                // normal generators don't model (walked 6px into a slope: every field-gated edge nulls) — but walking
                // reachability is symmetric, the body that walked in can walk back out. Accept ANY real movement,
                // field membership and H ignored (this is not progress, it is un-wedging); the next replan re-selects
                // from the new stance where the field's honest pricing (the slope is a dig now) takes over.
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
            res.Steps = EdgeToSteps(cur, b.node, b.frames, b.pillar, b.dig);
            foreach (var st in res.Steps) if (st.Frames != null) res.ExecFrames.AddRange(st.Frames);
            int landH = field.TryGetValue(pickCell, out int lh) ? lh : -1;
            DiagLog.Write($"[recede] BELLMAN ({curCx},{curCy})H={curH} -> ({pickCell.Item1},{pickCell.Item2})H={landH} total={bestTotal:0} pillar={b.pillar}");
            return res;
        }

        // (idx, manhattan-dist) of the line cell nearest (cx,cy), searched in a window around `near` (the player's
        // tracked line progress) so a self-crossing route doesn't snap to a far pass, and so the window follows the
        // player forward instead of staying pinned at the start. STRICT < (first/lowest-index minimum wins): an earlier
        // <= made ties keep the highest index, so a landing far from the whole window snapped to its far end → every
        // landing read as huge progress → shuffle in place.
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

        // Unit direction along the line from idx, advancing until the cumulative MANHATTAN arc length reaches `arc`
        // cells (not idx steps: the line walks diagonally so one idx ≈ 1-2 cells; arc length keeps the vector's reach
        // scale-constant regardless of how densely the line is sampled). Clamps to the line end (near the goal the
        // short/mid/long vectors all collapse to "toward goal", which is correct). Returns (0,0) if the line is too
        // short to move at all. This is the multi-scale "big direction" the scalar field H can't express: H says how
        // far, the vector says which way the corridor actually heads — disambiguating equal-H cells (the 1680↔1682
        // contour-line shuffle) and rewarding a transient-H-rise step that still goes the right way (V-pit downslope).
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

        // multi-scale arc lengths (cells) + their blend weights: mid is the workhorse (corridor heading), short trims
        // for near obstacles, long guards against mid-scale detours. Three dot-products cross-check: at a line bend
        // short and mid disagree (opposite sign) → the blended alignment shrinks → we fall back toward pure g+H there
        // instead of confidently shoving a wrong direction. AlignScale is deliberately SMALL: alignment is a TIE-BREAKER
        // for near-equal-H candidates (a contour shuffle where H differs by ~1-3), NOT a force that can override a clear
        // H descent. At 120 it could out-vote a landing whose H was 42 lower — vetoing the real downhill exit and pinning
        // the bot in an equal-H shuffle (the near-goal 3-cell loop). Sized so a fully-aligned step is worth only ~a dozen
        // H — enough to settle ties, never enough to beat an obviously lower-H action.
        const int ArcShort = 6, ArcMid = 20, ArcLong = 80;
        const float WShort = 0.3f, WMid = 1.0f, WLong = 0.4f, AlignScale = 18f;
        // per-cell cost of a landing's distance from the line (the field-optimal route). Charges drift away from the
        // line, so a one-way descent into a trap the line floats over loses to a line-hugging bridge. Tuned so a few
        // cells off costs little (transient excursions ok) but a deep stray (10+ cells into a pit) clearly out-prices it.
        const float DeviCost = 0.5f;   // coefficient of the super-linear (dist^1.5) line-deviation penalty — TIE-BREAKER size (must lose to a real H descent, else it vetoes a big-drop walk in favor of a one-cell dig)
        // per-altered-cell surcharge for dig/place/pillar. KEY INSIGHT: Bellman (total=ΔV+V(s')≡V(s)) only sees the
        // LANDING's value, not HOW you got there — so "walk over and fall down" and "dig straight down" to the same cell
        // tie exactly. But altering terrain is really far costlier than moving (time, destroyed blocks); V can't encode
        // that. This surcharge IS that cost-of-how. Sized so digging one cell is worth going ~a dozen cells out of the
        // way to avoid — big enough that walk+fall beats a dig to the same/near spot (kills the 60% avoidable digs), yet
        // still lost to a dig that's the ONLY descent (no walk/jump candidate, or all far higher H). Not in V (that would
        // re-introduce the two-cost mismatch) — purely a per-action tiebreak on "how".
        // terrain-alter surcharge = the action's actual frames × this (H units per frame: MoveSide=3 per ~5.3-frame
        // cell at run speed). Capped: with g=ΔH every descending edge totals exactly H(s), so an uncapped surcharge
        // on slow digs would let an H-RISING shuffle beat the only descending edge (the constant-40 stucks at
        // (3242,299) and (801,937)); the cap keeps a necessary dig affordable no matter how hard the rock.
        const float DigFramesToH = 0.5f;
        const float AlterSurchargeCap = 15f;

        // one Expand edge → its ExecStep(s). Mirrors the retrace conversion: pillar-composite (dig-up), pillar, dig,
        // or frame edge. dig-up composite splits into alternating mine/pillar sub-steps the executor can drive.
        static List<ExecStep> EdgeToSteps(SSNode from, SSNode to, List<PhysicsSimulator.ControlInput> frames, bool pillar, List<(int,int)> dig)
        {
            // dig/pillar/place `to` nodes describe the POST-alter world: StandCell's body-fit snap would judge them
            // against the still-unmodified tiles and relabel them back onto the current cell — which flipped the mine
            // direction (dig east → "digLeft to self", the second (981,435) loop). RawCell for those; `from` is real.
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
                steps.Add(new ExecStep { Pillar = false, TargetCx = tcx, TargetCy = tcy, Frames = TrimFrozenTail(frames) });
            return steps;
        }

        // The frame plan IS the position prediction for the next stretch of time — and a plan can predict garbage:
        // BridgePlace's "walk to tile center" was unreachable past a wall, so its loop pressed dir into the wall to
        // the 1200-frame fuse and the executor faithfully replayed ~9s of standing still (the y≈1010 freeze). Any
        // open-loop plan whose predicted position stops changing is dead weight BY DEFINITION — cutting the frozen
        // tail cannot alter the outcome (nothing moves in it), it only returns control to the closed loop sooner.
        // Zero false-kill risk, generator-agnostic. Short frozen tails (brake settle) are left alone.
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
            // NO BACKTRACK: never step onto a visited cell. Among UNVISITED reachable candidates, pick the lowest
            // maze cost. This forces forward progress out of local-minimum wells (a sealed pocket's low-cost floor
            // is already visited → the bot must extend sideways into new cells, even if cost rises briefly). When
            // every reachable candidate is already visited, there's genuinely nowhere new → report stuck.
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

        // Pick a SUBGOAL ~LegSubgoalCells ahead toward the final goal by walking the cached field's gradient downhill
        // from (sx,sy). Returns the cell reached (a real standable surface cell on the field). If the final goal is
        // already within range, returns it directly. This is what makes a leg "manual-single-point-nav fast": A*
        // chases a NEARBY reachable cell (found→stop in tens of expansions) instead of穷举 toward a far goal.
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

        // ===== BLOCK NAV (J): cut the cached field's gradient path into fixed ~BlockCells chunks ONCE, then run each
        // chunk as a plain single-point nav (Execute rolling=false → target==goal, box field, h precise → never the
        //子目标≠h freeze). A block queue + per-frame driver advances chunk by chunk. This is the "can't possibly hang"
        // design: every chunk is exactly the navwand case that's already fast.
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

        // rolling=false (navwand /nav, single point): plan ONCE straight to the goal with a fast box field, dispatch,
        // done. This is the original fast near-nav — NOT routed through the big cached field or the leg/subgoal loop.
        // rolling=true (J maze-nav, long route): big cached compass + subgoal legs + lookahead, for far goals.
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

        // Rolling: a partial leg just finished. If we're at the final goal, done. Otherwise plan the next leg from the
        // player's real position toward the TRUE final goal (reusing the cached compass) and dispatch it. Bail if
        // several legs in a row fail to close distance (local minimum). Returns true if a next leg was dispatched.
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

        // Dispatch a plan computed earlier (lookahead: a background Plan ran while the previous leg executed).
        // Same tail as Execute but skips planning — the caller already validated the player's real position matches
        // the plan's start cell. Sets _lastExecResult so the NEXT lookahead chains off this leg's predicted landing.
        public static void DispatchPlan(SSResult res)
        {
            StopGreedy(); StopSteps();
            _execDone = false; _execFailCode = null;
            var pStart = Main.LocalPlayer;

            // REALIGN: the plan was computed from the PREDICTED landing of the previous leg; the real player is a few
            // px off that prediction. Open-loop frame replay from the un-shifted plan would平移 the whole leg by that
            // gap (the恒定 dPy=-8 seam). Shift every frame's absolute position so the plan starts exactly where the
            // player really is. Velocity is position-invariant (unchanged); Place cell coords are tile-grid and a
            // sub-tile shift doesn't move them, so leave them.
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

        // a drift-replan re-targets the CURRENT leg's landing — the "same landing" tolerance is the label/terrain
        // quantization only: StandCell rounding (±1) plus a mid-execution dig leaving the landing up to 2 rows inside
        // yet-unmined rock (dig-up mines 2 rows per cycle → snap climbs ≤2). Beyond 2 rows it is a DIFFERENT place,
        // not the leg's landing → fail the leg (cheap: receding re-selects) rather than risk pursuing a teleported
        // goal (catastrophic: the 126-cell chasm dive). Derived bound, not margin-padded — the error asymmetry
        // (one wasted select cycle vs a dive) says keep it tight.
        const int ReplanGoalSnapCap = 2;

        static volatile SSResult _replanRes;
        static volatile bool _replanPending;
        static int _replanSeq;
        static string _replanReason;

        // Background replan: stop exec (player brakes to a brief 罚站), plan the correction off-thread, dispatch when
        // ready via PollReplan. The old plan is invalid the moment we deviate, so waiting a few frames beats freezing
        // the whole game on a synchronous Plan. Returns true so the caller's frame loop stops this tick.
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

        // Closed-loop walk driver: press toward the target column until the body's center reaches it, then finish
        // WITHOUT braking so the run speed carries into the next edge (jump needs it). Self-correcting: it aims at the
        // real target each frame, so a wrong start just means a few more/fewer steps — no whole-edge平移.
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

        public static void ApplyControls()
        {
            if (_walkActive) { WalkTick(); return; }
            if (_execFrames == null) return;
            var p = Main.LocalPlayer;
            if (p == null || !p.active) { StopExec(); return; }
            if (_execIdx >= _execFrames.Count)
            {
                float cx = p.position.X + p.width / 2f, fy = p.position.Y + p.height;
                float gx = _execGoalWx * 16f + 8f, gy = (_execGoalWy + 1) * 16f;
                DiagLog.Write($"[ss-land] goal=({_execGoalWx},{_execGoalWy}) actual_px=({cx:0.#},{fy:0.#}) dx={(cx-gx):0.#} dy={(fy-gy):0.#}");
                StopExec();
                return;
            }
            var f = _execFrames[_execIdx];
            float dxp = p.position.X - f.Px;
            float dyp = p.position.Y - f.Py;
            float drift = MathF.Sqrt(dxp * dxp + dyp * dyp);

            // FULL plan-vs-exec divergence trace: the player's state NOW reflects the controls from frame idx-1 (set
            // last tick, applied by the game this tick). so compare player-now to PREVIOUS planned frame. the first
            // frame where px/py/vx/vy diverges from plan is the偏差 source: vx diverge=accel/friction mismatch,
            // py-only diverge=stepUp/slope/halfbrick mismatch, all-after-N diverge=a missing per-frame game physics.
            if (_execIdx > 0)
            {
                var pf = _execFrames[_execIdx - 1];
                DiagLog.Write($"[ss-cmp] i={_execIdx - 1} plan(px={pf.Px:0.##} py={pf.Py:0.##} vx={pf.Vx:0.##} vy={pf.Vy:0.##} L={(pf.Left?1:0)}R={(pf.Right?1:0)}J={(pf.Jump?1:0)}) exec(px={p.position.X:0.##} py={p.position.Y:0.##} vx={p.velocity.X:0.##} vy={p.velocity.Y:0.##}) d(px={(p.position.X-pf.Px):0.##} py={(p.position.Y-pf.Py):0.##} vx={(p.velocity.X-pf.Vx):0.##} vy={(p.velocity.Y-pf.Vy):0.##})");
            }

            if (_execIdx % 15 == 0)
                DiagLog.Write($"[ss-exec] frame={_execIdx}/{_execFrames.Count} expect=({f.Px:0.#},{f.Py:0.#}) actual=({p.position.X:0.#},{p.position.Y:0.#}) drift={drift:0.#}");

            if (_replanCooldownLeft > 0) _replanCooldownLeft--;
            if (_rescueCooldownLeft > 0) _rescueCooldownLeft--;

            // ── PROPRIOCEPTION: predict one bare-player frame from last real state under last frame's input, compare
            // to the real result NOW. mismatch = my body didn't obey physics as expected (fell through / stuck / shoved
            // / wet). independent of the plan, so it works even when the planned frames are stale.
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
            // PLUNGE detection: a free fall is physically CORRECT per-frame (vy = +grav each tick), so the single-frame
            // proprio mismatch stays ~0 and can't see it. vy-gap also lags (vy must build up first). the earliest,
            // cleanest signal is POSITION: the player is well BELOW where the planned frame says it should be (real py
            // >> planned py) and still descending. that gap appears the instant the player drops off the planned arc
            // and grows monotonically.
            float belowPlan = p.position.Y - f.Py; // +ve = real player is lower than the plan
            bool falling = p.velocity.Y > RescueFallVy && belowPlan > PlungeBelowPx;
            if (mismatch > ProprioMismatchPx)
                DiagLog.Write($"[ss-proprio] mismatch={mismatch:0.#} realVy={p.velocity.Y:0.#} predVy={predVy:0.#} falling={(falling?1:0)} pos=({(int)(p.Center.X/16f)},{(int)((p.position.Y+p.height)/16f)})");
            // TELEPORT abort: a one-frame jump no physics can produce (recall/mirror/teleport, or being yanked far)
            // means this whole navigation is meaningless from the new spot — don't rescue, don't replan (replanning to
            // the old goal from spawn could be hundreds of cells away and blows up the planner). just stop dead; the
            // player re-issues a NavWand command if they still want to go somewhere.
            if (_lastReal.Valid && mismatch > TeleportPx)
            {
                DiagLog.Write($"[ss-teleport] mismatch={mismatch:0.#} → abort nav");
                _execFailCode = "cancelled";  // teleported away (recall/mirror) — this nav is meaningless now
                StopExec(); StopSteps(); DiagLog.EndRun();
                return;
            }
            // record THIS frame's real state for next frame's prediction (before any early return below)
            _lastReal = new RealState { Px = p.position.X, Py = p.position.Y, Vx = p.velocity.X, Vy = p.velocity.Y, Grounded = p.velocity.Y == 0f, Valid = true };

            // AIRBORNE SELF-RESCUE: proprioception says I'm falling faster than my input should produce = an unplanned
            // plunge (failed/missed platform). like a human slapping a platform under their feet, drop one below to
            // arrest the fall; the grounded replan below then re-plans from the saved spot.
            if (!_greedyActive && falling && _rescueCooldownLeft == 0)
            {
                int fcx = (int)((p.position.X + PhysicsSimulator.PlayerW / 2f) / 16f);
                // player is 42px (~2.6 cells); the feet sit partway into their cell and a fast fall (vy up to 10/frame)
                // would clear a platform placed in the feet cell or even one cell below within the same frame. drop it
                // TWO cells below the feet so the descent has room to actually land on top instead of phasing through.
                int feetCy = (int)((p.position.Y + PhysicsSimulator.PlayerH) / 16f);
                int fcy = feetCy + 2;
                if (CanPlaceReal(fcx, fcy))
                {
                    DiagLog.Write($"[ss-rescue] plunge belowPlan={belowPlan:0.#} realVy={p.velocity.Y:0.#} feet={feetCy} → place ({fcx},{fcy})");
                    EmitPlace(p, fcx, fcy);
                    _rescueCooldownLeft = RescueCooldown;
                    return;
                }
            }

            // STUCK (velocity deviation): the plan wanted the player moving this frame but the real body is blocked
            // (|pf.Vx| expected, |vx| ~0) — the velocity axis of deviation that position-distance checks miss. count
            // consecutive blocked frames; after StuckFrames, replan from where the player actually is. covers wall /
            // slope jams that otherwise spin replan-storms until MaxReplans.
            if (!_greedyActive && _execIdx > 0)
            {
                var spf = _execFrames[_execIdx - 1];
                bool blocked = MathF.Abs(spf.Vx) >= VelDevExpect && MathF.Abs(p.velocity.X) < VelDevReal && !(f.Place);
                _stuckFrames = blocked ? _stuckFrames + 1 : 0;
                if (_stuckFrames >= StuckFrames && _replanCooldownLeft == 0)
                {
                    DiagLog.Write($"[ss-dev] cls=stuck velDev={MathF.Abs(spf.Vx - p.velocity.X):0.#} planVx={spf.Vx:0.#} realVx={p.velocity.X:0.#} → replan");
                    _stuckFrames = 0;
                    if (Replan("stuck")) return;
                }
            }

            // replan only when grounded: airborne states aren't expansion points, so mid-jump replan can't help.
            // closed-loop drift correction (now aims at the TRUE goal + rebuilds steps, so no storm/pit). greedy
            // self-corrects per step so it skips this; edge-by-edge USES it — open-loop drift was what dropped it.
            if (!_greedyActive && drift > ReplanDriftPx && _replanCooldownLeft == 0 && p.velocity.Y == 0f)
            {
                if (Replan("drift")) return;
            }

            if (f.Place && !TilePlaced(f.PlaceCx, f.PlaceCy))
            {
                if (_placeStall == 0) DiagLog.Write($"[ss-place] frame={_execIdx} tile=({f.PlaceCx},{f.PlaceCy})");
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
                    DiagLog.Write($"[ss-place] FAILED tile=({f.PlaceCx},{f.PlaceCy}) playerCell=({fcx},{fcy}) nbrSupport={nbr} targetHasTile={Main.tile[f.PlaceCx, f.PlaceCy].HasTile} itemTime={p.itemTime} → replan");
                }
                _placeStall = 0;
                // greedy re-picks from real position next TickBlocks, so just abort this frame loop. edge-by-edge
                // replans toward the true goal (closed-loop), same as drift.
                if (_greedyActive) { StopExec(); return; }
                if (Replan("place_failed")) return;
                StopExec();
                return;
            }
            if (f.Place) { DiagLog.Write($"[ss-place] done tile=({f.PlaceCx},{f.PlaceCy})"); _placeStall = 0; }

            if (f.Left) p.controlLeft = true;
            if (f.Right) p.controlRight = true;
            if (f.Jump) p.controlJump = true;
            if (f.Down) p.controlDown = true;
            // full per-frame replay trace: every frame's intent vs reality. lets one run reveal jump edges,
            // drift, ground state, vx/vy without re-building to add more logging.
            DiagLog.Write($"[ss-frame] idx={_execIdx}/{_execFrames.Count} J={(f.Jump ? 1 : 0)} L={(f.Left ? 1 : 0)} R={(f.Right ? 1 : 0)} P={(f.Place ? 1 : 0)} cJ={(p.controlJump ? 1 : 0)} vx={p.velocity.X:0.##} vy={p.velocity.Y:0.##} gnd={(p.velocity.Y == 0f ? 1 : 0)} pos=({p.position.X:0.#},{p.position.Y:0.#}) exp=({f.Px:0.#},{f.Py:0.#}) drift={drift:0.#}");
            _prevReplayJump = f.Jump;
            _execIdx++;
            _lastExecFrameCount++;
        }

        static bool TilePlaced(int cx, int cy)
        {
            if (cx < 0 || cy < 0 || cx >= Main.maxTilesX || cy >= Main.maxTilesY) return false;
            return Main.tile[cx, cy].HasTile;
        }

        static void EmitPlace(Player p, int cx, int cy)
        {
            // the two silent-return paths made a never-materializing platform undiagnosable (the (2959,262) 16-leg
            // stall: 60 ticks/leg of TilePlaced=false with zero telemetry). Log the reason once per stall period.
            int slot = NavCoordinator.FindPlatformSlot(p);
            if (slot < 0)
            {
                if (_placeStall == 1) DiagLog.Write($"[ss-place] STALL-WHY tile=({cx},{cy}) no platform slot (out of platforms?)");
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
