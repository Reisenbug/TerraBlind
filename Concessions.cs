using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraBlind
{
	// 对项目的让步:把游戏调简单一点,好让"能不能跑通全程"这件事不被物料和手臂长度淹没。
	//
	// 每一条都是【环境】的让步,不是给规划器开后门 -- 规划器照旧要自己找路、自己判够不够得着,
	// 只是够得着的范围大一点、料不会突然没了。这样失败还是真失败,只是少一批噪音。
	public class Concessions : ModPlayer
	{
		// 木头永远够。房子的地板/平台/家具/墙全从木头合出来,断在半路时留下的是
		// "椅子从4变3、walkplace 卡在物品34没了"这种查不动的现场
		public const int WoodKeep = 9999;

		// 伸手范围加成。【已归零】—— 全流程跑通之后回到原版距离,只剩捅向导那一段
		// 单独开 LongArm。留着这个常量是因为好几处判据的注释拿它举例(隔墙够得着但脚过不去),
		// 归零之后那些分支自然不再触发,但代码路径还在
		public const int ReachBoost = 0;

		// 捅向导那一段专用的【超长手臂】。ReachBoost 只加 blockRange,只有放置吃得到;
		// 挖和对话都以 static tileRangeX 为底,所以要够到向导脚下的每一列,只能动这个 static。
		// 【动了就必须还回去】—— 它是全局的,规划器的判据也读它,留着不还会让整个寻路以为
		// 手能伸 30 格。开关成对出现在 WofPrep:BackToGuide 开,Patch 收尾关
		public const int LongArm = 30;
		static int _savedRangeX = -1, _savedRangeY = -1;
		public static bool LongArmOn => _savedRangeX >= 0;
		public static void LongArmBegin()
		{
			if (_savedRangeX >= 0) return;   // 已经开着,别把加长后的值当原值存下来
			_savedRangeX = Player.tileRangeX; _savedRangeY = Player.tileRangeY;
			Player.tileRangeX = LongArm; Player.tileRangeY = LongArm;
			DiagLog.Write($"[reach] 手臂加长到{LongArm}格(原{_savedRangeX}/{_savedRangeY})");
		}
		public static void LongArmEnd()
		{
			if (_savedRangeX < 0) return;
			Player.tileRangeX = _savedRangeX; Player.tileRangeY = _savedRangeY;
			DiagLog.Write($"[reach] 手臂还原到{_savedRangeX}/{_savedRangeY}");
			_savedRangeX = _savedRangeY = -1;
		}

		// 放置/挖掘用时倍率。【已还原成 1】—— 全流程跑通了,回到原版速度。
		// 提速当初是为了绕开"空中放置来不来得及""放置和飞行的时序竞争"这一类时序问题,
		// 还原之后这些会重新露头:一次放置回到十几帧,ItemUseCoordinator 要等更久
		public const float UseTimeMul = 1f;

		public static bool Enabled = true;

		// 沾到岩浆就放不下东西 = 掉进去出不来。自救要靠【方块】向上堤,
		// 不能靠平台 -- 平台放进岩浆会立即被烧毁。
		//
		// vanilla 的门(Player.cs:40728 CheckLavaBlocking)对两者判据不同:
		//   方块是 tileSolid -> 第一行无条件 return true,根本问不到 tileLavaDeath
		//   平台是 tileSolidTop -> 才走 CheckLiquidPlacement 问 tileLavaDeath[19]
		// 所以改 tileLavaDeath 对方块完全无效。而那个函数是 private,tML 没给 hook。
		//
		// 但它只在【目标格有岩浆】时才触发。所以放之前把那一格的液体抹掉,
		// 门的前提就没了,vanilla 自己就放行。那格本来就要被方块填掉,
		// 岩浆消失和被填掉结果一样。
		public static void ClearLavaForPlacement(int wx, int wy)
		{
			if (!Enabled) return;
			if (wx < 0 || wy < 0 || wx >= Main.maxTilesX || wy >= Main.maxTilesY) return;
			var t = Main.tile[wx, wy];
			if (t.LiquidAmount == 0 || t.LiquidType != LiquidID.Lava) return;
			t.LiquidAmount = 0;
			// 不叫 Liquid.LiquidUpdate:邻格的岩浆会立马流回来把门重新关上。
			// 放完方块占住这格,自然就流不进来了。
			NetMessage.SendTileSquare(-1, wx, wy, 1);
		}

		// 这东西放进岩浆会不会当场烧没。平台会(tileLavaDeath[19]=true),方块不会。
		// 抹掉岩浆能让它【放得下】,但烧不烧是放下【之后】的事,抹岩浆管不着 --
		// 所以自救向上堤只能用方块。判据问 vanilla 的表,不硬编"平台会烧"。
		public static bool BurnsInLava(string itemSpec)
		{
			int slot = PlaceAction.ResolveSlot(itemSpec);
			if (slot < 0) return false;
			var it = Main.LocalPlayer?.inventory[slot];
			if (it == null || it.IsAir || it.createTile < 0) return false;
			return Main.tileLavaDeath[it.createTile];
		}

		// 【人碰撞箱内不能放东西 —— 这条走原版逻辑,不绕】。
		// 试过在 PreItemCheck 里把 width/height 临时缩成 0 骗过 Collision.EmptyTile,
		// 结果是人一旦被封在方块里,vanilla 的挤出算炸:一帧飞几千格到地图边缘
		// (DragonLens 传送进石头里也复现)。人要放东西就自己让开。

		// 【人要碰到岩浆之前,把液面那一层就地变成方块】,人稳稳站上去,根本不进岩浆。
		//
		// 比"掉进去再堤出来"简单得多:掉进去之后人是浮的、四周都是液体、放置要跟浮力赛跑,
		// 而深岩浆池根本堤不出来。接触前凝固只需要改一格,而且那一格【本来就要被填】。
		//
		// 判据是【下一帧会不会碰到】:拿当前位置加速度外推一帧,看碰撞箱会盖到哪几格。
		// 只在【往下掉】时做 —— 横着走进岩浆是寻路的错,该让寻路自己绕(不然一路走一路凝固)。
		public const int FreezeLookahead = 2;   // 往前看几帧。1 帧太紧(放置有延迟),太多会提前凝固没必要的格

		public static void FreezeLavaBeneath(Player p)
		{
			if (!Enabled || p == null || !p.active || p.dead) return;
			if (p.velocity.Y <= 0f) return;   // 没在下落
			int bid = Unstick.BlockItem(p);
			if (bid < 0) return;              // 没方块可用,凝固不了
			int btile = ItemToTile(bid);      // 循环外算一次:里面 new Item() 每列一个太浪费
			if (btile < 0) return;

			// 外推:下落速度乘前瞻帧数,看脚会走到哪一行
			float futureFeet = p.position.Y + p.height + p.velocity.Y * FreezeLookahead;
			int lc = (int)(p.position.X / 16f);
			int rc = (int)((p.position.X + p.width - 1) / 16f);
			int nowFeetRow = (int)((p.position.Y + p.height - 1) / 16f);
			int futFeetRow = (int)(futureFeet / 16f);
			if (futFeetRow <= nowFeetRow) return;

			// 从现在的脚下【逐行】往下扫,先遇到什么就是什么:
			//   整行都有实地 -> 人落在那儿,岩浆轮不到,收工
			//   有岩浆       -> 这就是液面,凝固它
			// 【必须整行判完再决定】。原来在列循环里遇到 HasTile 就 return,
			// 左列有砖右列是岩浆时会漏 —— 人半只脚踩砖半只脚陷进去
			for (int y = nowFeetRow; y <= futFeetRow + 1; y++)
			{
				bool anyLava = false, allSolid = true;
				for (int x = lc; x <= rc; x++)
				{
					if (!Predicates.InBounds(x, y)) continue;
					var t = Main.tile[x, y];
					if (!t.HasTile) allSolid = false;
					if (!t.HasTile && t.LiquidAmount > 0 && t.LiquidType == LiquidID.Lava) anyLava = true;
				}
				if (allSolid) return;      // 整行是地,人落这儿,不用管下面的岩浆
				if (!anyLava) continue;    // 这一行还是空气,接着往下看
				{
					// 找到液面了。人跨几列就凝固几列 —— 只凝一列的话另一列还是液体,人会歪着陷进去
					for (int c = lc; c <= rc; c++)
					{
						if (!Predicates.InBounds(c, y)) continue;
						var ct = Main.tile[c, y];
						if (ct.HasTile) continue;
						if (ct.LiquidAmount == 0 || ct.LiquidType != LiquidID.Lava) continue;
						// 【绝不凝在人身体里】。把人封进方块之后 vanilla 的挤出会把他弹走 --
						// 一帧几千格飞到地图边缘。重叠就跳过,下一帧人落低了再凝也来得及
						if (BodyOverlaps(p, c, y)) continue;
						if (!TakeBlock(p, bid)) break;   // 料用完了,能凝几列凝几列
						ct.LiquidAmount = 0;
						ct.HasTile = true;
						ct.TileType = (ushort)btile;
						ct.Slope = SlopeType.Solid;
						ct.IsHalfBlock = false;
						WorldGen.SquareTileFrame(c, y);
						NetMessage.SendTileSquare(-1, c, y, 1);
					}
					DiagLog.Write($"[lava-freeze] 落点({lc}..{rc},{y})是岩浆面,vy={p.velocity.Y:0.##} → 就地凝成方块");
				}
				return;   // 凝好一层就够,人站上去了
			}
		}

		// 这一格和人的碰撞箱重叠吗。判据和 vanilla 的 Collision.EmptyTile 一致
		static bool BodyOverlaps(Player p, int x, int y)
		{
			float l = x * 16f, r = l + 16f, t = y * 16f, b = t + 16f;
			return p.position.X < r && p.position.X + p.width > l
				&& p.position.Y < b && p.position.Y + p.height > t;
		}

		// 从背包扣一个。扣不出来返回 false —— 凭空造方块会让"料够不够"这件事永远查不出问题
		static bool TakeBlock(Player p, int itemId)
		{
			for (int i = 0; i < 58 && i < p.inventory.Length; i++)
			{
				var it = p.inventory[i];
				if (it == null || it.IsAir || it.type != itemId || it.stack <= 0) continue;
				it.stack--;
				if (it.stack <= 0) p.inventory[i].TurnToAir();
				return true;
			}
			return false;
		}

		static int ItemToTile(int itemId)
		{
			var probe = new Item();
			probe.SetDefaults(itemId);
			return probe.createTile;
		}

		public override void PostUpdateEquips()
		{
			if (!Enabled) return;
			Player.blockRange += ReachBoost;
		}

		public override void PostUpdate()
		{
			if (Player.whoAmI != Main.myPlayer) return;
			// 清垃圾和让步无关 —— 关了让步照样得扔,所以排在 Enabled 之前
			KeepList.Sweep();
			if (!Enabled) return;
			TopUpWood();
		}

		// 补到 WoodKeep。找第一摞木头往上加;一摞都没有就塞进空格
		static void TopUpWood()
		{
			var p = Main.LocalPlayer;
			if (p == null || !p.active) return;
			int have = 0, slot = -1;
			for (int i = 0; i < 58 && i < p.inventory.Length; i++)
			{
				var it = p.inventory[i];
				if (it == null || it.IsAir || it.type != ItemID.Wood) continue;
				have += it.stack;
				if (slot < 0) slot = i;
			}
			if (have >= WoodKeep) return;
			if (slot >= 0) { p.inventory[slot].stack += WoodKeep - have; return; }
			for (int i = 0; i < 50 && i < p.inventory.Length; i++)
			{
				var it = p.inventory[i];
				if (it != null && !it.IsAir) continue;
				p.inventory[i] = new Item();
				p.inventory[i].SetDefaults(ItemID.Wood);
				p.inventory[i].stack = WoodKeep;
				return;
			}
		}
	}

	// 用时倍率走 GlobalItem:ModPlayer 的 UseTimeMultiplier 只管手上那件,
	// 这里一次覆盖所有物品,挖和放都在内
	public class ConcessionSpeed : GlobalItem
	{
		// 【雷管不加速】。原版 useTime=useAnimation=40,把投掷节奏卡死在 40 帧 ——
		// 这是打肉山那套距离表的基准。加速到 1/8 会让雷管连珠炮一样出去,
		// 距离/血量全对不上,打起来会出大问题
		static bool NoSpeedup(Item it) => it != null && it.type == ItemID.Dynamite;

		public override float UseTimeMultiplier(Item item, Player player)
			=> Concessions.Enabled && !NoSpeedup(item) ? Concessions.UseTimeMul : 1f;

		public override float UseAnimationMultiplier(Item item, Player player)
			=> Concessions.Enabled && !NoSpeedup(item) ? Concessions.UseTimeMul : 1f;
	}
}
