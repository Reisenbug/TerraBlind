using System.Text;
using Terraria;
using Terraria.ModLoader;

namespace TerraBlind
{
	// BRIDGE — lay a horizontal platform run N cells long, in a given direction, from where the player stands.
	//
	// Same two-phase shape as RopeLadder, for the same reason: the arm runs out before the run does.
	//   PLACE — stand still, step the cursor outward one cell at a time, place into every cell the arm reaches.
	//   WALK  — walk out onto the far end of what was just laid, then place again.
	//
	// Walking only ever happens on ground that is ALREADY laid, so there is no speed to match and nothing to fall
	// through: the two phases never overlap. (A human paves while walking and therefore has to meter their speed
	// against their placement rate — a program moves the cursor a cell per frame, so it can simply stop, fill the
	// whole reach, and then walk. The human's timing problem is an artefact of the human's hands.)
	//
	// Both phases end on an observed fact — the tile is there / the origin cell actually moved — so the result holds
	// at any movement speed, with or without boots, wings or honey.
	public static class BridgeBuilder
	{
		private enum Ph { Idle, Place, Walk, Done }
		private static Ph _ph = Ph.Idle;

		private static string _item = "";
		private static int _slot = -1;
		private static int _dir = 1;           // +1 right, -1 left
		private static int _want, _placed, _already;
		private static int _targetWx, _rowWy;  // cell being placed into; the row the bridge runs along
		private static int _walkToCx;
		private static int _lastOriginCx, _walkStall;
		private static bool _swingIssued;

		private const int WalkStallLimit = 120;

		public static bool IsRunning => _ph == Ph.Place || _ph == Ph.Walk;
		public static string Outcome = "idle";   // idle running done no_item blocked stuck
		public static string Reason = "";
		public static int Placed => _placed;

		public static bool Start(string itemName, string dir, int n, out string why)
		{
			why = "";
			var p = Main.LocalPlayer;
			if (p == null) { why = "no_player"; return false; }
			_slot = PlaceAction.HomeInHotbar(itemName);   // home the item in the hotbar once; use that slot for the whole run
			if (_slot < 0) { why = "no_item"; Outcome = "no_item"; Reason = itemName; return false; }

			_item = itemName; _dir = dir == "left" ? -1 : 1;
			_want = n < 1 ? 1 : n; _placed = 0; _already = 0;
			_swingIssued = false;
			Outcome = "running"; Reason = "";
			_ph = Ph.Place;

			// The bridge runs along the row the player STANDS ON — one below their own cell — so walking out onto it
			// needs no drop or jump.
			_rowWy = ActExecutor.OriginCy(p) + 1;
			_targetWx = ActExecutor.OriginCx(p) + _dir;
			DiagLog.Write($"[bridge] start {itemName} dir={dir} n={_want} slot={_slot} row={_rowWy} from={_targetWx}");
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
			// vanilla's own reach test decides where the arm ends — it already accounts for whatever is modifying it.
			if (!p.IsInTileInteractionRange(_targetWx, _rowWy, Terraria.DataStructures.TileReachCheckSettings.Simple))
			{
				BeginWalk();
				return;
			}
			// only OUR tile already being there is a reason to skip; grass and other cut-through decorations do not
			// block a placement, and refusing to swing at them would skip a cell the game would have accepted.
			if (IsWanted(_targetWx, _rowWy))
			{
				_already++; _targetWx += _dir;
				if (_placed + _already >= _want) { Finish("done"); return; }
				BeginPlace();
				return;
			}
			ItemUseCoordinator.Start(new ItemUseRequest
			{ TargetWx = _targetWx, TargetWy = _rowWy, Slot = _slot, DurationTicks = 0, Strict = false });
			_swingIssued = true;
		}

		private static void BeginWalk()
		{
			var p = Main.LocalPlayer;
			// step out one cell at a time and re-test the reach from there: the arm's true length is whatever vanilla
			// says at the new stance, never a number assumed here.
			_walkToCx = ActExecutor.OriginCx(p) + _dir;
			_lastOriginCx = ActExecutor.OriginCx(p);
			_walkStall = 0;
			_ph = Ph.Walk;
		}

		public static void Tick()
		{
			if (!IsRunning) return;
			var p = Main.LocalPlayer;
			if (p == null || !p.active) { Reason = "no_player"; Finish("stuck"); return; }

			if (_ph == Ph.Place)
			{
				if (ItemUseCoordinator.IsActive) return;
				if (!_swingIssued) return;
				_swingIssued = false;
				string o = ItemUseCoordinator.Outcome;
				// THE MAP IS THE VERDICT: our tile in that cell means it worked.
				if (IsWanted(_targetWx, _rowWy))
				{
					if (o == "already_there") _already++; else _placed++;
					_targetWx += _dir;
					if (_placed + _already >= _want) { Finish("done"); return; }
					BeginPlace();
					return;
				}
				if (o == "no_swing" && ItemUseCoordinator.Reason == "out_of_reach") { BeginWalk(); return; }
				Reason = ItemUseCoordinator.Reason.Length > 0 ? ItemUseCoordinator.Reason : o;
				Finish("blocked");
				return;
			}

			// WALK — onto ground already laid. Held, not tapped: there is nothing to fall through, so speed is free.
			if (_dir > 0) p.controlRight = true; else p.controlLeft = true;

			int cx = ActExecutor.OriginCx(p);
			if (cx != _lastOriginCx) { _lastOriginCx = cx; _walkStall = 0; }
			else if (++_walkStall >= WalkStallLimit)
			{
				Reason = "walk_blocked";
				Finish("stuck");
				return;
			}

			bool arrived = _dir > 0 ? cx >= _walkToCx : cx <= _walkToCx;
			if (arrived)
			{
				_ph = Ph.Place;
				BeginPlace();
			}
		}

		private static void Finish(string outcome)
		{
			Outcome = outcome;
			_ph = Ph.Done;
			DiagLog.Write($"[bridge] {outcome} placed={_placed} already={_already}/{_want} reason={Reason}");
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
