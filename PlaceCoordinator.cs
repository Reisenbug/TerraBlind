using System;
using Terraria;
using Terraria.ModLoader;

namespace TerraBlind
{
	public class PlaceRequest
	{
		public int Dx;
		public int Dy;
		public int Slot;
		public int RemainingFrames;
	}

	public class PlaceCoordinator : ModSystem
	{
		public static volatile PlaceRequest Active;

		public static bool IsActive => Active != null && Active.RemainingFrames > 0;

		public static void Start(PlaceRequest r)
		{
			Active = r;
		}

		public static void Stop()
		{
			Active = null;
		}

		public static void ApplyControls()
		{
			var r = Active;
			if (r == null) return;
			if (Main.LocalPlayer == null || !Main.LocalPlayer.active) { Active = null; return; }
			if (r.RemainingFrames <= 0) { Active = null; return; }

			var p = Main.LocalPlayer;
			int pcx = (int)Math.Round((p.position.X + p.width / 2f) / 16f);
			int feetY = (int)Math.Ceiling((p.position.Y + p.height) / 16f);
			int tileX = pcx + r.Dx;
			int tileY = feetY + r.Dy;

			float worldX = tileX * 16f + 8f;
			float worldY = tileY * 16f + 8f;
			Main.mouseX = (int)(worldX - Main.screenPosition.X);
			Main.mouseY = (int)(worldY - Main.screenPosition.Y);

			if (r.Slot >= 0 && r.Slot <= 9)
				p.selectedItem = r.Slot;
			p.controlUseItem = true;

			r.RemainingFrames--;
			if (r.RemainingFrames <= 0) Active = null;
		}
	}
}
