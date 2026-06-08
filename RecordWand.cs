using System.Collections.Generic;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraBlind
{
    public class RecordWand : ModItem
    {
        public override string Texture => "Terraria/Images/Item_" + ItemID.RodofDiscord;

        public override void SetDefaults()
        {
            Item.width = 28;
            Item.height = 28;
            Item.useStyle = ItemUseStyleID.Swing;
            Item.useTime = 20;
            Item.useAnimation = 20;
            Item.rare = ItemRarityID.Yellow;
            Item.maxStack = 1;
            Item.noMelee = true;
        }

        public override bool AltFunctionUse(Player player) => true;

        public override bool? UseItem(Player player)
        {
            if (player != Main.LocalPlayer) return null;
            if (player.altFunctionUse != 2) return true; // only right-click toggles; left does nothing

            if (RecordSystem.IsRecording)
            {
                RecordSystem.Stop();
                Main.NewText($"■ 停止录制 ({RecordSystem.LastFrameCount} 帧)", 255, 120, 120);
            }
            else
            {
                RecordSystem.Start();
                Main.NewText("● 开始录制", 120, 255, 120);
            }
            return true;
        }

        public override void ModifyTooltips(List<TooltipLine> tooltips)
        {
            tooltips.Add(new TooltipLine(Mod, "tip0", "右键：开始/停止录制人类操作 → human_rec.json + 屏幕轨迹"));
        }

        public override void AddRecipes()
        {
            CreateRecipe().AddIngredient(ItemID.DirtBlock, 1).AddTile(TileID.WorkBenches).Register();
        }
    }
}
