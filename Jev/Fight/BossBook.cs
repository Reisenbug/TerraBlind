using System.Collections.Generic;

namespace TerraBlind
{
	// 每个 boss 一条。打法和场地【都只是一段话】,人打 boss 靠背板,
	// 写成 if 就得为每个 boss 编阈值 -- 那些数我一个都不知道
	public class BossInfo
	{
		public string HowItFights = "";
		// 这个 boss 的场地长什么样。以前写死"一整片平台",对蜂巢和地狱都是谎话
		public string Arena = "";
		// 这一场不许用的意图。肉山那种完全平整的场地,任何竖直动作都是白白送伤害
		public DodgeAct[] Banned = System.Array.Empty<DodgeAct>();
		// 这一场该保持的水平距离(格)。0 = 不指定,按 danger 算。
		// 【只是一个数,不是一套规则】-- 有的 boss 的安全区就是不在通用公式的量程里
		public int WantCells;
		// Hold 的时候绕着它走而不是站住。【只给需要的 boss 开】:
		// 肉山那种只会平推的,绕圈就是往它怀里送
		public bool Orbit;
	}

	public static class BossBook
	{
		// 【对照实验:关掉背板】。蜂王零知识一遍过,所以要验的是"知识到底贡献了多少"。
		// 只关 HowItFights,场地和禁用动作照旧 -- 那两样是事实不是打法
		public static bool UseKnowledge = true;

		// 【说清楚上下都能走】。只说"一片平台"会让它以为高度是稀缺的,于是一路往上顶
		const string OpenArena = "一层层斜坡平台摞起来的场地,左右都能跑。"
			+ "平台可以穿:按住下就直接落到下一层,往上跳也能穿过去,所以换高度很便宜。"
			+ "最上面是实心方块封顶,顶到那里就再也上不去了;最下面一层是平台,底下是空的";

		static readonly Dictionary<int, BossInfo> Book = new()
		{
			[Terraria.ID.NPCID.EyeofCthulhu] = new BossInfo
			{
				Arena = OpenArena,
				HowItFights =
					"克苏鲁之眼通常先悬停在玩家头顶上方,蓄一会儿,然后朝玩家所在的位置直线冲刺。"
					+ "所以它悬停不动的时候正是最危险的时候,要提前把横向速度拉起来,"
					+ "站着不动等它冲下来必然被撞。它冲过去之后会有一段收招,那时可以贴近输出。"
					+ "它还会召唤服务者小怪,小怪贴身也掉血。",
			},

			[Terraria.ID.NPCID.KingSlime] = new BossInfo
			{
				Arena = OpenArena,
				HowItFights =
					"史莱姆王的打法就一句话:一直远离它,别停。它会瞬移到玩家身上,"
					+ "而瞬移本身带伤害 -- 站着不动就是把自己送到落点上,所以要时刻保持移动。"
					+ "它的速度跟着玩家走,玩家越快它越快,甩不掉它,别指望拉开就安全。"
					+ "不存在'它收招了可以贴近输出'的窗口,离得近就是在挨打。",
			},

			[Terraria.ID.NPCID.BrainofCthulhu] = new BossInfo
			{
				Arena = "一个正方形房间,四面都是墙,退无可退",
				HowItFights =
					"克苏鲁之脑分两个阶段。一阶段本体刀枪不入,场上一圈爬行者绕着它转,"
					+ "先把爬行者清光,清光的那一刻本体才会现身 -- 爬行者是一次性的,不会再刷。"
					+ "二阶段本体会瞬移到玩家附近再撞过来。全程碰到爬行者或者本体都掉血。"
					+ "场地是个正方形房间,往后退退不了多远就到墙上,"
					+ "所以单纯拉开距离在这里不太行得通。"
					+ "还有一点:【场上还有爬行者的时候,待在它们下方比待在上方安全】,"
					+ "它们绕着本体转,从下面走位比在上面被它们压着好受。",
			},

			[Terraria.ID.NPCID.EaterofWorldsHead] = new BossInfo
			{
				Arena = "腐化之地的竖井和土层,它穿墙钻土,墙挡不住它",
				HowItFights =
					"世界吞噬者是一条几十节的长虫,穿墙钻土,整条身体都会撞人。"
					+ "打法没什么花样:【别碰到它就行】。不用刻意拉很远,也拉不开 -- 它会一直钻过来,"
					+ "保持个不会擦到的距离,一直打就是了。报给你的距离说的是离你最近的那一节,"
					+ "不是头 -- 身体从背后钻出来一样掉血,所以看的是最近那节有多近。",
			},

			[Terraria.ID.NPCID.SkeletronHead] = new BossInfo
			{
				Arena = OpenArena,
				HowItFights =
					"骷髅王有一个头和两只手。【两只手还在的时候先打手】,手比头好打也更危险。"
					+ "打掉一只手之后它开始发射自动制导的骷髅头弹幕,两只手都没了发射得更快 -- "
					+ "那时候要盯着弹幕躲。最要命的是头:它会突然高速旋转着撞过来,"
					+ "【离高速移动的头远一点】,看到它速度起来了就别待在它的路线上。"
					+ "和手、和头都要留出距离,但场地没有边界,不用担心退到墙上。"
					+ "手和头都是从固定的中心荡过来的,每一下都有固定的弧线,"
					+ "钩爪能让你瞬间换一个方向或者拔高,避开正在扫过来的那一只。"
					+ "只在地面上左右跑的话,躲避就只剩一个维度,而制导骷髅头会从水平方向追上来。"
					+ "钩爪可以往上勾,也可以往左下右下勾,换一个高度常常比继续横跑躲得开。"
					+ "两只手都打掉之后弹幕会变密,那时候垂直方向的机动更有用。",
			},

			[Terraria.ID.NPCID.Spazmatism] = new BossInfo
			{
				Arena = OpenArena,
				// 喷火范围约 30 格,而 boss 一直在逼近 -- 定在 30 就等于常年站在火里
				WantCells = 40,
				HowItFights =
					"双子魔眼是两只分开飞的眼睛,正在打的是魔焰眼(绿色那只)。"
					+ "【碰到它掉的血比吃弹幕多得多】,躲开碰撞永远排在最前面。"
					+ "【这两只眼睛自己会跟人保持距离】:人靠近它就退,人退开它就跟上来,"
					+ "所以横向的距离根本调不动 -- 主动往它那边走,只会和它射出来的弹幕迎头相撞。"
					+ "【30 格以内就危险了】:那个范围里喷火够得着,而且冲刺起手到撞上只有十几帧,"
					+ "来不及反应 -- distance_i_asked_for 是 30 就是这个道理,站得比它远才有余地。"
					+ "一阶段的魔焰眼除了冲刺之外一直在追人:【上下不能停】。"
					+ "躲冲刺要的是横向和竖直一起变向,它锁的是起冲那一刻的位置。"
					+ "它掉到一半血会脱壳进二阶段,那之后的循环是:【喷一段时间火,然后连冲六次,再重来】。"
					+ "喷火是一道连续的火焰流,不是一发一发的 -- 站在里面每一帧都在掉血,"
					+ "所以【只要开始挨火就立刻走开】,离得越近烧得越狠。"
					+ "那六次冲刺的碰撞比喷火还疼,冲起来的时候一定会有被贴近的一瞬间,"
					+ "那一下靠变向躲:它锁的是起冲那一刻的位置,横向或竖直换一步就扑空了。"
					+ "【怎么看出它在冲】:boss_speed_cells_per_second 接近"
					+ "boss_fastest_in_the_last_second 就是正在冲的那一下,"
					+ "速度掉下来说明这一轮冲完了,那是拉开距离和输出的空档。"
					+ "【魔焰眼的火会点燃自己】,烧起来之后一直在掉血,所以吃一发火的代价比伤害数字大得多。"
					+ "【激光眼最危险的是冲撞不是激光】:它两个阶段都和人保持距离,"
					+ "激光打在身上一阶段几乎不痛、二阶段也只是稍微痛一点,不值得为躲激光乱走;"
					+ "但它撞过来一下就是一大块血 -- 两只眼睛都要当成会撞人的东西躲,"
					+ "不能因为在打魔焰眼就放着激光眼不管。"
					+ "【场上始终是两只眼睛,躲的时候两只都要算】,other_enemies 里能看到另一只在哪。"
					+ "背对一只跑常常正好撞进另一只,往两只都不在的那一侧走才是真的躲开。"
					+ "【被两只夹在中间的时候,先离开魔焰眼】:两边都有东西,没有空的那一侧了,"
					+ "这时候比的是哪边更疼 -- 魔焰眼的碰撞和喷火都比激光眼重,"
					+ "为了躲激光眼而朝魔焰眼挪是这场里最亏的一步。",
			},

			[Terraria.ID.NPCID.Plantera] = new BossInfo
			{
				Arena = OpenArena,
				Orbit = true,
				// 绕圈得有个半径,不给就从 46 格一路收到 2 格(切线方向没人维持距离)。
				// 40 是实测开场自然稳住的那一段,不是我挑的数
				WantCells = 40,
				HowItFights =
					"世纪之花分两个阶段,【血量掉到一半就进二阶段】。"
					+ "一阶段绕着它转:保持一个固定的距离,沿着圆周一直走,左上右下地绕回来。"
					+ "所以一阶段选 Hold,横向会自动沿切线走 -- 要的是轨迹始终围着它,别停下。"
					+ "二阶段不再绕圈,就是正常地拉开距离躲。"
					+ "【二阶段它身上会伸出触手】,触手碰一下掉的血比本体还多,"
					+ "而且它是贴着人甩过来的 -- other_enemies 里能看到触手在哪,"
					+ "离本体远不等于离触手远,要按最近的那条触手算距离。",
			},

			[Terraria.ID.NPCID.QueenBee] = new BossInfo
			{
				Arena = OpenArena,
				HowItFights =
					"蜂王的两种威胁要躲的方向【正好相反】。它悬在头顶上方的时候,"
					+ "掉下来的东西是往下砸的,这时候横着跑躲得开,上下动反而是迎上去。"
					+ "而它和自己处在差不多同一高度、横着冲过来的时候,左右跑是跟它抢同一条线,"
					+ "这时候要的是快速换个高度让它从那条线上扑空 -- 跳起来或者往下落都行,"
					+ "哪边快就走哪边。"
					+ "所以先看 i_am_above_the_boss_by 和 boss_cells_vertical 判断它在哪一头,"
					+ "再决定这一下该横着躲还是上下躲。",
			},

			[Terraria.ID.NPCID.Deerclops] = new BossInfo
			{
				Arena = OpenArena,
				HowItFights =
					"鹿角怪的节奏是【远近交替】:拉开一段就放一轮弹幕,靠近了再放一轮,来回循环。"
					+ "【关键是别离太远】 -- 离得远反而是它弹幕覆盖得最狠的时候,"
					+ "近身反倒有安全的间隙。所以不要一味后退,把距离控制在中近,"
					+ "跟着它那一轮弹幕的节奏进退。"
					+ "【标准打法是站在它面前反复跳起又落地】:跳起来是为了让弹幕从脚下过去,"
					+ "落地是为了逼它出下一招 -- 一直飘在空中它就不出手,节奏也就断了。"
					+ "跳的高度不用很高,离地十来格以内就够,人始终待在它跟前,"
					+ "别跑远也别悬着不下来。",
			},

			// 恶鬼自己一条。【它的规则覆盖肉山】:Boss() 取最近的那个,恶鬼贴得近就按这条算
			[Terraria.ID.NPCID.TheHungry] = new BossInfo
			{
				Arena = "地狱里一条完全平整的长桥,一路平到底",
				Banned = new[] { DodgeAct.Up, DodgeAct.Float, DodgeAct.Dive, DodgeAct.Grapple, DodgeAct.Close },
				WantCells = 10,
				HowItFights =
					"恶鬼是挂在肉山身上的一串小怪,伸得很长,碰到就掉血。"
					+ "它跟着肉山走,所以【离它至少十格】,这条比和肉山保持的那个距离更要紧 -- "
					+ "两个要求冲突的时候听这一条的,先把恶鬼甩开。",
			},

			[Terraria.ID.NPCID.WallofFlesh] = new BossInfo
			{
				Arena = "地狱里一条完全平整的长桥,一路平到底,没有高低差也没有可以跳上去的东西",
				// 【只禁竖直】。Close 现在收到 want(这一场是 60)就停,不会再扑到恶鬼嘴里
				Banned = new[] { DodgeAct.Up, DodgeAct.Float, DodgeAct.Dive, DodgeAct.Grapple },
				// 通用公式上限才 20 格,而这一场的安全区在 60 -- 量程根本不重合
				WantCells = 60,
				HowItFights =
					"肉山是一堵横跨整个屏幕的墙,从地狱的一头推到另一头,【只会水平移动,永远不会停】。"
					+ "它身上挂着一串叫恶鬼的小怪,伸得很长,碰到一样掉血。"
					+ "打法是往它的反方向跑,边跑边打 -- 停下来就会被推平。"
					+ "【距离是一个区间,两头都不能碰】:离太远会被它的激光扫满,"
					+ "离太近又会被身上那串恶鬼打到。跑过头了就该收回来,贴太近了就该退开,"
					+ "始终卡在中间那一段 -- 看 cells_further_than_i_asked_for 判断自己偏到哪一头了。"
					+ "【只要 incoming_projectiles 里出现它的激光,就一刻不停地动】,"
					+ "站定一下就会被扫到;但这不等于一路往远处跑,跑出那个区间照样吃满 -- "
					+ "要的是在区间里持续移动,不是拉开距离。"
					+ "它的血越少推得越快,这一点会让那个距离越来越难守。"
					+ "场地是完全平的,跳起来毫无意义:既躲不开它也够不到它,"
					+ "而且滞空的时候横向速度反而不好调整。全程贴着地面跑就行。",
			},
		};

		// 玩家这一局带着什么本事。【也只是一段话】,组合技尤其不该写成状态机
		public const string Abilities =
			"身上有羽落药水:不按上下键时下落速度只有平常的三分之一,按住上键只有十分之一 -- "
			+ "等于能在空中悬停,贴着地面冲过来的东西这样就撞不到。按下键恢复正常下落速度。"
			+ "还有钩爪:甩出去勾住方块,【勾住之后一定要马上跳一次】把自己弹开,"
			+ "不跳就会被慢慢拉过去,那不是位移是送死。跳出来能拿到一段速度,而且二段跳会重置。"
			+ "钩爪主要用来急转向和急拔高:往上勾、勾住就跳、同时按住上键能窜得很高,"
			+ "往左下右下勾则能快速换到另一个高度。荡出去的那一段改不了方向,吊着不跳也打不到人。";

		// 【部件要查到本体那条】。Boss() 返回的可能是蠕虫的某一节或者骷髅王的手,
		// 直接查表会返回空 -- 知识就在最需要的时候悄悄消失了
		static int Canon(int npcType)
		{
			if (npcType == Terraria.ID.NPCID.EaterofWorldsBody
			 || npcType == Terraria.ID.NPCID.EaterofWorldsTail)
				return Terraria.ID.NPCID.EaterofWorldsHead;
			if (npcType == Terraria.ID.NPCID.SkeletronHand)
				return Terraria.ID.NPCID.SkeletronHead;
			// 【恶鬼不映射到肉山】。映射过去就继承了 60 格,而恶鬼永远贴在人身边 --
			// 于是"离肉山 60"被当成"离恶鬼 60",人反而被吸到肉山脸上
			if (npcType == Terraria.ID.NPCID.WallofFleshEye)
				return Terraria.ID.NPCID.WallofFlesh;
			if (npcType == Terraria.ID.NPCID.TheHungryII)
				return Terraria.ID.NPCID.TheHungry;
			// 【激光眼也查魔焰眼那条】。两只眼共用一场战斗,那段话把两只都讲了
			if (npcType == Terraria.ID.NPCID.Retinazer)
				return Terraria.ID.NPCID.Spazmatism;
			// 触手和钩子查本体那条,否则绕圈和阶段判断都落空
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

		// 铺满配置。【取游戏里全部 boss,不只是书里写过的】:只铺 Book 的话,
		// 没写过背板的 boss 在配置里根本不出现,也就没法给它们写
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
			// 【书里有但没打 boss 标志的也要铺】。世界吞噬者的头、饥饿都是这种,
			// 上面那一趟扫不到,而它们恰恰是已经写好背板的
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
				// 【名字对不上要说】。人改了 Boss 那一栏就是静默失效,以为改了其实没改
				if (id == 0) { DiagLog.Write($"[bossbook] \"{o.Boss}\" 对不上任何 boss,这条没用上"); continue; }
				Overrides[id] = o;
			}
			int off = 0;
			foreach (var o in Overrides.Values) if (!o.Enabled) off++;
			DiagLog.Write($"[bossbook] {Overrides.Count} 条,其中 {off} 条关着");
		}

		// 【认全部 NPC 不只是书里的】。新写的那条背板对应的 boss 本来就不在书里,
		// 只查 Book 的话人刚写完就静默失效
		static int IdOf(string name)
		{
			foreach (var kv in Terraria.ID.ContentSamples.NpcsByNetId)
				if (Terraria.Lang.GetNPCNameValue(kv.Key) == name) return kv.Key;
			return 0;
		}

		// 【部件跟着本体那条走】。激光眼、骷髅手都没有自己的条目,
		// 不映射的话它们就永远读不到覆盖,开关也管不着它们
		static BossOverride Override(int npcType)
			=> Overrides.TryGetValue(Canon(npcType), out var o) ? o : null;

		public static string For(int npcType)
		{
			if (!UseKnowledge) return "";
			var o = Override(npcType);
			if (o == null) return Of(npcType).HowItFights;
			// 【空就是空,不回退】。条目是自动铺好的,里面本来就有原文 --
			// 人把它删干净就是想试零知识,这时候再塞回去等于开关失灵
			return o.Enabled ? o.HowItFights : "";
		}

		// 没有条目的 boss 也得有个场地描述,否则那个字段是空的
		public static string ArenaOf(int npcType)
		{
			string a = Of(npcType).Arena;
			return a.Length > 0 ? a : OpenArena;
		}

		// 这个 boss 指定的距离,没指定返回 0
		public static int WantCellsFor(int npcType) => Of(npcType).WantCells;
		public static bool OrbitFor(int npcType) => Of(npcType).Orbit;

		public static bool IsBanned(int npcType, DodgeAct act)
		{
			var b = Of(npcType).Banned;
			for (int i = 0; i < b.Length; i++)
				if (b[i] == act) return true;
			return false;
		}
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
