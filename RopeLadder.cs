using System.Text;
using Terraria;
using Terraria.ModLoader;

namespace TerraBlind
{
	// ROPE LADDER — build a rope column upward, N ropes tall, from wherever the player stands.
	//
	// The shape is two strictly alternating phases, and the reason they alternate is the player's arm, not the game:
	//   PLACE — stand still, walk the cursor UP one cell at a time and place a rope in every cell the arm reaches.
	//           A program can move the cursor a whole cell per frame, so there is nothing to gain from shuffling the
	//           body around mid-placement the way a human does — the body only moves once the arm has run out.
	//   CLIMB — hold W. On a rope that IS the climb; the rope already placed is what makes the next stretch reachable.
	//
	// Both phases end on an observed world fact, never on a frame count: placement ends when the tile is there,
	// climbing ends when the origin cell has actually risen. That is what keeps this correct across any mobility
	// (wings, boots, honey, potions) — a fixed-frame replay would silently drift the moment the player moves faster.
	public static class RopeLadder
	{
		// TopUp is the finishing climb: placement stops wherever the arm last reached, which leaves the player several
		// cells BELOW the rope's top and therefore unable to reach the cell above it. Ending the action at the top —
		// and reporting that cell outright — is what lets the next action use the coordinate instead of deriving it.
		private enum Ph { Idle, Place, Climb, TopUp, Done }
		private static Ph _ph = Ph.Idle;

		private static string _item = "";
		private static int _slot = -1;
		private static int _want, _placed, _already;
		private static int _targetWy;          // the cell currently being placed into
		private static int _climbToCy;         // origin cell we must reach before placing again
		private static int _climbFrames;
		private static int _lastOriginCy;      // to notice the climb actually progressing
		private static int _climbStall;
		private static bool _swingIssued;      // we started a swing whose outcome is ours to read
		private static int _topWy = -1;        // highest rope cell of this column (world Y)
		private static int _col;               // 整根绳梯钉在这一列,开工时定,中途不跟着身体走
		private static int _topStall;

		// A climb that stops making progress is stuck (blocked above, fell off, not actually on a rope). Bounded so
		// no path here can hang: this is the structural guarantee, not a heuristic.
		private const int ClimbStallLimit = 120;

		public static bool IsRunning => _ph == Ph.Place || _ph == Ph.Climb || _ph == Ph.TopUp;
		public static string Outcome = "idle";   // idle running done no_item blocked stuck
		public static string Reason = "";
		public static int Placed => _placed;

		public static bool Start(string itemName, int n, out string why)
		{
			why = "";
			var p = Main.LocalPlayer;
			if (p == null) { why = "no_player"; return false; }
			_slot = PlaceAction.HomeInHotbar(itemName);   // home the item in the hotbar once; use that slot for the whole run
			if (_slot < 0) { why = "no_item"; Outcome = "no_item"; Reason = itemName; return false; }

			_item = itemName; _want = n < 1 ? 1 : n; _placed = 0; _already = 0; _topWy = -1;
			_swingIssued = false;
			Outcome = "running"; Reason = "";
			_ph = Ph.Place;
			// FIRST ROPE GOES INTO THE PLAYER'S OWN CELL. A rope may occupy the player's hitbox, and the cell one above
			// the floor IS that cell — aiming a cell higher targets empty air with no rope below it to extend from,
			// which vanilla refuses while the swing animation plays on regardless (looks like working, places nothing).
			_targetWy = ActExecutor.OriginCy(p);
			// 列钉死在开工那一刻 —— BeginPlace 以前每次重读 OriginCx,爬的过程里身体飘一格,
			// 后面的绳就串到隔壁列去了。
			_col = ActExecutor.OriginCx(p);
			DiagLog.Write($"[rope] start {itemName} n={_want} slot={_slot} origin=({_col},{ActExecutor.OriginCy(p)})");
			BeginPlace();
			return true;
		}

		public static void Stop()
		{
			if (Outcome == "running") Outcome = "stopped";
			_ph = Ph.Idle;
			ItemUseCoordinator.Stop();
		}

		private static void BeginPlace()
		{
			var p = Main.LocalPlayer;
			int cx = _col;

			// ARM CHECK against vanilla's own range test — the authority on what the player can touch. Out of reach is
			// not a failure, it is simply the moment to climb: the ropes already placed are the way up.
			if (!p.IsInTileInteractionRange(cx, _targetWy, Terraria.DataStructures.TileReachCheckSettings.Simple))
			{
				BeginClimb();
				return;
			}
			// A ROPE ALREADY THERE is the only reason to skip a swing. Anything else occupying the cell — grass, a
			// vine, any cut-through decoration — does NOT stop a rope going in, and treating "HasTile" as "occupied"
			// is what silently skipped the first rope: the swing never happened, so the cell above had no rope under
			// it to extend from. Never predict whether the game will accept a placement; swing and read the map.
			if (IsRope(cx, _targetWy))
			{
				_already++; _topWy = _targetWy; _targetWy--;
				if (_placed + _already >= _want) { BeginTopUp(); return; }
				BeginPlace();
				return;
			}
			ItemUseCoordinator.Start(new ItemUseRequest
			{ TargetWx = cx, TargetWy = _targetWy, Slot = _slot, DurationTicks = 0, Strict = false });
			_swingIssued = true;
		}

		// climb the finished column until the body is AT its top cell, so the cell above it is within reach.
		private static void BeginTopUp()
		{
			_lastOriginCy = ActExecutor.OriginCy(Main.LocalPlayer);
			_topStall = 0;
			_ph = Ph.TopUp;
		}

		private static void BeginClimb()
		{
			var p = Main.LocalPlayer;
			// climb until the body has risen enough that the next cell is back within arm's reach. One cell at a time
			// keeps the goal honest: each pass re-tests the real reach instead of trusting an assumed arm length.
			_climbToCy = ActExecutor.OriginCy(p) - 1;
			_lastOriginCy = ActExecutor.OriginCy(p);
			_climbFrames = 0; _climbStall = 0;
			_ph = Ph.Climb;
		}

		public static void Tick()
		{
			if (!IsRunning) return;
			var p = Main.LocalPlayer;
			if (p == null || !p.active) { Finish("stuck"); Reason = "no_player"; return; }

			if (_ph == Ph.Place)
			{
				if (ItemUseCoordinator.IsActive) return;   // still swinging
				if (!_swingIssued) return;                 // nothing of ours to read yet — don't judge a stale outcome
				_swingIssued = false;
				int cx0 = ActExecutor.OriginCx(p);
				string o = ItemUseCoordinator.Outcome;
				// THE MAP IS THE VERDICT: a rope in that cell means it worked, whatever the coordinator concluded.
				// Rope placement depends on map state in ways not worth re-deriving here — so don't; just look.
				if (IsRope(cx0, _targetWy))
				{
					if (o == "already_there") _already++; else _placed++;
					_topWy = _targetWy;
					_targetWy--;
					if (_placed + _already >= _want) { BeginTopUp(); return; }
					BeginPlace();
					return;
				}
				// out of reach is the CLIMB signal, not an error — the arm ran out, so move the body.
				if (o == "no_swing" && ItemUseCoordinator.Reason == "out_of_reach") { BeginClimb(); return; }
				Reason = ItemUseCoordinator.Reason.Length > 0 ? ItemUseCoordinator.Reason : o;
				Finish("blocked");
				return;
			}

			if (_ph == Ph.TopUp)
			{
				// Climb to the ACTUAL top of the rope — the highest the body can rise. Rope-climbing hitches (a frame
				// or two of no movement mid-climb) fooled the old 6-frame "stopped rising" check into finishing several
				// cells short, which is why the later hop launched too low to reach the platform. Require a longer,
				// unmistakable stall (no rise for 30 frames) so brief hitches don't count as the top.
				p.controlUp = true;
				int tcy = ActExecutor.OriginCy(p);
				if (tcy != _lastOriginCy) { _lastOriginCy = tcy; _topStall = 0; }
				else if (++_topStall >= 30) { Finish("done"); return; }   // sustained no-rise = truly at the top
				return;
			}

			// CLIMB — hold W. On a rope this is the climb; the condition is the origin cell actually rising, so it
			// works the same whether the player is fast, slow, or slowed by honey.
			p.controlUp = true;
			_climbFrames++;

			int cy = ActExecutor.OriginCy(p);
			if (cy != _lastOriginCy) { _lastOriginCy = cy; _climbStall = 0; }
			else if (++_climbStall >= ClimbStallLimit)
			{
				Reason = OnRope(p) ? "climb_blocked" : "not_on_rope";
				Finish("stuck");
				return;
			}

			if (cy <= _climbToCy)
			{
				_ph = Ph.Place;
				BeginPlace();
			}
		}

		private static void Finish(string outcome)
		{
			Outcome = outcome;
			_ph = Ph.Done;
			DiagLog.Write($"[rope] {outcome} placed={_placed} already={_already}/{_want} reason={Reason}");
		}

		private static bool IsRope(int x, int y)
		{
			if (!InBounds(x, y)) return false;
			var t = Main.tile[x, y];
			return t.HasTile && Main.tileRope[t.TileType];
		}

		private static bool OnRope(Player p)
		{
			int cx = ActExecutor.OriginCx(p), cy = ActExecutor.OriginCy(p);
			for (int dy = 0; dy <= 1; dy++)
			{
				int y = cy + dy;
				if (!InBounds(cx, y)) continue;
				var t = Main.tile[cx, y];
				if (t.HasTile && Main.tileRope[t.TileType]) return true;
			}
			return false;
		}

		public static string StatusJson()
		{
			var p = Main.LocalPlayer;
			var sb = new StringBuilder();
			sb.Append("{\"outcome\":\"").Append(Outcome).Append('"')
			  .Append(",\"running\":").Append(IsRunning ? "true" : "false")
			  .Append(",\"phase\":\"").Append(_ph.ToString().ToLowerInvariant()).Append('"')
			  .Append(",\"item\":\"").Append(_item).Append('"')
			  .Append(",\"placed\":").Append(_placed).Append(",\"already_there\":").Append(_already).Append(",\"wanted\":").Append(_want)
			  .Append(",\"reason\":\"").Append(Reason).Append('"')
			  .Append(",\"target_cell\":[").Append(_col).Append(',').Append(_targetWy).Append(']');
			// top/above_top 是后续每一步(墙、地板、家具)的锚点,必须报绳梯真正所在的列。
			// 报 OriginCx 的话身体飘一格,整座房子就盖到绳梯隔壁去了。
			if (_topWy >= 0)
				sb.Append(",\"top\":[").Append(_col).Append(',').Append(_topWy).Append(']')
				  .Append(",\"above_top\":[").Append(_col).Append(',').Append(_topWy - 1).Append(']');
			if (p != null)
				sb.Append(",\"origin\":[").Append(ActExecutor.OriginCx(p)).Append(',').Append(ActExecutor.OriginCy(p)).Append(']')
				  .Append(",\"on_rope\":").Append(OnRope(p) ? "true" : "false")
				  .Append(",\"vel_y\":").Append(p.velocity.Y.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
			sb.Append('}');
			return sb.ToString();
		}

		private static bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Main.maxTilesX && y < Main.maxTilesY;
	}
}
