using Terraria.ModLoader;

namespace TerraBlind
{
	// /vis on|off, 调试图层的总开关。开关本身在模组配置里,这条命令只是游戏内的快捷方式:
	// 录像时要临时关掉,不想为此退出去开配置界面
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
			caller.Reply("(只改这一局。要长期改,在模组配置里)");
		}
	}
}
