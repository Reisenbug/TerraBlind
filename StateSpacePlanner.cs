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

        static CellKey Cell(SSNode s) => new CellKey
        {
            Cx = (int)((s.Px + PhysicsSimulator.PlayerW / 2f) / 16f),
            Cy = (int)((s.Py + PhysicsSimulator.PlayerH) / 16f),
            G = s.Grounded,
        };

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

            int spx = (int)((p.position.X + PhysicsSimulator.PlayerW / 2f) / 16f);
            int spy = (int)((p.position.Y + PhysicsSimulator.PlayerH) / 16f);
            _distField = BuildDistField(goalWx, goalWy, spx, spy, platformTile >= 0);

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
                int sx = (int)((start.Px + PhysicsSimulator.PlayerW / 2f) / 16f);
                int sy = (int)((start.Py + PhysicsSimulator.PlayerH) / 16f);
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
            int sx = (int)((start.Px + PhysicsSimulator.PlayerW / 2f) / 16f);
            int sy = (int)((start.Py + PhysicsSimulator.PlayerH) / 16f);
            int minX = Math.Min(sx, goalWx) - 6, maxX = Math.Max(sx, goalWx) + 6;
            int minY = Math.Min(sy, goalWy) - 4, maxY = Math.Max(sy, goalWy) + 4;
            if (maxX - minX > 80) maxX = minX + 80;
            if (maxY - minY > 40) maxY = minY + 40;

            var exp = new HashSet<(int, int)>();
            foreach (var (px, py) in explored)
                exp.Add(((int)((px + PhysicsSimulator.PlayerW / 2f) / 16f), (int)((py + PhysicsSimulator.PlayerH) / 16f)));

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
                int cx = (int)((s.Px + PhysicsSimulator.PlayerW / 2f) / 16f);
                int cy = (int)((s.Py + PhysicsSimulator.PlayerH) / 16f);
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

        // Reverse BFS from goal over standable cells; edges = walk/step, jump-up, fall, and (if has platforms)
        // build-up to an empty cell above. Gives every cell a coarse step-distance to the goal that the
        // state-space heuristic follows downhill — steering it around walls and toward climbs.
        static Dictionary<(int, int), int> BuildDistField(int gx, int gy, int sx, int sy, bool canBuild)
        {
            int minX = Math.Min(gx, sx) - 12, maxX = Math.Max(gx, sx) + 12;
            int minY = Math.Min(gy, sy) - 30, maxY = Math.Max(gy, sy) + 12;

            var dist = new Dictionary<(int, int), int>();
            var q = new Queue<(int, int)>();
            dist[(gx, gy)] = 0;
            q.Enqueue((gx, gy));

            while (q.Count > 0)
            {
                var (cx, cy) = q.Dequeue();
                int nd = dist[(cx, cy)] + 1;

                void Try(int nx, int ny)
                {
                    if (nx < minX || nx > maxX || ny < minY || ny > maxY) return;
                    if (dist.ContainsKey((nx, ny))) return;
                    if (!CoarseStand(nx, ny)) return;
                    dist[(nx, ny)] = nd;
                    q.Enqueue((nx, ny));
                }

                // reverse edges: a neighbor that could REACH (cx,cy) forward. Symmetric-enough for coarse use.
                for (int dx = -CoarseJumpSpan; dx <= CoarseJumpSpan; dx++)
                {
                    if (dx == 0) continue;
                    for (int dy = -CoarseJumpUp; dy <= CoarseJumpUp; dy++)
                        Try(cx + dx, cy + dy);
                }
                Try(cx, cy - 1); Try(cx, cy + 1);

                if (canBuild)
                    for (int dy = 1; dy <= CoarseJumpUp; dy++)
                        Try(cx, cy + dy); // could pillar up from below
            }
            return dist;
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

            // replan only when grounded: airborne states aren't expansion points, so mid-jump replan can't help
            if (drift > ReplanDriftPx && _replanCooldownLeft == 0 && p.velocity.Y == 0f)
            {
                if (Replan("drift")) return;
            }

            if (f.Place && !TilePlaced(f.PlaceCx, f.PlaceCy))
            {
                if (_placeStall == 0) DiagLog.Write($"[ss-place] frame={_execIdx} tile=({f.PlaceCx},{f.PlaceCy})");
                _placeStall++;
                if (_placeStall <= PlaceStallMax)
                {
                    if (f.Left) p.controlLeft = true;
                    if (f.Right) p.controlRight = true;
                    if (f.Jump) p.controlJump = true;
                    EmitPlace(p, f.PlaceCx, f.PlaceCy);
                    return; // stall here until the platform exists
                }
                DiagLog.Write($"[ss-place] FAILED tile=({f.PlaceCx},{f.PlaceCy}) after {PlaceStallMax}f → replan");
                _placeStall = 0;
                if (Replan("place_failed")) return;
                StopExec();
                return;
            }
            if (f.Place) { DiagLog.Write($"[ss-place] done tile=({f.PlaceCx},{f.PlaceCy})"); _placeStall = 0; }

            if (f.Left) p.controlLeft = true;
            if (f.Right) p.controlRight = true;
            if (f.Jump) p.controlJump = true;
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
