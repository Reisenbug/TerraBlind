using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraBlind
{
	public class ItemUseRequest
	{
		public int TargetWx;
		public int TargetWy;
		public int Slot;          // -1 = keep current selection
		public int DurationTicks; // 0 = run until Stop()
	}

	public class ItemUseCoordinator : ModSystem
	{
		private static volatile ItemUseRequest _active;
		private static int _ticksLeft;
		private static bool _snapped;    // has this request already snapped its target this session?
		private static int _watchType = -1;  // TileType of the collect target we're watching; -1 = not watching

		// the tile the target snapped to (for HTTP reporting); -1,-1 if no snap happened.
		public static int SnappedWx = -1;
		public static int SnappedWy = -1;
		// completion detection for collect actions (chop/mine): "running" while acting, then "removed" (target tile
		// gone = tree fell / ore mined) or "timeout" (duration ran out, tile still there). HTTP reports this so the LLM
		// knows the outcome instead of guessing. "n/a" for non-collect items (place/throw/potion) — nothing to watch.
		public static string Outcome = "idle";

		public static bool IsActive => _active != null;

		public static void Start(ItemUseRequest r)
		{
			_active = r;
			_ticksLeft = r.DurationTicks > 0 ? r.DurationTicks : int.MaxValue;
			_snapped = false;
			_watchType = -1;
			SnappedWx = -1; SnappedWy = -1;
			Outcome = "running";
		}

		public static void Stop()
		{
			if (Outcome == "running") Outcome = "stopped";
			_active = null;
		}

		public static void ApplyControls()
		{
			var req = _active;
			if (req == null) return;
			var p = Main.LocalPlayer;
			if (p == null || !p.active) { _active = null; return; }

			// COMPLETION DETECTION: once snapped onto a collect target, watch that tile. When it goes empty (tree fell,
			// ore mined) the action is done — stop early. This is "use axe until the tree is down": the terminating
			// condition is the game fact, not a fixed swing count.
			if (_watchType >= 0)
			{
				var wt = Main.tile[SnappedWx, SnappedWy];
				if (!wt.HasTile || wt.TileType != _watchType)
				{ Outcome = "removed"; _active = null; return; }
			}

			if (_ticksLeft <= 0)
			{
				if (Outcome == "running") Outcome = _watchType >= 0 ? "timeout" : "n/a";
				_active = null;
				return;
			}
			_ticksLeft--;

			int slot = req.Slot;
			if (slot < 0)
			{
				slot = FindAxeSlot(p);
				if (slot < 0)
				{
					Terraria.Main.NewText("[item_use] no axe in hotbar, stopping");
					_active = null;
					return;
				}
			}
			// selectedItem only holds items in the hotbar (0-9). A backpack slot (10-49) can't be held — swap it
			// into a hotbar slot first (prefer an empty one, else slot 0), then use from there.
			if (slot >= 10 && slot < p.inventory.Length)
			{
				int hb = -1;
				for (int i = 0; i < 10; i++)
					if (p.inventory[i] == null || p.inventory[i].IsAir) { hb = i; break; }
				if (hb < 0) hb = 0;   // no empty hotbar slot → displace slot 0 (its item goes to the backpack slot)
				var tmp = p.inventory[hb];
				p.inventory[hb] = p.inventory[slot];
				p.inventory[slot] = tmp;
				slot = hb;
			}

			// SNAP (once per request): a mining/chopping tool must land on an actual workable tile, but the LLM only
			// gives a rough "the tree is around here" coord that usually lands on air/leaves/an adjacent cell. Same idea
			// as vanilla SmartCursor: pull the target to the nearest tile the tool can act on. Non-collecting items
			// (place/throw/potion) are left untouched.
			if (!_snapped)
			{
				_snapped = true;
				var it = p.inventory[slot];
				if (it != null && !it.IsAir && TrySnap(it, ref req.TargetWx, ref req.TargetWy))
				{
					SnappedWx = req.TargetWx; SnappedWy = req.TargetWy;
					var st = Main.tile[SnappedWx, SnappedWy];
					if (st.HasTile) _watchType = st.TileType;   // watch this tile for removal (chop/mine done)
				}
			}

			float worldX = req.TargetWx * 16f + 8f;
			float worldY = req.TargetWy * 16f + 8f;
			Main.mouseX = (int)(worldX - Main.screenPosition.X);
			Main.mouseY = (int)(worldY - Main.screenPosition.Y);
			Main.SmartCursorWanted_Mouse = false;
			p.selectedItem = slot;

			if (p.itemTime == 0)
				p.controlUseItem = true;
		}

		// Snap a rough target to the nearest tile the given tool can actually act on, within a radius.
		// The LLM only knows "the tree is around here" — its coord routinely lands in the canopy or empty air a dozen
		// cells off the trunk, so the radius has to be generous. axe → tree trunk (then slid to the root); hammer →
		// Main.tileHammer; pick → any mineable solid. Returns true and rewrites wx/wy on a match; false leaves them
		// untouched (non-collecting item, already on a valid tile, or nothing in range).
		private const int SnapRadius = 12;
		private static bool TrySnap(Item it, ref int wx, ref int wy)
		{
			if (it.axe > 0) return TrySnapTree(ref wx, ref wy);

			System.Func<int, int, bool> ok;
			if (it.hammer > 0) ok = (x, y) => Main.tile[x, y].HasTile && Main.tileHammer[Main.tile[x, y].TileType];
			else if (it.pick > 0) ok = (x, y) => Main.tile[x, y].HasTile && Main.tileSolid[Main.tile[x, y].TileType] && !Main.tileHammer[Main.tile[x, y].TileType];
			else return false;   // not a collecting tool — don't snap

			if (InBounds(wx, wy) && ok(wx, wy)) return false;   // already on a valid tile, no snap needed

			int bestX = -1, bestY = -1, bestD = int.MaxValue;
			for (int dx = -SnapRadius; dx <= SnapRadius; dx++)
				for (int dy = -SnapRadius; dy <= SnapRadius; dy++)
				{
					int x = wx + dx, y = wy + dy;
					if (!InBounds(x, y) || !ok(x, y)) continue;
					int d = dx * dx + dy * dy;
					if (d < bestD) { bestD = d; bestX = x; bestY = y; }
				}
			if (bestX < 0) return false;
			wx = bestX; wy = bestY;
			return true;
		}

		// Chop must land on the tree's LOWEST trunk cell, not wherever the LLM aimed. Find the nearest trunk tile
		// (TileID.Sets.IsATreeTrunk), then slide straight down while the cell below is still the same trunk — same as
		// vanilla SmartCursorHelper.Step_Axe. This gives the axe an actual trunk cell at the base so the tree falls.
		private static bool TrySnapTree(ref int wx, ref int wy)
		{
			int bestX = -1, bestY = -1, bestD = int.MaxValue;
			for (int dx = -SnapRadius; dx <= SnapRadius; dx++)
				for (int dy = -SnapRadius; dy <= SnapRadius; dy++)
				{
					int x = wx + dx, y = wy + dy;
					if (!InBounds(x, y)) continue;
					var t = Main.tile[x, y];
					if (!t.HasTile || !TileID.Sets.IsATreeTrunk[t.TileType]) continue;
					int d = dx * dx + dy * dy;
					if (d < bestD) { bestD = d; bestX = x; bestY = y; }
				}
			if (bestX < 0) return false;

			int type = Main.tile[bestX, bestY].TileType;
			while (InBounds(bestX, bestY + 1)
				&& Main.tile[bestX, bestY + 1].HasTile
				&& Main.tile[bestX, bestY + 1].TileType == type)
				bestY++;

			wx = bestX; wy = bestY;
			return true;
		}

		private static bool InBounds(int x, int y) =>
			x >= 0 && y >= 0 && x < Main.maxTilesX && y < Main.maxTilesY;

		private static int FindAxeSlot(Player p)
		{
			for (int i = 0; i < 10; i++)
			{
				var item = p.inventory[i];
				if (item != null && !item.IsAir && item.axe > 0)
					return i;
			}
			return -1;
		}
	}
}
