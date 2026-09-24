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
					"克苏鲁之眼通常先悬停在玩家头顶上方,蓄一会儿,然后朝玩家所在的位置直线冲刺。"
					+ "需要横向速度。站着不动等它冲下来必然被撞。"
					+ "它还会召唤servant小怪。",
			},

			[Terraria.ID.NPCID.KingSlime] = new BossInfo
			{
				HowItFights =
					"史莱姆王的打法就一句话:一直远离它,别停。它会瞬移到玩家身上,"
					+ "而瞬移后的接触有伤害。站着不动就容易吃到。所以要时刻保持移动。"
			},

			[Terraria.ID.NPCID.BrainofCthulhu] = new BossInfo
			{
				HowItFights =
					"克苏鲁之脑分两个阶段。一阶段本体无敌,需要攻击场上的creeper"
					+ "先把爬行者清光,清光的那一刻本体才会现身。爬行者是一次性的,不会再刷。"
					+ "二阶段本体会瞬移到玩家附近再撞过来。全程碰到爬行者或者本体都掉血。"
					+ "场地是个正方形房间,往后退退不了多远就到墙上,"
					+ "所以单纯拉开距离在这里不太行得通。"
					+ "场上还有爬行者且使用近战武器的时候,待在它们下方比待在上方安全,"
					+ "它们由本体向玩家连线方向进行撞击。尽可能让它们在自己和brain的连线上。",
			},

			[Terraria.ID.NPCID.EaterofWorldsHead] = new BossInfo
			{
				HowItFights =
					"世界吞噬者是一条几十节的长虫,穿墙钻土,整条身体都会撞人。"
					+ "打法没什么花样:【别碰到它就行】。不用刻意拉很远。"
					+ "保持个不会擦到的距离,一直打就是了。报给你的距离说的是离你最近的那一节,"
					+ "不是头。身体一样有碰撞伤害。",
			},

			[Terraria.ID.NPCID.SkeletronHead] = new BossInfo
			{
				HowItFights =
					"骷髅王有一个头和两只手。两只手还在的时候先打手。"
					+ "打掉一只手之后它开始发射弧形轨迹的的骷髅头弹幕,两只手都没了发射得更快。"
					+ "那时候要盯着弹幕躲。它的头每隔一段时间会突然高速旋转着撞过来。"
					+ "离高速移动的头远一点,看到它速度起来了就别待在它的路线上。"
					+ "和手、和头都要留出距离。"
					+ "钩爪能让你瞬间换一个方向或者拔高,避开正在扫过来的那一只。"
					+ "只在地面上左右跑的话,躲避就只剩一个维度,而制导骷髅头对我们的命中率会很高。"
					+ "钩爪可以往上勾,也可以往左下右下勾。本场战斗中，换一个高度常常比继续横跑躲得开。"
					+ "两只手都打掉之后，垂直方向的机动更有用。",
			},

			[Terraria.ID.NPCID.Spazmatism] = new BossInfo
			{
				HowItFights =
					"双子魔眼是两只分开飞的眼睛,优先进攻是魔焰眼。"
					+ "优先躲开碰撞。"
					+ "一阶段boss移动逻辑：人靠近它就退,人退开它就跟上来,"
					+ "所以横向的距离根本调不动 -- 主动往它那边走,只会和它射出来的弹幕迎头相撞。"
					+ "【30 格以内就危险了】:那个范围里喷火够得着,而且冲刺来不及反应,站得比它远才有余地,保持 40 格左右。"
					+ "一阶段的魔焰眼除了冲刺之外一直在追人:【上下不能停】。"
					+ "躲冲刺要的是横向和竖直一起变向,它锁的是起冲那一刻的位置。"
					+ "它掉到一半血会脱壳进二阶段,那之后的循环是:【喷一段时间火,然后连冲六次,再重来】。"
					+ "喷火是一道连续的火焰流,不是一发一发的 -- 站在里面每一帧都在掉血,"
					+ "所以【只要开始挨火就立刻走开】,离得越近烧得越狠。"
					+ "【二阶段的魔焰眼要时刻远离】,不只是挨火的时候。"
					+ "那六次冲刺的碰撞比喷火还疼,冲起来的时候一定会有被贴近的一瞬间,"
					+ "那一下靠变向躲:它锁的是起冲那一刻的位置,横向或竖直换一步就扑空了。"
					+ "【怎么看出它在冲】:nearest_part_speed_cells_per_second 接近"
					+ "nearest_part_fastest_in_the_last_second 就是正在冲的那一下,"
					+ "速度掉下来说明这一轮冲完了,那是拉开距离和输出的空档。"
					+ "魔焰眼的弹幕伤害带 debuff,吃一发的代价比伤害数字大。"
					+ "激光眼最危险的是冲撞不是激光。它两个阶段都和人保持距离,"
					+ "激光打在身上一阶段几乎不痛、二阶段也只是稍微痛一点,不值得为躲激光乱走;"
					+ "但它撞过来一下就是一大块血 -- 两只眼睛都要当成会撞人的东西躲,"
					+ "不能因为在打魔焰眼就放着激光眼不管。"
					+ "【场上始终是两只眼睛,躲的时候两只都要算】,threats 里能看到另一只在哪。"
					+ "背对一只跑常常正好撞进另一只,往两只都不在的那一侧走才是真的躲开。"
					+ "被两只夹在中间的时候,先远离魔焰眼。",
			},

			[Terraria.ID.NPCID.TheDestroyer] = new BossInfo
			{
				HowItFights = "毁灭者是一条很长的机械虫。【一定不能被它的头撞到】,身体也要远离。如有需要，清理探针小怪。",
			},

			[Terraria.ID.NPCID.SkeletronPrime] = new BossInfo
			{
				HowItFights = "机械骷髅王的头要远离。"
			},

			[Terraria.ID.NPCID.Plantera] = new BossInfo
			{
				HowItFights =
					"世纪之花分两个阶段,【血量掉到一半就进二阶段】。"
					+ "一阶段绕着它转:保持 40 格左右的距离,沿着圆周一直走,左上右下地绕回来。"
					+ "要的是轨迹始终围着它。"
					+ "二阶段不再绕圈,就是正常地拉开距离躲。"
					+ "【二阶段它身上会伸出触手】,触手碰一下掉的血比本体还多,"
					+ "而且它是贴着人甩过来的 -- threats 里能看到触手在哪,"
					+ "离本体远不等于离触手远,要按最近的那条触手算距离。",
			},

			[Terraria.ID.NPCID.QueenBee] = new BossInfo
			{
				HowItFights =
					"蜂王悬在头顶上方的时候,横向移动。"
					+ "而它和自己处在差不多同一高度、横着冲过来的时候,上下移动。"
			},

			[Terraria.ID.NPCID.Deerclops] = new BossInfo
			{
				HowItFights =
					"尽量与boss平齐。此boss伤害不高，脚底下无碰撞伤害。需要始终在boss几格内。",
			},

			// 恶鬼离得比肉山近时按这条算(Boss() 取最近的)
			[Terraria.ID.NPCID.TheHungry] = new BossInfo
			{
				HowItFights =
					"恶鬼是挂在肉山身上的一串小怪,伸得很长,碰到就掉血。"
					+ "离它保持十格的安全距离。与肉山的距离规则冲突时，优先这一条。"
			},

			[Terraria.ID.NPCID.WallofFlesh] = new BossInfo
			{
				HowItFights =
					"肉山是一堵横跨整个屏幕的墙,从地狱的一头推到另一头,只会水平移动,永远不会停。"
					+ "它身上挂着一串叫恶鬼的小怪,伸得很长,碰到一样掉血。"
					+ "打法是往它的反方向跑,边跑边打。"
					+ "距离是 60 格左右的一个区间,两头都不能碰:离太远会被它的激光扫满,"
					+ "离太近又会被身上那串恶鬼打到。跑过头了就该收回来,贴太近了就该退开,"
					+ "始终卡在中间那一段 -- 看 nearest_part_cells_horizontal 判断自己偏到哪一头了。"
					+ "只要 incoming_projectiles 里出现它的激光,就一刻不停地动,"
					+ "站定一下就会被扫到;但这不等于一路往远处跑。"
					+ "要的是在区间里持续移动,不是拉开距离。"
					+ "它的血越少推得越快。"
					+ "场地是完全平的,跳起来毫无意义:既躲不开它也够不到它," // 专为视频准备的场地。 TODO
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
