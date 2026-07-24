using System.Collections.Generic;
using System.Text;
using Terraria;
using Terraria.ModLoader;

namespace TerraBlind
{
	// SEMANTIC PLACE — "put this thing in that cell", the way a person would say it. A human placing a block decides
	// two things: WHAT and WHERE. Everything else (which hotbar slot it lives in, when to press, how long to hold,
	// how to tell it worked) is body knowledge, not decision-making — so the caller must not have to supply it.
	//
	// The primitive layer (/act with steps/until/invariant/cursor) still exists underneath as the escape hatch for
	// things this layer does not cover. This layer exists because making the caller hand-assemble a placement out of
	// primitives is how 19 of 20 ropes got skipped: every field it had to guess was a field it could get wrong.
	//
	//   {"item": "绳", "at": [0,-1]}          → one cell, relative to the ORIGIN CELL (the player's own cell)
	//   {"item": "木平台", "at": [0,1], "n": 8, "step": [1,0]}   → a run of 8, one per cell going right
	//
	// The reply says what a person would see: placed how many, where it stopped, and — if it stopped early — the
	// observed reason, never a prediction. Item lookup is by NAME so the caller never touches slot numbers.
	public static class PlaceAction
	{
		public struct Cell { public int Wx, Wy; }

		private static readonly List<Cell> _queue = new();
		private static int _qi;
		private static int _slot = -1;
		private static string _itemName = "";
		private static int _placedCount;
		private static bool _running;

		public static bool IsRunning => _running;
		public static string Outcome = "idle";     // idle running done blocked no_item
		public static string Reason = "";
		public static int PlacedCount => _placedCount;
		private static int _stopWx = -1, _stopWy = -1;

		// Resolve an item by NAME (exact, then contains) anywhere in the inventory — hotbar or backpack, since
		// ItemUseCoordinator swaps a backpack slot up on its own. Returns -1 when the player simply doesn't have it,
		// which is a fact the caller can act on ("我没有绳"), not an internal error.
		public static int FindSlotByName(string name)
		{
			var p = Main.LocalPlayer;
			if (p == null || string.IsNullOrEmpty(name)) return -1;
			for (int i = 0; i < 58 && i < p.inventory.Length; i++)
			{
				var it = p.inventory[i];
				if (it != null && !it.IsAir && it.Name == name && it.createTile >= 0) return i;
			}
			for (int i = 0; i < 58 && i < p.inventory.Length; i++)
			{
				var it = p.inventory[i];
				if (it != null && !it.IsAir && it.createTile >= 0 &&
					(it.Name.Contains(name) || name.Contains(it.Name))) return i;
			}
			return -1;
		}

		// Start a placement of `n` cells beginning at the origin-relative offset (dx,dy), advancing by `step` each
		// time. n=1 with no step is the ordinary single placement.
		public static bool Start(string itemName, int dx, int dy, int n, int stepDx, int stepDy, out string why)
		{
			why = "";
			var p = Main.LocalPlayer;
			if (p == null) { why = "no_player"; return false; }

			_slot = FindSlotByName(itemName);
			if (_slot < 0)
			{
				why = "no_item"; Outcome = "no_item"; Reason = itemName;
				return false;
			}

			int ox = ActExecutor.OriginCx(p), oy = ActExecutor.OriginCy(p);
			_queue.Clear();
			if (n < 1) n = 1;
			for (int k = 0; k < n; k++)
				_queue.Add(new Cell { Wx = ox + dx + stepDx * k, Wy = oy + dy + stepDy * k });

			_qi = 0; _placedCount = 0; _running = true;
			_itemName = itemName;
			_stopWx = -1; _stopWy = -1;
			Outcome = "running"; Reason = "";
			DiagLog.Write($"[place] {itemName} slot={_slot} n={n} from=({ox + dx},{oy + dy}) step=({stepDx},{stepDy})");
			BeginCell();
			return true;
		}

		public static void Stop()
		{
			if (Outcome == "running") Outcome = "stopped";
			_running = false;
			ItemUseCoordinator.Stop();
		}

		private static void BeginCell()
		{
			var c = _queue[_qi];
			// already filled counts as satisfied — placing into an occupied cell is a no-op for a person too.
			if (InBounds(c.Wx, c.Wy) && Main.tile[c.Wx, c.Wy].HasTile) { NextCell(true); return; }
			ItemUseCoordinator.Start(new ItemUseRequest
			{ TargetWx = c.Wx, TargetWy = c.Wy, Slot = _slot, DurationTicks = 0, Strict = false });
		}

		private static void NextCell(bool counted)
		{
			if (counted) _placedCount++;
			_qi++;
			if (_qi >= _queue.Count) { Outcome = "done"; _running = false; return; }
			BeginCell();
		}

		// drives the queue: one cell at a time, each ending on the world fact that the tile appeared (or the observed
		// reason it did not). A cell that cannot be placed stops the run and reports where — continuing past it would
		// build something different from what was asked for, silently.
		public static void Tick()
		{
			if (!_running) return;
			if (ItemUseCoordinator.IsActive) return;

			string o = ItemUseCoordinator.Outcome;
			if (o == "placed") { NextCell(true); return; }

			var c = _queue[_qi];
			_stopWx = c.Wx; _stopWy = c.Wy;
			Outcome = "blocked";
			Reason = ItemUseCoordinator.Reason.Length > 0 ? ItemUseCoordinator.Reason : o;
			_running = false;
			DiagLog.Write($"[place] stopped at ({c.Wx},{c.Wy}) after {_placedCount} — {Outcome}/{Reason}");
		}

		public static string StatusJson()
		{
			var p = Main.LocalPlayer;
			var sb = new StringBuilder();
			sb.Append("{\"outcome\":\"").Append(Outcome).Append('"')
			  .Append(",\"running\":").Append(_running ? "true" : "false")
			  .Append(",\"item\":\"").Append(JsonEsc(_itemName)).Append('"')
			  .Append(",\"placed\":").Append(_placedCount)
			  .Append(",\"wanted\":").Append(_queue.Count)
			  .Append(",\"reason\":\"").Append(JsonEsc(Reason)).Append('"');
			if (_stopWx >= 0)
			{
				sb.Append(",\"stopped_at\":[").Append(_stopWx).Append(',').Append(_stopWy).Append(']');
				if (InBounds(_stopWx, _stopWy))
				{
					var t = Main.tile[_stopWx, _stopWy];
					sb.Append(",\"stopped_cell\":{\"has_tile\":").Append(t.HasTile ? "true" : "false")
					  .Append(",\"type\":").Append(t.HasTile ? t.TileType : -1)
					  .Append(",\"in_reach\":").Append(p != null && p.IsInTileInteractionRange(_stopWx, _stopWy,
						  Terraria.DataStructures.TileReachCheckSettings.Simple) ? "true" : "false").Append('}');
				}
			}
			if (p != null)
				sb.Append(",\"origin\":[").Append(ActExecutor.OriginCx(p)).Append(',').Append(ActExecutor.OriginCy(p)).Append(']');
			sb.Append('}');
			return sb.ToString();
		}

		private static bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Main.maxTilesX && y < Main.maxTilesY;
		private static string JsonEsc(string s) =>
			string.IsNullOrEmpty(s) ? "" : s.Replace("\\", "\\\\").Replace("\"", "\\\"");
	}
}
