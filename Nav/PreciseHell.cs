using Terraria;

namespace TerraBlind
{
	// 精确模式 —— 地狱专有的一套判据。
	//
	// 地狱和地表只差一件事:岩浆。掉进去,正常玩家秒死,无敌玩家也几乎爬不上来 ——
	// 无论哪种都是这一趟作废。除掉岩浆,地狱就能完全复用地表那套。
	//
	// 所以规矩只有四条:
	//   1 轨迹上任何一格碰到岩浆 = 任务失败,不是"贵一点"
	//   2 落点必须站得住;唯一的例外是【自己造落脚点】的动作(bridge/pillar/platdown)
	//   3 碰到岩浆就是不可逆的,没有第二套"不可逆"判据
	//   4 地狱里目标几乎全是悬空的 —— 所以自造落脚点是主力手段,不是备选
	public static class PreciseHell
	{
		// 人或目标在地狱层就算数。人在地表往地狱走的那一段也得按这套来 ——
		// 等人已经站在岩浆边上再开精确模式就晚了
		public static bool Active(int goalWy)
			=> goalWy >= Main.UnderworldLayer
			|| (Main.LocalPlayer != null && ActExecutor.OriginCy(Main.LocalPlayer) >= Main.UnderworldLayer);

		// 往下探到底有没有岩浆。中间碰到实处就停 —— 那是地,不是悬空
		public const int Probe = 40;
		public static bool LavaVoidBelow(int x, int y)
		{
			for (int k = 1; k <= Probe; k++)
			{
				if (Predicates.IsLava(x, y + k)) return true;
				if (Predicates.IsGround(x, y + k)) return false;
			}
			return false;
		}

		public static bool Standable(int x, int y) => CellKind.Stands(x, y);

		// 直接跳下去能落在哪:身子那两列一起看,先撞到岩浆就是不能跳。
		// 返回落脚行,不能跳返回 -1。平台梯慢且吃料,能白掉下去就别铺。
		public static int DropLanding(int bl, int br, int feetY, int maxDrop)
		{
			for (int y = feetY + 1; y <= feetY + maxDrop; y++)
				for (int c = bl; c <= br; c++)
				{
					if (Predicates.IsLava(c, y)) return -1;
					// 落脚点要能站住,而且头顶两行得容得下人
					if (Predicates.IsGround(c, y))
						return (Standable(c, y - 1) && !Predicates.IsWall(c, y - 2)) ? y - 1 : -1;
				}
			return -1;
		}

		// 规则 2:落点合格吗。build=true 表示这条边自己会把落脚点造出来,不要求现成的地
		public static bool LandingOk(int x, int y, bool build)
		{
			if (Predicates.IsLava(x, y)) return false;          // 落点本身是岩浆
			if (build) return true;                             // 自造落脚点:地是它自己铺的
			return Standable(x, y);
		}

		// 规则 1:一整段轨迹里有没有碰到岩浆。人 3 行高,所以每一格都要连着身子一起查。
		// PhysicsSimulator 把岩浆当空气,模拟出来的弧线看着完全正常 —— 这里是唯一拦得住的地方
		public static bool PathHitsLava(int x0, int y0, int x1, int y1)
		{
			int dx = System.Math.Abs(x1 - x0), dy = System.Math.Abs(y1 - y0);
			int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
			int err = dx - dy, x = x0, y = y0;
			for (int guard = 0; guard < dx + dy + 2; guard++)
			{
				if (BodyHitsLava(x, y)) return true;
				if (x == x1 && y == y1) return false;
				int e2 = err * 2;
				if (e2 > -dy) { err -= dy; x += sx; }
				if (e2 < dx) { err += dx; y += sy; }
			}
			return false;
		}

		// 脚在 (x,y) 时身子占 y-2..y 三行,任一行泡在岩浆里都算碰上
		public static bool BodyHitsLava(int x, int y)
		{
			for (int r = 0; r < 3; r++) if (Predicates.IsLava(x, y - r)) return true;
			return false;
		}
	}
}
