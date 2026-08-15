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
		const int LavaGap = 4;              // 离岩浆面 4 格以内开始罚
		const int LavaW = 30;               // 贴岩浆比贴石头贵:掉下去是死,蹭天花板只是难受
		const int ThinOk = 2;               // 这么薄的壳随便挖
		const int ThickW = 40;              // 超出部分按平方涨价
		const int ThickMax = 24;            // 量到这么厚就够贵了,再往前数没意义
		const int LavaProbe = 30;           // 桥面往下探这么多格找岩浆
		const int Body = 3;                 // 人 42px 高 = 3 行
		const int StartWindow = 60;         // 起点 x 容差。房子底下要岩浆,±8 那点余地根本挑不着
		const int Unreachable = int.MaxValue / 4;

		public struct Result
		{
			public bool Found;
			public string Why;
			public int StartX, StartY;
			public int HouseX, HouseY;
			public int DigCells, Cost;
			public bool HouseOnLava;
			public int HouseLavaCols;
			public int AnchorX, AnchorY;   // 导航终点:桥头正上方、天花板底下那一格
			public List<(int x, int y)> Line;
		}

		// 一列的空腔:天花板底面 .. 第一个实心/岩浆。岩浆算下表面 —— 桥架它上面,脚下是空气。
		static void Column(int x, out int ceil, out int floor)
		{
			ceil = Main.UnderworldLayer;
			floor = Main.maxTilesY - 2;
			if (x < 1 || x >= Main.maxTilesX - 1) { ceil = floor = 0; return; }
			// 生成器夹死(WorldGen "Underworld"):天花板 -190..-160,岩浆面 -120..-60。
			// 找【空气】不是找实心:hi 本身就泡在岩浆里,找实心第一格就命中,floor 恒等于 hi。
			int lo = Main.maxTilesY - 145, hi = Main.maxTilesY - 12;
			floor = lo;
			for (int y = hi; y >= lo; y--)
				if (!Predicates.IsSolid(x, y) && !Predicates.IsLava(x, y)) { floor = y + 1; break; }
			int clo = Main.maxTilesY - 200, chi = Main.maxTilesY - 150;
			ceil = clo;
			for (int y = System.Math.Min(floor, chi); y >= clo; y--)
				if (Predicates.IsSolid(x, y)) { ceil = y + 1; break; }
		}

		// 桥面在 (x,y):人占 y-1..y-Body+1。这几格有实心就得挖。
		static int Blocked(int x, int y)
		{
			int n = 0;
			for (int r = 1; r < Body + 1; r++) if (Predicates.IsSolid(x, y - r)) n++;
			return n;
		}

		// 桥面底下一路往下是不是岩浆。中间隔着石头就不算 —— 那是地,不是悬在岩浆上。
		static bool LavaBelow(int x, int y)
		{
			for (int k = 1; k <= LavaProbe; k++)
			{
				if (Predicates.IsLava(x, y + k)) return true;
				if (Predicates.IsSolid(x, y + k)) return false;
			}
			return false;
		}

		// 这一格挡着的话,往前还要连挖几列。薄壳挖穿就通了,厚墙是一路挖到底 ——
		// 按格数线性收钱两者一样贵(20 列×3 格 = 60,和 20 处薄壳同价),所以厚度必须自己涨价。
		static int Thickness(int x, int y, int dir)
		{
			int t = 0;
			for (int k = 0; k < ThickMax; k++)
			{
				if (Blocked(x + dir * k, y) == 0) break;
				t++;
			}
			return t;
		}

		// 没有禁区:拿 ceil/floor 当硬墙时,相邻两列量到不同的腔就接不上 → no_path。挖得动就过得去。
		// 居中按比例:腔 3 格高时"居中"=离下表面 1 格,60 格高时=30 格,一个式子两种都对。
		static int CellCost(int x, int y, int ceil, int floor)
		{
			int blk = Blocked(x, y);
			int c = blk * DigCell;
			// 越挖不到头越贵:薄壳(1~2 列)照旧,厚墙按平方涨,绕多远都比硬凿划算
			if (blk > 0)
			{
				int th = Thickness(x, y, 1) + Thickness(x, y, -1) - 1;
				if (th > ThinOk) c += (th - ThinOk) * (th - ThinOk) * ThickW;
			}
			int span = floor - ceil;
			if (span >= Body)
			{
				float rel = (float)(y - ceil) / span;
				if (rel < 0f) rel = 0f; else if (rel > 1f) rel = 1f;
				c += (int)(System.Math.Abs(rel - 0.5f) * 2f * CenterW);
			}
			int head = (y - Body) - ceil;
			if (head < CeilNear) c += (CeilNear - head) * CeilW;
			// 腔外面不是不能去,只是白挖 —— 给个明确的钱,别让它比腔里还便宜
			if (y <= ceil) c += (ceil - y + 1) * CeilW;
			// 桥面贴着岩浆面就等于把方块插进岩浆里。|rel-0.5| 两头一样贵,分不出"蹭石头"和"蹭岩浆",
			// 而 y>floor 才收钱,y==floor 白送 —— 于是线专挑岩浆面走。离下表面近本身就得涨价。
			int overLava = LavaGap - (floor - y);
			if (overLava > 0) c += overLava * LavaW;
			if (y > floor) c += (y - floor) * DigCell;
			// 方块绝不进岩浆:这条是禁令不是价钱,再贵的价都可能被更贵的绕路盖过去。
			// floor 是整列估出来的,桥面占的是具体那一格,所以直接问这一格。
			if (Predicates.IsLava(x, y)) return Unreachable;
			for (int r = 1; r < Body + 1; r++) if (Predicates.IsLava(x, y - r)) return Unreachable;
			return c;
		}

		public static Result Compute(int bx, int dir)
		{
			var res = new Result { Line = new List<(int x, int y)>() };
			dir = dir >= 0 ? 1 : -1;

			// 每列只量一次:候选 121 个、每个查 6 列,现算的话 Column() 要跑八百多趟,比 Dijkstra 本身还贵
			int span0 = StartWindow + HouseBuilder.RoomWidth + 2;
			var cCeil = new int[span0 * 2 + 1];
			var cFloor = new int[span0 * 2 + 1];
			for (int d = -span0; d <= span0; d++) Column(bx + d, out cCeil[d + span0], out cFloor[d + span0]);
			bool LavaAt(int off2)
			{
				int idx = off2 + span0;
				if (idx < 0 || idx >= cFloor.Length) return false;
				return Predicates.IsLava(bx + off2, cFloor[idx]);
			}

			// 排序:岩浆列数 → 离 bx 近 → 空腔高。没有第二判据的话,同样 6/6 也会白跑 60 格。
			int sx = bx, bestClear = -1, bestLava = -1, bestOff = int.MaxValue;
			for (int d = -StartWindow; d <= StartWindow; d++)
			{
				int x = bx + d;
				int c0 = cCeil[d + span0], f0 = cFloor[d + span0];
				if (f0 - c0 < Body) continue;
				int lv = 0;
				for (int k = 0; k < HouseBuilder.RoomWidth + 1; k++)
					if (LavaAt(d + dir * k)) lv++;
				int off = System.Math.Abs(d);
				bool better = lv > bestLava
					|| (lv == bestLava && off < bestOff)
					|| (lv == bestLava && off == bestOff && f0 - c0 > bestClear);
				if (better) { bestLava = lv; bestOff = off; bestClear = f0 - c0; sx = x; }
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

			// 升降一格必须占【两列】:先平一格再抬,坡度上限 0.5。一列一抬(斜率 1)的台阶不好搭。
			// 中间那列的钱也要算,不然抬升会白蹭一格免费的地。
			while (pq.Count > 0)
			{
				var (d, i, r) = pq.Min;
				pq.Remove(pq.Min);
				if (d > dist[i, r]) continue;
				foreach (var (di, dr) in new[] { (1, 0), (2, 1), (2, -1) })
				{
					int ni = i + di, nr = r + dr;
					if (ni < 0 || ni >= Length || nr < 0 || nr >= rows) continue;
					int cc = CellCost(sx + dir * ni, yLo + nr, ceilA[ni], floorA[ni]);
					if (cc >= Unreachable) continue;
					int mid = 0;
					if (di == 2)
					{
						// 跨两列时中间那列留在原高度,它的钱照付、岩浆照否决,不然抬升白蹭一格
						mid = CellCost(sx + dir * (i + 1), yLo + r, ceilA[i + 1], floorA[i + 1]);
						if (mid >= Unreachable) continue;
					}
					int nd = d + cc + mid + (dr != 0 ? SlopeW : 0);
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

			// 回溯走前驱链。抬升那一步跨了两列,中间那列没进过 prev —— 它留在【前驱的高度】,补上。
			var ys = new int[Length];
			for (int i = 0; i < Length; i++) ys[i] = -1;
			int ci = Length - 1, cr2 = endR;
			for (int guard = 0; guard < Length * rows + 16; guard++)
			{
				if (ys[ci] < 0) ys[ci] = yLo + cr2;
				int p = prev[ci, cr2];
				if (p < 0) break;
				int pi = p / rows, pr = p % rows;
				if (ci - pi == 2) ys[pi + 1] = yLo + pr;
				ci = pi; cr2 = pr;
			}
			for (int i = 0; i < Length; i++)
				if (ys[i] < 0) { res.Why = $"broken_trace@col{i}"; return res; }

			for (int i = 0; i < Length; i++)
				if (Predicates.IsLava(sx + dir * i, ys[i])) { res.Why = $"lava_on_deck@col{i}"; return res; }

			// 坡度 0.5 = 每变一次高度前必须先平至少一格。连续两列都变高就是边集坏了
			int maxStep = 0, backToBack = 0;
			for (int i = 1; i < Length; i++)
			{
				int st = System.Math.Abs(ys[i] - ys[i - 1]);
				maxStep = System.Math.Max(maxStep, st);
				if (st > 0 && i >= 2 && ys[i - 1] != ys[i - 2]) backToBack++;
			}
			if (maxStep > 1 || backToBack > 0)
				DiagLog.Write($"[hell-line] 坡度越界 maxStep={maxStep} 连抬={backToBack} —— 边集出问题了");

			// 两个面量得对不对,一行就能看出来 —— 位置错过一次就是错在这儿
			DiagLog.Write($"[hell-line] 面 x={sx} ceil={ceilA[0]} floor={floorA[0]} | 中段 x={sx + dir * (Length / 2)} " +
				$"ceil={ceilA[Length / 2]} floor={floorA[Length / 2]} | 末 ceil={ceilA[Length - 1]} floor={floorA[Length - 1]}");

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
			// 导航终点在桥头正上方、贴着天花板:那儿脚下是天花板的石头,人站得住;
			// 桥头本身悬在半空(底下要岩浆),走过去没法落脚,只能从这儿往下铺。
			res.AnchorX = sx; res.AnchorY = ceilA[0];
			PickHouse(ref res, sx, dir, ys, ceilA);
			return res;
		}

		// 房子要 6 列平地 + 头顶 10 行净空(HouseBuilder: RoomWidth*1+1 宽, PillarH=9 加地板)。
		// 桥可以有坡,房子不行 —— 所以只认窗口内 y 恒定的位置,挖得最少的那个。
		static void PickHouse(ref Result res, int sx, int dir, int[] ys, int[] ceilA)
		{
			const int W = HouseBuilder.RoomWidth + 1;
			// 房子钉在桥的最边缘:起点那 6 列,不再满桥找便宜地方。桥是从房子往外铺的,
			// 房子跑到中间就等于把桥截成两段。下方必须是岩浆 —— NPC 房要悬在岩浆上。
			res.HouseX = sx;
			res.HouseY = ys[0];
			int lavaCols = 0;
			for (int k = 0; k < W && k < Length; k++)
				if (LavaBelow(sx + dir * k, ys[k])) lavaCols++;
			res.HouseOnLava = lavaCols == W;
			res.HouseLavaCols = lavaCols;
		}
	}
}
