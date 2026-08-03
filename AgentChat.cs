using System.Collections.Concurrent;
using Terraria;
using Terraria.ModLoader;

namespace TerraBlind
{
	// Chat bridge for the external LLM agent (the "second player"). The player types /tb <instruction> in game
	// chat; the agent process polls GET /instruction, thinks, drives the primitive endpoints, and replies through
	// POST /say, which is flushed to game chat on the main thread (Main.NewText is not safe from the HTTP thread).
	public class AgentChatCommand : ModCommand
	{
		public override CommandType Type => CommandType.Chat;
		public override string Command => "tb";
		public override string Description => "Give the TerraBlind agent an instruction";
		public override string Usage => "/tb <instruction>";

		public override void Action(CommandCaller caller, string input, string[] args)
		{
			string text = string.Join(" ", args);
			if (string.IsNullOrWhiteSpace(text)) { caller.Reply("usage: /tb <instruction>"); return; }
			AgentChat.Instructions.Enqueue(text);
			Main.NewText($"<you → TB> {text}", 120, 200, 255);
			DiagLog.Write($"[agent] instruction queued: {text}");
			// push it as an event too, so the agent reacts instantly instead of polling /instruction
			HttpServerSystem.PushEvent("instruction", "{\"text\":\"" + HttpServerSystem.JsonEscPublic(text) + "\"}");
		}
	}

	public class AgentChat : ModSystem
	{
		public static readonly ConcurrentQueue<string> Instructions = new();
		static readonly ConcurrentQueue<(string text, bool bot)> _say = new();

		// bot=true 是脚本硬编的进度播报,false 是 LLM 自己写的话。颜色分开,不然分不清哪句是"想"出来的
		public static void Say(string text, bool bot = false) => _say.Enqueue((text, bot));

		public override void PostUpdateEverything()
		{
			while (_say.TryDequeue(out var t))
			{
				if (t.bot) Main.NewText($"<TB> {t.text}", 140, 170, 200);   // 灰蓝 = 脚本
				else Main.NewText($"<TB> {t.text}", 255, 200, 80);          // 橙 = LLM
			}
		}
	}
}
