using System.Collections.Generic;
using System.ComponentModel;
using Terraria.ModLoader;
using Terraria.ModLoader.Config;

namespace TerraBlind
{
	// 一条覆盖。【文本留空就不覆盖】,这样只想临时关掉某个 boss 的背板时不用先把话删了
	public class BossOverride
	{
		[DefaultValue("")]
		public string Boss = "";

		[DefaultValue(true)]
		public bool Enabled = true;

		[DefaultValue("")]
		[System.ComponentModel.DataAnnotations.StringLength(4000)]
		public string HowItFights = "";

		public override string ToString()
			=> (string.IsNullOrWhiteSpace(Boss) ? "(empty)" : Boss) + (Enabled ? "" : " [off]");
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
			Dodge.Enabled = DodgeBoss;
			BossBook.UseKnowledge = BossKnowledge;
			BossBook.SetOverrides(BossPlaybook);
			JevHud.Enabled = ShowJevHud;
		}
	}
}
