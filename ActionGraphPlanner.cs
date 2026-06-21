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

        // Frames = the forward-simulated control inputs that DEFINE this edge (Walk/Jump/Fall). execution replays
        // exactly these → planned trajectory == executed trajectory (the 99.99% fit). null for state-machine edges
        // (JumpPlace pillar / Bridge / Dig) which are driven by their own executors, not frame replay.
        public struct Edge { public int Cx, Cy; public Act Act; public float Cost; public List<PhysicsSimulator.ControlInput> Frames; }

        // costs are in ~FRAMES so all actions compare on real time. dig cost is computed per-tile (DigTable) so a
        // hard block under a weak pick is correctly expensive and A* routes around it (e.g. climbs out of a pit
        // instead of mining 25 dungeon bricks). these are the move/build estimates on the same frame scale.
        const int MoveSide = 5;   // ~16px / maxRun(3) per tile walked
        const int MoveDown = 3;   // falling a tile is cheap
        const int MoveUp = 9;     // climbing costs more than walking
        const int PlacePenalty = 18; // platform place: swing + settle wait, on the frame scale

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

        // player start State standing on cell (cx,cy): box centered on the column, feet on the (cy+1) floor top.
        static PhysicsSimulator.State CellToState(int cx, int cy)
        {
            float px = cx * 16f + 8f - PhysicsSimulator.PlayerW / 2f;
            float py = (cy + 1) * 16f - PhysicsSimulator.PlayerH;
            return new PhysicsSimulator.State { Px = px, Py = py, Vx = 0f, Vy = 0f, Grounded = true };
        }

        static int[] JumpHolds()
        {
            int max = Player.jumpHeight > 0 ? Player.jumpHeight : 15;
            var holds = new List<int>();
            for (int h = 3; h < max; h += 3) holds.Add(h);
            holds.Add(max);
            return holds.ToArray();
        }

        // forward-simulate a jump from cell (cx,cy) with (dir,hold). returns the landing cell + the exact frames IF
        // it lands. execution replays these frames → identical trajectory. this is the real edge test (no geometry).
        static (int cx, int cy, List<PhysicsSimulator.ControlInput> frames)? SimEdge(int cx, int cy, int dir, int hold, PhysicsSimulator.Params ph)
        {
            var start = CellToState(cx, cy);
            var r = PhysicsSimulator.SimulateJump(start, dir, hold, ph);
            if (!r.Landed) return null;
            if (r.Cx == cx && r.Cy == cy) return null; // didn't move
            return (r.Cx, r.Cy, r.Frames);
        }

        // generate all outgoing edges from (cx,cy). O(1)-ish per edge (tile lookups, no physics sim).
        static IEnumerable<Edge> Edges(int cx, int cy, bool canPlace)
        {
            // walk to horizontally adjacent standable cells (flat-ground walk: geometry == physics, no fake-edge
            // risk; execution drives "walk to target cell" directly, no frame replay needed).
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

            // jump: forward-simulate each (dir,hold). the landing cell IS the simulator's result (not a geometric
            // guess), and the frames are carried on the edge so execution replays the identical arc. an arc that
            // hits a wall / falls short / plunges simply lands somewhere else (or not standable) — no fake edges.
            var ph = PhysicsSimulator.Params.FromPlayer(Main.LocalPlayer);
            foreach (int dir in new[] { -1, 1 })
                foreach (int hold in JumpHolds())
                {
                    var sj = SimEdge(cx, cy, dir, hold, ph);
                    if (sj == null) continue;
                    var (nx, ny, frames) = sj.Value;
                    if (!Standable(nx, ny)) continue;           // must end on a real standable cell
                    int dxa = Math.Abs(nx - cx), dyu = cy - ny;
                    int cost = dxa * MoveSide + (dyu > 0 ? dyu * MoveUp : 0) + frames.Count / 4 + 1;
                    yield return new Edge { Cx = nx, Cy = ny, Act = Act.Jump, Cost = cost, Frames = frames };
                }

            if (canPlace)
            {
                // 2b PILLAR-UP (the key move for flat ground / pit floors): jump, place a platform under the feet,
                // land on it, repeat. Stands on what it just placed — needs NO pre-existing support, only OPEN air
                // to rise into. This is how you leave a flat floor. Each notch ≈ 2 tiles (SkillExecutor cadence).
                int pillarMax = JumpReach().dyUp;
                for (int dy = 2; dy <= pillarMax; dy += 2)
                {
                    bool clear = true;
                    for (int k = 0; k <= dy; k++) if (Block(cx, cy - k)) { clear = false; break; } // column up must be open
                    if (!clear) break;
                    yield return new Edge { Cx = cx, Cy = cy - dy, Act = Act.JumpPlace, Cost = dy * MoveUp + PlacePenalty };
                }
                // 3-1 BRIDGE: extend a platform sideways one cell, walk onto it. slides along even under a low
                // ceiling (foot + 1 head cell clear). attaches to current support or the new neighbor.
                foreach (int dir in new[] { -1, 1 })
                {
                    int nx = cx + dir;
                    if (Block(nx, cy) || Block(nx, cy - 1)) continue;          // body must fit
                    if (PathPlanner.PlatformPublic(nx, cy + 1)) continue;      // don't stack platforms
                    yield return new Edge { Cx = nx, Cy = cy, Act = Act.Bridge, Cost = MoveSide + PlacePenalty };
                }
            }

            // dig: carve into an adjacent solid cell (always available → graph stays connected). cost = real frames
            // to mine THAT tile with the current pick (hard block + weak pick = very expensive → A* routes around).
            foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                int nx = cx + dx, ny = cy + dy;
                if (nx < 1 || ny < PlayerCellH || nx >= Main.maxTilesX - 1 || ny + 1 >= Main.maxTilesY) continue;
                bool willStand = Floor(nx, ny + 1) || dy > 0; // digging down lands on what's below
                if (Block(nx, ny) && willStand)
                    yield return new Edge { Cx = nx, Cy = ny, Act = Act.Dig, Cost = DigTable.CostFrames(nx, ny) };
            }
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

        // maze trend field for the current Plan run; H reads it as the cost-to-go so A* follows the same up/diagonal
        // trend the maze gives (geometric 4-conn, walk/up/dig weighted) instead of bare manhattan, which underweighted
        // vertical progress and made A* hug the floor.
        static Dictionary<(int, int), int> _maze;

        public static Result Plan(int sx, int sy, int gx, int gy)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var res = new Result { StartCx = sx, StartCy = sy };
            var p = Main.LocalPlayer;
            bool canPlace = p != null && NavCoordinator.FindPlatformSlot(p) >= 0;

            {
                // dump the 3x4 neighborhood around the clicked goal: block(B)/platform(P)/floor(F)/air(.) per cell
                var sb2 = new System.Text.StringBuilder($"[ag-snap-dbg] click=({gx},{gy}) standable={Standable(gx, gy)}\n");
                for (int yy = gy - 2; yy <= gy + 2; yy++)
                {
                    sb2.Append($"  y{yy}:");
                    for (int xx = gx - 2; xx <= gx + 2; xx++)
                    {
                        char ch = '.';
                        if (Block(xx, yy)) ch = 'B';
                        else if (PathPlanner.PlatformPublic(xx, yy)) ch = 'P';
                        else if (Floor(xx, yy)) ch = 'F';
                        if (xx == gx && yy == gy) ch = char.ToLower(ch == '.' ? 'o' : ch);
                        sb2.Append($" {ch}");
                    }
                    sb2.Append('\n');
                }
                DiagLog.Write(sb2.ToString());
            }
            (int gx, int gy) goal = SnapGoal(gx, gy);
            res.GoalCx = goal.gx; res.GoalCy = goal.gy;
            _maze = MazeWand.BuildField(goal.gx, goal.gy, sx, sy);

            {
                // exactly answer "why doesn't it go up first?" — dump every edge out of the start cell.
                bool cp = p != null && NavCoordinator.FindPlatformSlot(p) >= 0;
                var eb = new System.Text.StringBuilder($"[ag-startedges] from=({sx},{sy}) canPlace={cp}:");
                foreach (var e in Edges(sx, sy, cp)) eb.Append($" {e.Act}->({e.Cx},{e.Cy})/{EdgeCost(sx, sy, e):0}");
                DiagLog.Write(eb.ToString());

                // DIAGNOSTIC: dump the maze-field cost up the start column (feet → 25 tiles up) to confirm H follows
                // the maze trend (cost should drop going UP toward the goal).
                var mb = new System.Text.StringBuilder($"[ag-mazecol] col={sx} (y:mazeCost) goal=({goal.gx},{goal.gy}):");
                for (int yy = sy; yy >= sy - 25; yy--)
                    mb.Append(_maze.TryGetValue((sx, yy), out int mc) ? $" {yy}:{mc}" : $" {yy}:x");
                DiagLog.Write(mb.ToString());
                _maze.TryGetValue((sx - 1, sy), out int mcl);
                _maze.TryGetValue((sx + 1, sy), out int mcr);
                _maze.TryGetValue((sx, sy), out int mc0);
                DiagLog.Write($"[ag-mazenbr] here({sx},{sy})={mc0} left={mcl} right={mcr}");
            }

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
                foreach (var e0 in Edges(cx, cy, canPlace))
                {
                    var e = e0; e.Cost = EdgeCost(cx, cy, e0);
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
            {
                var pb = new System.Text.StringBuilder("[ag-path]");
                int px = sx, py = sy;
                foreach (var e in res.Path) { pb.Append($" ({px},{py})-{e.Act}/{e.Cost:0}->({e.Cx},{e.Cy})"); px = e.Cx; py = e.Cy; }
                DiagLog.Write(pb.ToString());
            }
            return res;
        }

        // edge cost = small action bias + maze-regress penalty. moving WITH the maze trend (maze value drops toward
        // goal) costs only the bias, so A* is free to pick whatever executable action stays on-trend. moving AGAINST
        // the trend (maze value rises) is penalized per cell, so A* won't hug the floor / detour. the maze owns the
        // trend; the action graph only chooses how to execute it. dig keeps its real frame cost (hard blocks stay
        // expensive) so the planner still routes around unmineable rock instead of tunneling.
        static float EdgeCost(int fromCx, int fromCy, Edge e)
        {
            float bias = e.Act switch
            {
                Act.Dig => DigTable.CostFrames(e.Cx, e.Cy), // real mining frames, not a small bias
                Act.Walk => 1f,
                Act.Fall => 1f,
                Act.Jump => 2f,
                Act.JumpPlace => 3f,
                Act.Bridge => 3f,
                _ => 2f,
            };
            float regress = 0f;
            if (_maze != null && _maze.TryGetValue((fromCx, fromCy), out int mf) && _maze.TryGetValue((e.Cx, e.Cy), out int mt))
                regress = Math.Max(0, mt - mf); // only penalize moving away from the goal per the maze trend
            return bias + regress;
        }

        static float H(int cx, int cy, int gx, int gy)
        {
            // cost-to-go from the maze trend field (geometric, weights vertical/dig correctly). underestimates the
            // frame-scale edge cost so stays admissible, but its gradient pulls A* up/diagonal like the maze does.
            if (_maze != null && _maze.TryGetValue((cx, cy), out int mc)) return mc;
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

        // visualize the action path. Jump edges draw their REAL forward-simulated trajectory (per-frame dots, the
        // arc the player will actually fly) — not a colored block. state-machine edges (pillar/bridge/dig) and the
        // landing cells are marked as tiles. trail (SSPath dots) + tiles overlay in PathVisSystem.
        public static void Visualize(Result r)
        {
            var trail = new List<(float, float, bool)>();   // (px, py, isJump) per simulated frame
            var tiles = new List<(int, int, Color)>();      // landing cells + place/dig markers

            tiles.Add((r.StartCx, r.StartCy, Color.White));
            foreach (var e in r.Path)
            {
                if (e.Frames != null)
                {
                    // real physics trajectory: one dot per frame (foot point), green=jump arc, yellow=ground move
                    foreach (var f in e.Frames)
                        trail.Add((f.Px + PhysicsSimulator.PlayerW / 2f, f.Py + PhysicsSimulator.PlayerH, e.Act == Act.Jump));
                }
                Color c = e.Act switch
                {
                    Act.Walk => new Color(255, 230, 0),
                    Act.Jump => new Color(0, 200, 90),
                    Act.Fall => new Color(0, 150, 255),
                    Act.JumpPlace => new Color(180, 0, 255),   // pillar
                    Act.Bridge => new Color(255, 0, 200),      // bridge platform
                    Act.Dig => new Color(255, 60, 0),          // mined tile
                    _ => Color.Gray,
                };
                tiles.Add((e.Cx, e.Cy, c));
            }

            float gpx = r.GoalCx * 16f + 8f, gpy = (r.GoalCy + 1) * 16f;
            PathVisSystem.SetSSPath(trail, new List<(float, float)>(), gpx, gpy);
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
