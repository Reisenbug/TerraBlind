using Terraria;

namespace TerraBlind
{
	// 真实地形挡住人怎么办 —— 挖开。地狱要塞的墙、矿脉、山体横在路上时,这是唯一的解法。
	//
	// 判据只有这一份:HellDeck 里原本有一套(DigWayForward),DeckBuilder 又漏写了一套,
	// 于是同样的墙在老路径上能过、新路径上卡死。所有"被地形挡住"都该调这里。
	public static class ClearWay
	{
		// 手上最好的镐。没镐返回 -1 —— 那是真的过不去,得让调用方报出来
		public static int PickSlot(Player p)
		{
			int slot = -1, best = 0;
			for (int i = 0; i < 10 && i < p.inventory.Length; i++)
			{
				var it = p.inventory[i];
				if (it != null && !it.IsAir && it.pick > best) { best = it.pick; slot = i; }
			}
			return slot;
		}

		// 挖一格。够得着且有镐才动手;开挖了返回 true,这一帧就交给它
		public static bool Dig(Player p, int x, int y, string why)
		{
			if (!Predicates.InBounds(x, y) || !Predicates.IsSolid(x, y)) return false;
			if (!p.IsInTileInteractionRange(x, y, Terraria.DataStructures.TileReachCheckSettings.Simple)) return false;
			int pk = PickSlot(p);
			if (pk < 0) return false;
			if (ItemUseCoordinator.IsActive) return true;
			ItemUseCoordinator.Start(new ItemUseRequest { TargetWx = x, TargetWy = y, Slot = pk, Strict = true });
			DiagLog.Write($"[clearway] 挖({x},{y}) {why} type={Main.tile[x, y].TileType}");
			return true;
		}

		// 前进方向那一列,身子占的 3 行里有实心就挖掉。挖了返回 true(这一帧别再按方向键)
		public static bool Forward(Player p, int dir, string why = "挡路")
		{
			var (bl, br) = Predicates.BodyCols(p);
			int col = dir > 0 ? br + 1 : bl - 1;
			int fy = ActExecutor.OriginCy(p);
			for (int r = 0; r < 3; r++)
				if (Dig(p, col, fy - r, why)) return true;
			return false;
		}

		// 头顶挡着(跳不上去/柱子顶不上去)。身子跨两列,两列都要清
		public static bool Above(Player p, string why = "头顶挡着")
		{
			var (bl, br) = Predicates.BodyCols(p);
			int fy = ActExecutor.OriginCy(p);
			for (int c = bl; c <= br; c++)
				if (Dig(p, c, fy - 3, why)) return true;
			return false;
		}

		// 有没有镐。没有的话"挖开"这条路根本不存在,调用方要另想办法而不是干等
		public static bool HasPick(Player p) => PickSlot(p) >= 0;
	}
}
