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
		public const int Bridge = 170;      // 硬指标,不缩短。这是【桥】的长度,不含房子
		public const int HouseW = HouseBuilder.RoomWidth + 1;
		// 线要连房子一起算:头 6 列是房子地板,后面 170 格才是桥。
		// 只算 170 的话房子会吃掉桥的前 6 格,雷管站的地方就短了。
		public const int Length = HouseW + Bridge;
		const int SlopeW = 4;               // 上下挪一格的钱。没约束时线自己走平
		// 连着爬第 k 段额外加这么多:陡坡不禁止,但连成一串会越来越贵,于是"能分散就分散"。
		// 只加 SlopeW 是线性的 —— 连爬10段和分散爬10段一样贵,Dijkstra 没理由避开陡坡
		const int RunSlopeW = 26;           // 一段连爬 ≈ 一格挖掘的钱
		const int MaxRun = 3;               // 连爬段数封顶(状态维度),再陡也不再涨价
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
		const int StartCenterW = 240;       // 居中,大加分。满分给足,和锚点(6列×60)同量级
		const int StartNearW = 3;           // 离人近只是小优势,不该盖过前两项
		const int AnchorR = 3;              // 这么多格以内有实心就算够得着
		const int Unreachable = int.MaxValue / 4;

		public struct Result
		{
			public bool Found;
			public string Why;
			public int StartX, StartY;
			public int HouseX, HouseY;
			public int WorkX, WorkY, WorkI;   // 开工点:线上锚点最好的一格,从这儿往两头铺
			public int WorkAnchor;
			public int DigCells, Cost;
			public bool HouseOnLava;
			public int HouseLavaCols;
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

		// 桥面在 (x,y):人占 y-1..y-Body。再往上多留一格 = Head 行全要空。
		// 上坡时人先升一行再落到新桥面,只按身高 3 行算的话,第 4 行那块石头就是卡死的地方
		const int Head = Body + 1;
		static int Blocked(int x, int y)
		{
			int n = 0;
			for (int r = 1; r <= Head; r++) if (Predicates.IsSolid(x, y - r)) n++;
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
			int head = (y - Head) - ceil;
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
			for (int r = 1; r <= Head; r++) if (Predicates.IsLava(x, y - r)) return Unreachable;
			return c;
		}

		// 预估行必须【用 CellCost 挑】。自己编一套"取中点"会和 Dijkstra 差几十行,
		// 于是在没人去的行上数锚点,分数全是假的(量到:打分说锚 6/6,线上实测 0/8)。
		static int StartRow(int x, int ceil, int floor)
		{
			int best = ceil + 1, bc = Unreachable;
			for (int y = ceil + 1; y <= floor; y++)
			{
				int c = CellCost(x, y, ceil, floor);
				if (c < bc) { bc = c; best = y; }
			}
			return best;
		}

		// 一格的锚:贴身(r=1)最值钱,远的只是"能接过去"。四周全空就放不出来。
		static int AnchorScore(int x, int y)
		{
			for (int r = 1; r <= AnchorR; r++)
				for (int dy = -r; dy <= r; dy++)
					for (int dx = -r; dx <= r; dx++)
						if (System.Math.Abs(dx) + System.Math.Abs(dy) == r
							&& Predicates.IsSolid(x + dx, y + dy)) return AnchorR + 1 - r;
			return 0;
		}

		// by = 人脚下那行。开工点要选【离人最近】的,不知道人在哪一行就只能比列,差几十行也算近。
		public static Result Compute(int bx, int dir, int by = int.MinValue)
		{
			var res = new Result { Line = new List<(int x, int y)>() };
			dir = dir >= 0 ? 1 : -1;
			if (by == int.MinValue) by = Main.LocalPlayer != null ? ActExecutor.OriginCy(Main.LocalPlayer) : 0;

			// 每列只量一次:候选 121 个、每个查 6 列,现算的话 Column() 要跑八百多趟,比 Dijkstra 本身还贵
			int span0 = StartWindow + HouseBuilder.RoomWidth + 2;
			var cCeil = new int[span0 * 2 + 1];
			var cFloor = new int[span0 * 2 + 1];
			for (int d = -span0; d <= span0; d++) Column(bx + d, out cCeil[d + span0], out cFloor[d + span0]);
			// 锚不进打分:悬空处处都是,人自己造锚(PlaceAnywhere)。留下的判据只有居中和离人近。
			int sx = bx, bestScore = int.MinValue, bestClear = -1, bestRow = 0;
			float bestRel = -1f;
			// 【房子底下必须是岩浆】,所以先按"够不够岩浆"分档,再在同档里比居中/离人近。
			// 原来只按居中打分,岩浆只在最后 PickHouse 里数一数就报出去,选址等于没管过它 ——
			// 日志:ScanHouse 明明挪到了岩浆上的 1091 行,Compute 一重算又跑回 1047
			int bestLava = -1;
			for (int pass = 0; pass < 2; pass++)
			{
				for (int d = -StartWindow; d <= StartWindow; d++)
				{
					int x = bx + d;
					int c0 = cCeil[d + span0], f0 = cFloor[d + span0];
					if (f0 - c0 < Head) continue;   // 腔子得塞得下人 + 头顶那一格
					int y0 = StartRow(x, c0, f0);
					int lav = 0;
					for (int k = 0; k < HouseW; k++) if (LavaBelow(x + dir * k, y0)) lav++;
					if (pass == 0 && lav < HouseW) continue;   // 头一遍只要整排都在岩浆上的
					float rel = (float)(y0 - c0) / System.Math.Max(1, f0 - c0);
					// 就近不就价:只按离人远近挑。居中分再高也没用 —— 人得先徒步走过去,
					// 而在地狱里每多走一格都是掉岩浆的机会
					int score = -System.Math.Abs(d);
					if (score > bestScore)
					{ bestScore = score; sx = x; bestClear = f0 - c0; bestRel = rel; bestRow = y0; bestLava = lav; }
				}
				// 整排岩浆的位置一个都没有才放宽 —— 那时后面捅向导那套做不了,但房子还能盖
				if (bestScore != int.MinValue) break;
				DiagLog.Write("[hell-line] 附近没有整排悬在岩浆上的起点,放宽岩浆要求");
			}
			if (bestScore == int.MinValue) { res.Why = "start_too_tight"; return res; }
			DiagLog.Write($"[hell-line] 起点岩浆列={bestLava}/{HouseW}");
			// 预估行要和线上真实的 ys[0] 对得上,对不上就说明打分打在了没人去的行上
			DiagLog.Write($"[hell-line] 起点 x={sx}(离人{sx - bx}) 分={bestScore} 预估行={bestRow} 居中rel={bestRel:0.00} 空腔={bestClear}");

			int lastX = sx + dir * (Length - 1);
			if (lastX < 2 || lastX >= Main.maxTilesX - 2) { res.Why = "line_off_world"; return res; }

			int yLo = Main.UnderworldLayer + 1, yHi = Main.maxTilesY - 3;
			int rows = yHi - yLo + 1;
			var ceilA = new int[Length];
			var floorA = new int[Length];
			for (int i = 0; i < Length; i++) Column(sx + dir * i, out ceilA[i], out floorA[i]);

			// 第三维 = 到这里为止【连着爬了几段】。没有它就记不住"刚才是不是也在爬",
			// 连续陡坡和分散陡坡的总价就永远相等
			var dist = new int[Length, rows, MaxRun + 1];
			var prev = new int[Length, rows, MaxRun + 1];
			for (int i = 0; i < Length; i++)
				for (int r = 0; r < rows; r++)
					for (int k = 0; k <= MaxRun; k++) { dist[i, r, k] = Unreachable; prev[i, r, k] = -1; }

			var pq = new SortedSet<(int d, int i, int r, int k)>();
			for (int r = 0; r < rows; r++)
			{
				int c = CellCost(sx, yLo + r, ceilA[0], floorA[0]);
				if (c >= Unreachable) continue;
				dist[0, r, 0] = c;
				pq.Add((c, 0, r, 0));
			}
			if (pq.Count == 0) { res.Why = "start_blocked"; return res; }

			// 升降一格必须占【两列】:先平一格再抬,坡度上限 0.5。一列一抬(斜率 1)的台阶不好搭。
			// 中间那列的钱也要算,不然抬升会白蹭一格免费的地。
			while (pq.Count > 0)
			{
				var (d, i, r, k) = pq.Min;
				pq.Remove(pq.Min);
				if (d > dist[i, r, k]) continue;
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
					// 平走清零,爬一段就 +1(封顶)。罚款按【已经连爬了几段】收,越连越贵
					int nk = dr != 0 ? System.Math.Min(k + 1, MaxRun) : 0;
					int slope = dr != 0 ? SlopeW + k * RunSlopeW : 0;
					int nd = d + cc + mid + slope;
					if (nd >= dist[ni, nr, nk]) continue;
					if (dist[ni, nr, nk] < Unreachable) pq.Remove((dist[ni, nr, nk], ni, nr, nk));
					dist[ni, nr, nk] = nd;
					prev[ni, nr, nk] = (i * rows + r) * (MaxRun + 1) + k;   // 存整个前驱:竖直移动的前驱在【同一列】,只存行号回溯必错位
					pq.Add((nd, ni, nr, nk));
				}
			}

			int endR = -1, endC = Unreachable, endK = 0;
			for (int r = 0; r < rows; r++)
				for (int k = 0; k <= MaxRun; k++)
					if (dist[Length - 1, r, k] < endC) { endC = dist[Length - 1, r, k]; endR = r; endK = k; }
			if (endR < 0)
			{
				// 断在哪一列比"no_path"有用得多:列号一报出来就知道是地形挡死还是我的判据把整层判死了
				int reached = 0;
				for (int i = 0; i < Length; i++)
				{
					bool any = false;
					for (int r = 0; r < rows && !any; r++)
						for (int k = 0; k <= MaxRun; k++) if (dist[i, r, k] < Unreachable) { any = true; break; }
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
			int ci = Length - 1, cr2 = endR, ck = endK;
			for (int guard = 0; guard < Length * rows * (MaxRun + 1) + 16; guard++)
			{
				if (ys[ci] < 0) ys[ci] = yLo + cr2;
				int p = prev[ci, cr2, ck];
				if (p < 0) break;
				int pk = p % (MaxRun + 1), pcell = p / (MaxRun + 1);
				int pi = pcell / rows, pr = pcell % rows;
				if (ci - pi == 2) ys[pi + 1] = yLo + pr;
				ci = pi; cr2 = pr; ck = pk;
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
			if (maxStep > 1)
				DiagLog.Write($"[hell-line] 坡度越界 maxStep={maxStep} —— 边集出问题了");
			// 连爬有多长要看得见:改了 RunSlopeW 之后靠这个判断陡坡是不是真的分散开了
			int runNow = 0, runMax = 0, steps = 0;
			for (int i = 1; i < Length; i++)
			{
				if (ys[i] != ys[i - 1]) { steps++; runNow++; if (runNow > runMax) runMax = runNow; }
				else runNow = 0;
			}
			DiagLog.Write($"[hell-line] 坡 抬升{steps}次/{Length}列 最长连爬={runMax}段 背靠背={backToBack}");

			// 第一格放不出来的话整条线都白算 —— 所以把起点这一带【每一列】的锚点情况打全。
			// 放方块要正交邻居:上下左右任一格有实心就贴得住。四周全空 = 悬在岩浆上,放不出来。
			for (int i = 0; i < HouseW + 2 && i < Length; i++)
			{
				int x = sx + dir * i, y = ys[i];
				bool up = Predicates.IsSolid(x, y - 1), dn = Predicates.IsSolid(x, y + 1);
				bool lf = Predicates.IsSolid(x - 1, y), rt = Predicates.IsSolid(x + 1, y);
				DiagLog.Write($"[hell-line] 锚 i={i} ({x},{y}) 上{(up ? '#' : '.')}下{(dn ? '#' : '.')}" +
					$"左{(lf ? '#' : '.')}右{(rt ? '#' : '.')} 本格={(Predicates.IsSolid(x, y) ? "实心" : Predicates.IsLava(x, y) ? "岩浆" : "空")} " +
					$"下面={(Predicates.IsLava(x, y + 1) ? "岩浆" : dn ? "地" : "空")} 贴得住={(up || dn || lf || rt)}");
			}

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

			// 【离人最近】的那一格。锚不当门槛 —— 悬空是常态(整条线大半悬空),
			// 放不出第一格是 PlaceAnywhere 的活,不是算不出线。
			res.WorkI = 0; res.WorkAnchor = 0;
			int bestD = int.MaxValue;
			for (int i = 0; i < Length; i++)
			{
				int x = sx + dir * i, y = ys[i];
				int d2 = System.Math.Abs(x - bx) + System.Math.Abs(y - by);
				if (d2 < bestD)
				{ bestD = d2; res.WorkAnchor = AnchorScore(x, y); res.WorkI = i; res.WorkX = x; res.WorkY = y; }
			}
			// 离人多远是关键指标:大了就说明人要在地狱里徒步过去,那正是掉岩浆的机会
			DiagLog.Write($"[hell-line] 开工点 i={res.WorkI} ({res.WorkX},{res.WorkY}) 锚={res.WorkAnchor} " +
				$"离人{bestD}格 往房子{res.WorkI}格 往远端{Length - 1 - res.WorkI}格");

			res.Found = true;
			// 起点 = 房子【靠玩家】那一角(Line[0])。从右往左依次是:玩家、房子、桥。
			// 原来指到 sx+dir*HouseW(房子外侧,桥那头),人得先穿过房子那 6 列才能开工,
			// 等于踩在桥的位置上,把桥下方的岩浆挡住
			res.StartX = sx; res.StartY = ys[0];
			res.Cost = endC; res.DigCells = digTotal;
			PickHouse(ref res, sx, dir, ys, ceilA);
			return res;
		}

		// 房子要 6 列平地 + 头顶 10 行净空(HouseBuilder: RoomWidth*1+1 宽, PillarH=9 加地板)。
		// 桥可以有坡,房子不行 —— 所以只认窗口内 y 恒定的位置,挖得最少的那个。
		static void PickHouse(ref Result res, int sx, int dir, int[] ys, int[] ceilA)
		{
			const int W = HouseW;
			// 房子钉在桥的最边缘:起点那 6 列,不再满桥找便宜地方。桥是从房子往外铺的,
			// 房子跑到中间就等于把桥截成两段。下方必须是岩浆 —— NPC 房要悬在岩浆上。
			res.HouseX = sx;
			res.HouseY = ys[0];
			int lavaCols = 0;
			for (int k = 0; k < W && k < Length; k++)
				if (LavaBelow(sx + dir * k, ys[k])) lavaCols++;
			res.HouseOnLava = lavaCols == W;
			res.HouseLavaCols = lavaCols;
			// 选起点时是按【预估行】算的岩浆,这里是线上真实的 ys[0]。两者该一致,
			// 不一致就说明打分打在了没人去的行上 —— 报出来,别让"房子不在岩浆上"再无声溜过去
			if (lavaCols < W)
				DiagLog.Write($"[hell-line] 房子({res.HouseX},{res.HouseY})只有{lavaCols}/{W}列在岩浆上");
		}
	}
}
