using System.Text;
using Terraria;
using Terraria.ModLoader;

namespace TerraBlind
{
	// BRIDGE, lay a horizontal run N cells long, in a given direction, from where the player stands.
	//
	// ONE phase, not two. Placing and walking happen in the SAME frame: the cursor always aims at the edge cell and
	// swings whenever the hand is free, while the feet advance in the gaps. Laying a tile pushes the edge one cell
	// further out, which is what the feet were walking toward, the two chase each other and neither waits its turn.
	//
	// It used to alternate: fill the whole arm's reach standing still, then walk to the end, then fill again. Every
	// frame spent walking placed nothing, so roughly half the run was the hand sitting idle. Nothing forced that
	// it was justified by "a program can move the cursor a cell per frame, so it need not pave while walking", which
	// is true and beside the point: not having to interleave is not a reason to leave the hand idle.
	//
	// The HAND is the bottleneck (a placement takes item.useTime frames; walking a cell takes fewer), so the feet
	// yield to it: stop advancing once the edge is close enough that another step would overshoot what the hand can
	// lay in the meantime. That margin is computed from the live useTime and the live speed, so boots or a slowing
	// liquid change it automatically, no tuned constant.
	//
	// Everything still ends on an observed fact, the tile is there, the origin cell actually moved, so the result
	// holds at any movement speed.
	public static class BridgeBuilder
	{
		private enum Ph { Idle, Lay, Done }
		private static Ph _ph = Ph.Idle;

		private static string _item = "";
		private static int _slot = -1;
		private static int _dir = 1;           // +1 right, -1 left
		private static int _want, _placed, _already;
		private static int _targetWx, _rowWy;  // cell being placed into; the row the bridge runs along
		private static int _lastOriginCx, _walkStall;
		private static bool _swingIssued;
		private static int _reachStall;          // 手够不着、脚又走不动 = 真卡住了

		private const int WalkStallLimit = 120;
		private static int _digStall;
		private const int DigStallLimit = 60 * 20;   // 挖 20 秒还没通,那就是真过不去
		private const int ReachStallLimit = 240;
		// 上面那几个计数器只在【人没挪列】时才涨,人来回蹭两列就全被清零。于是整套一格
		// 没铺也永远不超时。所以另记两笔:总帧数,和"上次铺成到现在过了多久"
		private static int _frames, _sinceProgress, _lastDone;
		private const int MaxFrames = 60 * 120;
		private const int NoProgressLimit = 60 * 15;

		public static bool IsRunning => _ph == Ph.Lay;
		public static string Outcome = "idle";   // idle running done no_item blocked stuck
		public static string Reason = "";
		public static int Placed => _placed;
		public static int NextWx => _targetWx;   // 停在哪一格没铺。换料续铺要从这儿接上
		public static int RowWy => _rowWy;

		public static bool Start(string itemName, string dir, int n, out string why)
			=> Start(itemName, dir, n, int.MinValue, int.MinValue, out why);

		// startWx/startWy: 从哪一格开始铺。传了就照着铺,人自己走过去够。铺哪儿是调用方定的,
		// 不该跟着身体停在哪儿走。不传(int.MinValue)才退回"从我站的地方往外铺"。
		public static bool Start(string itemName, string dir, int n, int startWx, int startWy, out string why)
		{
			why = "";
			var p = Main.LocalPlayer;
			if (p == null) { why = "no_player"; return false; }
			_slot = PlaceAction.HomeInHotbar(itemName);   // home the item in the hotbar once; use that slot for the whole run
			if (_slot < 0) { why = "no_item"; Outcome = "no_item"; Reason = itemName; return false; }

			_item = itemName; _dir = dir == "left" ? -1 : 1;

			// 桥铺在【人脚下那一行】,人站在它上面一行,走出去不用跳也不用降。
			// 【这是前提,不是建议】:人不在那一行的话它只会左右走,永远够不着边缘,
			// 而调用方每帧让路给它,两边一起僵住(现场:人1043 row=1046,600帧一动不动)
			int wantRow = startWy != int.MinValue ? startWy : ActExecutor.OriginCy(p) + 1;
			if (ActExecutor.OriginCy(p) != wantRow - 1)
			{
				why = $"人在{ActExecutor.OriginCy(p)},该站在{wantRow - 1}才能沿{wantRow}行铺";
				Outcome = "bad_stand"; Reason = why; return false;
			}

			_want = n < 1 ? 1 : n; _placed = 0; _already = 0;
			_swingIssued = false;
			Outcome = "running"; Reason = "";
			_ph = Ph.Lay;
			_rowWy = wantRow;
			_targetWx = startWx != int.MinValue ? startWx : ActExecutor.OriginCx(p) + _dir;
			_lastOriginCx = ActExecutor.OriginCx(p); _walkStall = 0; _reachStall = 0; _digStall = 0;
			_frames = 0; _sinceProgress = 0; _lastDone = 0;
			DiagLog.Write($"[bridge] start {itemName} dir={dir} n={_want} slot={_slot} row={_rowWy} from={_targetWx}");
			return true;
		}

		public static void Stop()
		{
			if (Outcome == "running") Outcome = "stopped";
			_ph = Ph.Idle;
			ItemUseCoordinator.Stop();
		}

		// 手上一次挥完后,ItemUseCoordinator 会把结果留在 Outcome 里。这里只负责:结果算不算数、
		// 边缘往前推一格、够不够数。
		private static bool HarvestSwing()
		{
			if (!_swingIssued) return true;
			if (ItemUseCoordinator.IsActive) return false;   // 还在挥,等
			_swingIssued = false;
			string o = ItemUseCoordinator.Outcome;
			// 地图说了算:那格有我们要的东西就是成了。
			if (IsWanted(_targetWx, _rowWy))
			{
				if (o == "already_there") _already++; else _placed++;
				_targetWx += _dir;
				return true;
			}
			// 够不着不是失败,是该往前走了。走完这一格自然就够得着。
			if (o == "no_swing" && ItemUseCoordinator.Reason == "out_of_reach") return true;
			Reason = ItemUseCoordinator.Reason.Length > 0 ? ItemUseCoordinator.Reason : o;
			Finish("blocked");
			return false;
		}

		// 手是瓶颈:边缘比"放完这块的功夫脚能走的距离"还近就停下等手,不然会走到没铺的地方掉下去
		// 往前迈那一列脚下有没有地。桥面是平台,平台也算 --- IsGround 两个都认
		private static bool NextStepStandable(Player p)
		{
			var (bl, br) = Predicates.BodyCols(p);
			int col = _dir > 0 ? br + 1 : bl - 1;
			int fy = ActExecutor.OriginCy(p);
			// 脚下这一行、或再下一行有地都算(桥面会抬升/下沉一格)
			return Predicates.IsGround(col, fy + 1) || Predicates.IsGround(col, fy + 2);
		}

		private static bool ShouldAdvance(Player p)
		{
			float edgePx = _targetWx * 16f + 8f;
			float me = p.position.X + p.width / 2f;
			float gap = System.Math.Abs(edgePx - me);
			var it = p.inventory[_slot];
			float placeFrames = (it != null && !it.IsAir) ? it.useTime * System.Math.Max(0.1f, p.tileSpeed) : 15f;
			float speed = System.Math.Max(0.5f, p.maxRunSpeed);
			return gap > placeFrames * speed + 8f;
		}

		public static void Tick()
		{
			if (!IsRunning) return;
			var p = Main.LocalPlayer;
			if (p == null || !p.active) { Reason = "no_player"; Finish("stuck"); return; }

			// 卡死判据和"人在哪一列"无关:只看【有没有真铺出格子来】。
			// 铺出一格就清零,长时间没进展就是卡住了,不管人这会儿在原地还是来回走
			if (++_frames > MaxFrames)
			{ Reason = $"超时 铺了{_placed + _already}/{_want}"; Stuck(new Blocker(BlockKind.OutOfReach, _targetWx, _rowWy, "超时")); return; }
			int doneNow = _placed + _already;
			if (doneNow != _lastDone) { _lastDone = doneNow; _sinceProgress = 0; }
			else if (++_sinceProgress > NoProgressLimit)
			{ Reason = $"{_sinceProgress}帧没铺出一格,停在({_targetWx},{_rowWy})"; Stuck(Diagnose()); return; }

			// 1) 收上一次挥的结果(可能把边缘往前推一格,也可能判定失败直接结束)
			if (!HarvestSwing()) return;
			if (!IsRunning) return;

			// 2) 边缘那格已经是我们要的东西(之前铺过/地形本来就有)→ 跳过,不必挥
			while (_placed + _already < _want && IsWanted(_targetWx, _rowWy))
			{
				_already++; _targetWx += _dir;
			}
			if (_placed + _already >= _want) { Finish("done"); return; }

			// 3) 手空着且够得着 → 这一帧就挥
			bool inReach = Reach.CanPlace(p, _targetWx, _rowWy);
			if (!ItemUseCoordinator.IsActive && !_swingIssued && inReach)
			{
				ItemUseCoordinator.Start(new ItemUseRequest
				{ TargetWx = _targetWx, TargetWy = _rowWy, Slot = _slot, DurationTicks = 0, Strict = false });
				_swingIssued = true;
			}

			// 4) 同一帧里决定脚走不走。手在挥的时候脚照样能走,这正是省下来的时间。
			// 【但绝不迈进空里】。原来 !inReach 直接放行,而"够不着"最常见的原因正是
			// 前面那格还没铺出来 --- 人一步跨出桥面边缘就掉下去(现场:桥面1051,人掉到1073)。
			// 踩不住就站着挥,铺出来了自然走得过去
			bool advance = (!inReach || ShouldAdvance(p)) && NextStepStandable(p);
			if (advance)
			{
				if (_dir > 0) p.controlRight = true; else p.controlLeft = true;
			}

			// 卡死:手够不着、脚又没挪窝,才是真卡住。
			int cx = ActExecutor.OriginCx(p);
			if (cx != _lastOriginCx) { _lastOriginCx = cx; _walkStall = 0; _reachStall = 0; _digStall = 0; }
			else
			{
				// 前面是一格高的台阶就跳上去 -- 那是路,不是障碍。
				// 【踩上去必须还在这段桥的行上】:这一段锁死 _rowWy 铺,人就该走在 _rowWy-1。
				// 不问这一句的话,桥面下降时身前同行那格是【旁边的地形】(房子 pillar 侧面),
				// 一样长得像台阶,人就跳到柱子顶上去了,再也回不来
				var (jl, jr) = Predicates.BodyCols(p);
				int fcol = _dir > 0 ? jr + 1 : jl - 1;
				int fy2 = ActExecutor.OriginCy(p);
				if (Predicates.IsGround(fcol, fy2) && !Predicates.IsWall(fcol, fy2 - 1)
					&& fy2 == _rowWy)
				{
					p.controlJump = true;
					if (_dir > 0) p.controlRight = true; else p.controlLeft = true;
					return;
				}
				// 挖归挖,但不能靠它无限续命:挖了也清零 _walkStall 的话,挖不穿就永远不超时
				if (++_digStall < DigStallLimit && ClearWay.Forward(p, _dir)) return;
				if (advance && ++_walkStall >= WalkStallLimit)
				{ Reason = "walk_blocked"; Stuck(new Blocker(BlockKind.Terrain, fcol, fy2, "走不过去")); return; }
				if (!inReach && ++_reachStall >= ReachStallLimit)
				{ Reason = "cant_reach_edge"; Stuck(new Blocker(BlockKind.OutOfReach, _targetWx, _rowWy, "够不着边缘")); return; }
			}
		}

		// 停滞了到底属于哪一类:要放的那格被砖占着=地形,被自己身子压着=让开,
		// 都不是就是够不着。判定只此一处,免得每个失败点各猜一套
		private static Blocker Diagnose()
		{
			var p = Main.LocalPlayer;
			if (Predicates.IsWall(_targetWx, _rowWy))
				return new Blocker(BlockKind.Terrain, _targetWx, _rowWy, "目标格被占");
			if (p != null)
			{
				var (bl, br) = Predicates.BodyCols(p);
				int fy = ActExecutor.OriginCy(p);
				if (_targetWx >= bl && _targetWx <= br && _rowWy >= fy - 2 && _rowWy <= fy)
					return new Blocker(BlockKind.SelfInWay, _targetWx, _rowWy, "人压着目标格");
			}
			return new Blocker(BlockKind.OutOfReach, _targetWx, _rowWy, "没进展");
		}

		private static void Finish(string outcome)
		{
			Outcome = outcome;
			_ph = Ph.Done;
			DiagLog.Write($"[bridge] {outcome} placed={_placed} already={_already}/{_want} reason={Reason}");
		}

		// stuck 的唯一出口,签名逼着交出【卡在哪一格、哪一类】。能救就地救,救不了才真失败
		// 以前 5 处 stuck 各自 return,现场只剩一句给人看的话,代码没法据此补救
		private static void Stuck(Blocker b)
		{
			if (Unstick.Handle("bridge", b)) { _frames = 0; _sinceProgress = 0; _walkStall = 0; _reachStall = 0; _digStall = 0; return; }
			Finish("stuck");
		}

		public static string StatusJson()
		{
			var p = Main.LocalPlayer;
			var sb = new StringBuilder();
			sb.Append("{\"outcome\":\"").Append(Outcome).Append('"')
			  .Append(",\"running\":").Append(IsRunning ? "true" : "false")
			  .Append(",\"phase\":\"").Append(_ph.ToString().ToLowerInvariant()).Append('"')
			  .Append(",\"item\":\"").Append(_item).Append('"')
			  .Append(",\"dir\":").Append(_dir)
			  .Append(",\"placed\":").Append(_placed).Append(",\"already_there\":").Append(_already).Append(",\"wanted\":").Append(_want)
			  .Append(",\"reason\":\"").Append(Reason).Append('"')
			  .Append(",\"target_cell\":[").Append(_targetWx).Append(',').Append(_rowWy).Append(']');
			if (p != null)
				sb.Append(",\"origin\":[").Append(ActExecutor.OriginCx(p)).Append(',').Append(ActExecutor.OriginCy(p)).Append(']')
				  .Append(",\"on_ground\":").Append(p.velocity.Y == 0f ? "true" : "false")
				  .Append(",\"vel_x\":").Append(p.velocity.X.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
			sb.Append('}');
			return sb.ToString();
		}

		// does this cell hold the tile our item makes?
		private static bool IsWanted(int x, int y)
		{
			if (!InBounds(x, y)) return false;
			var t = Main.tile[x, y];
			if (!t.HasTile) return false;
			var p = Main.LocalPlayer;
			if (p == null || _slot < 0 || _slot >= p.inventory.Length) return false;
			var it = p.inventory[_slot];
			return it != null && !it.IsAir && it.createTile >= 0 && t.TileType == it.createTile;
		}

		private static bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Main.maxTilesX && y < Main.maxTilesY;
	}
}
