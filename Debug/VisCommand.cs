using Terraria.ModLoader;

namespace TerraBlind
{
	// /vis on|off --- 调试图层的总开关。拍视频时关掉:A* 轨迹、跳跃/挖掘/放置的色块、
	// 地狱桥线那层蓝格都会盖住真实地形
	public class VisCommand : ModCommand
	{
		public override CommandType Type => CommandType.Chat;
		public override string Command => "vis";
		public override string Description => "调试图层开关:/vis off 关掉全部覆盖层(拍视频用)";

		public override void Action(CommandCaller caller, string input, string[] args)
		{
			if (args.Length == 0)
			{
				caller.Reply($"调试图层现在是{(PathVisSystem.Enabled ? "开" : "关")}的。/vis on 或 /vis off");
				return;
			}
			PathVisSystem.Enabled = args[0] != "off" && args[0] != "0";
			caller.Reply(PathVisSystem.Enabled ? "调试图层开了。" : "调试图层关了,画面干净了。");
		}
	}
}
