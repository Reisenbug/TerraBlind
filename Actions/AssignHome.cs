using Terraria;

namespace TerraBlind
{
	// 给已存在的 NPC 指派住房 —— 不是生成、也不是传送。原版玩家在住房界面点那一下走的就是
	// WorldGen.moveRoom(x, y, n)(WorldGen.cs:1855):设 prioritizedTownNPCType、把它设成
	// homeless、再 SpawnTownNPC(x,y) 让它按房间规则入住。名字叫 Spawn,走的却是
	// RelocatedHomeless 分支,只改 homeTileX/Y。
	//
	// 坐标要房间【里面】的一格,火把那格正好。
	public static class AssignHome
	{
		public static string LastNote = "";

		// roomOccupied/roomEvil 是 private,读不到;能读的只有 canSpawn 和 hiScore。
		// canSpawn=false 是房间没封闭,hiScore<=0 是家具/光源不齐或已被占
		static string WhyBad()
			=> !WorldGen.canSpawn ? "房间没封闭(墙有洞/没门)"
			 : $"家具或光源不齐、或已被占用(hiScore={WorldGen.hiScore})";

		public static bool Try(int npcType, int wx, int wy, out string why)
		{
			why = "";
			int n = NPC.FindFirstNPC(npcType);
			if (n < 0) { why = $"世界里没有 type={npcType} 这个 NPC"; LastNote = why; DiagLog.Write($"[assign] {why}"); return false; }

			var npc = Main.npc[n];
			int oldX = npc.homeTileX, oldY = npc.homeTileY;
			bool wasHomeless = npc.homeless;

			WorldGen.moveRoom(wx, wy, n);

			// 成没成看 homeless 和坐标 —— moveRoom 自己不返回结果
			if (!npc.homeless && (npc.homeTileX != oldX || npc.homeTileY != oldY || wasHomeless))
			{
				LastNote = $"{npc.TypeName} 住进 ({npc.homeTileX},{npc.homeTileY})";
				DiagLog.Write($"[assign] {LastNote} 目标给的是({wx},{wy})");
				return true;
			}
			why = $"{npc.TypeName} 没住进 ({wx},{wy}):{WhyBad()}";
			LastNote = why;
			DiagLog.Write($"[assign] 失败 {why} homeless={npc.homeless}");
			return false;
		}
	}
}
