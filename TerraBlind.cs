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
		public static ModKeybind ShowHouseSite;
		public static ModKeybind TestBridge;
		public static ModKeybind TestRoom;

		public override void Load()
		{
			MazeWand.MarkMainThread();   // so field builds can report whether they froze the game thread
			ToggleMazeNav = KeybindLoader.RegisterKeybind(this, "ToggleMazeNav", "J");
			ToggleRecedingNav = KeybindLoader.RegisterKeybind(this, "ToggleRecedingNav", "K");
			ToggleBuildReplay = KeybindLoader.RegisterKeybind(this, "ToggleBuildReplay", "I");
			ToggleBuildRecord = KeybindLoader.RegisterKeybind(this, "ToggleBuildRecord", "U");
			ShowHouseSite = KeybindLoader.RegisterKeybind(this, "ShowHouseSite", "H");
			TestBridge = KeybindLoader.RegisterKeybind(this, "TestBridge", "B");
			TestRoom = KeybindLoader.RegisterKeybind(this, "TestRoom", "N");
			DiagLog.Write("[keybind] ToggleMazeNav=J, ToggleRecedingNav=K, ToggleBuildReplay=I, ToggleBuildRecord=U, ShowHouseSite=H, TestBridge=B, TestRoom=N registered (verify in Settings→Controls)");
		}

		public override void Unload()
		{
			ToggleMazeNav = null;
			ToggleRecedingNav = null;
			ToggleBuildReplay = null;
			ToggleBuildRecord = null;
			ShowHouseSite = null;
			TestBridge = null;
			TestRoom = null;
		}
	}
}
