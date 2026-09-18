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
	}

	public static class BossBook
	{
		// 【对照实验:关掉背板】。蜂王零知识一遍过,所以要验的是"知识到底贡献了多少"。
		// 只关 HowItFights,场地和禁用动作照旧 -- 那两样是事实不是打法
		public static bool UseKnowledge = true;

		const string OpenArena = "一整片平台,左右都能跑,没有坑也没有墙";

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

			[Terraria.ID.NPCID.WallofFlesh] = new BossInfo
			{
				Arena = "地狱里一条完全平整的长桥,一路平到底,没有高低差也没有可以跳上去的东西",
				// 【竖直动作全禁】。地面是平的,跳起来既躲不开也够不到,落地那段还没法变向
				// 【竖直全禁,外加不许主动靠近】。它只会推进,迎上去就是送 -- 最多站住不动
				Banned = new[] { DodgeAct.Up, DodgeAct.Float, DodgeAct.Dive, DodgeAct.Grapple, DodgeAct.Close },
				HowItFights =
					"肉山是一堵横跨整个屏幕的墙,从地狱的一头推到另一头,【只会水平移动,永远不会停】。"
					+ "它身上挂着一串叫恶鬼的小怪,伸得很长,碰到一样掉血。"
					+ "打法是往它的反方向跑,边跑边打 -- 停下来就会被推平。"
					+ "【距离有上下两头,都不能碰】:离太远会被它的激光扫满,"
					+ "离太近又会被身上那串恶鬼打到,所以要卡在中间那一段。"
					+ "【它放激光的时候必须一直跑】,那阵子停一下就吃满;"
					+ "不放激光的时候就不用死命拉开,保持住那个不远不近的距离就行。"
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
			if (npcType == Terraria.ID.NPCID.WallofFleshEye
			 || npcType == Terraria.ID.NPCID.TheHungry
			 || npcType == Terraria.ID.NPCID.TheHungryII)
				return Terraria.ID.NPCID.WallofFlesh;
			return npcType;
		}

		static readonly BossInfo None = new();

		public static BossInfo Of(int npcType)
			=> Book.TryGetValue(Canon(npcType), out var b) ? b : None;

		public static string For(int npcType) => UseKnowledge ? Of(npcType).HowItFights : "";

		// 没有条目的 boss 也得有个场地描述,否则那个字段是空的
		public static string ArenaOf(int npcType)
		{
			string a = Of(npcType).Arena;
			return a.Length > 0 ? a : OpenArena;
		}

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
