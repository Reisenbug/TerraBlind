using System.Collections.Generic;
using Terraria;

namespace TerraBlind
{
	// 沿着 HellLine 一格一格铺桥面。和 BridgeBuilder 的区别只有一个:那个锁死一行,
	// 这个跟着 Line 的起伏走 —— 地狱的线本来就不是平的。
	//
	// 线自己保证了坡度:每列最多变 1 行,而且不会连着两列都变(HellLine 的 maxStep/backToBack)。
	// 所以人站在已铺好的桥面上,下一格永远在伸手范围内,不用另算怎么爬。
	public static class DeckBuilder
	{
		private enum Ph { Idle, Place, Done }
		private static Ph _ph = Ph.Idle;

		private static List<(int x, int y)> _line = new();
		// 桥线上的格【绝不挖】。卡住时挖身前那一格是对的,但上升段身前那格正是下一块桥面,
		// 挖了等于把刚铺的桥拆断(ClearWay 那条"一格高台阶不挖"的规则本来就是为这个)。
		// 铺桥期间对外公开这份线,让挖的那一侧问一句
		static readonly HashSet<(int, int)> _lineSet = new();
		public static bool OnLine(int x, int y) => _lineSet.Contains((x, y));
		private static int _idx;
		private static string _item = "";
		private static int _frames, _cellFrames;
		private static bool _tried;   // 这一格我们动过手,用来分清 Placed / Already
		private static int _recovers;
		private static int _skipped;   // 连着放不上的格数,断了就清零
		private static int _runAt = -1;   // 已经在这个下标起过一趟连铺
		private static int _lastDx = int.MaxValue;   // 离目标最近到过几列。判"推了却没靠近"= 被顶住
		private static int _blockedFrames;           // 连着几帧没挪窝
		private static int _sameColFrames;           // 同列却够不着,连着几帧
		private static int _lastRecoverAt = -1000;   // 上一次起跳是第几帧
		private const int RecoverGap = 25;           // 一跳约 20 帧落地,隔这么久才算下一次
		// 【每条无声的 return 都留个名字】。人站着不动、日志 500 帧全空的时候,
		// 唯一能查的就是"每帧走到哪一条就退出了"。60 帧汇报一次,不刷屏
		private static string _where = "";
		private static int _heartbeat;
		private const int SameColStuck = 30;         // 同列够不着这么多帧 = 横移解决不了,交栈

		// 桥面有起伏,人站的行会差一点,松一格免得刚好在坡上误判成掉下去
		// 竖直差这么多就不是走两步能解决的,交栈。够得着的判据本身有几行余量,所以要比它松
		// 低这么多以内自己跳一下就上去了,再多就不是跳能解决的,交寻路
		private const int JumpBackSlack = 2;
		private const int VertSlack = 4;
		private const int StandSlack = 1;
		private const int MaxRecovers = 8;
		private const int MaxSkips = 6;
		private const int MinRun = 3;   // 短于这个不值得起一趟 BridgeBuilder,直接单格放
		// 朝目标推了这么多帧还没换列 = 被顶住了。太小会把"起跳前的一帧"误判成卡住
		private const int BlockedAt = 20;

		private const int MaxFrames = 60 * 600;
		private const int MaxCellFrames = 180;

		public static bool IsRunning => _ph != Ph.Idle && _ph != Ph.Done;
		public static string Outcome = "idle";
		public static string Reason = "";
		public static int Placed, Already;

		// 从 i 起【同一行、列连着走】有多少格。桥面有起伏,变行处就断段
		static int RunLen(int i)
		{
			if (i + 1 >= _line.Count) return 1;
			int dir = System.Math.Sign(_line[i + 1].x - _line[i].x);
			if (dir == 0) return 1;
			int n = 1;
			while (i + n < _line.Count
			       && _line[i + n].y == _line[i].y
			       && _line[i + n].x == _line[i].x + dir * n) n++;
			return n;
		}

		// 桥面用任何方块都行:挑存量最多的那种,一种铺光自动换下一种(平台不算,要站得住)
		public static int PickBlock()
		{
			var p = Main.LocalPlayer;
			if (p == null) return -1;
			int best = -1, bestStack = 0;
			for (int i = 0; i < 58 && i < p.inventory.Length; i++)
			{
				var it = p.inventory[i];
				if (it == null || it.IsAir || it.favorited) continue;
				if (it.createTile < 0 || it.stack <= 0) continue;
				if (Main.tileFrameImportant[it.createTile]) continue;   // 家具/门那些不是方块
				if (!Main.tileSolid[it.createTile] || Main.tileSolidTop[it.createTile]) continue;
				if (it.stack > bestStack) { bestStack = it.stack; best = it.type; }
			}
			return best;
		}

		public static bool Start(string itemName, List<(int x, int y)> line, int from, out string why)
		{
			why = "";
			if (line == null || line.Count == 0) { why = "空线"; return false; }
			_item = itemName;
			_line = line; _idx = System.Math.Max(0, from);
			_frames = 0; _cellFrames = 0; Placed = 0; Already = 0; _tried = false; _recovers = 0; _skipped = 0; _runAt = -1;
			_lastDx = int.MaxValue; _blockedFrames = 0; _sameColFrames = 0; _where = ""; _heartbeat = 0; _lastRecoverAt = -1000;
			_lineSet.Clear();
			foreach (var c in line) _lineSet.Add(c);
			Outcome = "running"; Reason = "";
			_ph = Ph.Place;
			DiagLog.Write($"[deck] start 料={(itemName.Length == 0 ? "任意方块:" + PickBlock() : itemName)} 共{line.Count}格 从i={_idx} ({line[_idx].x},{line[_idx].y})");
			return true;
		}

		public static void Stop()
		{
			// 【无声 Stop 是查不动的】:铺到一半被谁停掉,日志和"正常铺完"一模一样。
			// 2768 帧那次就是这样 —— 422 帧没有任何一行,分不出死了还是在走路
			if (IsRunning) DiagLog.Write($"[deck] STOP 停在第{_idx}/{_line.Count}格 已放{Placed}");
			if (Outcome == "running") Outcome = "stopped";
			_ph = Ph.Idle;
			_lineSet.Clear();   // 不铺桥了就别再拦着挖
			PlaceAnywhere.Stop();
		}

		public static void Tick()
		{
			if (!IsRunning) return;
			var p = Main.LocalPlayer;
			if (p == null || !p.active) { Fail("no_player"); return; }
			if (++_frames > MaxFrames) { Fail($"超时 铺到第{_idx}/{_line.Count}格"); return; }

			// 铺完了
			if (_idx >= _line.Count)
			{
				Outcome = "done"; _ph = Ph.Done;
				// 【铺完就放开】。不清的话 WofPrep 捅向导时要挖他脚下那格桥面,会被这条拦住
				_lineSet.Clear();
				// 【铺完必须把罗盘作废】。场是按 goal 缓存的,地形变了它不知道 —— 而这一趟
				// 刚往地狱里填了上百格方块。留着旧场的后果:走桥面时 H 还是"这里全是空的",
				// 于是贪心选出一条 bridge 边(一步跨 84 格把 H 从 564 打到 23),在已经铺好的
				// 桥上再铺一层平台,把方块全换掉。作废之后下一趟 RecedingNav.Start 会在
				// 后台重建(1.5s,不卡主线程)
				// 【先停寻路再作废】。RecedingNav.cs:284 是主线程直接 GetField 的,
				// 缓存没了而寻路还活着的话,它会当场同步建场(110万格,1.5秒)= 可见卡顿。
				// 桥都铺完了,那趟"回桥面"的寻路没有继续的理由,停掉正好;
				// 下一趟 Start 会在后台把新场建起来
				if (RecedingNav.Active) RecedingNav.Stop();
				MazeWand.InvalidateField();
				DiagLog.Write("[deck] 桥铺完了,作废旧罗盘");
				PathVisSystem.ClearDeck();
				DiagLog.Write($"[deck] DONE 放了{Placed}格 本来就有{Already}格");
				return;
			}

			var (x, y) = _line[_idx];

			// 【铺到之前先把上方净空挖出来】。人走桥面时身子要占 3 行,头顶还得留出跳的余量;
			// 等走到跟前被顶住再救就晚了 —— 那时人卡在桥面下一行,跳 8 次全撞天花板然后整条桥失败
			// (现场:人1051 桥面1050 只差一行,连报 8 次"跳上去"然后 STUCK)。
			// 往前看几格一起清,免得刚清完当前格、下一格的天花板又把人拦住
            if (ClearAhead(p)) { Mark("清净空"); return; }

			// 桥面必须站得住,所以只认 IsGround。判 HasTile 会把草/藤当铺好了,人走上去直接掉下去
			if (Predicates.IsGround(x, y) && !Predicates.IsPlatform(x, y))
			{
				if (_tried) Placed++; else Already++;
				_idx++; _cellFrames = 0; _tried = false; _skipped = 0;
				_lastDx = int.MaxValue; _blockedFrames = 0;   // 换目标了,离目标多远重新算
				// 每 20 格报一次进度。逐格打会淹掉日志,一行不打就分不出"在推进"和"死了"
				if (_idx % 20 == 0)
					DiagLog.Write($"[deck] 进度{_idx}/{_line.Count} 放了{Placed} 本来就有{Already}");
				// 【铺好一格就跟着往前一步】。连铺(BridgeBuilder)自带走位,而斜坡上同一行常常
				// 不足 MinRun 格,全走 PlaceAnywhere —— 那条是站着放的,一次换行落后一两格,
				// 几次累积到 14 格,桥头比人低三四行,于是判"回不了桥面"叫寻路,来回折腾。
				if (_idx < _line.Count)
				{
					int npx = ActExecutor.OriginCx(p), nx = _line[_idx].x;
					if (nx > npx) p.controlRight = true;
					else if (nx < npx) p.controlLeft = true;
				}
				return;
			}
			// 桥面这一格是平台:挖掉换成方块。平台会被踩空/穿下去,当桥面不合格
			if (Predicates.IsPlatform(x, y))
			{
				if (ItemUseCoordinator.IsActive) { Mark("挖平台中"); return; }
				int ppk = ClearWay.PickSlot(p);
				if (ppk < 0) { Fail($"({x},{y})是平台要换成方块,但没镐"); return; }
				// 【这一步是挖,按挖的尺子量】。CanPlace 宽出一个 blockRange(让步的 8 格),
				// 于是在挖不到的地方放行,每帧发起一次挖、每帧挖不动,181 帧后报 STUCK
				if (!Reach.CanMine(p, x, y))
				{
					// 【这条 return 也要计卡住】。它只按左右键横移,人推着墙原地踏步时列号不变,
					// 而累加 _blockedFrames 的代码排在下面 —— 每帧从这儿返回就永远跑不到,
					// blocked 恒为 0,ClearAhead 清身前那段(要 blocked 攒够)一次都不触发,
					// 于是墙不挖、走不过去、平台永远够不着,4200 帧原地不动。
					int pdx = System.Math.Abs(ActExecutor.OriginCx(p) - x);
					if (pdx < _lastDx) { _blockedFrames = 0; _lastDx = pdx; }
					else _blockedFrames++;
					Mark("走去换平台");
					if (ActExecutor.OriginCx(p) < x) p.controlRight = true; else p.controlLeft = true;
					// 推不动就先把身前那面墙清了 —— ClearAhead 里那段正是干这个的
					if (_blockedFrames >= BlockedAt && ClearAhead(p)) { _blockedFrames = 0; Mark("清身前的墙"); }
					return;
				}
				if (++_cellFrames > MaxCellFrames) { Fail($"({x},{y})平台换不掉,卡了{_cellFrames}帧"); return; }
				ItemUseCoordinator.Start(new ItemUseRequest { TargetWx = x, TargetWy = y, Slot = ppk, Strict = true });
				DiagLog.Write($"[deck] ({x},{y})是平台,挖掉换方块");
				return;
			}
			// HasTile 但站不住(草/藤):放置那边判"已经有东西"直接 done,这边判"还没好",
			// 于是每帧对撞死循环(日志:(684,1049) 刷 181 帧)。这一格谁也放不上,跳过
			if (Predicates.IsClutter(x, y))
			{
				DiagLog.Write($"[deck] ({x},{y})有占位物但站不住,跳过");
				if (++_skipped > MaxSkips) { Fail($"连着{_skipped}格站不住,最后({x},{y})"); return; }
				_idx++; _cellFrames = 0; _tried = false;
				_lastDx = int.MaxValue; _blockedFrames = 0;
				return;
			}

			// 人得站在【已经铺好的那一段】上。低了就跳:Reach 判的是"够得着"不是"站上去",
			// 人在桥面下方也够得着 → 立刻 done → 下一帧又低 → 又 Start,于是刷屏且没爬上去
			int py = ActExecutor.OriginCy(p), px = ActExecutor.OriginCx(p);
			// 【挡路的挖掉 —— 不管够不够得着】。原来这句只挂在下面"够不着"那条分支里,
			// 而挡的是【身子】不是【手】:ReachBoost 让手能隔着墙够到 8 格外,于是够得着 →
			// 跳过整条分支 → 交给 PlaceAnywhere → 它那份挖同样只在够不着时跑 → 两边都不挖。
			// 现场:2768 帧铺完(2064,1047)后 422 帧一行日志都没有,墙就在人前面。
			//
			// 判据是【推了却没挪窝】,不是"前面有方块":桥面本来就贴着地形,见方块就挖会把
			// 整条线两侧刨空。人朝目标推了 BlockedAt 帧列号还没变,那才是真被顶住。
			// 判"横着推不动"要看 velocity.X,不看 Y:人低于桥面时下面那段会一直让他跳,
			// 腾空占了大半帧数,拿 Y==0 当门会几乎数不上去 —— 而墙挡着恰恰就是这个场面
			// 【判据是"离目标近了没有",不是"列号变没变"】。人顶着墙时会原地跳、左右蹭,
			// 列号来回变 —— 拿"变了就清零"当门,计数永远攒不到,挖的那条路一次都轮不上。
			// 真正的卡住是【推了半天离目标还是那么远】
			int dxNow = System.Math.Abs(px - x);
			if (dxNow < _lastDx) { _blockedFrames = 0; _lastDx = dxNow; }
			else if (dxNow > 0) _blockedFrames++;
			if (_blockedFrames >= BlockedAt)
			{
				int bdir = px < x ? 1 : -1;
				if (ClearWay.Forward(p, bdir, "挡着桥面的路", stuck: true))
				{
					DiagLog.Write($"[deck] 人卡在{px}列{_blockedFrames}帧,挖开往{(bdir > 0 ? "右" : "左")}那面墙");
					_blockedFrames = 0;
					return;
				}
				// 挖不动(没镐/挖不掉的砖)就别再数了,让下面的超时把现场交出去
				if (_blockedFrames > BlockedAt * 3)
				{
					DiagLog.Write($"[deck] 人卡在{px}列{_blockedFrames}帧但挖不开,前面={(Predicates.IsWall(px + bdir, py) ? "墙" : "空")}");
					_blockedFrames = 0;
				}
			}

			if (py > y - 1 + StandSlack)
			{
				// 数帧会在跳到一半判死(一跳十几帧),所以只在落地时计次;腾空中横向照推不计次
				if (p.velocity.Y != 0f)
				{ Mark("腾空中横推"); if (px < x) p.controlRight = true; else if (px > x) p.controlLeft = true; return; }
				// 【人在桥上就只按左右键,别叫寻路】。桥面是自己刚铺的,连着一路通到目标格,
				// 走过去就是了 —— 而寻路会重建整张场、绕大圈、递归好几层"回桥面"。
				// 判据:脚下踩的就是这条桥线上的格子。
				var (tl, tr) = Predicates.TouchCols(p.position.X, p.width);
				bool onDeck = false;
				for (int c = tl; c <= tr && !onDeck; c++) onDeck = OnLine(c, py + 1);
				if (onDeck)
				{
					Mark("在桥上,走过去");
					if (px < x) p.controlRight = true; else if (px > x) p.controlLeft = true;
					return;
				}
				// 差一格就自己跳(比启动整套寻路便宜)。差得多【交给寻路】——
				// 它会挖会搭会跳,而这儿硬按方向键只会在坎前蹭,8 次蹭不上去就整条桥失败
				if (py - (y - 1) > JumpBackSlack)
				{
					// 【NotStanding 不是 OutOfReach】:要的是脚踩上去,不是手够到。
					// 用 OutOfReach 会走 Mode.Reach,而手隔 3 行就够得着 —— 每帧"到了"却一步不动
					if (!Unstick.Handle("deck", new Blocker(BlockKind.NotStanding, x, y - 1, "回桥面")))
						Fail($"回不了桥面 (人{py} 桥面{y})");
					return;
				}
				// 【跳不起来就是头顶挡着 —— 先挖了再跳】。现场:人1058 桥面1057 只差一行,
				// 而 (1170,1056)(1170,1058) 是地狱石砖,人一动不动连跳 8 次然后 STUCK
				if (ClearWay.Above(p, "挡着跳回桥面"))
				{ Mark("挖头顶"); return; }
				// 【计次要隔开】。原来每帧 +1:按下 controlJump 之后 vanilla 要下一帧才把
				// velocity.Y 变负,这一帧读到的还是 0 —— 于是"腾空不计次"那道门形同虚设,
				// 8 次机会在 9 帧里烧光(日志 3398→3406),人连跳都没跳起来就判死
				if (_frames - _lastRecoverAt < RecoverGap) { Mark("等上一跳落地"); return; }
				_lastRecoverAt = _frames;
				if (++_recovers > MaxRecovers) { Fail($"爬不回桥面 (人{py} 桥面{y})"); return; }
				DiagLog.Write($"[deck] 人在{py},桥面{y},跳上去({_recovers}/{MaxRecovers})");
				// 光按跳是原地起跳,上不去斜前方那一格 —— 得朝目标列一起推
				p.controlJump = true;
				if (px < x) p.controlRight = true; else if (px > x) p.controlLeft = true;
				_cellFrames = 0;
				return;
			}
			_recovers = 0;

			// 同一行的连续段一次铺完:房子的 base 就是这么干的 —— BridgeBuilder 锁一个槽连着放,
			// 实测 5.93 格/秒。逐格调 PlaceAnywhere 每格都要重新归位手上的东西,手根本没用满
			if (BridgeBuilder.IsRunning) { Mark("连铺中"); return; }
			// 连铺必须从有锚的格子起步:BridgeBuilder 不造锚,第一格悬空就整段 no_anchor(日志 20格 placed=0)。
			// 换行处新行头一格和上一段是斜对角不是四邻 —— 那一格交给 PlaceAnywhere 造。
			// 【用方块的判据】:这条桥铺的是方块,而原来借的是绳子那份(不认背景墙),
			// 在地狱要塞那种有墙的地方会把能起步的格判成没锚,整段连铺退化成一格一格慢铺
			if (PlaceAnywhere.Outcome != "stuck" && _runAt != _idx
			    && MazeWand.BlockAnchor(x, y))
			{
				int run = RunLen(_idx);
				if (run >= MinRun)
				{
					_runAt = _idx;
					int rdir = System.Math.Sign(_line[_idx + 1].x - x);
					int bid = _item.Length > 0 ? int.Parse(_item) : PickBlock();
					// 不在这儿 _idx += run:铺没铺满由上面那道 IsGround 逐格认账,
					// 乐观推进会把没铺上的格子当成铺好了,桥上留洞
					if (bid > 0 && BridgeBuilder.Start(bid.ToString(), rdir > 0 ? "right" : "left", run, x, y, out _))
					{
						DiagLog.Write($"[deck] 连铺{run}格 从({x},{y}) 料={bid}");
						_tried = true; _cellFrames = 0;
						return;
					}
				}
			}

			// 走到够得着再交给 PlaceAnywhere。不然它每一格都要"启动→发现够不着→走→放→收摊",
			// 日志里每格 6 列远、13 帧;BridgeBuilder 连续铺是 5.93 格/秒。
			if (!Reach.CanPlace(p, x, y))
			{
				if (PlaceAnywhere.IsRunning) { Mark("够不着+放置中"); return; }
				// 挡着又没镐:横着走一辈子也过不去,当场报出来,别烧满 MaxCellFrames 才说"卡了"
				if (!ClearWay.HasPick(p) && Predicates.IsWall(px + (px < x ? 1 : -1), py))
				{ Fail($"({px},{py})前面有地形挡着,手上没镐挖不开"); return; }
				// 【竖直够不着不能靠横移】。人在桥面上方 39 行时往右走一辈子也够不着,
				// 而每走一列 x 跟着推进 -> 列号一路爬(702->726),全是 out_of_reach 一格没铺。
				// 差得多就交栈(它会寻路/造落脚点),横移只管同高度的小偏差
				if (System.Math.Abs(py - y) > VertSlack)
				{
					// 【人在桥面【上方】而且桥面在前方 = 往前走一步就掉下去了】。
					// 铺出来的桥是往下走的台阶,铺完一段人留在高的那一级,下一格在下方几行 ——
					// 而这条分支直接交栈,横移那条永远轮不到。Unstick 只会叫导航"站到那格",
					// 可那格还是空气,站不上去,于是递归 8 层全在"回桥面"上打转然后放弃。
					// 交栈留给【桥面在上方】(要爬)和【同列】(纯竖直)那两种。
					int fdir = x > px ? 1 : -1;
					if (y > py && px != x && !Predicates.IsWall(px + fdir, py))
					{
						if (fdir > 0) p.controlRight = true; else p.controlLeft = true;
						Mark("往桥面那边走,走过去就掉下去了");
						return;
					}
					// 【脚下就是这条桥,走过去就行】。同上:桥面连着一路通到目标,叫寻路是绕远
					var (rl, rr) = Predicates.TouchCols(p.position.X, p.width);
					for (int c = rl; c <= rr; c++)
						if (OnLine(c, py + 1))
						{
							Mark("在桥上,走过去(横)");
							if (px < x) p.controlRight = true; else if (px > x) p.controlLeft = true;
							return;
						}
					if (!Unstick.Handle("deck", new Blocker(BlockKind.OutOfReach, x, y, "桥面够不着")))
						Fail($"人在{py},桥面{y},差{System.Math.Abs(py - y)}行,够不着又救不回来");
					return;
				}
				// 【同列还够不着 = 竖直问题,横移救不了】。原来这两个 if 都不成立时直接空转:
				// 不按键、不计数、不打日志 —— 人站着不动,日志 500 帧全空,连卡在哪都看不出来。
				// 现场:人在(3123,1054)一动不动,手不挥,玩家按方向键都被这条每帧盖掉
				if (px == x)
				{
					if (++_sameColFrames > SameColStuck)
					{
						_sameColFrames = 0;
						DiagLog.Write($"[deck] 人({px},{py})和桥面({x},{y})同列却够不着,差{System.Math.Abs(py - y)}行,交栈");
						if (!Unstick.Handle("deck", new Blocker(BlockKind.NotStanding, x, y - 1, "同列够不着")))
							Fail($"人({px},{py})和桥面({x},{y})同列够不着,救不回来");
					}
					return;
				}
				_sameColFrames = 0;
				if (px < x) p.controlRight = true; else p.controlLeft = true;
				return;
			}

			if (PlaceAnywhere.IsRunning) { Mark("放置中"); return; }
			// 一格放不上不该毁掉整条桥:跳过它接着铺,人走到那儿会掉一下但桥还在往前长。
			// 全线放不上才算真失败 —— 那时 _skipped 会一路涨上去。
			if (PlaceAnywhere.Outcome == "stuck" && _tried)
			{
				DiagLog.Write($"[deck] 第{_idx}格({x},{y})跳过:{PlaceAnywhere.Reason}");
				if (++_skipped > MaxSkips) { Fail($"连着{_skipped}格放不上,最后({x},{y}):{PlaceAnywhere.Reason}"); return; }
				PlaceAnywhere.Outcome = "idle";
				_idx++; _cellFrames = 0; _tried = false;
				_lastDx = int.MaxValue; _blockedFrames = 0;
				return;
			}
			if (++_cellFrames > MaxCellFrames) { Fail($"({x},{y})卡了{_cellFrames}帧"); return; }
			// 空名字 = 用任何方块。现挑,所以一种用光了下一格自动换别的
			string item = _item;
			if (item.Length == 0)
			{
				int id = PickBlock();
				if (id < 0) { Fail($"第{_idx}格({x},{y}):背包里没有方块了"); return; }
				item = id.ToString();
			}
			// 放置的全部麻烦(够不着/人挡着/没锚)都在 PlaceAnywhere 里,这里只管要结果。
			// 别在这儿 _idx++:它是异步的,这一格要等上面 IsSolid 判真才算完
			if (!PlaceAnywhere.Start(item, x, y, out string pw))
			{ Fail($"第{_idx}格({x},{y}):{pw}"); return; }
			_tried = true;
		}

		// 桥面【上方 HeadClear 行】必须是空的,从当前格往前看 LookAhead 格。
		// 挖了返回 true(这一帧交给挖,别再往下走)
		// 桥面上方要空几行。【HellLine.Head 读的就是这一份】—— 算线按它禁掉
		// "上方有挖不动的东西"的列,铺桥按它清障,两边必须是同一个数
		public const int HeadClear = 4;
		const int LookAhead = 4;   // 往前看几格。太少会走到跟前才发现,太多会挖到用不上的地方

		static bool ClearAhead(Player p)
		{
			// 【挖得到的桥线格,上方 HeadClear 行一律清空】。原来只看前 LookAhead(4) 格,
			// 手明明够得到第 5 格头顶的方块也不清,等走到跟前才发现被顶住。
			// 扫多远由【挖掘距离】定,不由一个写死的数定:Dig 自己会用 Reach.CanMine 挡够不着的,
			// 这儿多扫几格只是多几次判断。上限取 tileRangeX 的两倍,够覆盖手能碰到的全部。
			int scan = System.Math.Max(LookAhead, (Player.tileRangeX + 1) * 2);
			for (int k = 0; k < scan && _idx + k < _line.Count; k++)
			{
				var (cx, cy) = _line[_idx + k];
				for (int r = 1; r <= HeadClear; r++)
				{
					// 平台不算挡路(穿得过去),ClearWay.Dig 自己会判;这里只挑实心的问
					if (!Predicates.IsWall(cx, cy - r)) continue;
					if (!ClearWay.Dig(p, cx, cy - r, $"桥面净空(第{_idx + k}格上方{r})")) continue;
					DiagLog.Write($"[deck] 清第{_idx + k}格({cx},{cy})上方{r}行的方块");
					return true;
				}
			}
			// 【人身前那一列也要清】。上面只管【桥线格的正上方】,而挡住人的墙常常在
			// 人和桥线格【之间】—— 它不在任何桥线格头顶,于是一格都不清,人顶着墙站到死。
			// 人脚那一行到上面 HeadClear 行,就是走过去要占的空间。
			//
			// 【只在真走不动时才挖】:无条件挖身前会把上升段那块刚铺好的桥面也刨了
			// (桥往上走时,身前那一格正是下一块桥面)。人推了半天没挪窝才是真被挡
			if (_blockedFrames < BlockedAt) return false;
			var (bl, br) = Predicates.BodyCols(p);
			int fy = ActExecutor.OriginCy(p);
			int fwd = _idx < _line.Count && _line[_idx].x < bl ? -1 : 1;
			int col = fwd > 0 ? br + 1 : bl - 1;
			for (int r = 0; r <= HeadClear; r++)
			{
				if (!Predicates.IsWall(col, fy - r)) continue;
				if (!ClearWay.Dig(p, col, fy - r, $"挡在身前(往{(fwd > 0 ? "右" : "左")})")) continue;
				DiagLog.Write($"[deck] 清身前({col},{fy - r}) 人({bl}..{br},{fy})");
				return true;
			}
			return false;
		}

		// 记下这一帧走到哪条分支就退出了。同一条连着走 HeartbeatEvery 帧就汇报一次 ——
		// 不打的话"人不动"和"正常干活"在日志里长得一模一样
		const int HeartbeatEvery = 60;
		static void Mark(string where)
		{
			if (where != _where) { _where = where; _heartbeat = 0; return; }
			if (++_heartbeat % HeartbeatEvery != 0) return;
			var p = Main.LocalPlayer;
			var (gx, gy) = _idx < _line.Count ? _line[_idx] : (-1, -1);
			int cx = p != null ? ActExecutor.OriginCx(p) : -1, cy = p != null ? ActExecutor.OriginCy(p) : -1;
			DiagLog.Write($"[deck] 心跳 {_heartbeat}帧都在\"{where}\" 第{_idx}/{_line.Count}格({gx},{gy}) " +
				$"人({cx},{cy}) blocked={_blockedFrames} sameCol={_sameColFrames} cell={_cellFrames} " +
				$"placeany={PlaceAnywhere.Outcome}/{(PlaceAnywhere.IsRunning ? "跑" : "停")}");
		}

		static void Fail(string reason)
		{
			Outcome = "stuck"; Reason = reason; _ph = Ph.Idle;
			_lineSet.Clear();   // 桥不铺了,别再拦着别人挖
			DiagLog.Write($"[deck] STUCK {reason}");
		}
	}
}
