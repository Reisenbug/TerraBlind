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
        // pending plan request: when the async result arrives, what do we do?
        // 0=nothing, 1=preview only, 2=exec
        private static int _pendingMode = 0;
        private static int _pendingSeq = -1;

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
                if (NavCoordinator.IsActive) NavCoordinator.Stop();
                if (SegmentedNavCoordinator.IsActive) SegmentedNavCoordinator.Stop();
                _pendingWx = mx; _pendingWy = my; _pendingMode = 2;
                _pendingSeq = PlanningJob.Request(mx, my);
                DiagLog.Write($"[wand] exec request target=({mx},{my}) seq={_pendingSeq}");
            }
            else
            {
                // left click = state-space plan + visualize (runs on main thread, ms-scale)
                if (NavCoordinator.IsActive) NavCoordinator.Stop();
                if (SegmentedNavCoordinator.IsActive) SegmentedNavCoordinator.Stop();
                var ssr = StateSpacePlanner.Plan(mx, my);
                StateSpacePlanner.Visualize(ssr, mx, my);
                DiagLog.Write($"[wand] ss_plan target=({mx},{my}) found={ssr.Found} exp={ssr.Expansions} ms={ssr.Millis:0.#} path={ssr.Path.Count} best_dx={ssr.BestDx:0.#} best_dy={ssr.BestDy:0.#}");
            }
            return true;
        }

        public static void PollResult()
        {
            if (_pendingMode == 0) return;
            if (!PlanningJob.TryTakeResult(out string json, out int gx, out int gy, out int seq)) return;
            if (seq != _pendingSeq) return; // superseded
            var path = NavCoordinator.ParsePathPublic(json);
            DiagLog.Write($"[wand] result seq={seq} target=({gx},{gy}) path={path.Count} mode={_pendingMode}");
            PathVisSystem.SetPlanPath(path, PathPlanner.GetEnvelopeCache());
            if (_pendingMode == 2)
            {
                NavCoordinator.StartToWithPath(gx, gy, path);
            }
            _pendingMode = 0;
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

    public class NavWandPoller : ModSystem
    {
        public override void PostUpdateEverything() => NavWand.PollResult();
    }
}
