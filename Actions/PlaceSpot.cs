using Terraria;
using Terraria.ID;
using Terraria.ObjectData;

namespace TerraBlind
{
	// 一个东西放不下去,到底卡在哪一格。
	//
	// 尺寸/原点/锚点全部问 vanilla 的 TileObjectData,不硬编"桌子 3x2"。
	// 能不能放也先问 TileObject.CanPlace --- 那是游戏自己的判据,比我们重写一套准。
	// 放不下时才用尺寸逐格找原因,好告诉 Unstick 该解哪一格。
	public static class PlaceSpot
	{
		// 调用方说这里该拿什么补地板。家具下面缺格时用它,栈自己猜必错
		public enum Fill { Block, Platform }

		// 放哪儿。【坐标要算,不能猜】:模型报的 y 比自己头顶还高,底下悬空,NoFooting 卡死
		public static bool FindSpot(Player p, int itemId, out int bx, out int by)
		{
			bx = by = 0;
			if (p == null) return false;
			int cx = ActExecutor.OriginCx(p), cy = ActExecutor.OriginCy(p);
			int span = Player.tileRangeX + p.blockRange + 4;
			for (int r = 1; r <= span; r++)
				for (int dy = -r; dy <= r; dy++)
					for (int dx = -r; dx <= r; dx++)
					{
						if (System.Math.Abs(dx) != r && System.Math.Abs(dy) != r) continue;
						int x = cx + dx, y = cy + dy;
						if (!Predicates.InBounds(x, y)) continue;
						if (!Reach.CanPlace(p, x, y)) continue;
						if (!Check(itemId, x, y, Fill.Block, out _)) continue;
						bx = x; by = y;
						return true;
					}
			return false;
		}

		// 放得下吗。放不下就给出【第一个】要解决的 Blocker
		public static bool Check(int itemId, int wx, int wy, Fill fill, out Blocker why)
		{
			why = default;
			var probe = new Item();
			probe.SetDefaults(itemId);
			int type = probe.createTile;
			if (type < 0) { why = new Blocker(BlockKind.Hopeless, wx, wy, $"物品{itemId}不放东西"); return false; }

			var data = TileObjectData.GetTileData(type, probe.placeStyle);
			if (data == null)
			{
				// 单格的东西(方块/平台)没有 TileObjectData,自己判
				return CheckSingle(type, wx, wy, out why);
			}

			int x0 = wx - data.Origin.X, y0 = wy - data.Origin.Y;
			// 1) 占位范围内不许有【挡得住的】东西。
			// 【判据抄 vanilla TileObject.CanPlace:346】,别用 HasTile:草/藤/小花这些 tileCut 的,
			// 放置时会被直接顶掉,vanilla 放行。用 HasTile 判会比原版严,把站在草地上放工作台
			// 判成"占位里有东西",然后白跑一趟 Unstick 去挖草。
			for (int dx = 0; dx < data.Width; dx++)
				for (int dy = 0; dy < data.Height; dy++)
				{
					int x = x0 + dx, y = y0 + dy;
					if (!Predicates.InBounds(x, y)) { why = new Blocker(BlockKind.Hopeless, x, y, "越界"); return false; }
					if (Blocks(x, y))
					{ why = new Blocker(BlockKind.Terrain, x, y, "占位里有东西"); return false; }
				}

			// 2) 底下要有整齐的支撑。半砖/斜砖撑不住家具,挖掉重放比垫更省事
			if (data.AnchorBottom.tileCount > 0)
			{
				int ay = y0 + data.Height;
				int from = x0 + data.AnchorBottom.checkStart;
				for (int i = 0; i < data.AnchorBottom.tileCount; i++)
				{
					int x = from + i;
					if (!Predicates.InBounds(x, ay)) { why = new Blocker(BlockKind.Hopeless, x, ay, "越界"); return false; }
					var t = Main.tile[x, ay];
					if (!t.HasTile)
					{ why = new Blocker(BlockKind.NoFooting, x, ay, fill == Fill.Platform ? "缺支撑:补平台" : "缺支撑:补方块"); return false; }
					// 半砖/斜砖 = 非默认 style,家具坐不上去。挖掉,下一轮当"缺支撑"补整块
					if (t.Slope != SlopeType.Solid || t.IsHalfBlock)
					{ why = new Blocker(BlockKind.Terrain, x, ay, "支撑是半砖/斜砖,挖掉重放"); return false; }
				}
			}

			// 3) 前两条都过了还是放不下,交给 vanilla 说最终的话
			if (!TileObject.CanPlace(wx, wy, type, probe.placeStyle, 1, out _, onlyCheck: true))
			{ why = new Blocker(BlockKind.Hopeless, wx, wy, "vanilla 说放不了"); return false; }
			return true;
		}

		// 这一格挡不挡得住放置。照抄 vanilla TileObject.CanPlace 里那一行:
		// active() && (!tileCut || 484/654) && !BreakableWhenPlacing
		public static bool Blocks(int x, int y)
		{
			if (!Predicates.InBounds(x, y)) return true;
			var t = Main.tile[x, y];
			if (!t.HasTile) return false;
			int ty = t.TileType;
			if (Main.tileCut[ty] && ty != 484 && ty != 654) return false;   // 草/藤,放置时顶掉
			if (TileID.Sets.BreakableWhenPlacing[ty]) return false;
			return true;
		}

		// 方块/平台这种单格的:格子要空,而且得有锚点
		static bool CheckSingle(int type, int wx, int wy, out Blocker why)
		{
			why = default;
			if (!Predicates.InBounds(wx, wy)) { why = new Blocker(BlockKind.Hopeless, wx, wy, "越界"); return false; }
			if (Main.tile[wx, wy].HasTile)
			{ why = new Blocker(BlockKind.Terrain, wx, wy, "格子被占"); return false; }
			// 方块和平台都要贴着东西才放得住。没锚点就造一个
			if (!MazeWand.PlatformAnchor(wx, wy))
			{ why = new Blocker(BlockKind.NoFooting, wx, wy, "四周没锚点"); return false; }
			return true;
		}

		// 这东西占几格。日志和规划要用
		public static (int w, int h) Size(int itemId)
		{
			var probe = new Item();
			probe.SetDefaults(itemId);
			if (probe.createTile < 0) return (0, 0);
			var d = TileObjectData.GetTileData(probe.createTile, probe.placeStyle);
			return d == null ? (1, 1) : (d.Width, d.Height);
		}
	}
}
