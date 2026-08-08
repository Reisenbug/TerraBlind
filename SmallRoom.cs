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
		private static int _hopTries;
		private const int MaxHopTries = 12;

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
			_waited = 0; _hopTries = 0;
			Outcome = "running"; Reason = "";
			_ph = Ph.Floor;
			DiagLog.Write($"[room] start dir={_dir} x0={_x0} floor_row={_floorRow}");
			BridgeBuilder.Start(SolidName(), _dir > 0 ? "right" : "left", Width, out _);
			return true;
		}

		public static void Stop()
		{
			if (Outcome == "running") Outcome = "stopped";
			_ph = Ph.Idle;
			BridgeBuilder.Stop(); PillarUp.Stop(); HopUp.Stop(); DropDown.Stop();
			PlaceAction.Stop(); PlaceWalls.Stop();
		}

		// 地板要实心方块(人站的地面),柱子和屋顶用平台 —— 平台能穿过去,盖的时候不挡自己。
		// 地狱里木材未必够,挖出来的地狱岩一样能用,所以按"背包里存量最多的"挑,不写死物品。
		static string SolidName() => PickItem(true);
		static string PlatName() => PickItem(false);

		static string PickItem(bool wantSolid)
		{
			var p = Main.LocalPlayer;
			var td = Terraria.ID.TileID.Sets.Platforms;
			string best = null; int bestStack = 0;
			for (int i = 0; i < p.inventory.Length; i++)
			{
				var it = p.inventory[i];
				if (it == null || it.IsAir || it.createTile < 0) continue;
				bool isPlat = td != null && it.createTile < td.Length && td[it.createTile];
				if (wantSolid)
				{
					// 实心地面:平台不算(会被穿过去),家具火把之类也不算
					if (isPlat || !Main.tileSolid[it.createTile]) continue;
				}
				else if (!isPlat) continue;
				if (it.stack > bestStack) { bestStack = it.stack; best = it.Name; }
			}
			return best ?? (wantSolid ? "木材" : "木平台");
		}

		// 地板是从 _x0+dir 开始铺 Width 格(BridgeBuilder 不铺人脚下那格),所以最后一格是 _x0+dir*Width。
		// 以前按 _x0+dir*(Width-1) 算,柱子就歪进了地板里一格。
		static int RightCol => _x0 + _dir * Width;

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
					PillarUp.Start(PlatName(), PillarH, RightCol, out _);
					return;

				case Ph.RightPillar:
					if (PillarUp.IsRunning) return;
					if (PillarUp.Outcome != "done") { Fail($"right_pillar:{PillarUp.Outcome}/{PillarUp.Reason}"); return; }
					Advance(Ph.HopTop);
					HopUp.Start(_floorRow - 1 - PillarH, RightCol, out _);
					return;

				case Ph.HopTop:
				{
					if (HopUp.IsRunning) return;
					// 屋顶铺在"人当时脚下那一行",所以人没真上到柱顶就铺,屋顶就长在半空的错误高度上
					// (实测 row=205 和 207,柱顶其实是 200)。这里必须验高度,不能直接往下走。
					// PillarUp 的第一块砌在【人自己那格】(_baseWy = OriginCy),所以 n 块的顶块在
					// OriginCy-(n-1),站上去就是 OriginCy-n。人开工时站在 _floorRow-1。
					int topRow = _floorRow - 1 - PillarH;
					int cy = ActExecutor.OriginCy(p);
					if (cy > topRow)
					{
						// 一次跳不满是正常的(9 格柱子要跳几次),再跳,别当失败。
						if (++_hopTries > MaxHopTries) { Fail($"hop_top:停在 {cy},要到 {topRow}"); return; }
						HopUp.Start(topRow, RightCol, out _);
						return;
					}
					_hopTries = 0;
					Advance(Ph.Roof);
					BridgeBuilder.Start(PlatName(), _dir > 0 ? "left" : "right", RoofLen, out _);
					return;
				}

				case Ph.Roof:
					if (BridgeBuilder.IsRunning) return;
					if (BridgeBuilder.Outcome != "done") { Fail($"roof:{BridgeBuilder.Outcome}/{BridgeBuilder.Reason}"); return; }
					Advance(Ph.Drop);
					DropDown.Start(out _);
					return;

				case Ph.Drop:
					if (DropDown.IsRunning) return;
					Advance(Ph.LeftPillar);
					PillarUp.Start(PlatName(), LeftPillarH, _x0, out _);
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
			// 内腔:地板上面一行 到 屋顶下面一行,左右柱子之间(都不含柱子本身)。
			var cells = new System.Collections.Generic.List<(int, int)>();
			int roofRow = _floorRow - 1 - PillarH;
			for (int r = _floorRow - 1; r > roofRow; r--)
				for (int k = 1; k < Width; k++)
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
