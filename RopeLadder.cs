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
		private enum Ph { Idle, Place, Climb, Done }
		private static Ph _ph = Ph.Idle;

		private static string _item = "";
		private static int _slot = -1;
		private static int _want, _placed;
		private static int _targetWy;          // the cell currently being placed into
		private static int _climbToCy;         // origin cell we must reach before placing again
		private static int _climbFrames;
		private static int _lastOriginCy;      // to notice the climb actually progressing
		private static int _climbStall;
		private static bool _swingIssued;      // we started a swing whose outcome is ours to read

		// A climb that stops making progress is stuck (blocked above, fell off, not actually on a rope). Bounded so
		// no path here can hang: this is the structural guarantee, not a heuristic.
		private const int ClimbStallLimit = 120;

		public static bool IsRunning => _ph == Ph.Place || _ph == Ph.Climb;
		public static string Outcome = "idle";   // idle running done no_item blocked stuck
		public static string Reason = "";
		public static int Placed => _placed;

		public static bool Start(string itemName, int n, out string why)
		{
			why = "";
			var p = Main.LocalPlayer;
			if (p == null) { why = "no_player"; return false; }
			_slot = PlaceAction.FindSlotByName(itemName);
			if (_slot < 0) { why = "no_item"; Outcome = "no_item"; Reason = itemName; return false; }

			_item = itemName; _want = n < 1 ? 1 : n; _placed = 0;
			_swingIssued = false;
			Outcome = "running"; Reason = "";
			_ph = Ph.Place;
			// FIRST ROPE GOES INTO THE PLAYER'S OWN CELL. A rope may occupy the player's hitbox, and the cell one above
			// the floor IS that cell — aiming a cell higher targets empty air with no rope below it to extend from,
			// which vanilla refuses while the swing animation plays on regardless (looks like working, places nothing).
			_targetWy = ActExecutor.OriginCy(p);
			DiagLog.Write($"[rope] start {itemName} n={_want} slot={_slot} origin=({ActExecutor.OriginCx(p)},{ActExecutor.OriginCy(p)})");
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
			int cx = ActExecutor.OriginCx(p);

			// ARM CHECK against vanilla's own range test — the authority on what the player can touch. Out of reach is
			// not a failure, it is simply the moment to climb: the ropes already placed are the way up.
			if (!p.IsInTileInteractionRange(cx, _targetWy, Terraria.DataStructures.TileReachCheckSettings.Simple))
			{
				BeginClimb();
				return;
			}
			// already a rope there (previous run, or the world had one): count it and move up without swinging.
			if (InBounds(cx, _targetWy) && Main.tile[cx, _targetWy].HasTile)
			{
				_placed++; _targetWy--;
				if (_placed >= _want) { Finish("done"); return; }
				BeginPlace();
				return;
			}
			ItemUseCoordinator.Start(new ItemUseRequest
			{ TargetWx = cx, TargetWy = _targetWy, Slot = _slot, DurationTicks = 0, Strict = false });
			_swingIssued = true;
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
				string o = ItemUseCoordinator.Outcome;
				if (o == "placed")
				{
					_placed++; _targetWy--;
					if (_placed >= _want) { Finish("done"); return; }
					BeginPlace();
					return;
				}
				// out of reach is the CLIMB signal, not an error — the arm ran out, so move the body.
				if (o == "no_swing" && ItemUseCoordinator.Reason == "out_of_reach") { BeginClimb(); return; }
				Reason = ItemUseCoordinator.Reason.Length > 0 ? ItemUseCoordinator.Reason : o;
				Finish("blocked");
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
			DiagLog.Write($"[rope] {outcome} placed={_placed}/{_want} reason={Reason}");
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
			  .Append(",\"placed\":").Append(_placed).Append(",\"wanted\":").Append(_want)
			  .Append(",\"reason\":\"").Append(Reason).Append('"')
			  .Append(",\"target_cell\":[").Append(p != null ? ActExecutor.OriginCx(p) : -1).Append(',').Append(_targetWy).Append(']');
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
