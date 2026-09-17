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

			Line(sb, font, ref x, ref y, "[Jev]", Color.Gold);
			if (Dodge.Enabled)
			{
				Line(sb, font, ref x, ref y, "走位 " + Dodge.Last, Tint(Dodge.Confidence));
				Line(sb, font, ref x, ref y,
					$"     置信{Dodge.Confidence:0.00}  危险{Dodge.Danger:0.0}"
					+ (Dodge.SafeToAttack ? "" : " 别贴脸") + $"  {Dodge.LatencyMs}ms", Color.LightGray);
			}
			if (Combat.Enabled)
				Line(sb, font, ref x, ref y, "攻击 " + Combat.Last, Color.White);
			if (!Dodge.Enabled && !Combat.Enabled)
				Line(sb, font, ref x, ref y, "两层都没开", Color.Gray);
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
