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
		private static bool _running;

		// PER-CELL OUTCOMES. The counters are kept apart on purpose: "we placed it" and "it was already there" are
		// different facts about the world, and one number that means either is a number a caller can act wrongly on.
		// `_cells` records what happened at each coordinate so a caller can also catch aiming at the wrong cell —
		// a mistake no summary count can ever reveal.
		private struct Res { public int Wx, Wy; public string What; public int Type; }
		private static readonly List<Res> _cells = new();
		private static int _placedCount, _alreadyCount, _failedCount;

		public static bool IsRunning => _running;
		public static string Outcome = "idle";     // idle running done partial blocked no_item
		public static string Reason = "";
		public static int PlacedCount => _placedCount;
		private static int _stopWx = -1, _stopWy = -1;

		// THE one item resolver every action goes through. `spec` is either a numeric item id ("965") or a display
		// name ("绳"). Ids are the stable key — language-independent, alias-free — so a caller that knows what it
		// wants should send one; names stay supported because the LLM layer speaks names, not numbers.
		public static int ResolveSlot(string spec)
		{
			if (string.IsNullOrEmpty(spec)) return -1;
			return int.TryParse(spec, out int id) ? FindSlotById(id) : FindSlotByName(spec);
		}

		// Resolve an item by NAME (exact, then contains) anywhere in the inventory — hotbar or backpack, since
		// ItemUseCoordinator swaps a backpack slot up on its own. Returns -1 when the player simply doesn't have it,
		// which is a fact the caller can act on ("我没有绳"), not an internal error.
		public static int FindSlotByName(string name)
		{
			var p = Main.LocalPlayer;
			if (p == null || string.IsNullOrEmpty(name)) return -1;
			// PLACEABLE = makes a tile OR a wall. Walls have createTile == -1 and createWall >= 0, so filtering on
			// createTile alone silently hid every wall — the "no_item" for 木墙 despite 95 in the pack.
			for (int i = 0; i < 58 && i < p.inventory.Length; i++)
			{
				var it = p.inventory[i];
				if (it != null && !it.IsAir && it.Name == name && (it.createTile >= 0 || it.createWall >= 0)) return i;
			}
			for (int i = 0; i < 58 && i < p.inventory.Length; i++)
			{
				var it = p.inventory[i];
				if (it != null && !it.IsAir && (it.createTile >= 0 || it.createWall >= 0) &&
					(it.Name.Contains(name) || name.Contains(it.Name))) return i;
			}
			return -1;
		}

		// Resolve an item by its numeric ID (item.type) — exact, no aliasing, no localization.
		public static int FindSlotById(int id)
		{
			var p = Main.LocalPlayer;
			if (p == null || id < 0) return -1;
			for (int i = 0; i < 58 && i < p.inventory.Length; i++)
			{
				var it = p.inventory[i];
				if (it != null && !it.IsAir && it.type == id) return i;
			}
			return -1;
		}

		// GIVE AN ITEM A PERMANENT HOTBAR HOME. A build action uses one item many times; if it lives in the backpack
		// (slot ≥ 10) and gets swapped up per use, the swaps fight and the backpack slot number goes stale. Instead:
		// swap it ONCE into an empty hotbar slot (else slot 0) and leave it there. Returns the hotbar slot to use for
		// every subsequent use — no more swapping. -1 if not found.
		public static int HomeInHotbar(string spec)
		{
			var p = Main.LocalPlayer;
			if (p == null) return -1;
			return HomeSlot(ResolveSlot(spec));
		}

		public static int HomeSlot(int slot)
		{
			var p = Main.LocalPlayer;
			if (slot < 0) return -1;
			if (slot <= 9) return slot;                    // already in the hotbar — leave it
			int hb = -1;
			for (int i = 0; i < 10; i++)
				if (p.inventory[i] == null || p.inventory[i].IsAir) { hb = i; break; }
			if (hb < 0) hb = 0;                            // no empty slot — displace slot 0
			var tmp = p.inventory[hb]; p.inventory[hb] = p.inventory[slot]; p.inventory[slot] = tmp;
			return hb;
		}

		// Start a placement of `n` cells beginning at the origin-relative offset (dx,dy), advancing by `step` each
		// time. n=1 with no step is the ordinary single placement.
		// absolute=true → (dx,dy) IS the world cell, no arithmetic on the caller's part. Making a caller convert a cell
		// it already knows into an offset from a moving origin is handing it a subtraction to get wrong.
		public static bool Start(string itemName, int dx, int dy, int n, int stepDx, int stepDy, bool absolute, out string why)
		{
			why = "";
			var p = Main.LocalPlayer;
			if (p == null) { why = "no_player"; return false; }

			_slot = HomeInHotbar(itemName);   // home once in the hotbar; every cell in a run uses that stable slot
			if (_slot < 0)
			{
				why = "no_item"; Outcome = "no_item"; Reason = itemName;
				return false;
			}

			int ox = absolute ? 0 : ActExecutor.OriginCx(p);
			int oy = absolute ? 0 : ActExecutor.OriginCy(p);
			_queue.Clear();
			if (n < 1) n = 1;
			for (int k = 0; k < n; k++)
				_queue.Add(new Cell { Wx = ox + dx + stepDx * k, Wy = oy + dy + stepDy * k });

			_qi = 0; _placedCount = 0; _alreadyCount = 0; _failedCount = 0;
			_cells.Clear();
			_running = true;
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
			// Only skip when the cell ALREADY holds the very tile this item makes — that is genuinely nothing to do.
			// Anything else in the cell (grass, vines, decorations) does not stop a placement, and refusing to swing
			// at it would skip a placement the game allows. Swing, then let the map say what happened.
			int wantTile = WantTileType();
			if (InBounds(c.Wx, c.Wy) && Main.tile[c.Wx, c.Wy].HasTile
				&& wantTile >= 0 && Main.tile[c.Wx, c.Wy].TileType == wantTile)
			{
				Record(c.Wx, c.Wy, "already_there", Main.tile[c.Wx, c.Wy].TileType);
				_alreadyCount++;
				NextCell();
				return;
			}
			ItemUseCoordinator.Start(new ItemUseRequest
			{ TargetWx = c.Wx, TargetWy = c.Wy, Slot = _slot, DurationTicks = 0, Strict = false });
		}

		// the tile type the held item creates — what "placed" must mean for this run.
		private static int WantTileType()
		{
			var p = Main.LocalPlayer;
			if (p == null || _slot < 0 || _slot >= p.inventory.Length) return -1;
			var it = p.inventory[_slot];
			return (it != null && !it.IsAir) ? it.createTile : -1;
		}

		private static void Record(int wx, int wy, string what, int type)
			=> _cells.Add(new Res { Wx = wx, Wy = wy, What = what, Type = type });

		private static void NextCell()
		{
			_qi++;
			if (_qi >= _queue.Count) { Finish(); return; }
			BeginCell();
		}

		// `done` only when every cell was actually placed by us. Anything else is `partial` — a word that cannot be
		// mistaken for success at a glance.
		private static void Finish()
		{
			Outcome = _placedCount == _queue.Count ? "done" : "partial";
			_running = false;
		}

		// drives the queue: one cell at a time, each ending on the world fact that the tile appeared (or the observed
		// reason it did not). A cell that cannot be placed stops the run and reports where — continuing past it would
		// build something different from what was asked for, silently.
		public static void Tick()
		{
			if (!_running) return;
			if (ItemUseCoordinator.IsActive) return;

			var c = _queue[_qi];
			string o = ItemUseCoordinator.Outcome;
			if (o == "placed")
			{
				Record(c.Wx, c.Wy, "placed", InBounds(c.Wx, c.Wy) ? Main.tile[c.Wx, c.Wy].TileType : -1);
				_placedCount++; NextCell(); return;
			}
			if (o == "already_there")
			{
				Record(c.Wx, c.Wy, "already_there", InBounds(c.Wx, c.Wy) ? Main.tile[c.Wx, c.Wy].TileType : -1);
				_alreadyCount++; NextCell(); return;
			}

			_stopWx = c.Wx; _stopWy = c.Wy;
			Reason = ItemUseCoordinator.Reason.Length > 0 ? ItemUseCoordinator.Reason : o;
			Record(c.Wx, c.Wy, Reason, -1);
			_failedCount++;
			Outcome = "blocked";
			_running = false;
			DiagLog.Write($"[place] stopped at ({c.Wx},{c.Wy}) placed={_placedCount} already={_alreadyCount} — {Reason}");
		}

		public static string StatusJson()
		{
			var p = Main.LocalPlayer;
			var sb = new StringBuilder();
			sb.Append("{\"outcome\":\"").Append(Outcome).Append('"')
			  .Append(",\"running\":").Append(_running ? "true" : "false")
			  .Append(",\"item\":\"").Append(JsonEsc(_itemName)).Append('"')
			  .Append(",\"placed\":").Append(_placedCount)
			  .Append(",\"already_there\":").Append(_alreadyCount)
			  .Append(",\"failed\":").Append(_failedCount)
			  .Append(",\"wanted\":").Append(_queue.Count)
			  .Append(",\"reason\":\"").Append(JsonEsc(Reason)).Append('"');
			// every cell we touched and what actually happened there — this is what makes a mis-aimed coordinate
			// visible, which no summary count can do.
			sb.Append(",\"cells\":[");
			for (int i = 0; i < _cells.Count; i++)
			{
				if (i > 0) sb.Append(',');
				sb.Append("{\"at\":[").Append(_cells[i].Wx).Append(',').Append(_cells[i].Wy)
				  .Append("],\"result\":\"").Append(JsonEsc(_cells[i].What)).Append('"');
				if (_cells[i].Type >= 0) sb.Append(",\"type\":").Append(_cells[i].Type);
				sb.Append('}');
			}
			sb.Append(']');
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
