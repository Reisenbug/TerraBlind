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

        // AIR penalty: without it the geometric field cuts straight through the sky. tiny debuff only — underground has
        // background walls everywhere so flight is cheap; this just nudges toward ground and stops surface sky-cruising.
        const int FreeAir = 7;       // free below this (one jump's worth)
        const int AirSat = 10;       // h'=AirSat → half AirCap
        const int AirCap = 6;        // asymptote. tiny on purpose
        const int MaxAirProbe = 60;  // must exceed deepest valley we want to measure, else it reads shallow

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

        // J toggles rolling maze-nav toward point1 (the goal). Starts from the player's CURRENT position (the cached
        // compass is goal-keyed and valid anywhere), so it works even if you walked far from p2 after building.
        public static void ToggleNav()
        {
            Main.NewText($"[TerraBlind] J (active={StateSpacePlanner.BlockNavActive} goal=p2={(_p2.HasValue ? $"{_p2.Value.x},{_p2.Value.y}" : "none")})");
            DiagLog.Write("[maze-nav] J pressed");
            if (StateSpacePlanner.BlockNavActive) { StateSpacePlanner.BlockNavStop(); DiagLog.Write("[maze-nav] J → pause"); return; }
            // point2 is the GOAL (point1 = start marker). Block-nav: cut the path into fixed chunks, run each as a
            // plain single-point nav (can't hang). Starts from the player's current position.
            if (!_p2.HasValue) { DiagLog.Write("[maze-nav] J → no point2 (goal) set"); Main.NewText("[TerraBlind] set point2 (right-click) first"); return; }
            DiagLog.Write($"[maze-nav] J → block-nav toward p2=({_p2.Value.x},{_p2.Value.y})");
            StateSpacePlanner.BlockNavStart(_p2.Value.x, _p2.Value.y);
        }

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

            // p2 = goal, p1 = start. Flood the field FROM p2 (the goal) and cache it keyed on p2, so pressing J reuses
            // this very field instead of rebuilding. Player is expected to stay near p1 (inside the field's box).
            if (_p1.HasValue && _p2.HasValue)
                RunMazeAsync(_p2.Value, _p1.Value);
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
                    var field = BuildField(goal.x, goal.y, start.x, start.y, bigMargin: true);
                    _cachedField = field; _cachedGoal = (goal.x, goal.y);   // reuse on J (GetField(p2) hits this)
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
        // CACHED whole-region field, keyed by goal. The field is the EXPENSIVE part (seconds for a cross-map flood),
        // but it's a全图 compass valid from anywhere — so rolling A* builds it ONCE per goal and every leg reuses it
        // as the heuristic. Local terrain edits (a few dug tiles) don't change the大方向, so we DON'T rebuild on them;
        // only a new goal triggers a rebuild. Returns the same dictionary instance — callers must treat it read-only.
        // big margin around the goal↔start span so the cached compass still covers the player after drift / running
        // off; large enough for cross-map routes without flooding the entire 5M-cell world (memory + time).
        const int FieldMargin = 400;   // TEMP small for rolling validation (1500 = cross-map but builds on main thread
                                       // ~5s = hitch; real fix is off-thread field build). Keep goals within range for now.
        static (int gx, int gy) _cachedGoal = (int.MinValue, int.MinValue);
        static Dictionary<(int, int), int> _cachedField;
        public static Dictionary<(int, int), int> GetField(int gx, int gy)
        {
            var p = Main.LocalPlayer;
            int sx = p != null ? (int)(p.Center.X / 16f) : gx;
            int sy = p != null ? (int)((p.position.Y + p.height) / 16f) - 1 : gy;
            if (_cachedField != null && _cachedGoal == (gx, gy)) return _cachedField;
            _cachedField = BuildField(gx, gy, sx, sy, bigMargin: true);
            _cachedGoal = (gx, gy);
            return _cachedField;
        }
        public static void InvalidateField() { _cachedField = null; _cachedGoal = (int.MinValue, int.MinValue); }

        public static Dictionary<(int, int), int> BuildField(int gx, int gy, int sx, int sy, bool bigMargin = false)
        {
            int m = bigMargin ? FieldMargin : 120;
            int minX = System.Math.Max(0, System.Math.Min(sx, gx) - m), maxX = System.Math.Min(Main.maxTilesX - 1, System.Math.Max(sx, gx) + m);
            int minY = System.Math.Max(0, System.Math.Min(sy, gy) - m), maxY = System.Math.Min(Main.maxTilesY - 1, System.Math.Max(sy, gy) + m);

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

        // height above the nearest floor below, mapped to a cost. FreeAir tiles are free (gliding over a small pit).
        // Above that the penalty SATURATES: AirCap * h'/(h'+AirSat). Low h' is区分得开 (near-linear), high h' all
        // trends to AirCap so "40 deep" and "100 deep" read the same (both = a fall you can't return from). Smooth,
        // no threshold, no hard-cap cliff — AirCap is an asymptote, not a cutoff.
        static int AirCost(int cx, int cy)
        {
            int h = MaxAirProbe;
            for (int d = 1; d <= MaxAirProbe; d++)
                if (PathPlanner.IsFloorPublic(cx, cy + d)) { h = d; break; }
            if (h <= FreeAir) return 0;
            float hp = h - FreeAir;
            return (int)(AirCap * hp / (hp + AirSat));
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
