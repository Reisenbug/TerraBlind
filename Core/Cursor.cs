using Terraria;

namespace TerraBlind
{
	// 鼠标瞄准只有这一份。
	//
	// 原来 24 处各写一遍 `Main.mouseX = (int)(世界X - screenPosition.X)`,其中 9 处
	// 忘了关智能光标。那玩意会把目标悄悄挪到旁边的格子上,放置/挖掘就落在错的地方。
	// 一份代码就不可能只在某几处漏。
	public static class Cursor
	{
		// 像素坐标。所有瞄准最后都落到这儿
		public static void AimPx(float worldX, float worldY)
		{
			Main.mouseX = (int)(worldX - Main.screenPosition.X);
			Main.mouseY = (int)(worldY - Main.screenPosition.Y);
			// 智能光标会自己挑目标。我们每次都是指名道姓要某一格,绝不能让它改
			Main.SmartCursorWanted_Mouse = false;
		}

		// 格坐标,瞄那一格的中心
		public static void AimTile(int wx, int wy) => AimPx(wx * 16f + 8f, wy * 16f + 8f);

		// 相对玩家中心的偏移(格)。扔投掷物那种"朝某个方向甩"用这个
		public static void AimOffset(Player p, float dxTiles, float dyTiles)
			=> AimPx(p.Center.X + dxTiles * 16f, p.Center.Y + dyTiles * 16f);
	}
}
