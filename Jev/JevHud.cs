using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.ModLoader;
using Terraria.UI.Chat;

namespace TerraBlind
{
	// 屏幕左上角一小块,每帧覆盖刷新。【不往聊天里打】:聊天会堆积,那才是杂乱的来源
	public class JevHud : ModSystem
	{
		public static bool Enabled = false;

		public override void PostDrawInterface(SpriteBatch sb)
		{
			if (!Enabled) return;
			var p = Main.LocalPlayer;
			if (p == null || !p.active) return;

			var font = Terraria.GameContent.FontAssets.MouseText.Value;
			float x = 16f, y = 120f;

			// 关背板时标一行。录像和日志都得自己说明当时是哪一组,不然事后分不清
			Line(sb, font, ref x, ref y,
				BossBook.UseKnowledge ? "[Jev]" : "[Jev] no playbook",
				BossBook.UseKnowledge ? Color.Gold : Color.Orange);
			if (Dodge.Enabled)
			{
				Line(sb, font, ref x, ref y, "move  " + Dodge.Last, Tint(Dodge.Confidence));
				Line(sb, font, ref x, ref y,
					$"      confidence {Dodge.Confidence:0.00}  danger {Dodge.Danger:0.0}"
					+ (Dodge.SafeToAttack ? "" : "  keep away") + $"  {Dodge.LatencyMs}ms", Color.LightGray);
				// 【概率分布是证据】。写死的状态机给不出七个选项各占多少
				if (Dodge.TopTwo.Length > 0)
					Line(sb, font, ref x, ref y, "      " + Dodge.TopTwo, Color.MediumPurple);
			}
			if (Combat.Enabled)
				Line(sb, font, ref x, ref y, "fight " + Combat.Last, Color.White);
			if (!Dodge.Enabled && !Combat.Enabled)
				Line(sb, font, ref x, ref y, "both layers off", Color.Gray);
		}

		// 置信低就变暗:一眼看出它是在判断还是在猜
		static Color Tint(float c)
			=> c >= 0.7f ? Color.LightGreen : c >= 0.45f ? Color.Khaki : Color.IndianRed;

		static void Line(SpriteBatch sb, ReLogic.Graphics.DynamicSpriteFont font,
						 ref float x, ref float y, string text, Color c)
		{
			ChatManager.DrawColorCodedStringWithShadow(sb, font, text,
				new Vector2(x, y), c, 0f, Vector2.Zero, new Vector2(0.8f));
			y += 20f;
		}
	}
}
