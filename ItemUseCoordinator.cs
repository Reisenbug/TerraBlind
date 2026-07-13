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
		private static int _elapsed;         // ticks since this action started (for the no-progress grace window)
		// NO-PROGRESS window: after this many ticks of swinging, if the target tile still shows zero accumulated
		// mining damage, the tool simply can't dent it (wooden pick on stone, wrong tool). Stop early and report
		// "no_progress" instead of flailing to the full timeout — this is the human "two swings, nope, wrong tool".
		private const int ProgressGrace = 45;

		// the tile the target snapped to (for HTTP reporting); -1,-1 if no snap happened.
		public static int SnappedWx = -1;
		public static int SnappedWy = -1;
		// completion detection for collect actions (chop/mine): "running" while acting, then "removed" (target tile
		// gone = tree fell / ore mined) or "timeout" (duration ran out, tile still there). HTTP reports this so the LLM
		// knows the outcome instead of guessing. "n/a" for non-collect items (place/throw/potion) — nothing to watch.
		public static string Outcome = "idle";
		// when Outcome == "no_progress", why: "blocked" (a tree/chest sits on top, vanilla forbids mining the support —
		// clear the obstruction first, a better pick won't help) or "tool_weak" (pick just can't dent it — upgrade).
		public static string Reason = "";

		public static bool IsActive => _active != null;

		public static void Start(ItemUseRequest r)
		{
			_active = r;
			_ticksLeft = r.DurationTicks > 0 ? r.DurationTicks : int.MaxValue;
			_snapped = false;
			_watchType = -1;
			_elapsed = 0;
			SnappedWx = -1; SnappedWy = -1;
			Outcome = "running";
			Reason = "";
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

			_elapsed++;

			// COMPLETION DETECTION — three-way termination on a collect target (chop/mine):
			//   removed     → target tile gone (tree fell / ore mined): success. Stop.
			//   no_progress → swung past the grace window but the tile shows zero mining damage: the tool can't dent it
			//                 (wooden pick on stone). Stop early instead of flailing to timeout.
			//   timeout     → ran out the swing budget with the tile still there but being chipped: fallback.
			// This is "use axe until the tree is down" plus the human "wrong tool, give up early".
			if (_watchType >= 0)
			{
				var wt = Main.tile[SnappedWx, SnappedWy];
				if (!wt.HasTile || wt.TileType != _watchType)
				{ Outcome = "removed"; _active = null; return; }

				if (_elapsed >= ProgressGrace && TileMineDamage(p, SnappedWx, SnappedWy) <= 0)
				{
					// zero damage after the grace window → distinguish WHY. Vanilla CanKillTile is false when a tree or
					// chest sits on top (can't mine a support out from under it): that's structural, a better pick won't
					// help — clear the obstruction. Otherwise the pick simply isn't strong enough.
					Reason = WorldGen.CanKillTile(SnappedWx, SnappedWy) ? "tool_weak" : "blocked";
					Outcome = "no_progress"; _active = null; return;
				}
			}

			// A collect target has an OBSERVABLE result — it ends by removed (gone) or no_progress (can't dent it),
			// never by a swing count. So ignore the tick budget while watching a tile: keep swinging until the world
			// fact changes. The budget only bounds non-collect uses (throw a bomb / drink a potion) that have no
			// result tile to watch — those end at n/a when the budget runs out.
			if (_watchType < 0 && _ticksLeft <= 0)
			{
				if (Outcome == "running") Outcome = "n/a";
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

		// Chop must land on the MAIN TRUNK's lowest cell — cutting a ROOT or BRANCH (the sideways-offset columns) only
		// drops that decoration and its bit of wood; only severing the base of the central trunk fells the whole tree.
		// A tree is one TileType; body parts are distinguished by frameX (22=centre trunk, 44=left, 66=right, 88=branch)
		// and frameY. The LLM's rough coord often snaps to a root/branch column, so: find the nearest trunk tile, if it
		// sits on a root column shift sideways to the centre trunk (vanilla WorldGen.IsTileATreeRoot's offsetToTrunk),
		// then slide down to the base. Cut that and the tree falls.
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
			bool IsTree(int x, int y) => InBounds(x, y) && Main.tile[x, y].HasTile && Main.tile[x, y].TileType == type;
			// THE TRUNK is the ONE column spanning the tree's full height; branches/roots stick out sideways on only
			// part of the height, so they're always SHORTER. Among the columns near the hit, pick the LONGEST run of
			// tree (measured up + down from this row), then cut its lowest cell. Longest = trunk, no matter how thick a
			// branch is. Picture 1=tree: 010/110/010/011/111 — the middle column is the only full-height one.
			int trunkX = bestX, trunkLen = -1;
			for (int sx = -3; sx <= 3; sx++)
			{
				int cx = bestX + sx;
				if (!IsTree(cx, bestY)) continue;
				int up = 0, dn = 0;
				while (IsTree(cx, bestY - up - 1)) up++;
				while (IsTree(cx, bestY + dn + 1)) dn++;
				int len = up + dn + 1;
				if (len > trunkLen) { trunkLen = len; trunkX = cx; }
			}
			bestX = trunkX;
			while (IsTree(bestX, bestY + 1)) bestY++;   // walk down the trunk column to its ground-contact cell

			wx = bestX; wy = bestY;
			return true;
		}

		// Accumulated mining damage on a tile from this player's swings (hitTile buffers it, decaying after 60 ticks
		// of no hits). >0 means the tool is actually chipping the tile; 0 after the grace window means it isn't.
		private static int TileMineDamage(Player p, int x, int y)
		{
			int id = p.hitTile.TryFinding(x, y, 1);   // hitType 1 = TILE
			if (id < 0) return 0;
			return p.hitTile.data[id].damage;
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
