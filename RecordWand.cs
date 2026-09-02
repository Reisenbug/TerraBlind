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

            // RIGHT-click → raw frame recording (human_rec.json). LEFT-click → semantic BUILD recording
            // (build_rec.json): place/mine intents for house construction, diff-friendly and editable.
            if (player.altFunctionUse == 2)
            {
                if (RecordSystem.IsRecording)
                {
                    RecordSystem.Stop();
                    Chatter.Say($"■ 停止帧录制 ({RecordSystem.LastFrameCount} 帧)", 255, 120, 120);
                }
                else
                {
                    RecordSystem.Start();
                    Chatter.Say("● 开始帧录制", 120, 255, 120);
                }
                return true;
            }

            if (BuildRecorder.IsRecording)
            {
                BuildRecorder.Stop();
                Chatter.Say($"■ 停止建造录制 ({BuildRecorder.LastEventCount} 事件) → build_rec.json", 255, 120, 120);
            }
            else
            {
                BuildRecorder.Start();
                Chatter.Say("● 开始建造录制（放置/挖掘意图）", 120, 255, 120);
            }
            return true;
        }

        public override void ModifyTooltips(List<TooltipLine> tooltips)
        {
            tooltips.Add(new TooltipLine(Mod, "tip0", "左键：开始/停止建造录制 → build_rec.json（放置/挖掘意图，可编辑/复用）"));
            tooltips.Add(new TooltipLine(Mod, "tip1", "右键：开始/停止帧录制 → human_rec.json（原始逐帧）"));
        }

        public override void AddRecipes()
        {
            CreateRecipe().AddIngredient(ItemID.DirtBlock, 1).AddTile(TileID.WorkBenches).Register();
        }
    }
}
