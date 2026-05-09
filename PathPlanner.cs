using System;
using System.Collections.Generic;
using System.Text;
using Terraria;

namespace TerraBlind
{
    public static class PathPlanner
    {
        private const int GoalRangeFwd = 60;    // max forward search distance for goal (tiles)
        private const int GoalRangeBack = 20;   // max backward search distance for A* expansion (tiles)
        private const int MinGoalDist = 5;      // minimum distance from player to goal, prevents staying in place
        private const int AStarScanUp = 50;     // yMin = feetY - AStarScanUp; how high A* can expand
        private const int AStarScanDown = 50;   // yMax = feetY + AStarScanDown; how deep A* can expand
        private const int MaxBridge = 25;       // max bridge length per segment (tiles)
        private const int BridgeDtgThresh = 8;  // bridge penalty-free if ground is this many tiles below
        private const int CanProgressK = 0;     // goal validity: 0 = any standable tile is valid goal
        private const int JumpMinCol = 2;       // minimum horizontal tiles per jump, filters trivial hops
        private const float JumpOverheadMax = 4f;    // extra cost for short jumps; full-range jump = 0, min jump = JumpOverheadMax
        private const float BridgeCostBase = 4f;     // fixed cost to start a bridge
        private const float BridgeCostPerCol = 2f;   // cost per tile extended in a bridge
        private const float FallCost = 0.5f;         // cost per fall tile, cheaper than move to encourage natural drops
        private const float MoveCostBase = 1f;       // base cost per move tile, plus distance-to-ground penalty

        public static readonly int[] HoldFrameOptions = { 8, 12, 15 };

        // kept for envelope visualization only
        private static int[] _envelopeCache;
        public static int[] GetEnvelopeCache() => _envelopeCache;

        public static bool SolidPublic(int wx, int wy) => Solid(wx, wy);
        public static bool PlatformPublic(int wx, int wy) => Platform(wx, wy);
        public static bool IsBlockPublic(int wx, int wy) => IsBlock(wx, wy);
        public static bool IsFloorPublic(int wx, int wy) => IsFloor(wx, wy);
        public static bool IsHalfBrickPublic(int wx, int wy) => IsHalfBrick(wx, wy);

        public static string DebugJumpEdgesVerbose(Player p, int cx, int cy, int sign)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("[");
            float startPx = cx * 16f - (p.width / 2f) + 8f;
            float startPy = cy * 16f - p.height;
            int xMin = cx - 80, xMax = cx + 80, yMin = cy - 50, yMax = cy + 50;
            var seen = new HashSet<(int,int)>();
            var ph2 = PhysicsSimulator.Params.FromPlayer(p);
            bool first = true;
            foreach (int hold in HoldFrameOptions)
            {
                var startState = new PhysicsSimulator.State { Px = startPx, Py = startPy, Vx = sign * ph2.MaxRun, Vy = 0f, Grounded = true, JumpFramesLeft = hold };
                var result = PhysicsSimulator.SimulateJump(startState, sign, hold, ph2);
                if (!first) sb.Append(','); first = false;
                sb.Append($"{{\"hold\":{hold},\"landed\":{(result.Landed?"true":"false")},\"lx\":{result.Cx},\"ly\":{result.Cy}");
                if (result.Landed)
                {
                    bool inBounds = result.Cx >= xMin && result.Cx <= xMax && result.Cy >= yMin && result.Cy <= yMax;
                    bool minCol = sign * (result.Cx - cx) >= JumpMinCol;
                    bool standable = Standable(result.Cx, result.Cy);
                    bool dup = seen.Contains((result.Cx, result.Cy));
                    sb.Append($",\"inBounds\":{(inBounds?"true":"false")},\"minCol\":{(minCol?"true":"false")},\"standable\":{(standable?"true":"false")},\"dup\":{(dup?"true":"false")}");
                    seen.Add((result.Cx, result.Cy));
                }
                sb.Append("}");
            }
            sb.Append("]");
            return sb.ToString();
        }

        public static List<(int lx, int ly, int hold)> DebugJumpEdges(Player p, int cx, int cy, int sign)
        {
            var result = new List<(int, int, int)>();
            var edges = BuildJumpEdges(p, cx, cy, sign, cx - 80, cx + 80, cy - 50, cy + 50);
            foreach (var (lx, ly, frames, hold) in edges)
                result.Add((lx, ly, hold));
            return result;
        }

        private static bool IsBlock(int wx, int wy)
        {
            if (wx < 0 || wy < 0 || wx >= Main.maxTilesX || wy >= Main.maxTilesY) return true;
            var t = Main.tile[wx, wy];
            if (t == null || !t.HasTile) return false;
            if (!Main.tileSolid[t.TileType] || Main.tileSolidTop[t.TileType]) return false;
            return (int)t.Slope == 0 && !t.IsHalfBlock;
        }

        private static bool IsHalfBrick(int wx, int wy)
        {
            if (wx < 0 || wy < 0 || wx >= Main.maxTilesX || wy >= Main.maxTilesY) return false;
            var t = Main.tile[wx, wy];
            if (t == null || !t.HasTile) return false;
            if (!Main.tileSolid[t.TileType] || Main.tileSolidTop[t.TileType]) return false;
            return (int)t.Slope != 0 || t.IsHalfBlock;
        }

        private static bool Platform(int wx, int wy)
        {
            if (wx < 0 || wy < 0 || wx >= Main.maxTilesX || wy >= Main.maxTilesY) return false;
            var t = Main.tile[wx, wy];
            return t != null && t.HasTile && Main.tileSolidTop[t.TileType];
        }

        private static bool IsFloor(int wx, int wy) => IsBlock(wx, wy) || Platform(wx, wy) || IsHalfBrick(wx, wy);

        private static bool Solid(int wx, int wy) => IsBlock(wx, wy);

        // tile exists but cannot be placed on/through (tree trunks, vines, etc.)
        private static bool Occupied(int wx, int wy)
        {
            if (wx < 0 || wy < 0 || wx >= Main.maxTilesX || wy >= Main.maxTilesY) return false;
            var t = Main.tile[wx, wy];
            if (t == null || !t.HasTile) return false;
            return !Main.tileSolid[t.TileType] && !Main.tileSolidTop[t.TileType];
        }

        private static bool Standable(int wx, int wy)
            => !IsBlock(wx, wy) && !Platform(wx, wy) && !IsHalfBrick(wx, wy) && IsFloor(wx, wy + 1);

        private static int DistToGround(int wx, int wy, int maxDepth = 20)
        {
            for (int d = 0; d < maxDepth; d++)
                if (IsFloor(wx, wy + d)) return d;
            return maxDepth;
        }

        private static bool CanProgress(int gx, int gy, int sign, int K)
        {
            var visited = new HashSet<(int, int)>();
            var queue = new Queue<(int x, int y, int steps)>();
            queue.Enqueue((gx, gy, 0));
            visited.Add((gx, gy));
            while (queue.Count > 0)
            {
                var (cx, cy, steps) = queue.Dequeue();
                if (sign * (cx - gx) >= K) return true;
                if (steps >= K + 2) continue;
                int nx = cx + sign;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int ny = cy + dy;
                    if (Standable(nx, ny) && !visited.Contains((nx, ny)))
                    {
                        visited.Add((nx, ny));
                        queue.Enqueue((nx, ny, steps + 1));
                        break;
                    }
                }
                if (!IsFloor(cx, cy + 1) && !visited.Contains((cx, cy + 1)))
                {
                    visited.Add((cx, cy + 1));
                    queue.Enqueue((cx, cy + 1, steps + 1));
                }
            }
            return false;
        }

        private static float BridgePenalty(int dtg)
        {
            if (dtg >= BridgeDtgThresh) return 0;
            return (BridgeDtgThresh - dtg) * 2;
        }

        private static bool ArcClipsWall(List<PhysicsSimulator.ControlInput> frames,
            float startPx, float startPy, float startVx, int playerW, int playerH, int holdFrames,
            PhysicsSimulator.Params ph)
        {
            var s = new PhysicsSimulator.State
            {
                Px = startPx, Py = startPy,
                Vx = startVx, Vy = 0f,
                Grounded = true,
                JumpFramesLeft = holdFrames,
            };
            for (int f = 0; f < frames.Count; f++)
            {
                s = PhysicsSimulator.Step(s, frames[f], ph);
                int tileX0 = (int)(s.Px / 16);
                int tileX1 = (int)((s.Px + playerW - 1) / 16);
                int tileY0 = (int)(s.Py / 16);
                int tileY1 = (int)((s.Py + playerH - 1) / 16);
                for (int tx = tileX0; tx <= tileX1; tx++)
                    for (int ty = tileY0; ty <= tileY1; ty++)
                        if (IsBlock(tx, ty)) return true;
            }
            return false;
        }

        // returns list of (landCx, landCy, frames, holdFrames) for all valid jump outcomes from this tile
        private static List<(int cx, int cy, List<PhysicsSimulator.ControlInput> frames, int hold)>
            BuildJumpEdges(Player p, int cx, int cy, int sign, int xMin, int xMax, int yMin, int yMax)
        {
            var results = new List<(int, int, List<PhysicsSimulator.ControlInput>, int)>();
            var seen = new HashSet<(int, int)>();
            var ph = PhysicsSimulator.Params.FromPlayer(p);

            float startPx = cx * 16f - (p.width / 2f) + 8f;
            float startPy = cy * 16f - p.height;
            float startVx = sign * ph.MaxRun;

            foreach (int hold in HoldFrameOptions)
            {
                var startState = new PhysicsSimulator.State
                {
                    Px = startPx, Py = startPy,
                    Vx = startVx,
                    Vy = 0f,
                    Grounded = true,
                    JumpFramesLeft = hold,
                };
                var result = PhysicsSimulator.SimulateJump(startState, sign, hold, ph);
                if (!result.Landed) continue;
                int lx = (int)((result.EndState.Px + p.width / 2f) / 16f);
                int ly = (int)((result.EndState.Py + p.height) / 16f) - 1;
                if (lx < xMin || lx > xMax || ly < yMin || ly > yMax) continue;
                if (sign * (lx - cx) < JumpMinCol) continue;
                if (!Standable(lx, ly)) continue;
                if (seen.Contains((lx, ly))) continue;
                if (ArcClipsWall(result.Frames, startPx, startPy, startVx, p.width, p.height, hold, ph))
                {
                    DiagLog.Write($"[plan] jump edge wallclip src=({cx},{cy}) target=({lx},{ly}) hold={hold}");
                    continue;
                }
                seen.Add((lx, ly));
                results.Add((lx, ly, result.Frames, hold));
            }
            return results;
        }

        public static string Plan(int sign, System.Collections.Generic.HashSet<(int, int)> excludedGoals = null)
        {
            var p = Main.LocalPlayer;
            if (p == null || !p.active) return "{\"error\":\"no_player\"}";

            int pcx = (int)((p.position.X + p.width / 2f) / 16f);
            int feetY = (int)((p.position.Y + p.height) / 16f);
            while (IsBlock(pcx, feetY) && feetY > 0) feetY--;
            if (!IsFloor(pcx, feetY) && IsFloor(pcx, feetY + 1)) feetY++;
            if (IsHalfBrick(pcx, feetY)) feetY--;
            if (!Standable(pcx, feetY))
            {
                for (int dy = 1; dy <= 3; dy++)
                {
                    if (Standable(pcx, feetY + dy)) { feetY += dy; break; }
                    if (Standable(pcx, feetY - dy)) { feetY -= dy; break; }
                }
            }

            var excludedArr = new System.Text.StringBuilder();
            if (excludedGoals != null) { foreach (var eg in excludedGoals) excludedArr.Append($"[{eg.Item1},{eg.Item2}],"); }
            DiagLog.WriteEvent($"{{\"e\":\"plan_start\",\"tick\":{Main.GameUpdateCount},\"sign\":{sign},\"px\":{pcx},\"py\":{feetY},\"excluded_goals\":[{excludedArr.ToString().TrimEnd(',')}]}}");

            // build envelope cache for visualization only
            _envelopeCache = BuildEnvelopeVis(p);

            int xMin = sign > 0 ? pcx - GoalRangeBack : pcx - GoalRangeFwd;
            int xMax = sign > 0 ? pcx + GoalRangeFwd : pcx + GoalRangeBack;
            int yMin = feetY - AStarScanUp;
            int yMax = feetY + AStarScanDown;

            int goalX = -1, goalY = -1;
            var rejectedGoals = new System.Text.StringBuilder();
            for (int wx = xMin; wx <= xMax; wx++)
            {
                if (sign * (wx - pcx) < MinGoalDist) continue;
                for (int wy = Math.Max(50, feetY - AStarScanUp); wy <= yMax; wy++)
                {
                    if (!Standable(wx, wy)) continue;
                    bool excluded = excludedGoals != null && excludedGoals.Contains((wx, wy));
                    if (excluded) { rejectedGoals.Append($"{{\"wx\":{wx},\"wy\":{wy},\"reason\":\"excluded\"}},"); break; }
                    bool ok = CanProgress(wx, wy, sign, CanProgressK);
                    if (!ok) { rejectedGoals.Append($"{{\"wx\":{wx},\"wy\":{wy},\"reason\":\"no_progress\"}},"); break; }
                    int fwd = sign * (wx - pcx);
                    int rise = feetY - wy;
                    int score = fwd + Math.Max(0, rise) * 2;
                    int bestScore = goalX < 0 ? int.MinValue : sign * (goalX - pcx) + Math.Max(0, feetY - goalY) * 2;
                    if (score > bestScore) { goalX = wx; goalY = wy; }
                    break;
                }
            }
            DiagLog.Write($"[plan] goal=({goalX},{goalY})");
            if (goalX == -1)
            {
                DiagLog.Write("[plan] no goal found");
                DiagLog.WriteEvent($"{{\"e\":\"plan_failed\",\"tick\":{Main.GameUpdateCount},\"reason\":\"no_goal\",\"px\":{pcx},\"py\":{feetY},\"candidates_rejected\":[{rejectedGoals.ToString().TrimEnd(',')}]}}");
                return "{\"path\":[],\"cost\":0}";
            }
            DiagLog.Write($"[plan] goal=({goalX},{goalY}) start=({pcx},{feetY})");

            var g = new Dictionary<(int, int), float>();
            var prev = new Dictionary<(int, int), ((int, int), string, List<PhysicsSimulator.ControlInput>)>();
            var visited = new HashSet<(int, int)>();
            var bridgeNodes = new HashSet<(int, int)>();
            var heap = new PriorityQueue<(int wx, int wy), float>();

            var startNode = (pcx, feetY);
            g[startNode] = 0f;
            prev[startNode] = ((-1, -1), "", null);
            heap.Enqueue((pcx, feetY), Math.Abs(goalX - pcx) + Math.Abs(goalY - feetY));

            while (heap.Count > 0)
            {
                var (cx, cy) = heap.Dequeue();

                if (visited.Contains((cx, cy))) continue;
                visited.Add((cx, cy));

                if (cx == goalX && cy == goalY)
                    return BuildResult(prev, g, goalX, goalY, startNode);

                float curG = g.TryGetValue((cx, cy), out var cg) ? cg : float.MaxValue;

                foreach (var (dx, dy) in new[] { (1,0),(-1,0),(0,1),(0,-1),(1,-1),(-1,-1) })
                {
                    int nx = cx + dx, ny = cy + dy;
                    if (nx < xMin || nx > xMax || ny < yMin || ny > yMax) continue;
                    if (Solid(nx, ny)) continue;
                    if (dy == -1 && dx == 0) continue;
                    if (dy == -1 && !IsFloor(nx, ny + 1)) continue;
                    if (dy == -1 && dx != 0 && !Standable(nx, ny)) continue;
                    int dtg = dx != 0 ? DistToGround(nx, ny) : 0;
                    float cost = dy == 1 ? FallCost : MoveCostBase + dtg;
                    float ng = curG + cost;
                    if (ng < g.GetValueOrDefault((nx, ny), float.MaxValue))
                    {
                        g[(nx, ny)] = ng;
                        string action = dy == 1 ? "fall" : "move";
                        prev[(nx, ny)] = ((cx, cy), action, null);
                        float h = Math.Abs(goalX - nx) + Math.Abs(goalY - ny);
                        heap.Enqueue((nx, ny), ng + h);
                    }
                }

                bool canJump = (Standable(cx, cy) || bridgeNodes.Contains((cx, cy)))
                    && !Solid(cx, cy - 1) && !Solid(cx, cy - 2);
                if (canJump)
                {
                    var jumpEdges = BuildJumpEdges(p, cx, cy, sign, xMin, xMax, yMin, yMax);
                    foreach (var (lx, ly, frames, hold) in jumpEdges)
                    {
                        int rise = cy - ly;
                        float riseBonus = Math.Max(0, rise - 1) * 2f;
                        int col = Math.Abs(lx - cx);
                        int maxHold = HoldFrameOptions[HoldFrameOptions.Length - 1];
                        float efficiency = maxHold > 0 ? (float)hold / maxHold : 1f;
                        float jumpOverhead = JumpOverheadMax * (1f - efficiency);
                        float cost = Math.Max(col + jumpOverhead - riseBonus, 1f);
                        float ng = curG + cost;
                        if (ng < g.GetValueOrDefault((lx, ly), float.MaxValue))
                        {
                            g[(lx, ly)] = ng;
                            prev[(lx, ly)] = ((cx, cy), "jump", frames);
                            float h = Math.Abs(goalX - lx) + Math.Abs(goalY - ly);
                            heap.Enqueue((lx, ly), ng + h);
                        }
                    }
                }

                if (Standable(cx, cy) && !Solid(cx, cy - 1) && !Solid(cx, cy - 2))
                {
                    for (int topY = cy - 1; topY >= yMin; topY--)
                    {
                        if (Solid(cx, topY)) break;
                        if (Occupied(cx, topY)) continue;
                        if (!Solid(cx, topY - 1) && !Solid(cx, topY - 2) && !Occupied(cx, topY - 1) && !Occupied(cx, topY - 2))
                        {
                            int rise = cy - topY;
                            if (rise <= 7) continue;
                            // check full vertical clearance from launch point: rise + 2 tiles above cy
                            bool blocked = false;
                            for (int checkY = cy - 1; checkY >= cy - rise - 2; checkY--)
                                if (Solid(cx, checkY)) { blocked = true; break; }
                            if (blocked) continue;
                            float cost = curG + 3f + rise;
                            if (cost < g.GetValueOrDefault((cx, topY), float.MaxValue))
                            {
                                g[(cx, topY)] = cost;
                                bridgeNodes.Add((cx, topY));
                                prev[(cx, topY)] = ((cx, cy), "pillar", null);
                                float h = Math.Abs(goalX - cx) + Math.Abs(goalY - topY);
                                heap.Enqueue((cx, topY), cost + h);
                            }
                        }
                    }
                }

                if (Standable(cx, cy))
                {
                    int minDtg = 20;
                    for (int col = 1; col <= MaxBridge; col++)
                    {
                        int nx = cx + sign * col;
                        if (nx < xMin || nx > xMax) break;
                        if (Solid(nx, cy) || Solid(nx, cy - 1) || Solid(nx, cy - 2)) break;
                        minDtg = Math.Min(minDtg, DistToGround(nx, cy));
                        if (!IsFloor(nx, cy + 1) || Standable(nx, cy))
                        {
                            float cost = BridgeCostBase + col * BridgeCostPerCol + BridgePenalty(minDtg);
                            float ng = curG + cost;
                            if (ng < g.GetValueOrDefault((nx, cy), float.MaxValue))
                            {
                                g[(nx, cy)] = ng;
                                bridgeNodes.Add((nx, cy));
                                prev[(nx, cy)] = ((cx, cy), "bridge", null);
                                float h = Math.Abs(goalX - nx) + Math.Abs(goalY - cy);
                                heap.Enqueue((nx, cy), ng + h);
                            }
                        }
                    }
                }
            }

            (int, int) best = startNode;
            int bestFwd = 0;
            int bestWy = int.MaxValue;
            foreach (var kv in g)
            {
                var (wx, wy) = kv.Key;
                int fwd = sign * (wx - pcx);
                if (fwd <= 0) continue;
                if (!Standable(wx, wy)) continue;
                if (wy < bestWy || (wy == bestWy && fwd > bestFwd)) { bestFwd = fwd; bestWy = wy; best = (wx, wy); }
            }
            if (best == startNode || bestFwd <= 0)
            {
                DiagLog.Write($"[plan] no usable fallback bestFwd={bestFwd} visited={visited.Count}");
                DiagLog.WriteEvent($"{{\"e\":\"plan_failed\",\"tick\":{Main.GameUpdateCount},\"reason\":\"no_fallback\",\"px\":{pcx},\"py\":{feetY},\"candidates_rejected\":[]}}");
                return "{\"path\":[],\"cost\":0}";
            }
            DiagLog.Write($"[plan] fallback→({best.Item1},{best.Item2}) visited={visited.Count}");
            return BuildResult(prev, g, best.Item1, best.Item2, startNode);
        }

        private static string BuildResult(
            Dictionary<(int, int), ((int, int), string, List<PhysicsSimulator.ControlInput>)> prev,
            Dictionary<(int, int), float> g,
            int wx, int wy, (int, int) start)
        {
            // path entry: (landWx, landWy, sourceWx, sourceWy, action, frames)
            var path = new List<(int wx, int wy, int swx, int swy, string action, List<PhysicsSimulator.ControlInput> frames)>();
            var pos = (wx, wy);
            while (prev.TryGetValue(pos, out var entry) && entry.Item1 != (-1, -1))
            {
                path.Add((pos.Item1, pos.Item2, entry.Item1.Item1, entry.Item1.Item2, entry.Item2, entry.Item3));
                pos = entry.Item1;
            }
            path.Reverse();
            float cost = g.TryGetValue((wx, wy), out var c) ? c : 0f;
            var sb = new StringBuilder();
            sb.Append("{\"path\":[");
            for (int i = 0; i < path.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"wx\":").Append(path[i].wx)
                  .Append(",\"wy\":").Append(path[i].wy)
                  .Append(",\"action\":\"").Append(path[i].action).Append("\"");
                if (path[i].action == "jump" && path[i].frames != null)
                {
                    sb.Append(",\"swx\":").Append(path[i].swx)
                      .Append(",\"swy\":").Append(path[i].swy);
                    sb.Append(",\"frames\":[");
                    var frames = path[i].frames;
                    for (int fi = 0; fi < frames.Count; fi++)
                    {
                        if (fi > 0) sb.Append(',');
                        var f = frames[fi];
                        sb.Append("{\"j\":").Append(f.Jump ? "1" : "0")
                          .Append(",\"r\":").Append(f.Right ? "1" : "0")
                          .Append(",\"l\":").Append(f.Left ? "1" : "0")
                          .Append("}");
                    }
                    sb.Append("]");
                }
                sb.Append("}");
            }
            sb.Append("],\"goal\":[").Append(wx).Append(',').Append(wy).Append("]");
            sb.Append(",\"cost\":").Append(cost.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)).Append('}');
            DiagLog.Write($"[plan] path len={path.Count} cost={cost:0.#}");

            var evSb = new StringBuilder();
            evSb.Append($"{{\"e\":\"plan_done\",\"tick\":{Main.GameUpdateCount},\"goal\":[{wx},{wy}],\"path_len\":{path.Count},\"cost\":{cost.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)},\"envelope_len\":{(_envelopeCache != null ? _envelopeCache.Length : 0)},\"path\":[");
            for (int i = 0; i < path.Count; i++)
            {
                if (i > 0) evSb.Append(',');
                evSb.Append($"{{\"wx\":{path[i].wx},\"wy\":{path[i].wy},\"action\":\"{path[i].action}\"}}");
            }
            evSb.Append("]}");
            DiagLog.WriteEvent(evSb.ToString());

            return sb.ToString();
        }

        // envelope for visualization only, not used for edge generation
        private static int[] BuildEnvelopeVis(Player p)
        {
            var ph = PhysicsSimulator.Params.FromPlayer(p);
            float js = Player.jumpSpeed;
            float grav = ph.Gravity;
            int jh = Player.jumpHeight;
            float vx = ph.MaxRun;

            float holdSpeed = js - grav;
            float phase1Ticks = jh + 1;
            float phase2Ticks = holdSpeed / grav;
            float peakT = phase1Ticks + phase2Ticks;
            float peakRisePx = holdSpeed * phase1Ticks + holdSpeed * phase2Ticks - 0.5f * grav * phase2Ticks * phase2Ticks;

            var envList = new List<int>();
            for (int col = 0; ; col++)
            {
                float t = col * 16f / Math.Max(vx, 0.01f);
                float risePx;
                if (t <= phase1Ticks)
                    risePx = holdSpeed * t;
                else if (t <= peakT)
                {
                    float dt = t - phase1Ticks;
                    risePx = holdSpeed * phase1Ticks + holdSpeed * dt - 0.5f * grav * dt * dt;
                }
                else
                {
                    float dt = t - peakT;
                    risePx = peakRisePx - 0.5f * grav * dt * dt;
                }
                int dy = (int)(-risePx / 16f);
                envList.Add(dy);
                if (dy >= 50) break;
            }
            return envList.ToArray();
        }
    }
}
