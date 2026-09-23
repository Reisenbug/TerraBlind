using System.Collections.Generic;
using System.ComponentModel;
using Terraria.ModLoader;
using Terraria.ModLoader.Config;

namespace TerraBlind
{
	// 一个 boss 一条,开局自动铺满,文本默认就是代码里那段
	public class BossOverride
	{
		// 哪个 boss。【自动铺进来的,不用自己填】,改坏了就对不上,那一条静默失效
		[DefaultValue("")]
		public string Boss = "";

		[DefaultValue(true)]
		public bool Enabled = true;

		[System.ComponentModel.DataAnnotations.StringLength(4000)]
		[DefaultValue("")]
		public string HowItFights = "";

		public override string ToString()
			=> (string.IsNullOrWhiteSpace(Boss) ? "?" : Boss) + (Enabled ? "" : " [off]");
	}

	// 全是本机行为(画覆盖层、给自己加 buff、改自己脚下的地形),所以 ClientSide
	public class Config : ModConfig
	{
		public override ConfigScope Mode => ConfigScope.ClientSide;

		public static Config I => ModContent.GetInstance<Config>();

		[Header("Assist")]
		// 默认关:用户没下指令就改地形属于"没说一声就动了人家的世界"
		[DefaultValue(false)]
		public bool FreezeLavaUnderFeet;

		[DefaultValue(false)]
		public bool AlwaysGillsAndShine;

		[Header("Nav")]
		// 默认关:它改的是调了几个月的选边热路径
		[DefaultValue(false)]
		public bool AvoidDanger;

		// 默认关:会自己挥武器,而且会和放置/挖掘抢 Use
		[DefaultValue(false)]
		public bool FightBack;

		// 默认关:boss 战自己走位躲冲撞,会抢 Move/Jump,和寻路互斥
		[DefaultValue(false)]
		public bool DodgeBoss;

		[DefaultValue(true)]
		public bool AimAhead = true;

		// 对照组:走位意图不问 Jev,每 300ms 随机选一次,反射层照旧。用来看 Jev 到底值多少
		[DefaultValue(false)]
		public bool RandomBrain;

		// 关掉就不给 Jev 任何 boss 背板,只留场地和通用字段。用来验知识到底值多少
		[DefaultValue(true)]
		public bool BossKnowledge;

		// 游戏内改背板。【单独开一页】:打法是一整段话,挤在列表那一行里没法读也没法改
		[SeparatePage]
		public List<BossOverride> BossPlaybook = new();

		// 留空就去环境变量和 ~/.typesafe_key 找。【填了就会存进 ModConfigs/TerraBlind.json】
		[DefaultValue("")]
		public string TypeSafeKey;

		[Header("Debug")]
		[DefaultValue(true)]
		public bool ShowOverlay;

		// 单独开关:只想看 Jev 在想什么,不用连带打开一屏的格子覆盖层
		[DefaultValue(false)]
		public bool ShowJevHud;

		// A* 的轨迹/探索点最密,盖信息最多,单独一条
		[DefaultValue(false)]
		public bool ShowPlannerTrails;

		public override void OnChanged()
		{
			PathVisSystem.Enabled = ShowOverlay;
			PathVisSystem.ShowPlanner = ShowPlannerTrails;
			RiskLayer.Enabled = AvoidDanger;
			Combat.Enabled = FightBack;
			Combat.UseLead = AimAhead;
			Dodge.Enabled = DodgeBoss;
			if (Dodge.RandomBrain != RandomBrain) DiagLog.Write($"[dodge] 大脑换成 {(RandomBrain ? "随机" : "Jev")}");
			Dodge.RandomBrain = RandomBrain;
			BossBook.UseKnowledge = BossKnowledge;
			// 【这里也要铺】。OnChanged 会在 PostSetupContent 之后再响一次,
			// 而那次的实例列表还是空的 -- 不补就把刚铺好的 24 条清回 0
			BossBook.Seed(BossPlaybook);
			BossBook.SetOverrides(BossPlaybook);
			JevHud.Enabled = ShowJevHud;
		}
	}

	// 【必须等 PostSetupContent】。ContentSamples.Initialize 在 SetupContent 之后才跑
	// (ModContent.cs:513),而 ModConfig.OnLoaded 在 Mod.Load 之前 -- 那时候 boss 列表还是空的
	public class BossPlaybookSeeder : Terraria.ModLoader.ModSystem
	{
		public override void PostSetupContent()
		{
			var c = Config.I;
			if (c == null) { DiagLog.Write("[bossbook] PostSetupContent 时 Config.I 还是空的,没铺成"); return; }
			int before = c.BossPlaybook.Count;
			BossBook.Seed(c.BossPlaybook);
			DiagLog.Write($"[bossbook] 铺了 {c.BossPlaybook.Count - before} 条,现在共 {c.BossPlaybook.Count} 条");
			BossBook.SetOverrides(c.BossPlaybook);
		}
	}
}
