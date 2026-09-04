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
		private static int _checkedIdx = -1;         // 前方挖不动的东西查到哪一格了,一格只查一次
		private static (int, int) _detourAt = (int.MinValue, int.MinValue);   // 上次为哪一格改的线
		// 【每条无声的 return 都留个名字】。人站着不动、日志 500 帧全空的时候,
		// 唯一能查的就是"每帧走到哪一条就退出了"。60 帧汇报一次,不刷屏
		private static string _where = "";
		private static int _heartbeat;
		private const int SameColStuck = 30;         // 同列够不着这么多帧 = 横移解决不了,交栈

		// 竖直差这么多就不是走两步能解决的,说明人掉下去了,交寻路
		private const int VertSlack = 4;

		// 往前迈那一列脚下有没有地。判据和 BridgeBuilder 那份一致:桥面是平台也算,
		// 脚下这行或再下一行有地都行(桥面会抬升/下沉一格)
		private static bool NextStepStandable(Player p)
		{
			var (bl, br) = Predicates.BodyCols(p);
			int nx = _line[_idx].x, npx = ActExecutor.OriginCx(p);
			int col = nx > npx ? br + 1 : bl - 1;
			int fy = ActExecutor.OriginCy(p);
			return Predicates.IsGround(col, fy + 1) || Predicates.IsGround(col, fy + 2);
		}

		// 【绝不走到桥头】:站在边缘那一格,一个残余横速就掉下去。距离不写死,
		// 按【最窄的那把尺子】(挖)反推 -- 手臂长短一变它自己跟着变
		private static bool KeepBack(Player p, int x, int y) => Reach.CanMine(p, x, y);
		private const int MaxRecovers = 8;
		private const int MaxSkips = 6;
		private const int MinRun = 3;   // 短于这个不值得起一趟 BridgeBuilder,直接单格放
		// 朝目标推了这么多帧还没换列 = 被顶住了
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
			       && _line[i + n].x == _line[i].x + dir * n
			       && !NeedsDig(_line[i + n].x, _line[i + n].y)) n++;
			return n;
		}

		// 【要先挖才能铺的格子必须断开连续段】。BridgeBuilder 只会放不会挖:段里混着
		// 地狱石砖(76)/平台/罐子,它就对着那格空挥到超时,那两格永远换不掉
		static bool NeedsDig(int x, int y)
			=> Predicates.IsPlatform(x, y) || IsHellBrick(x, y) || Predicates.IsClutter(x, y);

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
			_lastDx = int.MaxValue; _blockedFrames = 0; _sameColFrames = 0; _where = ""; _heartbeat = 0;
			_stillHash = 0; _stillCount = 0; _stillAt = 0; _frozen = 0; _checkedIdx = -1;
			_detourAt = (int.MinValue, int.MinValue);
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

		// 地狱石砖:废墟自带,踩上去烧人 --- 桥面不能留它
		static bool IsHellBrick(int x, int y)
		{
			var t = Main.tile[x, y];
			return t.HasTile && t.TileType == Terraria.ID.TileID.HellstoneBrick;
		}

		// 这一格已经是合格的桥面。铺桥的验收和算线的免铺折扣都问这一份,别各编一套
		public static bool DeckReady(int x, int y)
			=> Predicates.IsGround(x, y) && !Predicates.IsPlatform(x, y) && !IsHellBrick(x, y);

		public static void Tick()
		{
			if (!IsRunning) return;
			var p = Main.LocalPlayer;
			if (p == null || !p.active) { Fail("no_player"); return; }
			if (++_frames > MaxFrames) { Fail($"超时 铺到第{_idx}/{_line.Count}格"); return; }
			// 【卡死就回到逐格】:连铺中间一格放不上就整段停住,而这边每帧让路,
			// 逐格那道检查一次也轮不到。逐格永远推得动
			if (Frozen(p))
			{
				DiagLog.Write($"[deck] 周围{StillSeconds}秒一格没变,停掉连铺回到逐格 第{_idx}/{_line.Count}格 人({ActExecutor.OriginCx(p)},{ActExecutor.OriginCy(p)})");
				if (BridgeBuilder.IsRunning) BridgeBuilder.Stop();
				if (PlaceAnywhere.IsRunning) PlaceAnywhere.Stop();
				_runAt = _idx;   // 这一段别再起连铺了,就是它卡的
				if (++_frozen > MaxFrozen) { Fail($"连着{_frozen}次卡在第{_idx}/{_line.Count}格,救不回来"); return; }
				return;
			}

			// 铺完了
			if (_idx >= _line.Count)
			{
				Outcome = "done"; _ph = Ph.Done;
				// 【铺完就放开】。不清的话 WofPrep 捅向导时要挖他脚下那格桥面,会被这条拦住
				_lineSet.Clear();
				// 【铺完必须作废旧罗盘】:场按 goal 缓存,地形变了它不知道,留着会在铺好的桥上再铺一层。
				// 【先停寻路再作废】:缓存没了而寻路还活着,它会当场同步建场(110万格)= 可见卡顿
				if (RecedingNav.Active) RecedingNav.Stop();
				MazeWand.InvalidateField();
				DiagLog.Write("[deck] 桥铺完了,作废旧罗盘");
				PathVisSystem.ClearDeck();
				DiagLog.Write($"[deck] DONE 放了{Placed}格 本来就有{Already}格");
				return;
			}

			// 【第二道保险】:算线时按镐力禁过一遍,但那是按当时的世界。铺到跟前发现挖不动就改线,
			// 别对着熔炉挥到看门狗判死
			if (_idx != _checkedIdx)
			{
				_checkedIdx = _idx;
				int bad = FirstUnmineable();
				if (bad >= 0 && !Detour(bad)) return;
			}
			var (x, y) = _line[_idx];

			// 【清净空时连铺必须停】:两个 Tick 同一帧都跑,这边站着挖那边照样往前走,
			// 人就边走边挖,一直走到那格正上方 -- 脚下只剩刚放的一格,下一步就掉
            if (ClearAhead(p))
			{
				if (BridgeBuilder.IsRunning) BridgeBuilder.Stop();
				Mark("清净空"); return;
			}

			// 桥面必须站得住,所以只认 IsGround。判 HasTile 会把草/藤当铺好了,人走上去直接掉下去
			// 地狱石砖(76)是废墟自带的,踩上去烧人 --- 当成不合格,照平台那条挖掉换方块
			if (DeckReady(x, y))
			{
				if (_tried) Placed++; else Already++;
				_idx++; _cellFrames = 0; _tried = false; _frozen = 0; _skipped = 0;
				_lastDx = int.MaxValue; _blockedFrames = 0;   // 换目标了,离目标多远重新算
				// 每 20 格报一次进度。逐格打会淹掉日志,一行不打就分不出"在推进"和"死了"
				if (_idx % 20 == 0)
					DiagLog.Write($"[deck] 进度{_idx}/{_line.Count} 放了{Placed} 本来就有{Already}");
				// 【铺好一格就跟一步】:PlaceAnywhere 是站着放的,不跟就一次落后一两格。
				// 【但够得着就别再往前】:走到桥头那一格,一个残余横速就掉下去
				if (_idx < _line.Count)
				{
					var (nx, ny) = _line[_idx];
					int npx = ActExecutor.OriginCx(p);
					if (!KeepBack(p, nx, ny) && NextStepStandable(p))
					{
						if (nx > npx) p.controlRight = true;
						else if (nx < npx) p.controlLeft = true;
					}
				}
				return;
			}
			// 平台/地狱石砖/占位物(罐子、草、藤)都挖掉换方块。罐子原来直接跳过,桥面就留了洞;
			// 占位物敲不动就别死磕,落到下面那条跳过
			bool clutterGaveUp = Predicates.IsClutter(x, y) && _cellFrames > MaxCellFrames;
			if ((Predicates.IsPlatform(x, y) || IsHellBrick(x, y) || Predicates.IsClutter(x, y)) && !clutterGaveUp)
			{
				if (ItemUseCoordinator.IsActive) { Mark("挖平台中"); return; }
				int ppk = ClearWay.PickSlot(p);
				if (ppk < 0) { Fail($"({x},{y})要换成方块,但没镐"); return; }
				// 【这一步是挖,按挖的尺子量】。CanPlace 宽出一个 blockRange(让步的 8 格),
				// 于是在挖不到的地方放行,每帧发起一次挖、每帧挖不动,181 帧后报 STUCK
				if (!Reach.CanMine(p, x, y))
				{
					// 【这条 return 也要计卡住】:累加 _blockedFrames 的代码排在下面,每帧从这儿返回
					// 就永远跑不到,blocked 恒为 0,ClearAhead 一次都不触发,4200 帧原地不动
					int pdx = System.Math.Abs(ActExecutor.OriginCx(p) - x);
					if (pdx < _lastDx) { _blockedFrames = 0; _lastDx = pdx; }
					else _blockedFrames++;
					Mark("走去换平台");
					if (ActExecutor.OriginCx(p) < x) p.controlRight = true; else p.controlLeft = true;
					// 推不动就先把身前那面墙清了 —— ClearAhead 里那段正是干这个的
					if (_blockedFrames >= BlockedAt && ClearAhead(p)) { _blockedFrames = 0; Mark("清身前的墙"); }
					return;
				}
				// 平台/石砖换不掉是真失败(那是路);占位物由上面 clutterGaveUp 接走
				if (++_cellFrames > MaxCellFrames) { Fail($"({x},{y})换不掉,卡了{_cellFrames}帧"); return; }
				ItemUseCoordinator.Start(new ItemUseRequest { TargetWx = x, TargetWy = y, Slot = ppk, Strict = true });
				DiagLog.Write($"[deck] ({x},{y})挖掉换方块");
				return;
			}
			// 【兜底】:上面那条挖不掉才走到这儿(挖不动的占位物)。放置那边判"已经有东西"
			// 直接 done,这边判"还没好",不跳过就每帧对撞死循环
			if (Predicates.IsClutter(x, y))
			{
				DiagLog.Write($"[deck] ({x},{y})有占位物但站不住,跳过");
				if (++_skipped > MaxSkips) { Fail($"连着{_skipped}格站不住,最后({x},{y})"); return; }
				_idx++; _cellFrames = 0; _tried = false; _frozen = 0;
				_lastDx = int.MaxValue; _blockedFrames = 0;
				return;
			}

			int py = ActExecutor.OriginCy(p), px = ActExecutor.OriginCx(p);
			// 判据是【推了却没靠近】:见方块就挖会把桥两侧刨空。
			// 【够得着不算卡住】:那是故意留的安全距离,否则每 20 帧刨一次桥边地形
			int dxNow = System.Math.Abs(px - x);
			if (KeepBack(p, x, y) || dxNow < _lastDx) { _blockedFrames = 0; _lastDx = dxNow; }
			else if (dxNow > 0) _blockedFrames++;
			if (_blockedFrames >= BlockedAt)
			{
				int bdir = px < x ? 1 : -1;
				if (ClearWay.Forward(p, bdir, "挡着桥面的路", stuck: true))
				{
					DiagLog.Write($"[deck] 人卡在{px}列{_blockedFrames}帧,挖开往{(bdir > 0 ? "右" : "左")}那面墙");
					// 【这一列没挖干净就别清零】:Forward 一帧只挖一格,提前清零会让剩下的
					// 一两格再没人管,人头顶留着方块,走到那儿被顶住
					if (!ClearWay.ForwardClear(p, bdir)) return;   // 还有活儿,保住计数
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

			// 【只管掉下去,不管在上方】:桥面下降时人站在旧行、把新行铺在下方,那是常态,
			// 拿绝对值判会把整个下降段当成出事,每隔几格叫一次寻路
			if (py - (y - 1) > VertSlack)
			{
				if (RecedingNav.Active) { Mark("寻路回桥上"); return; }
				if (BridgeBuilder.IsRunning) BridgeBuilder.Stop();
				if (++_recovers > MaxRecovers) { Fail($"回不了桥面 (人{py} 桥面{y})"); return; }
				DiagLog.Write($"[deck] 人{py} 桥面{y} 差{py - (y - 1)}行,寻路回桥上({_recovers}/{MaxRecovers})");
				RecedingNav.Start(x, y - 1, RecedingNav.Mode.Stand);
				_cellFrames = 0;
				return;
			}
			_recovers = 0;

			// 同一行的连续段一次铺完:房子的 base 就是这么干的 —— BridgeBuilder 锁一个槽连着放,
			// 实测 5.93 格/秒。逐格调 PlaceAnywhere 每格都要重新归位手上的东西,手根本没用满
			if (BridgeBuilder.IsRunning) { Mark("连铺中"); return; }
			// 连铺必须从有锚的格子起步(BridgeBuilder 不造锚,第一格悬空就整段 no_anchor)。
			// 【用方块的判据】:借绳子那份(不认背景墙)会在有墙的地方把能起步的格判成没锚
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

			// 走到够得着再交给 PlaceAnywhere,不然每格都要"启动→发现够不着→走→放→收摊"。
			// 【按最窄的那把尺子(挖)】:只够放不够挖的话,挡路的障碍清不掉
			if (!KeepBack(p, x, y))
			{
				if (PlaceAnywhere.IsRunning) { Mark("够不着+放置中"); return; }
				// 挡着又没镐:横着走一辈子也过不去,当场报出来,别烧满 MaxCellFrames 才说"卡了"
				if (!ClearWay.HasPick(p) && Predicates.IsWall(px + (px < x ? 1 : -1), py))
				{ Fail($"({px},{py})前面有地形挡着,手上没镐挖不开"); return; }
				// 【竖直够不着不能靠横移】:人在桥面上方 39 行时往右走一辈子也够不着,
				// 列号还一路爬。差得多就交栈,横移只管同高度的小偏差
				if (System.Math.Abs(py - y) > VertSlack)
				{
					// 【人在桥面上方且桥面在前方 = 往前走一步就掉下去】:交栈只会叫导航
					// "站到那格",可那格还是空气,递归 8 层全在"回桥面"上打转然后放弃
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
				// 【同列还够不着 = 竖直问题,横移救不了】:不这么写就是空转,
				// 不按键不计数不打日志,人站着不动而日志 500 帧全空
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
				_idx++; _cellFrames = 0; _tried = false; _frozen = 0;
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

		// 桥面上方要空几行。【HellLine.Head 读的就是这一份】:算线按它禁列、
		// 铺桥按它清障,两边必须是同一个数
		public const int HeadClear = 4;
		const int LookAhead = 4;   // 往前看几格。太少会走到跟前才发现,太多会挖到用不上的地方

		// 【卡死只认世界事实】:别的判据都挂在各自分支上,从别的 return 退出就一次也数不到。
		// 这条在 Tick 最前面,不管走哪条分支都数得到:周围地形连着几秒一格没变就是真卡住了
		const int StillSeconds = 3;
		const int StillEvery = 10;                       // 每这么多帧采一次样,别每帧扫 61x61
		const int StillMax = 60 * StillSeconds / StillEvery;
		const int MaxFrozen = 5;                         // 同一格救这么多次还是不动,才真判死
		static int _stillHash, _stillCount, _stillAt, _frozen;

		static bool Frozen(Player p)
		{
			if (_frames - _stillAt < StillEvery) return false;
			_stillAt = _frames;
			int cx = ActExecutor.OriginCx(p), cy = ActExecutor.OriginCy(p);
			int r = Player.tileRangeX + 2;
			int h = 17;
			for (int x = cx - r; x <= cx + r; x++)
				for (int y = cy - r; y <= cy + r; y++)
					h = h * 31 + (Predicates.InBounds(x, y) && Main.tile[x, y].HasTile ? Main.tile[x, y].TileType + 1 : 0);
			// 人挪了窝也算有变化 -- 走在长直段上时地形确实一动不动,那不是卡住
			h = h * 31 + cx; h = h * 31 + cy;
			if (h != _stillHash) { _stillHash = h; _stillCount = 0; return false; }
			return ++_stillCount >= StillMax;
		}

		// 前方 LookAhead 格里第一个被挖不动的东西占着的(桥面格本身站不住的,或头顶净空里的)。
		// 桥面格已经是能站的实心就不算,它就是桥
		static int FirstUnmineable()
		{
			int pick = MazeWand.BestPickPower();
			for (int k = 0; k < LookAhead && _idx + k < _line.Count; k++)
			{
				var (cx, cy) = _line[_idx + k];
				int top = TopOfCol(cx, cy);
				if (!Predicates.IsGround(cx, top) && HellLine.Unmineable(cx, top, pick)) return _idx + k;
				for (int r = 1; r <= HeadClear; r++)
					if (HellLine.Unmineable(cx, top - r, pick)) return _idx + k;
			}
			return -1;
		}

		// 从人站的那格(上一格,已铺好)起把剩下的线重算,换掉 _line 尾巴。人站的那格不变,_idx 仍指它下一格
		static bool Detour(int badI)
		{
			int from = System.Math.Max(_idx - 1, 0);
			var (sx, sy) = _line[from];
			var (bx, by) = _line[badI];
			int lastX = _line[_line.Count - 1].x;
			DiagLog.Write($"[deck] 第{badI}格({bx},{by})挡着挖不动的东西(type={Main.tile[bx, by].TileType}),从第{from}格({sx},{sy})改线到x={lastX}");
			// 改完线还是撞同一格 = 改线绕不开它(衔接格下面那格 DP 没验),别每帧算一遍 Dijkstra
			if (_detourAt == (bx, by)) { Fail($"({bx},{by})改线之后还是挡着"); return false; }
			_detourAt = (bx, by);
			if (!HellLine.Reroute(sx, sy, lastX, out var nl, out string why))
			{ Fail($"({bx},{by})挖不动又绕不开:{why}"); return false; }
			_line.RemoveRange(from, _line.Count - from);
			_line.AddRange(nl);
			_lineSet.Clear();
			foreach (var c in _line) _lineSet.Add(c);
			// nl[0] 就是人站的那格;_idx 本来就在 0 的话那格还没铺,别跳过它
			_idx = from < _idx ? from + 1 : from;
			_runAt = -1; _cellFrames = 0; _tried = false; _frozen = 0; _skipped = 0;
			_lastDx = int.MaxValue; _blockedFrames = 0; _checkedIdx = -1;
			if (BridgeBuilder.IsRunning) BridgeBuilder.Stop();
			if (PlaceAnywhere.IsRunning) PlaceAnywhere.Stop();
			return true;
		}

		// 这一列在线上最高的那格。变高处一列有两格,净空要从最上面那格算起
		static int TopOfCol(int cx, int cy)
		{
			if (OnLine(cx, cy - 1)) return cy - 1;
			return cy;
		}

		static bool ClearAhead(Player p)
		{
			// 【扫多远由挖掘距离定,不写死】:Dig 自己用 Reach.CanMine 挡够不着的,
			// 这儿多扫几格只是多几次判断。写死 4 格的话手够得到第 5 格也不清
			int scan = System.Math.Max(LookAhead, (Player.tileRangeX + 1) * 2);
			for (int k = 0; k < scan && _idx + k < _line.Count; k++)
			{
				var (cx, cy) = _line[_idx + k];
				// 【从这一列最高的那格桥面往上数】。变高处同一列有上下两格(衔接格),
				// 按下面那格算的话上面那格正好落进 HeadClear 里,净空会把自己的桥挖掉
				int top = TopOfCol(cx, cy);
				for (int r = 1; r <= HeadClear; r++)
				{
					// 平台不算挡路(穿得过去),ClearWay.Dig 自己会判;这里只挑实心的问
					if (!Predicates.IsWall(cx, top - r)) continue;
					if (!ClearWay.Dig(p, cx, top - r, $"桥面净空(第{_idx + k}格上方{r})")) continue;
					DiagLog.Write($"[deck] 清第{_idx + k}格({cx},{top})上方{r}行的方块");
					return true;
				}
			}
			// 挡住人的墙常在人和桥线格【之间】,不在任何桥线格头顶,所以身前那列也要清;
			// 但只在真推不动时才挖,无条件挖会把上升段刚铺好的下一块桥面也刨了
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
