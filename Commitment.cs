using System.Collections.Generic;
using Terraria;

namespace TerraBlind
{
	// COMMITMENT — the fix for greedy's short memory.
	//
	// Receding selection re-asks "which neighbour has the lowest H?" every single cycle, having forgotten why it
	// walked here. In a basin that question flip-flops: at H=366 the best neighbour is 367, and from 367 the best
	// neighbour is 366. Every step is locally optimal and the pair goes nowhere. Meanwhile all three ways out that
	// a human sees at a glance — drop down the shaft, climb west, mine through — start by making H WORSE, so
	// per-step selection rejects every one of them, forever.
	//
	// A human escapes not by evaluating better but by DECIDING ONCE: "I'm going down," and then going down even
	// though it looks worse halfway. So: when the loop detector fires, pick a target that is genuinely better —
	// the nearest cell whose H is meaningfully below here — and commit to reaching it. Until it is reached, the
	// planner routes to THAT cell and rising H along the way is not a reason to reconsider.
	//
	// Only two things end a commitment early: arriving, or failing to make progress toward it. Note what is NOT on
	// that list — H getting worse. That is expected; it is the entire point.
	public static class Commitment
	{
		public static bool Active { get; private set; }
		public static int Gx { get; private set; }
		public static int Gy { get; private set; }

		static int _startH, _targetH;
		static int _cycles;            // cycles spent on this commitment
		static int _budget;            // give up after this many
		static int _bestDist;          // closest we have come to the target
		static int _sinceProgress;

		// 远处那些 H 极低的格【隔着走不通的墙】:承诺 (1292,238) 54 格外,人在 1344..1348 弹了
		// 10 轮,dist 55→60 从没靠近。承诺的意义是"跨出当前这个坑",不是"直奔全场最低点" ---
		// 跨出去之后场自然会重新指路。所以只在近处找。
		const int MaxRadius = 14;
		const int FailRadius = 3;      // 一个目标到不了,它周围这一圈也别再试了
		// A target that cannot be reached shows itself fast: distance oscillated 14→10→14→10 for twenty cycles
		// against the first commitment, closing to 10 and bouncing back every time. Ten cycles of no new best is
		// already conclusive — the point of committing is to stop dithering, not to keep failing longer.
		const int StaleCycles = 10;

		static readonly HashSet<(int, int)> _failed = new();

		public static void Clear()
		{
			if (Active) DiagLog.Write($"[commit] cleared (was →({Gx},{Gy}))");
			Active = false; _cycles = 0; _sinceProgress = 0;
		}

		public static void Reset() { Clear(); _failed.Clear(); }

		// Look for somewhere worth committing to. Judge candidates by FIELD COST, not by how close they look:
		// straight-line distance knows nothing about what lies between. Committing to the nearest qualifying cell
		// picked (977,550) — ten cells west, through terrain the body could not cross — and burned ten cycles
		// bouncing off it before falling back to (987,535), twelve cells straight up, which was reached in six.
		//
		// H already prices the crossing: it is the field's own cost-to-goal, so a cell whose H is far below here is
		// one the field believes is genuinely progress, and cheap H-drop per cell of travel means the route there
		// is real rather than a wall seen from ten cells away.
		public static bool Begin(int curCx, int curCy, int curH)
		{
			var field = MazeWand.PeekField();
			if (field == null) return false;
			// One pass over the field, not a ring scan outward: the field is a dictionary of the cells that exist,
			// while scanning rings visits every coordinate in a 121×121 box — ~900k lookups on the main thread,
			// each calling CanStand, which is its own three tile reads. Iterate what is there and keep the nearest
			// qualifying cell. CanStand is checked LAST, only for cells that already pass the cheap tests.
			// 【完全不看 H】。H 在这个坑里指的就是死方向 --- 承诺再拿 H 选目标,
			// 等于继承同一个错误:上一版挑了 (1342,239)(H 更低,西南),而唯一出口在西北、H 更高。
			// 承诺要的是"跨出这个坑",判据只有三条:没去过、站得住、近。
			int bestX = 0, bestY = 0, bestD = 0, bestH = 0;
			float bestScore = float.MaxValue;
			foreach (var kv in field)
			{
				if (_failed.Contains(kv.Key)) continue;
				int dx = kv.Key.Item1 - curCx, dy = kv.Key.Item2 - curCy;
				if (dx > MaxRadius || dx < -MaxRadius || dy > MaxRadius || dy < -MaxRadius) continue;
				int d = System.Math.Abs(dx) + System.Math.Abs(dy);
				if (d == 0) continue;
				// 去过【不是】排除条件 --- 唯一的出口 (1345,230) 正好去过,加这条就把它筛没了,
				// 于是承诺把整条走廊 1284..1346 试了个遍,全在坑外,一个都到不了。
				// 去过只作同分时的次选。
				float sc = d + (StateSpacePlanner.WasVisited(kv.Key.Item1, kv.Key.Item2) ? 0.5f : 0f);
				if (sc >= bestScore) continue;
				if (!CellKind.Stands(kv.Key.Item1, kv.Key.Item2)) continue;
				bestScore = sc; bestD = d; bestX = kv.Key.Item1; bestY = kv.Key.Item2; bestH = kv.Value;
			}
			bool found = bestScore != float.MaxValue;
			if (!found)
			{
				DiagLog.Write($"[commit] no target within {MaxRadius} of ({curCx},{curCy})H={curH}");
				return false;
			}
			Active = true; Gx = bestX; Gy = bestY;
			_startH = curH; _targetH = bestH;
			_cycles = 0; _sinceProgress = 0; _bestDist = bestD;
			// budget scales with distance: a target 8 cells off should not get the same allowance as one 40 away
			_budget = 20 + bestD * 6;
			DiagLog.Write($"[commit] BEGIN ({curCx},{curCy})H={curH} → ({Gx},{Gy})H={bestH} dist={bestD} drop={curH - bestH} budget={_budget}");
			return true;
		}

		// Called once per replan while committed. Returns false when the commitment is over and normal
		// goal-directed selection should resume.
		public static bool Tick(int curCx, int curCy)
		{
			if (!Active) return false;
			int d = System.Math.Abs(curCx - Gx) + System.Math.Abs(curCy - Gy);
			if (d <= 1)
			{
				DiagLog.Write($"[commit] REACHED ({Gx},{Gy}) in {_cycles} cycles");
				_failed.Clear();   // getting somewhere clears the doubts — the terrain reads differently from here
				Clear();
				return false;
			}
			// progress is measured against the TARGET, not against H — H is allowed to get worse the whole way
			if (d < _bestDist) { _bestDist = d; _sinceProgress = 0; }
			else _sinceProgress++;
			_cycles++;
			DiagLog.Write($"[commit] →({Gx},{Gy}) from ({curCx},{curCy}) dist={d} best={_bestDist} cycle={_cycles}/{_budget} stale={_sinceProgress}");
			if (_sinceProgress >= StaleCycles || _cycles >= _budget)
			{
				DiagLog.Write($"[commit] ABANDON ({Gx},{Gy}) dist={d} best={_bestDist} cycles={_cycles}/{_budget} stale={_sinceProgress}");
				// 【连片拉黑】。只黑一个点没用:到不了的是整片区域,下一轮就挑隔壁一格,
				// 同一个方向再耗 10 轮 --- 日志里十个目标九个都在西南那一带,轮着来把预算耗光,
				// 而唯一的出口只因为远 1 格,一次都没轮上。
				for (int fx = -FailRadius; fx <= FailRadius; fx++)
					for (int fy = -FailRadius; fy <= FailRadius; fy++)
						_failed.Add((Gx + fx, Gy + fy));
				Clear();
				return false;
			}
			return true;
		}
	}
}
