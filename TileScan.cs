using System.Collections.Generic;
using Terraria;

namespace TerraBlind
{
	// 【找最近的某种方块,只有这一份】。原来这套环形扫描内联在 /find_tiles 的 handler 里,
	// mod 内部要用只能自己再写一遍 --- 抽出来,端点和 StartRun 共用
	public static class TileScan
	{
		// 从玩家往外一圈圈扫,按距离近的先出。wantN 满了就停
		public static List<(int x, int y)> Nearest(int wantType, int wantN, int maxD)
		{
			var found = new List<(int x, int y)>();
			var pl = Main.LocalPlayer;
			if (pl == null) return found;
			int pcx = (int)(pl.Center.X / 16f), pcy = (int)(pl.Center.Y / 16f);
			for (int r = 0; r <= maxD && found.Count < wantN; r++)
				for (int dx = -r; dx <= r && found.Count < wantN; dx++)
				{
					// 只走这一圈的边:左右两条边整列都算,中间几列只取上下两格
					var dys = new List<int>();
					if (r == 0) dys.Add(0);
					else if (System.Math.Abs(dx) == r) for (int k = -r; k <= r; k++) dys.Add(k);
					else { dys.Add(-r); dys.Add(r); }
					foreach (int dy in dys)
					{
						int x = pcx + dx, y = pcy + dy;
						if (x < 0 || y < 0 || x >= Main.maxTilesX || y >= Main.maxTilesY) continue;
						var t = Main.tile[x, y];
						if (!t.HasTile || t.TileType != wantType) continue;
						found.Add((x, y));
						if (found.Count >= wantN) break;
					}
				}
			return found;
		}

		public static int Dist(int x, int y)
		{
			var pl = Main.LocalPlayer;
			if (pl == null) return int.MaxValue;
			int pcx = (int)(pl.Center.X / 16f), pcy = (int)(pl.Center.Y / 16f);
			return System.Math.Abs(x - pcx) + System.Math.Abs(y - pcy);
		}
	}
}
