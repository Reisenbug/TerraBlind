using System.Collections.Generic;
using Terraria;

namespace TerraBlind
{
	// HELL DECK — 照着 HellLine 那条线逐格铺桥面。
	//
	// 不用 BridgeBuilder:那个只会沿【一行】平推,而线是有坡的。图上有起伏、有挖掘、
	// 有已存在的方块,平推出来的却是一条直线 —— 图和实物对不上就是这么来的。
	//
	// 每一格四种情况:已经是方块→跳过;空→放;实心但不是我要的→照放(物块替换);
	// 升降的拐角→多铺一格当锚点(斜着的两格贴不上,新方块要正交邻居)。
	public static class HellDeck
	{
		public struct Cell
		{
			public int X, Y;
			public bool Anchor;   // 拐角补的那一格,不是线本身
		}

		private static List<Cell> _plan = new List<Cell>();
		private static int _idx;
		private static string _item = "";
		private static int _frames, _cellFrames;
		private static bool _running;

		private const int MaxCellFrames = 240;

		public static bool IsRunning => _running;
		public static string Outcome = "idle";
		public static string Reason = "";
		public static int Placed;
		public static IReadOnlyList<Cell> Plan => _plan;
		public static int Index => _idx;

		// 把线展开成实际要铺的格子:线本身 + 每次升降的拐角锚点。
		// (0,0)→(1,1) 之间要在 (0,1) 或 (1,0) 有东西,不然 (1,1) 没地方贴。
		public static List<Cell> Expand(List<(int x, int y)> line)
		{
			var plan = new List<Cell>();
			for (int i = 0; i < line.Count; i++)
			{
				var (x, y) = line[i];
				if (i > 0)
				{
					var (px, py) = line[i - 1];
					if (py != y) plan.Add(new Cell { X = px, Y = y, Anchor = true });
				}
				plan.Add(new Cell { X = x, Y = y });
			}
			return plan;
		}

		public static bool Start(string itemName, List<(int x, int y)> line, out string why)
		{
			why = "";
			if (Main.LocalPlayer == null) { why = "no_player"; return false; }
			_item = itemName;
			_plan = Expand(line);
			_idx = 0; _frames = 0; _cellFrames = 0; Placed = 0;
			_running = true; Outcome = "running"; Reason = "";
			int have = 0;
			foreach (var c in _plan) if (Predicates.IsSolid(c.X, c.Y)) have++;
			DiagLog.Write($"[helldeck] START {_plan.Count}格(线{line.Count}+锚点{_plan.Count - line.Count}) 已有{have} 料={itemName}");
			return true;
		}

		public static void Stop()
		{
			if (Outcome == "running") Outcome = "stopped";
			_running = false;
		}

		public static void Tick()
		{
			if (!_running) return;
			var p = Main.LocalPlayer;
			if (p == null || !p.active) { Fail("no_player"); return; }
			if (++_frames > 60 * 900) { Fail("timeout"); return; }

			// 走完了
			while (_idx < _plan.Count && Predicates.IsSolid(_plan[_idx].X, _plan[_idx].Y))
			{ _idx++; _cellFrames = 0; }   // 已经有方块的直接跳过,不重复铺
			if (_idx >= _plan.Count)
			{
				Outcome = "done"; _running = false;
				DiagLog.Write($"[helldeck] DONE 铺了{Placed}格");
				return;
			}

			var cell = _plan[_idx];
			// 岩浆格放不进去(vanilla 只挡空格子),跳过它继续往前 —— 卡在这儿等没有意义
			if (Predicates.IsLava(cell.X, cell.Y))
			{
				DiagLog.Write($"[helldeck] ({cell.X},{cell.Y})是岩浆,跳过");
				_idx++; _cellFrames = 0; return;
			}

			if (++_cellFrames > MaxCellFrames)
			{
				DiagLog.Write($"[helldeck] ({cell.X},{cell.Y})铺不上,跳过 已铺{Placed}");
				_idx++; _cellFrames = 0; return;
			}

			// 够不着就先走过去:BridgeBuilder 那套"手够不着就挪脚"这里同样需要
			if (!p.IsInTileInteractionRange(cell.X, cell.Y, Terraria.DataStructures.TileReachCheckSettings.Simple))
			{
				int cx = ActExecutor.OriginCx(p);
				if (cx < cell.X) p.controlRight = true; else if (cx > cell.X) p.controlLeft = true;
				return;
			}
			if (!PlaceAction.IsRunning)
				PlaceAction.Start(_item, cell.X, cell.Y, 1, 0, 0, true, out _);
			if (Predicates.IsSolid(cell.X, cell.Y)) { Placed++; _idx++; _cellFrames = 0; }
		}

		static void Fail(string reason)
		{
			Outcome = "stuck"; Reason = reason; _running = false;
			DiagLog.Write($"[helldeck] STUCK {reason} 已铺{Placed}/{_plan.Count}");
		}
	}
}
