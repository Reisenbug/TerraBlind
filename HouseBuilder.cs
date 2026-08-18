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
			FixStruct, Fix, Reclaim, Done
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
		// 房间内的一格,用来给 NPC 指派住房(moveRoom 要房间【里面】的坐标,火把那格正好)
		public static int TorchWx => _torchWx;
		public static int TorchWy => _torchWy;
		// 第 i 张椅子在哪一列 —— 放置和验收共用这一份。
		// 以前放的是 Wx(LocalMax-3)、验的是 Wx(2+RoomWidth*i),单间差一格,靠 HasTypeNear 的
		// ±1 容差才没报错;坐标算两遍迟早对不上
		static int ChairCol(int i) => _rooms == 1 ? Wx(LocalMax - 3) : Wx(2 + RoomWidth * i);
		// 椅子那一列:晚上 NPC 坐在这儿,要把他捅下去就得挖【这一列】的地板
		public static int ChairWx => ChairCol(0);
		public static int ChairWy => _floorRow;

		// 一律用数字 id:ResolveSlot 匹配不上就去比 it.Name,那是本地化名(中文),内部名永远不匹配
		const int H_FLOOR = 94;       // 木平台
		const int H_WOOD = 9;         // 木材
		const int H_WORKBENCH = 36;
		const int H_TABLE = 32;
		const int H_CHAIR = 34;
		// 放下去之后地上是【方块 ID】,和上面那些物品 ID 是两套号:椅子 34→15、桌子 32→14。
		// 拿物品 ID 去比 TileType 会全部判缺 —— 房子明明盖好了却报验收不合格。
		const int T_TABLE = Terraria.ID.TileID.Tables;
		const int T_CHAIR = Terraria.ID.TileID.Chairs;
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

		// 火把不挑种类:原版判房间要光源看的是 TileID.Sets.RoomNeeds.CountsAsTorch,
		// 里面是 tile 4,而所有火把(蓝/骨/丛林/暗影…)放出来的都是 tile 4。
		static bool IsTorch(Item it)
			=> it != null && !it.IsAir && it.type < Terraria.ID.ItemID.Sets.Torches.Length
			   && Terraria.ID.ItemID.Sets.Torches[it.type];

		static int HaveTorch()
		{
			var p = Main.LocalPlayer;
			if (p == null) return 0;
			int n = 0;
			for (int i = 0; i < 58 && i < p.inventory.Length; i++)
				if (IsTorch(p.inventory[i])) n += p.inventory[i].stack;
			return n;
		}

		// 手头存量最多的那种火把。没有就退回普通火把 id,让合成那步去补
		static int TorchId()
		{
			var p = Main.LocalPlayer;
			if (p == null) return H_TORCH;
			int best = -1, bestStack = 0;
			for (int i = 0; i < 58 && i < p.inventory.Length; i++)
			{
				var it = p.inventory[i];
				if (!IsTorch(it) || it.stack <= bestStack) continue;
				bestStack = it.stack; best = it.type;
			}
			return best < 0 ? H_TORCH : best;
		}

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
			_waited = 0; _hopTries = 0; _liftTries = 0; _roomIdx = 0; _roofRow = 0; _fixTried = false;
			_reclaimTries = 0; ThrowItems.Forget();
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
			// 捡东西超时不算盖房失败:房子已经成了,东西在地上也丢不了
			if (++_waited > StepTimeout)
			{
				if (_ph == Ph.Reclaim) { DiagLog.Write("[house] 捡回超时,东西留在地上"); ThrowItems.Forget(); Done(); return; }
				Fail($"timeout_at_{_ph}"); return;
			}

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
					// 只扫落脚后身子占的 3 行:扫到 lcy-3 会把脚下的梯子和左下角自己算成障碍
					float wantPx = ClearStandPx(_x0, _ay - 1, _ay - 3);
					if (float.IsNaN(wantPx))
					{
						// 真实地形挡着不是失败,是挖开:选址躲不掉的(要塞墙/矿脉)只能清出来
						for (int c = _x0 - 1; c <= _x0 + 1; c++)
							for (int ry = _ay - 1; ry >= _ay - 3; ry--)
								if (ClearWay.Dig(p, c, ry, "房址被挡")) { _waited = 0; return; }
						Fail($"({_x0}) 那一列爬不上去:{_ay - 1}→{_ay - 3} 头顶有方块 {ColDump(_x0 - 1, _ay - 1, _ay - 3)} {ColDump(_x0, _ay - 1, _ay - 3)} {ColDump(_x0 + 1, _ay - 1, _ay - 3)}{(ClearWay.HasPick(p) ? "(够不着)" : "(没镐)")}"); return;
					}
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
					if (HaveTorch() < TorchCount)
					{ Fail($"火把只有 {HaveTorch()}/{TorchCount},进屋前得先攒够(开箱砸罐)"); return; }

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
					if (!Need(PlaceAction.Start(H_CHAIR.ToString(), ChairCol(0), _floorRow, 1, 0, 0, true, out string wc0),
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
					PlaceAction.Start(TorchId().ToString(), _torchWx, _torchWy, 1, 0, 0, true, out _);
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
					// 缺东西先自己补一遍再说 —— 少一张椅子就整栋报废太亏,而缺哪格、缺什么验收都已经知道了。
					string missing = AuditHouse();
					if (missing != null)
					{
						if (_fixTried) { Fail($"验收不合格:{missing}"); return; }
						_fixTried = true;
						// 先补结构:地板/屋顶/柱子缺一格,NPC 判定就不认。
						// 补法和铺桥面完全一样 —— PlaceAnywhere 够不着会自己走、没锚会自己造
						if (_fixTiles.Count > 0 || _fixWalls.Count > 0 || _fixDig.Count > 0)
						{
							_digTries = 0;
							DiagLog.Write($"[house] 验收缺 {missing} → 补结构 方块{_fixTiles.Count}格 墙{_fixWalls.Count}格 挖{_fixDig.Count}格");
							_fixTileIdx = 0;
							Advance(Ph.FixStruct);
							return;
						}
						if (_fixList.Count == 0) { Fail($"验收不合格:{missing}"); return; }
						// 背包里没有就先合:上一轮放丢了的那件,东西已经从背包扣掉了。
						foreach (var (_, _, item) in _fixList)
							if (Predicates.Have(item) < 1)
							{
								CraftCoordinator.Craft(item, 1);
								DiagLog.Write($"[house] 补合 {item} +{CraftCoordinator.LastCrafted} stop={CraftCoordinator.LastStop} 现有={Predicates.Have(item)}");
							}
						DiagLog.Write($"[house] 验收缺 {missing} → 补放 {_fixList.Count} 件");
						var ft = new List<(int, int, string)>();
						foreach (var (fx, fy, item) in _fixList) ft.Add((fx, fy, item.ToString()));
						Advance(Ph.Fix);
						if (!Need(WalkPlace.Start(Wx(LocalMax - 2), ft, out string wf), "补家具", wf)) return;
						return;
					}
					Done();
					return;

				// 结构补格:一格一格交给 PlaceAnywhere(和铺桥面同一套),补完再补墙,然后回去重验
				case Ph.FixStruct:
				{
					if (PlaceAnywhere.IsRunning || PlaceWalls.IsRunning || ItemUseCoordinator.IsActive) return;
					// 先挖通内腔:里面堵着石头,墙铺上去也没用,原版 CheckRoom 照样不认
					while (_fixDig.Count > 0)
					{
						var (dx4, dy4) = _fixDig[_fixDig.Count - 1];
						if (!Predicates.IsWall(dx4, dy4))
						{ _fixDig.RemoveAt(_fixDig.Count - 1); _digTries = 0; continue; }
						// 没镐/挖不动就别死磕这一格,记一笔跳过去,不然每帧卡在同一格
						if (++_digTries > MaxDigTries)
						{
							DiagLog.Write($"[house] ({dx4},{dy4})挖不掉,跳过");
							_fixDig.RemoveAt(_fixDig.Count - 1); _digTries = 0; continue;
						}
						if (ClearWay.Dig(p, dx4, dy4, "房内堵着")) return;
						// 够不着就走过去 —— 房子就这么大,横向挪一挪必定能够着
						if (ActExecutor.OriginCx(p) < dx4) p.controlRight = true; else p.controlLeft = true;
						return;
					}
					while (_fixTileIdx < _fixTiles.Count)
					{
						var (fx2, fy2) = _fixTiles[_fixTileIdx];
						if (Predicates.IsGround(fx2, fy2)) { _fixTileIdx++; continue; }
						int bid2 = DeckBuilder.PickBlock();
						if (bid2 < 0) { DiagLog.Write("[house] 没方块补结构了"); break; }
						_fixTileIdx++;
						if (PlaceAnywhere.Start(bid2.ToString(), fx2, fy2, out _)) return;
					}
					if (_fixWalls.Count > 0)
					{
						var wcells = new List<(int, int)>(_fixWalls);
						_fixWalls.Clear();
						if (Predicates.Have(H_WALL) < wcells.Count) CraftCoordinator.Craft(H_WALL, wcells.Count);
						if (PlaceWalls.Start(H_WALL.ToString(), wcells, out _)) return;
					}
					// 结构补过了,家具还缺就接着走原来那条
					if (_fixList.Count > 0)
					{
						var ft2 = new List<(int, int, string)>();
						foreach (var (fx3, fy3, item3) in _fixList)
						{
							if (Predicates.Have(item3) < 1) CraftCoordinator.Craft(item3, 1);
							ft2.Add((fx3, fy3, item3.ToString()));
						}
						Advance(Ph.Fix);
						if (!Need(WalkPlace.Start(Wx(LocalMax - 2), ft2, out string wf2), "补家具", wf2)) return;
						return;
					}
					Advance(Ph.Fix);
					return;
				}

				// 补完再验一次。还缺就认输 —— _fixTried 挡着,不会来回补个没完。
				case Ph.Fix:
					if (WalkPlace.IsRunning) return;
					string still = AuditHouse();
					if (still != null) { Fail($"补过一轮还是缺:{still}"); return; }
					Advance(Ph.Reclaim);
					return;

				// 为腾位置扔掉的东西都还在地上,走过去捡回来。捡不回来不算盖房失败:
				// 房子已经成了,东西还在地上,下次经过照样能捡。
				case Ph.Reclaim:
				{
					if (RecedingNav.Active) return;
					if (!ThrowItems.AnyOnGround(out int ix, out int iy)) { ThrowItems.Forget(); Done(); return; }
					if (++_reclaimTries > MaxReclaim)
					{ DiagLog.Write($"[house] 捡不回来,剩{ThrowItems.Thrown.Count}件在地上"); ThrowItems.Forget(); Done(); return; }
					DiagLog.Write($"[house] 去捡回扔掉的东西 ({ix},{iy})");
					RecedingNav.Start(ix, iy, RecedingNav.Mode.Reach);
					_waited = 0;
					return;
				}
			}
		}

		static void Done()
		{
			Outcome = "done"; _ph = Ph.Done;
			DiagLog.Write($"[house] done rooms={_rooms} x0={_x0} floor_row={_floorRow}");
			Main.NewText($"[TerraBlind] 房子盖好了 ({_x0},{_floorRow}) {_rooms}间", 120, 255, 120);
			// 椅子底下必须是岩浆:晚上 NPC 坐椅子上,捅他就是挖【椅子这一列】的地板。
			// 不合格不算盖房失败,但要当场说 —— 否则要等到最后一步才发现白忙
			if (_rooms == 1)
			{
				bool chairLava = Predicates.LavaBelow(ChairWx, ChairWy + 1);
				DiagLog.Write($"[house] 椅子({ChairWx},{ChairWy}) 底下是岩浆={chairLava}");
				if (!chairLava)
					Main.NewText($"[TerraBlind] 注意:椅子({ChairWx},{ChairWy})底下不是岩浆,捅不下去", 255, 200, 120);
			}
			// 盖完就把爆破专家指过来。房子合不合格由原版判,失败信息说明差什么
			if (_rooms == 1)
			{
				bool ok = AssignHome.Try(Terraria.ID.NPCID.Demolitionist, _torchWx, _torchWy, out string awhy);
				Main.NewText(ok ? $"[TerraBlind] {AssignHome.LastNote}" : $"[TerraBlind] 指派住房失败:{awhy}",
					ok ? (byte)120 : (byte)255, ok ? (byte)255 : (byte)200, 120);
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
		// 缺的家具:补的时候要知道往哪放什么,所以和文字报告分开存
		static readonly List<(int wx, int wy, int item)> _fixList = new();
		// 结构缺格分两类:方块类(地板/屋顶/柱子)交给 PlaceAnywhere,背景墙交给 PlaceWalls
		static readonly List<(int wx, int wy)> _fixTiles = new();
		static readonly List<(int wx, int wy)> _fixWalls = new();
		static readonly List<(int wx, int wy)> _fixDig = new();   // 内腔里不该有的块,要挖掉
		static int _fixTileIdx;
		static int _digTries;
		const int MaxDigTries = 60 * 10;   // 一格挖 10 秒还不掉,那就是挖不动
		static bool _fixTried;   // 只补一轮,补完还缺就认输,不来回补个没完
		static int _reclaimTries;
		const int MaxReclaim = 6;

		// 结构验收就这一份:所有部件都是"一条线段上每一格都得有东西"。
		// 地板/屋顶是横线,主柱/支柱是竖线,判据只差 solid 还是 wall。
		// 各写一套的下场已经见过:椅子放 Wx(3) 验 Wx(2),靠 ±1 容差才没炸
		static int CheckLine(List<string> bad, string name, int x0, int y0, int dx, int dy, int n, bool wall)
		{
			int miss = 0;
			for (int k = 0; k < n; k++)
			{
				int wx = x0 + dx * k, wy = y0 + dy * k;
				bool ok = wall ? Main.tile[wx, wy].WallType != 0 : Predicates.IsGround(wx, wy);
				if (ok) continue;
				miss++;
				if (miss <= 3) bad.Add($"{name}({wx},{wy})");
				// 缺的格子记下来:结构补格和铺桥面是同一件事,交给 PlaceAnywhere 就行
				(wall ? _fixWalls : _fixTiles).Add((wx, wy));
			}
			if (miss > 3) bad.Add($"{name}还缺{miss - 3}处");
			return miss;
		}

		static string AuditHouse()
		{
			var bad = new List<string>();
			_fixList.Clear(); _fixTiles.Clear(); _fixWalls.Clear(); _fixDig.Clear(); _fixTileIdx = 0;
			for (int i = 0; i < ChairCount; i++)
			{
				int wx = ChairCol(i);
				if (!HasTypeNear(wx, _floorRow, T_CHAIR)) { bad.Add($"椅({wx},{_floorRow})"); _fixList.Add((wx, _floorRow, H_CHAIR)); }
			}
			for (int i = 0; i < TableCount; i++)
			{
				int wx = Wx(14 - RoomWidth * i);
				if (!HasTypeNear(wx, _floorRow, T_TABLE)) { bad.Add($"桌({wx},{_floorRow})"); _fixList.Add((wx, _floorRow, H_TABLE)); }
			}
			// 墙:清单还是 WallOrder(它是放置的权威顺序),但报告走和结构件同一套限流,
			// 不然一间缺 20 格就刷 20 行
			for (int r = 0; r < _rooms; r++)
			{
				int col1 = 1 + RoomWidth * r, wmiss = 0, cmiss = 0;
				foreach (var (dr, dc) in WallOrder)
				{
					int wx = Wx(col1 + (dc - 1)), wy = _roofRow + dr;
					// 内腔里【不该有】的东西也要查:原版 StartRoomCheck 从这儿洪泛,
					// 起点是实心直接判死(RoomCheckStartedInASolidTile),中间有块会把空间切碎。
					// 家具是 tileFrameImportant,不算堵;平台也不挡,所以只认 IsWall
					if (Predicates.IsWall(wx, wy) && !Main.tileFrameImportant[Main.tile[wx, wy].TileType])
					{
						if (++cmiss <= 3) bad.Add($"堵({wx},{wy})");
						_fixDig.Add((wx, wy));
					}
					if (Main.tile[wx, wy].WallType != 0) continue;
					if (++wmiss <= 3) bad.Add($"墙({wx},{wy})");
					_fixWalls.Add((wx, wy));
				}
				if (wmiss > 3) bad.Add($"第{r + 1}间墙还缺{wmiss - 3}处");
				if (cmiss > 3) bad.Add($"第{r + 1}间还堵{cmiss - 3}处");
				// 火把不在这儿验:每间放完当场就查过一次(Ph.Torch),那时候 _torchWx/_torchWy 还是那一间的。
				// 事后按公式反推 _roofRow 算出来的是另一格,四间齐全的房子会被报成缺火把。
			}
			// 结构五件套,全走同一个方法:地板/屋顶/主柱/每间支柱
			CheckLine(bad, "地板", Wx(1), _floorRow + 1, _dir, 0, LocalMax, false);
			if (_roofRow > 0)
				CheckLine(bad, "屋顶", Wx(1), _roofRow, _dir, 0, LocalMax, false);
			CheckLine(bad, "主柱", MainCol, _floorRow, 0, -1, PillarH, false);
			for (int r = 0; r < _rooms; r++)
				CheckLine(bad, $"支柱{r + 1}", Wx(1 + RoomWidth * r), _floorRow, 0, -1, SupportH, false);
			if (bad.Count == 0) return null;
			string all = string.Join(" ", bad);
			DiagLog.Write($"[house] AUDIT 缺 {bad.Count} 处: {all}");
			// 结构缺了没法补(_fixList 只装家具),但也不该把整栋判死 —— 房子合不合格
			// 最终由原版 moveRoom 说了算。所以只在【有家具可补】时才当作失败去补,否则只警告
			// 家具没缺、只缺结构:也别放着不管,走 FixStruct 用 PlaceAnywhere/PlaceWalls 补
			if (_fixList.Count == 0 && (_fixTiles.Count > 0 || _fixWalls.Count > 0 || _fixDig.Count > 0)) return all;
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
				cc.Add((ChairCol(i), _floorRow, H_CHAIR.ToString()));
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
