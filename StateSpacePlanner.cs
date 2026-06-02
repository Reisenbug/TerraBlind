using System;
using System.Collections.Generic;
using Terraria;

namespace TerraBlind
{
    // State-space physics search: node = real physics state, expansion = enumerate inputs × simulate.
    // Standalone prototype; does not touch the grid A*.
    public static class StateSpacePlanner
    {
        const float PxQuant = 4f;
        const float VxQuant = 0.5f;
        const int   MaxExpansions = 20000;
        const int   MaxSegFrames = 120;
        const int   HoldStep = 2;

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

        struct NodeKey : IEquatable<NodeKey>
        {
            public int Qpx, Qpy, Qvx, Qvy; public bool G;
            public bool Equals(NodeKey o) => Qpx == o.Qpx && Qpy == o.Qpy && Qvx == o.Qvx && Qvy == o.Qvy && G == o.G;
            public override int GetHashCode() => HashCode.Combine(Qpx, Qpy, Qvx, Qvy, G);
        }

        static NodeKey Key(SSNode s) => new NodeKey
        {
            Qpx = (int)MathF.Round(s.Px / PxQuant),
            Qpy = (int)MathF.Round(s.Py / PxQuant),
            Qvx = (int)MathF.Round(s.Vx / VxQuant),
            Qvy = (int)MathF.Round(s.Vy / VxQuant),
            G = s.Grounded,
        };

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

        public static SSResult Plan(int goalWx, int goalWy)
        {
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

            var start = new SSNode
            {
                Px = p.position.X, Py = p.position.Y,
                Vx = p.velocity.X, Vy = 0f, Grounded = true,
            };

            var g = new Dictionary<NodeKey, float>();
            var came = new Dictionary<NodeKey, (NodeKey prev, SSNode node, List<PhysicsSimulator.ControlInput> frames)>();
            var open = new PriorityQueue<SSNode, float>();
            var startKey = Key(start);
            g[startKey] = 0f;
            came[startKey] = (startKey, start, null);
            open.Enqueue(start, Heuristic(start, goalCx, goalFeetY, ph));

            int expansions = 0;
            NodeKey goalKey = default; bool found = false;
            float bestDist = float.MaxValue;

            while (open.Count > 0 && expansions < MaxExpansions)
            {
                var cur = open.Dequeue();
                var curKey = Key(cur);
                float curG = g.TryGetValue(curKey, out var gv) ? gv : float.MaxValue;

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
                    found = true; goalKey = curKey; break;
                }

                expansions++;
                if (res.Explored.Count < 3000) res.Explored.Add((cur.Px, cur.Py));
                foreach (var (next, frames, cost) in Expand(cur, ph, goalCx, holdOptions, platformTile))
                {
                    var nk = Key(next);
                    float ng = curG + cost;
                    if (ng < g.GetValueOrDefault(nk, float.MaxValue))
                    {
                        g[nk] = ng;
                        came[nk] = (curKey, next, frames);
                        open.Enqueue(next, ng + Heuristic(next, goalCx, goalFeetY, ph));
                    }
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
                var k = goalKey;
                var revPts = new List<(float, float)>();
                var revSegs = new List<PathSeg>();
                var revFrameLists = new List<List<PhysicsSimulator.ControlInput>>();
                while (came.TryGetValue(k, out var e) && !e.prev.Equals(k))
                {
                    revPts.Add((e.node.Px, e.node.Py));
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
            return res;
        }

        static IEnumerable<(SSNode next, List<PhysicsSimulator.ControlInput> frames, float cost)> Expand(
            SSNode cur, PhysicsSimulator.Params ph, float goalCx, int[] holdOptions, int platformTile)
        {
            if (!cur.Grounded) yield break;

            int dirToGoal = goalCx >= cur.Px ? 1 : -1;
            foreach (int dir in new[] { dirToGoal, -dirToGoal })
            {
                foreach (int hold in holdOptions)
                {
                    var seg = SimulateSegment(cur, dir, hold, ph);
                    if (!seg.HasValue) continue;
                    yield return (seg.Value.node, seg.Value.frames, seg.Value.frames.Count);

                    if (platformTile >= 0)
                    {
                        var jp = JumpPlace(cur, dir, hold, ph, platformTile);
                        if (jp.HasValue)
                            yield return (jp.Value.node, jp.Value.frames, jp.Value.frames.Count + JumpPlaceCost);
                    }
                }
            }
        }

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
            for (int f = 0; f < MaxSegFrames; f++)
            {
                var input = new PhysicsSimulator.ControlInput { Right = dir > 0, Left = dir < 0, Jump = f < hold };
                s = PhysicsSimulator.Step(s, input, ph);
                if (f < hold) continue; // only consider placing after the hold phase (descending/apex)
                int fcx = (int)((s.Px + PhysicsSimulator.PlayerW / 2f) / 16f);
                int fcy = (int)((s.Py + PhysicsSimulator.PlayerH + 1f) / 16f); // foot cell
                if (CanPlaceReal(fcx, fcy)) { placeCx = fcx; placeCy = fcy; break; }
            }
            if (placeCx == int.MinValue) return null;

            // re-simulate with the platform present so native collision lands the player on it
            var seg = SimulateWithPlatform(cur, dir, hold, ph, placeCx, placeCy, platformTile);
            if (!seg.HasValue || !seg.Value.node.Grounded) return null;
            MarkPlaceFrame(seg.Value.frames, placeCx, placeCy);
            return seg.Value;
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

        static float Heuristic(SSNode s, float goalCx, float goalFeetY, PhysicsSimulator.Params ph)
        {
            float cx = s.Px + PhysicsSimulator.PlayerW / 2f;
            float feetY = s.Py + PhysicsSimulator.PlayerH;
            float dx = MathF.Abs(cx - goalCx);
            float dy = MathF.Abs(feetY - goalFeetY);
            return dx / MathF.Max(ph.MaxRun, 0.1f) + dy / 5f;
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
            DiagLog.Write($"[ss-replan] reason={reason} #{_replanCount} found={res.Found} ms={res.Millis:0.#} frames={res.ExecFrames.Count}");
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
