using System.Collections.Generic;
using System.Text;
using Terraria;

namespace TerraBlind
{
	// HOUSE — 盖房子的唯一编排。大房子和肉山桥那间单间是同一个东西,只差间数:
	//   Rooms=4 → 21 宽 4 间(地表那座);Rooms=1 → 6 宽单间(地狱桥起点)
	//
	// 形状(每间 5 格宽,加最右边一根柱子 = 5*Rooms+1 列):
	//   铺地板 → 最右端 pillar → 跳上柱顶 → 往回铺屋顶 → 掉下来 → 每间一根支柱
	//   → 放工作台 → 合成家具和墙 → 摆家具 → 一间一间铺墙
	//
	// "支柱"和单间的"第二根柱子"是同一样东西 —— 所以不需要两套流程。
	//
	// 编排以前在 python(_build_house),坐标在两边各推了一遍,于是同一个 off-by-one 反复出现
	// (柱子歪一格、屋顶铺在半空、pillar_top 算错)。现在只有这一份。
	//
	// 每一步启动一个已有的异步原语,等它报完成再进下一步 —— 这里不重新实现任何动作。
	public static class HouseBuilder
	{
		private enum Ph
		{
			Idle, Lift, SeedFloor, HopFloor, Floor,
			MainPillar, SettleBelow, HopTop, SettleTop, Roof, MoveOver, Drop,
			SupportSettle, Support, Bench, Craft, Furniture, WallSettle, WallHop, Walls,
			Done
		}

		private static Ph _ph = Ph.Idle;
		private static int _dir = 1;
		private static int _x0, _ay;              // 房子矩形的左下角(选址给的)
		private static int _floorRow;             // 地板实际所在行
		private static int _rooms = 1;
		private static int _waited, _hopTries, _liftTries;
		private static int _roomIdx;              // 正在处理第几间(支柱/铺墙都按间走)
		private static int _supIdx;               // 这一间的第几根支柱

		public const int RoomWidth = 5;           // 每间宽度
		public const int PillarH = 9;             // 主柱高
		public const int SupportH = 8;            // 支柱高
		private const int MaxHopTries = 12;
		private const int MaxLift = 40;
		private const int StepTimeout = 60 * 120;

		public static bool IsRunning => _ph != Ph.Idle && _ph != Ph.Done;
		public static string Outcome = "idle";
		public static string Reason = "";

		public static int Width => RoomWidth * _rooms + 1;     // 地板总长(列数)

		// 家具:大房子 3 桌 4 椅,单间 1 工作台 1 椅子(玩家定的,不是推出来的)
		static int TableCount => _rooms >= 4 ? 3 : 0;
		static int ChairCount => _rooms >= 4 ? 4 : 1;
		static int WallCount => _rooms * 24;

		public static bool Start(int rooms, int dir, int ax, int ay, out string why)
		{
			why = "";
			var p = Main.LocalPlayer;
			if (p == null) { why = "no_player"; return false; }
			_rooms = rooms < 1 ? 1 : rooms;
			_dir = dir >= 0 ? 1 : -1;
			_x0 = ax; _ay = ay;
			_floorRow = ay;
			_waited = 0; _hopTries = 0; _liftTries = 0; _roomIdx = 0; _supIdx = 0;
			Outcome = "running"; Reason = "";
			DiagLog.Write($"[house] start rooms={_rooms} dir={_dir} corner=({ax},{ay}) width={Width}");
			// 先对齐到左下角那一列:nav 容差 1.5 格,后面每一步都拿 _x0 当锚点。
			_ph = Ph.Lift;
			SettleAt.Start(_x0, out _);
			return true;
		}

		// 从人当前位置开工(测试键用):脚下那格当左下角。
		public static bool StartHere(int rooms, int dir, out string why)
		{
			var p = Main.LocalPlayer;
			if (p == null) { why = "no_player"; return false; }
			return Start(rooms, dir, ActExecutor.OriginCx(p), ActExecutor.OriginCy(p) + 1, out why);
		}

		public static void Stop()
		{
			if (Outcome == "running") Outcome = "stopped";
			_ph = Ph.Idle;
			BridgeBuilder.Stop(); PillarUp.Stop(); HopUp.Stop(); DropDown.Stop();
			SettleAt.Stop(); PlaceAction.Stop(); PlaceWalls.Stop(); WalkPlace.Stop();
		}

		// 地板要实心方块(站的地面),柱子屋顶用平台(能穿过去,盖的时候不挡自己)。
		// 按背包里存量最多的挑,不写死物品 —— 地狱里挖出来的地狱岩一样能用。
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
				if (wantSolid) { if (isPlat || !Main.tileSolid[it.createTile]) continue; }
				else if (!isPlat) continue;
				if (it.stack > bestStack) { bestStack = it.stack; best = it.Name; }
			}
			return best ?? (wantSolid ? "木材" : "木平台");
		}

		// 物品名按内部名查 —— createItem.Name 在中文环境返回中文,拿去匹配会失败。
		static string ItemName(int id)
			=> Terraria.ID.ItemID.Search.ContainsId(id) ? Terraria.ID.ItemID.Search.GetName(id) : id.ToString();

		static int Col(int k) => _x0 + _dir * k;          // 相对左下角第 k 列
		static int MainCol => Col(Width);                  // 最右那根主柱
		static int PillarTop => _floorRow - (PillarH - 1); // 照抄 _build_house

		// 第 r 间的支柱列:每间左边那根。间 0 → 第1列,间 1 → 第6列 …
		static int SupportCol(int r) => Col(1 + RoomWidth * r);

		public static void Tick()
		{
			if (!IsRunning) return;
			var p = Main.LocalPlayer;
			if (p == null || !p.active) { Fail("no_player"); return; }
			if (++_waited > StepTimeout) { Fail($"timeout_at_{_ph}"); return; }

			switch (_ph)
			{
				// ── 先站到左下角那一列,再垫到地板下面那一行 ────────────────────────
				case Ph.Lift:
				{
					if (SettleAt.IsRunning || PillarUp.IsRunning || HopUp.IsRunning) return;
					if (ActExecutor.OriginCx(p) != _x0 && _liftTries == 0)
					{ Fail($"站不到左下角那一列(要{_x0},在{ActExecutor.OriginCx(p)})"); return; }
					int cy = ActExecutor.OriginCy(p);
					if (cy <= _ay + 1)   // 地板放在 _ay,人得站在 _ay+1 才够得着往上放
					{
						_floorRow = _ay;
						Advance(Ph.SeedFloor);
						PlaceAction.Start(SolidName(), _x0, _ay, 1, 0, 0, true, out _);
						return;
					}
					if (++_liftTries > MaxLift) { Fail($"垫了{MaxLift}次还没到 {_ay + 1}"); return; }
					// col 不传:让 PillarUp 自己把身体从这一列让开
					PillarUp.Start(PlatName(), 1, -1, out _);
					_ph = Ph.Lift; _waited = 0;
					return;
				}

				case Ph.SeedFloor:
					if (PlaceAction.IsRunning) return;
					// bridge 是"站在已铺好的地板上往外接",起点这格得先有
					if (!Predicates.InBounds(_x0, _ay) || !Main.tile[_x0, _ay].HasTile)
					{ Fail($"({_x0},{_ay}) 没放上地板"); return; }
					Advance(Ph.HopFloor);
					HopUp.Start(_ay, _x0, out _);
					return;

				case Ph.HopFloor:
					if (HopUp.IsRunning) return;
					if (ActExecutor.OriginCy(p) != _ay - 1)
					{
						if (++_hopTries > MaxHopTries) { Fail($"没站上地板:cy={ActExecutor.OriginCy(p)} 应为 {_ay - 1}"); return; }
						HopUp.Start(_ay, _x0, out _);
						return;
					}
					_hopTries = 0;
					Advance(Ph.Floor);
					BridgeBuilder.Start(SolidName(), _dir > 0 ? "right" : "left", Width, out _);
					return;

				case Ph.Floor:
					if (BridgeBuilder.IsRunning) return;
					if (BridgeBuilder.Outcome != "done") { Fail($"地板:{BridgeBuilder.Outcome}/{BridgeBuilder.Reason}"); return; }
					// 砌柱子前【不要】站到那一列:传了 col 的 PillarUp 不会 StepAside,身体占着就砌不上
					Advance(Ph.MainPillar);
					PillarUp.Start(PlatName(), PillarH, MainCol, out _);
					return;

				case Ph.MainPillar:
					if (PillarUp.IsRunning) return;
					if (PillarUp.Outcome != "done") { Fail($"主柱:{PillarUp.Outcome}/{PillarUp.Reason}"); return; }
					Advance(Ph.SettleBelow);
					SettleAt.Start(MainCol, out _);
					return;

				case Ph.SettleBelow:
					if (SettleAt.IsRunning) return;
					Advance(Ph.HopTop);
					HopUp.Start(PillarTop, MainCol, out _);
					return;

				case Ph.HopTop:
					if (HopUp.IsRunning) return;
					Advance(Ph.SettleTop);
					SettleAt.Start(MainCol, out _);
					return;

				case Ph.SettleTop:
					if (SettleAt.IsRunning) return;
					// 判据照抄 _build_house:cx == 柱列 且 cy <= pillar_top-1
					if (ActExecutor.OriginCx(p) != MainCol || ActExecutor.OriginCy(p) > PillarTop - 1)
					{
						if (++_hopTries > MaxHopTries)
						{ Fail($"没上到柱顶:({ActExecutor.OriginCx(p)},{ActExecutor.OriginCy(p)})"); return; }
						_ph = Ph.HopTop; _waited = 0;
						HopUp.Start(PillarTop, MainCol, out _);
						return;
					}
					_hopTries = 0;
					// 屋顶沿人现在脚下那一行铺(照抄 roof_row = o["cy"]+1),不用公式
					Advance(Ph.Roof);
					BridgeBuilder.Start(PlatName(), _dir > 0 ? "left" : "right", Width - 1, out _);
					return;

				case Ph.Roof:
					if (BridgeBuilder.IsRunning) return;
					if (BridgeBuilder.Outcome != "done") { Fail($"屋顶:{BridgeBuilder.Outcome}/{BridgeBuilder.Reason}"); return; }
					// 铺完屋顶横移 2 格再掉下去 —— 落点就在支柱附近,而且不会站进要砌的那一格
					Advance(Ph.MoveOver);
					SettleAt.Start(ActExecutor.OriginCx(p) - _dir * 2, out _);
					return;

				case Ph.MoveOver:
					if (SettleAt.IsRunning) return;
					Advance(Ph.Drop);
					DropDown.Start(out _);
					return;

				// ── 每间一根支柱。站到支柱旁边(不是支柱上)再砌 ────────────────────
				case Ph.Drop:
					if (DropDown.IsRunning) return;
					_roomIdx = 0;
					Advance(Ph.SupportSettle);
					SettleAt.Start(SupportCol(0) + _dir * 2, out _);
					return;

				case Ph.SupportSettle:
					if (SettleAt.IsRunning) return;
					Advance(Ph.Support);
					PillarUp.Start(PlatName(), SupportH, SupportCol(_roomIdx), out _);
					return;

				case Ph.Support:
					if (PillarUp.IsRunning) return;
					if (PillarUp.Outcome != "done") { Fail($"支柱{_roomIdx}:{PillarUp.Outcome}/{PillarUp.Reason}"); return; }
					if (++_roomIdx < _rooms)
					{
						Advance(Ph.SupportSettle);
						SettleAt.Start(SupportCol(_roomIdx) + _dir * 2, out _);
						return;
					}
					// 支柱都砌完 → 放工作台(椅子和墙都要它才能合成)
					Advance(Ph.Bench);
					{
						int benchCol = Col(Width - 2);
						WalkPlace.Start(benchCol, new List<(int, int, string)>
						{ (benchCol, _floorRow - 1, ItemName(Terraria.ID.ItemID.WorkBench)) }, out _);
					}
					return;

				case Ph.Bench:
					if (WalkPlace.IsRunning) return;
					if (!Main.tile[Col(Width - 2), _floorRow - 1].HasTile)
					{ Fail($"工作台没放上 ({Col(Width - 2)},{_floorRow - 1})"); return; }
					Advance(Ph.Craft);
					return;

				case Ph.Craft:
				{
					// 工作台放下后配方要几帧才出现,所以这一步等它出现再合成
					bool ready = false;
					for (int ri = 0; ri < Main.numAvailableRecipes; ri++)
						if (Main.recipe[Main.availableRecipe[ri]].createItem.type == Terraria.ID.ItemID.WoodenChair)
						{ ready = true; break; }
					if (!ready) return;      // 等,由 StepTimeout 兜底

					if (TableCount > 0)
					{
						int tables = Predicates.Have(Terraria.ID.ItemID.WoodenTable);
						if (tables < TableCount) CraftCoordinator.Craft(Terraria.ID.ItemID.WoodenTable, TableCount - tables);
					}
					int chairs = Predicates.Have(Terraria.ID.ItemID.WoodenChair);
					if (chairs < ChairCount) CraftCoordinator.Craft(Terraria.ID.ItemID.WoodenChair, ChairCount - chairs);
					int walls = Predicates.Have(Terraria.ID.ItemID.WoodWall);
					if (walls < WallCount) CraftCoordinator.Craft(Terraria.ID.ItemID.WoodWall, WallCount - walls);

					// 合不出来就如实失败 —— CraftCoordinator 只数真进了背包的
					if (Predicates.Have(Terraria.ID.ItemID.WoodenChair) < ChairCount)
					{ Fail($"椅子只有 {Predicates.Have(Terraria.ID.ItemID.WoodenChair)}/{ChairCount}"); return; }
					if (Predicates.Have(Terraria.ID.ItemID.WoodWall) < WallCount)
					{ Fail($"木墙只有 {Predicates.Have(Terraria.ID.ItemID.WoodWall)}/{WallCount}"); return; }

					Advance(Ph.Furniture);
					var targets = new List<(int, int, string)>();
					for (int i = 0; i < TableCount; i++)
						targets.Add((Col(Width - 7 - RoomWidth * i), _floorRow - 1, ItemName(Terraria.ID.ItemID.WoodenTable)));
					for (int i = 0; i < ChairCount; i++)
						targets.Add((Col(Width - 4 - RoomWidth * i), _floorRow - 1, ItemName(Terraria.ID.ItemID.WoodenChair)));
					WalkPlace.Start(Col(1), targets, out _);
					return;
				}

				case Ph.Furniture:
					if (WalkPlace.IsRunning) return;
					_roomIdx = 0;
					Advance(Ph.WallSettle);
					SettleAt.Start(SupportCol(0) + _dir * 3, out _);
					return;

				// ── 一间一间铺墙:站到那一间中间,跳回地板层,铺 ──────────────────
				case Ph.WallSettle:
					if (SettleAt.IsRunning) return;
					Advance(Ph.WallHop);
					HopUp.Start(_floorRow, SupportCol(_roomIdx) + _dir * 3, out _);
					return;

				case Ph.WallHop:
					if (HopUp.IsRunning) return;
					Advance(Ph.Walls);
					StartWalls(_roomIdx);
					return;

				case Ph.Walls:
					if (PlaceWalls.IsRunning) return;
					if (++_roomIdx < _rooms)
					{
						Advance(Ph.WallSettle);
						SettleAt.Start(SupportCol(_roomIdx) + _dir * 3, out _);
						return;
					}
					Outcome = "done"; _ph = Ph.Done;
					DiagLog.Write($"[house] done rooms={_rooms} x0={_x0} floor_row={_floorRow}");
					Main.NewText($"[TerraBlind] 房子盖好了 ({_x0},{_floorRow}) {_rooms}间", 120, 255, 120);
					return;
			}
		}

		// 第 r 间的内腔:两根柱子之间、地板上一行到屋顶下一行。
		// 顺序照抄 _build_house 的 H_WALL_ORDER —— vanilla 的墙体合并依赖放置顺序。
		static readonly (int dr, int dc)[] WallOrder =
		{
			(1,2),(2,2),(3,2),(4,2),(5,2),(6,2),
			(6,3),(6,4),(6,5),
			(5,5),(4,5),(3,5),(2,5),(1,5),
			(1,4),(1,3),
			(2,3),(3,4),(4,3),(5,4),
		};

		static void StartWalls(int room)
		{
			int roofRow = _floorRow - PillarH;
			int col1 = 1 + RoomWidth * room;
			var cells = new List<(int, int)>();
			foreach (var (dr, dc) in WallOrder)
				cells.Add((Col(col1 + (dc - 1)), roofRow + dr));
			PlaceWalls.Start(ItemName(Terraria.ID.ItemID.WoodWall), cells, out _);
		}

		static void Advance(Ph next)
		{
			_ph = next; _waited = 0;
			DiagLog.Write($"[house] → {next}" + (_rooms > 1 ? $" room={_roomIdx}" : ""));
		}

		static void Fail(string why)
		{
			Reason = why; Outcome = "stuck"; _ph = Ph.Done;
			DiagLog.Write($"[house] FAIL {why}");
			Main.NewText($"[TerraBlind] 盖房失败: {why}", 255, 120, 120);
		}

		public static string StatusJson()
		{
			var sb = new StringBuilder();
			sb.Append("{\"outcome\":\"").Append(Outcome).Append('"')
			  .Append(",\"running\":").Append(IsRunning ? "true" : "false")
			  .Append(",\"phase\":\"").Append(_ph.ToString().ToLowerInvariant()).Append('"')
			  .Append(",\"reason\":\"").Append(JsonEsc(Reason)).Append('"')
			  .Append(",\"rooms\":").Append(_rooms)
			  .Append(",\"room_idx\":").Append(_roomIdx)
			  .Append(",\"x0\":").Append(_x0).Append(",\"floor_row\":").Append(_floorRow)
			  .Append(",\"width\":").Append(Width)
			  .Append(",\"dir\":").Append(_dir).Append('}');
			return sb.ToString();
		}

		static string JsonEsc(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
	}
}
