using Microsoft.Xna.Framework;
using System.Collections.Generic;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraBlind
{
    public class MazeWand : ModItem
    {
        public override string Texture => "Terraria/Images/Item_" + ItemID.RodofDiscord;

        // cost ≈ relative TIME per cell (Dijkstra is a compass, not frame-exact). Horizontal = 3px/frame; bare fall
        // tops out at maxFall=10px/frame (gravity 0.4, ~25-frame ramp). So a downward cell costs ~1/3 of a sideways
        // cell at terminal velocity — this is what makes a long drop (jungle main shaft) beat a walk-and-dig detour.
        // Up is the slowest (climbing), so it's the dearest move. Dig is ~10× a walk and bumped further (was digging
        // too eagerly): dig only when it clearly beats routing around.
        const int MoveDown = 1, MoveSide = 3, MoveUp = 9;
        const int DigDown = 80, DigSide = 120, DigUp = 160;

        // AIR penalty: the maze is a geometric 4-connected field, so without this it抄近 straight through the sky
        // (air cell == ground cell cost). Penalize by HEIGHT above the nearest floor: low air (pits/gaps/steps) is
        // free so the field still glides over a pit without being misled into filling/detouring it; high air (a真·
        // sky shortcut) gets expensive fast and is pushed back to the ground. Tunable via the MazeWand probe.
        const int FreeAir = 3;       // air this many cells above ground is free (jump over pits / small gaps)
        const int AirQuad = 1;       // penalty grows with height SQUARED: (h-FreeAir)^2 * AirQuad. low flight stays
                                     // cheap (indicates trend) while high flight escalates fast and is pushed down.
        const int AirCap = 400;      // …but capped, so a cell over a deep abyss doesn't blow up to an absurd cost.
                                     // high enough that ~50-cell sky cruising is crushed (was 60 → flat above ~11 cells,
                                     // so high flight wasn't punished more than low; 400 ≈ caps around 23 cells up).
        // how deep below an air cell we look for ground = the deepest valley whose true depth we can MEASURE. Too
        // small and a deep canyon reads as only this-many cells of air, so its squared penalty never builds and the
        // field happily cruises over it. Must comfortably exceed the radius where AirCap saturates (~23 cells) so
        // any flight high enough to matter is actually detected as high.
        const int MaxAirProbe = 60;

        // entering a lava cell would burn the player to death — never route through it. A huge step cost makes the
        // field treat lava as effectively impassable (it'll still cross a 1-cell lava bridge only if literally no
        // other route exists, same as a very expensive dig). LiquidID/LiquidAmount API per StateSnapshotPlayer.
        const int LavaCost = 100000;
        static bool IsLava(int x, int y)
        {
            if (x < 0 || y < 0 || x >= Main.maxTilesX || y >= Main.maxTilesY) return false;
            var t = Main.tile[x, y];
            return t.LiquidAmount > 0 && t.LiquidType == LiquidID.Lava;
        }

        public override void SetDefaults()
        {
            Item.width = 28;
            Item.height = 28;
            Item.useStyle = ItemUseStyleID.Swing;
            Item.useTime = 20;
            Item.useAnimation = 20;
            Item.rare = ItemRarityID.Green;
            Item.maxStack = 1;
            Item.noMelee = true;
        }

        public override bool AltFunctionUse(Player player) => true;

        // Debug tool: left-click clears and sets point1 (the maze GOAL); right-click sets point2 (the START). When both
        // exist, run the maze field point2→point1 and draw the descended path. p1/p2 are static — this is a single-
        // instance manual probe, not the nav pipeline.
        static (int x, int y)? _p1, _p2;
        static volatile bool _mazeBusy;

        public override bool? UseItem(Player player)
        {
            if (player != Main.LocalPlayer) return null;

            int mx = (int)((Main.mouseX + Main.screenPosition.X) / 16f);
            int my = (int)((Main.mouseY + Main.screenPosition.Y) / 16f);

            if (player.altFunctionUse == 2)
                _p2 = (mx, my);
            else
            {
                _p1 = (mx, my);
                _p2 = null; // left-click resets the pair
            }
            DiagLog.Write($"[maze] p1={_p1?.ToString() ?? "-"} p2={_p2?.ToString() ?? "-"}");

            if (_p1.HasValue && _p2.HasValue)
                RunMazeAsync(_p1.Value, _p2.Value);
            return true;
        }

        //千格距离的 BuildField 耗时几百 ms — run it off the main thread so the game doesn't hitch. PlanCtx-free here
        // (BuildField has no shared scratch), and PathVisSystem.SetTiles is lock-guarded, so the bg thread can draw.
        static void RunMazeAsync((int x, int y) goal, (int x, int y) start)
        {
            if (_mazeBusy) { DiagLog.Write("[maze] busy, ignored"); return; }
            _mazeBusy = true;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    DiagLog.StartRun($"{start.x}_{start.y}__{goal.x}_{goal.y}");
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var field = BuildField(goal.x, goal.y, start.x, start.y);
                    var (path, breaks) = DescendPath(field, start.x, start.y, goal.x, goal.y);
                    DiagLog.Write($"[maze] start=({start.x},{start.y}) goal=({goal.x},{goal.y}) path={path.Count} breaks={breaks} field={field.Count} ms={sw.Elapsed.TotalMilliseconds:0} startInField={field.ContainsKey(start)}");
                    var tiles = new List<(int, int, Color)>();
                    foreach (var (x, y) in path)
                        tiles.Add((x, y, PathPlanner.IsBlockPublic(x, y) ? new Color(255, 60, 60) : new Color(40, 200, 255)));
                    PathVisSystem.SetTiles(tiles);
                    DiagLog.EndRun();
                }
                catch (System.Exception e) { DiagLog.Write($"[maze] EXC {e.Message}"); DiagLog.EndRun(); }
                finally { _mazeBusy = false; }
            });
        }

        // Geometric 2D maze (route restored 2026-06): node = every tile cell, 4-connected, physics ignored.
        // Direction-aware cost (walk cheap, dig expensive). This is the TREND field — the greedy executor reads its
        // cost gradient, not the drawn path. Dead-ends are a separate problem handled at the execution layer.
        public static Dictionary<(int, int), int> BuildField(int gx, int gy, int sx, int sy)
        {
            int minX = System.Math.Min(sx, gx) - 120, maxX = System.Math.Max(sx, gx) + 120;
            int minY = System.Math.Min(sy, gy) - 120, maxY = System.Math.Max(sy, gy) + 120;

            var dist = new Dictionary<(int, int), int>();
            var closed = new HashSet<(int, int)>();
            var pq = new SortedSet<(int cost, int x, int y)>();
            dist[(gx, gy)] = 0;
            pq.Add((0, gx, gy));

            int[] dxs = { 1, -1, 0, 0 };
            int[] dys = { 0, 0, 1, -1 };

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (pq.Count > 0)
            {
                var cur = pq.Min;
                pq.Remove(cur);
                var (cost, cx, cy) = cur;
                if (!closed.Add((cx, cy))) continue;

                for (int i = 0; i < 4; i++)
                {
                    int nx = cx + dxs[i], ny = cy + dys[i];
                    if (nx < minX || nx > maxX || ny < minY || ny > maxY) continue;
                    if (closed.Contains((nx, ny))) continue;
                    int nc = cost + StepCost(cx, cy, nx, ny);
                    if (dist.TryGetValue((nx, ny), out int old) && nc >= old) continue;
                    dist[(nx, ny)] = nc;
                    pq.Add((nc, nx, ny));
                }
            }
            DiagLog.Write($"[ss-field] dist={dist.Count} ms={sw.Elapsed.TotalMilliseconds:0}");
            return dist;
        }

        // forward cost of moving FROM (nx,ny) TO (cx,cy): direction is (cx,cy)-(nx,ny), price set by the cell
        // being entered (cx,cy). reverse BFS expands neighbor nx,ny so we cost the forward step toward goal.
        static int StepCost(int cx, int cy, int nx, int ny)
        {
            if (IsLava(cx, cy)) return LavaCost;   // entering lava = death; treat as impassable
            bool wall = PathPlanner.IsBlockPublic(cx, cy);
            bool horizontal = cx != nx;
            int baseCost;
            if (horizontal) baseCost = wall ? DigSide : MoveSide;
            else if (cy > ny) baseCost = wall ? DigDown : MoveDown;    // y+ is down
            else baseCost = wall ? DigUp : MoveUp;
            // air penalty ONLY on HORIZONTAL entry into open air — that's "flying sideways", which doesn't exist.
            // VERTICAL moves are exempt: falling is the cheap intended descent, and climbing/jumping straight up a
            // wall face has the feet briefly unsupported too — penalizing it made an 18-cell climb-around (1458)
            // cost more than digging through the wall (1440), which is backwards.
            if (!wall && horizontal) baseCost += AirCost(cx, cy);
            return baseCost;
        }

        // height of cell (cx,cy) above the nearest floor below it, mapped to a cost. probe down for a floor; cells
        // within FreeAir of the ground cost nothing (gliding over a pit), higher cells cost (height-FreeAir)*perTile.
        static int AirCost(int cx, int cy)
        {
            for (int h = 1; h <= MaxAirProbe; h++)
                if (PathPlanner.IsFloorPublic(cx, cy + h))
                    return h <= FreeAir ? 0 : System.Math.Min(AirCap, (h - FreeAir) * (h - FreeAir) * AirQuad);
            int maxH = MaxAirProbe - FreeAir;
            return System.Math.Min(AirCap, maxH * maxH * AirQuad); // no floor within probe = max height
        }

        static (List<(int, int)>, int) DescendPath(Dictionary<(int, int), int> field, int sx, int sy, int gx, int gy)
        {
            var path = new List<(int, int)>();
            int breaks = 0;
            var cur = (sx, sy);
            if (!field.ContainsKey(cur)) return (path, breaks);

            var seen = new HashSet<(int, int)>();
            for (int step = 0; step < 20000; step++)
            {
                path.Add(cur);
                if (cur == (gx, gy)) break;
                if (!seen.Add(cur)) break;
                // pick the neighbor that reconstructs the Dijkstra-optimal path: minimize (cost OF this step) +
                // (neighbor's remaining cost to goal). Using only field[n] ignores the step cost, so the greedy walk
                // can slide onto a cheap-looking neighbor via an expensive edge (e.g. a horizontal hop into deep air)
                // — drawing a route the field never actually priced that way.
                int bestTotal = int.MaxValue; var best = cur;
                foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    var n = (cur.Item1 + dx, cur.Item2 + dy);
                    if (!field.TryGetValue(n, out int dn)) continue;
                    int total = StepCost(n.Item1, n.Item2, cur.Item1, cur.Item2) + dn;
                    if (total < bestTotal) { bestTotal = total; best = n; }
                }
                if (best == cur) break;
                cur = best;
            }
            // per-step cost breakdown for a small probe (a few dozen cells with a wall in the middle): show why the
            // field picked THIS route — direction, walk vs dig, the air penalty, and the running field value.
            if (path.Count <= 3000)
            {
                int walk = 0, dig = 0;
                for (int i = 1; i < path.Count; i++)
                {
                    var (px, py) = path[i - 1];
                    var (cxk, cyk) = path[i];
                    bool wall = PathPlanner.IsBlockPublic(cxk, cyk);
                    bool down = cyk > py;
                    string dir = cxk != px ? (cxk > px ? "R" : "L") : (down ? "D" : "U");
                    int sc = StepCost(cxk, cyk, px, py);
                    int air = (!wall && !down) ? AirCost(cxk, cyk) : 0;
                    if (wall) dig++; else walk++;
                    DiagLog.Write($"[maze-step] {i} {dir} ({cxk},{cyk}) {(wall ? "DIG" : "walk")} stepCost={sc} air={air} field={field[path[i]]}");
                }
                DiagLog.Write($"[maze-detail] len={path.Count} walk={walk} dig={dig} totalCost={(field.TryGetValue((sx, sy), out int tc) ? tc : -1)}");
            }
            return (path, breaks);
        }

        static void DrawHeatmap(Dictionary<(int, int), int> field, int sx, int sy, int gx, int gy)
        {
            // normalize by the start cell's cost so the gradient spreads across the actual start→goal range;
            // cells farther than start clamp to red. (using global max washes everything green — a few far
            // dig-heavy cells blow up the scale.)
            float scale = field.TryGetValue((sx, sy), out int sc) && sc > 0 ? sc : 1f;
            var tiles = new List<(int, int, Color)>();
            foreach (var kv in field)
            {
                float t = System.Math.Min(1f, kv.Value / scale);
                var c = new Color(t, 1f - t, 0.2f) * 0.5f;
                tiles.Add((kv.Key.Item1, kv.Key.Item2, c));
            }
            DiagLog.Write($"[maze-field] fieldSize={field.Count} scale={scale} startInField={field.ContainsKey((sx, sy))}");
            PathVisSystem.SetTiles(tiles);
        }

        public override void AddRecipes()
        {
            CreateRecipe().AddIngredient(ItemID.DirtBlock, 1).AddTile(TileID.WorkBenches).Register();
        }
    }
}
