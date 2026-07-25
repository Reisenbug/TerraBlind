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
		public int DurationTicks; // 0 = run until Stop(). IGNORED for mining and placing — both end on a world fact,
		                          // not a swing budget; only bounds uses with nothing observable (potion/bomb/summon).
		public bool Strict;       // exact-coord caller: never snap to a different tile; target gone = report, don't hunt
	}

	public class ItemUseCoordinator : ModSystem
	{
		private static volatile ItemUseRequest _active;
		private static int _ticksLeft;
		private static bool _snapped;    // has this request already snapped its target this session?
		private static int _watchType = -1;  // TileType of the collect target we're watching; -1 = not watching
		// PLACEMENT has its own eye, mirroring the collect one. Collect watches a tile DISAPPEAR; placement watches
		// the target cell GAIN the tile this item creates (item.createTile — the placement counterpart of pick/axe/
		// hammer). Without this a place action had nothing observable at all and always ended "n/a", which upper
		// layers read as success: 20 ropes could fail silently and every single call reported fine.
		private static int _placeType = -1;  // TileType this item will create; -1 = not a placing item
		private static int _swings;          // COMPLETED swings (itemAnimation falling edge), not frames pressed
		private static int _prevAnim;        // last frame's itemAnimation, for that falling edge
		private static bool _preHadTile;     // something (not ours) occupied the target before we swung
		// How many full swings a placement gets before we call it refused. One is enough when it works — the extra
		// two absorb a swing eaten by a stance change or an item swap.
		private const int PlaceSwingGrace = 3;
		// hard ceiling in frames for one placement attempt, so the attempt ends even if no swing ever completes.
		private const int PlaceFrameCeiling = 90;
		private static int _elapsed;         // ticks since this action started (for the no-progress grace window)
		// NO-PROGRESS window: after this many ticks of swinging, if the target tile still shows zero accumulated
		// mining damage, the tool simply can't dent it (wooden pick on stone, wrong tool). Stop early and report
		// "no_progress" instead of flailing to the full timeout — this is the human "two swings, nope, wrong tool".
		private const int ProgressGrace = 45;

		// the tile the target snapped to (for HTTP reporting); -1,-1 if no snap happened.
		public static int SnappedWx = -1;
		public static int SnappedWy = -1;
		// completion detection. Collect (chop/mine): "removed" (target gone) / "no_progress" (can't dent) / "timeout".
		// Place (block/platform/rope): "placed" (WE put a tile in an empty cell) / "already_there" (the cell was
		// occupied before we swung — we did nothing) / "not_placed" (swung but nothing landed — see Reason) /
		// "no_swing" (never even swung — target unreachable / wrong item / out of stock).
		// "n/a" is now RESERVED for items that are genuinely neither (potion/bomb): no tile to watch either way.
		public static string Outcome = "idle";
		// why the action failed. collect: "blocked" (support tile, can't mine) / "tool_weak" (pick too weak) /
		// "out_of_reach". place: "occupied" (cell already has a different tile) / "no_anchor" (rope/platform needs a
		// neighbour to attach to) / "out_of_reach" / "wrong_item" / "out_of_stock".
		public static string Reason = "";

		public static bool IsActive => _active != null;

		public static void Start(ItemUseRequest r)
		{
			_active = r;
			_ticksLeft = r.DurationTicks > 0 ? r.DurationTicks : int.MaxValue;
			_snapped = false;
			_watchType = -1;
			_placeType = -1;
			_swings = 0;
			_prevAnim = 0;
			_preHadTile = false;
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

			// PLACE completion — the mirror of the collect check above: the target cell now holds the tile this item
			// creates, so the placement landed. Checked before the budget runs out so a successful place ends at once.
			if (_placeType >= 0)
			{
				var pt = Main.tile[req.TargetWx, req.TargetWy];
				if (pt.HasTile && pt.TileType == _placeType)
				{ Outcome = "placed"; _active = null; return; }
			}

			// PLACE gives up only after COMPLETED SWINGS, never on the tick budget. A placement takes item.useTime
			// frames to resolve, which the caller has no way of knowing — judging it by a caller-supplied budget meant
			// dur:2 reported "the game refused" when the truth was "the animation had not finished yet". Swings are
			// counted on the itemAnimation falling edge, which vanilla always drives to 0, so this window is
			// structurally guaranteed to close: no path here can spin forever.
			if (_placeType >= 0)
			{
				// belt-and-braces on the swing count: an item whose animation never returns to 0 (autoReuse held down)
				// would never tick a falling edge, so an elapsed-frame ceiling backs it up. Whichever fires first ends
				// the attempt — the point is only that SOMETHING always does.
				if (_swings >= PlaceSwingGrace || _elapsed >= PlaceFrameCeiling)
				{
					Outcome = "not_placed"; Reason = DiagnosePlace(p, req);
					DiagLog.Write($"[item_use] not_placed at ({req.TargetWx},{req.TargetWy}) swings={_swings} elapsed={_elapsed} reason={Reason}");
					_active = null; return;
				}
			}
			// Uses with NOTHING observable (potion, bomb, summon) are the only ones still bounded by the budget —
			// there is no world fact to wait for, so the swing count is all they have.
			else if (_watchType < 0 && _ticksLeft <= 0)
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
				if (it != null && !it.IsAir)
				{
					if (req.Strict)
					{
						// exact-coord caller (batch mine): never re-aim to a different tile. Target gone = report it,
						// not silently snap onto whatever solid rock happens to be nearby.
						var tt = Main.tile[req.TargetWx, req.TargetWy];
						if (!tt.HasTile) { Outcome = "target_gone"; _active = null; return; }
						SnappedWx = req.TargetWx; SnappedWy = req.TargetWy;
						_watchType = tt.TileType;
					}
					else if (TrySnap(it, ref req.TargetWx, ref req.TargetWy))
					{
						SnappedWx = req.TargetWx; SnappedWy = req.TargetWy;
						var st = Main.tile[SnappedWx, SnappedWy];
						if (st.HasTile) _watchType = st.TileType;   // watch this tile for removal (chop/mine done)
					}
					// PLACING item (createTile >= 0): register the tile we expect to appear at the EXACT target. No snap
					// — a placement coord means the cell you want filled, not "somewhere around here". Registering
					// this is what gives the place path an observable result at all.
					if (_watchType < 0 && it.createTile >= 0)
					{
						_placeType = it.createTile;
						SnappedWx = req.TargetWx; SnappedWy = req.TargetWy;
						// ALREADY-THERE means OUR tile is already in that cell — not merely that something is. Grass, vines
						// and other cut-through decorations sit in cells that accept a placement perfectly well; refusing
						// to swing at them skips a placement the game would have allowed. So only an exact match counts
						// as "nothing to do"; everything else gets swung at, and the MAP decides what happened.
						var pre = Main.tile[req.TargetWx, req.TargetWy];
						if (pre.HasTile && pre.TileType == _placeType)
						{
							Outcome = "already_there"; Reason = pre.TileType.ToString();
							_active = null; return;
						}
						_preHadTile = pre.HasTile;
					}

					// REACH: swinging at a tile outside interaction range just flails (vanilla clamps the tile target),
					// and mining below the feet moves the player, so a batch computed from the old stance goes stale.
					// Check the authoritative range up front and report at once instead of burning the grace window.
					// Placement is bounded by the same reach, so it reports the same way rather than swinging at air.
					if ((_watchType >= 0 || _placeType >= 0)
						&& !p.IsInTileInteractionRange(SnappedWx, SnappedWy, Terraria.DataStructures.TileReachCheckSettings.Simple))
					{
						Outcome = _placeType >= 0 ? "no_swing" : "no_progress";
						Reason = "out_of_reach"; _active = null; return;
					}
				}
			}

			float worldX = req.TargetWx * 16f + 8f;
			float worldY = req.TargetWy * 16f + 8f;
			Main.mouseX = (int)(worldX - Main.screenPosition.X);
			Main.mouseY = (int)(worldY - Main.screenPosition.Y);
			Main.SmartCursorWanted_Mouse = false;
			p.selectedItem = slot;

			// COMPLETED-SWING COUNT on the itemAnimation falling edge. Counting "frames we pressed the button" instead
			// would multiply-count one swing (itemAnimation stays 0 for several frames before vanilla starts it), which
			// is what made a 2-frame budget look like a swing that had already been given its chance.
			if (_prevAnim > 0 && p.itemAnimation == 0) _swings++;
			_prevAnim = p.itemAnimation;

			if (p.itemTime == 0)
				p.controlUseItem = true;
		}

		// WHY a placement produced nothing — but ONLY reasons that are OBSERVED world facts, never a prediction of
		// whether vanilla would accept the tile. The eye reports what happened; it must not pre-judge what CAN happen,
		// or it will refuse legal placements the way it wrongly refused rope-into-air (a rope into an anchorless cell
		// is legal, just pointless — the player is allowed to do pointless things, so the primitive must be too).
		// "no_anchor" survives only as an appended HINT below, never as a gate.
		private static string DiagnosePlace(Player p, ItemUseRequest req)
		{
			int x = req.TargetWx, y = req.TargetWy;
			if (!InBounds(x, y)) return "out_of_bounds";
			var it = p.inventory[p.selectedItem];
			if (it == null || it.IsAir) return "empty_hand";
			if (it.createTile != _placeType) return "wrong_item";
			if (it.stack <= 0) return "out_of_stock";
			if (!p.IsInTileInteractionRange(x, y, Terraria.DataStructures.TileReachCheckSettings.Simple)) return "out_of_reach";
			var t = Main.tile[x, y];
			if (t.HasTile) return "occupied";
			// nothing observed blocked it: the swing landed, the cell is empty, in reach, right item, in stock — yet
			// no tile appeared, so vanilla rejected the placement for its own reason. Report that plainly and hand the
			// LLM a NON-BINDING hint (anchorless neighbours) to reason from, rather than inventing a verdict.
			return HasAnchor(x, y) ? "rejected" : "rejected_no_anchor_hint";
		}

		// A placed tile needs something to attach to. Rope in particular only extends from an existing rope or a
		// ceiling — a mid-air target silently does nothing at all, which is exactly the failure that used to report
		// as success. Main.tileRope covers every rope variant, so no ID list to keep in sync.
		private static bool HasAnchor(int x, int y)
		{
			(int, int)[] n = { (0, -1), (0, 1), (-1, 0), (1, 0) };
			foreach (var (dx, dy) in n)
			{
				int a = x + dx, b = y + dy;
				if (!InBounds(a, b)) continue;
				var t = Main.tile[a, b];
				if (t.HasTile && (Main.tileSolid[t.TileType] || Main.tileSolidTop[t.TileType] || Main.tileRope[t.TileType]))
					return true;
			}
			return false;
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

		// Live view of the watched target for /item_use_status: is there still a tile, can the HELD tool act on it,
		// and how much mining damage has accumulated — rising damage = the swings are landing; flat 0 = flailing.
		public static string TargetJson()
		{
			if (SnappedWx < 0 || !InBounds(SnappedWx, SnappedWy)) return "null";
			var t = Main.tile[SnappedWx, SnappedWy];
			bool has = t.HasTile;
			int type = has ? t.TileType : -1;
			var p = Main.LocalPlayer;
			bool toolOk = false;
			int dmg = 0;
			if (p != null && p.active)
			{
				if (has)
				{
					var it = p.inventory[p.selectedItem];
					if (it != null && !it.IsAir)
						toolOk = it.axe > 0 ? Main.tileAxe[type]
							: it.hammer > 0 ? Main.tileHammer[type]
							: it.pick > 0 && Main.tileSolid[type] && !Main.tileAxe[type] && !Main.tileHammer[type];
					dmg = TileMineDamage(p, SnappedWx, SnappedWy);
				}
			}
			return "{\"has_tile\":" + (has ? "true" : "false") + ",\"type\":" + type
				+ ",\"tool_ok\":" + (toolOk ? "true" : "false") + ",\"damage\":" + dmg + "}";
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
