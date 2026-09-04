using Terraria;

namespace TerraBlind
{
	// 一格坐标只有四种可能,这是全项目唯一的一份判据。
	//
	// 以前 Standable 有四份互不相同的实现(PathPlanner 认平台不算站、StateSpacePlanner 只查 2 行岩浆、
	// ActionGraphPlanner 查满 3 行但不查岩浆),再加上散落的 PlatformAnchor / nearSupport / DropLands /
	// OverLavaVoid。同一个坑在不同路径上表现不同,就是这么来的。
	public enum Cell
	{
		Lava,      // 身子那 3 行里有岩浆,不通,没有价
		Stand,     // 现在就能站:格子空着,脚下有实处
		Build,     // 放一块平台就能站:格子空着,脚下没实处,但周围锚得住
		Pillar,    // 锚不住,只能砌柱子上去。还是通的,只是最贵
		Solid      // 实心,要挖。价按 DigTable,不在这四种通行价里
	}

	public static class CellKind
	{
		public const int BodyRows = 3;   // 人 42px 高 = 3 行

		public static Cell Of(int x, int y)
		{
			if (!Predicates.InBounds(x, y)) return Cell.Solid;
			// 岩浆先判:碰上就是重开,比"能不能站"优先
			for (int r = 0; r < BodyRows; r++)
				if (Predicates.IsLava(x, y - r)) return Cell.Lava;
			// 脚下那格也不能是岩浆,站在岩浆面上等于泡在里面
			if (Predicates.IsLava(x, y + 1)) return Cell.Lava;

			// 身子占的 3 行有实心 = 这格是墙,归挖
			for (int r = 0; r < BodyRows; r++)
				if (Predicates.IsWall(x, y - r)) return Cell.Solid;
			// 平台【占着的那一格】人站不进去(站的是它上面那格)。漏了这条,摞起来的平台
			// 会被判成可站,人却穿不下去也站不住 --- 今天那个平台梯死循环就是这么来的。
			for (int r = 0; r < BodyRows; r++)
				if (Predicates.IsPlatform(x, y - r)) return Cell.Solid;

			if (Predicates.IsGround(x, y + 1)) return Cell.Stand;
			// 锚点判据【跟着料走】:岩浆格只能用方块(平台会被烧),而方块的锚点比平台严
			return MazeWand.AnchorFor(x, y) ? Cell.Build : Cell.Pillar;
		}

		// 通行吗。Lava 之外全通,只是价钱不同
		public static bool Passable(int x, int y) => Of(x, y) != Cell.Lava;

		// 不用造任何东西就能站
		public static bool Stands(int x, int y) => Of(x, y) == Cell.Stand;

		// 造完落脚点就能站(含现在就能站的)
		public static bool CanStandAfterBuild(int x, int y)
		{
			var k = Of(x, y);
			return k == Cell.Stand || k == Cell.Build || k == Cell.Pillar;
		}
	}
}
