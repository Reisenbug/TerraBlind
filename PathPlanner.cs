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
        private const int BridgeDtgThresh = 12;
        private const int CanProgressK = 0;     // goal validity: 0 = any standable tile is valid goal
        private const int JumpMinCol = 0;
        private const float JumpOverheadMax = 4f;    // extra cost for short jumps; full-range jump = 0, min jump = JumpOverheadMax
        private const float BridgeCostBase = 10f;
        private const float BridgeCostPerCol = 4f;
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
            foreach (var (lx, ly, frames, hold, arcClips, wallFrames, ceilFrames, endVx) in edges)
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

        private static bool HasPlatformInInventory(Player p)
        {
            for (int i = 0; i < 10; i++)
            {
                var it = p.inventory[i];
                if (it != null && !it.IsAir && it.createTile >= 0 && Terraria.ID.TileID.Sets.Platforms[it.createTile])
                    return true;
            }
            return false;
        }

        // simulate hold=15 air jump from tile (cx,cy) with startVx, returns landing cx, rise, endVx.
        // landing detected by feetY >= startFeetY (vy>0 phase). uses real Step (tile collision applies).
        private static (int landCx, int landFrame, int rise, float endVx) SimAirJumpRaw(
            PhysicsSimulator.Params ph, int cx, int cy, int sign, float startVx, int moveEnd)
        {
            float startPx = cx * 16f - PhysicsSimulator.PlayerW / 2f + 8f;
            float startPy = cy * 16f - PhysicsSimulator.PlayerH;
            int startFeetY = cy;
            var s = new PhysicsSimulator.State
            {
                Px = startPx, Py = startPy,
                Vx = startVx, Vy = 0f, Grounded = true, JumpFramesLeft = 15,
            };
            int landIdx = -1;
            int minFeetY = startFeetY;
            for (int f = 0; f < 120; f++)
            {
                var input = new PhysicsSimulator.ControlInput
                {
                    Jump  = f < 15,
                    Right = sign > 0 && f < moveEnd,
                    Left  = sign < 0 && f < moveEnd,
                };
                s = PhysicsSimulator.Step(s, input, ph);
                int curFeetY = (int)((s.Py + PhysicsSimulator.PlayerH) / 16f);
                if (curFeetY < minFeetY) minFeetY = curFeetY;
                if (f > 15 && curFeetY >= startFeetY) { landIdx = f; break; }
            }
            if (landIdx < 0) landIdx = 119;
            int landCx = (int)((s.Px + PhysicsSimulator.PlayerW / 2f) / 16f);
            return (landCx, landIdx, startFeetY - minFeetY, s.Vx);
        }

        // binary-search moveEnd so landCx matches full-press landCx with smallest |endVx| (mirror BuildPlatJumpFrames)
        private static (int landCx, int landFrame, int rise, float endVx) SimAirJump(
            PhysicsSimulator.Params ph, int cx, int cy, int sign, float startVx = 0f)
        {
            var full = SimAirJumpRaw(ph, cx, cy, sign, startVx, 120);
            int target = full.landCx;
            var best = full;
            int lo = 1, hi = full.landFrame;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                var sim = SimAirJumpRaw(ph, cx, cy, sign, startVx, mid);
                if (sim.landCx == target)
                {
                    best = sim;
                    hi = mid - 1;
                }
                else if ((sign > 0 && sim.landCx < target) || (sign < 0 && sim.landCx > target))
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            return best;
        }

        public static bool CanPlacePlatformAt(int wx, int wy)
        {
            if (wx < 0 || wy < 0 || wx >= Main.maxTilesX || wy >= Main.maxTilesY) return false;
            var t = Main.tile[wx, wy];
            if (t != null && t.HasTile && !Main.tileCut[t.TileType]) return false;
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    int nx = wx + dx, ny = wy + dy;
                    if (nx < 0 || ny < 0 || nx >= Main.maxTilesX || ny >= Main.maxTilesY) continue;
                    var nb = Main.tile[nx, ny];
                    if (nb == null) continue;
                    if (nb.HasTile) return true;
                    if (nb.WallType > 0) return true;
                }
            return false;
        }

        // CanPlacePlatformAt with simulated already-placed tiles
        private static bool CanPlacePlatformAtWith(int wx, int wy, HashSet<(int, int)> placed)
        {
            if (wx < 0 || wy < 0 || wx >= Main.maxTilesX || wy >= Main.maxTilesY) return false;
            var t = Main.tile[wx, wy];
            if (t != null && t.HasTile && !Main.tileCut[t.TileType]) return false;
            if (placed.Contains((wx, wy))) return false; // already placed here
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    int nx = wx + dx, ny = wy + dy;
                    if (placed.Contains((nx, ny))) return true; // neighbor is a placed tile
                    if (nx < 0 || ny < 0 || nx >= Main.maxTilesX || ny >= Main.maxTilesY) continue;
                    var nb = Main.tile[nx, ny];
                    if (nb == null) continue;
                    if (nb.HasTile) return true;
                    if (nb.WallType > 0) return true;
                }
            return false;
        }

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
            return (BridgeDtgThresh - dtg) * 4;
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
        private static List<(int cx, int cy, List<PhysicsSimulator.ControlInput> frames, int hold, bool arcClips, int wallFrames, int ceilFrames, float endVx)>
            BuildJumpEdges(Player p, int cx, int cy, int sign, int xMin, int xMax, int yMin, int yMax, float? overrideVx = null)
        {
            var results = new List<(int, int, List<PhysicsSimulator.ControlInput>, int, bool, int, int, float)>();
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
                if (Math.Abs(lx - cx) < JumpMinCol) continue;
                if (Math.Abs(lx - cx) <= 2 && Math.Abs(ly - cy) < 2) continue;
                if (!Standable(lx, ly)) continue;
                if (seen.Contains((lx, ly))) continue;
                bool arcClips = ArcClipsWall(result.Frames, startPx, startPy, startVx, p.width, p.height, hold, ph);
                if (arcClips)
                {
                    DiagLog.Write($"[plan] jump edge wallclip src=({cx},{cy}) target=({lx},{ly}) hold={hold}");
                    continue;
                }
                float endVx = result.EndState.Vx;
                seen.Add((lx, ly));
                results.Add((lx, ly, result.Frames, hold, arcClips, result.WallContactFrames, result.CeilingContactFrames, endVx));

                if (lx != cx && Standable(lx, ly - 1) && !seen.Contains((lx, ly - 1))
                    && !Solid(lx, ly - 2) && !Solid(lx, ly - 3)
                    && !Solid(lx + (sign > 0 ? 1 : -1), ly - 2) && !Solid(lx + (sign > 0 ? 1 : -1), ly - 3))
                {
                    seen.Add((lx, ly - 1));
                    results.Add((lx, ly - 1, result.Frames, hold, arcClips, result.WallContactFrames, result.CeilingContactFrames, endVx));
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
            var goalSet = new HashSet<(int, int, bool)>();
            if (Standable(goalWx, goalWy))
            {
                goalSet.Add((goalWx, goalWy, false));
            }
            else
            {
                var bfsQueue = new Queue<(int, int)>();
                var bfsSeen = new HashSet<(int, int)>();
                bfsQueue.Enqueue((goalWx, goalWy));
                bfsSeen.Add((goalWx, goalWy));
                while (bfsQueue.Count > 0)
                {
                    var (bx, by) = bfsQueue.Dequeue();
                    if (Math.Abs(bx - goalWx) > 3 || Math.Abs(by - goalWy) > 3) continue;
                    if (Standable(bx, by)) goalSet.Add((bx, by, false));
                    foreach (var (ddx, ddy) in new[] { (1,0),(-1,0),(0,1),(0,-1) })
                    {
                        var nb = (bx + ddx, by + ddy);
                        if (!bfsSeen.Contains(nb)) { bfsSeen.Add(nb); bfsQueue.Enqueue(nb); }
                    }
                }
                if (goalSet.Count == 0)
                {
                    DiagLog.Write($"[plan] PlanTo goal unreachable ({goalWx},{goalWy}), no standable within r=8");
                    goalSet.Add((goalWx, goalWy, false));
                }
            }
            return Plan(sign, null, goalSet, bidir: true, noFallback: true);
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

        private const float HeuristicWeight = 1f;

        public static string Plan(int sign, System.Collections.Generic.HashSet<(int, int)> excludedGoals = null, HashSet<(int, int, bool)> goalSet = null, bool bidir = false, bool noFallback = false)
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

            var ph = PhysicsSimulator.Params.FromPlayer(p);
            var g = new Dictionary<(int, int, bool), float>();
            var prev = new Dictionary<(int, int, bool), ((int, int, bool), string, List<PhysicsSimulator.ControlInput>)>();
            var visited = new HashSet<(int, int, bool)>();
            var bridgeNodes = new HashSet<(int, int, bool)>();
            var heap = new PriorityQueue<(int wx, int wy, bool hc), float>();
            var verifyData = new Dictionary<(int, int, bool), (int hold, float startVx, float endVx, int wallFrames, int ceilFrames)>();
            var nodeEndVx = new Dictionary<(int, int, bool), float>();
            var pillarVerifyData = new Dictionary<(int, int, bool), (bool leftClear, bool rightClear, bool centerOnlyClear)>();
            var mineTilesData = new Dictionary<(int, int, bool), List<(int, int)>>();
            var mineNodes = new HashSet<(int, int, bool)>();
            var mineDepth = new Dictionary<(int, int, bool), int>();
            var pillarTopNodes = new HashSet<(int, int)>();
            var plannedStandable = new HashSet<(int, int)>(); // tiles A* assumes will be placed/mined-clear (bridge/pillar/jump_x/jump_y landing/mine air)
            bool IsStandableNode(int wx, int wy) => Standable(wx, wy) || plannedStandable.Contains((wx, wy));
            void AddPlanned(int wx, int wy) => plannedStandable.Add((wx, wy));
            int maxMineDepth = Math.Abs(goalX - pcx) + Math.Abs(goalY - feetY) + 8;
            float startVx = p.velocity.X;

            var startNode = (pcx, feetY, false);
            g[startNode] = 0f;
            prev[startNode] = ((-1, -1, false), "", null);
            heap.Enqueue((pcx, feetY, false), HeuristicWeight * MinDistToGoal(goalSet, pcx, feetY));

            while (heap.Count > 0)
            {
                var (cx, cy, hc) = heap.Dequeue();

                if (visited.Contains((cx, cy, hc))) continue;
                visited.Add((cx, cy, hc));

                if (goalSet.Contains((cx, cy, false)) || goalSet.Contains((cx, cy, true)))
                    return BuildResult(prev, g, cx, cy, hc, startNode, verifyData, pillarVerifyData, mineTilesData);

                float curG = g.TryGetValue((cx, cy, hc), out var cg) ? cg : float.MaxValue;

                int curMineDepth = mineDepth.TryGetValue((cx, cy, hc), out var md) ? md : 0;
                bool canMineFrom = IsStandableNode(cx, cy) && curMineDepth < maxMineDepth;
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
                                for (int col = cx - 1; col <= cx; col++) { if (Solid(col, ny)) { mt.Add((col, ny)); mc += MineCostPerTile; } }
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
                                AddPlanned(nx, ny);
                                mineDepth[(nx, ny, false)] = curMineDepth + 1;
                                heap.Enqueue((nx, ny, false), ng + HeuristicWeight * MinDistToGoal(goalSet, nx, ny));
                            }
                        }
                        continue;
                    }

                    if (dy == 1 && dx == 0 && (IsBlock(cx, cy + 1) || IsBlock(cx + 1, cy + 1))) continue;
                    if (dy == 1 && dx == 0 && !Standable(nx, ny)) continue;
                    if (dy == -1 && !IsFloor(nx, ny + 1)) continue;
                    if (dy == -1 && dx != 0 && !Standable(nx, ny)) continue;
                    if (dy == 0 && dx != 0 && (Solid(nx, ny - 1) || Solid(nx, ny - 2) || Solid(nx + dx, ny - 1) || Solid(nx + dx, ny - 2))) continue;
                    if (dy == 0 && dx != 0 && !IsStandableNode(nx, ny)) continue;
                    int dtg = dx != 0 ? DistToGround(nx, ny) : 0;
                    float moveCost = dy == 1 ? FallCost : MoveCostBase + dtg;
                    float moveng = curG + moveCost;
                    if (moveng < g.GetValueOrDefault((nx, ny, false), float.MaxValue))
                    {
                        g[(nx, ny, false)] = moveng;
                        string action = dy == 1 ? "fall" : "move";
                        prev[(nx, ny, false)] = ((cx, cy, hc), action, null);
                        heap.Enqueue((nx, ny, false), moveng + HeuristicWeight * MinDistToGoal(goalSet, nx, ny));
                        if (dy == 0 && dx != 0)
                        {
                            float curEndVx = nodeEndVx.TryGetValue((cx, cy, hc), out var ev) ? ev : (cx == pcx && cy == feetY ? startVx : sign * ph.MaxRun);
                            float nxEndVx = Math.Sign(dx) * Math.Min(Math.Abs(curEndVx) + ph.AccRun, ph.MaxRun);
                            nodeEndVx[(nx, ny, false)] = nxEndVx;
                        }
                    }
                }

                if (canMineFrom && !hc)
                {
                    var mt = new List<(int, int)>();
                    float mc = 0f;
                    for (int col = cx - 1; col <= cx; col++)
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
                            heap.Enqueue((cx, cy, true), ng + HeuristicWeight * MinDistToGoal(goalSet, cx, cy));
                        }
                    }
                }

                bool headClear = !Solid(cx - 1, cy - 1) && !Solid(cx - 1, cy - 2) && !Solid(cx, cy - 1) && !Solid(cx, cy - 2);
                bool canJump = IsStandableNode(cx, cy) && (headClear || hc);
                if (canJump)
                {
                    bool isStart = (cx == pcx && cy == feetY);
                    float inferredVx;
                    if (isStart)
                    {
                        inferredVx = startVx;
                    }
                    else if (prev.TryGetValue((cx, cy, hc), out var prevEntry))
                    {
                        string prevAction = prevEntry.Item2;
                        if (prevAction == "pillar" || prevAction.StartsWith("mine_"))
                            inferredVx = 0f;
                        else if (prevAction == "jump" && verifyData.TryGetValue((cx, cy, false), out var prevVd))
                            inferredVx = prevVd.endVx;
                        else if (prevAction == "move" && nodeEndVx.TryGetValue((cx, cy, false), out var moveEv))
                            inferredVx = moveEv;
                        else
                            inferredVx = sign * ph.MaxRun; // fall/bridge → Full
                    }
                    else
                    {
                        inferredVx = sign * ph.MaxRun;
                    }

                    foreach (int jsign in new[] { sign, -sign })
                    {
                        // if inferred vx opposes jump direction, use 0 (can't instantly reverse)
                        float jumpVx = (inferredVx * jsign >= 0) ? jsign * Math.Abs(inferredVx) : 0f;
                        var edges = BuildJumpEdges(p, cx, cy, jsign, xMin, xMax, yMin, yMax, jumpVx);
                        foreach (var (lx, ly, frames, hold, arcClips, wallFrames, ceilFrames, endVx) in edges)
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
                                verifyData[(lx, ly, false)] = (hold, startVx: jumpVx, endVx: endVx, wallFrames, ceilFrames);
                                float h = HeuristicWeight * MinDistToGoal(goalSet, lx, ly);
                                heap.Enqueue((lx, ly, false), ng + h);
                            }
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
                            bool centerBlocked = false;
                            for (int checkY = cy - 1; checkY >= cy - rise - 2; checkY--)
                                if (Solid(cx, checkY)) { centerBlocked = true; break; }
                            bool centerOnlyClear = !centerBlocked;
                            bool leftBlocked = false;
                            if (cx - 1 >= xMin)
                            {
                                for (int checkY = cy - 1; checkY >= cy - rise - 2; checkY--)
                                    if (Solid(cx - 1, checkY) || Solid(cx, checkY)) { leftBlocked = true; break; }
                            }
                            else leftBlocked = true;
                            bool leftClear = !leftBlocked;
                            bool rightBlocked = false;
                            if (cx + 1 <= xMax)
                            {
                                for (int checkY = cy - 1; checkY >= cy - rise - 2; checkY--)
                                    if (Solid(cx, checkY) || Solid(cx + 1, checkY)) { rightBlocked = true; break; }
                            }
                            else rightBlocked = true;
                            bool rightClear = !rightBlocked;
                            if (!leftClear) continue;
                            if (excludedGoals != null && excludedGoals.Contains((cx, topY))) continue;
                            float cost = curG + 3f + rise * 6f;
                            if (cost < g.GetValueOrDefault((cx, topY, false), float.MaxValue))
                            {
                                g[(cx, topY, false)] = cost;
                                pillarTopNodes.Add((cx, topY));
                                bridgeNodes.Add((cx, topY, false));
                                AddPlanned(cx, topY);
                                prev[(cx, topY, false)] = ((cx, cy, hc), "pillar", null);
                                pillarVerifyData[(cx, topY, false)] = (leftClear, rightClear, centerOnlyClear);
                                float h = HeuristicWeight * MinDistToGoal(goalSet, cx, topY);
                                heap.Enqueue((cx, topY, false), cost + h);
                            }
                        }
                    }
                }

                if (IsStandableNode(cx, cy))
                {
                    foreach (int bsign in new[] { sign, -sign })
                    {
                        int minDtg = 20;
                        for (int col = 1; ; col++)
                        {
                            int nx = cx + bsign * col;
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
                                    AddPlanned(nx, cy);
                                    prev[(nx, cy, false)] = ((cx, cy, hc), "bridge", null);
                                    float h = HeuristicWeight * MinDistToGoal(goalSet, nx, cy);
                                    heap.Enqueue((nx, cy, false), ng + h);
                                }
                            }
                        }
                    }
                }

                if (IsStandableNode(cx, cy) && HasPlatformInInventory(p))
                {
                    foreach (int bsign in new[] { sign, -sign })
                    {
                        var placeTiles = new List<(int, int)>();
                        var placedSet = new HashSet<(int, int)>();
                        for (int col = 1; ; col++)
                        {
                            int nx = cx + bsign * col;
                            if (nx < xMin || nx > xMax) break;
                            if (Solid(nx, cy) || Solid(nx, cy - 1) || Solid(nx, cy - 2)) break;
                            int floorTile = nx; int floorY = cy + 1;
                            bool needsFloor = !IsFloor(nx, cy + 1);
                            if (needsFloor)
                            {
                                if (!CanPlacePlatformAtWith(floorTile, floorY, placedSet)) break;
                                placeTiles.Add((floorTile, floorY));
                                placedSet.Add((floorTile, floorY));
                            }
                            if (!needsFloor || Standable(nx, cy))
                            {
                                if (placeTiles.Count == 0) break; // no platform needed, regular move suffices
                                float pwCost = col * MoveCostBase + placeTiles.Count * 3f;
                                float pwNg = curG + pwCost;
                                if (pwNg < g.GetValueOrDefault((nx, cy, false), float.MaxValue))
                                {
                                    g[(nx, cy, false)] = pwNg;
                                    bridgeNodes.Add((nx, cy, false));
                                    AddPlanned(nx, cy);
                                    prev[(nx, cy, false)] = ((cx, cy, hc), "platform_walk", null);
                                    mineTilesData[(nx, cy, false)] = new List<(int, int)>(placeTiles);
                                    float h = HeuristicWeight * MinDistToGoal(goalSet, nx, cy);
                                    heap.Enqueue((nx, cy, false), pwNg + h);
                                }
                                break;
                            }
                        }
                    }
                }

                if (canJump && HasPlatformInInventory(p))
                {
                    foreach (int jsign in new[] { sign, -sign })
                    {
                        float inferredVx2;
                        if (cx == pcx && cy == feetY) inferredVx2 = startVx;
                        else if (prev.TryGetValue((cx, cy, hc), out var pEntry2))
                        {
                            string pa2 = pEntry2.Item2;
                            if (pa2 == "pillar" || pa2.StartsWith("mine_")) inferredVx2 = 0f;
                            else if (pa2 == "jump" && verifyData.TryGetValue((cx, cy, false), out var pvd2)) inferredVx2 = pvd2.endVx;
                            else if (pa2 == "move" && nodeEndVx.TryGetValue((cx, cy, false), out var moveEv2)) inferredVx2 = moveEv2;
                            else inferredVx2 = jsign * ph.MaxRun;
                        }
                        else inferredVx2 = jsign * ph.MaxRun;
                        float jumpVx2 = (inferredVx2 * jsign >= 0) ? jsign * Math.Abs(inferredVx2) : 0f;

                        var jbEdges = BuildJumpEdges(p, cx, cy, jsign, xMin, xMax, yMin, yMax, jumpVx2);
                        foreach (var (lx, ly, frames, hold, arcClips, wallFrames, ceilFrames, endVx) in jbEdges)
                        {
                            var placeTiles = new List<(int, int)>();
                            for (int fi = hold; fi < frames.Count; fi++)
                            {
                                int ftx = (int)((frames[fi].Px + p.width / 2f) / 16f);
                                int fty = (int)((frames[fi].Py + p.height) / 16f);
                                if (CanPlacePlatformAt(ftx, fty) && !placeTiles.Contains((ftx, fty)))
                                    placeTiles.Add((ftx, fty));
                            }
                            if (placeTiles.Count == 0) continue;
                            float jbCost = Math.Max(Math.Abs(lx - cx) + (JumpOverheadMax * (1f - (float)hold / HoldFrameOptions[HoldFrameOptions.Length - 1])) - Math.Max(0, cy - ly - 1) * 2f, 1f) + placeTiles.Count * 2f;
                            float jbNg = curG + jbCost;
                            if (jbNg < g.GetValueOrDefault((lx, ly, false), float.MaxValue))
                            {
                                g[(lx, ly, false)] = jbNg;
                                prev[(lx, ly, false)] = ((cx, cy, hc), "jump_bridge", frames);
                                verifyData[(lx, ly, false)] = (hold, jumpVx2, endVx, wallFrames, ceilFrames);
                                mineTilesData[(lx, ly, false)] = placeTiles;
                                bridgeNodes.Add((lx, ly, false));
                                AddPlanned(lx, ly);
                                heap.Enqueue((lx, ly, false), jbNg + HeuristicWeight * MinDistToGoal(goalSet, lx, ly));
                            }
                        }
                    }
                }

                // jump_x: horizontal arc, place platform at landing (cy-1 row), land on platform
                if (canJump && HasPlatformInInventory(p))
                {
                    foreach (int jsign in new[] { sign, -sign })
                    {
                        float jxStartVx = 0f;
                        if (cx == pcx && cy == feetY) jxStartVx = startVx;
                        else if (prev.TryGetValue((cx, cy, hc), out var pEntry3))
                        {
                            string pa3 = pEntry3.Item2;
                            if (pa3 == "jump_x" && nodeEndVx.TryGetValue((cx, cy, false), out var jxev)) jxStartVx = jxev;
                            else if (pa3 == "move" && nodeEndVx.TryGetValue((cx, cy, false), out var mev3)) jxStartVx = mev3;
                        }
                        if (jxStartVx * jsign < 0) jxStartVx = 0f; // can't instantly reverse
                        var simRes = SimAirJump(ph, cx, cy, jsign, jxStartVx);
                        int lx = simRes.landCx;
                        int ly = cy;
                        if (Math.Abs(lx - cx) < 2) continue;
                        if (lx < xMin || lx > xMax) continue;
                        if (!CanPlacePlatformAt(lx, ly)) continue;
                        float jxCost = Math.Abs(lx - cx) + 1f;
                        float jxNg = curG + jxCost;
                        if (jxNg < g.GetValueOrDefault((lx, ly, false), float.MaxValue))
                        {
                            g[(lx, ly, false)] = jxNg;
                            prev[(lx, ly, false)] = ((cx, cy, hc), "jump_x", null);
                            mineTilesData[(lx, ly, false)] = new List<(int, int)> { (lx, ly) };
                            nodeEndVx[(lx, ly, false)] = simRes.endVx;
                            bridgeNodes.Add((lx, ly, false));
                            pillarTopNodes.Add((lx, ly));
                            AddPlanned(lx, ly);
                            heap.Enqueue((lx, ly, false), jxNg + HeuristicWeight * MinDistToGoal(goalSet, lx, ly));
                        }
                    }
                }

                // jump_y: vertical jump, place platform at apex, land on it (replaces pillar for backwall envs)
                if (Standable(cx, cy) && (headClear || hc) && HasPlatformInInventory(p))
                {
                    var simY = SimAirJump(ph, cx, cy, 0);
                    int rise = simY.rise;
                    if (rise >= 2)
                    {
                        int topY = cy - rise + 1;
                        DiagLog.Write($"[plan] jump_y candidate cx={cx} cy={cy} rise={rise} topY={topY}");
                        // ensure vertical column clear from cy-1 up to topY-2 (head room while rising)
                        bool clear = true;
                        for (int y = cy - 1; y >= topY - 2; y--)
                        {
                            if (Solid(cx, y)) { clear = false; break; }
                        }
                        if (clear && CanPlacePlatformAt(cx, topY) && topY >= yMin)
                        {
                            float jyCost = 4f + (cy - topY) * 1.5f; // cheaper than pillar (3 + rise*6)
                            float jyNg = curG + jyCost;
                            if (jyNg < g.GetValueOrDefault((cx, topY, false), float.MaxValue))
                            {
                                g[(cx, topY, false)] = jyNg;
                                prev[(cx, topY, false)] = ((cx, cy, hc), "jump_y", null);
                                mineTilesData[(cx, topY, false)] = new List<(int, int)> { (cx, topY) };
                                pillarTopNodes.Add((cx, topY));
                                bridgeNodes.Add((cx, topY, false));
                                AddPlanned(cx, topY);
                                heap.Enqueue((cx, topY, false), jyNg + HeuristicWeight * MinDistToGoal(goalSet, cx, topY));
                            }
                        }
                    }
                }
            }

            if (noFallback)
            {
                DiagLog.Write($"[plan] no path to goal visited={visited.Count}");
                return "{\"path\":[],\"cost\":0}";
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
            Dictionary<(int, int, bool), (int hold, float startVx, float endVx, int wallFrames, int ceilFrames)> verifyData = null,
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
                if (path[i].action == "pillar" || path[i].action == "jump_x" || path[i].action == "jump_y")
                {
                    sb.Append(",\"swx\":").Append(path[i].swx)
                      .Append(",\"swy\":").Append(path[i].swy);
                }
                if ((path[i].action == "jump" || path[i].action == "jump_bridge") && path[i].frames != null)
                {
                    float jumpStartVx = verifyData != null && verifyData.TryGetValue((path[i].wx, path[i].wy, false), out var jvd) ? jvd.startVx : 0f;
                    sb.Append(",\"start_vx\":").Append(jumpStartVx.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
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
                if ((path[i].action == "jump_bridge" || path[i].action == "platform_walk" || path[i].action == "jump_x" || path[i].action == "jump_y") && mineTilesData != null && mineTilesData.TryGetValue((path[i].wx, path[i].wy, false), out var jbpt))
                {
                    sb.Append(",\"place_tiles\":[");
                    for (int mi = 0; mi < jbpt.Count; mi++)
                    {
                        if (mi > 0) sb.Append(',');
                        sb.Append('[').Append(jbpt[mi].Item1).Append(',').Append(jbpt[mi].Item2).Append(']');
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
                if (node.action == "jump" && verifyData != null && verifyData.TryGetValue((node.wx, node.wy, false), out var vd))
                    DiagLog.Write($"[verify] edge_emit type=jump from=({node.swx},{node.swy}) to=({node.wx},{node.wy}) hold={vd.hold} startVx={vd.startVx:0.##} endVx={vd.endVx:0.##} wall_frames={vd.wallFrames} ceil_frames={vd.ceilFrames} tick={Main.GameUpdateCount}");
                if (node.action == "pillar" && pillarVerifyData != null && pillarVerifyData.TryGetValue((node.wx, node.wy, false), out var pv))
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
