using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
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
		JsonArray _decisions = new();
		JsonObject _intents;
		ulong _refreshTick;

		public override void PostDrawInterface(SpriteBatch sb)
		{
			var p = Main.LocalPlayer;
			if (Main.gameMenu || p == null || !p.active) return;
			DrawAgentDecisions(sb);
			if ((FightCoordinator.AimTarget ?? (Combat.Enabled ? Combat.AimTarget : null)) is Vector2 target)
			{
				Vector2 screen = target - Main.screenPosition;
				var pixel = Terraria.GameContent.TextureAssets.MagicPixel.Value;
				sb.Draw(pixel, new Microsoft.Xna.Framework.Rectangle((int)screen.X - 7, (int)screen.Y, 15, 2), Color.Lime);
				sb.Draw(pixel, new Microsoft.Xna.Framework.Rectangle((int)screen.X, (int)screen.Y - 7, 2, 15), Color.Lime);
			}
			if (!Enabled) return;

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
					$"      confidence {Dodge.Confidence:0.00}  {Dodge.LatencyMs}ms", Color.LightGray);
				// 【概率分布是证据】。写死的状态机给不出七个选项各占多少
				if (Dodge.TopTwo.Length > 0)
					Line(sb, font, ref x, ref y, "      " + Dodge.TopTwo, Color.MediumPurple);
			}
			if (Combat.Enabled)
				Line(sb, font, ref x, ref y, "fight " + Combat.Last, Color.White);
			if (!Dodge.Enabled && !Combat.Enabled)
				Line(sb, font, ref x, ref y, "both layers off", Color.Gray);
		}

		void DrawAgentDecisions(SpriteBatch sb)
		{
			if (Main.GameUpdateCount - _refreshTick >= 6)
			{
				_refreshTick = Main.GameUpdateCount;
				_decisions = AgentTools.DecisionChannels.Snapshot();
				_intents = AgentScript.IntentStatus;
			}
			if (_decisions.Count == 0 && _intents == null) return;
			var entries = new List<(string Text, Color Color)> { ("Jev 决策", Color.Gold) };
			if (_intents != null)
			{
				string current = _intents["current"]?["description"]?.ToString();
				entries.Add((current == null ? "状态：" + Label(_intents["status"]?.ToString()) : "当前：" + current, Color.White));
				if (_intents["prepared"]?["description"] is JsonNode next) entries.Add(("预备：" + next, Color.LightCyan));
				if (_intents["feedback"] is JsonObject feedback)
					entries.Add(("反馈：" + (feedback["verified"]?.GetValue<bool>() == true ? "已核对 " : "受阻 ")
						+ feedback["description"] + " " + feedback["error"], feedback["verified"]?.GetValue<bool>() == true ? Color.LightGreen : Color.Orange));
			}
			foreach (var row in _decisions)
			{
				var choices = new List<string>();
				foreach (var (_, answer) in row["answers"].AsObject())
					if (answer?["choice"] is JsonNode choice) choices.Add(Choice(choice.ToString()));
					else if (answer?["noul"] is JsonNode probability) choices.Add(probability.ToString());
				string status = row["status"]?.ToString();
				string text = Label(row["channel"].ToString()) + "：" + (choices.Count > 0 ? string.Join(" / ", choices) : Label(status));
				if (row["elapsed_ms"] is JsonNode ms) text += "  " + ms + "ms";
				if (row["in_flight"]?.GetValue<bool>() == true) text += " · 更新中";
				if (row["error"] is JsonNode error) text += " · " + error;
				entries.Add((text, status == "failed" ? Color.Orange : status == "ready" ? Color.LightGreen : Color.LightGray));
			}
			var font = Terraria.GameContent.FontAssets.MouseText.Value;
			float width = Math.Min(420, Main.screenWidth / Main.UIScale - 32), scale = .75f;
			var lines = new List<(string Text, Color Color)>();
			foreach (var entry in entries)
			{
				var wrapped = Utils.WordwrapString(entry.Text, font, (int)((width - 20) / scale), entry.Text.Length + 1, out _);
				int count = 0;
				foreach (string line in wrapped)
					if (line != null && count++ < 2) lines.Add((line, entry.Color));
			}
			float x = Math.Max(8, Main.screenWidth / Main.UIScale - width - 16);
			float y = Math.Max(16, Main.screenHeight / Main.UIScale - lines.Count * 19 - 34);
			sb.Draw(Terraria.GameContent.TextureAssets.MagicPixel.Value, new Rectangle((int)x, (int)y, (int)width, lines.Count * 19 + 16), Color.Black * .7f);
			foreach (var line in lines)
			{
				ChatManager.DrawColorCodedStringWithShadow(sb, font, line.Text, new Vector2(x + 10, y + 8), line.Color, 0, Vector2.Zero, new Vector2(scale));
				y += 19;
			}
		}

		static string Choice(string choice)
		{
			if (choice.StartsWith("cover_left_")) return "向左搭挡墙";
			if (choice.StartsWith("cover_right_")) return "向右搭挡墙";
			int split = choice.IndexOf("_weapon_", StringComparison.Ordinal);
			if (split >= 0 && int.TryParse(choice[(split + 8)..], out int weapon))
				return Label(choice[..split]) + " · " + Lang.GetItemNameValue(weapon);
			if (choice.StartsWith("resource_") && int.TryParse(choice[9..], out int resource)) return "采集 " + Lang.GetItemNameValue(resource);
			if (choice.StartsWith("slot_")) return "武器槽 " + choice[5..];
			return Label(choice);
		}

		static string Label(string text) => text switch
		{
			"local_intents" => "意图", "combat" => "战术", "loadout" or "weapon" => "武器", "survival" => "生存",
			"task_progress" => "进度", "movement" or "travel_intent" => "移动", "opportunity" => "顺路机会",
			"continue" or "keep" => "继续", "review" or "needs_review" => "需要调整", "pending" => "判断中",
			"ready" => "已返回", "failed" or "blocked" => "受阻", "executing" => "执行中", "completed" => "阶段完成",
			"yield" => "片段结束", "verified" => "已核对", "interrupted" => "已中断", "recall" => "回城",
			"follow_route" or "travel" => "继续赶路", "left" => "向左", "right" => "向右",
			"jump_left" => "向左跳", "jump_right" => "向右跳", "terrain" => "调整地形", "equipment" => "调整装备",
			"ranged_weapon" => "准备远程攻击", "take" => "顺路收集", "pass" => "继续原目标", _ => text ?? ""
		};

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
