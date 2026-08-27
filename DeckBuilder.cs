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
		private static int _idx;
		private static string _item = "";
		private static int _frames, _cellFrames;
		private static bool _tried;   // 这一格我们动过手,用来分清 Placed / Already
		private static int _recovers;
		private static int _skipped;   // 连着放不上的格数,断了就清零
		private static int _runAt = -1;   // 已经在这个下标起过一趟连铺

		// 桥面有起伏,人站的行会差一点,松一格免得刚好在坡上误判成掉下去
		// 竖直差这么多就不是走两步能解决的,交栈。够得着的判据本身有几行余量,所以要比它松
		// 低这么多以内自己跳一下就上去了,再多就不是跳能解决的,交寻路
		private const int JumpBackSlack = 2;
		private const int VertSlack = 4;
		private const int StandSlack = 1;
		private const int MaxRecovers = 8;
		private const int MaxSkips = 6;
		private const int MinRun = 3;   // 短于这个不值得起一趟 BridgeBuilder,直接单格放

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
			Outcome = "running"; Reason = "";
			_ph = Ph.Place;
			DiagLog.Write($"[deck] start 料={(itemName.Length == 0 ? "任意方块:" + PickBlock() : itemName)} 共{line.Count}格 从i={_idx} ({line[_idx].x},{line[_idx].y})");
			return true;
		}

		public static void Stop()
		{
			if (Outcome == "running") Outcome = "stopped";
			_ph = Ph.Idle;
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
				PathVisSystem.ClearDeck();
				DiagLog.Write($"[deck] DONE 放了{Placed}格 本来就有{Already}格");
				return;
			}

			var (x, y) = _line[_idx];

			// 桥面必须站得住,所以只认 IsGround。判 HasTile 会把草/藤当铺好了,人走上去直接掉下去
			if (Predicates.IsGround(x, y) && !Predicates.IsPlatform(x, y))
			{
				if (_tried) Placed++; else Already++;
				_idx++; _cellFrames = 0; _tried = false; _skipped = 0;
				return;
			}
			// 桥面这一格是平台:挖掉换成方块。平台会被踩空/穿下去,当桥面不合格
			if (Predicates.IsPlatform(x, y))
			{
				if (ItemUseCoordinator.IsActive) return;
				int ppk = ClearWay.PickSlot(p);
				if (ppk < 0) { Fail($"({x},{y})是平台要换成方块,但没镐"); return; }
				if (!p.IsInTileInteractionRange(x, y, Terraria.DataStructures.TileReachCheckSettings.Simple))
				{ if (ActExecutor.OriginCx(p) < x) p.controlRight = true; else p.controlLeft = true; return; }
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
				return;
			}

			// 人得站在【已经铺好的那一段】上。低了就跳:Reach 判的是"够得着"不是"站上去",
			// 人在桥面下方也够得着 → 立刻 done → 下一帧又低 → 又 Start,于是刷屏且没爬上去
			int py = ActExecutor.OriginCy(p), px = ActExecutor.OriginCx(p);
			if (py > y - 1 + StandSlack)
			{
				// 数帧会在跳到一半判死(一跳十几帧),所以只在落地时计次;腾空中横向照推不计次
				if (p.velocity.Y != 0f)
				{ if (px < x) p.controlRight = true; else if (px > x) p.controlLeft = true; return; }
				// 差一格就自己跳(比启动整套寻路便宜)。差得多【交给寻路】——
				// 它会挖会搭会跳,而这儿硬按方向键只会在坎前蹭,8 次蹭不上去就整条桥失败
				if (py - (y - 1) > JumpBackSlack)
				{
					if (!Unstick.Handle("deck", new Blocker(BlockKind.OutOfReach, x, y - 1, "回桥面")))
						Fail($"回不了桥面 (人{py} 桥面{y})");
					return;
				}
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
			if (BridgeBuilder.IsRunning) return;
			// 连铺必须从有锚的格子起步:BridgeBuilder 不造锚,第一格悬空就整段 no_anchor(日志 20格 placed=0)。
			// 换行处新行头一格和上一段是斜对角不是四邻 —— 那一格交给 PlaceAnywhere 造
			if (PlaceAnywhere.Outcome != "stuck" && _runAt != _idx
			    && ItemUseCoordinator.HasAnchor(x, y))
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
			if (!p.IsInTileInteractionRange(x, y, Terraria.DataStructures.TileReachCheckSettings.Simple))
			{
				if (PlaceAnywhere.IsRunning) return;
				// 真实地形横在路上(要塞墙/矿脉/山体)就挖开 —— 老的 HellDeck 早有这一手,
				// 我新写这个时漏了,于是同一堵墙老路径过得去、新路径卡死
				if (ClearWay.Forward(p, px < x ? 1 : -1)) return;
				// 挡着又没镐:横着走一辈子也过不去,当场报出来,别烧满 MaxCellFrames 才说"卡了"
				if (!ClearWay.HasPick(p) && Predicates.IsWall(px + (px < x ? 1 : -1), py))
				{ Fail($"({px},{py})前面有地形挡着,手上没镐挖不开"); return; }
				// 【竖直够不着不能靠横移】。人在桥面上方 39 行时往右走一辈子也够不着,
				// 而每走一列 x 跟着推进 -> 列号一路爬(702->726),全是 out_of_reach 一格没铺。
				// 差得多就交栈(它会寻路/造落脚点),横移只管同高度的小偏差
				if (System.Math.Abs(py - y) > VertSlack)
				{
					if (!Unstick.Handle("deck", new Blocker(BlockKind.OutOfReach, x, y, "桥面够不着")))
						Fail($"人在{py},桥面{y},差{System.Math.Abs(py - y)}行,够不着又救不回来");
					return;
				}
				if (px < x) p.controlRight = true; else if (px > x) p.controlLeft = true;
				return;
			}

			if (PlaceAnywhere.IsRunning) return;
			// 一格放不上不该毁掉整条桥:跳过它接着铺,人走到那儿会掉一下但桥还在往前长。
			// 全线放不上才算真失败 —— 那时 _skipped 会一路涨上去。
			if (PlaceAnywhere.Outcome == "stuck" && _tried)
			{
				DiagLog.Write($"[deck] 第{_idx}格({x},{y})跳过:{PlaceAnywhere.Reason}");
				if (++_skipped > MaxSkips) { Fail($"连着{_skipped}格放不上,最后({x},{y}):{PlaceAnywhere.Reason}"); return; }
				PlaceAnywhere.Outcome = "idle";
				_idx++; _cellFrames = 0; _tried = false;
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

		static void Fail(string reason)
		{
			Outcome = "stuck"; Reason = reason; _ph = Ph.Idle;
			DiagLog.Write($"[deck] STUCK {reason}");
		}
	}
}
