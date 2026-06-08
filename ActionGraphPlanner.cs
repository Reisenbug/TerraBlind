using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;

namespace TerraBlind
{
    // Full A* over an action graph. Nodes = standable cells (player box fits, has/can-make support). Edges = real
    // moves: walk / jump / fall / jump-place / bridge / dig. Edge existence uses O(1) coarse geometric checks
    // (no per-edge physics sim — that stays in the executor), so building is cheap and A* with a heuristic stays
    // fast. Dig edges keep the graph always-connected → any reachable-by-digging goal is found. Reach limits come
    // from the player's CURRENT movement stats (not hardcoded) so accessories scale automatically.
    public static class ActionGraphPlanner
    {
        public enum Act { Walk, Jump, Fall, JumpPlace, Bridge, Dig }

        public struct Edge { public int Cx, Cy; public Act Act; public float Cost; }

        // maze cost scale (carried over verbatim — proven good)
        const int MoveDown = 1, MoveSide = 1, MoveUp = 3;
        const int DigDown = 20, DigSide = 30, DigUp = 40;
        const int PlacePenalty = 10; // jump-place / bridge surcharge: > walk, < dig (build sparingly, dig rarer)

        const int PlayerCellH = 3; // 42px ≈ 2.6 tiles: foot cell + 2 head cells must clear

        static bool Block(int x, int y) => PathPlanner.IsBlockPublic(x, y);
        static bool Floor(int x, int y) => PathPlanner.IsFloorPublic(x, y);

        // a cell is standable if the player box fits (foot + 2 head cells clear) and something supports the feet.
        static bool Standable(int cx, int cy)
        {
            if (cx < 1 || cy < PlayerCellH || cx >= Main.maxTilesX - 1 || cy + 1 >= Main.maxTilesY) return false;
            for (int k = 0; k < PlayerCellH; k++) if (Block(cx, cy - k)) return false;
            return Floor(cx, cy + 1);
        }

        static (int dxMax, int dyUp) JumpReach()
        {
            var p = Main.LocalPlayer;
            float js = 5.01f, grav = 0.4f, maxRun = 3.0f; int hold = 15;
            if (p != null)
            {
                maxRun = p.maxRunSpeed > 0 ? p.maxRunSpeed : maxRun;
                grav = p.gravity > 0 ? p.gravity : grav;
                js = Player.jumpSpeed;                       // static: global jump speed incl. accessory bonuses
                if (Player.jumpHeight > 0) hold = Player.jumpHeight; // static: hold-frame cap
            }
            float riseVy = js - grav;                       // hold-phase upward speed
            float risePx = riseVy * hold;                   // px climbed during hold
            float apexExtra = (riseVy * riseVy) / (2f * grav); // coast to apex after release
            int dyUp = (int)Math.Ceiling((risePx + apexExtra) / 16f) + 1;
            int airFrames = (int)(2 * (riseVy / grav)) + hold; // rough total airborne frames
            int dxMax = (int)Math.Ceiling(maxRun * airFrames / 16f);
            return (Math.Min(dxMax, 12), Math.Min(dyUp, 9));   // clamp to sane caps
        }

        const int FallMax = 25;

        // generate all outgoing edges from (cx,cy). O(1)-ish per edge (tile lookups, no physics sim).
        static IEnumerable<Edge> Edges(int cx, int cy, bool canPlace)
        {
            // walk to horizontally adjacent standable cells
            foreach (int dir in new[] { -1, 1 })
                if (Standable(cx + dir, cy))
                    yield return new Edge { Cx = cx + dir, Cy = cy, Act = Act.Walk, Cost = MoveSide };

            // fall: drop straight (or diagonally by one) onto a lower standable cell, column clear
            foreach (int dir in new[] { -1, 0, 1 })
            {
                int col = cx + dir;
                for (int dy = 1; dy <= FallMax; dy++)
                {
                    if (Block(col, cy + dy)) break;
                    if (Standable(col, cy + dy)) { yield return new Edge { Cx = col, Cy = cy + dy, Act = Act.Fall, Cost = dy * MoveDown + (dir != 0 ? MoveSide : 0) }; break; }
                }
            }

            // jump: to a higher/level standable cell within reach, arc roughly clear (head clear along the way)
            var (dxMax, dyUp) = JumpReach();
            for (int dx = -dxMax; dx <= dxMax; dx++)
                for (int dy = -dyUp; dy <= 2; dy++)
                {
                    if (dx == 0 && dy >= 0) continue;            // straight-up handled, level handled by walk
                    int nx = cx + dx, ny = cy + dy;
                    if (!Standable(nx, ny)) continue;
                    if (!ArcClear(cx, cy, nx, ny)) continue;
                    int cost = Math.Abs(dx) * MoveSide + (dy < 0 ? -dy * MoveUp : 0) + 1;
                    yield return new Edge { Cx = nx, Cy = ny, Act = Act.Jump, Cost = cost };
                }

            if (canPlace)
            {
                // jump-place: build a platform up to dyUp above, land on it. needs head clearance at destination.
                for (int dy = 1; dy <= Math.Min(dyUp, 6); dy++)
                {
                    int ny = cy - dy;
                    if (Block(cx, ny + 1)) break;               // column blocked
                    if (CanBuildStand(cx, ny)) { yield return new Edge { Cx = cx, Cy = ny, Act = Act.JumpPlace, Cost = dy * MoveUp + PlacePenalty }; }
                }
                // bridge: extend a platform sideways one cell (must have support to attach + head clear there)
                foreach (int dir in new[] { -1, 1 })
                {
                    int nx = cx + dir;
                    if (CanBuildStand(nx, cy) && (Floor(cx, cy + 1) || Floor(nx, cy + 1)))
                        yield return new Edge { Cx = nx, Cy = cy, Act = Act.Bridge, Cost = MoveSide + PlacePenalty };
                }
            }

            // dig: carve into an adjacent solid cell (always available → graph stays connected). 4-dir.
            foreach (var (dx, dy, c) in new[] { (1, 0, DigSide), (-1, 0, DigSide), (0, 1, DigDown), (0, -1, DigUp) })
            {
                int nx = cx + dx, ny = cy + dy;
                if (nx < 1 || ny < PlayerCellH || nx >= Main.maxTilesX - 1 || ny + 1 >= Main.maxTilesY) continue;
                // after digging, the cell must become standable (dig the head cells too if needed is a future edge)
                bool willStand = Floor(nx, ny + 1) || dy > 0; // digging down lands on what's below
                if (Block(nx, ny) && willStand)
                    yield return new Edge { Cx = nx, Cy = ny, Act = Act.Dig, Cost = c };
            }
        }

        // platform-build destination: empty (or cuttable) target, head clear for the player box, not stacking on
        // an existing platform, and some real neighbor gives attach support.
        static bool CanBuildStand(int cx, int cy)
        {
            if (cx < 1 || cy < PlayerCellH || cx >= Main.maxTilesX - 1 || cy + 1 >= Main.maxTilesY) return false;
            for (int k = 0; k < PlayerCellH; k++) if (Block(cx, cy - k)) return false; // head/body room
            // need attach support in the support row neighborhood (a real tile adjacent to the platform we'd place)
            for (int ax = -1; ax <= 1; ax++)
            {
                var nb = Main.tile[cx + ax, cy + 1];
                if (nb != null && nb.HasTile) return true;
            }
            return false;
        }

        // rough arc clearance: every tile column between start and end (at the player's head rows) is open enough.
        // coarse: require the destination foot column and the start column head cells clear along the vertical span.
        static bool ArcClear(int x0, int y0, int x1, int y1)
        {
            int hi = Math.Min(y0, y1) - (PlayerCellH - 1);
            int lo = Math.Max(y0, y1);
            int stepX = x1 > x0 ? 1 : (x1 < x0 ? -1 : 0);
            for (int x = x0; ; x += stepX)
            {
                for (int y = hi; y <= lo; y++)
                    if (Block(x, y)) { if (x == x1 && y > y1) continue; return false; }
                if (x == x1) break;
                if (stepX == 0) break;
            }
            return true;
        }

        public class Result
        {
            public bool Found;
            public List<Edge> Path = new();
            public int Expansions;
            public double Millis;
            public int StartCx, StartCy, GoalCx, GoalCy;
        }

        const int MaxExpansions = 60000;

        public static Result Plan(int sx, int sy, int gx, int gy)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var res = new Result { StartCx = sx, StartCy = sy };
            var p = Main.LocalPlayer;
            bool canPlace = p != null && NavCoordinator.FindPlatformSlot(p) >= 0;

            (int gx, int gy) goal = SnapGoal(gx, gy);
            res.GoalCx = goal.gx; res.GoalCy = goal.gy;

            var g = new Dictionary<(int, int), float> { [(sx, sy)] = 0 };
            var came = new Dictionary<(int, int), (int, int, Edge)>();
            var closed = new HashSet<(int, int)>();
            var open = new PriorityQueue<(int, int), float>();
            open.Enqueue((sx, sy), H(sx, sy, goal.gx, goal.gy));

            bool found = false;
            while (open.Count > 0 && res.Expansions < MaxExpansions)
            {
                var (cx, cy) = open.Dequeue();
                if (!closed.Add((cx, cy))) continue;
                if (cx == goal.gx && cy == goal.gy) { found = true; break; }
                res.Expansions++;
                float cg = g[(cx, cy)];
                foreach (var e in Edges(cx, cy, canPlace))
                {
                    var nk = (e.Cx, e.Cy);
                    if (closed.Contains(nk)) continue;
                    float ng = cg + e.Cost;
                    if (g.TryGetValue(nk, out float old) && ng >= old) continue;
                    g[nk] = ng;
                    came[nk] = (cx, cy, e);
                    open.Enqueue(nk, ng + H(e.Cx, e.Cy, goal.gx, goal.gy));
                }
            }

            if (found)
            {
                var k = (goal.gx, goal.gy);
                while (k != (sx, sy) && came.TryGetValue(k, out var pe))
                {
                    res.Path.Add(pe.Item3);
                    k = (pe.Item1, pe.Item2);
                }
                res.Path.Reverse();
            }
            res.Found = found;
            res.Expansions = res.Expansions;
            sw.Stop();
            res.Millis = sw.Elapsed.TotalMilliseconds;
            DiagLog.Write($"[ag-plan] start=({sx},{sy}) goal=({goal.gx},{goal.gy}) found={found} exp={res.Expansions} ms={res.Millis:0.#} pathLen={res.Path.Count} canPlace={canPlace}");
            return res;
        }

        static float H(int cx, int cy, int gx, int gy)
        {
            // admissible-ish: manhattan with the cheapest move costs (down/side = 1)
            int dx = Math.Abs(cx - gx), dy = Math.Abs(cy - gy);
            return dx * MoveSide + dy * MoveDown;
        }

        const int GoalSnapMaxDrop = 60;
        static (int gx, int gy) SnapGoal(int gx, int gy)
        {
            for (int d = 0; d <= GoalSnapMaxDrop; d++)
                if (Standable(gx, gy + d)) return (gx, gy + d);
            return (gx, gy);
        }

        // visualize the action path: color tiles by act, mark placements/digs.
        public static void Visualize(Result r)
        {
            var tiles = new List<(int, int, Color)>();
            int cx = r.StartCx, cy = r.StartCy;
            tiles.Add((cx, cy, Color.White));
            foreach (var e in r.Path)
            {
                Color c = e.Act switch
                {
                    Act.Walk => new Color(255, 230, 0),
                    Act.Jump => new Color(0, 200, 90),
                    Act.Fall => new Color(0, 150, 255),
                    Act.JumpPlace => new Color(180, 0, 255),
                    Act.Bridge => new Color(255, 0, 200),
                    Act.Dig => new Color(255, 60, 0),
                    _ => Color.Gray,
                };
                tiles.Add((e.Cx, e.Cy, c));
                cx = e.Cx; cy = e.Cy;
            }
            tiles.Add((r.GoalCx, r.GoalCy, Color.Red));
            PathVisSystem.SetTiles(tiles);
        }

        public static void PlanAndVisualize(int gx, int gy)
        {
            var p = Main.LocalPlayer;
            if (p == null || !p.active) return;
            int sx = (int)((p.Center.X) / 16f);
            int sy = (int)((p.position.Y + p.height) / 16f) - 1;
            var r = Plan(sx, sy, gx, gy);
            Visualize(r);
        }
    }
}
