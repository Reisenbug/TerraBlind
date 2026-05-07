using System;
using System.Collections.Generic;
using System.Text;
using Terraria;

namespace TerraBlind
{
    public static class PathPlanner
    {
        private const int GoalRange = 40;
        private const int MaxBridge = 25;

        private static int[] _envelopeCache;
        public static int[] GetEnvelopeCache() => _envelopeCache;

        public static bool SolidPublic(int wx, int wy) => Solid(wx, wy);

        private static bool Solid(int wx, int wy)
        {
            if (wx < 0 || wy < 0 || wx >= Main.maxTilesX || wy >= Main.maxTilesY) return true;
            var t = Main.tile[wx, wy];
            return t != null && t.HasTile && Main.tileSolid[t.TileType] && !Main.tileSolidTop[t.TileType];
        }

        private static bool Standable(int wx, int wy)
        {
            return !Solid(wx, wy) && Solid(wx, wy + 1);
        }

        private static int DistToGround(int wx, int wy, int maxDepth = 20)
        {
            for (int d = 0; d < maxDepth; d++)
                if (Solid(wx, wy + d)) return d;
            return maxDepth;
        }

        private static int[] BuildEnvelope(Player p, int maxCols)
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

            var env = new int[maxCols];
            for (int col = 0; col < maxCols; col++)
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
                env[col] = (int)(-risePx / 16f);
            }
            return env;
        }

        private static float BridgePenalty(int dtg)
        {
            if (dtg >= 8) return 0;
            return (8 - dtg) * 2;
        }

        public static string Plan(int sign)
        {
            var p = Main.LocalPlayer;
            if (p == null || !p.active) return "{\"error\":\"no_player\"}";

            int pcx = (int)((p.position.X + p.width / 2f) / 16f);
            int feetY = (int)((p.position.Y + p.height) / 16f);
            while (Solid(pcx, feetY) && feetY > 0) feetY--;

            int maxJumpCols = 9;
            var envelope = BuildEnvelope(p, maxJumpCols + 1);
            _envelopeCache = envelope;

            int xMin = pcx - GoalRange;
            int xMax = pcx + GoalRange;
            int yMin = feetY - 20;
            int yMax = feetY + 15;

            int goalX = -1, goalY = -1;
            int startWx = sign > 0 ? xMax : xMin;
            int endWx   = sign > 0 ? xMin : xMax;
            for (int wx = startWx; sign > 0 ? wx >= endWx : wx <= endWx; wx -= sign)
            {
                if (sign * (wx - pcx) <= 0) continue;
                for (int wy = yMin; wy <= yMax; wy++)
                {
                    if (Standable(wx, wy))
                    {
                        if (goalX < 0) { goalX = wx; goalY = wy; }
                        break;
                    }
                }
                if (goalX >= 0) break;
            }
            if (goalX == -1)
            {
                DiagLog.Write("[plan] no goal found");
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

                foreach (var (dx, dy) in new[] { (1,0),(-1,0),(0,1),(0,-1) })
                {
                    int nx = cx + dx, ny = cy + dy;
                    if (nx < xMin || nx > xMax || ny < yMin || ny > yMax) continue;
                    if (Solid(nx, ny)) continue;
                    if (dy == -1 && dx == 0) continue;
                    if (dy == -1 && !Solid(nx, ny + 1)) continue;
                    int dtg = dx != 0 ? DistToGround(nx, ny) : 0;
                    float cost = dy == 1 ? 0.5f : 1f + dtg;
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
                        for (int col = 1; col < envelope.Length; col++)
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
                            for (int ny = cy + arcDy; ny < Math.Min(cy + arcDy + 4, yMax + 1); ny++)
                            {
                                if (Standable(nx, ny))
                                {
                                    int rise = cy - ny;
                                    float riseBonus = Math.Max(0, rise - 1) * 2f;
                                    float cost = Math.Max(4f + col - riseBonus, 1f);
                                    float ng = curG + cost;
                                    if (ng < g.GetValueOrDefault((nx, ny), float.MaxValue))
                                    {
                                        g[(nx, ny)] = ng;
                                        prev[(nx, ny)] = ((cx, cy), "jump");
                                        float h = Math.Abs(goalX - nx) + Math.Abs(goalY - ny);
                                        heap.Enqueue((nx, ny), ng + h);
                                    }
                                    break;
                                }
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
                        if (Solid(nx, cy)) break;
                        minDtg = Math.Min(minDtg, DistToGround(nx, cy));
                        int bridgeLandY = -1;
                        for (int dy = -7; dy <= 8; dy++) { if (Standable(nx, cy + dy)) { bridgeLandY = cy + dy; break; } }
                        if (bridgeLandY >= 0 && !Solid(nx, bridgeLandY - 1) && !Solid(nx, bridgeLandY - 2))
                        {
                            float cost = 4f + col * 2f + BridgePenalty(minDtg);
                            float ng = curG + cost;
                            if (ng < g.GetValueOrDefault((nx, bridgeLandY), float.MaxValue))
                            {
                                g[(nx, bridgeLandY)] = ng;
                                bridgeNodes.Add((nx, bridgeLandY));
                                prev[(nx, bridgeLandY)] = ((cx, cy), "bridge");
                                float h = Math.Abs(goalX - nx) + Math.Abs(goalY - bridgeLandY);
                                heap.Enqueue((nx, bridgeLandY), ng + h);
                            }
                        }
                    }
                }
            }

            (int, int) best = start;
            int bestFwd = 0;
            foreach (var kv in g)
            {
                var (wx, wy) = kv.Key;
                int fwd = sign * (wx - pcx);
                if (fwd <= 0) continue;
                if (!Standable(wx, wy)) continue;
                if (fwd > bestFwd) { bestFwd = fwd; best = (wx, wy); }
            }
            if (best == start)
            {
                DiagLog.Write($"[plan] no forward standable visited={visited.Count}");
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
            sb.Append("],\"cost\":").Append(cost.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)).Append('}');
            DiagLog.Write($"[plan] path len={path.Count} cost={cost:0.#}");
            return sb.ToString();
        }
    }
}
