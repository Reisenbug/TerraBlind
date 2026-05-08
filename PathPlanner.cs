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
        private const int CanProgressK = 1;     // goal validity: at least K tiles ahead must be reachable
        private const int JumpMinCol = 2;       // minimum horizontal tiles per jump, filters trivial hops
        private const float JumpOverheadMax = 4f;    // extra cost for short jumps; full-range jump = 0, min jump = JumpOverheadMax
        private const float BridgeCostBase = 4f;     // fixed cost to start a bridge
        private const float BridgeCostPerCol = 2f;   // cost per tile extended in a bridge
        private const float FallCost = 0.5f;         // cost per fall tile, cheaper than move to encourage natural drops
        private const float MoveCostBase = 1f;       // base cost per move tile, plus distance-to-ground penalty

        private static int[] _envelopeCache;
        public static int[] GetEnvelopeCache() => _envelopeCache;

        public static bool SolidPublic(int wx, int wy) => Solid(wx, wy);
        public static bool PlatformPublic(int wx, int wy) => Platform(wx, wy);

        private static bool Solid(int wx, int wy)
        {
            if (wx < 0 || wy < 0 || wx >= Main.maxTilesX || wy >= Main.maxTilesY) return true;
            var t = Main.tile[wx, wy];
            return t != null && t.HasTile && Main.tileSolid[t.TileType] && !Main.tileSolidTop[t.TileType];
        }

        private static bool Platform(int wx, int wy)
        {
            if (wx < 0 || wy < 0 || wx >= Main.maxTilesX || wy >= Main.maxTilesY) return false;
            var t = Main.tile[wx, wy];
            return t != null && t.HasTile && Main.tileSolidTop[t.TileType];
        }

        private static bool Standable(int wx, int wy)
        {
            return !Solid(wx, wy) && !Platform(wx, wy) && (Solid(wx, wy + 1) || Platform(wx, wy + 1));
        }

        private static int DistToGround(int wx, int wy, int maxDepth = 20)
        {
            for (int d = 0; d < maxDepth; d++)
                if (Solid(wx, wy + d)) return d;
            return maxDepth;
        }

        private static int[] BuildEnvelope(Player p, int maxDropTiles = 20)
        {
            float js = Player.jumpSpeed;
            float grav = p.gravity > 0f ? p.gravity : 0.4f;
            int jh = Player.jumpHeight;
            float vx = Math.Max(p.maxRunSpeed, p.accRunSpeed);

            float holdSpeed = js - grav;
            float phase1Ticks = jh + 1;
            float phase2Ticks = holdSpeed / grav;
            float peakT = phase1Ticks + phase2Ticks;
            float peakRisePx = holdSpeed * phase1Ticks + holdSpeed * phase2Ticks - 0.5f * grav * phase2Ticks * phase2Ticks;

            var envList = new System.Collections.Generic.List<int>();
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
                if (dy >= maxDropTiles) break;
            }
            return envList.ToArray();
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
                if (!Solid(cx, cy + 1) && !visited.Contains((cx, cy + 1)))
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

        public static string Plan(int sign, System.Collections.Generic.HashSet<(int, int)> excludedGoals = null)
        {
            var p = Main.LocalPlayer;
            if (p == null || !p.active) return "{\"error\":\"no_player\"}";

            int pcx = (int)((p.position.X + p.width / 2f) / 16f);
            int feetY = (int)((p.position.Y + p.height) / 16f);
            while (Solid(pcx, feetY) && feetY > 0) feetY--;
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

            var envelope = BuildEnvelope(p, 50);
            _envelopeCache = envelope;

            int xMin = sign > 0 ? pcx - GoalRangeBack : pcx - GoalRangeFwd;
            int xMax = sign > 0 ? pcx + GoalRangeFwd : pcx + GoalRangeBack;
            int yMin = feetY - AStarScanUp;
            int yMax = feetY + AStarScanDown;

            int goalX = -1, goalY = -1;
            var rejectedGoals = new System.Text.StringBuilder();
            for (int wx = xMin; wx <= xMax; wx++)
            {
                if (sign * (wx - pcx) < MinGoalDist) continue;
                for (int wy = yMin; wy <= yMax; wy++)
                {
                    if (!Standable(wx, wy)) continue;
                    bool excluded = excludedGoals != null && excludedGoals.Contains((wx, wy));
                    if (excluded) { rejectedGoals.Append($"{{\"wx\":{wx},\"wy\":{wy},\"reason\":\"excluded\"}},"); break; }
                    bool ok = CanProgress(wx, wy, sign, CanProgressK);
                    if (!ok) { rejectedGoals.Append($"{{\"wx\":{wx},\"wy\":{wy},\"reason\":\"no_progress\"}},"); break; }
                    int distWx = Math.Abs(wx - pcx);
                    int distGoal = goalX < 0 ? int.MaxValue : Math.Abs(goalX - pcx);
                    bool higher = wy < goalY;
                    bool closerOrSameHeight = distWx <= distGoal || wy <= goalY;
                    if (goalX < 0 || (higher && closerOrSameHeight)) { goalX = wx; goalY = wy; }
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
            var prev = new Dictionary<(int, int), ((int, int), string)>();
            var visited = new HashSet<(int, int)>();
            var bridgeNodes = new HashSet<(int, int)>();
            var heap = new PriorityQueue<(int wx, int wy), float>();

            var start = (pcx, feetY);
            g[start] = 0f;
            prev[start] = ((-1, -1), "");
            heap.Enqueue((pcx, feetY), Math.Abs(goalX - pcx) + Math.Abs(goalY - feetY));

            while (heap.Count > 0)
            {
                var (cx, cy) = heap.Dequeue();

                if (visited.Contains((cx, cy))) continue;
                visited.Add((cx, cy));

                if (cx == goalX && cy == goalY)
                    return BuildResult(prev, g, goalX, goalY, start);

                float curG = g.TryGetValue((cx, cy), out var cg) ? cg : float.MaxValue;

                foreach (var (dx, dy) in new[] { (1,0),(-1,0),(0,1),(0,-1),(1,-1),(-1,-1) })
                {
                    int nx = cx + dx, ny = cy + dy;
                    if (nx < xMin || nx > xMax || ny < yMin || ny > yMax) continue;
                    if (Solid(nx, ny)) continue;
                    if (dy == -1 && dx == 0) continue;
                    if (dy == -1 && !Solid(nx, ny + 1) && !Platform(nx, ny + 1)) continue;
                    if (dy == -1 && dx != 0 && !Standable(nx, ny)) continue;
                    int dtg = dx != 0 ? DistToGround(nx, ny) : 0;
                    float cost = dy == 1 ? FallCost : MoveCostBase + dtg;
                    float ng = curG + cost;
                    if (ng < g.GetValueOrDefault((nx, ny), float.MaxValue))
                    {
                        g[(nx, ny)] = ng;
                        string action = dy == 1 ? "fall" : "move";
                        prev[(nx, ny)] = ((cx, cy), action);
                        float h = Math.Abs(goalX - nx) + Math.Abs(goalY - ny);
                        heap.Enqueue((nx, ny), ng + h);
                    }
                }

                bool canJump = (Standable(cx, cy) || bridgeNodes.Contains((cx, cy)))
                    && !Solid(cx, cy - 1) && !Solid(cx, cy - 2);
                if (canJump)
                {
                    foreach (int js in new[] { sign })
                    {
                        for (int col = JumpMinCol; col < envelope.Length; col++)
                        {
                            int nx = cx + js * col;
                            if (nx < xMin || nx > xMax) break;
                            int arcDy = envelope[col];
                            bool blocked = false;
                            for (int i = 1; i < col; i++)
                            {
                                int arcY = cy + envelope[i];
                                int bx = cx + js * i;
                                if (Solid(bx, arcY) || Solid(bx, arcY - 1)) { blocked = true; break; }
                            }
                            if (blocked) break;
                            int ny = cy + arcDy;
                            if (ny >= yMin && ny <= yMax && Standable(nx, ny))
                            {
                                {
                                    int rise = cy - ny;
                                    float riseBonus = Math.Max(0, rise - 1) * 2f;
                                    int maxCol = envelope.Length - 1;
                                    float efficiency = maxCol > 0 ? (float)col / maxCol : 1f;
                                    float jumpOverhead = JumpOverheadMax * (1f - efficiency);
                                    float cost = Math.Max(col + jumpOverhead - riseBonus, 1f);
                                    float ng = curG + cost;
                                    if (ng < g.GetValueOrDefault((nx, ny), float.MaxValue))
                                    {
                                        g[(nx, ny)] = ng;
                                        prev[(nx, ny)] = ((cx, cy), "jump");
                                        float h = Math.Abs(goalX - nx) + Math.Abs(goalY - ny);
                                        heap.Enqueue((nx, ny), ng + h);
                                    }
                                }
                            }
                        }
                    }
                }

                if (Standable(cx, cy) && !Solid(cx, cy - 1) && !Solid(cx, cy - 2))
                {
                    for (int topY = cy - 1; topY >= yMin; topY--)
                    {
                        if (Solid(cx, topY)) break;
                        if (Standable(cx, topY))
                        {
                            int rise = cy - topY;
                            float cost = curG + 3f + rise;
                            if (cost < g.GetValueOrDefault((cx, topY), float.MaxValue))
                            {
                                g[(cx, topY)] = cost;
                                bridgeNodes.Add((cx, topY));
                                prev[(cx, topY)] = ((cx, cy), "pillar");
                                float h = Math.Abs(goalX - cx) + Math.Abs(goalY - topY);
                                heap.Enqueue((cx, topY), cost + h);
                            }
                            break;
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
                        if (!Solid(nx, cy + 1) || Standable(nx, cy))
                        {
                            float cost = BridgeCostBase + col * BridgeCostPerCol + BridgePenalty(minDtg);
                            float ng = curG + cost;
                            if (ng < g.GetValueOrDefault((nx, cy), float.MaxValue))
                            {
                                g[(nx, cy)] = ng;
                                bridgeNodes.Add((nx, cy));
                                prev[(nx, cy)] = ((cx, cy), "bridge");
                                float h = Math.Abs(goalX - nx) + Math.Abs(goalY - cy);
                                heap.Enqueue((nx, cy), ng + h);
                            }
                        }
                    }
                }
            }

            (int, int) best = start;
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
            if (best == start || bestFwd < MinGoalDist)
            {
                DiagLog.Write($"[plan] no usable fallback bestFwd={bestFwd} visited={visited.Count}");
                DiagLog.WriteEvent($"{{\"e\":\"plan_failed\",\"tick\":{Main.GameUpdateCount},\"reason\":\"no_fallback\",\"px\":{pcx},\"py\":{feetY},\"candidates_rejected\":[]}}");
                return "{\"path\":[],\"cost\":0}";
            }
            DiagLog.Write($"[plan] fallback→({best.Item1},{best.Item2}) visited={visited.Count}");
            return BuildResult(prev, g, best.Item1, best.Item2, start);
        }

        private static string BuildResult(
            Dictionary<(int, int), ((int, int), string)> prev,
            Dictionary<(int, int), float> g,
            int wx, int wy, (int, int) start)
        {
            var path = new List<(int wx, int wy, string action)>();
            var pos = (wx, wy);
            while (prev.TryGetValue(pos, out var entry) && entry.Item1 != (-1, -1))
            {
                path.Add((pos.Item1, pos.Item2, entry.Item2));
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
                  .Append(",\"action\":\"").Append(path[i].action).Append("\"}");
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
    }
}
