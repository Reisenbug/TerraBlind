using System.Collections.Concurrent;
using Terraria;
using Terraria.ModLoader;
using Terraria.GameContent;

namespace TerraBlind
{
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
			AgentChat.Display($"<you → TB> {text}", 120, 200, 255);
			DiagLog.Write($"[agent] instruction: {text}");
			if (Config.I.AgentEnabled) { NativeAgent.Submit(text); return; }
			AgentChat.Instructions.Enqueue(text);
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

		public static void Display(string text, byte r = 255, byte g = 255, byte b = 255)
		{
			int width = System.Math.Max(100, System.Math.Min(700, (int)(Main.screenWidth / Main.UIScale) - 40));
			var lines = Utils.WordwrapString(text, FontAssets.MouseText.Value, width, text.Length + 1, out int count);
			for (int i = 0; i < count; i++) Main.NewText(lines[i], r, g, b);
		}

		public override void PostUpdateEverything()
		{
			while (_say.TryDequeue(out var t))
			{
				if (AgentNetwork.RelayAgentReply(t.text)) continue;
				if (t.bot) Display($"<TB> {t.text}", 140, 170, 200);
				else Display($"<TB> {t.text}", 90, 230, 120);
			}
		}

		public override void OnWorldUnload()
		{
			while (_say.TryDequeue(out _)) { }
			while (Instructions.TryDequeue(out _)) { }
		}
	}
}
