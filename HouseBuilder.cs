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
			SupportSettle, Support, BenchSettle, Bench, Craft, Tables, Chairs, WallSettle, WallHop, Walls, Torch,
			Done
		}

		private static Ph _ph = Ph.Idle;
		private static int _dir = 1;
		private static int _x0, _ay;              // 房子矩形的左下角(选址给的)
		private static int _floorRow;             // 地板实际所在行
		private static int _rooms = 1;
		private static int _waited, _hopTries, _liftTries;
		private static int _roomIdx;              // 正在处理第几间(支柱/铺墙都按间走)
		private static int _roofRow;              // python: roof_row = 上到柱顶后实际站位的 cy+1

		// 照抄 python 的 H_*:全部用数字 id。ResolveSlot 先 int.TryParse,能解析就按
		// item.type 精确匹配;否则拿去比 it.Name —— 那是本地化名(中文"工作台"),
		// 内部名 "WorkBench" 永远匹配不上,报 no_item。
		const int H_FLOOR = 94;       // 木平台
		const int H_WOOD = 9;         // 木材
		const int H_WORKBENCH = 36;
		const int H_TABLE = 32;
		const int H_CHAIR = 34;
		const int H_WALL = 93;        // 木墙
		const int H_TORCH = 8;

		public const int RoomWidth = 5;           // 每间宽度
		public const int PillarH = 9;             // 主柱高 (H_PILLAR)
		public const int SupportH = 8;            // 支柱高 (H_SUP)
		private const int MaxHopTries = 12;
		private const int MaxLift = 40;
		private const int StepTimeout = 60 * 120;

		public static bool IsRunning => _ph != Ph.Idle && _ph != Ph.Done;
		public static string Outcome = "idle";
		public static string Reason = "";

		public static int Width => RoomWidth * _rooms;         // bridge 铺几格(python: H_LEN=20)

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
			_waited = 0; _hopTries = 0; _liftTries = 0; _roomIdx = 0; _roofRow = 0;
			Outcome = "running"; Reason = "";
			DiagLog.Write($"[house] start rooms={_rooms} dir={_dir} corner=({ax},{ay}) width={Width}");
			// 先对齐到左下角那一列:nav 容差 1.5 格,后面每一步都拿 _x0 当锚点。
			_ph = Ph.Lift;
			if (!Need(SettleAt.Start(_x0, out string w0), "对齐左下角", w0)) return false;
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

		// python 里地基用 H_WOOD(木材),其它一律 H_FLOOR(木平台)。照抄,不自己挑。
		static string Floor() => H_WOOD.ToString();
		static string Plat() => H_FLOOR.ToString();



		// python 用局部坐标 wx(local):wx(1)=ax(左下角), wx(21)=end_x(主柱)。
		// 所有摆放位置都照抄它的 local 值,不自己另算一套。
		static int Wx(int local) => _x0 + _dir * (local - 1);
		static int MainCol => Wx(LocalMax);                // = end_x
		static int LocalMax => RoomWidth * _rooms + 1;     // 4间→21, 1间→6
		static int PillarTop => _floorRow - (PillarH - 1); // 照抄 _build_house

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
					// 人站在 _ay 那格时,身体正占着要放地板的位置 —— 方块放不进碰撞箱,
					// 报出来就是"没放上地板"。选址允许房子悬空,左下角完全可能就是人脚下那格。
					// N 键那条能跑通的路径用的是 OriginCy(p)+1:地板铺在【人踩着的那一行】,
					// 房子长在人现在站的高度上。这里对齐成一样的行为。
					if (cy >= _ay - 1)
					{
						// 人已经在目标高度或更高:地板就铺在脚下那一行,和 N 键(OriginCy+1)一致。
						// 直接用 _ay 的话人正占着那格,方块放不进碰撞箱 → "没放上地板"。
						if (_ay != cy + 1)
							DiagLog.Write($"[house] 左下角 y={_ay} 够不着/在身上,地板改到脚下那行 {cy + 1}");
						_ay = cy + 1;
					}
					if (cy == _ay - 1)   // 站在地板那行的上面一格 = 踩着它
					{
						_floorRow = _ay;
						Advance(Ph.SeedFloor);
						if (!Need(PlaceAction.Start(Plat(), _x0, _ay, 1, 0, 0, true, out string ws1), "放地板第一格", ws1)) return;
						return;
					}
					if (++_liftTries > MaxLift) { Fail($"垫了{MaxLift}次还没到 {_ay + 1}"); return; }
					// col 不传:让 PillarUp 自己把身体从这一列让开
					if (!Need(PillarUp.Start(Plat(), 1, -1, out string ws2), "垫平台", ws2)) return;
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
					if (!Need(BridgeBuilder.Start(Floor(), _dir > 0 ? "right" : "left", Width, out string ws3), "铺地板", ws3)) return;
					return;

				case Ph.Floor:
					if (BridgeBuilder.IsRunning) return;
					if (BridgeBuilder.Outcome != "done") { Fail($"地板:{BridgeBuilder.Outcome}/{BridgeBuilder.Reason}"); return; }
					// 砌柱子前【不要】站到那一列:传了 col 的 PillarUp 不会 StepAside,身体占着就砌不上
					Advance(Ph.MainPillar);
					if (!Need(PillarUp.Start(Plat(), PillarH, MainCol, out string ws4), "砌主柱", ws4)) return;
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
					// 照抄 roof_row = o["cy"] + 1:屋顶行按实际站位记下来,后面铺墙/放火把都用它
					_roofRow = ActExecutor.OriginCy(p) + 1;
					Advance(Ph.Roof);
					if (!Need(BridgeBuilder.Start(Plat(), _dir > 0 ? "left" : "right", Width, out string ws5), "铺屋顶", ws5)) return;
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
					if (!Need(DropDown.Start(out string ws6), "掉下来", ws6)) return;
					return;

				// ── 每间一根支柱。站到支柱旁边(不是支柱上)再砌 ────────────────────
				case Ph.Drop:
					if (DropDown.IsRunning) return;
					_roomIdx = 0;
					Advance(Ph.SupportSettle);
					SettleAt.Start(Wx(3), out _);
					return;

				case Ph.SupportSettle:
					if (SettleAt.IsRunning) return;
					Advance(Ph.Support);
					if (!Need(PillarUp.Start(Plat(), SupportH, Wx(1 + RoomWidth * _roomIdx), out string ws7), "砌支柱", ws7)) return;
					return;

				case Ph.Support:
					if (PillarUp.IsRunning) return;
					if (PillarUp.Outcome != "done") { Fail($"支柱{_roomIdx}:{PillarUp.Outcome}/{PillarUp.Reason}"); return; }
					if (++_roomIdx < _rooms)
					{
						Advance(Ph.SupportSettle);
						SettleAt.Start(Wx(3 + RoomWidth * _roomIdx), out _);
						return;
					}
					// python: 支柱砌完 → settle wx(19) → 重读 floor_row = o["cy"] → 合工作台 → 放
					Advance(Ph.BenchSettle);
					SettleAt.Start(Wx(LocalMax - 2), out _);
					return;

				case Ph.BenchSettle:
				{
					if (SettleAt.IsRunning) return;
					_floorRow = ActExecutor.OriginCy(p);
					// 先合成再放:椅子和墙要工作台才能合,而工作台本身只要木材,徒手就能合。
					// 之前直接去放,背包里没有 → WalkPlace.Start 立刻 return false,一帧就"失败"。
					if (Predicates.Have(H_WORKBENCH) < 1)
						CraftCoordinator.Craft(H_WORKBENCH, 1);
					if (Predicates.Have(H_WORKBENCH) < 1)
					{ Fail($"合不出工作台({CraftCoordinator.LastStop})"); return; }
					// python 用 place_at(人已经站在 wx(19) 了),不是 walk_place
					Advance(Ph.Bench);
					if (!Need(PlaceAction.Start(H_WORKBENCH.ToString(), Wx(LocalMax - 2), _floorRow, 1, 0, 0, true, out string wb),
						"放工作台", wb)) return;
					return;
				}

				case Ph.Bench:
					if (PlaceAction.IsRunning) return;
					if (!Main.tile[Wx(LocalMax - 2), _floorRow].HasTile)
					{ Fail($"工作台没放上 ({Wx(LocalMax - 2)},{_floorRow}):{PlaceAction.Outcome}"); return; }
					Advance(Ph.Craft);
					return;

				case Ph.Craft:
				{
					// 工作台放下后配方要几帧才出现,等它出现再合成。
					// 等的是【自己真要合的那样东西】:python 盖 4 间所以等木桌,单间不做桌子,
					// 等木桌会一直等到超时 —— 单间等木椅。
					int waitFor = TableCount > 0 ? H_TABLE : H_CHAIR;
					bool ready = false;
					for (int ri = 0; ri < Main.numAvailableRecipes; ri++)
						if (Main.recipe[Main.availableRecipe[ri]].createItem.type == waitFor)
						{ ready = true; break; }
					if (!ready) return;      // 等,由 StepTimeout 兜底

					if (TableCount > 0)
					{
						int tables = Predicates.Have(H_TABLE);
						if (tables < TableCount) CraftCoordinator.Craft(H_TABLE, TableCount - tables);
					}
					int chairs = Predicates.Have(H_CHAIR);
					if (chairs < ChairCount) CraftCoordinator.Craft(H_CHAIR, ChairCount - chairs);
					int walls = Predicates.Have(H_WALL);
					if (walls < WallCount) CraftCoordinator.Craft(H_WALL, WallCount - walls);

					// 合不出来就如实失败 —— CraftCoordinator 只数真进了背包的
					if (Predicates.Have(H_CHAIR) < ChairCount)
					{ Fail($"椅子只有 {Predicates.Have(H_CHAIR)}/{ChairCount}"); return; }
					if (Predicates.Have(H_WALL) < WallCount)
					{ Fail($"木墙只有 {Predicates.Have(H_WALL)}/{WallCount}"); return; }

					// 单间:工作台就在脚下,合完椅子直接原地放,不走来走去。
					// 多间才需要 walk_place —— 桌 wx(14,9,4) 走到 wx(3),椅 wx(2,7,12,17) 走回 wx(19)。
					if (TableCount > 0)
					{
						var tt = new List<(int, int, string)>();
						for (int i = 0; i < TableCount; i++)
							tt.Add((Wx(14 - RoomWidth * i), _floorRow, H_TABLE.ToString()));
						Advance(Ph.Tables);
						if (!Need(WalkPlace.Start(Wx(3), tt, out string wt), "摆桌子", wt)) return;
						return;
					}
					// 椅子放在工作台旁边那格(人现在就站这儿)
					Advance(Ph.Chairs);
					if (!Need(PlaceAction.Start(H_CHAIR.ToString(), Wx(LocalMax - 3), _floorRow, 1, 0, 0, true, out string wc0),
						"放椅子", wc0)) return;
					return;
				}

				case Ph.Tables:
					if (WalkPlace.IsRunning) return;
					DiagLog.Write($"[house] tables → {WalkPlace.Outcome}/{WalkPlace.Reason}");
					Advance(Ph.Chairs);
					if (!Need(StartChairs(out string wc), "摆椅子", wc)) return;
					return;

				case Ph.Chairs:
					// 单间走的是 PlaceAction,多间走 WalkPlace —— 等哪个都行,两个都不在跑就是完了
					if (PlaceAction.IsRunning || WalkPlace.IsRunning) return;
					DiagLog.Write($"[house] chairs → 背包椅子={Predicates.Have(H_CHAIR)}");
					_roomIdx = 0;
					// 单间:人已经站在地板上了,直接砌墙。PlaceWalls 够不着的格子自己会跳。
					if (_rooms == 1) { Advance(Ph.Walls); StartWalls(0); return; }
					Advance(Ph.WallSettle);
					SettleAt.Start(Wx(4), out _);
					return;

				// ── 一间一间铺墙:站到那一间中间,跳回地板层,铺 ──────────────────
				case Ph.WallSettle:
					if (SettleAt.IsRunning) return;
					Advance(Ph.WallHop);
					HopUp.Start(_floorRow, Wx(4 + RoomWidth * _roomIdx), out _);
					return;

				case Ph.WallHop:
					if (HopUp.IsRunning) return;
					Advance(Ph.Walls);
					StartWalls(_roomIdx);
					return;

				case Ph.Walls:
					if (PlaceWalls.IsRunning) return;
					// python 每间铺完墙紧接着放一个火把:place_at wx(col1+2), roof_row+2
					Advance(Ph.Torch);
					PlaceAction.Start(H_TORCH.ToString(), Wx(1 + RoomWidth * _roomIdx + 2), _roofRow + 2, 1, 0, 0, true, out _);
					return;

				case Ph.Torch:
					if (PlaceAction.IsRunning) return;
					// 火把放不上不算失败(python 也不检查) —— 少个照明不影响房子成立
					if (++_roomIdx < _rooms)
					{
						Advance(Ph.WallSettle);
						SettleAt.Start(Wx(4 + RoomWidth * _roomIdx), out _);
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
			int col1 = 1 + RoomWidth * room;
			var cells = new List<(int, int)>();
			foreach (var (dr, dc) in WallOrder)
				cells.Add((Wx(col1 + (dc - 1)), _roofRow + dr));
			if (!Need(PlaceWalls.Start(H_WALL.ToString(), cells, out string ws9), "铺墙", ws9)) return;
		}

		// 原语的 Start 失败时【不会】把 IsRunning 置起来,所以丢掉返回值的话下一帧看见
		// IsRunning==false,会误判成"这步跑完了"然后去验结果 —— 报出来的是"东西没放上",
		// 而真正的原因(背包里没有 / 够不着)全丢了。实测:工作台那步 1 帧就失败,因为
		// 背包里还没有工作台,而 Start 早就 return false 了。
		static bool Need(bool started, string what, string why)
		{
			if (started) return true;
			Fail($"{what} 启动失败:{why}");
			return false;
		}

		// python: 椅子 wx(2,7,12,17),走到 wx(19)。
		// 重试时换个终点往回走 —— 同一个方向再走一遍,够不着的还是够不着。
		static bool StartChairs(out string why)
		{
			var cc = new List<(int, int, string)>();
			for (int i = 0; i < ChairCount; i++)
				cc.Add((Wx(2 + RoomWidth * i), _floorRow, H_CHAIR.ToString()));
			int dest = (_hopTries % 2 == 0) ? Wx(LocalMax - 2) : Wx(2);
			return WalkPlace.Start(dest, cc, out why);
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
