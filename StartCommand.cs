using Terraria.ModLoader;

namespace TerraBlind
{
	// /start —— tb 1 的全流程,不经过 python
	public class StartCommand : ModCommand
	{
		public override CommandType Type => CommandType.Chat;
		public override string Command => "start";
		public override string Description => "跑完整流程:砍树 → 收火把 → 盖房子 → 下地狱 → 打肉山";

		public override void Action(CommandCaller caller, string input, string[] args)
		{
			if (args.Length > 0 && args[0] == "stop")
			{
				StartRun.Stop();
				caller.Reply("停了。");
				return;
			}
			if (!StartRun.Start(out string why)) caller.Reply($"起不来:{why}");
			else caller.Reply("开工。");
		}
	}
}
