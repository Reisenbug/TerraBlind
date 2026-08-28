using System.Collections.Generic;
using Terraria;
using Terraria.ID;

namespace TerraBlind
{
	// 做不成怎么办 --- 只有这一份,而且会【递归】。
	//
	// 以前每个原语在失败前各自想办法(或者干脆不想),同一种阻挡在一个原语里会挖开、
	// 在另一个里直接失败。归并完 17 个动作类的失败原因,只剩 6 种,而且互相递归:
	//   缺椅子 -> 合椅子 -> 缺工作台(够不着/没有) + 缺木头 -> 砍树 -> 缺斧头 -> 合斧头 ...
	//
	// 用法:原语调 Stuck(b) 而不是直接 Finish。这里能解就压栈去解,解完让原语重试;
	// 解不了才让它真失败,失败带整条链条上报。
	public static class Unstick
	{
		public const int HopelessAt = 3;   // 同一个坎救这么多次还在,就是真无解
		public const int MaxDepth = 8;     // 递归上限。到底了还没解决就认输,别无限往下挖

		struct Frame
		{
			public Blocker B;
			public string Who;
			public int Tries;
			public override string ToString() => $"{Who}:{B}";
		}

		static readonly List<Frame> _stack = new();
		public static string LastAction = "";
		public static string FailChain = "";   // 失败时整条链条,给日志和上报用

		public static bool Busy => _stack.Count > 0;
		public static int Depth => _stack.Count;

		public static void Reset() { _stack.Clear(); LastAction = ""; FailChain = ""; }

		// 原语调这个。true = 已经接手了,调用方这一轮别往下走,下一轮重试
		public static bool Handle(string who, Blocker b)
		{
			// 同一个坎又回来了 = 上一次的解法没起作用
			for (int i = 0; i < _stack.Count; i++)
				if (Same(_stack[i].B, b) && _stack[i].Who == who)
				{
					// 【上一次的解法还在跑就不算一次重试】。Tries 的本意是"试了 3 次都没用",
					// 但调用方是【每帧】调的 —— 寻路刚启动 3 帧就被判死,人还没走两步。
					// (2431,1045) 那次:寻路启动 → "靠近中" ×2 → 放弃,而 RecedingNav
					// 手上有 37 条候选、walk 一步就能把 H 从 181 降到 82。
					if (Working()) { Solve(who, b); return true; }
					var f = _stack[i];
					f.Tries++;
					_stack[i] = f;
					if (f.Tries > HopelessAt) { Give(who, b, $"救了{HopelessAt}次还卡着"); return false; }
					return Solve(who, b);
				}

			if (b.Kind == BlockKind.Hopeless) { Give(who, b, "真无解"); return false; }
			if (_stack.Count >= MaxDepth) { Give(who, b, $"递归深到{MaxDepth}层"); return false; }

			_stack.Add(new Frame { B = b, Who = who, Tries = 1 });
			return Solve(who, b);
		}

		// 上一次派出去的解法还在执行中吗。在跑就别数重试 —— 数的该是"试过几次没用",
		// 不是"问过几帧"。这几样都是自带终止条件的原语,跑完自然停
		static bool Working()
			=> SettleAt.IsRunning || RecedingNav.Active || PillarUp.IsRunning || PlatformDown.IsRunning
			   || BridgeBuilder.IsRunning || PlaceAction.IsRunning || MineCoordinator.IsActive
			   || ItemUseCoordinator.IsActive;

		// 这一层解决了,弹栈
		public static void Done(string who, Blocker b)
		{
			for (int i = _stack.Count - 1; i >= 0; i--)
				if (Same(_stack[i].B, b) && _stack[i].Who == who) { _stack.RemoveAt(i); return; }
		}

		static bool Same(Blocker a, Blocker b)
			=> a.Kind == b.Kind && a.Wx == b.Wx && a.Wy == b.Wy && a.ItemId == b.ItemId;

		static void Give(string who, Blocker b, string why)
		{
			var sb = new System.Text.StringBuilder();
			foreach (var f in _stack) sb.Append(f).Append(" <- ");
			sb.Append($"{who}:{b}");
			FailChain = sb.ToString();
			DiagLog.Write($"[unstick] 放弃({why}) 链条: {FailChain}");
			_stack.Clear();
		}

		static bool Solve(string who, Blocker b)
		{
			var p = Main.LocalPlayer;
			if (p == null) return false;
			bool ok = b.Kind switch
			{
				BlockKind.Terrain => Dig(p, b),
				BlockKind.SelfInWay => StepAside(p, b),
				BlockKind.OutOfReach => Approach(p, b),
				BlockKind.NoFooting => MakeFooting(p, b),
				BlockKind.NoItem => GetItem(p, b),
				BlockKind.NoTool => GetItem(p, b),
				_ => false
			};
			DiagLog.Write($"[unstick] 深{_stack.Count} {who} {b} -> {(ok ? LastAction : "没招了")}");
			if (!ok) Give(who, b, "这一类没解法了");
			return ok;
		}

		// --- 六类解法 ---

		// 目标格被占:挖掉
		static bool Dig(Player p, Blocker b)
		{
			if (ClearWay.Dig(p, b.Wx, b.Wy, "unstick")) { LastAction = $"挖({b.Wx},{b.Wy})"; return true; }
			// 【这把镐挖不动】(地狱熔炉/祭坛/神庙砖)。得如实说,不然会被下面两条误诊成
			// "够不着"或"没镐" —— 那两条都会去做无用功,而真相是这格永远挖不开,只能绕
			if (Predicates.IsWall(b.Wx, b.Wy) && DigTable.CostFrames(b.Wx, b.Wy) >= DigTable.Unmineable)
				return Handle("unstick", new Blocker(BlockKind.Hopeless, b.Wx, b.Wy,
					$"tile{Main.tile[b.Wx, b.Wy].TileType}这把镐挖不动,得绕"));
			if (!p.IsInTileInteractionRange(b.Wx, b.Wy, Terraria.DataStructures.TileReachCheckSettings.Simple))
				return Handle("unstick", new Blocker(BlockKind.OutOfReach, b.Wx, b.Wy, "要挖但够不着"));
			if (ClearWay.PickSlot(p) < 0)
				return Handle("unstick", Blocker.Tool(Terraria.ID.ItemID.CopperPickaxe, "挖需要镐"));
			// 平台不归 ClearWay 管(平时穿过去就行),挡在身体里时得拆
			if (Predicates.IsPlatform(b.Wx, b.Wy) && !ItemUseCoordinator.IsActive)
			{
				ItemUseCoordinator.Start(new ItemUseRequest
				{ TargetWx = b.Wx, TargetWy = b.Wy, Slot = ClearWay.PickSlot(p), Strict = true });
				LastAction = $"拆平台({b.Wx},{b.Wy})";
				return true;
			}
			return false;
		}

		// 人自己压着要动的格子:往离目标远的那边让
		static bool StepAside(Player p, Blocker b)
		{
			if (SettleAt.IsRunning) { LastAction = "让位中"; return true; }
			int cx = ActExecutor.OriginCx(p);
			int away = cx <= b.Wx ? -1 : 1;
			var (bl, br) = Predicates.BodyCols(p);
			int to = cx + away * (br - bl + 1);
			if (!SettleAt.Start(to, out _)) return false;
			LastAction = $"让到{to}列";
			return true;
		}

		// 够不着:走过去。同高就横着靠,高低差大就先造落脚点上下去
		static bool Approach(Player p, Blocker b)
		{
			if (SettleAt.IsRunning || RecedingNav.Active) { LastAction = "靠近中"; return true; }
			int cx = ActExecutor.OriginCx(p), cy = ActExecutor.OriginCy(p);
			if (System.Math.Abs(b.Wy - cy) <= 1)
			{
				int want = b.Wx + (cx <= b.Wx ? -2 : 2);
				if (SettleAt.Start(want, out _)) { LastAction = $"靠到{want}列"; return true; }
			}
			// 差得远就交给寻路,它自己会挖会搭
			RecedingNav.Start(b.Wx, b.Wy, RecedingNav.Mode.Reach);
			LastAction = $"寻路去({b.Wx},{b.Wy})";
			return true;
		}

		// 没地方站:造一个。
		// 先看是不是【就差这一格】--- 家具下面缺一块支撑就属于这种,补一格就完事,
		// 用不着搭桥搭柱子。够得着且四周有锚点就直接放。
		static bool MakeFooting(Player p, Blocker b)
		{
			if (PillarUp.IsRunning || PlatformDown.IsRunning || BridgeBuilder.IsRunning || PlaceAction.IsRunning)
			{ LastAction = "造落脚点中"; return true; }
			if (!Main.tile[b.Wx, b.Wy].HasTile
				&& p.IsInTileInteractionRange(b.Wx, b.Wy, Terraria.DataStructures.TileReachCheckSettings.Simple))
			{
				bool wantPlat = b.Detail.Contains("平台");
				int fillId = wantPlat ? PlatformItem(p) : BlockItem(p);
				if (fillId < 0)
					return Handle("unstick", Blocker.Item(wantPlat ? ItemID.WoodPlatform : ItemID.DirtBlock, 20, "补支撑没料"));
				if (!MazeWand.PlatformAnchor(b.Wx, b.Wy))
					return Handle("unstick", new Blocker(BlockKind.NoFooting, b.Wx, b.Wy + 1, "补这格前得先有锚点"));
				if (PlaceAction.Start(fillId.ToString(), b.Wx, b.Wy, 1, 0, 0, true, out _))
				{ LastAction = $"补一格({b.Wx},{b.Wy})"; return true; }
			}
			int cx = ActExecutor.OriginCx(p), cy = ActExecutor.OriginCy(p);
			int plat = PlatformItem(p);
			if (plat < 0) return Handle("unstick", Blocker.Item(Terraria.ID.ItemID.WoodPlatform, 20, "造落脚点没平台"));
			string item = plat.ToString();
			if (b.Wy < cy - 1 && PillarUp.Start(item, cy - b.Wy, cx, out _))
			{ LastAction = $"pillar升{cy - b.Wy}"; return true; }
			if (b.Wy > cy + 1 && PlatformDown.Start(item, b.Wy, out _))
			{ LastAction = $"平台梯降到{b.Wy}"; return true; }
			int n = System.Math.Abs(b.Wx - cx);
			if (n > 0 && BridgeBuilder.Start(item, b.Wx > cx ? "right" : "left", n, out _))
			{ LastAction = $"搭桥{n}格"; return true; }
			return false;
		}

		// 缺料/缺工具:合成。合不出就递归去补它的材料和工作台
		static bool GetItem(Player p, Blocker b)
		{
			if (b.ItemId <= 0) return false;
			int need = b.Count > 0 ? b.Count : 1;
			if (Predicates.Have(b.ItemId) >= need) { LastAction = "已经有了"; return true; }

			CraftCoordinator.Craft(b.ItemId, need - Predicates.Have(b.ItemId));
			if (Predicates.Have(b.ItemId) >= need)
			{ LastAction = $"合出物品{b.ItemId}"; return true; }

			string stop = CraftCoordinator.LastStop;
			DiagLog.Write($"[unstick] 合物品{b.ItemId} 没成:{stop} 现有{Predicates.Have(b.ItemId)}/{need}");
			// no_recipe 有两种:配方要工作台(身边没有),或者这东西【根本不能合】(木头/石头)。
			// 后者要去采,查来源表。
			if (stop == "no_recipe")
			{
				if (StationFor(b.ItemId) >= 0) return NeedStation(p, b);
				return Gather(p, b);
			}
			if (stop == "no_more_materials" || stop == "") return NeedMaterials(p, b);
			return Gather(p, b);
		}

		// 合不出来就去采。来源表里按顺序试,前一个不行换下一个
		static bool Gather(Player p, Blocker b)
		{
			var srcs = ItemSource.For(b.ItemId);
			if (srcs == null) { DiagLog.Write($"[unstick] 物品{b.ItemId} 不在来源表里,不知道从哪弄"); return false; }
			foreach (var s in srcs)
				switch (s.Kind)
				{
					case SourceKind.Tile: if (GatherTile(p, b, s)) return true; break;
					case SourceKind.Chest: if (LootChest(p, b)) return true; break;
					case SourceKind.Npc: if (BuyFrom(p, b, s)) return true; break;
					default: DiagLog.Write($"[unstick] 来源 {s} 还没实现"); break;
				}
			return false;
		}

		// 砍/挖某种方块。够不着就先靠过去,没工具就先弄工具
		static bool GatherTile(Player p, Blocker b, Source s)
		{
			if (ItemUseCoordinator.IsActive) { LastAction = "采集中"; return true; }
			var at = FindTile(p, s.Id, 80);
			if (!at.HasValue) { DiagLog.Write($"[unstick] 80格内没找到 tile {s.Id}"); return false; }
			var (tx, ty) = at.Value;
			if (!p.IsInTileInteractionRange(tx, ty, Terraria.DataStructures.TileReachCheckSettings.Simple))
				return Handle("unstick", new Blocker(BlockKind.OutOfReach, tx, ty, $"要采 tile {s.Id}"));
			int slot = ToolFor(p, s.Id);
			if (slot < 0)
				return Handle("unstick", Blocker.Tool(NeededTool(s.Id), $"采 tile {s.Id} 要工具"));
			ItemUseCoordinator.Start(new ItemUseRequest { TargetWx = tx, TargetWy = ty, Slot = slot, Strict = false });
			LastAction = $"采({tx},{ty}) tile{s.Id}";
			return true;
		}

		// 开箱子拿。找最近一个没开过的
		static readonly HashSet<(int, int)> _looted = new();
		static bool LootChest(Player p, Blocker b)
		{
			var at = FindChest(p, 80);
			if (!at.HasValue) { DiagLog.Write("[unstick] 附近没有没开过的箱子"); return false; }
			var (cx2, cy2) = at.Value;
			if (!p.IsInTileInteractionRange(cx2, cy2, Terraria.DataStructures.TileReachCheckSettings.Simple))
				return Handle("unstick", new Blocker(BlockKind.OutOfReach, cx2, cy2, "要开箱子"));
			_looted.Add((cx2, cy2));
			HttpServerSystem.QueueInteract(cx2, cy2);
			LastAction = $"开箱({cx2},{cy2})";
			return true;
		}

		// 找 NPC 买。钱不够就先卖东西
		static bool BuyFrom(Player p, Blocker b, Source s)
		{
			DiagLog.Write($"[unstick] 向 NPC {s.Id} 买 物品{b.ItemId}: 还没实现");
			return false;
		}

		// 采这种 tile 该用什么工具:树用斧,其余用镐
		static int NeededTool(int tileId)
			=> tileId == TileID.Trees || tileId == TileID.PalmTree ? ItemID.CopperAxe : ItemID.CopperPickaxe;

		// 手上有没有能采它的工具,返回槽位
		static int ToolFor(Player p, int tileId)
		{
			bool wantAxe = tileId == TileID.Trees || tileId == TileID.PalmTree;
			int best = -1, bestPow = 0;
			for (int i = 0; i < 10 && i < p.inventory.Length; i++)
			{
				var it = p.inventory[i];
				if (it == null || it.IsAir) continue;
				int pow = wantAxe ? it.axe : it.pick;
				if (pow > bestPow) { bestPow = pow; best = i; }
			}
			return best;
		}

		static (int x, int y)? FindChest(Player p, int radius)
		{
			int cx = ActExecutor.OriginCx(p), cy = ActExecutor.OriginCy(p);
			for (int r = 1; r <= radius; r++)
				for (int dx = -r; dx <= r; dx++)
					for (int dy = -r; dy <= r; dy++)
					{
						if (System.Math.Abs(dx) != r && System.Math.Abs(dy) != r) continue;
						int x = cx + dx, y = cy + dy;
						if (!Predicates.InBounds(x, y)) continue;
						var t = Main.tile[x, y];
						if (!t.HasTile) continue;
						if (t.TileType != TileID.Containers && t.TileType != TileID.Containers2) continue;
						if (t.TileFrameX % 36 != 0 || t.TileFrameY % 36 != 0) continue;
						if (_looted.Contains((x, y))) continue;
						if (Chest.IsLocked(x, y)) continue;
						return (x, y);
					}
			return null;
		}

		// 配方要工作台,身边没有:先找一台走过去,再没有就合一台
		static bool NeedStation(Player p, Blocker b)
		{
			int tile = StationFor(b.ItemId);
			if (tile < 0) return false;
			var near = FindTile(p, tile, 60);
			if (near.HasValue)
				return Handle("unstick", new Blocker(BlockKind.OutOfReach, near.Value.x, near.Value.y, $"要用{tile}号工作台"));
			int mk = ItemThatPlaces(tile);
			if (mk <= 0) return false;
			if (Predicates.Have(mk) > 0)
			{
				// 有工作台没放:就地放一个
				int cx = ActExecutor.OriginCx(p), cy = ActExecutor.OriginCy(p);
				var (bl, br) = Predicates.BodyCols(p);
				int at = cx <= bl ? br + 1 : bl - 1;
				if (PlaceAction.Start(mk.ToString(), at, cy, 1, 0, 0, true, out _))
				{ LastAction = $"放工作台在({at},{cy})"; return true; }
				return false;
			}
			return Handle("unstick", Blocker.Item(mk, 1, "合成要工作台"));
		}

		// 材料不够:挑第一样缺的往下递归
		static bool NeedMaterials(Player p, Blocker b)
		{
			for (int i = 0; i < Main.recipe.Length; i++)
			{
				var r = Main.recipe[i];
				if (r == null || r.createItem == null || r.createItem.type != b.ItemId) continue;
				foreach (var ing in r.requiredItem)
				{
					if (ing == null || ing.type <= 0) continue;
					if (Predicates.Have(ing.type) < ing.stack)
						return Handle("unstick", Blocker.Item(ing.type, ing.stack, $"合物品{b.ItemId}要的料"));
				}
			}
			return false;
		}

		// --- 小工具 ---

		static int PlatformItem(Player p)
		{
			for (int i = 0; i < 58 && i < p.inventory.Length; i++)
			{
				var it = p.inventory[i];
				if (it != null && !it.IsAir && it.createTile >= 0
					&& Main.tileSolidTop[it.createTile] && it.stack > 0) return it.type;
			}
			return -1;
		}

		// 背包里第一样能当方块放的东西。岩浆自救也用这一份(方块不怕烧,平台怕)
		public static int BlockItem(Player p)
		{
			if (p == null || p.inventory == null) return -1;
			for (int i = 0; i < 58 && i < p.inventory.Length; i++)
			{
				var it = p.inventory[i];
				if (it == null || it.IsAir || it.stack <= 0) continue;
				if (it.createTile >= 0 && Main.tileSolid[it.createTile] && !Main.tileSolidTop[it.createTile])
					return it.type;
			}
			return -1;
		}

		static int StationFor(int itemId)
		{
			for (int i = 0; i < Main.recipe.Length; i++)
			{
				var r = Main.recipe[i];
				if (r == null || r.createItem == null || r.createItem.type != itemId) continue;
				foreach (int t in r.requiredTile) if (t >= 0) return t;
			}
			return -1;
		}

		// 5000 个物品逐个 SetDefaults 很贵,查过一次就记住
		static readonly Dictionary<int, int> _placesCache = new();
		static int ItemThatPlaces(int tileId)
		{
			if (_placesCache.TryGetValue(tileId, out int hit)) return hit;
			int found = -1;
			for (int i = 0; i < Terraria.ID.ItemID.Count && found < 0; i++)
			{
				var probe = new Item();
				probe.SetDefaults(i);
				if (probe.createTile == tileId) found = i;
			}
			_placesCache[tileId] = found;
			return found;
		}

		static (int x, int y)? FindTile(Player p, int tileId, int radius)
		{
			int cx = ActExecutor.OriginCx(p), cy = ActExecutor.OriginCy(p);
			for (int r = 1; r <= radius; r++)
				for (int dx = -r; dx <= r; dx++)
					for (int dy = -r; dy <= r; dy++)
					{
						if (System.Math.Abs(dx) != r && System.Math.Abs(dy) != r) continue;
						int x = cx + dx, y = cy + dy;
						if (!Predicates.InBounds(x, y)) continue;
						var t = Main.tile[x, y];
						if (t.HasTile && t.TileType == tileId) return (x, y);
					}
			return null;
		}
	}
}
