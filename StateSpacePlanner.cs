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
        // NOTE: do NOT collapse grounded-cell vx variants to "cheapest only" — the residual landing vx is needed
        // to chain continuous diagonal slides down a sloped seam (each step rides the prior step's velocity).
        // tried it; it broke otherwise-solvable descents by severing that chain.
        static bool Dominated(List<Label> labels, float g, float vx, float vy)
        {
            foreach (var l in labels)
            {
                if (l.G <= g + 0.01f && MathF.Abs(l.Vx) >= MathF.Abs(vx) - 0.01f && MathF.Sign(l.Vx) == MathF.Sign(vx)
                    && MathF.Abs(l.Vy - vy) < VxQuant)
                    return true;
            }
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
            public List<ExecStep> Steps = new(); // ordered edges for edge-by-edge execution (frame replay or pillar macro)
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
            bool hasPickaxe = false;
            for (int i = 0; i < 10; i++) { var it = p.inventory[i]; if (it != null && !it.IsAir && it.pick > 0) { hasPickaxe = true; break; } }

            var (spx, spy) = StandCell(p.position.X, p.position.Y);
            // distance fuse: BuildField is a reverse-Dijkstra over a (|dx|+240)×(|dy|+240) window; a goal hundreds of
            // cells away (e.g. after a teleport leaves the old goal across the map) makes it explode and hang. refuse
            // to even start — return empty so the caller aborts instead of freezing.
            if (System.Math.Abs(spx - goalWx) > MaxPlanSpanCells || System.Math.Abs(spy - goalWy) > MaxPlanSpanCells)
            {
                DiagLog.Write($"[ss-toofar] start=({spx},{spy}) goal=({goalWx},{goalWy}) span>{MaxPlanSpanCells} → abort");
                return res;
            }
            _distField = MazeWand.BuildField(goalWx, goalWy, spx, spy);
            _blockH = null;

            // DIAGNOSTIC: dump the maze-field H up the start column to answer "why does A* go DOWN into the pit". if a
            // lower cell has LOWER H than a higher one, the field itself rewards descending. x = cell not in field.
            {
                var hb = new System.Text.StringBuilder($"[ss-mazeH] start=({spx},{spy}) goal=({goalWx},{goalWy}) col={spx} (y:H):");
                for (int yy = spy + 4; yy >= spy - 30; yy--)
                    hb.Append(_distField.TryGetValue((spx, yy), out int d) ? $" {yy}:{d}" : $" {yy}:x");
                DiagLog.Write(hb.ToString());
            }

            var start = new SSNode
            {
                Px = p.position.X, Py = p.position.Y,
                Vx = p.velocity.X, Vy = 0f, Grounded = true,
            };

            var labels = new Dictionary<CellKey, List<Label>>();
            var came = new Dictionary<SSNode, (SSNode prev, List<PhysicsSimulator.ControlInput> frames, float g, bool pillar, List<(int, int)> digTiles)>();
            var open = new PriorityQueue<SSNode, float>();
            labels[Cell(start)] = new List<Label> { new Label { G = 0f, Vx = start.Vx, Vy = start.Vy } };
            came[start] = (start, null, 0f, false, null);
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
                foreach (var (next, frames, cost, pillar, digTiles) in Expand(cur, ph, goalCx, goalFeetY, holdOptions, platformTile, hasPickaxe))
                {
                    float ng = curG + cost;
                    var ck = Cell(next);
                    if (!labels.TryGetValue(ck, out var list)) { list = new List<Label>(); labels[ck] = list; }
                    if (F_Dominance && Dominated(list, ng, next.Vx, next.Vy)) continue;
                    list.RemoveAll(l => l.G >= ng - 0.01f && MathF.Abs(l.Vx) <= MathF.Abs(next.Vx) + 0.01f && MathF.Sign(l.Vx) == MathF.Sign(next.Vx) && MathF.Abs(l.Vy - next.Vy) < VxQuant);
                    list.Add(new Label { G = ng, Vx = next.Vx, Vy = next.Vy });
                    came[next] = (cur, frames, ng, pillar, digTiles);
                    open.Enqueue(next, ng + HeuristicWeight * Heuristic(next, goalCx, goalFeetY, ph));
                }
            }

            sw.Stop();
            res.Expansions = expansions;
            res.Millis = sw.Elapsed.TotalMilliseconds;
            res.Found = found;
            if (!found)
            {
                DiagLog.Write($"[ss-fail] exp={expansions}/{MaxExpansions} openLeft={open.Count} bestCell={StandCell(res.BestPx, res.BestPy)} bestDx={res.BestDx:0.#} bestDy={res.BestDy:0.#}");
                DumpTerrain(start, goalWx, goalWy, res.Explored);
            }
            if (found)
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

        static IEnumerable<(SSNode next, List<PhysicsSimulator.ControlInput> frames, float cost, bool pillar, List<(int, int)> digTiles)> Expand(
            SSNode cur, PhysicsSimulator.Params ph, float goalCx, float goalFeetY, int[] holdOptions, int platformTile, bool hasPickaxe)
        {
            if (!cur.Grounded) yield break;

            float curH = Heuristic(cur, goalCx, goalFeetY, ph);

            // First emit all plain walk/jump edges, tracking whether ANY of them meaningfully reduces the
            // (vertical-aware) heuristic. Horizontal shuffling toward a wall lowers x-distance but not h once
            // blocked; only real progress counts. Placement is expensive, so only build when walk/jump is stuck.
            bool anyProgress = false;
            bool vertProgress = false; // a plain jump that lands the player on a HIGHER cell (climbs a natural ledge)
            int dirToGoal = goalCx >= cur.Px ? 1 : -1;
            var (_, dcy) = StandCell(cur.Px, cur.Py);
            foreach (int dir in new[] { dirToGoal, -dirToGoal })
            {
                foreach (int hold in holdOptions)
                {
                    var seg = SimulateSegment(cur, dir, hold, ph);
                    if (!seg.HasValue) continue;
                    // progress uses the RAW per-cell field, not the block-coarsened Heuristic: inside an 8x8 block
                    // the coarsened H is flat, so every in-block move reads "no progress" and dig fires even where a
                    // plain jump clears a low step. raw field still drops cell-by-cell toward the goal.
                    if (RawProgress(cur, seg.Value.node)) anyProgress = true;
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
                var (fcx, fcy) = StandCell(cur.Px, cur.Py);
                bool plat = PathPlanner.PlatformPublic(fcx, fcy + 1);
                if (plat)
                {
                    // a human drops off a platform by holding Down (+ a direction) and rides the fall all the way
                    // to the real floor, not stopping one tile below. emit drop edges for hold-left / -right /
                    // -straight so A* can pick the one that rides the diagonal seam down to the bottom.
                    foreach (int ddir in new[] { dirToGoal, -dirToGoal, 0 })
                    {
                        var drop = SimulateDrop(cur, ddir, ph);
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
                foreach (var pe in OnDemandPlatformEdges(cur, ph, platformTile, vertProgress, hasPickaxe, anyProgress))
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
            SSNode cur, PhysicsSimulator.Params ph, int platformTile, bool vertProgress, bool hasPickaxe, bool anyProgress)
        {
            var (ccx, ccy) = StandCell(cur.Px, cur.Py);
            int curH = _distField != null && _distField.TryGetValue((ccx, ccy), out int h0) ? h0 : int.MaxValue;
            int hl = _distField != null && _distField.TryGetValue((ccx - 1, ccy), out int a) ? a : int.MaxValue;
            int hr = _distField != null && _distField.TryGetValue((ccx + 1, ccy), out int b) ? b : int.MaxValue;
            int gdir = hl < hr ? -1 : 1;                 // gradient-descent horizontal direction (toward lower maze H)
            int targetDir = gdir;
            int maxScan = MaxScan(ph);

            // --- VERTICAL: maze wants UP and a plain jump can't reach. prefer in-place VERTICAL JUMP-PLACE (跳放):
            // jump straight up (dir=0), drop ONE platform at the arc top, land on it — gains several tiles at once
            // when a foothold (e.g. a tree) lets the tile stick. only fall back to PILLAR (原地一格格垒) when no
            // jump-place clears VertPlaceMinRise tiles (a short hop isn't worth the jump/land overhead).
            if (platformTile >= 0 && MathF.Abs(cur.Vx) < VerticalJumpVxMax && _distField != null)
            {
                int upH = _distField.TryGetValue((ccx, ccy - 3), out int hu) ? hu : int.MaxValue;
                if (upH < curH)
                {
                    bool anyVertJumpPlace = false;
                    foreach (int hold in BuildHoldOptions())
                    {
                        var jp = JumpPlace(cur, 0, hold, ph, platformTile);
                        if (!jp.HasValue) continue;
                        var (jcx, jcy) = StandCell(jp.Value.node.Px, jp.Value.node.Py);
                        if (ccy - jcy < VertPlaceMinRise) continue; // too short → pillar does it cheaper
                        anyVertJumpPlace = true;
                        yield return (jp.Value.node, jp.Value.frames, jp.Value.frames.Count + JumpPlaceCost, false, null);
                    }
                    // gate pillar on no lateral way out: if walking along gdir reaches a lower-H standable cell,
                    // a human walks out and climbs the slope rather than pillaring up in place.
                    bool lateralOut = F_PillarNeedNoLateral && HasLateralProgress(ccx, ccy, gdir, curH, maxScan);
                    if (!anyVertJumpPlace && !vertProgress && !lateralOut && SkillExecutor.CanPillarFrom(ccx, ccy, out int topFeetY) && topFeetY < ccy)
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
            }

            // VERTICAL DOWN: worth-it test is inside DigDown (H drop >= margin AND no lateral walk reaches an
            // equally-low cell), not !anyProgress — the latter made dig a last resort so A* detoured first.
            if (hasPickaxe && _distField != null)
            {
                var dd = DigDown(cur, ccx, ccy, curH, gdir, maxScan);
                if (dd.HasValue)
                    yield return (dd.Value.node, null, dd.Value.cost, false, dd.Value.tiles);
            }

            // --- VERTICAL UP THROUGH SEALED CEILING: cycles of "mine 2 rows above the head, pillar-jump
            // 2 tiles onto placed blocks", until breaking out into a lower-H cell. pillar=true AND
            // digTiles!=null together mark this composite edge; retrace expands it into alternating
            // Dig/Pillar sub-steps. needs blocks to pillar with, hence platformTile gate.
            if (hasPickaxe && !anyProgress && platformTile >= 0 && _distField != null && MathF.Abs(cur.Vx) < VerticalJumpVxMax)
            {
                var du = DigUp(cur, ccx, ccy, curH);
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
                var walk = SimulateSegment(cur, gdir, 0, ph);
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
                        var jp = JumpPlace(cur, gdir, hold, ph, platformTile);
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
                // DIG: always offer the horizontal tunnel edge (cost = real mining frames); let A* compare it on
                // price against walk/jump/jump-place/pillar/detour rather than gating it out beforehand. a 1-tile step
                // is NOT mined because mining a tile (~36f) costs more than the jump that clears it, so A* picks the
                // jump; a wall is dug only when tunnelling is genuinely cheaper than routing around it.
                if (hasPickaxe)
                {
                    var dig = DigThroughWall(gdir, ccx, ccy, curH);
                    if (dig.HasValue)
                        yield return (dig.Value.node, null, dig.Value.cost, false, dig.Value.tiles);
                }
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
                    var jp = JumpPlaceAcross(cur, gdir, hold, ph, platformTile, curH);
                    if (jp.HasValue) { anyAcross = true; yield return (jp.Value.node, jp.Value.frames, jp.Value.frames.Count + JumpPlaceCost, false, null); }
                }
                if (!anyAcross)
                {
                    var br = BridgePlace(cur, gdir, ph, platformTile);
                    if (br.HasValue)
                        yield return (br.Value.node, br.Value.frames, br.Value.frames.Count + BridgeCost, false, null);
                }
            }
        }

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
        static bool WorthDig(int ccx, int ccy, int curH, int lh, int gdir, int maxScan)
        {
            if (curH - lh < DigWorthMargin) return false;
            for (int d = 1; d <= maxScan; d++)
                for (int dir = -1; dir <= 1; dir += 2)
                {
                    int x = ccx + dir * d;
                    if (PathPlanner.IsBlockPublic(x, ccy)) continue;
                    if (CoarseStand(x, ccy) && _distField.TryGetValue((x, ccy), out int hx) && hx <= lh) return false;
                }
            return true;
        }

        static (SSNode node, List<(int wx, int wy)> tiles, float cost)? DigDown(SSNode cur, int ccx, int ccy, int curH, int gdir, int maxScan)
        {
            float centerPx = cur.Px + PhysicsSimulator.PlayerW / 2f;
            int c2 = centerPx > ccx * 16f + 8f ? ccx + 1 : ccx - 1;
            var tiles = new List<(int, int)>();
            float cost = 0f;
            for (int y = ccy + 1; y <= ccy + DigMaxScan; y++)
            {
                if (!DigSolid(ccx, y) && !DigSolid(c2, y) && tiles.Count > 0)
                {
                    int landC = PathPlanner.IsFloorPublic(ccx, y + 1) ? ccx
                              : PathPlanner.IsFloorPublic(c2, y + 1) ? c2 : int.MinValue;
                    if (landC == int.MinValue) continue;   // open cavity, keep falling deeper in scan
                    bool hasH = _distField.TryGetValue((landC, y), out int lh);
                    if (!(hasH && WorthDig(ccx, ccy, curH, lh, gdir, maxScan))) return null;
                    float npx = landC * 16f + 8f - PhysicsSimulator.PlayerW / 2f;
                    float npy = (y + 1) * 16f - PhysicsSimulator.PlayerH;
                    var node = new SSNode { Px = npx, Py = npy, Vx = 0f, Vy = 0f, Grounded = true };
                    return (node, tiles, cost);
                }
                foreach (int c in new[] { ccx, c2 })
                    if (DigSolid(c, y))
                    {
                        int fc = DigTable.CostFrames(Main.tile[c, y].TileType);
                        // CanKillTile: Terraria forbids breaking a tile that supports an attached object above (chest /
                        // tree / framed object) — mining it would never succeed and the executor would hang. treat it
                        // as unmineable so A* routes around instead of planning an un-diggable tile.
                        if (fc >= DigTable.Unmineable || !Terraria.WorldGen.CanKillTile(c, y)) return null;
                        cost += fc;
                        tiles.Add((c, y));
                    }
            }
            // no cavity within scan → land at the shaft bottom (the dug space IS the standing room, the
            // undug rock below IS the floor). the maze field penetrates rock with dig-weighted costs, so
            // the H gate stays meaningful mid-rock — long descents chain shaft after shaft.
            int yEnd = ccy + DigMaxScan;
            bool endFloor = PathPlanner.IsFloorPublic(ccx, yEnd + 1);
            bool endH = _distField.TryGetValue((ccx, yEnd), out int eh);
            if (tiles.Count > 0 && endFloor && endH && WorthDig(ccx, ccy, curH, eh, gdir, maxScan))
            {
                float epx = ccx * 16f + 8f - PhysicsSimulator.PlayerW / 2f;
                float epy = (yEnd + 1) * 16f - PhysicsSimulator.PlayerH;
                return (new SSNode { Px = epx, Py = epy, Vx = 0f, Vy = 0f, Grounded = true }, tiles, cost);
            }
            DiagLog.Write($"[ss-digdown] from=({ccx},{ccy}) shaftEnd=({ccx},{yEnd}) tiles={tiles.Count} endFloor={endFloor} → null");
            return null;
        }

        // Dig upward through a sealed ceiling: per cycle mine 2 rows above the head (2 columns, same body-width
        // reason as DigDown), then pillar-jump 2 tiles onto placed blocks. Yields only when the ceiling is
        // actually sealed (first cycle mines something — open headroom belongs to jump/jump-place/pillar) and
        // the breakout cell has lower maze H.
        static (SSNode node, List<(int wx, int wy)> tiles, float cost)? DigUp(SSNode cur, int ccx, int ccy, int curH)
        {
            float centerPx = cur.Px + PhysicsSimulator.PlayerW / 2f;
            int c2 = centerPx > ccx * 16f + 8f ? ccx + 1 : ccx - 1;
            var tiles = new List<(int, int)>();
            float cost = 0f;
            for (int k = 1; k * 2 <= DigMaxScan; k++)
            {
                foreach (int y in new[] { ccy - 1 - 2 * k, ccy - 2 - 2 * k })
                    foreach (int c in new[] { ccx, c2 })
                        if (DigSolid(c, y))
                        {
                            int fc = DigTable.CostFrames(Main.tile[c, y].TileType);
                            if (fc >= DigTable.Unmineable) return null;
                            cost += fc;
                            tiles.Add((c, y));
                        }
                if (k == 1 && tiles.Count == 0) return null;
                int feetY = ccy - 2 * k;
                cost += 43f;
                if (_distField.TryGetValue((ccx, feetY), out int lh) && lh < curH)
                {
                    float npx = ccx * 16f + 8f - PhysicsSimulator.PlayerW / 2f;
                    float npy = (feetY + 1) * 16f - PhysicsSimulator.PlayerH;
                    return (new SSNode { Px = npx, Py = npy, Vx = 0f, Vy = 0f, Grounded = true }, tiles, cost);
                }
            }
            return null;
        }

        // Mine straight through a solid wall along dir, stopping at the first standable cell on the far side.
        // Returns (landing node, tiles to mine, total mining-frame cost), or null if unmineable / no standable exit.
        static (SSNode node, List<(int wx, int wy)> tiles, float cost)? DigThroughWall(int dir, int ccx, int ccy, int curH)
        {
            var tiles = new List<(int, int)>();
            float cost = 0f;
            int x = ccx + dir;
            for (int step = 0; step < DigMaxScan; step++, x += dir)
            {
                // mine whatever blocks the 3 body rows of THIS column first, accumulating real frame cost
                foreach (int y in new[] { ccy, ccy - 1, ccy - 2 })
                    if (DigSolid(x, y))
                    {
                        int fc = DigTable.CostFrames(Main.tile[x, y].TileType);
                        // unmineable = no/weak pick, OR a tile that supports an attached object above (chest/tree/etc.)
                        // which Terraria won't let break — mining it would hang the executor. route around instead.
                        if (fc >= DigTable.Unmineable || !Terraria.WorldGen.CanKillTile(x, y)) { DiagLog.Write($"[ss-digwall] from=({ccx},{ccy}) dir={dir} UNMINEABLE/unbreakable at ({x},{y}) → null"); return null; }
                        cost += fc;
                        tiles.Add((x, y));
                    }
                // STOP as soon as this column is a valid landing toward the goal: body rows clear, support underfoot
                // (native floor OR solid rock below — standing in the tunnel counts), and maze H lower than start.
                // don't run to DigMaxScan — that overshoots the target column and the end-cell H climbs back up.
                bool bodyClear = !DigSolid(x, ccy) && !DigSolid(x, ccy - 1) && !DigSolid(x, ccy - 2);
                bool support = PathPlanner.IsFloorPublic(x, ccy + 1) || DigSolid(x, ccy + 1);
                bool toward = _distField != null && _distField.TryGetValue((x, ccy), out int hx) && hx < curH;
                if (bodyClear && support && toward)
                {
                    float npx = x * 16f + 8f - PhysicsSimulator.PlayerW / 2f;
                    float npy = (ccy + 1) * 16f - PhysicsSimulator.PlayerH;
                    var node = new SSNode { Px = npx, Py = npy, Vx = 0f, Vy = 0f, Grounded = true };
                    return (node, tiles, cost);
                }
            }
            DiagLog.Write($"[ss-digwall] from=({ccx},{ccy}) dir={dir} NO LANDING within {DigMaxScan}: tiles={tiles.Count} → null");
            return null;
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
            if (apexFootCx == int.MinValue) { _jpNoSpot++; return null; }

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
            if (placeCx == int.MinValue) { _jpNoSpot++; return null; }
            float probeVy = 0f, probeFootPy = 0f;
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
            if (!MarkPlaceFrame(seg.Value.frames, placeCx, placeCy)) { _jpNoSpot++; return null; } // unreachable placement

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

        // "Jump and place ONE platform to cross a gap" (移动跳放横穿). Unlike JumpPlace (which only accepts
        // landings HIGHER than the start — climbing), this accepts same-height / lower landings as long as the
        // landing cell's maze H drops below here (real progress toward goal). One placement per (dir,hold): scan
        // the descending arc for the FIRST foot cell that is placeable + adjacent to real support, drop a
        // platform, land on it. Placed tile NOT stored in node (pure-physics key, no combinatorial blowup —
        // the 118ab5f lesson). H-gate caps fan-out: an across-place that doesn't reduce H is never yielded.
        static (SSNode node, List<PhysicsSimulator.ControlInput> frames)? JumpPlaceAcross(
            SSNode cur, int dir, int hold, PhysicsSimulator.Params ph, int platformTile, int curH)
        {
            if (hold == 0 || dir == 0) return null;

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
                if (!(_distField != null && _distField.TryGetValue((lcx, lcy), out int lh) && lh < curH)) return null;
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
            var (ncx, ncy) = StandCell(node.Px, node.Py);
            if (!PathPlanner.IsFloorPublic(ncx, ncy + 1)) return null;
            return (node, frames);
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
                    if (footSupported && MathF.Abs(s.Px - startPx) >= 24f) break;
                    if (footSupported && MathF.Abs(s.Px - prevPx) < 0.05f && f >= 2) break; // wall: not advancing
                }
            }
            if (frames.Count == 0) return null;
            var node = new SSNode { Px = s.Px, Py = s.Py, Vx = s.Vx, Vy = s.Vy, Grounded = s.Grounded };
            if (MathF.Abs(node.Px - cur.Px) < 1f && MathF.Abs(node.Py - cur.Py) < 1f) return null; // no self-loops
            // FRAGILE: in water gravity is so weak the sim still reads Grounded=true while floating over empty cells
            // (the player hasn't sunk enough to register a non-ground frame). a grounded landing whose foot columns
            // have NO real floor below is a fake stand — reject it so A* must place a platform instead of "walking"
            // across open water and looping. only applies to grounded landings (airborne fall/jump edges are fine).
            if (node.Grounded)
            {
                var (ncx, ncy) = StandCell(node.Px, node.Py);
                if (!PathPlanner.IsFloorPublic(ncx, ncy + 1)) return null; // reported stand cell has no floor = fake
            }
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

        // The maze field is a memoryless 2D cost grid: it scores a path only by total weighted cost, blind to the
        // ORDER of moves. So "up-then-right" and "right-then-up" get identical H even though the player's physics
        // make them very different (horizontal speed feeds the jump). Its per-cell gradient down the start column
        // tricks A* into climbing straight up (pillar) instead of walking out and jumping diagonally like a human.
        // Fix: coarsen H to N×N blocks (min field value in the block). The block's interior has a FLAT H, so A*
        // no longer chases the per-cell vertical gradient — it explores move order freely via physics Expand, and
        // the field only steers the coarse region-to-region direction. HBlockSize=1 disables (per-cell = old behavior).
        const int HBlockSize = 8;
        static Dictionary<(int, int), int> _blockH;

        static int BlockMinH(int cx, int cy)
        {
            int bx = (cx < 0 ? cx - HBlockSize + 1 : cx) / HBlockSize;
            int by = (cy < 0 ? cy - HBlockSize + 1 : cy) / HBlockSize;
            if (_blockH != null && _blockH.TryGetValue((bx, by), out int cached)) return cached;
            int best = int.MaxValue;
            int x0 = bx * HBlockSize, y0 = by * HBlockSize;
            for (int x = x0; x < x0 + HBlockSize; x++)
                for (int y = y0; y < y0 + HBlockSize; y++)
                    if (_distField.TryGetValue((x, y), out int v) && v < best) best = v;
            _blockH ??= new Dictionary<(int, int), int>();
            _blockH[(bx, by)] = best;
            return best;
        }

        // is there a standable cell along gdir (within reach) with lower maze H than here — i.e. a walk-out route.
        static bool HasLateralProgress(int ccx, int ccy, int gdir, int curH, int maxScan)
        {
            for (int d = 1; d <= maxScan; d++)
            {
                int x = ccx + gdir * d;
                if (PathPlanner.IsBlockPublic(x, ccy)) break; // wall blocks the walk-out
                if (!CoarseStand(x, ccy)) continue;
                if (_distField.TryGetValue((x, ccy), out int hx) && hx < curH) return true;
            }
            return false;
        }

        // progress on the RAW per-cell maze field (not block-coarsened): landing cell's H lower than the current
        // cell's. used to decide "a plain move already advances → don't dig"; the coarsened Heuristic is flat
        // inside a block and would wrongly report no progress for in-block moves.
        static bool RawProgress(SSNode from, SSNode to)
        {
            if (_distField == null) return false;
            var (fcx, fcy) = StandCell(from.Px, from.Py);
            var (tcx, tcy) = StandCell(to.Px, to.Py);
            if (!_distField.TryGetValue((fcx, fcy), out int fh)) return false;
            if (!_distField.TryGetValue((tcx, tcy), out int th)) return false;
            return th < fh;
        }

        static float Heuristic(SSNode s, float goalCx, float goalFeetY, PhysicsSimulator.Params ph)
        {
            if (_distField != null)
            {
                var (cx, cy) = StandCell(s.Px, s.Py);
                int h = HBlockSize <= 1
                    ? (_distField.TryGetValue((cx, cy), out int d0) ? d0 : int.MaxValue)
                    : BlockMinH(cx, cy);
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

        public static bool IsActive => _execFrames != null && _execIdx < _execFrames.Count;

        public static void StopExec() { _execFrames = null; _execIdx = 0; }

        // ===== Action-graph path executor: run ActionGraphPlanner.Plan's path edge-by-edge. Jump edges REPLAY the
        // edge's own forward-simulated frames (planned trajectory == executed trajectory). pillar/bridge/dig go to
        // their state-machine executors. each edge starts only when the player is landed + at rest (clean state).
        // Edge-by-edge executor for a state-space Plan path. frame steps replay their own simulated frames (planned
        // == executed); pillar steps drive SkillExecutor.StartPillarJump (the macro climb). each step starts only
        // when the previous executor is idle and the player is landed + settled (clean rest state).
        static List<ExecStep> _ssSteps;
        static int _ssStepIdx;
        static bool _ssDispatched;
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
                    DiagLog.Write($"[ss-framecmp] planFrames={_ssPrevStep.Frames.Count} execFrames={_lastExecFrameCount} planLand=({lf.Px:0.##},{lf.Py:0.##}) execLand=({p.position.X:0.##},{p.position.Y:0.##}) d(px={(p.position.X - lf.Px):0.##} py={(p.position.Y - lf.Py):0.##}) planVx={lf.Vx:0.###} execVx={p.velocity.X:0.###} dVx={(p.velocity.X - lf.Vx):0.###}");
                }
                _ssStepIdx++;
                _ssDispatched = false;
                if (!StepsActive) { DiagLog.Write("[ss-steps] done"); StopSteps(); DiagLog.EndRun(); return; }
            }
            if (busy || p.velocity.Y != 0f) return; // start each step from rest on the ground

            var st = _ssSteps[_ssStepIdx];
            int ccx = (int)(p.Center.X / 16f);
            DiagLog.Write($"[ss-steps] #{_ssStepIdx}/{_ssSteps.Count} {(st.Pillar ? "pillar" : st.Dig ? "dig" : "move")} ->({st.TargetCx},{st.TargetCy})");
            _ssDispatched = true;
            _ssPrevStep = st; _lastExecFrameCount = 0;
            _execGoalWx = st.TargetCx; _execGoalWy = st.TargetCy;

            if (st.Pillar)
                SkillExecutor.StartPillarJump(st.TargetCx >= ccx, st.TargetCy);
            else if (st.Dig)
            {
                int sfeet = (int)((p.position.Y + p.height) / 16f) - 1;
                MineCoordinator.Start(new MineRequest { Dir = st.DigDir, StartWx = ccx, StartWy = sfeet, TargetWx = st.TargetCx, TargetWy = st.TargetCy, MineTiles = st.MineTiles });
            }
            else if (st.Frames != null && st.Frames.Count > 0)
            {
                // DIAGNOSTIC: does the player's REAL start match the start this edge's frames were planned from?
                // any gap here = open-loop replay from a wrong origin → accumulates → edge-of-block plunge.
                var f0 = st.Frames[0];
                DiagLog.Write($"[ss-startgap] step#{_ssStepIdx} planStart=({f0.Px:0.##},{f0.Py:0.##}) realStart=({p.position.X:0.##},{p.position.Y:0.##}) dPx={(p.position.X - f0.Px):0.##} dPy={(p.position.Y - f0.Py):0.##}");
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
            _blockH = null;
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
            foreach (var (next, frames, _, _, _) in Expand(cur, ph, gx, gy, BuildHoldOptions(), platformTile, false))
            {
                if (frames == null) continue; // greedy can't drive the pillar macro; skip those edges
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
            StopGreedy(); StopSteps();
            var pStart = Main.LocalPlayer;
            var (rsx, rsy) = StandCell(pStart.position.X, pStart.position.Y);
            DiagLog.StartRun($"{rsx}_{rsy}__{goalWx}_{goalWy}");
            DiagLog.Write($"[run] ss_exec start=({rsx},{rsy}) goal=({goalWx},{goalWy})");
            var res = Plan(goalWx, goalWy);
            Visualize(res, goalWx, goalWy);
            DiagLog.Write($"[ss-plan] target=({goalWx},{goalWy}) found={res.Found} exp={res.Expansions} ms={res.Millis:0.#} steps={res.Steps.Count} best_dx={res.BestDx:0.#} best_dy={res.BestDy:0.#}");
            if (!res.Found || res.Steps.Count == 0) { StopSteps(); return res; }
            _finalGoalWx = res.GoalWx; _finalGoalWy = res.GoalWy;  // true destination; replan aims here, never a step target
            _execGoalWx = res.GoalWx; _execGoalWy = res.GoalWy;
            _replanCooldownLeft = 0;
            _replanCount = 0;
            _placeStall = 0;
            _rescueCooldownLeft = 0;
            _stuckFrames = 0;
            _lastReal.Valid = false;
            StartSteps(res.Steps);   // edge-by-edge: frame replay + pillar macro
            return res;
        }

        static bool Replan(string reason)
        {
            if (_replanCount >= MaxReplans) { DiagLog.Write("[ss-replan] max replans hit → stop"); return false; }
            _replanCount++;
            // aim at the TRUE goal from the player's real position (closed-loop correction). during edge-by-edge
            // execution, rebuild the step list (not the open-loop ExecFrames); steps re-derive from where the
            // player actually is, so accumulated drift can't snowball into a pit.
            bool steps = StepsActive;
            _silentPath = true;
            var res = Plan(_finalGoalWx, _finalGoalWy);
            _silentPath = false;
            Visualize(res, _finalGoalWx, _finalGoalWy);
            var rp = Main.LocalPlayer;
            string first = res.Steps.Count > 0
                ? (res.Steps[0].Pillar ? "pillar" : res.Steps[0].Dig ? "dig" : "move") + $"->({res.Steps[0].TargetCx},{res.Steps[0].TargetCy})"
                : "-";
            DiagLog.Write($"[ss-replan] reason={reason} #{_replanCount} from=({(int)((rp.position.X+10)/16f)},{(int)((rp.position.Y+42)/16f)}) goal=({_finalGoalWx},{_finalGoalWy}) found={res.Found} exp={res.Expansions} ms={res.Millis:0.#} steps={res.Steps.Count} first={first}");
            if (!res.Found || res.Steps.Count == 0) return false;
            _replanCooldownLeft = ReplanCooldown;
            _placeStall = 0;
            if (steps) { StartSteps(res.Steps); return true; }
            _execFrames = res.ExecFrames; _execIdx = 0;
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
