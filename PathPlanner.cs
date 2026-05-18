using System;
using System.Collections.Generic;
using System.Text;
using Terraria;

namespace TerraBlind
{
    public static class PathPlanner
    {
        private const int GoalRangeFwd = 60;    // max forward search distance for goal (tiles)
        private const int GoalRangeBack = 60;   // max backward search distance for A* expansion (tiles)
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
        public static readonly int[] HoldFrameOptionsWet = { 10, 16, 22, 30 };

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
            foreach (var (lx, ly, frames, hold, arcClips, wallFrames, ceilFrames) in edges)
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
                float prevVy = s.Vy;
                s = PhysicsSimulator.Step(s, frames[f], ph);
                if (prevVy > 0f) continue;
                int tileX0 = (int)(s.Px / 16);
                int tileX1 = (int)((s.Px + playerW - 1) / 16);
                int tileY0 = (int)(s.Py / 16);
                for (int tx = tileX0; tx <= tileX1; tx++)
                    if (IsBlock(tx, tileY0)) return true;
            }
            return false;
        }

        // returns list of (landCx, landCy, frames, holdFrames) for all valid jump outcomes from this tile
        private static List<(int cx, int cy, List<PhysicsSimulator.ControlInput> frames, int hold, bool arcClips, int wallFrames, int ceilFrames)>
            BuildJumpEdges(Player p, int cx, int cy, int sign, int xMin, int xMax, int yMin, int yMax, float? overrideVx = null)
        {
            var results = new List<(int, int, List<PhysicsSimulator.ControlInput>, int, bool, int, int)>();
            var seen = new HashSet<(int, int)>();
            var ph = PhysicsSimulator.Params.FromPlayer(p);

            float startPx = cx * 16f - (p.width / 2f) + 8f;
            float startPy = cy * 16f - p.height;
            float startVx = overrideVx ?? sign * ph.MaxRun;

            var holdOptions = p.wet && !p.honeyWet && !p.merman ? HoldFrameOptionsWet : HoldFrameOptions;
            foreach (int hold in holdOptions)
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
                bool arcClips = ArcClipsWall(result.Frames, startPx, startPy, startVx, p.width, p.height, hold, ph);
                if (arcClips)
                {
                    DiagLog.Write($"[plan] jump edge wallclip src=({cx},{cy}) target=({lx},{ly}) hold={hold}");
                    continue;
                }
                seen.Add((lx, ly));
                results.Add((lx, ly, result.Frames, hold, arcClips, result.WallContactFrames, result.CeilingContactFrames));

                // step-up: horizontal movement allows climbing 1 extra tile
                if (lx != cx && Standable(lx, ly - 1) && !seen.Contains((lx, ly - 1))
                    && !Solid(lx, ly - 2) && !Solid(lx, ly - 3)
                    && !Solid(lx + (sign > 0 ? 1 : -1), ly - 2) && !Solid(lx + (sign > 0 ? 1 : -1), ly - 3))
                {
                    seen.Add((lx, ly - 1));
                    results.Add((lx, ly - 1, result.Frames, hold, arcClips, result.WallContactFrames, result.CeilingContactFrames));
                }
            }
            return results;
        }

        private const int ReachX = 5;
        private const int ReachY = 4;
        private const float MineCostPerTile = 6f;

        public static string PlanTo(int goalWx, int goalWy)
        {
            var p = Main.LocalPlayer;
            if (p == null || !p.active) return "{\"error\":\"no_player\"}";
            int sign = goalWx >= (int)((p.position.X + p.width / 2f) / 16f) ? 1 : -1;
            var goalSet = new HashSet<(int, int, bool)> { (goalWx, goalWy, false) };
            return Plan(sign, null, goalSet, bidir: true);
        }

        private static float MinDistToGoal(HashSet<(int, int, bool)> goalSet, int x, int y)
        {
            float best = float.MaxValue;
            foreach (var (gx, gy, _) in goalSet)
            {
                float d = Math.Abs(gx - x) + Math.Abs(gy - y);
                if (d < best) best = d;
            }
            return best;
        }

        public static string Plan(int sign, System.Collections.Generic.HashSet<(int, int)> excludedGoals = null, HashSet<(int, int, bool)> goalSet = null, bool bidir = false)
        {
            var p = Main.LocalPlayer;
            if (p == null || !p.active) return "{\"error\":\"no_player\"}";

            if (p.maxRunSpeed > PhysicsSimulator.MaxRunSpeed + 0.01f)
                DiagLog.Write($"[plan] WARN buff detected maxRunSpeed={p.maxRunSpeed:0.##} planner not validated for non-default speed");

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

            int xMin = bidir ? pcx - GoalRangeFwd : (sign > 0 ? pcx - GoalRangeBack : pcx - GoalRangeFwd);
            int xMax = bidir ? pcx + GoalRangeFwd : (sign > 0 ? pcx + GoalRangeFwd : pcx + GoalRangeBack);
            int yMin = feetY - AStarScanUp;
            int yMax = feetY + AStarScanDown;

            int goalX, goalY;
            if (goalSet != null && goalSet.Count > 0)
            {
                goalX = -1; goalY = -1;
                foreach (var (gx, gy, _) in goalSet)
                {
                    xMin = Math.Min(xMin, gx - 5);
                    xMax = Math.Max(xMax, gx + 5);
                    yMin = Math.Min(yMin, gy - 5);
                    yMax = Math.Max(yMax, gy + 5);
                    if (goalX < 0) { goalX = gx; goalY = gy; }
                }
            }
            else
            {
                goalSet = new HashSet<(int, int, bool)>();
                goalX = -1; goalY = -1;
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
                if (goalX == -1)
                {
                    DiagLog.Write("[plan] no goal found");
                    DiagLog.WriteEvent($"{{\"e\":\"plan_failed\",\"tick\":{Main.GameUpdateCount},\"reason\":\"no_goal\",\"px\":{pcx},\"py\":{feetY},\"candidates_rejected\":[{rejectedGoals.ToString().TrimEnd(',')}]}}");
                    return "{\"path\":[],\"cost\":0}";
                }
                goalSet.Add((goalX, goalY, false));
            }
            DiagLog.Write($"[plan] goal=({goalX},{goalY}) goalSet={goalSet.Count} start=({pcx},{feetY})");

            var g = new Dictionary<(int, int, bool), float>();
            var prev = new Dictionary<(int, int, bool), ((int, int, bool), string, List<PhysicsSimulator.ControlInput>)>();
            var visited = new HashSet<(int, int, bool)>();
            var bridgeNodes = new HashSet<(int, int, bool)>();
            var heap = new PriorityQueue<(int wx, int wy, bool hc), float>();
            var verifyData = new Dictionary<(int, int, bool), (int hold, float startVx, int wallFrames, int ceilFrames)>();
            var pillarVerifyData = new Dictionary<(int, int, bool), (bool leftClear, bool rightClear, bool centerOnlyClear)>();
            var mineTilesData = new Dictionary<(int, int, bool), List<(int, int)>>();
            var mineNodes = new HashSet<(int, int, bool)>();
            var mineDepth = new Dictionary<(int, int, bool), int>();
            int maxMineDepth = Math.Abs(goalX - pcx) + Math.Abs(goalY - feetY) + 8;

            var startNode = (pcx, feetY, false);
            g[startNode] = 0f;
            prev[startNode] = ((-1, -1, false), "", null);
            heap.Enqueue((pcx, feetY, false), MinDistToGoal(goalSet, pcx, feetY));

            while (heap.Count > 0)
            {
                var (cx, cy, hc) = heap.Dequeue();

                if (visited.Contains((cx, cy, hc))) continue;
                visited.Add((cx, cy, hc));

                if (goalSet.Contains((cx, cy, hc)) || goalSet.Contains((cx, cy, false)) || goalSet.Contains((cx, cy, true)))
                    return BuildResult(prev, g, cx, cy, hc, startNode, verifyData, pillarVerifyData, mineTilesData);

                float curG = g.TryGetValue((cx, cy, hc), out var cg) ? cg : float.MaxValue;

                int curMineDepth = mineDepth.TryGetValue((cx, cy, hc), out var md) ? md : 0;
                bool canMineFrom = (Standable(cx, cy) || mineNodes.Contains((cx, cy, hc))) && curMineDepth < maxMineDepth;
                foreach (var (dx, dy) in new[] { (1,0),(-1,0),(0,1),(0,-1),(1,-1),(-1,-1) })
                {
                    int nx = cx + dx, ny = cy + dy;
                    if (nx < xMin || nx > xMax || ny < yMin || ny > yMax) continue;
                    if (dy == -1 && dx == 0) continue;

                    if (Solid(nx, ny))
                    {
                        if (!canMineFrom) continue;
                        if (dy == 1 || (dy == 0 && dx != 0))
                        {
                            var mt = new List<(int, int)>();
                            float mc = 0f;
                            if (dy == 0)
                            {
                                int wallX = dx > 0 ? nx : cx - 2;
                                for (int ddy = 0; ddy <= 2; ddy++) { if (Solid(wallX, cy - ddy)) { mt.Add((wallX, cy - ddy)); mc += MineCostPerTile; } }
                                if (mt.Count == 0 || !IsFloor(nx, ny + 1)) continue;
                            }
                            else
                            {
                                for (int col = cx; col <= cx + 1; col++) { if (Solid(col, ny)) { mt.Add((col, ny)); mc += MineCostPerTile; } }
                                if (mt.Count == 0) continue;
                            }
                            float cost = dy == 1 ? mc + FallCost : mc + MoveCostBase;
                            float ng = curG + cost;
                            if (ng < g.GetValueOrDefault((nx, ny, false), float.MaxValue))
                            {
                                g[(nx, ny, false)] = ng;
                                string mineAction = dy == 1 ? "mine_down" : (dx > 0 ? "mine_right" : "mine_left");
                                prev[(nx, ny, false)] = ((cx, cy, hc), mineAction, null);
                                mineTilesData[(nx, ny, false)] = mt;
                                mineNodes.Add((nx, ny, false));
                                mineDepth[(nx, ny, false)] = curMineDepth + 1;
                                heap.Enqueue((nx, ny, false), ng + MinDistToGoal(goalSet, nx, ny));
                            }
                        }
                        continue;
                    }

                    if (dy == 1 && dx == 0 && (IsFloor(cx, cy + 1) || IsFloor(cx + 1, cy + 1))) continue;
                    if (dy == -1 && !IsFloor(nx, ny + 1)) continue;
                    if (dy == -1 && dx != 0 && !Standable(nx, ny)) continue;
                    if (dy == 0 && dx != 0 && (Solid(nx, ny - 1) || Solid(nx, ny - 2) || Solid(nx + dx, ny - 1) || Solid(nx + dx, ny - 2))) continue;
                    int dtg = dx != 0 ? DistToGround(nx, ny) : 0;
                    float moveCost = dy == 1 ? FallCost : MoveCostBase + dtg;
                    float moveng = curG + moveCost;
                    if (moveng < g.GetValueOrDefault((nx, ny, false), float.MaxValue))
                    {
                        g[(nx, ny, false)] = moveng;
                        string action = dy == 1 ? "fall" : "move";
                        prev[(nx, ny, false)] = ((cx, cy, hc), action, null);
                        heap.Enqueue((nx, ny, false), moveng + MinDistToGoal(goalSet, nx, ny));
                    }
                }

                if (canMineFrom && !hc)
                {
                    var mt = new List<(int, int)>();
                    float mc = 0f;
                    for (int col = cx; col <= cx + 1; col++)
                        for (int ddy = 1; ddy <= 2; ddy++)
                            if (Solid(col, cy - ddy)) { mt.Add((col, cy - ddy)); mc += MineCostPerTile; }
                    if (mt.Count > 0)
                    {
                        float ng = curG + mc;
                        if (ng < g.GetValueOrDefault((cx, cy, true), float.MaxValue))
                        {
                            g[(cx, cy, true)] = ng;
                            prev[(cx, cy, true)] = ((cx, cy, hc), "mine_up", null);
                            mineTilesData[(cx, cy, true)] = mt;
                            heap.Enqueue((cx, cy, true), ng + MinDistToGoal(goalSet, cx, cy));
                        }
                    }
                }

                bool headClear = !Solid(cx, cy - 1) && !Solid(cx, cy - 2) && !Solid(cx + 1, cy - 1) && !Solid(cx + 1, cy - 2);
                bool canJump = (Standable(cx, cy) || bridgeNodes.Contains((cx, cy, hc)))
                    && (headClear || hc);
                if (canJump)
                {
                    bool fromPillar = prev.TryGetValue((cx, cy, hc), out var prevEntry) && prevEntry.Item2 == "pillar";
                    float? jumpStartVx = fromPillar ? 0f : (float?)null;
                    var jumpEdges = BuildJumpEdges(p, cx, cy, sign, xMin, xMax, yMin, yMax, jumpStartVx);
                    foreach (var (lx, ly, frames, hold, arcClips, wallFrames, ceilFrames) in jumpEdges)
                    {
                        int rise = cy - ly;
                        float riseBonus = Math.Max(0, rise - 1) * 2f;
                        int col = Math.Abs(lx - cx);
                        int maxHold = HoldFrameOptions[HoldFrameOptions.Length - 1];
                        float efficiency = maxHold > 0 ? (float)hold / maxHold : 1f;
                        float jumpOverhead = JumpOverheadMax * (1f - efficiency);
                        float cost = Math.Max(col + jumpOverhead - riseBonus, 1f);
                        float ng = curG + cost;
                        if (ng < g.GetValueOrDefault((lx, ly, false), float.MaxValue))
                        {
                            g[(lx, ly, false)] = ng;
                            prev[(lx, ly, false)] = ((cx, cy, hc), "jump", frames);
                            verifyData[(lx, ly, false)] = (hold, startVx: jumpStartVx ?? sign * PhysicsSimulator.MaxRunSpeed, wallFrames, ceilFrames);
                            float h = MinDistToGoal(goalSet, lx, ly);
                            heap.Enqueue((lx, ly, false), ng + h);
                        }
                    }
                }

                if (Standable(cx, cy) && (headClear || hc))
                {
                    for (int topY = cy - 1; topY >= yMin; topY--)
                    {
                        if (Solid(cx, topY)) break;
                        if (Occupied(cx, topY)) continue;
                        if (!Solid(cx, topY - 1) && !Solid(cx, topY - 2) && !Occupied(cx, topY - 1) && !Occupied(cx, topY - 2))
                        {
                            int rise = cy - topY;
                            if (rise <= 1) continue;
                            // center-only clearance (old #5 logic): single column cx
                            bool centerBlocked = false;
                            for (int checkY = cy - 1; checkY >= cy - rise - 2; checkY--)
                                if (Solid(cx, checkY)) { centerBlocked = true; break; }
                            bool centerOnlyClear = !centerBlocked;
                            // left clearance: columns (cx-1, cx) — matches SkillExecutor Left occupancy
                            bool leftBlocked = false;
                            if (cx - 1 >= xMin)
                            {
                                for (int checkY = cy - 1; checkY >= cy - rise - 2; checkY--)
                                    if (Solid(cx - 1, checkY) || Solid(cx, checkY)) { leftBlocked = true; break; }
                            }
                            else leftBlocked = true;
                            bool leftClear = !leftBlocked;
                            // right clearance: columns (cx, cx+1) — for diagnostics only
                            bool rightBlocked = false;
                            if (cx + 1 <= xMax)
                            {
                                for (int checkY = cy - 1; checkY >= cy - rise - 2; checkY--)
                                    if (Solid(cx, checkY) || Solid(cx + 1, checkY)) { rightBlocked = true; break; }
                            }
                            else rightBlocked = true;
                            bool rightClear = !rightBlocked;
                            // only generate edge if left is clear (SkillExecutor is fixed Left)
                            if (!leftClear) continue;
                            if (excludedGoals != null && excludedGoals.Contains((cx, topY))) continue;
                            float cost = curG + 3f + rise * 6f;
                            if (cost < g.GetValueOrDefault((cx, topY, false), float.MaxValue))
                            {
                                g[(cx, topY, false)] = cost;
                                bridgeNodes.Add((cx, topY, false));
                                prev[(cx, topY, false)] = ((cx, cy, hc), "pillar", null);
                                pillarVerifyData[(cx, topY, false)] = (leftClear, rightClear, centerOnlyClear);
                                float h = MinDistToGoal(goalSet, cx, topY);
                                heap.Enqueue((cx, topY, false), cost + h);
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
                            if (ng < g.GetValueOrDefault((nx, cy, false), float.MaxValue))
                            {
                                g[(nx, cy, false)] = ng;
                                bridgeNodes.Add((nx, cy, false));
                                prev[(nx, cy, false)] = ((cx, cy, hc), "bridge", null);
                                float h = MinDistToGoal(goalSet, nx, cy);
                                heap.Enqueue((nx, cy, false), ng + h);
                            }
                        }
                    }
                }
            }

            (int, int, bool) best = startNode;
            int bestFwd = 0;
            int bestWy = int.MaxValue;
            foreach (var kv in g)
            {
                var (wx, wy, whc) = kv.Key;
                int fwd = sign * (wx - pcx);
                if (fwd <= 0) continue;
                if (!Standable(wx, wy)) continue;
                if (wy < bestWy || (wy == bestWy && fwd > bestFwd)) { bestFwd = fwd; bestWy = wy; best = (wx, wy, whc); }
            }
            if (best == startNode || bestFwd <= 0)
            {
                DiagLog.Write($"[plan] no usable fallback bestFwd={bestFwd} visited={visited.Count}");
                DiagLog.WriteEvent($"{{\"e\":\"plan_failed\",\"tick\":{Main.GameUpdateCount},\"reason\":\"no_fallback\",\"px\":{pcx},\"py\":{feetY},\"candidates_rejected\":[]}}");
                return "{\"path\":[],\"cost\":0}";
            }
            DiagLog.Write($"[plan] fallback→({best.Item1},{best.Item2}) visited={visited.Count}");
            return BuildResult(prev, g, best.Item1, best.Item2, best.Item3, startNode, verifyData, pillarVerifyData, mineTilesData);
        }

        private static string BuildResult(
            Dictionary<(int, int, bool), ((int, int, bool), string, List<PhysicsSimulator.ControlInput>)> prev,
            Dictionary<(int, int, bool), float> g,
            int wx, int wy, bool hc, (int, int, bool) start,
            Dictionary<(int, int, bool), (int hold, float startVx, int wallFrames, int ceilFrames)> verifyData = null,
            Dictionary<(int, int, bool), (bool leftClear, bool rightClear, bool centerOnlyClear)> pillarVerifyData = null,
            Dictionary<(int, int, bool), List<(int, int)>> mineTilesData = null)
        {
            var path = new List<(int wx, int wy, int swx, int swy, string action, List<PhysicsSimulator.ControlInput> frames)>();
            var pos = (wx, wy, hc);
            while (prev.TryGetValue(pos, out var entry) && entry.Item1 != (-1, -1, false))
            {
                path.Add((pos.Item1, pos.Item2, entry.Item1.Item1, entry.Item1.Item2, entry.Item2, entry.Item3));
                pos = entry.Item1;
            }
            path.Reverse();
            float cost = g.TryGetValue((wx, wy, hc), out var c) ? c : 0f;
            var sb = new StringBuilder();
            sb.Append("{\"path\":[");
            for (int i = 0; i < path.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"wx\":").Append(path[i].wx)
                  .Append(",\"wy\":").Append(path[i].wy)
                  .Append(",\"action\":\"").Append(path[i].action).Append("\"");
                if (path[i].action == "pillar")
                {
                    sb.Append(",\"swx\":").Append(path[i].swx)
                      .Append(",\"swy\":").Append(path[i].swy);
                }
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
                if (path[i].action.StartsWith("mine_") && mineTilesData != null && (mineTilesData.TryGetValue((path[i].wx, path[i].wy, false), out var mt) || mineTilesData.TryGetValue((path[i].wx, path[i].wy, true), out mt)))
                {
                    sb.Append(",\"mine_tiles\":[");
                    for (int mi = 0; mi < mt.Count; mi++)
                    {
                        if (mi > 0) sb.Append(',');
                        sb.Append('[').Append(mt[mi].Item1).Append(',').Append(mt[mi].Item2).Append(']');
                    }
                    sb.Append("]");
                }
                sb.Append("}");
            }
            sb.Append("],\"goal\":[").Append(wx).Append(',').Append(wy).Append("]");
            sb.Append(",\"cost\":").Append(cost.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)).Append('}');
            DiagLog.Write($"[plan] path len={path.Count} cost={cost:0.#}");
            foreach (var node in path)
            {
                if (node.action == "jump" && verifyData != null && (verifyData.TryGetValue((node.wx, node.wy, false), out var vd) || verifyData.TryGetValue((node.wx, node.wy, true), out vd)))
                    DiagLog.Write($"[verify] edge_emit type=jump from=({node.swx},{node.swy}) to=({node.wx},{node.wy}) hold={vd.hold} startVx={vd.startVx:0.##} wall_frames={vd.wallFrames} ceil_frames={vd.ceilFrames} tick={Main.GameUpdateCount}");
                if (node.action == "pillar" && pillarVerifyData != null && (pillarVerifyData.TryGetValue((node.wx, node.wy, false), out var pv) || pillarVerifyData.TryGetValue((node.wx, node.wy, true), out pv)))
                {
                    int rise = node.swy - node.wy;
                    DiagLog.Write($"[verify] edge_emit type=pillar from=({node.swx},{node.swy}) to=({node.wx},{node.wy}) rise={rise} side=Left left_clear={pv.leftClear} right_clear={pv.rightClear} center_only_clear={pv.centerOnlyClear} tick={Main.GameUpdateCount}");
                }
            }

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
