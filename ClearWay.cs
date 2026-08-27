using Terraria;

namespace TerraBlind
{
	// 真实地形挡住人怎么办 —— 挖开。地狱要塞的墙、矿脉、山体横在路上时,这是唯一的解法。
	//
	// 判据只有这一份:HellDeck 里原本有一套(DigWayForward),DeckBuilder 又漏写了一套,
	// 于是同样的墙在老路径上能过、新路径上卡死。所有"被地形挡住"都该调这里。
	public static class ClearWay
	{
		// 手上最好的镐。没镐返回 -1 -- 那是真的过不去,得让调用方报出来。
		// 【背包里的也算】:只扫热键栏的话,镐在背包里就等于"没镐",挖掘边一条都不生成,
		// 人明明带着镐却被判成过不去。找到就用 HomeSlot 搬上热键栏(和放置料同一套搬运)。
		public static int PickSlot(Player p)
		{
			int slot = -1, best = 0;
			for (int i = 0; i < 10 && i < p.inventory.Length; i++)
			{
				var it = p.inventory[i];
				if (it != null && !it.IsAir && it.pick > best) { best = it.pick; slot = i; }
			}
			if (slot >= 0) return slot;
			// 热键栏没有 -> 翻背包(10..53),搬最好的那把上来
			int bagSlot = -1, bagBest = 0;
			for (int i = 10; i < 54 && i < p.inventory.Length; i++)
			{
				var it = p.inventory[i];
				if (it != null && !it.IsAir && it.pick > bagBest) { bagBest = it.pick; bagSlot = i; }
			}
			if (bagSlot < 0) return -1;
			// 【只在主线程搬】。规划器(含后台的 ExploreCoordinator)也调这个函数,
			// 后台线程动 p.inventory 会和主线程撞车 -- Trap.ScanAhead 那次并发损坏就是这么来的。
			// 后台只需要知道"有没有镐",返回背包里那个槽号就够。
			if (MazeWand.OnMainThread)
			{
				int hb = PlaceAction.HomeSlot(bagSlot);
				DiagLog.Write($"[clearway] 镐在背包槽{bagSlot}(pick={bagBest}),搬到热键{hb}");
				return hb;
			}
			return bagSlot;
		}

		// 挖一格。够得着且有镐才动手;开挖了返回 true,这一帧就交给它
		public static bool Dig(Player p, int x, int y, string why)
		{
			// 平台不挖:能直接穿过去/跳上去,挖它是白费镐和时间
			if (!Predicates.IsWall(x, y)) return false;
			if (!p.IsInTileInteractionRange(x, y, Terraria.DataStructures.TileReachCheckSettings.Simple)) return false;
			int pk = PickSlot(p);
			if (pk < 0) return false;
			if (ItemUseCoordinator.IsActive) return true;
			ItemUseCoordinator.Start(new ItemUseRequest { TargetWx = x, TargetWy = y, Slot = pk, Strict = true });
			DiagLog.Write($"[clearway] 挖({x},{y}) {why} type={Main.tile[x, y].TileType}");
			return true;
		}

		// 前进方向那一列,身子占的 3 行里有实心就挖掉。挖了返回 true(这一帧别再按方向键)
		// 【不挖脚那一行】:一格高的台阶就在 fy,跳一下就上去,挖它等于把路拆了。
		// 日志:刚放好的衔接方块(910,1037) 40帧后被当"挡路"挖掉,桥就断在那儿
		public static bool Forward(Player p, int dir, string why = "挡路")
		{
			var (bl, br) = Predicates.BodyCols(p);
			int col = dir > 0 ? br + 1 : bl - 1;
			int fy = ActExecutor.OriginCy(p);
			// 台阶只有一格高就别动它;两格及以上人跳不过去,那才是真挡路
			bool step = Predicates.IsWall(col, fy) && !Predicates.IsWall(col, fy - 1);
			for (int r = step ? 1 : 0; r < 3; r++)
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
