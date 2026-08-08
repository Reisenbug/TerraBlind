using System.Text;
using Terraria;

namespace TerraBlind
{
	// SMALL ROOM — 6 宽的单间,肉山桥起点那一间。大房子(21×10)的简化版,形状和步骤都是玩家定的:
	//   铺 6 格地板 → 右端 pillar 9 格 → 跳上柱顶 → 往回搭 5 格屋顶 → 掉下去 → 左端 pillar 8 格
	//   → 放工作台和椅子 → 铺墙
	//
	// 左端那根柱子是"掉下去再往上砌"而不是"从上往下砌":手臂够不到脚下 8 格,而 PillarUp 本来就
	// 是往上长的。跟平时盖房一个路子。
	//
	// 每一步都启动一个已有的异步原语,等它报完成再进下一步 —— 这里只做编排,不重新实现任何动作。
	public static class SmallRoom
	{
		private enum Ph
		{
			Idle, Floor, RightPillar, HopTop, Roof, Drop, LeftPillar,
			Bench, Chair, Walls, Done
		}

		private static Ph _ph = Ph.Idle;
		private static int _dir = 1;              // 房间往哪边铺(+1 右)
		private static int _x0, _floorRow;        // 起点列 / 地板所在行
		private static int _waited;

		public const int Width = 6;
		public const int PillarH = 9;
		public const int RoofLen = 5;
		public const int LeftPillarH = 8;
		private const int StepTimeout = 60 * 90;  // 单步最长 90 秒,防止某个原语不报完成就悬着

		public static bool IsRunning => _ph != Ph.Idle && _ph != Ph.Done;
		public static string Outcome = "idle";
		public static string Reason = "";

		public static bool Start(int dir, out string why)
		{
			why = "";
			var p = Main.LocalPlayer;
			if (p == null) { why = "no_player"; return false; }
			_dir = dir >= 0 ? 1 : -1;
			_x0 = ActExecutor.OriginCx(p);
			_floorRow = ActExecutor.OriginCy(p) + 1;
			_waited = 0;
			Outcome = "running"; Reason = "";
			_ph = Ph.Floor;
			DiagLog.Write($"[room] start dir={_dir} x0={_x0} floor_row={_floorRow}");
			BridgeBuilder.Start(PlatformName(), _dir > 0 ? "right" : "left", Width, out _);
			return true;
		}

		public static void Stop()
		{
			if (Outcome == "running") Outcome = "stopped";
			_ph = Ph.Idle;
			BridgeBuilder.Stop(); PillarUp.Stop(); HopUp.Stop(); DropDown.Stop();
			PlaceAction.Stop(); PlaceWalls.Stop();
		}

		// 手上有什么就用什么:地狱里木材未必够,挖出来的地狱岩一样能铺。
		static string PlatformName()
		{
			var p = Main.LocalPlayer;
			var td = Terraria.ID.TileID.Sets.Platforms;
			for (int i = 0; i < p.inventory.Length; i++)
			{
				var it = p.inventory[i];
				if (it == null || it.IsAir || it.createTile < 0) continue;
				if (td != null && it.createTile < td.Length && td[it.createTile]) return it.Name;
			}
			for (int i = 0; i < p.inventory.Length; i++)
			{
				var it = p.inventory[i];
				if (it != null && !it.IsAir && it.createTile >= 0 && it.stack >= Width) return it.Name;
			}
			return "木平台";
		}

		static int RightCol => _x0 + _dir * (Width - 1);

		public static void Tick()
		{
			if (!IsRunning) return;
			var p = Main.LocalPlayer;
			if (p == null || !p.active) { Fail("no_player"); return; }
			if (++_waited > StepTimeout) { Fail($"timeout_at_{_ph}"); return; }

			switch (_ph)
			{
				case Ph.Floor:
					if (BridgeBuilder.IsRunning) return;
					if (BridgeBuilder.Outcome != "done") { Fail($"floor:{BridgeBuilder.Outcome}/{BridgeBuilder.Reason}"); return; }
					Advance(Ph.RightPillar);
					PillarUp.Start(PlatformName(), PillarH, RightCol, out _);
					return;

				case Ph.RightPillar:
					if (PillarUp.IsRunning) return;
					if (PillarUp.Outcome != "done") { Fail($"right_pillar:{PillarUp.Outcome}/{PillarUp.Reason}"); return; }
					Advance(Ph.HopTop);
					HopUp.Start(_floorRow - (PillarH - 1), RightCol, out _);
					return;

				case Ph.HopTop:
					if (HopUp.IsRunning) return;
					// 上没上去看脚下那一行,不看 outcome —— 跳不满也可能已经站上去了
					Advance(Ph.Roof);
					BridgeBuilder.Start(PlatformName(), _dir > 0 ? "left" : "right", RoofLen, out _);
					return;

				case Ph.Roof:
					if (BridgeBuilder.IsRunning) return;
					if (BridgeBuilder.Outcome != "done") { Fail($"roof:{BridgeBuilder.Outcome}/{BridgeBuilder.Reason}"); return; }
					Advance(Ph.Drop);
					DropDown.Start(out _);
					return;

				case Ph.Drop:
					if (DropDown.IsRunning) return;
					Advance(Ph.LeftPillar);
					PillarUp.Start(PlatformName(), LeftPillarH, _x0, out _);
					return;

				case Ph.LeftPillar:
					if (PillarUp.IsRunning) return;
					if (PillarUp.Outcome != "done") { Fail($"left_pillar:{PillarUp.Outcome}/{PillarUp.Reason}"); return; }
					Advance(Ph.Bench);
					PlaceAction.Start("工作台", _x0 + _dir * 2, _floorRow - 1, 1, 0, 0, true, out _);
					return;

				case Ph.Bench:
					if (PlaceAction.IsRunning) return;
					Advance(Ph.Chair);
					PlaceAction.Start("木椅", _x0 + _dir * 4, _floorRow - 1, 1, 0, 0, true, out _);
					return;

				case Ph.Chair:
					if (PlaceAction.IsRunning) return;
					Advance(Ph.Walls);
					StartWalls();
					return;

				case Ph.Walls:
					if (PlaceWalls.IsRunning) return;
					Outcome = "done"; _ph = Ph.Done;
					DiagLog.Write($"[room] done x0={_x0} floor_row={_floorRow}");
					Main.NewText($"[TerraBlind] 单间盖好了 ({_x0},{_floorRow})", 120, 255, 120);
					return;
			}
		}

		static void Advance(Ph next)
		{
			_ph = next; _waited = 0;
			DiagLog.Write($"[room] → {next}");
		}

		static void Fail(string why)
		{
			Reason = why; Outcome = "stuck"; _ph = Ph.Done;
			DiagLog.Write($"[room] FAIL {why}");
			Main.NewText($"[TerraBlind] 单间失败: {why}", 255, 120, 120);
		}

		// 墙铺在地板和屋顶之间的内腔:地板上一行到屋顶下一行,两根柱子之间。
		static void StartWalls()
		{
			var cells = new System.Collections.Generic.List<(int, int)>();
			int top = _floorRow - (PillarH - 1);
			for (int r = _floorRow - 1; r > top; r--)
				for (int k = 1; k < Width - 1; k++)
					cells.Add((_x0 + _dir * k, r));
			PlaceWalls.Start("木墙", cells, out _);
		}

		public static string StatusJson()
		{
			var sb = new StringBuilder();
			sb.Append("{\"outcome\":\"").Append(Outcome).Append('"')
			  .Append(",\"running\":").Append(IsRunning ? "true" : "false")
			  .Append(",\"phase\":\"").Append(_ph.ToString().ToLowerInvariant()).Append('"')
			  .Append(",\"reason\":\"").Append(Reason).Append('"')
			  .Append(",\"x0\":").Append(_x0).Append(",\"floor_row\":").Append(_floorRow)
			  .Append(",\"dir\":").Append(_dir).Append('}');
			return sb.ToString();
		}
	}
}
