using System.Collections.Generic;
using Terraria;

namespace TerraBlind
{
	// 让某一格出现方块/平台。调用方只管要结果,过程全在里面解决。
	//
	// 放置只有三个硬条件:够得着、那格空着、四邻有锚。前两个能自己制造:
	// 够不着就挪脚,身子挡着就让开。没锚就从【人脚下那块地】接一串过去 ——
	// 人站着,脚下必是实处,它旁边那格就有锚,放出来之后又成为下一格的锚。
	//
	// 所以"放不出来"在结构上只剩两种:目标本身是熔岩,或者一路被熔岩隔断。
	public static class PlaceAnywhere
	{
		private enum Ph { Idle, Step, Move, Done }
		private static Ph _ph = Ph.Idle;

		private static string _item = "";
		private static int _tx, _ty;
		private static List<(int x, int y)> _chain = new();
		private static int _idx;
		private static int _frames, _cellFrames;
		private static readonly HashSet<(int, int)> _bad = new();
		private static int _rebuilds;

		private const int MaxFrames = 60 * 90;
		private const int MaxCellFrames = 150;
		private const int MaxRebuilds = 8;
		private const int RowGap = 4;   // 行差超过这个,横向走位就不可能够到

		public static bool IsRunning => _ph != Ph.Idle && _ph != Ph.Done;
		public static string Outcome = "idle";
		public static string Reason = "";

		public static bool Start(string itemName, int tx, int ty, out string why)
		{
			why = "";
			var p = Main.LocalPlayer;
			if (p == null) { why = "no_player"; return false; }
			_item = itemName; _tx = tx; _ty = ty;
			_frames = 0; _cellFrames = 0; _idx = 0; _rebuilds = 0; _bad.Clear();
			Outcome = "running"; Reason = "";
			if (Predicates.IsSolid(tx, ty))
			{ Outcome = "done"; _ph = Ph.Done; DiagLog.Write($"[placeany] ({tx},{ty})已经有东西"); return true; }
			if (Predicates.IsLava(tx, ty))
			{ why = $"({tx},{ty})是熔岩,放不进去"; Outcome = "stuck"; Reason = why;
			  DiagLog.Write($"[placeany] STUCK {why}"); return false; }
			if (!Build(out why)) { Outcome = "stuck"; Reason = why; DiagLog.Write($"[placeany] STUCK {why}"); return false; }
			DiagLog.Write($"[placeany] ({tx},{ty}) 要接{_chain.Count}格,从({_chain[0].x},{_chain[0].y})起");
			_ph = Ph.Step;
			return true;
		}

		public static void Stop()
		{
			if (Outcome == "running") Outcome = "stopped";
			_ph = Ph.Idle;
		}

		// 锚就用眼那一份判据,绝不另写(写过一次 3×3 的,7 格全判反)
		static bool HasAnchor(int x, int y) => ItemUseCoordinator.HasAnchor(x, y);

		static bool Free(int x, int y)
			=> Predicates.InBounds(x, y) && !Predicates.IsSolid(x, y)
			   && !Predicates.IsLava(x, y) && !_bad.Contains((x, y));

		// 从目标往回 BFS 到【任何一个已经有锚的空格】,得到一串要依次放的格子。
		// 只走四邻 —— 锚也只认四邻。人身子占的格子不排除:放到跟前时人会让开。
		static bool Build(out string why)
		{
			why = "";
			_chain.Clear();
			if (HasAnchor(_tx, _ty)) { _chain.Add((_tx, _ty)); return true; }
			var prev = new Dictionary<(int, int), (int, int)>();
			var seen = new HashSet<(int, int)> { (_tx, _ty) };
			var q = new Queue<(int, int)>();
			q.Enqueue((_tx, _ty));
			while (q.Count > 0)
			{
				var (cx, cy) = q.Dequeue();
				if ((cx, cy) != (_tx, _ty) && HasAnchor(cx, cy))
				{
					var cur = (cx, cy);
					while (true) { _chain.Add(cur); if (cur == (_tx, _ty)) break; cur = prev[cur]; }
					return true;
				}
				foreach (var (dx, dy) in new[] { (0, 1), (0, -1), (-1, 0), (1, 0) })
				{
					var n = (cx + dx, cy + dy);
					if (seen.Contains(n) || !Free(n.Item1, n.Item2)) continue;
					seen.Add(n); prev[n] = (cx, cy); q.Enqueue(n);
				}
			}
			why = $"({_tx},{_ty})一路被熔岩/实心隔断,接不到任何有锚的地方";
			return false;
		}

		// 目标格在人身子里 → 往远离它的方向让一格。让开由 SettleAt 精确落位。
		static bool StepAside(Player p, int x, int y, out string why)
		{
			var (bl, br) = Predicates.BodyCols(p);
			int fy = ActExecutor.OriginCy(p);
			if (x < bl || x > br || y > fy || y < fy - 2) { why = ""; return false; }
			// 让到【身子完全不盖住 x】为止。挪一格不够:目标在边缘时,新位置还是盖着它,
			// 于是每帧重来一次 —— 日志里 (3315,1052) 身3313..3315 让到 3314,一直循环。
			int span = br - bl;                     // 人跨 1~2 列(20px 宽)
			int col = x <= (bl + br) / 2 ? x + span + 1 : x - span - 1;
			DiagLog.Write($"[placeany] ({x},{y})在身子里(身{bl}..{br} 脚{fy}),让到列{col}");
			return SettleAt.Start(col, out why);
		}

		public static void Tick()
		{
			if (_ph != Ph.Step && _ph != Ph.Move) return;
			var p = Main.LocalPlayer;
			if (p == null || !p.active) { Fail("no_player"); return; }
			if (++_frames > MaxFrames) { Fail($"超时 接到第{_idx}/{_chain.Count}格"); return; }

			if (_ph == Ph.Move)
			{
				if (SettleAt.IsRunning) return;
				_ph = Ph.Step; _cellFrames = 0;
				return;
			}

			if (Predicates.IsSolid(_tx, _ty))
			{
				Outcome = "done"; _ph = Ph.Done;
				DiagLog.Write($"[placeany] DONE ({_tx},{_ty}) 接了{_idx}格");
				return;
			}
			if (_idx >= _chain.Count) { Fail($"链铺完了({_chain.Count}格)目标还是空的"); return; }

			var (x, y) = _chain[_idx];
			if (Predicates.IsSolid(x, y)) { _idx++; _cellFrames = 0; return; }
			if (++_cellFrames > MaxCellFrames) { Retry($"({x},{y})卡了{_cellFrames}帧"); return; }

			// 人挡着就让开 —— 碰撞箱里放不了任何东西
			if (StepAside(p, x, y, out string sw)) { _ph = Ph.Move; return; }
			if (sw.Length > 0) { Retry($"让不开({x},{y}):{sw}"); return; }

			// 够不着:左右走只能改列。让位时人可能掉下去十几行(日志:人1061 目标1051),
			// 那时横向走一辈子也够不着 —— 行差得多就当链失效,从人现在的位置重接一条。
			if (!p.IsInTileInteractionRange(x, y, Terraria.DataStructures.TileReachCheckSettings.Simple))
			{
				int cx = ActExecutor.OriginCx(p), cy = ActExecutor.OriginCy(p);
				if (System.Math.Abs(cy - y) > RowGap)
				{ DiagLog.Write($"[placeany] 人({cx},{cy})和({x},{y})差{System.Math.Abs(cy - y)}行,横向走不过去"); Retry("行差太大"); return; }
				if (_cellFrames % 60 == 1) DiagLog.Write($"[placeany] 够不着({x},{y}) 人在({cx},{cy})");
				if (cx < x) p.controlRight = true; else if (cx > x) p.controlLeft = true;
				return;
			}
			if (PlaceAction.IsRunning) return;
			if (PlaceAction.Outcome == "blocked")
			{
				DiagLog.Write($"[placeany] ({x},{y})放不上:{PlaceAction.Reason}");
				// out_of_reach 是【人站错了】不是这格不行 —— 拉黑它会把好格子一个个丢掉,
				// 链越重算越远(日志里连丢 8 格)。这种只等下一帧,让上面的走位去解决。
				if (PlaceAction.Reason != null && PlaceAction.Reason.Contains("out_of_reach")) return;
				_bad.Add((x, y));
				Retry($"({x},{y}){PlaceAction.Reason}");
				return;
			}
			PlaceAction.Start(_item, x, y, 1, 0, 0, true, out _);
		}

		// 换一条链再来。地形/站位一直在变,所以重算而不是在原链上打转。
		static void Retry(string note)
		{
			if (++_rebuilds > MaxRebuilds) { Fail($"重算{_rebuilds}次仍失败,最后:{note}"); return; }
			DiagLog.Write($"[placeany] 重算链({_rebuilds}/{MaxRebuilds}) 因为 {note}");
			if (!Build(out string why)) { Fail(why); return; }
			_idx = 0; _cellFrames = 0;
		}

		static void Fail(string reason)
		{
			Outcome = "stuck"; Reason = reason; _ph = Ph.Idle;
			DiagLog.Write($"[placeany] STUCK {reason}");
		}
	}
}
