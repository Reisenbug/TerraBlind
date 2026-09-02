using System.Collections.Generic;
using Terraria;
using Terraria.ID;

namespace TerraBlind
{
	// 【顺路采集】。赶路的全程扫身边有没有箱子,值得就拐过去捡了再走 ---
	// python 那份的注释写得很清楚:"这是这条路上捡到大部分东西的原因"。
	// itinerary 只列了几个大目标,路上贴着走过去的箱子全靠这一层。
	//
	// 【绝不每帧算】。判值不值要 PathCost,里面是 BuildFieldMulti(秒级),
	// 所以:降频扫描 + 候选扔后台算,主线程只读结果。
	public static class GreedPickup
	{
		const int ScanEvery = 90;      // 多久扫一次身边
		const int ScanRadius = 25;     // 直线多少格以内才进候选
		const int DigMax = 4;          // 顺路可以凿几格,再多就不是顺路了
		const int WalkMax = 40;

		// 处理过就别再回来:掏空的箱子还立在原地,地图分不出来,得自己记。
		// 【按局清】--- python 那份是模块级 set,跨局不清,重开游戏没重开进程就再也不开箱了
		static readonly HashSet<(int, int)> _done = new();
		static bool _busy;
		static (int x, int y)? _ready;
		static int _lastScan;

		public static void Reset() { _done.Clear(); _ready = null; _lastScan = 0; }
		public static void MarkDone(int x, int y) => _done.Add((x, y));

		// 身边有没有值得拐一趟的。有就返回那一格,调用方自己去捡。
		// 【只在主线程调】,而且调用方得容忍它返回 null(后台还在算)
		public static (int x, int y)? Poll()
		{
			if (_ready.HasValue)
			{
				var r = _ready.Value; _ready = null;
				// 拿出来的时候再验一次:走这一路可能已经被顺手捡掉了
				if (_done.Contains(r) || !IsChest(r.x, r.y)) return null;
				return r;
			}
			if (_busy) return null;
			if (Main.GameUpdateCount - _lastScan < ScanEvery) return null;
			_lastScan = (int)Main.GameUpdateCount;

			var cands = ScanNearby();
			if (cands.Count == 0) return null;
			_busy = true;
			System.Threading.Tasks.Task.Run(() =>
			{
				try
				{
					foreach (var c in cands)
					{
						if (!HttpServerSystem.PathCost(c.x, c.y, out int dig, out int walk)) continue;
						if (dig > DigMax || walk > WalkMax) continue;
						DiagLog.Write($"[greed] 顺路捡({c.x},{c.y}) 要挖{dig}走{walk},上限{DigMax}/{WalkMax}");
						_ready = c;
						return;
					}
				}
				catch (System.Exception e) { DiagLog.Write($"[greed] 算路炸了:{e.Message}"); }
				finally { _busy = false; }
			});
			return null;
		}

		// 直线半径内的箱子。归一到左上角那格 --- 箱子占 2x2,只有锚点算数
		static List<(int x, int y)> ScanNearby()
		{
			var outp = new List<(int x, int y)>();
			var p = Main.LocalPlayer;
			if (p == null) return outp;
			int pcx = ActExecutor.OriginCx(p), pcy = ActExecutor.OriginCy(p);
			for (int x = pcx - ScanRadius; x <= pcx + ScanRadius; x++)
				for (int y = pcy - ScanRadius; y <= pcy + ScanRadius; y++)
				{
					if (x < 1 || y < 1 || x >= Main.maxTilesX - 1 || y >= Main.maxTilesY - 1) continue;
					if (!IsChest(x, y)) continue;
					if (_done.Contains((x, y))) continue;
					outp.Add((x, y));
				}
			return outp;
		}

		// 能开的箱子的【锚点格】。上锁的、神庙的、蜂巢里的都不算 --- 和 /descent_route 同一套判据
		static bool IsChest(int x, int y)
		{
			var t = Main.tile[x, y];
			if (!t.HasTile) return false;
			if (t.TileType != TileID.Containers && t.TileType != TileID.Containers2) return false;
			if (t.TileFrameX % 36 != 0 || t.TileFrameY % 36 != 0) return false;
			if (Chest.IsLocked(x, y)) return false;
			if (t.TileType == TileID.Containers && t.TileFrameX / 36 == 16) return false;
			return true;
		}
	}
}
