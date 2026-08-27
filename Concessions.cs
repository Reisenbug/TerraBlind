using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ObjectData;
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

		// 锚点:放东西要求四周/底下有支撑。这是【卡住我们最多】的一道门 --
		// 悬空处放不出第一格,于是要先砌柱子造锚(Cell.Pillar,价 45 vs Build 的 15),
		// 而造锚本身又要锚点,Unstick.MakeFooting 那条递归就是这么来的(房子里那块野方块)。
		//
		// vanilla 判据在 TileObject.CanPlace(TileObject.cs:176),问的是 TileObjectData 的
		// AnchorTop/Bottom/Left/Right/Wall。每一段都长这样:
		//     if (tileData.AnchorBottom.tileCount != 0) { ...检查... }
		// 所以把 tileCount 清零 = 那段整个短路 = 锚点检查形同不存在。
		// 改的是数据不是代码,不用 IL 补丁。
		//
		// 【为什么在 Load 里做】:WriteCheck 在 readOnlyData 时抛 FieldAccessException。
		// 那个标志只在 LockWrites 里置 true,而 LockWrites 全代码没人调 -- 现在写哪儿都行,
		// 但仍然放在启动期,免得哪天 vanilla 真去调它。
		public static void DropAnchorRequirements()
		{
			if (!Enabled) return;
			int cleared = 0;
			for (int type = 0; type < TileID.Count; type++)
			{
				// style/alternate 各有自己的一份 TileObjectData(GetTileData 会逐层下钻),
				// 只清 style 0 的话门/床那些多态的东西照旧要锚点
				for (int style = 0; style < MaxStyleScan; style++)
					for (int alt = 0; alt <= MaxAltScan; alt++)
					{
						TileObjectData d;
						try { d = TileObjectData.GetTileData(type, style, alt); }
						catch { continue; }
						if (d == null) continue;
						if (d.AnchorTop.tileCount == 0 && d.AnchorBottom.tileCount == 0
							&& d.AnchorLeft.tileCount == 0 && d.AnchorRight.tileCount == 0
							&& !d.AnchorWall) continue;
						d.AnchorTop = AnchorData.Empty;
						d.AnchorBottom = AnchorData.Empty;
						d.AnchorLeft = AnchorData.Empty;
						d.AnchorRight = AnchorData.Empty;
						d.AnchorWall = false;
						cleared++;
					}
			}
			DiagLog.Write($"[concession] 清掉 {cleared} 份锚点声明,放置不再要求支撑");
		}
		// GetTileData 对越界的 style 不抛异常,返回的是同一份基础数据,多扫几轮只是白跑。
		// 32/4 够覆盖 vanilla 最多态的那几个(门/床/王座)
		const int MaxStyleScan = 32, MaxAltScan = 4;

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
