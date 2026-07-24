using Terraria.ModLoader;

namespace TerraBlind
{
	public class TerraBlind : Mod
	{
		// J toggles rolling maze-nav execution toward MazeWand's point1 (debug driver). Registered here, polled in
		// StateSnapshotPlayer.ProcessTriggers.
		public static ModKeybind ToggleMazeNav;
		public static ModKeybind ToggleRecedingNav;
		public static ModKeybind ToggleBuildReplay;
		public static ModKeybind ToggleBuildRecord;

		public override void Load()
		{
			ToggleMazeNav = KeybindLoader.RegisterKeybind(this, "ToggleMazeNav", "J");
			ToggleRecedingNav = KeybindLoader.RegisterKeybind(this, "ToggleRecedingNav", "K");
			ToggleBuildReplay = KeybindLoader.RegisterKeybind(this, "ToggleBuildReplay", "I");
			ToggleBuildRecord = KeybindLoader.RegisterKeybind(this, "ToggleBuildRecord", "U");
			DiagLog.Write("[keybind] ToggleMazeNav=J, ToggleRecedingNav=K, ToggleBuildReplay=I, ToggleBuildRecord=U registered (verify in Settings→Controls)");
		}

		public override void Unload()
		{
			ToggleMazeNav = null;
			ToggleRecedingNav = null;
			ToggleBuildReplay = null;
			ToggleBuildRecord = null;
		}
	}
}
