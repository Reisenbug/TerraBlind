using Microsoft.Xna.Framework;
using System.Collections.Generic;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraBlind
{
    public class NavWand : ModItem
    {
        public override string Texture => "Terraria/Images/Item_" + ItemID.RodofDiscord;

        private static int _pendingWx = -1;
        private static int _pendingWy = -1;

        public override void SetDefaults()
        {
            Item.width = 28;
            Item.height = 28;
            Item.useStyle = ItemUseStyleID.Swing;
            Item.useTime = 20;
            Item.useAnimation = 20;
            Item.value = 0;
            Item.rare = ItemRarityID.Blue;
            Item.maxStack = 1;
            Item.noMelee = true;
        }

        public override bool AltFunctionUse(Player player) => true;

        public override bool? UseItem(Player player)
        {
            if (player != Main.LocalPlayer) return null;

            int mx = (int)((Main.mouseX + Main.screenPosition.X) / 16f);
            int my = (int)((Main.mouseY + Main.screenPosition.Y) / 16f);

            if (player.altFunctionUse == 2)
            {
                if (_pendingWx >= 0)
                {
                    if (NavCoordinator.IsActive) NavCoordinator.Stop();
                    if (SegmentedNavCoordinator.IsActive) SegmentedNavCoordinator.Stop();
                    SegmentedNavCoordinator.StartTo(_pendingWx, _pendingWy);
                }
            }
            else
            {
                if (NavCoordinator.IsActive) NavCoordinator.Stop();
                if (SegmentedNavCoordinator.IsActive) SegmentedNavCoordinator.Stop();
                _pendingWx = mx;
                _pendingWy = my;
                // preview: show waypoints + first-segment plan
                var p = Main.LocalPlayer;
                int sx = (int)((p.position.X + p.width / 2f) / 16f);
                int sy = (int)((p.position.Y + p.height) / 16f);
                var wps = WaypointPlanner.Generate(sx, sy, mx, my);
                var tiles = new System.Collections.Generic.List<(int, int, Microsoft.Xna.Framework.Color)>();
                foreach (var (wx, wy) in wps)
                    tiles.Add((wx, wy, new Microsoft.Xna.Framework.Color(255, 100, 255, 220)));
                PathVisSystem.SetTiles(tiles, ttlFrames: 600);
                int firstWx = wps.Count > 0 ? wps[0].wx : mx;
                int firstWy = wps.Count > 0 ? wps[0].wy : my;
                string json = PathPlanner.PlanToWindowed(firstWx, firstWy, 25);
                var path = NavCoordinator.ParsePathPublic(json);
                var actions = string.Join(",", path.ConvertAll(n => $"({n.Wx},{n.Wy}){n.Action}"));
                DiagLog.Write($"[wand] target=({mx},{my}) wps={wps.Count} first-seg path={path.Count} nodes=[{actions}]");
                PathVisSystem.SetPlanPath(path, PathPlanner.GetEnvelopeCache());
            }
            return true;
        }

        public override void ModifyTooltips(List<TooltipLine> tooltips)
        {
            tooltips.Add(new TooltipLine(Mod, "tip0", "左键：规划到目标位置并可视化"));
            tooltips.Add(new TooltipLine(Mod, "tip1", "右键：执行规划的路径"));
        }

        public override void AddRecipes()
        {
            CreateRecipe()
                .AddIngredient(ItemID.DirtBlock, 1)
                .AddTile(TileID.WorkBenches)
                .Register();
        }
    }
}
