using System.Collections.Generic;
using Terraria;

namespace TerraBlind
{
	// HELL LINE — 地狱那条 170 格的桥走哪儿。
	//
	// 地狱是上下两个不规则表面夹出来的长通道:上表面是天花板底面(石头),
	// 下表面是岩浆面或地面。要一条相对居中、平滑、处处站得住的线。
	//
	// 一维 DP:逐列推进,|Δy|<=1(桥只能缓坡)。代价三项 —— 抖动、挖掘、偏离中心。
	// 小坡跟着走只花 SlopeW,挖平要 DigSide*3,所以坡自己就跟上去了;
	// 黑曜石砖房挡死时绕不出去,挖穿反而便宜,于是自己去挖。不写"如果是建筑就挖"。
	public static class HellLine
	{
		public const int Length = 170;      // 硬指标,不缩短
		const int SlopeW = 4;               // 抖一格的钱。略高于 MoveSide=3,没约束时倾向水平
		const int CenterW = 1;              // 轻微拉向中间,只在有余地时起作用
		const int DigCell = 26;             // 和主线同价(MazeWand.DigSide),绝不另编一套
		const int Body = 3;                 // 人 42px 高 = 3 行
		const int StartWindow = 8;          // 起点 x 容差:正好落在闭合处就往旁边挪
		const int Unreachable = int.MaxValue / 4;

		public struct Result
		{
			public bool Found;
			public string Why;
			public int StartX, StartY;
			public int HouseX, HouseY;
			public int DigCells, Cost;
			public List<(int x, int y)> Line;
		}

		// 一列的空腔:天花板底面下一格 .. 第一个实心/岩浆。岩浆算下表面 —— 桥架它上面,脚下是空气。
		static void Column(int x, out int ceil, out int floor)
		{
			ceil = Main.UnderworldLayer;
			floor = Main.maxTilesY - 2;
			if (x < 1 || x >= Main.maxTilesX - 1) { ceil = floor = 0; return; }
			int y = Main.UnderworldLayer;
			while (y < Main.maxTilesY - 2 && Predicates.IsSolid(x, y)) y++;
			ceil = y;
			while (y < Main.maxTilesY - 2 && !Predicates.IsSolid(x, y) && !Predicates.IsLava(x, y)) y++;
			floor = y;
		}

		// 站在 (x,y):脚下那格是 y,身子占 y-1..y-Body+1。要挖几格?
		static int Blocked(int x, int y)
		{
			int n = 0;
			for (int r = 0; r < Body; r++) if (Predicates.IsSolid(x, y - r)) n++;
			return n;
		}

		public static Result Compute(int bx, int dir)
		{
			var res = new Result { Line = new List<(int x, int y)>() };
			dir = dir >= 0 ? 1 : -1;

			// 起点列:bx 附近净空最好的一列。钉死 bx 的话正好赶上闭合处就一落地埋石头里。
			int sx = bx, bestClear = -1;
			for (int d = -StartWindow; d <= StartWindow; d++)
			{
				int x = bx + d;
				Column(x, out int c, out int f);
				int clear = f - c;
				if (clear > bestClear) { bestClear = clear; sx = x; }
			}
			if (bestClear < Body)
			{
				res.Why = $"start_too_tight clear={bestClear}";
				return res;
			}

			int lastX = sx + dir * (Length - 1);
			if (lastX < 2 || lastX >= Main.maxTilesX - 2) { res.Why = "line_off_world"; return res; }

			int yLo = Main.UnderworldLayer, yHi = Main.maxTilesY - 3;
			int rows = yHi - yLo + 1;
			var cost = new int[Length, rows];
			var from = new int[Length, rows];

			// 每列先量一次空腔,DP 里反复用
			var ceilA = new int[Length];
			var floorA = new int[Length];
			for (int i = 0; i < Length; i++) Column(sx + dir * i, out ceilA[i], out floorA[i]);

			for (int i = 0; i < Length; i++)
				for (int r = 0; r < rows; r++) { cost[i, r] = Unreachable; from[i, r] = -1; }

			// 一列的固有代价:挖 + 偏离中心。天花板/地板外面直接判死,别让线跑到实体层里去。
			int Own(int i, int y)
			{
				int ceil = ceilA[i], floor = floorA[i];
				if (y <= Main.UnderworldLayer || y >= Main.maxTilesY - 3) return Unreachable;
				int mid = (ceil + floor) / 2;
				int dig = Blocked(sx + dir * i, y);
				return dig * DigCell + System.Math.Abs(y - mid) * CenterW;
			}

			for (int r = 0; r < rows; r++)
			{
				int own = Own(0, yLo + r);
				if (own < Unreachable) cost[0, r] = own;
			}

			for (int i = 1; i < Length; i++)
				for (int r = 0; r < rows; r++)
				{
					int own = Own(i, yLo + r);
					if (own >= Unreachable) continue;
					int best = Unreachable, bestP = -1;
					for (int d = -1; d <= 1; d++)
					{
						int pr = r + d;
						if (pr < 0 || pr >= rows) continue;
						if (cost[i - 1, pr] >= Unreachable) continue;
						int c = cost[i - 1, pr] + System.Math.Abs(d) * SlopeW;
						if (c < best) { best = c; bestP = pr; }
					}
					if (bestP < 0) continue;
					int tot = best + own;
					if (tot < cost[i, r]) { cost[i, r] = tot; from[i, r] = bestP; }
				}

			int endR = -1, endC = Unreachable;
			for (int r = 0; r < rows; r++) if (cost[Length - 1, r] < endC) { endC = cost[Length - 1, r]; endR = r; }
			if (endR < 0) { res.Why = "no_path"; return res; }

			var ys = new int[Length];
			int cr = endR;
			for (int i = Length - 1; i >= 0; i--) { ys[i] = yLo + cr; cr = i > 0 ? from[i, cr] : cr; }

			int digTotal = 0;
			for (int i = 0; i < Length; i++)
			{
				int x = sx + dir * i;
				res.Line.Add((x, ys[i]));
				digTotal += Blocked(x, ys[i]);
			}

			res.Found = true;
			res.StartX = sx; res.StartY = ys[0];
			res.Cost = endC; res.DigCells = digTotal;
			PickHouse(ref res, sx, dir, ys, ceilA);
			return res;
		}

		// 房子要 6 列平地 + 头顶 10 行净空(HouseBuilder: RoomWidth*1+1 宽, PillarH=9 加地板)。
		// 桥可以有坡,房子不行 —— 所以只认窗口内 y 恒定的位置,挖得最少的那个。
		static void PickHouse(ref Result res, int sx, int dir, int[] ys, int[] ceilA)
		{
			const int W = HouseBuilder.RoomWidth + 1;
			const int Head = HouseBuilder.PillarH + 1;
			int best = int.MaxValue, bestI = -1;
			for (int i = 0; i + W <= Length; i++)
			{
				bool flat = true;
				int dig = 0;
				for (int k = 0; k < W; k++)
				{
					if (ys[i + k] != ys[i]) { flat = false; break; }
					if (ys[i + k] - ceilA[i + k] < Head) { flat = false; break; }
					dig += Blocked(sx + dir * (i + k), ys[i + k]);
				}
				if (!flat) continue;
				if (dig < best) { best = dig; bestI = i; }
			}
			if (bestI < 0) { res.HouseX = res.StartX; res.HouseY = res.StartY; return; }
			res.HouseX = sx + dir * bestI;
			res.HouseY = ys[bestI];
		}
	}
}
