using System.Collections.Generic;
using System.Text;
using Terraria;
using Terraria.ModLoader;

namespace TerraBlind
{
	// PLACE-WALLS — place background walls at an ORDERED list of cells, strictly in order (one fully done before the
	// next starts). Order matters here: vanilla's wall merge/spread depends on placement order, so the caller's
	// sequence must be honored exactly, not "place whatever's in reach".
	//
	// For each cell: if the player can reach it standing, place; if not, hold jump and place the moment it comes into
	// reach (a taller wall placed from a shorter piece of furniture — e.g. the workbench room — needs a hop). The
	// player does not move between cells; the caller positions them (on the room's table) first. Walls may sit inside
	// the player's hitbox, so no stepping aside.
	public static class PlaceWalls
	{
		private static bool _running;
		private static int _slot = -1, _wallType = -1;
		private static readonly List<(int wx, int wy)> _cells = new();
		private static int _i;                 // current cell index (strict order)
		private static int _placed;
		private static int _cellFrames;        // frames spent trying the current cell

		private const int MaxCellFrames = 240; // per-cell bound: can't reach even by jumping → give up on it

		public static bool IsRunning => _running;
		public static string Outcome = "idle"; // idle running done no_item incomplete
		public static string Reason = "";
		public static int Placed => _placed;

		public static bool Start(string itemName, List<(int, int)> cells, out string why)
		{
			why = "";
			var p = Main.LocalPlayer;
			if (p == null) { why = "no_player"; return false; }
			_slot = PlaceAction.HomeInHotbar(itemName);
			if (_slot < 0) { why = "no_item"; Outcome = "no_item"; Reason = itemName; return false; }
			var it = p.inventory[_slot];
			_wallType = (it != null && !it.IsAir) ? it.createWall : -1;
			if (_wallType < 0) { why = "not_a_wall"; Outcome = "no_item"; Reason = itemName; return false; }

			_cells.Clear(); _cells.AddRange(cells);
			_i = 0; _placed = 0; _cellFrames = 0;
			_running = true;
			Outcome = "running"; Reason = "";
			DiagLog.Write($"[walls] start {itemName} wall={_wallType} slot={_slot} cells={_cells.Count}");
			return true;
		}

		public static void Stop()
		{
			if (Outcome == "running") Outcome = "stopped";
			_running = false;
		}

		// a wall is here when the cell's WallType matches what our item makes. Vanilla spread may fill neighbours with
		// the same wall type, which is fine — "done" for a cell only needs THIS cell to carry the wall.
		private static bool WallHere(int wx, int wy)
		{
			if (!InBounds(wx, wy)) return false;
			return Main.tile[wx, wy].WallType == _wallType;
		}

		public static void Tick()
		{
			if (!_running) return;
			var p = Main.LocalPlayer;
			if (p == null || !p.active) { Reason = "no_player"; Outcome = "incomplete"; _running = false; return; }

			if (_i >= _cells.Count)
			{
				Outcome = _placed >= _cells.Count ? "done" : "incomplete";
				_running = false;
				DiagLog.Write($"[walls] {Outcome} placed={_placed}/{_cells.Count}");
				return;
			}

			var (wx, wy) = _cells[_i];

			// already carries our wall (we placed it, or spread from an earlier cell) → count and advance in order.
			if (WallHere(wx, wy)) { _placed++; _i++; _cellFrames = 0; return; }

			_cellFrames++;
			if (_cellFrames > MaxCellFrames)
			{
				// couldn't get this cell walled even with jumping — skip it (report incomplete at the end) rather than
				// hang. Strict order is preserved: we only move on after genuinely failing this one.
				DiagLog.Write($"[walls] skip cell {_i} ({wx},{wy}) — unreachable");
				_i++; _cellFrames = 0; return;
			}

			bool inReach = Reach.CanPlace(p, wx, wy);
			if (!inReach)
			{
				// too high to reach standing (short furniture / top row) → hop for it. controlDown never — that would
				// drop off the furniture. Just jump and re-test reach each frame.
				p.controlJump = true;
				return;
			}

			// in reach → aim and swing. Placement judged by the map (WallHere) next frame.
			p.selectedItem = _slot;
			Cursor.AimTile(wx, wy);
			if (p.itemTime == 0) p.controlUseItem = true;
		}

		public static string StatusJson()
		{
			var p = Main.LocalPlayer;
			var sb = new StringBuilder();
			sb.Append("{\"outcome\":\"").Append(Outcome).Append('"')
			  .Append(",\"running\":").Append(_running ? "true" : "false")
			  .Append(",\"placed\":").Append(_placed).Append(",\"cells\":").Append(_cells.Count)
			  .Append(",\"i\":").Append(_i).Append(",\"reason\":\"").Append(Reason).Append('"');
			if (_i < _cells.Count) sb.Append(",\"current\":[").Append(_cells[_i].wx).Append(',').Append(_cells[_i].wy).Append(']');
			if (p != null) sb.Append(",\"origin\":[").Append(ActExecutor.OriginCx(p)).Append(',').Append(ActExecutor.OriginCy(p)).Append(']');
			sb.Append('}');
			return sb.ToString();
		}

		private static bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Main.maxTilesX && y < Main.maxTilesY;
	}
}
