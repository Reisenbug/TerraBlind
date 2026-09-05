using Terraria;
using Terraria.ModLoader;

namespace TerraBlind
{
	// 游戏内聊天框只留【给人看的】那一路:AgentChat 的 <you → TB> / <TB> 对话。
	// 其余全是诊断(卡点、脱困、超时、相位播报…),录视频时刷屏,而日志里本来就有一份。
	//
	// 【别把 AgentChat 也收进来】。那是录像的主角。它直接调 Main.NewText,不走这里。
	public static class Chatter
	{
		// 关掉诊断刷屏。要现场看内部状态时用 /tbchat on 打开
		public static bool Diag = false;

		public static void Say(string msg) { if (Diag) Main.NewText(msg); }
		public static void Say(string msg, byte r, byte g, byte b) { if (Diag) Main.NewText(msg, r, g, b); }
		public static void Say(string msg, Microsoft.Xna.Framework.Color c) { if (Diag) Main.NewText(msg, c); }
	}

	public class ChatterCommand : ModCommand
	{
		public override CommandType Type => CommandType.Chat;
		public override string Command => "tbdiag";
		public override string Description => "游戏内诊断刷屏开关(录视频时关掉)";
		public override string Usage => "/tbdiag";

		public override void Action(CommandCaller caller, string input, string[] args)
		{
			Chatter.Diag = !Chatter.Diag;
			Main.NewText($"[TerraBlind] 诊断刷屏 {(Chatter.Diag ? "开" : "关")}", 200, 200, 120);
		}
	}
}
