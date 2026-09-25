using System.Collections.Generic;

namespace TerraBlind
{
	// 每个 boss 一条,打法只是一段话
	public class BossInfo
	{
		public string HowItFights = "";
	}

	public static class BossBook
	{
		// 对照实验:关掉背板。知识到底贡献了多少
		// 只关 HowItFights
		public static bool UseKnowledge = true;

		static readonly Dictionary<int, BossInfo> Book = new()
		{
			[Terraria.ID.NPCID.EyeofCthulhu] = new BossInfo
			{
				HowItFights =
					"The Eye of Cthulhu usually hovers above the player, winds up for a moment, then charges in a straight line at where the player is. "
					+ "It takes horizontal speed. Standing still waiting for it to come down means getting hit. "
					+ "It also summons Servant minions.",
			},

			[Terraria.ID.NPCID.KingSlime] = new BossInfo
			{
				HowItFights =
					"King Slime in one sentence: keep away from it and never stop. It teleports onto the player, "
					+ "and touching it after the teleport hurts. Standing still makes that easy to eat, so keep moving at all times."
			},

			[Terraria.ID.NPCID.BrainofCthulhu] = new BossInfo
			{
				HowItFights =
					"The Brain of Cthulhu has two phases. In phase one the Brain itself is invulnerable; attack the Creepers on the field "
					+ "and clear them all first. The Brain shows itself the moment they are gone. Creepers are one-time and do not respawn. "
					+ "In phase two the Brain teleports near the player and then rams. Touching a Creeper or the Brain hurts the whole fight. "
					+ "The arena is a square room; backing off soon hits a wall, "
					+ "so just opening distance does not work well here. "
					+ "While Creepers remain and I use a melee weapon, being below them is safer than above; "
					+ "they ram along the line from the Brain to the player. Keep them on the line between me and the Brain when possible.",
			},

			[Terraria.ID.NPCID.EaterofWorldsHead] = new BossInfo
			{
				HowItFights =
					"The Eater of Worlds is a worm dozens of segments long; it tunnels through walls and dirt, and its whole body hurts on contact. "
					+ "Nothing fancy: just do not touch it. No need to go far away. "
					+ "Keep a distance where it cannot graze me and keep shooting. The distance you are given is to the nearest segment, "
					+ "not the head. The body does contact damage too.",
			},

			[Terraria.ID.NPCID.SkeletronHead] = new BossInfo
			{
				HowItFights =
					"Skeletron has a head and two hands. Hit the hands first while both are there. "
					+ "After one hand is down it starts firing skull shots on curved paths, faster once both hands are gone. "
					+ "Then watch the shots and dodge them. Every so often its head suddenly spins at high speed and rams. "
					+ "Stay away from the head when it moves fast; once it speeds up, stay off its path. "
					+ "Keep distance from both the hands and the head. "
					+ "The grappling hook can instantly change direction or gain height, avoiding the one sweeping in. "
					+ "Only running left and right on the ground leaves one dimension to dodge in, and the homing skulls will hit us a lot. "
					+ "The hook can go up, or down-left and down-right. In this fight, changing height often dodges better than running sideways. "
					+ "Once both hands are down, vertical movement is more useful.",
			},

			[Terraria.ID.NPCID.Spazmatism] = new BossInfo
			{
				HowItFights =
					"The Twins are two eyes flying separately; attack Spazmatism first. "
					+ "Avoiding contact comes first. "
					+ "Phase one movement: when I close in it backs off, when I back off it follows, "
					+ "so the horizontal distance cannot really be controlled; moving toward it only runs me head-on into its shots. "
					+ "Within 30 cells is dangerous: its flamethrower reaches there and its charges leave no time to react; stay farther, about 40 cells. "
					+ "In phase one Spazmatism chases me whenever it is not charging: never stop moving vertically. "
					+ "Dodging a charge needs a change of direction both horizontally and vertically; it locks onto where I was when the charge started. "
					+ "At half health it sheds its shell into phase two, which loops: breathe fire for a while, then charge six times in a row, then repeat. "
					+ "The fire is a continuous stream, not separate shots; standing in it hurts every frame, "
					+ "so the moment the fire starts hitting me, leave; the closer I am, the worse it burns. "
					+ "In phase two, stay away from Spazmatism at all times, not only while it breathes fire. "
					+ "Those six charges hurt more than the fire, and each one will get close for an instant; "
					+ "dodge that by changing direction: it locks onto where I was when the charge started, so one step sideways or vertically makes it miss. "
					+ "How to tell it is charging: Spazmatism's speed_cells_per_second in threats close to "
					+ "its top_speed_in_the_last_second is the charge itself; "
					+ "when the speed drops, that round of charges is over, which is the window to open distance and deal damage. "
					+ "Spazmatism's shots carry a debuff, so one hit costs more than its damage number. "
					+ "Retinazer's danger is its ram, not its laser. It keeps its distance in both phases; "
					+ "its laser barely hurts in phase one and only a little more in phase two, not worth scrambling to dodge; "
					+ "but one ram is a big chunk of health. Treat both eyes as things that ram, "
					+ "and do not ignore Retinazer just because I am fighting Spazmatism. "
					+ "There are always two eyes on the field; count both when dodging. threats shows where the other one is. "
					+ "Running from one often runs straight into the other; the real dodge is toward the side where neither is. "
					+ "When caught between the two, get away from Spazmatism first.",
			},

			[Terraria.ID.NPCID.TheDestroyer] = new BossInfo
			{
				HowItFights = "The Destroyer is a very long mechanical worm. Its head must never hit me, and stay away from the body too. Clear the Probe minions if needed.",
			},

			[Terraria.ID.NPCID.SkeletronPrime] = new BossInfo
			{
				HowItFights = "Stay away from Skeletron Prime's head."
			},

			[Terraria.ID.NPCID.Plantera] = new BossInfo
			{
				HowItFights =
					"Plantera has two phases; it enters phase two at half health. "
					+ "In phase one circle around it: keep about 40 cells away and keep moving along the circle, looping up-left and down-right. "
					+ "What matters is that my path always wraps around it. "
					+ "In phase two stop circling and just open distance and dodge normally. "
					+ "In phase two it grows tentacles; a tentacle hurts more than the body, "
					+ "and they lash out right next to me. threats shows where the tentacles are; "
					+ "being far from the body is not being far from a tentacle, so measure distance to the nearest tentacle.",
			},

			[Terraria.ID.NPCID.QueenBee] = new BossInfo
			{
				HowItFights =
					"When Queen Bee hovers above my head, move horizontally. "
					+ "When it is at about my height and charging sideways at me, move vertically."
			},

			[Terraria.ID.NPCID.Deerclops] = new BossInfo
			{
				HowItFights =
					"Stay level with the boss as much as possible. It does little damage and there is no contact damage under its feet. Always stay within a few cells of it.",
			},

			// 恶鬼离得比肉山近时按这条算(Boss() 取最近的)
			[Terraria.ID.NPCID.TheHungry] = new BossInfo
			{
				HowItFights =
					"The Hungry are a string of minions hanging off the Wall of Flesh; they reach far and hurt on contact. "
					+ "Keep a safe distance of ten cells from them. When this conflicts with the Wall of Flesh distance rule, this one wins."
			},

			[Terraria.ID.NPCID.WallofFlesh] = new BossInfo
			{
				HowItFights =
					"The Wall of Flesh is a wall spanning the whole screen, pushing from one end of the Underworld to the other; it only moves horizontally and never stops. "
					+ "A string of minions called The Hungry hangs off it, reaching far, and they hurt on contact too. "
					+ "Run away from it, shooting as I go. "
					+ "The distance is a band of about 60 cells, and neither end is safe: too far and its laser sweeps me, "
					+ "too close and The Hungry hit me. Overshoot and come back, too close and back off, "
					+ "always staying in the middle of the band; read the Wall of Flesh's cells_to_my_right in threats to tell which end I have drifted to. "
					+ "Whenever its laser shows up in incoming_projectiles, keep moving without a pause; "
					+ "stopping for a moment gets me swept. That does not mean running far away. "
					+ "The goal is to keep moving inside the band, not to open distance. "
					+ "The lower its health, the faster it pushes. "
					+ "The arena is completely flat, and jumping is pointless: it neither dodges it nor reaches it," // 专为视频准备的场地。 TODO
			},
		};

		// 部件查本体那条。Boss() 返回的可能是蠕虫的某一节或者骷髅王的手
		public static int Canonical(int npcType) => Canon(npcType);

		static int Canon(int npcType)
		{
			if (npcType == Terraria.ID.NPCID.TheDestroyerBody
			 || npcType == Terraria.ID.NPCID.TheDestroyerTail
			 || npcType == Terraria.ID.NPCID.Probe)
				return Terraria.ID.NPCID.TheDestroyer;
			if (npcType == Terraria.ID.NPCID.PrimeCannon || npcType == Terraria.ID.NPCID.PrimeSaw
			 || npcType == Terraria.ID.NPCID.PrimeVice || npcType == Terraria.ID.NPCID.PrimeLaser)
				return Terraria.ID.NPCID.SkeletronPrime;
			if (npcType == Terraria.ID.NPCID.EaterofWorldsBody
			 || npcType == Terraria.ID.NPCID.EaterofWorldsTail)
				return Terraria.ID.NPCID.EaterofWorldsHead;
			if (npcType == Terraria.ID.NPCID.SkeletronHand)
				return Terraria.ID.NPCID.SkeletronHead;
			// 恶鬼不映射到肉山
			if (npcType == Terraria.ID.NPCID.WallofFleshEye)
				return Terraria.ID.NPCID.WallofFlesh;
			if (npcType == Terraria.ID.NPCID.TheHungryII)
				return Terraria.ID.NPCID.TheHungry;
			// 双子共用魔焰眼那条
			if (npcType == Terraria.ID.NPCID.Retinazer)
				return Terraria.ID.NPCID.Spazmatism;
			if (npcType == Terraria.ID.NPCID.PlanterasTentacle
			 || npcType == Terraria.ID.NPCID.PlanterasHook)
				return Terraria.ID.NPCID.Plantera;
			return npcType;
		}

		static readonly BossInfo None = new();

		public static BossInfo Of(int npcType)
			=> Book.TryGetValue(Canon(npcType), out var b) ? b : None;

		// npc id -> 覆盖。配置里存的是名字,解析一次存成 id,查的时候不用每帧比字符串
		static readonly Dictionary<int, BossOverride> Overrides = new();

		// 铺满配置:游戏里全部 boss
		public static void Seed(List<BossOverride> list)
		{
			if (list == null) return;
			var have = new HashSet<string>();
			foreach (var o in list) if (o != null && o.Boss != null) have.Add(o.Boss);
			foreach (var kv in Terraria.ID.ContentSamples.NpcsByNetId)
			{
				// ContentSamples 每个类型一份 SetDefaults 过的实例,boss 标志是准的,模组 boss 也在里面
				if (kv.Value == null || !kv.Value.boss) continue;
				string name = Terraria.Lang.GetNPCNameValue(kv.Key);
				if (string.IsNullOrWhiteSpace(name) || !have.Add(name)) continue;
				Book.TryGetValue(kv.Key, out var info);
				list.Add(new BossOverride
				{
					Boss = name,
					Enabled = true,
					HowItFights = info?.HowItFights ?? "",
				});
			}
			// 书里有但没有 boss 标志的(世界吞噬者的头、恶鬼)上面扫不到,补上
			foreach (var kv in Book)
			{
				string name = Terraria.Lang.GetNPCNameValue(kv.Key);
				if (string.IsNullOrWhiteSpace(name) || !have.Add(name)) continue;
				list.Add(new BossOverride { Boss = name, Enabled = true, HowItFights = kv.Value.HowItFights });
			}
		}

		public static void SetOverrides(List<BossOverride> list)
		{
			Overrides.Clear();
			if (list == null) return;
			foreach (var o in list)
			{
				if (o == null || string.IsNullOrWhiteSpace(o.Boss)) continue;
				int id = IdOf(o.Boss.Trim());
				if (id == 0) { DiagLog.Write($"[bossbook] \"{o.Boss}\" 对不上任何 boss,这条没用上"); continue; }
				Overrides[id] = o;
			}
			int off = 0;
			foreach (var o in Overrides.Values) if (!o.Enabled) off++;
			DiagLog.Write($"[bossbook] {Overrides.Count} 条,其中 {off} 条关着");
		}

		// 认全部 NPC,不只是书里的
		static int IdOf(string name)
		{
			foreach (var kv in Terraria.ID.ContentSamples.NpcsByNetId)
				if (Terraria.Lang.GetNPCNameValue(kv.Key) == name) return kv.Key;
			return 0;
		}

		// 部件跟着本体那条走
		static BossOverride Override(int npcType)
			=> Overrides.TryGetValue(Canon(npcType), out var o) ? o : null;

		// 场上每个有背板的 boss 各一段,锁定的排第一
		public static List<(string Name, string Text)> OnField(int lockedType)
		{
			var seen = new HashSet<int>();
			var list = new List<(string, string)>();
			void Put(int type)
			{
				int c = Canon(type);
				if (!seen.Add(c)) return;
				string txt = For(c);
				if (!string.IsNullOrEmpty(txt)) list.Add((Terraria.Lang.GetNPCNameValue(c), txt));
			}
			Put(lockedType);
			for (int i = 0; i < Terraria.Main.maxNPCs; i++)
			{
				var n = Terraria.Main.npc[i];
				if (n != null && n.active && !n.friendly) Put(n.type);
			}
			return list;
		}

		public static string For(int npcType)
		{
			if (!UseKnowledge) return "";
			var o = Override(npcType);
			if (o == null) return Of(npcType).HowItFights;
			return o.Enabled ? o.HowItFights : "";
		}

		// 这个 boss 指定的距离
	}

	public class BossKnowledgeCommand : Terraria.ModLoader.ModCommand
	{
		public override Terraria.ModLoader.CommandType Type => Terraria.ModLoader.CommandType.Chat;
		public override string Command => "bossbook";
		public override string Description => "开关 boss 背板,用来做对照实验";
		public override string Usage => "/bossbook";

		public override void Action(Terraria.ModLoader.CommandCaller caller, string input, string[] args)
		{
			BossBook.UseKnowledge = !BossBook.UseKnowledge;
			Terraria.Main.NewText($"[TerraBlind] boss playbook {(BossBook.UseKnowledge ? "ON" : "OFF")}", 200, 200, 120);
			DiagLog.Write($"[bossbook] UseKnowledge={BossBook.UseKnowledge}");
		}
	}
}
