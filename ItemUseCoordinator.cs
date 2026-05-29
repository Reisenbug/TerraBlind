using Terraria;
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

		public static bool IsActive => _active != null;

		public static void Start(ItemUseRequest r)
		{
			_active = r;
			_ticksLeft = r.DurationTicks > 0 ? r.DurationTicks : int.MaxValue;
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

			if (_ticksLeft <= 0) { _active = null; return; }
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

			float worldX = req.TargetWx * 16f + 8f;
			float worldY = req.TargetWy * 16f + 8f;
			Main.mouseX = (int)(worldX - Main.screenPosition.X);
			Main.mouseY = (int)(worldY - Main.screenPosition.Y);
			Main.SmartCursorWanted_Mouse = false;
			p.selectedItem = slot;

			if (p.itemTime == 0)
				p.controlUseItem = true;
		}

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
