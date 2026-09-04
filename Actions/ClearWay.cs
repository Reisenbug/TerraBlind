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
			// 【正在铺的桥面不挖】。卡住时挖身前那格是对的,但上升段身前那格正是下一块桥面
			if (DeckBuilder.OnLine(x, y)) return false;
			// 【挖不动的当场认账】。地狱熔炉(tile 77)镐力不够 65 时伤害恒 0,
			// 地狱祭坛/神庙砖同理。原来这儿不查,Dig 照样开挥并返回 true("我在处理"),
			// 调用方每帧 return —— 人对着炉子挥一辈子。
			// 判据用 DigTable 那一份(它抄的是 vanilla 的伤害表),不另写第二套
			if (DigTable.CostFrames(x, y) >= DigTable.Unmineable)
			{
				if (_lastHard != (x, y))
				{
					_lastHard = (x, y);
					DiagLog.Write($"[clearway] ({x},{y}) type={Main.tile[x, y].TileType} 这把镐挖不动,不挥了 —— 绕开它");
				}
				return false;   // false = "我处理不了",让调用方去走别的路(绕/跳/搭)
			}
			if (!Reach.CanMine(p, x, y)) return false;
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
		static (int, int) _lastHard = (int.MinValue, int.MinValue);   // 挖不动的那一格只报一次,别每帧刷屏

		// stuck=true:调用方【已经确认人推不动了】。这时"一格高的台阶跳一下就过去"不成立 ——
		// 卡住本身就是跳不过去的证据,那一格照挖。
		// stuck=false:只是顺手清一下路,一格高的台阶留着(挖了会把刚铺好的桥面拆断)
		public static bool Forward(Player p, int dir, string why = "挡路", bool stuck = false)
		{
			var (bl, br) = Predicates.BodyCols(p);
			int col = dir > 0 ? br + 1 : bl - 1;
			int fy = ActExecutor.OriginCy(p);
			// 台阶只有一格高就别动它;两格及以上人跳不过去,那才是真挡路
			bool step = !stuck && Predicates.IsWall(col, fy) && !Predicates.IsWall(col, fy - 1);
			// 【挖 4 行,不是 3 行】。人走过去要占 3 行,头顶还得留一格跳的余量 ——
			// 只挖 3 行的话第 4 行那块砖照样把人顶住,跳不起来
			for (int r = step ? 1 : 0; r <= DeckBuilder.HeadClear; r++)
				if (Dig(p, col, fy - r, why)) return true;
			return false;
		}

		// 前进方向那一列的 HeadClear+1 行都空了吗。Forward 一帧只挖一格,调用方靠这个
		// 判断"还要不要接着挖" —— 挖开三格人就挤过去了,剩下的头顶那两格会被漏掉
		public static bool ForwardClear(Player p, int dir)
		{
			var (bl, br) = Predicates.BodyCols(p);
			int col = dir > 0 ? br + 1 : bl - 1;
			int fy = ActExecutor.OriginCy(p);
			for (int r = 0; r <= DeckBuilder.HeadClear; r++)
				if (Predicates.IsWall(col, fy - r) && !DeckBuilder.OnLine(col, fy - r)) return false;
			return true;
		}

		// 头顶挡着(跳不上去/柱子顶不上去)。身子跨两列,两列都要清
		public static bool Above(Player p, string why = "头顶挡着")
		{
			var (bl, br) = Predicates.BodyCols(p);
			int fy = ActExecutor.OriginCy(p);
			// 身子占 fy..fy-2,从头顶那行往上挖到留出跳的余量为止。
			// 只挖 fy-3 一行的话,连着几行的砖挖掉一层还是跳不动
			for (int r = 3; r <= DeckBuilder.HeadClear; r++)
				for (int c = bl; c <= br; c++)
					if (Dig(p, c, fy - r, why)) return true;
			return false;
		}

		// 有没有镐。没有的话"挖开"这条路根本不存在,调用方要另想办法而不是干等
		public static bool HasPick(Player p) => PickSlot(p) >= 0;
	}
}
