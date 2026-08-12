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
			Idle, Lift, LiftStep, Floor,
			MainPillar, SettleBelow, HopTop, SettleTop, Roof, MoveOver, Drop,
			SupportSettle, Support, BenchSettle, Bench, Craft, Tables, Chairs, WallSettle, WallHop, Walls, Torch,
			Done
		}

		private static Ph _ph = Ph.Idle;
		private static int _dir = 1;
		private static int _x0, _ay;              // 房子矩形的左下角(选址给的)
		private static int _floorRow;             // 地板实际所在行
		private static int _rooms = 1;
		private static int _waited, _hopTries, _liftTries, _liftBefore;
		private static int _roomIdx;              // 正在处理第几间(支柱/铺墙都按间走)
		private static int _roofRow;              // python: roof_row = 上到柱顶后实际站位的 cy+1
		private static int _torchWx, _torchWy;

		// 一律用数字 id:ResolveSlot 匹配不上就去比 it.Name,那是本地化名(中文),内部名永远不匹配
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
		// 每间一个火把。合不出来:配方要凝胶,不刷怪就只能开箱砸罐捡 —— 是进屋前该备好的料
		static int TorchCount => _rooms;

		// (ax,ay)=左下角=地板第一格,是放出来的不是走上去的:站旁边放出它 → 跳上去踩着 → 往外铺
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
			DiagLog.Write($"[house] start rooms={_rooms} dir={_dir} corner=({ax},{ay}) width={Width} 现在({ActExecutor.OriginCx(p)},{ActExecutor.OriginCy(p)})");
			_ph = Ph.Lift;
			return true;
		}

		// 从人当前位置开工(测试键用)。人站在左下角【隔两列】,所以左下角在两列外、高一行。
		public static bool StartHere(int rooms, int dir, out string why)
		{
			var p = Main.LocalPlayer;
			if (p == null) { why = "no_player"; return false; }
			int d = dir >= 0 ? 1 : -1;
			return Start(rooms, d, ActExecutor.OriginCx(p) + d * 2, ActExecutor.OriginCy(p) - 1, out why);
		}

		// 梯子和房子共用 x0 那一列:人站在下面的地上,一路往上搭平台,最上面那格就是左下角。
		// nav 和这里共用这一份,免得两边各推一遍
		public static int StandCol(int ax, int ay, int dir) => ax;

		// 梯脚:x0 那一列往下第一块地的上面一格。够不到地返回 int.MinValue
		public static int LadderFootRow(int ax, int ay)
		{
			int L = Predicates.LadderLen(ax, ay);
			return L < 0 ? int.MinValue : ay + L;
		}

		// 人 20px 跨两列,爬升段里那两列头顶都不能有方块 —— 挑干净的那半边站。
		// +3/+13 不取 +1/+15:贴着列边界站,几 px 误差就把 PillarCol 甩到隔壁列。
		static float ClearStandPx(int x0, int fromCy, int toCy)
		{
			if (!ColClear(x0, fromCy, toCy)) return float.NaN;
			if (ColClear(x0 - 1, fromCy, toCy)) return x0 * 16f + 3f;
			if (ColClear(x0 + 1, fromCy, toCy)) return x0 * 16f + 13f;
			return float.NaN;
		}

		// 数量对不上时要看的是"在哪个槽",不是"有几个"
		static string SlotDump(int id)
		{
			var p = Main.LocalPlayer;
			if (p == null) return "?";
			var sb = new System.Text.StringBuilder();
			for (int i = 0; i < p.inventory.Length; i++)
			{
				var it = p.inventory[i];
				if (it != null && !it.IsAir && it.type == id) sb.Append($"[{i}]x{it.stack} ");
			}
			return sb.Length == 0 ? "无" : sb.ToString().Trim();
		}

		static string ColDump(int col, int fromCy, int toCy)
		{
			var sb = new System.Text.StringBuilder($"{col}:");
			for (int y = toCy; y <= fromCy; y++)
				sb.Append(Predicates.IsSolid(col, y) ? '#' : '.');
			return sb.ToString();
		}

		static bool ColClear(int col, int fromCy, int toCy)
		{
			for (int y = toCy; y <= fromCy; y++)
				if (Predicates.IsSolid(col, y)) return false;
			return true;
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
				// 站到 (_standCx, _ay+1) —— 左下角隔两列。站正下方不行:(_x0,_ay) 会在身体里。
				case Ph.Lift:
				{
					if (SettleAt.IsRunning || HopUp.IsRunning || DropDown.IsRunning) return;
					int lcx = Predicates.PillarCol(p), lcy = ActExecutor.OriginCy(p);
					// 踩在左下角上了 → 开工。梯子最上面那格就是左下角,不用再单独放
					if (lcx == _x0 && lcy == _ay - 1)
					{
						_liftTries = 0;
						Advance(Ph.Floor);
						if (!Need(BridgeBuilder.Start(Floor(), _dir > 0 ? "right" : "left", Width, _x0 + _dir, _ay, out string wf), "铺地板", wf)) return;
						return;
					}
					if (++_liftTries > MaxLift)
					{ Fail($"上不到左下角 ({_x0},{_ay}),现在({lcx},{lcy})"); return; }
					// 先对 x 再对高度:地面上横向走安全,爬到柱顶再横向走是悬空,走不过去还会掉下来
					if (lcx != _x0) { SettleAt.Start(_x0, out _); _waited = 0; return; }
					// 爬过头:只挪到位,别一路掉到地面再重爬一遍
					if (lcy < _ay - 1) { DropDown.Start(_ay - 1, out _); _waited = 0; return; }
					// 爬之前先把身体挪到头顶干净的那半边 —— 人跨两列,撞上哪列就卡在那儿
					// 从头顶那格起扫,别把人自己占的 3 行算进去 —— 站半砖上脚那格是实心,不是障碍
					float wantPx = ClearStandPx(_x0, lcy - 3, _ay - 1);
					if (float.IsNaN(wantPx))
					{ Fail($"({_x0}) 那一列爬不上去:{lcy - 3}→{_ay - 1} 头顶有方块 {ColDump(_x0 - 1, lcy - 3, _ay - 1)} {ColDump(_x0, lcy - 3, _ay - 1)} {ColDump(_x0 + 1, lcy - 3, _ay - 1)}"); return; }
					float curPx = p.position.X + p.width / 2f;
					if (System.Math.Abs(curPx - wantPx) > 3f)
					{ SettleAt.StartPx(_x0, wantPx, 3f, out _); _waited = 0; return; }
					// 平台当绳子:从脚下的地一路搭到左下角,搭完人就站在左下角上。
					_liftBefore = lcy;
					Advance(Ph.LiftStep);
					SkillExecutor.StartPillarJump(_dir > 0, _ay, false);
					return;
				}

				case Ph.LiftStep:
					if (SkillExecutor.IsActive) return;
					// 没爬高就别回去重启 —— 会 start/done 空转到 MaxLift,还把人蹭下平台
					if (ActExecutor.OriginCy(p) >= _liftBefore)
					{ Fail($"爬不动:还在 {ActExecutor.OriginCy(p)},要到 {_ay - 1}"); return; }
					_ph = Ph.Lift; _waited = 0;
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
					// 背包不开就合不了:原版 AdjTiles()(扫身边工作台)只在 Main.playerInventory 时跑,
					// 不跑 adjTile[] 就是空的,要工作台的配方永远不出现。人也一样,得开背包。
					Main.playerInventory = true;
					// 等【自己真要合的】那样出现:单间不做桌子,等木桌会一直等到超时
					int waitFor = TableCount > 0 ? H_TABLE : H_CHAIR;
					bool ready = false;
					for (int ri = 0; ri < Main.numAvailableRecipes; ri++)
						if (Main.recipe[Main.availableRecipe[ri]].createItem.type == waitFor)
						{ ready = true; break; }
					if (!ready) return;      // 等,由 StepTimeout 兜底

					// 椅子先合:桌子排第一时老是只出 2 张(合成本身报成功),换个顺序试
					int chairs = Predicates.Have(H_CHAIR);
					if (chairs < ChairCount)
					{
						CraftCoordinator.Craft(H_CHAIR, ChairCount - chairs);
						DiagLog.Write($"[house] craft chair +{CraftCoordinator.LastCrafted} overflow={CraftCoordinator.LastOverflow} stop={CraftCoordinator.LastStop} 现有={Predicates.Have(H_CHAIR)}");
					}
					if (TableCount > 0)
					{
						int tables = Predicates.Have(H_TABLE);
						if (tables < TableCount)
						{
							CraftCoordinator.Craft(H_TABLE, TableCount - tables);
							DiagLog.Write($"[house] craft table +{CraftCoordinator.LastCrafted} overflow={CraftCoordinator.LastOverflow} stop={CraftCoordinator.LastStop} 现有={Predicates.Have(H_TABLE)}");
						}
					}
					int walls = Predicates.Have(H_WALL);
					if (walls < WallCount) CraftCoordinator.Craft(H_WALL, WallCount - walls);

					// 桌子当初漏了验,少一张就静默过去,摆的时候挥空到超时
					if (TableCount > 0 && Predicates.Have(H_TABLE) < TableCount)
					{ Fail($"木桌只有 {Predicates.Have(H_TABLE)}/{TableCount}:{CraftCoordinator.LastStop}"); return; }
					if (Predicates.Have(H_CHAIR) < ChairCount)
					{ Fail($"椅子只有 {Predicates.Have(H_CHAIR)}/{ChairCount}"); return; }
					if (Predicates.Have(H_WALL) < WallCount)
					{ Fail($"木墙只有 {Predicates.Have(H_WALL)}/{WallCount}"); return; }
					if (Predicates.Have(H_TORCH) < TorchCount)
					{ Fail($"火把只有 {Predicates.Have(H_TORCH)}/{TorchCount},进屋前得先攒够(开箱砸罐)"); return; }

					// 单间:工作台就在脚下,合完椅子直接原地放,不走来走去。
					// 多间才需要 walk_place —— 桌 wx(14,9,4) 走到 wx(3),椅 wx(2,7,12,17) 走回 wx(19)。
					if (TableCount > 0)
					{
						var tt = new List<(int, int, string)>();
						for (int i = 0; i < TableCount; i++)
							tt.Add((Wx(14 - RoomWidth * i), _floorRow, H_TABLE.ToString()));
						DiagLog.Write($"[house] 摆桌前 背包桌子={Predicates.Have(H_TABLE)} 目标{tt.Count}个 mouseItem={(Main.mouseItem != null && !Main.mouseItem.IsAir ? Main.mouseItem.type + "x" + Main.mouseItem.stack : "空")} 分布={SlotDump(H_TABLE)}");
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
					DiagLog.Write($"[house] tables → {WalkPlace.Outcome}/{WalkPlace.Reason} placed={WalkPlace.PlacedCount}");
					if (WalkPlace.Outcome != "done")
					{ Fail($"桌子只摆了 {WalkPlace.PlacedCount}/{TableCount}:{WalkPlace.Outcome}"); return; }
					Advance(Ph.Chairs);
					if (!Need(StartChairs(out string wc), "摆椅子", wc)) return;
					return;

				case Ph.Chairs:
					// 单间走的是 PlaceAction,多间走 WalkPlace —— 等哪个都行,两个都不在跑就是完了
					if (PlaceAction.IsRunning || WalkPlace.IsRunning) return;
					DiagLog.Write($"[house] chairs → 背包椅子={Predicates.Have(H_CHAIR)}");
					// 从最后一间开始倒着铺:摆完椅子人就在第四间那头,顺手就砌,不用先跑回第一间
					_roomIdx = _rooms - 1;
					// 单间:人已经站在地板上了,直接砌墙。PlaceWalls 够不着的格子自己会跳。
					if (_rooms == 1) { Advance(Ph.Walls); StartWalls(0); return; }
					Advance(Ph.WallSettle);
					SettleAt.Start(Wx(4 + RoomWidth * _roomIdx), out _);
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
					_torchWx = Wx(1 + RoomWidth * _roomIdx + 2); _torchWy = _roofRow + 2;
					Advance(Ph.Torch);
					PlaceAction.Start(H_TORCH.ToString(), _torchWx, _torchWy, 1, 0, 0, true, out _);
					return;

				case Ph.Torch:
					if (PlaceAction.IsRunning) return;
					// NPC 要光源才肯住,少一个火把这间房就不算数
					if (!Main.tile[_torchWx, _torchWy].HasTile)
					{ Fail($"第{_roomIdx + 1}间火把没放上 ({_torchWx},{_torchWy}):{PlaceAction.Outcome}"); return; }
					if (--_roomIdx >= 0)
					{
						Advance(Ph.WallSettle);
						SettleAt.Start(Wx(4 + RoomWidth * _roomIdx), out _);
						return;
					}
					// 验收:逐格看有没有东西。placed=4 只说明挥了四次工具,最后一张椅子没落地也照样报 4。
					string missing = AuditHouse();
					if (missing != null) { Fail($"验收不合格:{missing}"); return; }
					Outcome = "done"; _ph = Ph.Done;
					DiagLog.Write($"[house] done rooms={_rooms} x0={_x0} floor_row={_floorRow}");
					Main.NewText($"[TerraBlind] 房子盖好了 ({_x0},{_floorRow}) {_rooms}间", 120, 255, 120);
					return;
			}
		}

		// 那一格上有没有【指定类型】的东西。家具占多格,vanilla 只在原点记 TileType,所以按类型查而不是按 HasTile。
		static bool HasType(int wx, int wy, int type)
		{
			if (wx < 0 || wy < 0 || wx >= Main.maxTilesX || wy >= Main.maxTilesY) return false;
			var t = Main.tile[wx, wy];
			return t.HasTile && t.TileType == type;
		}

		// 家具的原点可能落在相邻格(桌椅 2~3 格宽,放置时的对齐点不一定是我要的那格),所以左右各看一格。
		static bool HasTypeNear(int wx, int wy, int type)
		{
			for (int d = -1; d <= 1; d++)
				for (int dy = -1; dy <= 1; dy++)
					if (HasType(wx + d, wy + dy, type)) return true;
			return false;
		}

		// 完工验收:每一格都实地看过。缺什么报什么坐标 —— "placed=4" 是挥了四次工具,不是四张椅子落了地。
		static string AuditHouse()
		{
			var bad = new List<string>();
			for (int i = 0; i < ChairCount; i++)
			{
				int wx = Wx(2 + RoomWidth * i);
				if (!HasTypeNear(wx, _floorRow, H_CHAIR)) bad.Add($"椅({wx},{_floorRow})");
			}
			for (int i = 0; i < TableCount; i++)
			{
				int wx = Wx(14 - RoomWidth * i);
				if (!HasTypeNear(wx, _floorRow, H_TABLE)) bad.Add($"桌({wx},{_floorRow})");
			}
			for (int r = 0; r < _rooms; r++)
			{
				int col1 = 1 + RoomWidth * r;
				foreach (var (dr, dc) in WallOrder)
				{
					int wx = Wx(col1 + (dc - 1)), wy = _roofRow + dr;
					if (Main.tile[wx, wy].WallType == 0) bad.Add($"墙({wx},{wy})");
				}
				int tx = Wx(col1 + 2), ty = _roofRow + 2;
				if (!HasTypeNear(tx, ty, H_TORCH)) bad.Add($"火把({tx},{ty})");
			}
			// 地板:柱子之间每一格都得踩得住,漏一格 NPC 判定就不认
			for (int c = 1; c <= LocalMax; c++)
			{
				int wx = Wx(c);
				if (!Main.tile[wx, _floorRow + 1].HasTile) bad.Add($"地板({wx},{_floorRow + 1})");
			}
			if (bad.Count == 0) return null;
			string all = string.Join(" ", bad);
			DiagLog.Write($"[house] AUDIT 缺 {bad.Count} 处: {all}");
			return bad.Count > 6 ? $"缺{bad.Count}处 {string.Join(" ", bad.GetRange(0, 6))}…" : all;
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

		// 原语 Start 失败不会置 IsRunning,丢掉返回值就会被当成"这步跑完了",真原因(没东西/够不着)全丢
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
