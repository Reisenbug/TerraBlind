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
			Idle, Floor, RightPillar, SettleBelow, HopTop, SettleTop, Roof, Drop, SettleLeft, LeftPillar,
			Furniture, Walls, Done
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
			BridgeBuilder.Stop(); PillarUp.Stop(); HopUp.Stop(); DropDown.Stop(); SettleAt.Stop();
			PlaceAction.Stop(); PlaceWalls.Stop(); WalkPlace.Stop();
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
		// 照 _build_house:pillar_top = 地板行 - (柱高-1)
		static int PillarTop => _floorRow - (PillarH - 1);

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
					// 砌柱子之前【不要】站到那一列去:传了 col 的 PillarUp 不会 StepAside,
					// 身体占着那格就砌不上。站在地基上别处就行,顺序照搬 _build_house。
					Advance(Ph.RightPillar);
					PillarUp.Start(PlatName(), PillarH, RightCol, out _);
					return;

				case Ph.RightPillar:
					if (PillarUp.IsRunning) return;
					if (PillarUp.Outcome != "done") { Fail($"right_pillar:{PillarUp.Outcome}/{PillarUp.Reason}"); return; }
					// 砌完了才站到柱子底下,再往上跳。
					Advance(Ph.SettleBelow);
					SettleAt.Start(RightCol, out _);
					return;

				case Ph.SettleBelow:
					if (SettleAt.IsRunning) return;
					Advance(Ph.HopTop);
					HopUp.Start(PillarTop, RightCol, out _);
					return;

				case Ph.HopTop:
					if (HopUp.IsRunning) return;
					Advance(Ph.SettleTop);
					SettleAt.Start(RightCol, out _);
					return;

				case Ph.SettleTop:
					if (SettleAt.IsRunning) return;
					// 判据照抄 _build_house:cx == 柱列 且 cy <= pillar_top-1 就是上去了。
					// 之前我自己推了另一套行号,人明明已经站在柱顶还被判成没到,于是一直跳。
					if (ActExecutor.OriginCx(p) != RightCol || ActExecutor.OriginCy(p) > PillarTop - 1)
					{
						if (++_hopTries > MaxHopTries)
						{ Fail($"没上到柱顶:({ActExecutor.OriginCx(p)},{ActExecutor.OriginCy(p)})"); return; }
						_ph = Ph.HopTop; _waited = 0;
						HopUp.Start(PillarTop, RightCol, out _);
						return;
					}
					_hopTries = 0;
					// 屋顶沿【人现在脚下那一行】走 —— 和 _build_house 的 roof_row = o["cy"]+1 一样,
					// 用实际站位,不用公式。
					Advance(Ph.Roof);
					BridgeBuilder.Start(PlatName(), _dir > 0 ? "left" : "right", RoofLen, out _);
					return;

				case Ph.Roof:
					if (BridgeBuilder.IsRunning) return;
					if (BridgeBuilder.Outcome != "done") { Fail($"roof:{BridgeBuilder.Outcome}/{BridgeBuilder.Reason}"); return; }
					Advance(Ph.Drop);
					DropDown.Start(out _);
					return;

				case Ph.Drop:
					if (DropDown.IsRunning) return;
					// 屋顶只有 RoofLen 格,铺完人停在屋顶那一端,离 _x0 还差一截 —— 掉下来直接砌
					// 左柱是够不着的。先走到贴着 _x0 内侧那一格:够得着 _x0,又没占着它
					// (占着的话传了 col 的 PillarUp 不会 StepAside,砌不上)。
					Advance(Ph.SettleLeft);
					SettleAt.Start(_x0 + _dir, out _);
					return;

				case Ph.SettleLeft:
					if (SettleAt.IsRunning) return;
					Advance(Ph.LeftPillar);
					PillarUp.Start(PlatName(), LeftPillarH, _x0, out _);
					return;

				case Ph.LeftPillar:
					if (PillarUp.IsRunning) return;
					if (PillarUp.Outcome != "done") { Fail($"left_pillar:{PillarUp.Outcome}/{PillarUp.Reason}"); return; }
					// 家具用 WalkPlace 不用 PlaceAction:砌完柱子人在 _x0 那头,家具在房间中间,
					// 站着够不着。WalkPlace 会边走边放,路过够得着就放 —— _build_house 也是这么摆的。
					Advance(Ph.Furniture);
					{
						var targets = new System.Collections.Generic.List<(int, int, string)>
						{
							(_x0 + _dir * 2, _floorRow - 1, "工作台"),
							(_x0 + _dir * 4, _floorRow - 1, "木椅"),
						};
						WalkPlace.Start(_x0 + _dir * (Width - 1), targets, out _);
					}
					return;

				case Ph.Furniture:
					if (WalkPlace.IsRunning) return;
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
