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
        const int   MaxExpansions = 20000;
        const int   MaxSegFrames = 120;
        const int   HoldStep = 2;
        // weighted A*: f = g + w·h. w>1 trades a little path optimality for far fewer expansions,
        // which is what makes the deep climb plans (exp~5000) affordable.
        const float HeuristicWeight = 1.8f;

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
        static (int cx, int cy) StandCell(float px, float py)
            => ((int)((px + PhysicsSimulator.PlayerW / 2f) / 16f), (int)((py + PhysicsSimulator.PlayerH - 1f) / 16f));

        static CellKey Cell(SSNode s)
        {
            var (cx, cy) = StandCell(s.Px, s.Py);
            return new CellKey { Cx = cx, Cy = cy, G = s.Grounded };
        }

        struct Label { public float G, Vx, Vy; }

        // a candidate is dominated if some existing label reached the same cell no costlier (g) and with at
        // least as much usable speed (same-direction |vx|, and vy). dominated states can do nothing the
        // dominator can't, so they're pruned — this is what stops one cell soaking up hundreds of vx variants.
        static bool Dominated(List<Label> labels, float g, float vx, float vy)
        {
            foreach (var l in labels)
                if (l.G <= g + 0.01f && MathF.Abs(l.Vx) >= MathF.Abs(vx) - 0.01f && MathF.Sign(l.Vx) == MathF.Sign(vx)
                    && MathF.Abs(l.Vy - vy) < VxQuant)
                    return true;
            return false;
        }

        public class SSResult
        {
            public bool Found;
            public int Expansions;
            public double Millis;
            public List<(float px, float py)> Path = new();
            public List<PathSeg> Segments = new();
            public List<PhysicsSimulator.ControlInput> ExecFrames = new();
            public float BestPx, BestPy, BestDx, BestDy;
            public List<(float px, float py)> Explored = new();
            public int GoalWx, GoalWy; // goal after snapping to a standable cell
        }

        public class PathSeg
        {
            public bool IsJump;
            public int Hold;
            public int FrameCount;
            public List<(float px, float py)> Trail = new();
        }

        static int _jpNoSpot, _jpNoLand, _jpFellThrough, _jpSlidOff, _jpOk;

        public static SSResult Plan(int goalWx, int goalWy)
        {
            _jpNoSpot = _jpNoLand = _jpFellThrough = _jpSlidOff = _jpOk = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var res = new SSResult();
            var p = Main.LocalPlayer;
            if (p == null || !p.active) return res;
            var ph = PhysicsSimulator.Params.FromPlayer(p);
            var holdOptions = BuildHoldOptions();

            goalWy = SnapGoalToStandable(goalWx, goalWy);
            res.GoalWx = goalWx; res.GoalWy = goalWy;
            float goalCx = goalWx * 16f + 8f;
            float goalFeetY = (goalWy + 1) * 16f;

            int platformSlot = NavCoordinator.FindPlatformSlot(p);
            int platformTile = platformSlot >= 0 ? p.inventory[platformSlot].createTile : -1;

            var (spx, spy) = StandCell(p.position.X, p.position.Y);
            _distField = MazeWand.BuildField(goalWx, goalWy, spx, spy);

            var blocks = BuildBlockPlan(spx, spy, goalWx, goalWy, _distField);
            if (blocks != null)
            {
                var bd = new System.Text.StringBuilder();
                foreach (var b in blocks) bd.Append($" {b.Kind}({b.FromCx},{b.FromCy}->{b.ToCx},{b.ToCy})");
                DiagLog.Write($"[ss-blocks] n={blocks.Count}{bd}");
                VisualizeBlocks(blocks);
            }
            else DiagLog.Write($"[ss-blocks] null start=({spx},{spy}) inField={_distField.ContainsKey((spx, spy))} fieldSize={_distField.Count} near={string.Join(";", System.Linq.Enumerable.Take(System.Linq.Enumerable.Where(_distField.Keys, k => Math.Abs(k.Item1 - spx) <= 1), 6))}");

            var start = new SSNode
            {
                Px = p.position.X, Py = p.position.Y,
                Vx = p.velocity.X, Vy = 0f, Grounded = true,
            };

            var labels = new Dictionary<CellKey, List<Label>>();
            var came = new Dictionary<SSNode, (SSNode prev, List<PhysicsSimulator.ControlInput> frames, float g)>();
            var open = new PriorityQueue<SSNode, float>();
            labels[Cell(start)] = new List<Label> { new Label { G = 0f, Vx = start.Vx, Vy = start.Vy } };
            came[start] = (start, null, 0f);
            open.Enqueue(start, Heuristic(start, goalCx, goalFeetY, ph));

            int expansions = 0;
            SSNode goalNode = default; bool found = false;
            float bestDist = float.MaxValue;

            while (open.Count > 0 && expansions < MaxExpansions)
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
                }

                if (ReachedGoal(cur, goalCx, goalFeetY))
                {
                    found = true; goalNode = cur; break;
                }

                expansions++;
                if (res.Explored.Count < 3000) res.Explored.Add((cur.Px, cur.Py));
                foreach (var (next, frames, cost) in Expand(cur, ph, goalCx, goalFeetY, holdOptions, platformTile))
                {
                    float ng = curG + cost;
                    var ck = Cell(next);
                    if (!labels.TryGetValue(ck, out var list)) { list = new List<Label>(); labels[ck] = list; }
                    if (F_Dominance && Dominated(list, ng, next.Vx, next.Vy)) continue;
                    list.RemoveAll(l => l.G >= ng - 0.01f && MathF.Abs(l.Vx) <= MathF.Abs(next.Vx) + 0.01f && MathF.Sign(l.Vx) == MathF.Sign(next.Vx) && MathF.Abs(l.Vy - next.Vy) < VxQuant);
                    list.Add(new Label { G = ng, Vx = next.Vx, Vy = next.Vy });
                    came[next] = (cur, frames, ng);
                    open.Enqueue(next, ng + HeuristicWeight * Heuristic(next, goalCx, goalFeetY, ph));
                }
            }

            sw.Stop();
            res.Expansions = expansions;
            res.Millis = sw.Elapsed.TotalMilliseconds;
            res.Found = found;
            if (!found)
                DumpTerrain(start, goalWx, goalWy, res.Explored);
            if (found)
            {
                var k = goalNode;
                var revPts = new List<(float, float)>();
                var revSegs = new List<PathSeg>();
                var revFrameLists = new List<List<PhysicsSimulator.ControlInput>>();
                while (came.TryGetValue(k, out var e) && !e.prev.Equals(k))
                {
                    revPts.Add((k.Px, k.Py));
                    if (e.frames != null)
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
                        revFrameLists.Add(e.frames);
                    }
                    k = e.prev;
                }
                revPts.Reverse();
                revSegs.Reverse();
                revFrameLists.Reverse();
                res.Path = revPts;
                res.Segments = revSegs;
                foreach (var fl in revFrameLists) res.ExecFrames.AddRange(fl);

                var segDesc = new System.Text.StringBuilder();
                foreach (var sg in revSegs)
                    segDesc.Append(sg.IsJump ? $" jump(h{sg.Hold},{sg.FrameCount}f)" : $" walk({sg.FrameCount}f)");
                DiagLog.Write($"[ss-path] segs={revSegs.Count}{segDesc}");
            }
            DiagLog.Write($"[ss-jptally] ok={_jpOk} noSpot={_jpNoSpot} noLand={_jpNoLand} fellThrough={_jpFellThrough} slidOff={_jpSlidOff}");
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

        static IEnumerable<(SSNode next, List<PhysicsSimulator.ControlInput> frames, float cost)> Expand(
            SSNode cur, PhysicsSimulator.Params ph, float goalCx, float goalFeetY, int[] holdOptions, int platformTile)
        {
            if (!cur.Grounded) yield break;

            float curH = Heuristic(cur, goalCx, goalFeetY, ph);

            // First emit all plain walk/jump edges, tracking whether ANY of them meaningfully reduces the
            // (vertical-aware) heuristic. Horizontal shuffling toward a wall lowers x-distance but not h once
            // blocked; only real progress counts. Placement is expensive, so only build when walk/jump is stuck.
            bool anyProgress = false;
            int dirToGoal = goalCx >= cur.Px ? 1 : -1;
            foreach (int dir in new[] { dirToGoal, -dirToGoal })
            {
                foreach (int hold in holdOptions)
                {
                    var seg = SimulateSegment(cur, dir, hold, ph);
                    if (!seg.HasValue) continue;
                    if (Heuristic(seg.Value.node, goalCx, goalFeetY, ph) < curH - HProgressEps) anyProgress = true;
                    yield return (seg.Value.node, seg.Value.frames, seg.Value.frames.Count);
                }
            }

            if (platformTile < 0 || (F_Gate && anyProgress)) yield break;

            // stuck: plain movement can't get closer. Build with platforms.
            foreach (int dir in new[] { dirToGoal, -dirToGoal })
                foreach (int hold in holdOptions)
                {
                    var jp = JumpPlace(cur, dir, hold, ph, platformTile);
                    if (jp.HasValue)
                        yield return (jp.Value.node, jp.Value.frames, jp.Value.frames.Count + JumpPlaceCost);
                }

            // vertical jump-place (dir=0): stack straight up, no horizontal drift — the destination dictates
            // the move. Vx≈0 means the player lands back on the tile it placed (no fall-through/slide-off).
            if (MathF.Abs(cur.Vx) < VerticalJumpVxMax)
                foreach (int hold in holdOptions)
                {
                    var jp = JumpPlace(cur, 0, hold, ph, platformTile);
                    if (jp.HasValue)
                        yield return (jp.Value.node, jp.Value.frames, jp.Value.frames.Count + JumpPlaceCost);
                }

            // 3-1 bridge: place one platform on the support row, step onto it, brake to a stop. one tile each way.
            foreach (int dir in new[] { dirToGoal, -dirToGoal })
            {
                var br = BridgePlace(cur, dir, ph, platformTile);
                if (br.HasValue)
                    yield return (br.Value.node, br.Value.frames, br.Value.frames.Count + BridgeCost);
            }
        }

        const float VerticalJumpVxMax = 0.5f;
        const float HProgressEps = 1.5f;

        // temporary bisection switches: flip one off, rebuild, see which filter was killing valid plans
        const bool F_Gate = true;        // anyProgress gating of placement
        const bool F_Dominance = true;   // velocity dominance pruning
        const bool F_Brake = false;      // reject jump-place when brake can't settle
        const bool F_LandOnPlat = true;  // reject jump-place when not landing on the placed tile
        const bool F_DescentOnly = true; // place only during descent (vy>0)
        const bool F_Trend = true;       // two-phase up-then-left heuristic bias

        const float JumpPlaceCost = 30f; // bias: prefer plain walk/jump; place only when it opens a path
        const float BridgeCost = 30f;    // same as jump-place: consumes a platform, use only to open a path

        // "Jump and place one platform": jump (hold), scan the arc for the FIRST frame where the foot cell is
        // empty + adjacent to real support (cliff/wall), place a platform there, and land on it. One placement
        // per jump, placed tile is NOT stored in the node (node stays pure physics) — the landing node simply
        // stands on the new platform's top, supported by real terrain. Covers "hug a wall/block and jump-place
        // upward". Pure open-air pillaring (placement supported only by prior placements) is left to the macro.
        static (SSNode node, List<PhysicsSimulator.ControlInput> frames)? JumpPlace(
            SSNode cur, int dir, int hold, PhysicsSimulator.Params ph, int platformTile)
        {
            if (hold == 0) return null; // need to leave the ground

            // simulate the free arc to find where to place
            var s = new PhysicsSimulator.State
            {
                Px = cur.Px, Py = cur.Py, Vx = cur.Vx, Vy = cur.Vy,
                Grounded = true, JumpFramesLeft = hold,
            };
            int placeCx = int.MinValue, placeCy = 0;
            float probeVy = 0f, probeFootPy = 0f;
            for (int f = 0; f < MaxSegFrames; f++)
            {
                var input = new PhysicsSimulator.ControlInput { Right = dir > 0, Left = dir < 0, Jump = f < hold };
                s = PhysicsSimulator.Step(s, input, ph);
                if (F_DescentOnly ? (s.Vy <= 0f) : (f < hold)) continue;
                int fcx = (int)((s.Px + PhysicsSimulator.PlayerW / 2f) / 16f);
                int fcy = (int)((s.Py + PhysicsSimulator.PlayerH + 1f) / 16f); // foot cell
                if (CanPlaceReal(fcx, fcy)) { placeCx = fcx; placeCy = fcy; probeVy = s.Vy; probeFootPy = s.Py + PhysicsSimulator.PlayerH; break; }
            }
            if (placeCx == int.MinValue) { _jpNoSpot++; return null; }

            // re-simulate with the platform present so native collision lands the player on it
            var seg = SimulateWithPlatform(cur, dir, hold, ph, placeCx, placeCy, platformTile);
            if (!seg.HasValue || !seg.Value.node.Grounded) { _jpNoLand++; return null; }
            // must actually land ON the placed platform — otherwise the player passed through it and landed
            // elsewhere (often back on the ground). Such "place but fall through" edges are useless and, when
            // admitted, flood the search with cheap no-op placements (exp blowup). Reject them.
            int landFeetCy = (int)((seg.Value.node.Py + PhysicsSimulator.PlayerH) / 16f);
            if (F_LandOnPlat && landFeetCy != placeCy)
            {
                if (_jpFellThrough < 12)
                    DiagLog.Write($"[ss-ft] place=({placeCx},{placeCy}) hold={hold} dir={dir} probeVy={probeVy:0.#} probeFootPy={probeFootPy:0.#} platTopPy={placeCy * 16} landFeetCy={landFeetCy}");
                _jpFellThrough++; return null;
            }
            MarkPlaceFrame(seg.Value.frames, placeCx, placeCy);

            // Landing on a 1-tile platform with residual Vx slides the player off next frame. Append a brake:
            // counter-press to decelerate; the landing node takes the actual settled position. Only a slide
            // that loses ground contact (falls off) invalidates the edge.
            if (F_Brake)
            {
                var braked = AppendBrake(seg.Value.node, seg.Value.frames, ph);
                if (braked == null) { _jpSlidOff++; return null; }
                _jpOk++;
                return braked.Value;
            }
            _jpOk++;
            return seg.Value;
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
                    if (!s.Grounded) return null;              // slid off the single tile and fell — invalid
                    if (past && MathF.Abs(s.Vx) < 0.1f) break; // settled on the new tile
                }
                if (frames.Count == 0) return null;
                var (lcx, lcy) = StandCell(s.Px, s.Py);
                if (lcx != placeCx) return null;               // didn't end standing on the new tile
                var f0 = frames[0];                            // place on the first frame (before stepping over)
                f0.Place = true; f0.PlaceCx = placeCx; f0.PlaceCy = placeCy;
                frames[0] = f0;
                var node = new SSNode { Px = s.Px, Py = s.Py, Vx = s.Vx, Vy = s.Vy, Grounded = true };
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
            t.HasTile = true; t.TileType = (ushort)platformTile; t.IsHalfBlock = false; t.Slope = Terraria.ID.SlopeType.Solid;
            try { return SimulateSegment(cur, dir, hold, ph); }
            finally { t.HasTile = oHad; t.TileType = oType; t.IsHalfBlock = oHalf; t.Slope = oSlope; }
        }

        // Tag the apex frame for placement: at the apex the player is near-motionless, so stalling there to
        // await UseItem won't drop the player past the platform; the descent then lands cleanly on it.
        static void MarkPlaceFrame(List<PhysicsSimulator.ControlInput> frames, int cx, int cy)
        {
            int idx = 0;
            float minPy = float.MaxValue;
            for (int i = 0; i < frames.Count; i++)
                if (frames[i].Py < minPy) { minPy = frames[i].Py; idx = i; }
            var fr = frames[idx];
            fr.Place = true; fr.PlaceCx = cx; fr.PlaceCy = cy;
            frames[idx] = fr;
        }

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
                input.Px = s.Px; input.Py = s.Py;
                frames.Add(input);
                if (!s.Grounded) everAirborne = true;
                if (s.Grounded && everAirborne)
                    break;
                if (s.Grounded && hold == 0)
                {
                    if (MathF.Abs(s.Px - startPx) >= 24f) break;
                    if (MathF.Abs(s.Px - prevPx) < 0.05f && f >= 2) break; // wall: not advancing
                }
            }
            if (frames.Count == 0) return null;
            var node = new SSNode { Px = s.Px, Py = s.Py, Vx = s.Vx, Vy = s.Vy, Grounded = s.Grounded };
            if (MathF.Abs(node.Px - cur.Px) < 1f && MathF.Abs(node.Py - cur.Py) < 1f) return null; // no self-loops
            return (node, frames);
        }

        // A goal cell the player can't stand on (floating, no floor underneath) is unreachable, and the search
        // burns its whole budget trying. Drop the goal down to the first standable cell so any click resolves
        // to "go near there" instead of hanging on an impossible target.
        const int GoalSnapMaxDrop = 40;
        static int SnapGoalToStandable(int gx, int gy)
        {
            for (int d = 0; d <= GoalSnapMaxDrop; d++)
            {
                int y = gy + d;
                if (PathPlanner.IsFloorPublic(gx, y + 1) && !PathPlanner.IsBlockPublic(gx, y))
                {
                    if (d > 0) DiagLog.Write($"[ss-snap] goal ({gx},{gy}) floating → ({gx},{y}) drop={d}");
                    return y;
                }
            }
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

        static Dictionary<(int, int), int> _distField;

        static float Heuristic(SSNode s, float goalCx, float goalFeetY, PhysicsSimulator.Params ph)
        {
            if (_distField != null)
            {
                var (cx, cy) = StandCell(s.Px, s.Py);
                if (_distField.TryGetValue((cx, cy), out int dstep))
                    return dstep * DistStepCost;
            }
            float ccx = s.Px + PhysicsSimulator.PlayerW / 2f;
            float feetY = s.Py + PhysicsSimulator.PlayerH;
            float dx = MathF.Abs(ccx - goalCx);
            float dy = MathF.Abs(feetY - goalFeetY);
            float dyW = (F_Trend && dy > TrendNearDyTiles * 16f) ? TrendClimbDyWeight : (1f / 5f);
            return dx / MathF.Max(ph.MaxRun, 0.1f) + dy * dyW;
        }

        const int CoarseJumpUp = 6;    // tiles a jump can clear
        const int CoarseJumpSpan = 8;  // horizontal reach of a jump

        static bool CoarseStand(int cx, int cy)
        {
            if (cx < 0 || cy < 0 || cx >= Main.maxTilesX || cy + 1 >= Main.maxTilesY) return false;
            if (PathPlanner.IsBlockPublic(cx, cy)) return false;
            return PathPlanner.IsFloorPublic(cx, cy + 1);
        }

        public enum BlockKind { Walk, PillarUp, DropDown, JumpAcross }

        public struct Block
        {
            public BlockKind Kind;
            public int FromCx, FromCy, ToCx, ToCy;
        }

        // Macro layer: descend the distance field from start to goal, then merge the cell path into a short
        // sequence of action blocks (walk runs, pillar climbs, drops, gap jumps). The bot decides "what to do
        // roughly where" here — cheap, grid-level — and leaves precise frames to the micro (state-space) layer.
        public static List<Block> BuildBlockPlan(int sx, int sy, int gx, int gy, Dictionary<(int, int), int> dist)
        {
            var path = new List<(int x, int y)>();
            var cur = (sx, sy);
            if (!dist.ContainsKey(cur)) return null;
            path.Add(cur);
            var seen = new HashSet<(int, int)> { cur };
            for (int step = 0; step < 400 && cur != (gx, gy); step++)
            {
                int bestD = dist[cur]; (int, int) best = cur;
                for (int dx = -CoarseJumpSpan; dx <= CoarseJumpSpan; dx++)
                    for (int dy = -CoarseJumpUp; dy <= CoarseJumpUp; dy++)
                    {
                        var n = (cur.Item1 + dx, cur.Item2 + dy);
                        if (seen.Contains(n)) continue;
                        if (dist.TryGetValue(n, out int dn) && dn < bestD) { bestD = dn; best = n; }
                    }
                if (best == cur) break;
                cur = best; seen.Add(cur); path.Add(cur);
            }

            var blocks = new List<Block>();
            int i = 0;
            while (i < path.Count - 1)
            {
                var (x0, y0) = path[i];
                var (x1, y1) = path[i + 1];
                int dyStep = y1 - y0;
                BlockKind kind;
                if (dyStep <= -2) kind = BlockKind.PillarUp;
                else if (dyStep >= 3) kind = BlockKind.DropDown;
                else if (Math.Abs(x1 - x0) >= 3 && dyStep > -2 && dyStep < 3 && !CoarseStand((x0 + x1) / 2, Math.Max(y0, y1))) kind = BlockKind.JumpAcross;
                else kind = BlockKind.Walk;

                int j = i + 1;
                while (j < path.Count - 1)
                {
                    var (ax, ay) = path[j];
                    var (bx, by) = path[j + 1];
                    int d2 = by - ay;
                    BlockKind k2 = d2 <= -2 ? BlockKind.PillarUp : (d2 >= 3 ? BlockKind.DropDown : BlockKind.Walk);
                    if (k2 != kind && !(kind == BlockKind.JumpAcross && k2 == BlockKind.Walk)) break;
                    j++;
                }
                blocks.Add(new Block { Kind = kind, FromCx = x0, FromCy = y0, ToCx = path[j].x, ToCy = path[j].y });
                i = j;
            }
            return blocks;
        }

        static void VisualizeBlocks(List<Block> blocks)
        {
            var tiles = new List<(int, int, Microsoft.Xna.Framework.Color)>();
            var labels = new List<(int, int, string, Microsoft.Xna.Framework.Color)>();
            foreach (var b in blocks)
            {
                var col = b.Kind switch
                {
                    BlockKind.Walk => new Microsoft.Xna.Framework.Color(255, 230, 0, 120),
                    BlockKind.PillarUp => new Microsoft.Xna.Framework.Color(180, 120, 255, 140),
                    BlockKind.DropDown => new Microsoft.Xna.Framework.Color(0, 200, 255, 120),
                    _ => new Microsoft.Xna.Framework.Color(255, 100, 0, 140),
                };
                int steps = Math.Max(Math.Abs(b.ToCx - b.FromCx), Math.Abs(b.ToCy - b.FromCy));
                for (int s = 0; s <= steps; s++)
                {
                    int x = steps == 0 ? b.FromCx : b.FromCx + (b.ToCx - b.FromCx) * s / steps;
                    int y = steps == 0 ? b.FromCy : b.FromCy + (b.ToCy - b.FromCy) * s / steps;
                    tiles.Add((x, y, col));
                }
                labels.Add((b.FromCx, b.FromCy, b.Kind.ToString().Substring(0, 1), Microsoft.Xna.Framework.Color.White));
            }
            PathVisSystem.SetTiles(tiles, 1200);
            PathVisSystem.SetLabels(labels, 1200);
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
            float goalPx = res.GoalWx * 16f + 8f;
            float goalPy = (res.GoalWy + 1) * 16f;
            PathVisSystem.SetSSPath(trail, explored, goalPx, goalPy, placed, ttlFrames: 1200);
        }

        // ── execution ──
        static List<PhysicsSimulator.ControlInput> _execFrames;
        static int _execIdx;
        static int _execGoalWx, _execGoalWy;
        const float ReplanDriftPx = 24f;
        const int ReplanCooldown = 10;
        static int _replanCooldownLeft;
        static int _replanCount;
        const int MaxReplans = 40;
        static int _placeStall;
        const int PlaceStallMax = 60;

        public static bool IsActive => _execFrames != null && _execIdx < _execFrames.Count;

        public static void StopExec() { _execFrames = null; _execIdx = 0; }

        static List<Block> PlanBlocks(int goalWx, int goalWy)
        {
            var p = Main.LocalPlayer;
            if (p == null || !p.active) return null;
            goalWy = SnapGoalToStandable(goalWx, goalWy);
            var (spx, spy) = StandCell(p.position.X, p.position.Y);
            _distField = MazeWand.BuildField(goalWx, goalWy, spx, spy);
            return BuildBlockPlan(spx, spy, goalWx, goalWy, _distField);
        }

        public static void VisualizeBlockPlan(int goalWx, int goalWy)
        {
            var blocks = PlanBlocks(goalWx, goalWy);
            if (blocks == null || blocks.Count == 0) { DiagLog.Write("[ss-visblk] no blocks"); return; }
            var bd = new System.Text.StringBuilder();
            foreach (var b in blocks) bd.Append($" {b.Kind}({b.FromCx},{b.FromCy}->{b.ToCx},{b.ToCy})");
            DiagLog.Write($"[ss-visblk] n={blocks.Count}{bd}");
            VisualizeBlocks(blocks);
        }

        // GREEDY single-step driver (route 2): maze field gives the global trend; each step we forward-sim only a
        // few candidate actions (walk/jump segments + jump-place left/right/up), score each landing cell by its
        // maze cost, and execute the single best. No search tree → no blowup. When no candidate improves (shaft:
        // jump-place blocked), fall back to one vertical pillar cycle — "low-gain → climb" emerges, not hardcoded.
        static bool _greedyActive;
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
            _distField = MazeWand.BuildField(goalWx, goalWy, spx, spy);
            if (!_distField.ContainsKey((spx, spy))) { DiagLog.Write($"[ss-greedy] start ({spx},{spy}) not in field"); return; }
            _greedyActive = true; _greedyGoalWx = goalWx; _greedyGoalWy = goalWy;
            _greedyTrail.Clear(); _greedyVisited.Clear();
            DiagLog.Write($"[ss-greedy] start=({spx},{spy}) goal=({goalWx},{goalWy}) field={_distField.Count}");
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
                "jumpplace" => JumpPlace(cur, dir, BuildHoldOptions()[^1], ph, platformTile),
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

        // chosen each time both executors are idle. Picks the candidate whose landing cell has the lowest maze cost.
        public static void TickBlocks()
        {
            RunPendingTest();
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
            int curCost = _distField.TryGetValue((curCx, curCy), out int cc) ? cc : int.MaxValue;

            _greedyVisited.Add((curCx, curCy));

            _jpNoSpot = _jpNoLand = _jpFellThrough = _jpSlidOff = _jpOk = 0;
            // NO BACKTRACK: never step onto a visited cell. Among UNVISITED reachable candidates, pick the lowest
            // maze cost. This forces forward progress out of local-minimum wells (a sealed pocket's low-cost floor
            // is already visited → the bot must extend sideways into new cells, even if cost rises briefly). When
            // every reachable candidate is already visited, there's genuinely nowhere new → report stuck.
            List<PhysicsSimulator.ControlInput> chosen = null;
            int chosenCost = int.MaxValue, chosenFC = int.MaxValue;
            var cand = new System.Text.StringBuilder();
            int candN = 0;
            foreach (var (next, frames, _) in Expand(cur, ph, gx, gy, BuildHoldOptions(), platformTile))
            {
                var (ncx, ncy) = StandCell(next.Px, next.Py);
                bool inField = _distField.TryGetValue((ncx, ncy), out int ncost);
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

        public static SSResult Execute(int goalWx, int goalWy)
        {
            var res = Plan(goalWx, goalWy);
            Visualize(res, goalWx, goalWy);
            DiagLog.Write($"[ss-plan] target=({goalWx},{goalWy}) found={res.Found} exp={res.Expansions} ms={res.Millis:0.#} frames={res.ExecFrames.Count} best_dx={res.BestDx:0.#} best_dy={res.BestDy:0.#}");
            if (!res.Found || res.ExecFrames.Count == 0) { StopExec(); return res; }
            _execFrames = res.ExecFrames;
            _execIdx = 0;
            _execGoalWx = res.GoalWx; _execGoalWy = res.GoalWy;
            _replanCooldownLeft = 0;
            _replanCount = 0;
            _placeStall = 0;
            return res;
        }

        static bool Replan(string reason)
        {
            if (_replanCount >= MaxReplans) { DiagLog.Write("[ss-replan] max replans hit → stop"); return false; }
            _replanCount++;
            var res = Plan(_execGoalWx, _execGoalWy);
            Visualize(res, _execGoalWx, _execGoalWy);
            var rp = Main.LocalPlayer;
            DiagLog.Write($"[ss-replan] reason={reason} #{_replanCount} from=({(int)((rp.position.X+10)/16f)},{(int)((rp.position.Y+42)/16f)}) found={res.Found} exp={res.Expansions} ms={res.Millis:0.#} bestdy={res.BestDy:0.#} frames={res.ExecFrames.Count}");
            if (!res.Found || res.ExecFrames.Count == 0) return false;
            _execFrames = res.ExecFrames;
            _execIdx = 0;
            _replanCooldownLeft = ReplanCooldown;
            _placeStall = 0;
            return true;
        }

        public static void ApplyControls()
        {
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

            if (_execIdx % 15 == 0)
                DiagLog.Write($"[ss-exec] frame={_execIdx}/{_execFrames.Count} expect=({f.Px:0.#},{f.Py:0.#}) actual=({p.position.X:0.#},{p.position.Y:0.#}) drift={drift:0.#}");

            if (_replanCooldownLeft > 0) _replanCooldownLeft--;

            // replan only when grounded: airborne states aren't expansion points, so mid-jump replan can't help.
            // greedy owns its own per-step re-decision (next TickBlocks re-picks from the real position), so the
            // physics-A* replan must NOT fire under greedy — it would hijack the frame loop with failing searches.
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
                // greedy owns re-decision: abort this step, TickBlocks re-picks from the real position next frame.
                // the physics-A* Replan must not fire here — it would inject a long open-loop path that drifts.
                if (_greedyActive) { StopExec(); return; }
                if (Replan("place_failed")) return;
                StopExec();
                return;
            }
            if (f.Place) { DiagLog.Write($"[ss-place] done tile=({f.PlaceCx},{f.PlaceCy})"); _placeStall = 0; }

            if (f.Left) p.controlLeft = true;
            if (f.Right) p.controlRight = true;
            if (f.Jump) p.controlJump = true;
            // full per-frame replay trace: every frame's intent vs reality. lets one run reveal jump edges,
            // drift, ground state, vx/vy without re-building to add more logging.
            DiagLog.Write($"[ss-frame] idx={_execIdx}/{_execFrames.Count} J={(f.Jump ? 1 : 0)} L={(f.Left ? 1 : 0)} R={(f.Right ? 1 : 0)} P={(f.Place ? 1 : 0)} cJ={(p.controlJump ? 1 : 0)} vx={p.velocity.X:0.##} vy={p.velocity.Y:0.##} gnd={(p.velocity.Y == 0f ? 1 : 0)} pos=({p.position.X:0.#},{p.position.Y:0.#}) exp=({f.Px:0.#},{f.Py:0.#}) drift={drift:0.#}");
            _prevReplayJump = f.Jump;
            _execIdx++;
        }

        static bool TilePlaced(int cx, int cy)
        {
            if (cx < 0 || cy < 0 || cx >= Main.maxTilesX || cy >= Main.maxTilesY) return false;
            return Main.tile[cx, cy].HasTile;
        }

        static void EmitPlace(Player p, int cx, int cy)
        {
            int slot = NavCoordinator.FindPlatformSlot(p);
            if (slot < 0) return;
            p.selectedItem = slot;
            Main.SmartCursorWanted_Mouse = false; // SmartCursor would retarget the cursor away from PlaceCx/Cy
            if (p.itemTime > 0) return; // mid-swing; wait for cooldown before re-firing
            Main.mouseX = (int)(cx * 16f + 8f - Main.screenPosition.X);
            Main.mouseY = (int)(cy * 16f + 8f - Main.screenPosition.Y);
            p.controlUseItem = true;
        }
    }
}
