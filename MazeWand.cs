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

        const int MoveDown = 1, MoveSide = 1, MoveUp = 3;
        const int DigDown = 20, DigSide = 30, DigUp = 40;

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

        public override bool? UseItem(Player player)
        {
            if (player != Main.LocalPlayer) return null;

            int gx = (int)((Main.mouseX + Main.screenPosition.X) / 16f);
            int gy = (int)((Main.mouseY + Main.screenPosition.Y) / 16f);
            int sx = (int)((player.Center.X) / 16f);
            int sy = (int)((player.position.Y + player.height) / 16f) - 1;

            var (path, breaks) = Solve(sx, sy, gx, gy);
            DiagLog.Write($"[maze] start=({sx},{sy}) goal=({gx},{gy}) path={path.Count} breaks={breaks}");

            var tiles = new List<(int, int, Color)>();
            foreach (var (x, y) in path)
                tiles.Add((x, y, PathPlanner.IsBlockPublic(x, y) ? new Color(255, 60, 60) : new Color(40, 200, 255)));
            PathVisSystem.SetTiles(tiles);
            return true;
        }

        // Dijkstra on the raw tile grid: every cell a node, 4-connected. Entering air costs 1, entering a wall
        // costs WallCost — so total = breaks*WallCost + steps is minimized lexicographically (fewest breaks, then
        // shortest). Physics ignored; this is a pure 2D maze.
        static (List<(int, int)>, int) Solve(int sx, int sy, int gx, int gy)
        {
            int minX = System.Math.Min(sx, gx) - 120, maxX = System.Math.Max(sx, gx) + 120;
            int minY = System.Math.Min(sy, gy) - 120, maxY = System.Math.Max(sy, gy) + 120;

            var distCost = new Dictionary<(int, int), int>();
            var prev = new Dictionary<(int, int), (int, int)>();
            var pq = new SortedSet<(int cost, int x, int y)>();

            distCost[(sx, sy)] = 0;
            pq.Add((0, sx, sy));

            int[] dxs = { 1, -1, 0, 0 };
            int[] dys = { 0, 0, 1, -1 };

            while (pq.Count > 0)
            {
                var cur = pq.Min;
                pq.Remove(cur);
                var (cost, cx, cy) = cur;
                if (cx == gx && cy == gy) break;
                if (cost > distCost[(cx, cy)]) continue;

                for (int i = 0; i < 4; i++)
                {
                    int nx = cx + dxs[i], ny = cy + dys[i];
                    if (nx < minX || nx > maxX || ny < minY || ny > maxY) continue;
                    bool wall = PathPlanner.IsBlockPublic(nx, ny);
                    int step;
                    if (dxs[i] != 0) step = wall ? DigSide : MoveSide;
                    else if (dys[i] > 0) step = wall ? DigDown : MoveDown;   // y+ is down
                    else step = wall ? DigUp : MoveUp;
                    int nc = cost + step;
                    if (distCost.TryGetValue((nx, ny), out int old) && nc >= old) continue;
                    distCost[(nx, ny)] = nc;
                    prev[(nx, ny)] = (cx, cy);
                    pq.Add((nc, nx, ny));
                }
            }

            var path = new List<(int, int)>();
            int breaks = 0;
            if (distCost.ContainsKey((gx, gy)))
            {
                var c = (gx, gy);
                while (true)
                {
                    path.Add(c);
                    if (PathPlanner.IsBlockPublic(c.Item1, c.Item2)) breaks++;
                    if (c == (sx, sy)) break;
                    if (!prev.TryGetValue(c, out c)) break;
                }
                path.Reverse();
            }
            return (path, breaks);
        }

        public override void AddRecipes()
        {
            CreateRecipe().AddIngredient(ItemID.DirtBlock, 1).AddTile(TileID.WorkBenches).Register();
        }
    }
}
