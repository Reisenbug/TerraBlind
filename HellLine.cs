using System.Collections.Generic;
using Terraria;

namespace TerraBlind
{
	// HELL LINE — 地狱那条 170 格的桥铺在哪一行。
	//
	// 地狱是上下两个不规则表面夹出来的长通道:上表面是天花板底面(石头),
	// 下表面是岩浆面或地面。桥是给人站、给雷管停的实心面,所以要连续 170 格。
	//
	// Dijkstra 不是逐列 DP:DP 一列一个状态就只能翻过去或挖过去,绕不开东西。
	// 图上四邻走,能回头能上下,形状交给它自己找。
	public static class HellLine
	{
		public const int Length = 170;      // 硬指标,不缩短
		const int SlopeW = 4;               // 上下挪一格的钱。没约束时线自己走平
		const int DigCell = 26;             // 和主线同价(MazeWand.DigSide),绝不另编一套
		const int CenterW = 20;             // 偏离空腔中间的钱,按【比例】算不按格数
		const int CeilW = 6;                // 贴天花板的钱,这个是绝对格数:人得塞进去
		const int CeilNear = 5;             // 头顶 5 格以内开始罚
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

		// 一列的空腔:天花板底面 .. 第一个实心/岩浆。岩浆算下表面 —— 桥架它上面,脚下是空气。
		static void Column(int x, out int ceil, out int floor)
		{
			ceil = Main.UnderworldLayer;
			floor = Main.maxTilesY - 2;
			if (x < 1 || x >= Main.maxTilesX - 1) { ceil = floor = 0; return; }
			// UnderworldLayer 那一行通常是空气,天花板的石头在它【下面】。原来从这儿直接找"第一个实心"
			// 就把天花板顶面当成了下表面,量出来的空腔是天花板上方那段空气 —— 于是每一格都判死,全图 no_path。
			int y = Main.UnderworldLayer, lim = Main.maxTilesY - 2;
			while (y < lim && !Predicates.IsSolid(x, y)) y++;
			while (y < lim && Predicates.IsSolid(x, y)) y++;
			ceil = y;
			while (y < lim && !Predicates.IsSolid(x, y) && !Predicates.IsLava(x, y)) y++;
			floor = y;
		}

		// 桥面在 (x,y):人占 y-1..y-Body+1。这几格有实心就得挖。
		static int Blocked(int x, int y)
		{
			int n = 0;
			for (int r = 1; r < Body + 1; r++) if (Predicates.IsSolid(x, y - r)) n++;
			return n;
		}

		// 一格待着值多少。居中按比例 —— 空腔 3 格高时"居中"就是离下表面 1 格,
		// 60 格高时就是 30 格,同一个式子两种都对,所以下方永远不设格数阈值。
		static int CellCost(int x, int y, int ceil, int floor)
		{
			int span = floor - ceil;
			if (span < Body) return Unreachable;
			if (y <= ceil || y > floor) return Unreachable;
			int c = Blocked(x, y) * DigCell;
			float rel = (float)(y - ceil) / span;
			c += (int)(System.Math.Abs(rel - 0.5f) * 2f * CenterW);
			int head = (y - Body) - ceil;
			if (head < CeilNear) c += (CeilNear - head) * CeilW;
			return c;
		}

		public static Result Compute(int bx, int dir)
		{
			var res = new Result { Line = new List<(int x, int y)>() };
			dir = dir >= 0 ? 1 : -1;

			int sx = bx, bestClear = -1;
			for (int d = -StartWindow; d <= StartWindow; d++)
			{
				Column(bx + d, out int c0, out int f0);
				if (f0 - c0 > bestClear) { bestClear = f0 - c0; sx = bx + d; }
			}
			if (bestClear < Body) { res.Why = $"start_too_tight clear={bestClear}"; return res; }

			int lastX = sx + dir * (Length - 1);
			if (lastX < 2 || lastX >= Main.maxTilesX - 2) { res.Why = "line_off_world"; return res; }

			int yLo = Main.UnderworldLayer + 1, yHi = Main.maxTilesY - 3;
			int rows = yHi - yLo + 1;
			var ceilA = new int[Length];
			var floorA = new int[Length];
			for (int i = 0; i < Length; i++) Column(sx + dir * i, out ceilA[i], out floorA[i]);

			var dist = new int[Length, rows];
			var prev = new int[Length, rows];
			for (int i = 0; i < Length; i++)
				for (int r = 0; r < rows; r++) { dist[i, r] = Unreachable; prev[i, r] = -1; }

			var pq = new SortedSet<(int d, int i, int r)>();
			for (int r = 0; r < rows; r++)
			{
				int c = CellCost(sx, yLo + r, ceilA[0], floorA[0]);
				if (c >= Unreachable) continue;
				dist[0, r] = c;
				pq.Add((c, 0, r));
			}
			if (pq.Count == 0) { res.Why = "start_blocked"; return res; }

			// 四邻:前后各一列 + 同列上下。能回头能上下,所以绕得开东西 —— DP 绕不开。
			while (pq.Count > 0)
			{
				var (d, i, r) = pq.Min;
				pq.Remove(pq.Min);
				if (d > dist[i, r]) continue;
				foreach (var (di, dr) in new[] { (1, 0), (0, 1), (0, -1), (-1, 0) })
				{
					int ni = i + di, nr = r + dr;
					if (ni < 0 || ni >= Length || nr < 0 || nr >= rows) continue;
					int cc = CellCost(sx + dir * ni, yLo + nr, ceilA[ni], floorA[ni]);
					if (cc >= Unreachable) continue;
					int nd = d + cc + (dr != 0 ? SlopeW : 0);
					if (nd >= dist[ni, nr]) continue;
					if (dist[ni, nr] < Unreachable) pq.Remove((dist[ni, nr], ni, nr));
					dist[ni, nr] = nd;
					prev[ni, nr] = i * rows + r;   // 存整个前驱:竖直移动的前驱在【同一列】,只存行号回溯必错位
					pq.Add((nd, ni, nr));
				}
			}

			int endR = -1, endC = Unreachable;
			for (int r = 0; r < rows; r++) if (dist[Length - 1, r] < endC) { endC = dist[Length - 1, r]; endR = r; }
			if (endR < 0)
			{
				// 断在哪一列比"no_path"有用得多:列号一报出来就知道是地形挡死还是我的判据把整层判死了
				int reached = 0;
				for (int i = 0; i < Length; i++)
				{
					bool any = false;
					for (int r = 0; r < rows; r++) if (dist[i, r] < Unreachable) { any = true; break; }
					if (!any) break;
					reached = i;
				}
				int ok0 = 0;
				for (int r = 0; r < rows; r++) if (CellCost(sx, yLo + r, ceilA[0], floorA[0]) < Unreachable) ok0++;
				DiagLog.Write($"[hell-line] no_path 断在第{reached}列 x={sx + dir * reached} " +
					$"ceil={ceilA[reached]} floor={floorA[reached]} span={floorA[reached] - ceilA[reached]} " +
					$"起点列可用行={ok0} yLo={yLo} rows={rows}");
				res.Why = $"no_path@col{reached}";
				return res;
			}

			// 路径会在同一列上下挪,所以回溯是走前驱链,不是每列退一格。同列多次经过取最后落的那行。
			var ys = new int[Length];
			for (int i = 0; i < Length; i++) ys[i] = -1;
			int ci = Length - 1, cr2 = endR;
			for (int guard = 0; guard < Length * rows + 16; guard++)
			{
				if (ys[ci] < 0) ys[ci] = yLo + cr2;
				int p = prev[ci, cr2];
				if (p < 0) break;
				ci = p / rows; cr2 = p % rows;
			}
			for (int i = 0; i < Length; i++)
				if (ys[i] < 0) { res.Why = $"broken_trace@col{i}"; return res; }

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
					if (ys[i + k] != ys[i] || ys[i + k] - ceilA[i + k] < Head) { flat = false; break; }
					dig += Blocked(sx + dir * (i + k), ys[i + k]);
				}
				if (flat && dig < best) { best = dig; bestI = i; }
			}
			if (bestI < 0) { res.HouseX = res.StartX; res.HouseY = res.StartY; return; }
			res.HouseX = sx + dir * bestI;
			res.HouseY = ys[bestI];
		}
	}
}
