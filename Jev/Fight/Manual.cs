using Terraria;
using Terraria.Localization;

namespace TerraBlind
{
	// 转人工:自动瞄准射击和 Jev 走位都停,再按一次恢复
	public static class Manual
	{
		public static bool On;

		public static void Toggle()
		{
			On = !On;
			string mode = Language.GetTextValue(On
				? "Mods.TerraBlind.Exploration.ManualControl" : "Mods.TerraBlind.Exploration.AutomaticControl");
			Main.NewText($"[TerraBlind] {mode}", 255, 220, 120);
			DiagLog.Write($"[manual] {(On ? "转人工" : "恢复自动")}");
		}
	}
}
