using System;
using System.Collections.Generic;
using System.IO;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraBlind
{
	public class AgentNetwork : ModSystem
	{
		enum Message : byte { Register, Request, Execute, Result, Reply, Unregister }
		static readonly Dictionary<int, int> Owners = new();
		static readonly HashSet<int> PendingDefaultOwners = new();
		const string DefaultOwnerName = "Reisenberg";
		static bool _registered;
		static int _owner = -1;
		public static int OwnerPlayer => _owner;
		static string _lastStatus = "";
		static uint _lastSent;
		static bool _awaitingReply;
		static uint _requestTick;

		static void Log(string text)
		{
			DiagLog.Write("[ai-net] " + text);
			ModContent.GetInstance<TerraBlind>().Logger.Info("[ai-net] " + text);
		}
		public static bool CanAutomate => !Main.dedServ
			&& (Main.netMode == NetmodeID.SinglePlayer || Config.I.AiClient);

		static ModPacket Packet(Message type)
		{
			var packet = ModContent.GetInstance<TerraBlind>().GetPacket();
			packet.Write((byte)type);
			return packet;
		}

		public override void OnWorldLoad() => Reset();
		public override void OnWorldUnload()
		{
			if (CanAutomate) Run("stop");
			Reset();
		}
		static void Reset()
		{
			Owners.Clear(); PendingDefaultOwners.Clear(); _registered = false; _owner = -1;
			_lastStatus = ""; _lastSent = 0;
			_awaitingReply = false;
		}
		public static void Disconnect(int player)
		{
			if (Main.netMode != NetmodeID.Server) return;
			Owners.Remove(player);
			PendingDefaultOwners.Remove(player);
			foreach (int bot in new List<int>(Owners.Keys))
				if (Owners[bot] == player)
				{
					Owners[bot] = -1; PendingDefaultOwners.Add(bot); Execute(bot, -1, "stop");
				}
		}

		public override void PostUpdateEverything()
		{
			if (Main.netMode == NetmodeID.Server)
			{
				foreach (int bot in new List<int>(Owners.Keys))
				{
					if (!Main.player[bot].active) { Owners.Remove(bot); PendingDefaultOwners.Remove(bot); continue; }
					int owner = Owners[bot];
					if (owner >= 0 && !Main.player[owner].active)
					{
						Owners[bot] = -1;
						PendingDefaultOwners.Add(bot);
						Execute(bot, -1, "stop");
					}
					TryDefaultOwner(bot);
				}
				return;
			}
			if (Main.netMode != NetmodeID.MultiplayerClient || !Main.LocalPlayer.active) return;
			if (_awaitingReply && Main.GameUpdateCount - _requestTick >= 600)
			{
				_awaitingReply = false;
				Log("reply timeout");
				AgentChat.Display("AI 请求已发出，但10秒内未收到服务器回复。请检查双方日志中的 [ai-net]。");
			}
			if (!Config.I.AiClient)
			{
				if (_registered)
				{
					Run("stop");
					Packet(Message.Unregister).Send();
					_registered = false; _owner = -1; _lastStatus = ""; _lastSent = 0;
				}
				return;
			}
			if (!_registered)
			{
				Log($"register player={Main.myPlayer}");
				Packet(Message.Register).Send();
				_registered = true;
			}
			if (_owner < 0 || Main.GameUpdateCount - _lastSent < 60) return;
			string status = Status();
			if (status == _lastStatus) return;
			_lastStatus = status; _lastSent = Main.GameUpdateCount;
			Report(status);
		}

		static void TryDefaultOwner(int bot)
		{
			if (!PendingDefaultOwners.Contains(bot) || Owners[bot] >= 0) return;
			for (int player = 0; player < Main.maxPlayers; player++)
			{
				if (player == bot || !Main.player[player].active || Main.player[player].name != DefaultOwnerName) continue;
				Owners[bot] = player;
				PendingDefaultOwners.Remove(bot);
				Log($"default owner bot={bot} owner={player} name={DefaultOwnerName}");
				Execute(bot, player, "status");
				return;
			}
		}

		static string Status() => $"流程={StartRun.Phase}/{StartRun.Outcome} {StartRun.Reason}; 寻路={RecedingNav.Active}/{RecedingNav.LastStop}; 战斗={Combat.Enabled}; 闪避={Dodge.Enabled}";

		public static void Request(int bot, string command)
		{
			Log($"request player={Main.myPlayer} bot={bot} action={command.Split(' ')[0]}");
			var packet = Packet(Message.Request);
			packet.Write(bot); packet.Write(command); packet.Send();
			_requestTick = Main.GameUpdateCount; _awaitingReply = true;
			AgentChat.Display(command == "list" ? "正在向服务器查询 AI 玩家……" : "AI 请求已发往服务器。");
		}
		static void Execute(int bot, int owner, string command)
		{
			var packet = Packet(Message.Execute);
			packet.Write(owner); packet.Write(command); packet.Send(bot);
		}
		static void Reply(int player, string text)
		{
			Log($"reply to={player} chars={text.Length}");
			var packet = Packet(Message.Reply);
			packet.Write(text); packet.Send(player);
		}
		static void Report(string text)
		{
			var packet = Packet(Message.Result);
			packet.Write(text); packet.Send();
		}

		public static bool RelayAgentReply(string text)
		{
			if (Main.netMode != NetmodeID.MultiplayerClient || !Config.I.AiClient || _owner < 0) return false;
			for (int offset = 0; offset < text.Length; offset += 900)
				Report(text.Substring(offset, System.Math.Min(900, text.Length - offset)));
			return true;
		}

		public static void Receive(BinaryReader reader, int sender)
		{
			var type = (Message)reader.ReadByte();
			if (type != Message.Result) Log($"receive mode={Main.netMode} sender={sender} type={type}");
			if (Main.netMode == NetmodeID.Server)
			{
				if (sender < 0 || sender >= Main.maxPlayers || !Main.player[sender].active)
				{ Log($"reject inactive sender={sender}"); return; }
				if (type == Message.Register)
				{
					if (Owners.TryAdd(sender, -1)) PendingDefaultOwners.Add(sender);
					TryDefaultOwner(sender);
					return;
				}
				if (type == Message.Unregister)
				{
					PendingDefaultOwners.Remove(sender);
					if (Owners.Remove(sender, out int previousOwner) && previousOwner >= 0)
						Reply(previousOwner, $"AI[{sender}] 已关闭 AI 身份并解除绑定。");
					return;
				}
				if (type == Message.Result)
				{
					string result = reader.ReadString();
					if (result.Length <= 1024 && Owners.TryGetValue(sender, out int owner) && owner >= 0)
						Reply(owner, $"AI[{sender}] {result}");
					return;
				}
				if (type != Message.Request) return;
				int bot = reader.ReadInt32();
				string command = reader.ReadString();
				if (command.Length > 4000) return;
				if (command == "list")
				{
					var bots = new List<string>();
					foreach (var pair in Owners)
						if (Main.player[pair.Key].active)
							bots.Add($"{pair.Key}:{Main.player[pair.Key].name} 所有者={pair.Value}");
					Reply(sender, bots.Count == 0 ? "没有 AI 在线。" : string.Join("; ", bots));
					return;
				}
				if (bot == sender || !Owners.TryGetValue(bot, out int current) || !Main.player[bot].active)
				{ Reply(sender, "目标 AI 不在线。先 /ai list。"); return; }
				if (command == "bind" && current == -1)
				{
					Owners[bot] = sender;
					PendingDefaultOwners.Remove(bot);
					Execute(bot, sender, "status");
					return;
				}
				if (current != sender) { Reply(sender, "你没有绑定此 AI。"); return; }
				if (command == "unbind")
				{
					PendingDefaultOwners.Remove(bot);
					Owners[bot] = -1; Execute(bot, -1, "stop");
					Reply(sender, "已解除绑定并停止 AI。"); return;
				}
				Execute(bot, sender, command);
				return;
			}
			if (Main.netMode != NetmodeID.MultiplayerClient) return;
			if (type == Message.Reply)
			{
				_awaitingReply = false;
				AgentChat.Display(reader.ReadString());
			}
			if (type == Message.Execute && Config.I.AiClient)
			{
				_owner = reader.ReadInt32();
				string command = reader.ReadString();
				Report(Run(command));
			}
		}

		static string Run(string command)
		{
			string[] args = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (args.Length == 0) return "空指令。";
			switch (args[0])
			{
				case "status": return Status();
				case "task":
					NativeAgent.Submit(command.Length > 5 ? command.Substring(5) : "");
					return "指令已交给内置 agent。";
				case "rules":
					try
					{
						var patch = System.Text.Json.Nodes.JsonNode.Parse(command.Substring(5).Trim()).AsObject();
						patch["session"] = PvpDuel.Session;
						return PvpDuel.UpdateDirective(patch, true).ToJsonString();
					}
					catch (Exception ex) { return "rules 参数错误：" + ex.Message; }
				case "stop":
					NativeAgent.Stop();
					PlayerCombat.Stop();
					Combat.Enabled = false; Dodge.Enabled = false; Combat.Stop();
					Manual.On = true;
					StateSnapshotPlayer.StopRemoteTask(); ReplaySystem.Stop(); KeyPress.Clear();
					HttpServerSystem.PendingControl = null;
					return Halt.All();
				case "start":
					Manual.On = false;
					return StartRun.Start(out string why) ? "完整流程已开始。" : why;
				case "fight":
					Run("stop");
					Manual.On = false; Combat.Enabled = true; Dodge.Enabled = true;
					return "战斗与闪避已开启。";
				case "goto":
					if (args.Length != 3 || !int.TryParse(args[1], out int x) || !int.TryParse(args[2], out int y)
						|| x < 1 || y < 1 || x >= Main.maxTilesX - 1 || y >= Main.maxTilesY - 1)
						return "用法: goto <方块X> <方块Y>";
					Run("stop"); RecedingNav.Start(x, y);
					return $"前往 ({x},{y})。";
				default: return "支持 status、start、fight、goto X Y、stop。";
			}
		}
	}

	public class AgentConnectionPlayer : ModPlayer
	{
		public override void PlayerDisconnect() => AgentNetwork.Disconnect(Player.whoAmI);
	}

	public class AiCommand : ModCommand
	{
		public override CommandType Type => CommandType.Chat;
		public override string Command => "ai";
		public override string Description => "AI 玩家: /ai list 或 /ai <玩家编号> bind|unbind|status|task 指令|start|fight|goto X Y|stop";
		public override void Action(CommandCaller caller, string input, string[] args)
		{
			DiagLog.Write($"[ai-net] command mode={Main.netMode} input={input}");
			if (Main.netMode != NetmodeID.MultiplayerClient)
			{ caller.Reply("/ai 用于多人游戏。"); return; }
			if (args.Length == 1 && args[0] == "list") { AgentNetwork.Request(-1, "list"); return; }
			if (args.Length < 2 || !int.TryParse(args[0], out int bot))
			{ caller.Reply(Description); return; }
			AgentNetwork.Request(bot, string.Join(" ", args, 1, args.Length - 1));
		}
	}
}
