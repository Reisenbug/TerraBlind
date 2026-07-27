using System.Text;
using Terraria;
using Terraria.ModLoader;

namespace TerraBlind
{
	// PILLAR — build a PLATFORM column upward, N tall, in the column one cell to the player's RIGHT.
	//
	// Platforms attach to the platform below, so the column grows straight up while the player stays on the ground;
	// the only limit is the arm (~6 cells standing). Past that the player JUMPS — airborne, the arm reaches higher —
	// and places the next cell at its absolute coordinate mid-air. Land, jump again, place the next.
	//
	// This drives useItem and the cursor DIRECTLY, not through ItemUseCoordinator, because the motion here is
	// continuous, not one-cell-at-a-time. Per the intended timing:
	//   - keep pressing use; the CURSOR advances the instant the target cell shows our platform on the map. The tile
	//     lands early in a useItem cycle, so the map — not the animation — is the trigger. (An autoReuse item's
	//     animation may never fall back to 0, so trusting that edge pins the cursor on cell one forever.)
	//   - jumping: each frame test whether the target cell is in reach; if yes, useItem; if no, stop placing and wait
	//     for the next jump. Never hang mid-air waiting.
	public static class PillarUp
	{
		private enum Ph { Idle, StepAside, Fill, JumpRise, Done }
		private static Ph _ph = Ph.Idle;

		private static string _item = "";
		private static int _slot = -1, _tileType = -1;
		private static int _colCx;             // the column being built, fixed for the run
		private static int _want, _placed;
		private static int _wy;                // the cell the cursor is currently on (world Y), decreasing upward
		private static int _baseWy;            // ground level: the lowest cell of the column
		private static int _frames, _phaseFrames;
		private static int _wyStuckFrames;     // frames the cursor cell has NOT advanced (real no-progress)
		private static int _lastWy;
		private static bool _grounded;         // have the feet touched down so the ground row is valid?

		private const int MaxPhaseFrames = 240;   // a single fill/jump phase can't outlast this — structural bound
		// If the cursor cell hasn't advanced for this long, the column simply cannot grow (target unreachable even by
		// jumping, out of stock, blocked). This counter is NEVER reset by landing, so a jump loop can't dodge it —
		// which is exactly what let JumpRise spin forever (each landing zeroed _phaseFrames before it hit the cap).
		private const int NoProgressFrames = 180;

		public static bool IsRunning => _ph == Ph.StepAside || _ph == Ph.Fill || _ph == Ph.JumpRise;
		public static string Outcome = "idle";   // idle running done no_item blocked stuck
		public static string Reason = "";
		public static int Placed => _placed;

		// col < 0 → build in the player's own column (they will step aside to clear it). col >= 0 → build in THAT
		// column exactly (e.g. the last cell of a foundation): the caller decides where, not where the body happens
		// to be. Either way the body must not occupy the column while filling, so StepAside walks clear of it.
		public static bool Start(string itemName, int n, int col, out string why)
		{
			why = "";
			var p = Main.LocalPlayer;
			if (p == null) { why = "no_player"; return false; }
			_slot = PlaceAction.HomeInHotbar(itemName);   // home the item in the hotbar once; use that slot for the whole run
			if (_slot < 0) { why = "no_item"; Outcome = "no_item"; Reason = itemName; return false; }
			var it = p.inventory[_slot];
			_tileType = (it != null && !it.IsAir) ? it.createTile : -1;
			if (_tileType < 0) { why = "not_a_block"; Outcome = "no_item"; Reason = itemName; return false; }

			_item = itemName; _want = n < 1 ? 1 : n; _placed = 0;
			_colCx = col >= 0 ? col : ActExecutor.OriginCx(p);
			_baseWy = ActExecutor.OriginCy(p);         // ground-level cell of the column (the row the player stands on)
			_wy = _baseWy;
			_frames = 0; _phaseFrames = 0;
			_lastWy = int.MinValue; _wyStuckFrames = 0; _grounded = false;
			Outcome = "running"; Reason = "";
			DiagLog.Write($"[pillar] start {itemName} n={_want} slot={_slot} col={_colCx} base_y={_baseWy}");
			// col given → the caller already anchored the player; do NOT move. Only step aside when building the
			// player's own column (col < 0), where the body sits in the column and must clear it.
			_ph = col >= 0 ? Ph.Fill : Ph.StepAside;
			return true;
		}

		public static void Stop()
		{
			if (Outcome == "running") Outcome = "stopped";
			_ph = Ph.Idle;
		}

		private static bool Filled(int y) =>
			InBounds(_colCx, y) && Main.tile[_colCx, y].HasTile && Main.tile[_colCx, y].TileType == _tileType;

		// aim the cursor at the current target cell and hold the item selected.
		private static void Aim(Player p)
		{
			p.selectedItem = _slot <= 9 ? _slot : p.selectedItem;
			Main.mouseX = (int)(_colCx * 16f + 8f - Main.screenPosition.X);
			Main.mouseY = (int)(_wy * 16f + 8f - Main.screenPosition.Y);
			Main.SmartCursorWanted_Mouse = false;

			// DEBUG: paint the cell the cursor is swinging at, so it's visible on screen exactly where we're aiming.
			// Green once the cell holds our platform, yellow while we're still trying to place it.
			var col = Filled(_wy) ? new Microsoft.Xna.Framework.Color(0, 255, 0, 200)
								  : new Microsoft.Xna.Framework.Color(255, 220, 0, 200);
			PathVisSystem.SetTiles(new System.Collections.Generic.List<(int, int, Microsoft.Xna.Framework.Color)>
				{ (_colCx, _wy, col) }, ttlFrames: 6);
		}

		// advance to the next cell up; returns false when the whole pillar is done.
		private static bool Advance()
		{
			_placed++;
			if (_placed >= _want) { Finish("done"); return false; }
			_wy--;
			_phaseFrames = 0;
			return true;
		}

		public static void Tick()
		{
			if (!IsRunning) return;
			var p = Main.LocalPlayer;
			if (p == null || !p.active) { Reason = "no_player"; Finish("stuck"); return; }
			_frames++; _phaseFrames++;

			// LAND FIRST. A previous pillar ends mid-air (its last block is jump-placed, player still falling), and if
			// the next pillar grabs the ground row NOW it grabs a mid-air row and builds from the sky. So wait until
			// the feet are on the ground, THEN take the ground row as the column's first cell.
			if (!_grounded)
			{
				if (p.velocity.Y != 0f) { if (_phaseFrames > MaxPhaseFrames) { Reason = "no_landing"; Finish("stuck"); return; } return; }
				_grounded = true;
				_wy = ActExecutor.OriginCy(p);
				_baseWy = _wy;
			}

			// GLOBAL no-progress guard, immune to the per-jump reset. If the cursor cell (_wy) hasn't changed for
			// NoProgressFrames, the pillar cannot advance — bail instead of jumping forever.
			if (_wy != _lastWy) { _lastWy = _wy; _wyStuckFrames = 0; }
			else if (++_wyStuckFrames > NoProgressFrames) { Reason = "unreachable"; Finish("stuck"); return; }

			// (item already homed in the hotbar at Start — _slot is a stable 0-9 slot, no per-frame swapping.)

			// STEP ASIDE — stand just LEFT of the target column, body clear of it, close enough to reach it. The body
			// must not overlap the column (block/platform can't go inside the hitbox), and must be adjacent so the
			// ground cell is in reach. Walk toward that stance: right if we're left of it, left if we overlap.
			if (_ph == Ph.StepAside)
			{
				int bodyL = (int)(p.position.X / 16f);
				int bodyR = (int)((p.position.X + p.width - 1) / 16f);
				if (_phaseFrames > MaxPhaseFrames) { Reason = "cant_position"; Finish("stuck"); return; }

				if (bodyR >= _colCx)         // body overlaps or is right of the column → step left off it
				{
					p.controlLeft = true;
					return;
				}
				// body is entirely left of the column. Close the gap so the ground cell is reachable, then fill.
				if (!p.IsInTileInteractionRange(_colCx, _baseWy, Terraria.DataStructures.TileReachCheckSettings.Simple)
					&& bodyR < _colCx - 1)
				{
					p.controlRight = true;
					return;
				}
				_ph = Ph.Fill; _phaseFrames = 0;
				return;
			}

			bool inReach = p.IsInTileInteractionRange(_colCx, _wy, Terraria.DataStructures.TileReachCheckSettings.Simple);

			if (_ph == Ph.Fill)
			{
				// standing reach ran out → the rest is reached by jumping.
				if (!inReach) { _ph = Ph.JumpRise; _phaseFrames = 0; return; }
				if (_phaseFrames > MaxPhaseFrames) { Reason = "fill_stall"; Finish("stuck"); return; }

				Aim(p);
				// CONTINUOUS placement: keep pressing use, and advance the cursor THE MOMENT the tile appears on the
				// map. The tile lands early in the useItem cycle, so waiting for the cycle to finish is both needless
				// and unreliable — an autoReuse item's animation may never fall back to 0, which is exactly what pinned
				// the cursor on the first cell forever. The map is the trigger; the animation is not consulted.
				if (Filled(_wy)) { if (!Advance()) return; }
				if (p.itemTime == 0) p.controlUseItem = true;
				return;
			}

			// JUMP RISE — hold jump. Each frame: is the target in reach yet? If so, place (continuous, same as fill).
			// If a whole jump goes by without reaching it, land and jump again — never hang in the air.
			p.controlJump = true;
			if (_phaseFrames > MaxPhaseFrames) { Reason = "jump_unreached"; Finish("stuck"); return; }

			if (inReach)
			{
				Aim(p);
				if (Filled(_wy)) { if (!Advance()) return; }
				if (p.itemTime == 0) p.controlUseItem = true;
				return;
			}

			// not in reach this frame. If we've landed again (a full jump elapsed without reaching), start a fresh
			// jump; otherwise keep rising.
			if (p.velocity.Y == 0f && _phaseFrames > 4) { _phaseFrames = 0; }   // grounded → next jump begins
		}

		private static void Finish(string outcome)
		{
			Outcome = outcome;
			_ph = Ph.Done;
			DiagLog.Write($"[pillar] {outcome} placed={_placed}/{_want} reason={Reason}");
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
			  .Append(",\"col\":").Append(_colCx).Append(",\"cursor\":[").Append(_colCx).Append(',').Append(_wy).Append(']');
			if (p != null)
				sb.Append(",\"origin\":[").Append(ActExecutor.OriginCx(p)).Append(',').Append(ActExecutor.OriginCy(p)).Append(']')
				  .Append(",\"on_ground\":").Append(p.velocity.Y == 0f ? "true" : "false")
				  .Append(",\"vel_y\":").Append(p.velocity.Y.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
			sb.Append('}');
			return sb.ToString();
		}

		private static bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Main.maxTilesX && y < Main.maxTilesY;
	}
}
