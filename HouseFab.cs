using Terraria;
using Terraria.ID;

namespace TerraBlind
{
	// 一次把整座房子放出来,不走位、不挥手、不判够不够得着。
	//
	// WorldGen.PlaceTile(..., forced:true) 绕过 Collision.EmptyTile(WorldGen.cs:54195),
	// 所以【人的碰撞箱里也能放】;而岩浆、伸手范围、releaseUseItem 那些门全在
	// Player.PlaceThing 那一层,PlaceTile 根本不经过。于是走位/让位/清场/停位一整套都不需要了。
	//
	// 结构定义照抄 HouseBuilder.AuditHouse 的验收清单,两边必须是同一份,
	// 否则生成完自己验不过。墙形状直接共用 HouseBuilder.WallOrder。
	public static class HouseFab
	{
		const int T_PLATFORM = TileID.Platforms;
		const int T_WALL = WallID.Wood;
		const int T_TORCH = TileID.Torches;

		public static string LastReport = "";

		// (ax,ay)=左下角=地板第一格,和 HouseBuilder.Start 同一套坐标
		public static bool Build(int rooms, int dir, int ax, int ay, out string why)
		{
			why = "";
			if (rooms < 1) rooms = 1;
			dir = dir >= 0 ? 1 : -1;

			int localMax = HouseBuilder.RoomWidth * rooms + 1;
			int floorRow = ay;
			int roofRow = floorRow - HouseBuilder.PillarH;
			int mainCol = ax + dir * (localMax - 1);
			int placed = 0, walls = 0;

			int Wx(int local) => ax + dir * (local - 1);

			// 1) 先清空整个框:里面剩着的方块/平台会把内腔切碎,原版 StartRoomCheck 直接判死
			for (int ix = 0; ix < localMax; ix++)
				for (int iy = 0; iy <= HouseBuilder.PillarH; iy++)
				{
					int cx = ax + dir * ix, cy = floorRow - iy;
					if (!Predicates.InBounds(cx, cy)) continue;
					if (Main.tile[cx, cy].HasTile) WorldGen.KillTile(cx, cy, noItem: true);
				}

			// 2) 地板:踩的那一面是 floorRow+1,和 CheckLine 一致
			for (int k = 0; k < localMax; k++)
				placed += Put(Wx(1) + dir * k, floorRow + 1, T_PLATFORM);

			// 3) 屋顶
			for (int k = 0; k < localMax; k++)
				placed += Put(Wx(1) + dir * k, roofRow, T_PLATFORM);

			// 4) 主柱:从地板往上 PillarH 格
			for (int k = 0; k < HouseBuilder.PillarH; k++)
				placed += Put(mainCol, floorRow - k, T_PLATFORM);

			// 5) 每间的支柱
			for (int r = 0; r < rooms; r++)
				for (int k = 0; k < HouseBuilder.SupportH; k++)
					placed += Put(Wx(1 + HouseBuilder.RoomWidth * r), floorRow - k, T_PLATFORM);

			// 6) 背景墙
			for (int r = 0; r < rooms; r++)
			{
				int col1 = 1 + HouseBuilder.RoomWidth * r;
				foreach (var (dr, dc) in HouseBuilder.WallOrder)
				{
					int wx = Wx(col1 + (dc - 1)), wy = roofRow + dr;
					if (!Predicates.InBounds(wx, wy)) continue;
					if (Main.tile[wx, wy].WallType != 0) continue;
					WorldGen.PlaceWall(wx, wy, T_WALL, mute: true);
					if (Main.tile[wx, wy].WallType != 0) walls++;
				}
			}

			// 7) 家具:数量和位置照抄 AuditHouse
			int tableCount = rooms >= 4 ? 3 : 0;
			int chairCount = rooms >= 4 ? 4 : 1;
			for (int i = 0; i < tableCount; i++)
				placed += Put(Wx(14 - HouseBuilder.RoomWidth * i), floorRow, TileID.Tables);
			for (int i = 0; i < chairCount; i++)
				placed += Put(Wx(2 + HouseBuilder.RoomWidth * i), floorRow, TileID.Chairs);

			// 8) 每间一个火把
			for (int r = 0; r < rooms; r++)
				placed += Put(Wx(1 + HouseBuilder.RoomWidth * r + 2), roofRow + 2, T_TORCH);

			LastReport = $"rooms={rooms} dir={dir} 角=({ax},{ay}) 地板行={floorRow + 1} 屋顶行={roofRow} 放了{placed}格 墙{walls}格";
			DiagLog.Write($"[housefab] {LastReport}");
			return true;
		}

		// forced:true 是关键:不查玩家碰撞箱、不查液体。放完确认真出现了才计数
		static int Put(int x, int y, int type, int style = 0)
		{
			if (!Predicates.InBounds(x, y)) return 0;
			WorldGen.PlaceTile(x, y, type, mute: true, forced: true, plr: -1, style: style);
			return Main.tile[x, y].HasTile && Main.tile[x, y].TileType == type ? 1 : 0;
		}
	}
}
