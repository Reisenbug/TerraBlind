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

		// 伸手范围加成。vanilla tileRangeX=5 tileRangeY=4,每帧 ResetEffects 清零 blockRange
		// 再由饰品累加,所以这里每帧加,和饰品是同一条路,不动 static 字段
		// (改 static 会让所有读 tileRangeX 的地方跟着变,包括规划器自己的判据)
		public const int ReachBoost = 8;

		// 放置/挖掘用时倍率。1/8 让一次放置从十几帧压到一两帧,
		// 空中放置来不来得及、放置和飞行的时序竞争,这一整类问题就消失了
		public const float UseTimeMul = 0.125f;

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

		// 人自己的碰撞箱挡着放不了东西 —— 掉进岩浆时这是【致命】的:
		// 要填的那格正是人泡着的那格,人不挪开就永远填不上,而挪不开正是被困住的原因。
		//
		// vanilla 判据 Collision.EmptyTile(Collision.cs:1418):拿 16x16 的格矩形去和
		// player.position/width/height 做 Intersects。读的是【玩家字段】,不是常量 --
		// 所以放置那一刻把碰撞箱缩掉,相交为假,门自己就开了。
		//
		// 挂 PreItemCheck/PostItemCheck:它俩正好夹住 ItemCheck_Inner(Player.cs:42334/42338),
		// PlaceThing 就在中间。挂在 PostUpdateEverything 没用 —— 那时 ItemCheck 早跑完了。
		// 只在【我们自己要放东西】的那一帧缩,别的时候一律原样,免得影响碰撞/受击。
		public static bool ShrinkHitboxThisFrame;
		static int _savedW, _savedH;
		static bool _shrunk;

		public override bool PreItemCheck()
		{
			if (!Enabled || !ShrinkHitboxThisFrame || Player.whoAmI != Main.myPlayer) return true;
			_savedW = Player.width; _savedH = Player.height;
			// 缩到 0 而不是挪走:挪坐标会让这一帧的放置瞄准/够得着判据跟着漂
			Player.width = 0; Player.height = 0;
			_shrunk = true;
			return true;
		}

		public override void PostItemCheck()
		{
			// 旗子【一帧有效】。不在这儿清的话碰撞箱会一直是 0,人从此穿墙掉出世界
			ShrinkHitboxThisFrame = false;
			if (!_shrunk) return;
			Player.width = _savedW; Player.height = _savedH;
			_shrunk = false;
		}

		public override void PostUpdateEquips()
		{
			if (!Enabled) return;
			Player.blockRange += ReachBoost;
		}

		public override void PostUpdate()
		{
			if (!Enabled || Player.whoAmI != Main.myPlayer) return;
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
		public override float UseTimeMultiplier(Item item, Player player)
			=> Concessions.Enabled ? Concessions.UseTimeMul : 1f;

		public override float UseAnimationMultiplier(Item item, Player player)
			=> Concessions.Enabled ? Concessions.UseTimeMul : 1f;
	}
}
