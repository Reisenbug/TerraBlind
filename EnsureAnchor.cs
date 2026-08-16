using System.Collections.Generic;
using Terraria;

namespace TerraBlind
{
	// 让某一格【放得出东西】。悬空是常态,所以不换位置,自己把锚造出来。
	//
	// 在纯空气里放第一块是不可能的 —— 目标的邻居往往也四周全空,一样放不出来。
	// 所以 BFS 从目标往外找【最近的能贴住的空格】(靠着某个实处),再沿路径一格格铺回来。
	// 平台按 3×3 算邻居(原版 framing 读了 8 邻,斜着也贴),比方块的 4 邻好接得多。
	public static class EnsureAnchor
	{
		private enum Ph { Idle, Lay, Done }
		private static Ph _ph = Ph.Idle;

		private static string _item = "";
		private static int _tx, _ty;
		private static List<(int x, int y)> _path = new();   // 从远处的锚点铺回目标,顺序就是铺的顺序
		private static int _idx;
		private static int _frames, _cellFrames, _repaths;
		// 放不上的格子记下来,重搜时绕开 —— 不然每次搜出同一条路,原地循环到超时
		private static readonly HashSet<(int, int)> _bad = new();

		private const int MaxFrames = 60 * 60;
		private const int MaxCellFrames = 180;
		private const int MaxRepaths = 6;

		public static bool IsRunning => _ph != Ph.Idle && _ph != Ph.Done;
		public static string Outcome = "idle";
		public static string Reason = "";

		// 用眼那一份判据,不另写。我写过一个查 3×3 的版本(照 WorldGen 的 platform framing 抄的),
		// 结果 7 格全判 true 而游戏 7 格全拒 —— framing 是"长什么样",不是"能不能放"。
		public static bool HasAnchor(int x, int y) => ItemUseCoordinator.HasAnchor(x, y);

		// 能考虑的空格:界内、空着、不是熔岩,而且【人现在就够得着】——
		// 够不着的格子放不了,BFS 把它算进路径只会规划出一条走不通的路。
		static bool Free(int x, int y)
		{
			if (!Predicates.InBounds(x, y) || Predicates.IsSolid(x, y) || Predicates.IsLava(x, y)) return false;
			if (_bad.Contains((x, y))) return false;
			var p = Main.LocalPlayer;
			if (p == null) return false;
			// 身子占的格子放不进东西 —— 日志里 BFS 把人自己站的那格当候选,当然放不上
			var (bl, br) = Predicates.BodyCols(p);
			int fy = ActExecutor.OriginCy(p);
			if (x >= bl && x <= br && y <= fy && y >= fy - 2) return false;
			return p.IsInTileInteractionRange(x, y, Terraria.DataStructures.TileReachCheckSettings.Simple);
		}

		public static bool Start(string itemName, int tx, int ty, out string why)
		{
			why = "";
			if (Main.LocalPlayer == null) { why = "no_player"; return false; }
			_item = itemName; _tx = tx; _ty = ty;
			_frames = 0; _cellFrames = 0; _idx = 0; _repaths = 0; _bad.Clear();
			Outcome = "running"; Reason = "";
			if (HasAnchor(tx, ty))
			{
				Outcome = "done"; _ph = Ph.Done;
				DiagLog.Write($"[anchor] ({tx},{ty}) 本来就贴得住");
				return true;
			}
			if (!FindPath(out _path))
			{ why = $"({tx},{ty})够得着的范围里没有能贴住的地方"; Outcome = "stuck"; Reason = why;
			  DiagLog.Write($"[anchor] STUCK {why}"); return false; }
			var (fx, fy) = _path[0];
			DiagLog.Write($"[anchor] ({tx},{ty})四周全空 → 从({fx},{fy})起铺{_path.Count}格接回来");
			_ph = Ph.Lay;
			return true;
		}

		// BFS:从目标往外扩,找最近的"已经贴得住"的空格,把路径反过来铺回目标。
		// 扩展只走四邻 —— 锚也只认四邻,斜着接不上。
		static bool FindPath(out List<(int x, int y)> path)
		{
			path = new List<(int x, int y)>();
			var prev = new Dictionary<(int, int), (int, int)>();
			var seen = new HashSet<(int, int)> { (_tx, _ty) };
			var q = new Queue<(int x, int y)>();
			q.Enqueue((_tx, _ty));
			// 人脚下那格永远是实处 —— 所以"够得着的范围里必有可贴处"这件事是结构性成立的,
			// BFS 最差也会走到人身边接上。搜不到只可能是被熔岩或已有方块封死。
			while (q.Count > 0)
			{
				var (cx, cy) = q.Dequeue();
				// 找到一个自己就贴得住的空格 —— 从它起手,顺着来路铺回目标
				if ((cx != _tx || cy != _ty) && HasAnchor(cx, cy))
				{
					var cur = (cx, cy);
					while (true)
					{
						path.Add(cur);
						if (cur == (_tx, _ty)) break;
						cur = prev[cur];
					}
					return true;
				}
				foreach (var (dx, dy) in new[] { (0, 1), (0, -1), (-1, 0), (1, 0) })
				{
					var n = (cx + dx, cy + dy);
					if (seen.Contains(n) || !Free(n.Item1, n.Item2)) continue;
					seen.Add(n); prev[n] = (cx, cy); q.Enqueue(n);
				}
			}
			return false;
		}

		public static void Stop()
		{
			if (Outcome == "running") Outcome = "stopped";
			_ph = Ph.Idle;
		}

		public static void Tick()
		{
			if (_ph != Ph.Lay) return;
			var p = Main.LocalPlayer;
			if (p == null || !p.active) { Fail("no_player"); return; }
			if (++_frames > MaxFrames) { Fail($"超时 铺到第{_idx}/{_path.Count}格"); return; }

			// 目标贴得住了就收工 —— 路径可能还没铺完,但锚已经有了,不用铺满
			if (HasAnchor(_tx, _ty))
			{
				Outcome = "done"; _ph = Ph.Done;
				DiagLog.Write($"[anchor] DONE ({_tx},{_ty}) 铺了{_idx}格");
				return;
			}
			if (_idx >= _path.Count) { Fail($"整条路铺完了({_path.Count}格)目标还是贴不住"); return; }

			var (x, y) = _path[_idx];
			// 铺到目标自己那格就停:要的是给目标当锚,不是把目标占掉
			if ((x, y) == (_tx, _ty)) { Fail("路径只剩目标本身,锚没造出来"); return; }
			if (Predicates.IsSolid(x, y)) { _idx++; _cellFrames = 0; return; }

			if (++_cellFrames > MaxCellFrames) { Fail($"({x},{y})铺不上,卡了{_cellFrames}帧"); return; }
			// 不自己走位:路径是在【当前够得着的范围】里搜出来的,人不动就够得着。
			// 人被推开导致够不着,那是路径失效,重搜一条,别硬推(硬推没有"走不动"判据,会推到超时)。
			if (!p.IsInTileInteractionRange(x, y, Terraria.DataStructures.TileReachCheckSettings.Simple))
			{
				DiagLog.Write($"[anchor] 够不着({x},{y}) 人在({ActExecutor.OriginCx(p)},{ActExecutor.OriginCy(p)}),重新找路");
				if (++_repaths > MaxRepaths) { Fail($"重找{_repaths}次还是够不着({x},{y})"); return; }
				if (!FindPath(out _path)) { Fail($"人挪位后够不着({x},{y}),也没别的路"); return; }
				_idx = 0; _cellFrames = 0; return;
			}
			if (PlaceAction.IsRunning) return;
			// 眼说 blocked 就别再挥了 —— 这一格放不上,整条路径作废,重新找一条
			if (PlaceAction.Outcome == "blocked")
			{
				DiagLog.Write($"[anchor] ({x},{y})放不上:{PlaceAction.Reason},重新找路");
				_bad.Add((x, y));
				if (++_repaths > MaxRepaths) { Fail($"重找{_repaths}次都放不上,最后卡在({x},{y})"); return; }
				if (!FindPath(out _path)) { Fail($"重找也没路:{PlaceAction.Reason}"); return; }
				_idx = 0; _cellFrames = 0; return;
			}
			PlaceAction.Start(_item, x, y, 1, 0, 0, true, out _);
		}

		static void Fail(string reason)
		{
			Outcome = "stuck"; Reason = reason; _ph = Ph.Idle;
			DiagLog.Write($"[anchor] STUCK {reason}");
		}
	}
}
