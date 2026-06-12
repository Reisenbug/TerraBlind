using System.Collections.Generic;
using Terraria;
using Terraria.ModLoader;

namespace TerraBlind
{
	public class MineRequest
	{
		public List<(int Wx, int Wy)> Tiles;
	}

	public class MineCoordinator : ModSystem
	{
		private static volatile MineRequest _active;
		private static int _idx;

		public static bool IsActive => _active != null;

		public static void Start(MineRequest r)
		{
			_active = r;
			_idx = 0;
		}

		public static void Stop()
		{
			_active = null;
		}

		public static void ApplyControls()
		{
			var req = _active;
			if (req == null) return;
			var p = Main.LocalPlayer;
			if (p == null || !p.active) { _active = null; return; }

			while (_idx < req.Tiles.Count && !Main.tile[req.Tiles[_idx].Wx, req.Tiles[_idx].Wy].HasTile)
				_idx++;

			if (_idx >= req.Tiles.Count) { _active = null; return; }

			var (wx, wy) = req.Tiles[_idx];

			int slot = FindPickaxeSlot(p);
			if (slot < 0) { _active = null; return; }

			int feetY = (int)((p.position.Y + p.height) / 16f);
			if (wy >= feetY)
			{
				// digging below (shaft): the 20px body can straddle 3 columns (2+16+2) and rest on a lip
				// outside the 2-column shaft — micro-step until the body fits inside the shaft's column span
				int minC = int.MaxValue, maxC = int.MinValue;
				foreach (var t in req.Tiles) { if (t.Wx < minC) minC = t.Wx; if (t.Wx > maxC) maxC = t.Wx; }
				float lo = minC * 16f, hi = (maxC + 1) * 16f - p.width;
				if (p.position.X < lo - 0.5f) p.controlRight = true;
				else if (p.position.X > hi + 0.5f) p.controlLeft = true;
			}
			else
			{
				// walk into the tunnel as it opens — deeper columns are outside mining reach from the start cell
				int pcx = (int)(p.Center.X / 16f);
				if (wx > pcx + 1) p.controlRight = true;
				else if (wx < pcx - 1) p.controlLeft = true;
			}

			// Player.Update recomputes tileTargetX/Y from Main.mouseX every frame, so writing tileTarget
			// directly gets overwritten — drive the mouse instead (same as PlaceCoordinator).
			Main.mouseX = (int)(wx * 16f + 8f - Main.screenPosition.X);
			Main.mouseY = (int)(wy * 16f + 8f - Main.screenPosition.Y);
			Main.SmartCursorWanted_Mouse = false;
			p.selectedItem = slot;
			if (p.itemTime == 0)
				p.controlUseItem = true;
		}

		private static int FindPickaxeSlot(Player p)
		{
			for (int i = 0; i < 10; i++)
			{
				var item = p.inventory[i];
				if (item != null && !item.IsAir && item.pick > 0)
					return i;
			}
			return -1;
		}
	}
}
