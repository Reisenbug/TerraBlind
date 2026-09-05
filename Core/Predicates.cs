using System.Collections.Generic;
using System.Text;
using Terraria;
using Terraria.ID;

namespace TerraBlind
{
	// PREDICATES, the eye's conclusions. Everything here is a pure query: it reads the world and answers a question,
	// with no side effects and no frame state. Actions ask these instead of each re-deriving "can I stand there?" in
	// their own way, and instead of a coordinate being burned into a script as a constant.
	//
	// The split that matters: a predicate MEASURES (how wide is the ledge, how much headroom, is a room legal). It
	// never decides how much is enough, that threshold is the caller's parameter. Measuring is fixed; thresholds are
	// per-task. That is why the same predicates serve a surface hut and a hell hut.
	public static class Predicates
	{
		public static bool InBounds(int x, int y) => x >= 1 && y >= 1 && x < Main.maxTilesX - 1 && y < Main.maxTilesY - 1;

		private static string Esc(string s) => string.IsNullOrEmpty(s) ? "" : s.Replace("\\", "\\\\").Replace("\"", "\\\"");

		// 柱子建在哪一列由身体中心决定,和 OriginCx(覆盖最多的列)不是一回事。
		// 横向对位只认这个,混用两套判据会互相推翻死循环。
		public static int PillarCol(Player p) => (int)((p.position.X + p.width / 2f) / 16f);

		// 碰撞箱压住的列区间。-1 不能省:右边缘那 px 属于前一列,多算一列就会往自己脚底下搭桥。
		public static (int left, int right) BodyCols(float px, float w)
			=> ((int)(px / 16f), (int)((px + w - 1f) / 16f));
		public static (int left, int right) BodyCols(Player p) => BodyCols(p.position.X, p.width);

		// 碰撞箱【左右边界各自落在哪一格】。不减那 1px。
		// BodyCols 减 1 是为了"贴着格边时别多算一列"(往自己脚下搭桥那个坑),
		// 但要问"他踩着哪几列"时那 1px 会漏掉最右那列:向导明明压着 3 列,
		// BodyCols 只报 2 列,挖完他往旁边一滑就被第 3 列接住,永远掉不下去
		public static (int left, int right) TouchCols(float px, float w)
			=> ((int)(px / 16f), (int)((px + w) / 16f));

		// 这一格和人的碰撞箱重叠吗。重叠就【放不下任何东西】(vanilla Collision.EmptyTile
		// 拿 position/width/height 做矩形相交,没有别的判据)。
		//
		// 【绝不拿 OriginCy±N 近似】。站在半砖上脚底下沉 8px,身子从 3 行变成跨 4 行,
		// 而 OriginCy 自己也跟着降一行:[fy-2,fy] 会把头顶那行漏掉、又把脚下那行多算进来。
		// 现场:桥第一格和碰撞箱重合,放不出来 → 跳起来让开 → 落回半砖 → 还是重合 → STUCK
		public static bool BodyOverlaps(Player p, int x, int y)
		{
			if (p == null) return false;
			float l = x * 16f, r = l + 16f, t = y * 16f, b = t + 16f;
			return p.position.X < r && p.position.X + p.width > l
				&& p.position.Y < b && p.position.Y + p.height > t;
		}

		// 人身子盖住的【行】区间。半砖/斜砖上会是 4 行,平地上 3 行
		public static (int top, int bottom) BodyRows(Player p)
			=> ((int)(p.position.Y / 16f), (int)((p.position.Y + p.height - 1f) / 16f));

		// SOLID = 踩得住且走不过去。平台是 solidTop 也算地。树/藤/草 HasTile 但不 solid
		public static bool IsSolid(int x, int y)
		{
			if (!InBounds(x, y)) return false;
			var t = Main.tile[x, y];
			return t.HasTile && Main.tileSolid[t.TileType];
		}

		// 平台:tileSolid[19] 也是 true,所以 IsSolid 分不出它。认平台只能靠 tileSolidTop。
		// 平台能穿过去(不用挖),但站不住桥面的活(要替换成方块),两处都得先认出它
		public static bool IsPlatform(int x, int y)
		{
			if (!InBounds(x, y)) return false;
			var t = Main.tile[x, y];
			return t.HasTile && Main.tileSolidTop[t.TileType];
		}

		// 挡路的:实心且【不是】平台。平台横在面前直接走过去/跳上去,挖它纯属浪费
		public static bool IsWall(int x, int y) => IsSolid(x, y) && !IsPlatform(x, y);

		// 占着格子但站不住也放不进去:树干/藤/草。放置那边会报 occupied,桥面认它就是留洞
		public static bool IsClutter(int x, int y)
		{
			if (!InBounds(x, y)) return false;
			var t = Main.tile[x, y];
			return t.HasTile && !Main.tileSolid[t.TileType] && !Main.tileSolidTop[t.TileType];
		}

		// GROUND = something you can stand ON TOP of: a full solid block or a platform.
		public static bool IsGround(int x, int y)
		{
			if (!InBounds(x, y)) return false;
			var t = Main.tile[x, y];
			return t.HasTile && (Main.tileSolid[t.TileType] || Main.tileSolidTop[t.TileType]);
		}

		// 液体算可通过(能游/趟):"塞不塞得下"和"会不会死"是两个问题,混在一起岩浆湖就成了墙
		public static bool IsPassable(int x, int y) => InBounds(x, y) && !IsSolid(x, y);

		public static bool IsLava(int x, int y)
		{
			if (!InBounds(x, y)) return false;
			var t = Main.tile[x, y];
			return t.LiquidAmount > 0 && t.LiquidType == LiquidID.Lava;
		}

		public static bool IsAnyLiquid(int x, int y) => InBounds(x, y) && Main.tile[x, y].LiquidAmount > 0;

		// 人 3 格高:(x,y) 和上面两格要空、下面一格是地。宽度不在这判(人 20px 会跨两列),那是 ClearWidth 的事
		public static bool CanStand(int x, int y)
		{
			if (!InBounds(x, y)) return false;
			if (!IsGround(x, y + 1)) return false;
			for (int k = 0; k < 3; k++)
				if (!IsPassable(x, y - k)) return false;
			return true;
		}

		// HEADROOM, how many rows above (x,y) are clear, capped at `cap` so a query over open sky is bounded.
		public static int Headroom(int x, int y, int cap)
		{
			int n = 0;
			while (n < cap && IsPassable(x, y - n)) n++;
			return n;
		}

		// CLEAR WIDTH, how many consecutive columns around x are standable on row y, and where that run starts.
		// This is the "is there a ledge big enough" measurement, without saying what big enough is.
		public static int ClearWidth(int x, int y, int cap, out int left)
		{
			left = x;
			if (!CanStand(x, y)) return 0;
			int lx = x, rx = x;
			while (x - lx < cap && CanStand(lx - 1, y)) lx--;
			while (rx - lx < cap && CanStand(rx + 1, y)) rx++;
			left = lx;
			return rx - lx + 1;
		}

		// DANGER DISTANCE, is there lava (or any liquid, if asked) within radius r of (x,y)? A cheap box scan; hell
		// work lives or dies on it.
		public static bool NearHazard(int x, int y, int r, bool lavaOnly = true)
		{
			for (int dy = -r; dy <= r; dy++)
				for (int dx = -r; dx <= r; dx++)
				{
					int cx = x + dx, cy = y + dy;
					if (!InBounds(cx, cy)) continue;
					if (lavaOnly ? IsLava(cx, cy) : IsAnyLiquid(cx, cy)) return true;
				}
			return false;
		}

		// 真的空:没 tile 也没背景墙。树/草不挡路但占着格子,背景墙不挡路但盖在里面不算独立房间
		public static bool Vacant(int x, int y)
		{
			if (!InBounds(x, y)) return false;
			var t = Main.tile[x, y];
			return !t.HasTile && t.WallType == 0;
		}

		// 把一个 w×h 的框画到游戏里:绿=空(放得下),红=被占。选址对不对,看一眼比读坐标可靠。
		// 键盘 H 和 /scan_house 共用这一份。画的必须和判的是同一套 Vacant。
		// dir=-1 时房子往左建,框也要往左画。否则地狱那种 dir=-1 的房子框和实际位置对不上。
		public static int VisualizeBox(int bx, int by, int w, int h, string label, int ttlFrames = 3600, int dir = 1)
		{
			var vis = new System.Collections.Generic.List<(int, int, Microsoft.Xna.Framework.Color)>();
			int blocked = 0;
			for (int ix0 = 0; ix0 < w; ix0++)
				for (int iy = 0; iy < h; iy++)
				{
					int ix = dir > 0 ? ix0 : -ix0;
					int cx = bx + ix, cy = by - iy;
					bool bad = !Vacant(cx, cy);
					if (bad) blocked++;
					vis.Add((cx, cy, bad
						? new Microsoft.Xna.Framework.Color(255, 60, 60) * 0.55f
						: new Microsoft.Xna.Framework.Color(60, 230, 90) * 0.28f));
				}
			PathVisSystem.SetTiles(vis, ttlFrames);
			PathVisSystem.SetLabels(new System.Collections.Generic.List<(int, int, string, Microsoft.Xna.Framework.Color)>
			{ (bx, by, label, Microsoft.Xna.Framework.Color.White) }, ttlFrames);
			return blocked;
		}

		// 每列上下试探多少格。房子悬空也合法,所以不再是"沿地表找落脚点",纯粹是在附近找空位。
		const int VertScan = 60;

		// 腐化/猩红:用原版自己的集合,别自己列 ID
		public static bool EvilTile(int x, int y)
		{
			if (!InBounds(x, y)) return false;
			var t = Main.tile[x, y];
			return t.HasTile && (TileID.Sets.Corrupt[t.TileType] || TileID.Sets.Crimson[t.TileType]);
		}

		// 房址周围这个半径内不能有邪恶方块。挨着腐化盖,蔓延过来房子就废了
		public const int EvilClearR = 100;
		// 只看四角:腐化成片,r=100 时四角落在片里就够判,逐格是 22 万格/候选点
		public static bool EvilNearby(int bx, int by, int w, int h, int r)
		{
			int l = bx - r, rr = bx + w - 1 + r, t = by - h + 1 - r, b = by + r;
			return EvilTile(l, t) || EvilTile(rr, t) || EvilTile(l, b) || EvilTile(rr, b);
		}

		// 梯子:从 (x,y) 往下数到第一块地,中间必须全空。返回要搭几格(0=左下角就贴着地),
		// 超过 MaxLadder 或够不到地返回 -1。
		public const int MaxLadder = 20;
		public static int LadderLen(int x, int y)
		{
			for (int L = 0; L <= MaxLadder; L++)
			{
				int below = y + L + 1;
				if (!InBounds(x, below)) return -1;
				if (IsGround(x, below)) return L;
				if (!Vacant(x, below)) return -1;
			}
			return -1;
		}

		// 脚下一路往下是不是岩浆(中间隔着石头就不算。那是地,不是悬空在岩浆上)。
		// 杀向导要靠这个:挖开他脚下他才掉得进去
		public const int LavaProbeDepth = 30;
		public static bool LavaBelow(int x, int y)
		{
			for (int k = 1; k <= LavaProbeDepth; k++)
			{
				if (IsLava(x, y + k)) return true;
				if (IsSolid(x, y + k)) return false;
			}
			return false;
		}

		// 向外扫最近的房址:(x,y)=左下角,往右 w 列往上 h 行(含自己)必须全空,外加左下角要够得着
		// needLadder=false:地狱起点悬空,底下常是岩浆,要梯子就一个候选都选不出(锚是人造的)
		// needLava=true:整排底下必须是岩浆。肉山那套要把向导从房里捅下去
		public static bool ScanHouse(int fromX, int fromY, int w, int h, int range,
			out int hitX, out int hitY, out int scanned, bool needLadder = true, bool needLava = false)
		{
			hitX = hitY = -1; scanned = 0;
			for (int d = 0; d <= range; d++)
				for (int sgn = 0; sgn < (d == 0 ? 1 : 2); sgn++)
				{
					int x = d == 0 ? fromX : (sgn == 0 ? fromX - d : fromX + d);
					if (x < 1 || x + w >= Main.maxTilesX - 1) continue;
					// 同一列里由近及远试各个高度,先到先得 → 返回的总是离出发点最近的合法位置。
					for (int dy = 0; dy <= VertScan; dy++)
						for (int vs = 0; vs < (dy == 0 ? 1 : 2); vs++)
						{
							int y = dy == 0 ? fromY : (vs == 0 ? fromY - dy : fromY + dy);
							if (!InBounds(x, y) || !InBounds(x + w - 1, y - h + 1)) continue;
							scanned++;
							// 房址可以悬空:平台梯从下面的地一路搭上来。要求那一列 L 格全空、L<=20、底下是地
							if (needLadder && LadderLen(x, y) < 0) continue;
							bool ok = true;
							for (int ix = 0; ix < w && ok; ix++)
								for (int iy = 0; iy < h && ok; iy++)
									if (!Vacant(x + ix, y - iy)) ok = false;
							if (!ok) continue;
							if (needLava)
							{
								bool allLava = true;
								for (int ix = 0; ix < w && allLava; ix++)
									if (!LavaBelow(x + ix, y)) allLava = false;
								if (!allLava) continue;
							}
							// 最后再查邪恶:要扫 71x60,比前面几条贵得多,让便宜的先筛
							if (EvilNearby(x, y, w, h, EvilClearR)) continue;
							hitX = x; hitY = y;
							return true;
						}
				}
			return false;
		}

		public static bool ScanFlat(int fromX, int fromY, int w, int h, int hazardR, int range,
			out int hitX, out int hitY, out int scanned)
		{
			hitX = hitY = -1; scanned = 0;
			for (int d = 0; d <= range; d++)
			{
				for (int s = 0; s < (d == 0 ? 1 : 2); s++)
				{
					int x = d == 0 ? fromX : (s == 0 ? fromX - d : fromX + d);
					if (x < 1 || x >= Main.maxTilesX - 1) continue;
					// walk this column vertically around the reference row, nearest rows first
					for (int dy = 0; dy <= 200; dy++)
					{
						for (int vs = 0; vs < (dy == 0 ? 1 : 2); vs++)
						{
							int y = dy == 0 ? fromY : (vs == 0 ? fromY - dy : fromY + dy);
							if (!InBounds(x, y)) continue;
							scanned++;
							if (!CanStand(x, y)) continue;
							if (ClearWidth(x, y, w + 4, out int left) < w) continue;
							// headroom must hold across the whole run, not just the probe column
							bool ok = true;
							for (int i = 0; i < w && ok; i++)
								if (Headroom(left + i, y, h) < h) ok = false;
							if (!ok) continue;
							if (hazardR > 0 && NearHazard(left + w / 2, y, hazardR)) continue;
							hitX = left; hitY = y;
							return true;
						}
					}
				}
			}
			return false;
		}

		// 用原版的房屋判定(不自己重写):传【房间内部】一点,报缺哪一项。"NPC 没入住"不算诊断,"没门"才算。
		// roomDoor/roomTable 那几个是 private,所以从公开的 houseTile[] 反推
		private static bool HasAny(int[] types)
		{
			for (int i = 0; i < types.Length; i++)
			{
				int t = types[i];
				if (t >= 0 && t < WorldGen.houseTile.Length && WorldGen.houseTile[t]) return true;
			}
			return false;
		}

		public static string RoomJson(int x, int y)
		{
			var sb = new StringBuilder();
			bool shape = WorldGen.StartRoomCheck(x, y);
			bool door = shape && HasAny(TileID.Sets.RoomNeeds.CountsAsDoor);
			bool table = shape && HasAny(TileID.Sets.RoomNeeds.CountsAsTable);
			bool chair = shape && HasAny(TileID.Sets.RoomNeeds.CountsAsChair);
			bool torch = shape && HasAny(TileID.Sets.RoomNeeds.CountsAsTorch);
			bool needs = shape && door && table && chair && torch;
			sb.Append("{\"legal\":").Append(needs ? "true" : "false");
			sb.Append(",\"shape_ok\":").Append(shape ? "true" : "false");
			sb.Append(",\"reason\":\"").Append(WorldGen.roomCheckFailureReason.ToString()).Append('"');
			sb.Append(",\"has\":{\"door\":").Append(door ? "true" : "false")
			  .Append(",\"table\":").Append(table ? "true" : "false")
			  .Append(",\"chair\":").Append(chair ? "true" : "false")
			  .Append(",\"torch\":").Append(torch ? "true" : "false").Append('}');
			var missing = new List<string>();
			if (shape)
			{
				if (!door) missing.Add("door");
				if (!table) missing.Add("table");
				if (!chair) missing.Add("chair");
				if (!torch) missing.Add("torch");
			}
			sb.Append(",\"missing\":[");
			for (int i = 0; i < missing.Count; i++)
			{
				if (i > 0) sb.Append(',');
				sb.Append('"').Append(missing[i]).Append('"');
			}
			sb.Append(']');
			sb.Append(",\"tiles\":").Append(WorldGen.numRoomTiles);
			sb.Append(",\"bounds\":[").Append(WorldGen.roomX1).Append(',').Append(WorldGen.roomY1).Append(',')
			  .Append(WorldGen.roomX2).Append(',').Append(WorldGen.roomY2).Append(']');
			sb.Append('}');
			return sb.ToString();
		}

		// HAVE, how many of an item id the player holds, across hotbar and backpack.
		// 【鼠标上拿着的那件也算】。放置/合成中途物品会悬在 Main.mouseItem 上,它不在
		// inventory 里。只数背包就会少一个,于是"以为够了"不再补合,摆到最后一张时
		// 背包空了报 no_item。当年桌子"合成报成功却只出2张"就是这个,当时靠换合成顺序绕开了
		public static int Have(int id)
		{
			var p = Main.LocalPlayer;
			if (p == null || id < 0) return 0;
			int n = 0;
			for (int i = 0; i < 58 && i < p.inventory.Length; i++)
			{
				var it = p.inventory[i];
				if (it != null && !it.IsAir && it.type == id) n += it.stack;
			}
			var m = Main.mouseItem;
			if (m != null && !m.IsAir && m.type == id) n += m.stack;
			return n;
		}

		// NPC FIND, where a town NPC is, by type id. Needed to wait for a merchant to arrive and to dig out the cell
		// under the Guide, both of which are "look at a specific NPC" questions nothing could answer before.
		public static string NpcJson(int type)
		{
			var sb = new StringBuilder();
			sb.Append("{\"npcs\":[");
			bool first = true;
			for (int i = 0; i < Main.maxNPCs; i++)
			{
				var n = Main.npc[i];
				if (n == null || !n.active) continue;
				if (type >= 0 && n.type != type) continue;
				if (!first) sb.Append(',');
				first = false;
				sb.Append("{\"idx\":").Append(i).Append(",\"type\":").Append(n.type)
				  .Append(",\"name\":\"").Append(Esc(n.FullName ?? n.TypeName ?? "")).Append('"')
				  .Append(",\"town\":").Append(n.townNPC ? "true" : "false")
				  .Append(",\"cell\":[").Append((int)(n.Center.X / 16f)).Append(',').Append((int)(n.Center.Y / 16f)).Append(']')
				  .Append(",\"home\":[").Append(n.homeTileX).Append(',').Append(n.homeTileY).Append(']')
				  .Append(",\"life\":").Append(n.life).Append('}');
			}
			sb.Append("]}");
			return sb.ToString();
		}

		// CELL, the composed answer for one cell: every geometric predicate at once, so a caller diagnosing "why
		// can't I stand here" gets the reason instead of a bare false.
		public static string CellJson(int x, int y, int widthCap, int headCap)
		{
			var sb = new StringBuilder();
			bool stand = CanStand(x, y);
			int width = ClearWidth(x, y, widthCap, out int left);
			sb.Append("{\"cell\":[").Append(x).Append(',').Append(y).Append(']');
			sb.Append(",\"can_stand\":").Append(stand ? "true" : "false");
			sb.Append(",\"ground_below\":").Append(IsGround(x, y + 1) ? "true" : "false");
			sb.Append(",\"solid\":").Append(IsSolid(x, y) ? "true" : "false");
			sb.Append(",\"headroom\":").Append(Headroom(x, y, headCap));
			sb.Append(",\"clear_width\":").Append(width).Append(",\"run_left\":").Append(left);
			sb.Append(",\"lava\":").Append(IsLava(x, y) ? "true" : "false");
			sb.Append(",\"liquid\":").Append(IsAnyLiquid(x, y) ? "true" : "false");
			sb.Append('}');
			return sb.ToString();
		}
	}
}
