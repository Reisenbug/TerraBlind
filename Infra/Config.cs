using System.ComponentModel;
using Terraria.ModLoader;
using Terraria.ModLoader.Config;

namespace TerraBlind
{
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

		[Header("Debug")]
		[DefaultValue(true)]
		public bool ShowOverlay;

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
		}
	}
}
