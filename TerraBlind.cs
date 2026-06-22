using Terraria.ModLoader;

namespace TerraBlind
{
	public class TerraBlind : Mod
	{
		// J toggles rolling maze-nav execution toward MazeWand's point1 (debug driver). Registered here, polled in
		// StateSnapshotPlayer.ProcessTriggers.
		public static ModKeybind ToggleMazeNav;

		public override void Load()
		{
			ToggleMazeNav = KeybindLoader.RegisterKeybind(this, "ToggleMazeNav", "J");
			DiagLog.Write("[keybind] ToggleMazeNav registered (default J — verify it's bound in Settings→Controls)");
		}

		public override void Unload()
		{
			ToggleMazeNav = null;
		}
	}
}
