using System.Collections.Generic;
using Terraria;

namespace TerraBlind
{
	// 让某一格出现方块/平台。调用方只管要结果,过程全在里面解决。
	//
	// 放置只有三个硬条件:够得着、那格空着、四邻有锚。前两个能自己制造:
	// 够不着就挪脚,身子挡着就让开。没锚就从【人脚下那块地】接一串过去
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
		private static int _lastPx = int.MinValue;   // 上一帧人在哪一列,判"推了却没挪窝"
		private static int _blockedFrames;

		private const int MaxFrames = 60 * 90;
		private const int MaxCellFrames = 150;
		private const int MaxRebuilds = 8;
		private const int RowGap = 4;   // 行差超过这个,横向走位就不可能够到
		private const int JumpSettleFrames = 25;   // 一次跳约20帧落地,过了就别再等
		private static bool _asideNav;   // 这次让位是不是起了寻路(决定要不要看 LastStop)
		// 每条无声 return 留个名字,60 帧汇报一次。人不动时唯一能查的就是"卡在哪一条"
		private static string _where = "";
		private static int _heartbeat;
		const int HeartbeatEvery = 60;
		private const int MaxAsideCols = 5;   // 让位最多往外找几列。再远就够不着了(tileRangeX=5)
		private const int AsideRows = 3;
		// 人头顶留几行不许砌。跳一下约 3 行,留够跳出去的空间
		private const int HeadRoom = 3;      // 上下找几行。半砖/坡地上落脚点未必和目标同高
		private const int BlockedAt = 20;   // 朝目标推了这么多帧还没换列 = 被顶住了

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
			_lastPx = int.MinValue; _blockedFrames = 0; _asideNav = false; _where = ""; _heartbeat = 0;
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
			if (IsRunning) DiagLog.Write($"[placeany] STOP 停在({_tx},{_ty}) 链第{_idx}/{_chain.Count}格");
			if (Outcome == "running") Outcome = "stopped";
			_ph = Ph.Idle;
		}

		// 锚点【跟着料走】:平台是 3x3 含斜角认墙,方块是四邻认墙。
		// 绝不另写第三份(写过一次 3x3 的,7 格全判反)
		static bool HasAnchor(int x, int y)
			=> PlacingPlatform() ? MazeWand.PlatformAnchor(x, y) : MazeWand.BlockAnchor(x, y);

		static bool PlacingPlatform()
		{
			int slot = PlaceAction.ResolveSlot(_item);
			if (slot < 0) return false;
			var it = Main.LocalPlayer?.inventory[slot];
			if (it == null || it.IsAir || it.createTile < 0) return false;
			return Main.tileSolidTop[it.createTile];
		}

		// 判空要跟游戏一致:它 occupied 看 HasTile,不看 tileSolid。草/藤/火把不 solid 但占位,
		// 用 IsSolid 判空会反复选中同一格然后报 occupied(日志里连着 9 次)
		static bool Occupied(int x, int y)
			=> Predicates.InBounds(x, y) && Main.tile[x, y].HasTile;

		static bool Free(int x, int y)
			=> Predicates.InBounds(x, y) && !Occupied(x, y)
			   && !(Predicates.IsLava(x, y) && Concessions.BurnsInLava(_item))
			   && !_bad.Contains((x, y));

		// 先试【绕开身子】的一条,不行再退回原来那条。两侧悬空时人根本让不开:
		// 跳上去 2 格,链上更高的格又进了身体,于是跳→等151帧→重算,循环不停
		static bool Build(out string why)
		{
			if (BuildAvoid(true, out why)) return true;
			// 放宽【身体格】重试:人到跟前会让开。但"不许封住头顶"那条【永远不放宽】,
			// 砌完人就出不来了,A* 也搜不出路,只能整趟重开
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
			// 【绝不把自己封在里面】:链是最短路,它不知道人站在哪,砌完头顶 A* 再也搜不出路。
			// 判据只管【人头顶那一列】:身子上方 3 行不许有链上的格,那是跳出去的唯一出口
			bool SealsPlayer(int x, int y)
			{
				if (pl == null) return false;
				var bc2 = Predicates.BodyCols(pl);
				if (x < bc2.left || x > bc2.right) return false;
				int top = Predicates.BodyRows(pl).top;
				return y < top && y >= top - HeadRoom;
			}
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
					if (SealsPlayer(n.Item1, n.Item2)) continue;
					seen.Add(n); prev[n] = (cx, cy); q.Enqueue(n);
				}
			}
			// 别一律说"被熔岩隔断":头顶那几行是我们自己不许砌的,那是【人站错地方】,
			// 挪一步就有路,和真被岩浆封死完全是两回事
			why = $"({_tx},{_ty})接不到任何有锚的地方(要么被熔岩/实心隔断,要么绕不开人头顶那{HeadRoom}行)";
			return false;
		}

		// 判据只有这一份:StepAside 和 Tick 用同一个,不然一边以为让开了一边还在等。
		// 【走 vanilla 的矩形相交】,不拿 OriginCy±2 近似。半砖上身子跨 4 行,近似会判错
		static bool InBody(Player p, int x, int y) => Predicates.BodyOverlaps(p, x, y);

		// 够得着又不压住目标的最近落脚列。伸手 tileRangeX=5,身子占 1~2 列,
		// 所以离目标 2~4 列的地方两个条件都能满足。朝它走,而不是朝目标走。
		static int ApproachCol(Player p, int x, int y)
		{
			var (c, _) = ApproachSpot(p, x, y);
			return c < 0 ? ActExecutor.OriginCx(p) : c;
		}

		// 【站哪儿才能放 (x,y)】:一格都不压住它而且够得着,找不到返回 (-1,-1) 让调用方交栈。
		// 判据按【真碰撞箱】算 -- 近似会把"其实压着"判成"没压着",放不出来又不知道为什么
		static (int col, int row) ApproachSpot(Player p, int x, int y)
		{
			int cx = ActExecutor.OriginCx(p);
			int span = Predicates.BodyCols(p).right - Predicates.BodyCols(p).left;
			int best = -1, bestRow = -1, bestD = int.MaxValue;
			// 只找【站得住】的:脚下那格实心。悬空的列不算落脚点。走过去就掉下去
			for (int off = span + 1; off <= MaxAsideCols; off++)
				foreach (int col in new[] { x - off, x + off })
				{
					if (col < 1 || col >= Main.maxTilesX - 1) continue;
					// 站在目标同一行、或者高一点低一点都行,逐行找踩得住的
					for (int row = y - AsideRows; row <= y + AsideRows; row++)
					{
						if (!Predicates.IsSolid(col, row + 1)) continue;   // 脚下要有地
						if (WouldOverlap(p, col, row, x, y)) continue;     // 站上去还压着目标就白搭
						if (!ReachesFrom(p, col, row, x, y)) continue;     // 站上去够不着也白搭
						int d = System.Math.Abs(col - cx) + System.Math.Abs(row - y);
						if (d < bestD) { bestD = d; best = col; bestRow = row; }
					}
				}
			return (best, bestRow);
		}

		// 人【站在 (col,row) 上】的话,碰撞箱会不会盖住 (x,y)。
		// 按 vanilla 的矩形相交算,和 Collision.EmptyTile 同一把尺子
		static bool WouldOverlap(Player p, int col, int row, int x, int y)
		{
			float px = col * 16f + 8f - p.width / 2f;
			float py = (row + 1) * 16f - p.height;
			float l = x * 16f, r = l + 16f, t = y * 16f, b = t + 16f;
			return px < r && px + p.width > l && py < b && py + p.height > t;
		}

		// 站在 (col,row) 上够不够得着 (x,y)。用 vanilla 自己的判据,不自己数格数
		static bool ReachesFrom(Player p, int col, int row, int x, int y)
		{
			var save = p.position;
			p.position.X = col * 16f + 8f - p.width / 2f;
			p.position.Y = (row + 1) * 16f - p.height;
			bool ok = Reach.CanPlace(p, x, y);
			p.position = save;
			return ok;
		}

		// 目标格在人身子里 → 挪到一个放得出来的位置。挪法一律交寻路(候选只收脚下实心的格);
		// 一个候选都没有 = 谁站着都放不出来,交栈
		static bool StepAside(Player p, int x, int y, out string why)
		{
			why = "";
			if (!InBody(p, x, y)) return false;
			var (bl, br) = Predicates.BodyCols(p);

			var (col, row) = ApproachSpot(p, x, y);
			if (col < 0)
			{
				// 一个能站的地方都没有。这不是"再试一次"能解决的。交栈,让它去挖/搭
				DiagLog.Write($"[placeany] ({x},{y})在身子里(身{bl}..{br}),周围{MaxAsideCols}列内没有能放它的落脚点,交栈");
				if (!Unstick.Handle("placeany", new Blocker(BlockKind.NoFooting, x, y + 1, "让不开:没有能放这格的落脚点")))
					why = $"({x},{y})在身子里,周围没有能站的地方";
				return false;
			}

			// 就在隔壁同一行 -> SettleAt 横移就够,不值得起一趟寻路(它要建 110 万格的场)
			int fy = ActExecutor.OriginCy(p);
			if (row == fy && System.Math.Abs(col - (col > br ? br : bl)) <= 2 && NoGapTo(p, col, fy))
			{
				DiagLog.Write($"[placeany] ({x},{y})在身子里(身{bl}..{br} 脚{fy}),横移到列{col}");
				return SettleAt.Start(col, out why);
			}

			// 要换行、要绕、要爬。【精准一格】的寻路。Mode.Stand 会跳会搭会挖,
			// 到不了会报 unreachable,不会像原来那样跳一下落回原地无限循环
			DiagLog.Write($"[placeany] ({x},{y})在身子里(身{bl}..{br} 脚{fy}),寻路去({col},{row})站住");
			RecedingNav.Start(col, row, RecedingNav.Mode.Stand);
			_asideNav = true;
			return true;
		}

		// 从身子边缘走到 col,沿途每一列脚下都得有地。中间有缺口人会掉下去。
		// 日志:750 和 753 都踩得住,751/752 是空的,人走过去掉了 39 行
		static bool NoGapTo(Player p, int col, int fy)
		{
			var (bl, br) = Predicates.BodyCols(p);
			int from = col > br ? br : bl;
			int step = col > from ? 1 : -1;
			for (int c = from; c != col + step; c += step)
				if (!Predicates.IsSolid(c, fy + 1)) return false;
			return true;
		}

		static void Mark(string where)
		{
			if (where != _where) { _where = where; _heartbeat = 0; return; }
			if (++_heartbeat % HeartbeatEvery != 0) return;
			var p = Main.LocalPlayer;
			int cx = p != null ? ActExecutor.OriginCx(p) : -1, cy = p != null ? ActExecutor.OriginCy(p) : -1;
			var (lx, ly) = _idx < _chain.Count ? _chain[_idx] : (-1, -1);
			DiagLog.Write($"[placeany] 心跳 {_heartbeat}帧都在\"{where}\" 目标({_tx},{_ty}) " +
				$"链{_idx}/{_chain.Count}=({lx},{ly}) 人({cx},{cy}) blocked={_blockedFrames} cell={_cellFrames}");
		}

		public static void Tick()
		{
			if (_ph != Ph.Step && _ph != Ph.Move) return;
			var p = Main.LocalPlayer;
			if (p == null || !p.active) { Fail("no_player"); return; }
			if (++_frames > MaxFrames) { Fail($"超时 接到第{_idx}/{_chain.Count}格"); return; }

			if (_ph == Ph.Move)
			{
				// 【寻路也要等】。原来只等 SettleAt,让位改走寻路之后一帧就当"让完了"回到 Step,
				// 而人还在半路。又判在身子里,又发一次寻路,永远走不完
				if (SettleAt.IsRunning || RecedingNav.Active) { Mark("让位中"); return; }
				// 寻路认输了就别装作让开了。只在【这次让位真的起了寻路】时才看 LastStop,
				// 横移那条路读到的会是上一趟寻路的旧值
				if (_asideNav)
				{
					_asideNav = false;
					string ls = RecedingNav.LastStop;
					if (ls != null && ls != "done") { Retry($"让位寻路没到({ls}),换条链"); return; }
				}
				_ph = Ph.Step; _cellFrames = 0;
				return;
			}

			if (Occupied(_tx, _ty))
			{
				Outcome = "done"; _ph = Ph.Done;
				DiagLog.Write($"[placeany] DONE ({_tx},{_ty}) 接了{_idx}格");
				return;
			}
			// 【交栈之后要让路】:Unstick 会派 pillar/平台梯去造落脚点,它们自己按方向键。
			// 不等的话这边同一帧也在推,两套控制打架,谁也走不成
			if (PillarUp.IsRunning || PlatformDown.IsRunning) { Mark("等落脚点造好"); return; }
			if (_idx >= _chain.Count) { Fail($"链铺完了({_chain.Count}格)目标还是空的"); return; }

			var (x, y) = _chain[_idx];
			if (Occupied(x, y)) { _idx++; _cellFrames = 0; return; }
			if (++_cellFrames > MaxCellFrames) { Retry($"({x},{y})卡了{_cellFrames}帧"); return; }

			// 挡路的挖掉,不管够不够得着(手隔墙够得到时"够不着"分支不进,永远不挖)。
			// 判据是【推了却没挪窝】:见方块就挖会把路两侧刨空
			{
				int bpx = ActExecutor.OriginCx(p);
				if (bpx != _lastPx) { _blockedFrames = 0; _lastPx = bpx; }
				else if (bpx != x && System.Math.Abs(p.velocity.X) < 0.1f) _blockedFrames++;
				if (_blockedFrames >= BlockedAt)
				{
					int bdir = bpx < x ? 1 : -1;
					if (ClearWay.Forward(p, bdir, "挡着放置的路", stuck: true))
					{
						DiagLog.Write($"[placeany] 人卡在{bpx}列{_blockedFrames}帧,挖开往{(bdir > 0 ? "右" : "左")}那面墙");
						_blockedFrames = 0;
						return;
					}
					if (_blockedFrames > BlockedAt * 3)
					{
						DiagLog.Write($"[placeany] 人卡在{bpx}列{_blockedFrames}帧但挖不开,前面={(Predicates.IsWall(bpx + bdir, ActExecutor.OriginCy(p)) ? "墙" : "空")}");
						_blockedFrames = 0;
					}
				}
			}

			// 人挡着就让开。碰撞箱里放不了任何东西
			if (StepAside(p, x, y, out string sw)) { _ph = Ph.Move; return; }
			if (sw.Length > 0) { Retry($"让不开({x},{y}):{sw}"); return; }
			// StepAside 交栈之后还在身子里:栈那边在挖/搭,等它。落了地还没让开就换条链,
			// 别干等到 MaxCellFrames。日志里每次白等 151 帧,那就是"每爬2格停几秒"的来源
			if (InBody(p, x, y))
			{
				if (p.velocity.Y == 0f && _cellFrames > JumpSettleFrames)
				{ Retry($"({x},{y})让位没让开,换条绕开的链"); return; }
				return;
			}
			if (_bad.Contains((x, y))) { Retry($"({x},{y})让不开,绕路"); return; }

			// 够不着:左右走只能改列。让位时人可能掉下去十几行(日志:人1061 目标1051),
			// 那时横向走一辈子也够不着。行差得多就当链失效,从人现在的位置重接一条。
			if (!Reach.CanPlace(p, x, y))
			{
				int cx = ActExecutor.OriginCx(p), cy = ActExecutor.OriginCy(p);
				// 行差太大得先【改行】(pillar/平台梯),交栈。上方目标站位选 y+4:
				// 再高目标进身体,再低放置尺子够不到(现场:桥起点悬空在头顶6行,这儿直接认输整趟 STUCK)
				if (System.Math.Abs(cy - y) > RowGap)
				{
					int foot = y < cy ? y + 4 : y - 1;
					if (!Unstick.Handle("placeany", new Blocker(BlockKind.NoFooting, x, foot, $"差{System.Math.Abs(cy - y)}行,先造能放的站位")))
						Fail($"人({cx},{cy})和({x},{y})差{System.Math.Abs(cy - y)}行,造站位也没辙");
					return;
				}
				if (_cellFrames % 60 == 1) DiagLog.Write($"[placeany] 够不着({x},{y}) 人在({cx},{cy})");
				// 【别朝目标走】:走到目标头上,StepAside 又把人赶开,两边互相推翻。
				// 日志:settle 到 3508 → 这里往右推回 3511 → 让开 → 再推回,190帧全是 out_of_reach
				int dst = ApproachCol(p, x, y);
				// 【没有落脚列 = 要造一个,不是再算一遍链】。悬空处这是死结:要放砖得先走近,
				// 要走近得有地站,要有地得先放砖 -- 重算 9 次只会转回同一个结论。交栈让它造
				if (dst == cx)
				{
					DiagLog.Write($"[placeany] 够不着({x},{y}) 人({cx},{cy}) 周围{MaxAsideCols}列内没有落脚列,交栈造一个");
					if (!Unstick.Handle("placeany", new Blocker(BlockKind.NoFooting, x, y + 1, "够不着又没有落脚列")))
						Retry($"够不着({x},{y})但没有能站的落脚列,交栈也没辙");
					return;
				}
				int dir = dst > cx ? 1 : -1;
				// 地形挡着就挖开,不然横向走一辈子也过不去(卡满 MaxCellFrames 才报错)
				if (ClearWay.Forward(p, dir)) { Mark("挖挡路"); return; }
				if (dir > 0) p.controlRight = true; else p.controlLeft = true;
				return;
			}
			if (PlaceAction.IsRunning) { Mark("挥手放置中"); return; }
			if (PlaceAction.Outcome == "blocked")
			{
				DiagLog.Write($"[placeany] ({x},{y})放不上:{PlaceAction.Reason}");
				// out_of_reach 是【人站错了】不是这格不行。拉黑它会把好格子一个个丢掉,
				// 链越重算越远(日志里连丢 8 格)。这种只等下一帧,让上面的走位去解决。
				if (PlaceAction.Reason != null && PlaceAction.Reason.Contains("out_of_reach")) { Mark("放置报够不着"); return; }
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
			{
				// 【卡满 151 帧却一条分支日志都没有】= 每帧走到底又什么都没做。
				// 把当时的判据全打出来,不然只能看见"重算链"循环八次然后 STUCK
				var pp = Main.LocalPlayer;
				var (tx0, ty0) = _idx < _chain.Count ? _chain[_idx] : (_tx, _ty);
				if (pp != null) DiagLog.Write($"[placeany] 卡住现场 目标({tx0},{ty0}) 人({ActExecutor.OriginCx(pp)},{ActExecutor.OriginCy(pp)})"
					+ $" 身子里={InBody(pp, tx0, ty0)} 够得着={Reach.CanPlace(pp, tx0, ty0)} 落脚列={ApproachCol(pp, tx0, ty0)}"
					+ $" 放置中={PlaceAction.IsRunning} 放置结果={PlaceAction.Outcome}/{PlaceAction.Reason}"
					+ $" vy={pp.velocity.Y:0.##} 拉黑={_bad.Count}");
			}
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
