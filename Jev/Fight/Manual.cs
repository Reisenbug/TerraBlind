using Terraria;

namespace TerraBlind
{
	// 转人工:自动瞄准射击和 Jev 走位都停,再按一次恢复
	public static class Manual
	{
		public static bool On;

		public static void Toggle()
		{
			On = !On;
			Main.NewText(On ? "[TerraBlind] 人工" : "[TerraBlind] 自动", 255, 220, 120);
			DiagLog.Write($"[manual] {(On ? "转人工" : "恢复自动")}");
		}
	}
}
