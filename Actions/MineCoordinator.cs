using Terraria;
using Terraria.ModLoader;

namespace TerraBlind
{
	public enum MineDir { Left, Right, Down, Up }

	public class MineRequest
	{
		public MineDir Dir;
		public int StartWx, StartWy;   // cell the player stood on when mining began, defines the valid box with Target
		public int TargetWx, TargetWy; // landing cell from the planner, mining ends when the player stands here
		public System.Collections.Generic.List<(int wx, int wy)> MineTiles; // planned tiles (fallback when the live hitbox window slides past an obstacle, e.g. a slope half-brick that lifted the player)
	}

	public class MineCoordinator : ModSystem
	{
		private static volatile MineRequest _active;
		// 连着多少帧扫不出要挖的格就认输。横向挖有"走进已挖开的隧道"这种合法空帧,给足余量
		private static int _idleFrames;
		private const int IdleMax = 90;
		// 上次为够不着而伸手的那一格。只为日志去重。每帧都打会刷满
		private static (int, int) _boostLogAt = (int.MinValue, int.MinValue);
		private static int _stallFrames, _lastRemaining;
		private const int StallMax = 600; // ~10s with no tile removed = can't reach the target → bail
		private const int MineBoxMargin = 2; // tiles of slack around the start↔target box before mining aborts

		public static bool IsActive => _active != null;

		public static void Start(MineRequest r)
		{
			_active = r;
			_stallFrames = 0;
			_idleFrames = 0;
			_lastRemaining = int.MaxValue;
		}

		public static void Stop()
		{
			_active = null;
		}

		// solid INCLUDING slopes/half-bricks (mirrors StateSpacePlanner.DigSolid): anything that supports or
		// blocks the hitbox must be mined; platforms (solidTop) are dropped through, not mined.
		private static bool DigSolid(int x, int y)
		{
			if (x < 0 || y < 0 || x >= Main.maxTilesX || y >= Main.maxTilesY) return false;
			var t = Main.tile[x, y];
			return Predicates.IsWall(x, y);
		}

		public static void ApplyControls()
		{
			var req = _active;
			if (req == null) return;
			var p = Main.LocalPlayer;
			if (p == null || !p.active) { _active = null; return; }

			int slot = FindPickaxeSlot(p);
			if (slot < 0) { _active = null; return; }

			// real-time hitbox cells, recomputed every frame so upstream landing drift can't aim mining at
			// stale absolute coords (the bug: tiles planned from a too-shallow landing ended up overhead).
			// 20px wide → 2 columns; 42px tall → 3 rows.
			int leftC = (int)(p.position.X / 16f);
			int rightC = (int)((p.position.X + p.width - 1) / 16f);
			int headR = (int)(p.position.Y / 16f);
			int footR = (int)((p.position.Y + p.height - 1) / 16f);

			int pcx = (int)(p.Center.X / 16f), pfeet = (int)((p.position.Y + p.height) / 16f) - 1;

			// safety box: mining is only ever valid inside the start↔target rectangle (+margin). Any drift
			// horizontal or vertical, that walks the player out of it means execution lost the plan; stop and let
			// the closed-loop replan re-plan from the new position rather than mining whatever is now in front.
			int boxX0 = System.Math.Min(req.StartWx, req.TargetWx) - MineBoxMargin;
			int boxX1 = System.Math.Max(req.StartWx, req.TargetWx) + MineBoxMargin;
			int boxY0 = System.Math.Min(req.StartWy, req.TargetWy) - MineBoxMargin;
			int boxY1 = System.Math.Max(req.StartWy, req.TargetWy) + MineBoxMargin;
			if (pcx < boxX0 || pcx > boxX1 || pfeet < boxY0 || pfeet > boxY1)
			{
				DiagLog.Write($"[mine] out of box dir={req.Dir} p=({pcx},{pfeet}) box=[{boxX0}..{boxX1},{boxY0}..{boxY1}] → stop");
				_active = null;
				return;
			}

			int tx = int.MinValue, ty = int.MinValue, remaining = 0;
			bool done = false;
			switch (req.Dir)
			{
				case MineDir.Right:
				case MineDir.Left:
				{
					int col = req.Dir == MineDir.Right ? rightC + 1 : leftC - 1;
					// rows cover the body (footR..footR-2) AND down to the target row. a slope/half-brick at the exit
					// lifts the player a cell (footR rises), sliding the obstacle out of the body window, but it still
					// sits at/below TargetWy, so include rows down to TargetWy. clamp the span against an anomalous gap.
					int loRow = System.Math.Max(footR - 4, System.Math.Min(footR - 2, req.TargetWy));
					int hiRow = System.Math.Min(footR + 2, System.Math.Max(footR, req.TargetWy));
					for (int y = hiRow; y >= loRow; y--)
						if (DigSolid(col, y)) { remaining++; if (ty == int.MinValue) { tx = col; ty = y; } }
					// fallback: live window empty but planner still lists an un-mined cell ahead → aim at it (covers
					// obstacles the live hitbox slid past, e.g. a slope half-brick that lifted the player).
					if (remaining == 0 && req.MineTiles != null)
						foreach (var (mwx, mwy) in req.MineTiles)
							if (DigSolid(mwx, mwy) && ((req.Dir == MineDir.Right && mwx >= pcx) || (req.Dir == MineDir.Left && mwx <= pcx)))
							{ remaining++; tx = mwx; ty = mwy; break; }
					if (req.Dir == MineDir.Right) p.controlRight = true; else p.controlLeft = true;
					// done = reached OR passed the target column (don't require pfeet exact: a slope/half-brick at the
					// exit lifts the player a cell, so pfeet==TargetWy never holds and mining hangs forever). horizontal
					// progress to the target column is what "tunnelled through" means.
					// AND nothing solid left in the window: a sloped half-brick lets the hitbox squeeze a few px into
					// the target column, so the raw center crosses the line BEFORE anything is mined, done fired on
					// dispatch, zero tiles dug, and the replan re-picked the same dig every 2 ticks (the (1502,588)
					// overhead-slope loop). Passing the line only counts once the tunnel is actually clear; if some
					// tile can never clear, the step watchdog bounds the wait.
					done = (req.Dir == MineDir.Right ? pcx >= req.TargetWx : pcx <= req.TargetWx) && remaining == 0;
					break;
				}
				case MineDir.Down:
				{
					// start at footR (not footR+1): standing on a slope/half-brick puts that support tile in
					// the foot row itself; skipping it mines the cell BELOW the slope and the player never sinks.
					// on a full block footR is air (DigSolid false) so this is a harmless no-op there.
					for (int row = footR; row <= footR + 1; row++)
						for (int x = leftC; x <= rightC; x++) // 2 body columns
							if (DigSolid(x, row)) { remaining++; if (tx == int.MinValue) { tx = x; ty = row; } }
					CenterInShaft(p, leftC, rightC);
					p.controlDown = true; // drop through platforms in the shaft
					done = pfeet >= req.TargetWy; // sank to (or below) the landing cell
					break;
				}
				case MineDir.Up:
				{
					// one dig-up cycle clears the 2 rows above the head so the pillar step can lift 2 tiles.
					for (int y = headR - 1; y >= headR - 2; y--)
						for (int x = leftC; x <= rightC; x++)
							if (DigSolid(x, y)) { remaining++; if (ty == int.MinValue) { tx = x; ty = y; } }
					CenterInShaft(p, leftC, rightC);
					done = remaining == 0; // headroom cleared → let the pillar step take over
					break;
				}
			}
			if (done) { _active = null; return; }

			// only count stalls while there's rock to remove and it isn't shrinking; remaining==0 means we're
			// walking the opened tunnel toward the target, not stuck.
			_stallFrames = (remaining == 0 || remaining < _lastRemaining) ? 0 : _stallFrames + 1;
			_lastRemaining = remaining;
			if (_stallFrames > StallMax)
			{
				DiagLog.Write($"[mine] stalled {StallMax}f dir={req.Dir} target=({req.TargetWx},{req.TargetWy}) → stop");
				_active = null;
				return;
			}

			// 【这条以前是完全静默的 return,而且不清 _active。于是彻底死锁】。
			// 扫不出要挖的格时 remaining==0,上面那段又把 _stallFrames 清零,600 帧的看门狗永远不触发;
			// _active 一直非空,TickSteps 开头的 busy 门就把后面每一轮都挡在 "start n=1" 上,
			// 人一格不动、一条日志不打,只能等 sentinel 判 H 平线放弃整段
			// (现场:(1461,230) 要挖 (1460,230),第一次派发之后 14 轮全停在 start n=1)。
			// 横向那一步本来就有"走进已挖开的隧道"这种合法的空帧,所以不能一见空就停;
			// 但连着空这么久就是真扫不出来了,交回上层重规划
			if (tx == int.MinValue)
			{
				if (++_idleFrames <= IdleMax) return;
				DiagLog.Write($"[mine] 连着{IdleMax}帧扫不出要挖的格 dir={req.Dir} target=({req.TargetWx},{req.TargetWy}) 人=({pcx},{pfeet}) → stop");
				_active = null; _idleFrames = 0;
				return;
			}
			_idleFrames = 0;

			// the target tile supports an attached object above (chest/tree/etc.) → Terraria won't let it break;
			// swinging forever would hang. abort now (don't wait out StallMax) so closed-loop replan routes around.
			if (!Terraria.WorldGen.CanKillTile(tx, ty))
			{
				DiagLog.Write($"[mine] unbreakable ({tx},{ty}) dir={req.Dir} → stop");
				_active = null;
				return;
			}

			// 【够不着就别空挥】。vanilla 够不着时根本不破坏方块,而这里原来只管瞄准+按键,
			// 于是挥满 StallMax 帧一格没掉,上层重选又选中同一条边,再挥。死循环。
			// 现场:人在(1118,829)对着(1117,831)挥了 600 帧,sentinel 判 H 平线放弃整段。
			// 尺子用 Reach.CanMine(vanilla 挖掘那一份),别另编
			if (!Reach.CanMine(p, tx, ty))
			{
				DiagLog.Write($"[mine] 够不着 ({tx},{ty}) dir={req.Dir} 人=({ActExecutor.OriginCx(p)},{ActExecutor.OriginCy(p)}) → stop");
				_active = null;
				return;
			}

			// 【够不着就临时把手伸长,别空挥】。vanilla 够不着时根本不破坏方块,而这里原来只管
			// 瞄准+按键,于是挥满 StallMax 帧一格没掉,上层重选又选中同一条边,再挥。死循环
			// (现场:人在(1118,829)对着(1117,831)挥了 600 帧,sentinel 判 H 平线放弃整段)。
			// 【只在这一帧开,同帧关】。LongArm 改的是 static tileRangeX,全局都读得到,
			// 跨帧持有会让整个寻路以为手能伸 30 格。所以用 try/finally 保证挥完就还
			// 【别抢别人的手臂】。捅向导那段(WofPrep)会长期开着 30 格,那时 LongArmBegin 是空操作,
			// 我这儿的 finally 却会把它提前还掉。所以本来就开着就不碰
			bool boosted = !Concessions.LongArmOn && !Reach.CanMine(p, tx, ty);
			if (boosted)
			{
				Concessions.LongArmBegin();
				if (_boostLogAt != (tx, ty))
				{ _boostLogAt = (tx, ty); DiagLog.Write($"[mine] 够不着 ({tx},{ty}) dir={req.Dir} 人=({ActExecutor.OriginCx(p)},{ActExecutor.OriginCy(p)}) → 伸长手臂挖"); }
			}
			try
			{
				Cursor.AimTile(tx, ty);
				p.selectedItem = slot;
				if (p.itemTime == 0)
					p.controlUseItem = true;
			}
			finally { if (boosted) Concessions.LongArmEnd(); }
		}

		// aim for the 2-column shaft center (±2px), not the edge, an edge-snug stop leaves a sub-pixel lip
		// that still supports the 20px body, who never falls and the deeper tiles leave mining reach.
		private static void CenterInShaft(Player p, int leftC, int rightC)
		{
			float mid = (leftC * 16f + (rightC + 1) * 16f - p.width) / 2f;
			if (p.position.X < mid - 2f) p.controlRight = true;
			else if (p.position.X > mid + 2f) p.controlLeft = true;
		}

		private static int FindPickaxeSlot(Player p)
		{
			for (int i = 0; i < 10; i++)
			{
				var item = p.inventory[i];
				if (item != null && !item.IsAir && item.pick > 0)
					return i;
			}
			return -1;
		}
	}
}
