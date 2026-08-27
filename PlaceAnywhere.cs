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
		private const int JumpSettleFrames = 25;   // 一次跳约20帧落地,过了就别再等

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
			if (Occupied(tx, ty))
			{ Outcome = "done"; _ph = Ph.Done; DiagLog.Write($"[placeany] ({tx},{ty})已经有东西"); return true; }
			// 岩浆格现在放得下 -- 按下去那一帧会先抹掉液体(Concessions.ClearLavaForPlacement)。
			// 只有【会被烧掉】的东西还得拦:平台放进去当场没,人以为搭上了其实还在往下掉
			if (Predicates.IsLava(tx, ty) && Concessions.BurnsInLava(_item))
			{ why = $"({tx},{ty})是熔岩,{_item}放进去会被烧掉"; Outcome = "stuck"; Reason = why;
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

		// 锚就用眼那一份判据,绝不另写(写过一次 3×3 的,7 格全判反)。
		// 【锚点让步之后这里永远真】:Concessions.DropAnchorRequirements 清空了 vanilla 的
		// 锚点声明,悬空也放得下,于是下面那趟 BFS 接链每次都在第一格就返回 -- 等于直接放。
		// 不删 BFS:收回让步时它还得用,而且 Free()/避身体那些判据是接链和直放共用的。
		static bool HasAnchor(int x, int y) => MazeWand.PlatformAnchor(x, y) || ItemUseCoordinator.HasAnchor(x, y);

		// 判空要跟游戏一致:它 occupied 看 HasTile,不看 tileSolid。草/藤/火把不 solid 但占位,
		// 用 IsSolid 判空会反复选中同一格然后报 occupied(日志里连着 9 次)
		static bool Occupied(int x, int y)
			=> Predicates.InBounds(x, y) && Main.tile[x, y].HasTile;

		static bool Free(int x, int y)
			=> Predicates.InBounds(x, y) && !Occupied(x, y)
			   && !(Predicates.IsLava(x, y) && Concessions.BurnsInLava(_item))
			   && !_bad.Contains((x, y));

		// 先试【绕开身子】的一条,不行再退回原来那条。
		// 原来一律不排除身体格,靠"到跟前人会让开" —— 可两侧悬空时根本让不开:
		// 人跳上去 2 格,链上更高的格子又进了身体,于是跳→等151帧→重算,循环不停(日志里就是这样)
		static bool Build(out string why)
		{
			if (BuildAvoid(true, out why)) return true;
			return BuildAvoid(false, out why);
		}

		static bool BuildAvoid(bool avoidBody, out string why)
		{
			why = "";
			_chain.Clear();
			var pl = Main.LocalPlayer;
			int bl0 = 0, br0 = -1, fy0 = 0;
			if (avoidBody && pl != null)
			{ var bc = Predicates.BodyCols(pl); bl0 = bc.left; br0 = bc.right; fy0 = ActExecutor.OriginCy(pl); }
			bool InBodyCell(int x, int y)
				=> avoidBody && x >= bl0 && x <= br0 && y <= fy0 && y >= fy0 - 2;
			// 目标本身被拉黑 = 它反复放不上,再找路也是绕回它,直接认输
			if (_bad.Contains((_tx, _ty))) { why = $"({_tx},{_ty})自己就放不上"; return false; }
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
					if (InBodyCell(n.Item1, n.Item2)) continue;
					seen.Add(n); prev[n] = (cx, cy); q.Enqueue(n);
				}
			}
			why = $"({_tx},{_ty})一路被熔岩/实心隔断,接不到任何有锚的地方";
			return false;
		}

		// 判据只有这一份:StepAside 和 Tick 用同一个,不然一边以为让开了一边还在等
		static bool InBody(Player p, int x, int y)
		{
			var (bl, br) = Predicates.BodyCols(p);
			int fy = ActExecutor.OriginCy(p);
			return x >= bl && x <= br && y <= fy && y >= fy - 2;
		}

		// 够得着又不压住目标的最近落脚列。伸手 tileRangeX=5,身子占 1~2 列,
		// 所以离目标 2~4 列的地方两个条件都能满足 —— 朝它走,而不是朝目标走。
		static int ApproachCol(Player p, int x, int y)
		{
			int cx = ActExecutor.OriginCx(p);
			int span = Predicates.BodyCols(p).right - Predicates.BodyCols(p).left;
			int best = cx, bestD = int.MaxValue;
			for (int off = span + 1; off <= 4; off++)
				foreach (int col in new[] { x - off, x + off })
				{
					if (!Predicates.IsSolid(col, y + 1) && !Predicates.IsSolid(col, y)) continue;
					int d = System.Math.Abs(col - cx);
					if (d < bestD) { bestD = d; best = col; }
				}
			return best;
		}

		// 目标格在人身子里 → 往远离它的方向让一格。让开由 SettleAt 精确落位。
		static bool StepAside(Player p, int x, int y, out string why)
		{
			var (bl, br) = Predicates.BodyCols(p);
			int fy = ActExecutor.OriginCy(p);
			if (!InBody(p, x, y)) { why = ""; return false; }
			// 让到【身子完全不盖住 x】为止。挪一格不够:目标在边缘时新位置还盖着它,每帧重来。
			int span = br - bl;                     // 人跨 1~2 列(20px 宽)
			// 两个方向都试,而且【必须那一列有地可站】—— 让到悬空处人会掉下去。
			// 日志:让到列3487 之后人从 1042 掉到 1050,目标反而在头顶 8 行外,横向再也够不着。
			foreach (int col in new[] { x + span + 1, x - span - 1 })
			{
				if (!Predicates.IsSolid(col, fy + 1)) continue;
				// 终点有地不够,【沿途每一列】都要有地:750 和 753 都踩得住,中间 751/752 是空的,
				// 人走过去就从缺口掉下 39 行((755,1092)),目标反而够不着了。
				int from = col > br ? br : bl;
				int step = col > from ? 1 : -1;
				bool gap = false;
				for (int c = from; c != col + step; c += step)
					if (!Predicates.IsSolid(c, fy + 1)) { gap = true; break; }
				if (gap)
				{
					DiagLog.Write($"[placeany] ({x},{y})在身子里,想让到列{col} 但 {(col > br ? br : bl)}→{col} 途中有缺口,换方向");
					continue;
				}
				DiagLog.Write($"[placeany] ({x},{y})在身子里(身{bl}..{br} 脚{fy}),让到列{col}(全程脚下有地)");
				return SettleAt.Start(col, out why);
			}
			// 两边都悬空:站着不动让不开。往上跳一格就能把身子挪出去 —— 悬空处跳比走安全。
			// 【绝不拉黑】:"现在在身子里"是暂时的,拉黑等于永久判死,目标自己被拉黑就直接 STUCK。
			if (y >= fy - 2 && y <= fy)
			{
				p.controlJump = true;
				if (_cellFrames % 30 == 1) DiagLog.Write($"[placeany] ({x},{y})在身子里(身{bl}..{br} 脚{fy}),两侧悬空,跳起来让开");
			}
			why = "";
			return false;
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

			if (Occupied(_tx, _ty))
			{
				Outcome = "done"; _ph = Ph.Done;
				DiagLog.Write($"[placeany] DONE ({_tx},{_ty}) 接了{_idx}格");
				return;
			}
			if (_idx >= _chain.Count) { Fail($"链铺完了({_chain.Count}格)目标还是空的"); return; }

			var (x, y) = _chain[_idx];
			if (Occupied(x, y)) { _idx++; _cellFrames = 0; return; }
			if (++_cellFrames > MaxCellFrames) { Retry($"({x},{y})卡了{_cellFrames}帧"); return; }

			// 人挡着就让开 —— 碰撞箱里放不了任何东西
			if (StepAside(p, x, y, out string sw)) { _ph = Ph.Move; return; }
			if (sw.Length > 0) { Retry($"让不开({x},{y}):{sw}"); return; }
			// 还在身子里(上面在跳):落地了就立刻重算一条绕开身子的链,别干等到 MaxCellFrames。
			// 日志里每次都白等 151 帧才 Retry —— 那就是"每爬2格停几秒"的来源
			if (InBody(p, x, y))
			{
				if (p.velocity.Y == 0f && _cellFrames > JumpSettleFrames)
				{ Retry($"({x},{y})跳完还在身子里,换条绕开的链"); return; }
				return;
			}
			if (_bad.Contains((x, y))) { Retry($"({x},{y})让不开,绕路"); return; }

			// 够不着:左右走只能改列。让位时人可能掉下去十几行(日志:人1061 目标1051),
			// 那时横向走一辈子也够不着 —— 行差得多就当链失效,从人现在的位置重接一条。
			if (!p.IsInTileInteractionRange(x, y, Terraria.DataStructures.TileReachCheckSettings.Simple))
			{
				int cx = ActExecutor.OriginCx(p), cy = ActExecutor.OriginCy(p);
				// 行差太大:重算链没有意义(人没挪窝,算出来还是同一条),直接认输交给上层。
				// 上层有寻路/pillar/平台梯,那才是改变行的手段。
				if (System.Math.Abs(cy - y) > RowGap)
				{ Fail($"人({cx},{cy})和({x},{y})差{System.Math.Abs(cy - y)}行,横向够不着"); return; }
				if (_cellFrames % 60 == 1) DiagLog.Write($"[placeany] 够不着({x},{y}) 人在({cx},{cy})");
				// 【别朝目标走】:走到目标头上,StepAside 又把人赶开,两边互相推翻。
				// 日志:settle 到 3508 → 这里往右推回 3511 → 让开 → 再推回,190帧全是 out_of_reach
				int dst = ApproachCol(p, x, y);
				if (dst == cx) { Retry($"够不着({x},{y})但没有能站的落脚列"); return; }
				int dir = dst > cx ? 1 : -1;
				// 地形挡着就挖开,不然横向走一辈子也过不去(卡满 MaxCellFrames 才报错)
				if (ClearWay.Forward(p, dir)) return;
				if (dir > 0) p.controlRight = true; else p.controlLeft = true;
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
