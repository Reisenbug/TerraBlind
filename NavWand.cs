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
                // right click = block-plan execute prototype: maze block → pillar-jump executor
                if (NavCoordinator.IsActive) NavCoordinator.Stop();
                if (SegmentedNavCoordinator.IsActive) SegmentedNavCoordinator.Stop();
                StateSpacePlanner.StopExec();
                StateSpacePlanner.ExecBlocks(mx, my);
                DiagLog.Write($"[wand] ss_execblk target=({mx},{my})");
            }
            else
            {
                // left click = action-graph A* plan + visualize full action path (stage 1: no execution)
                if (NavCoordinator.IsActive) NavCoordinator.Stop();
                if (SegmentedNavCoordinator.IsActive) SegmentedNavCoordinator.Stop();
                ActionGraphPlanner.PlanAndVisualize(mx, my);
                DiagLog.Write($"[wand] ag_plan target=({mx},{my})");
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
