using Terraria.ModLoader;

namespace TerraBlind
{
	public class TerraBlind : Mod
	{
		// J toggles rolling maze-nav execution toward MazeWand's point1 (debug driver). Registered here, polled in
		// StateSnapshotPlayer.ProcessTriggers.
		public static ModKeybind ToggleMazeNav;
		public static ModKeybind ToggleRecedingNav;
		public static ModKeybind ShowHellLine;
		public static ModKeybind PreviewDescent;
		public static ModKeybind ShowHouseSite;
		public static ModKeybind TestBridge;
		public static ModKeybind TestRoom;
		public static ModKeybind TestPillar;
		public static ModKeybind TestPlatDown;
		public static ModKeybind BuildHellBridge;
		public static ModKeybind TestReachWork;
		public static ModKeybind ShowMinima;

		public override void Load()
		{
			MazeWand.MarkMainThread();   // so field builds can report whether they froze the game thread
			Concessions.DropAnchorRequirements();
			ToggleMazeNav = KeybindLoader.RegisterKeybind(this, "ToggleMazeNav", "J");
			ToggleRecedingNav = KeybindLoader.RegisterKeybind(this, "ToggleRecedingNav", "K");
			ShowHellLine = KeybindLoader.RegisterKeybind(this, "ShowHellLine", "U");
			PreviewDescent = KeybindLoader.RegisterKeybind(this, "PreviewDescent", "I");
			ShowHouseSite = KeybindLoader.RegisterKeybind(this, "ShowHouseSite", "H");
			TestBridge = KeybindLoader.RegisterKeybind(this, "TestBridge", "B");
			TestRoom = KeybindLoader.RegisterKeybind(this, "TestRoom", "N");
			TestPillar = KeybindLoader.RegisterKeybind(this, "TestPillar", "P");
			TestPlatDown = KeybindLoader.RegisterKeybind(this, "TestPlatDown", "O");
			BuildHellBridge = KeybindLoader.RegisterKeybind(this, "BuildHellBridge", "L");
			TestReachWork = KeybindLoader.RegisterKeybind(this, "TestReachWork", "OemOpenBrackets");
			ShowMinima = KeybindLoader.RegisterKeybind(this, "ShowMinima", "M");
			DiagLog.Write("[keybind] ToggleMazeNav=J, ToggleRecedingNav=K, ShowHellLine=U, PreviewDescent=I, ShowHouseSite=H, TestBridge=B, TestRoom=N, TestPillar=P, TestReachWork=[, ShowMinima=M registered (verify in Settings→Controls)");
		}

		public override void Unload()
		{
			ToggleMazeNav = null;
			ToggleRecedingNav = null;
			ShowHellLine = null;
			PreviewDescent = null;
			ShowHouseSite = null;
			TestBridge = null;
			TestRoom = null;
			TestPillar = null;
			TestPlatDown = null;
			BuildHellBridge = null;
			TestReachWork = null;
			ShowMinima = null;
		}
	}
}
