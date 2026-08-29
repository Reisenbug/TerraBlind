using System.Collections.Generic;
using Terraria;
using Terraria.ID;

namespace TerraBlind
{
	// 背包满了箱子就掏不动 —— vanilla 的 LootAll 把塞不下的原样写回箱子里,
	// 好东西看着就在眼前却拿不走。腾位置的办法是【删掉没用的】。
	//
	// 和 ThrowItems 是两条路,别合并:那边是"暂扔地上、合完捡回来",给合成腾一帧用的;
	// 下丛林一路往下走,扔地上的再也不回来,只能真删。
	public static class KeepList
	{
		// 用户点名要的。这七件之外还有一层【一律保住】的兜底(工具/建材/收藏),见 Keep()
		static readonly HashSet<int> Named = new()
		{
			ItemID.BreathingReed,     // 芦苇呼吸管
			ItemID.WoodenBoomerang,   // 木质回旋镖
			ItemID.Spear,             // 长矛
			ItemID.StaffofRegrowth,   // 再生法杖
			ItemID.PortableStool,     // 梯凳
			ItemID.JungleGrassSeeds,  // 丛林草种子
			ItemID.VineRopeCoil,      // 植物纤维绳索宝典
		};

		// 草药 + 草药种子。vanilla 没有草药集合(只有 ItemID.Sets.GrassSeeds,那是草皮种子不是草药),
		// 所以只能点名。七种草药 ID 连号 313..318 + 2358,种子 307..312 + 2357
		static bool Herb(Item it)
		{
			int t = it.type;
			if (t >= ItemID.Daybloom && t <= ItemID.Fireblossom) return true;             // 313..318 草药
			if (t >= ItemID.DaybloomSeeds && t <= ItemID.FireblossomSeeds) return true;   // 307..312 种子
			return t == ItemID.Shiverthorn || t == ItemID.ShiverthornSeeds;
		}

		// 矿石。判据问 vanilla 的 TileID.Sets.Ore,不点名 —— 点名必漏(锡/铅/钨/铂在另一段 ID)。
		// 【锭不算】:锭 createTile 是 -1(不放东西),自然进不来,不用额外判
		public static bool Ore(Item it)
			=> it.createTile >= 0 && it.createTile < TileID.Sets.Ore.Length && TileID.Sets.Ore[it.createTile];

		// 能当建材的:实心方块和平台。桥/柱子/岩浆堤全靠它,留【几种】就够 ——
		// 一路挖下来几十种土石各占一格,那才是把背包塞满的东西
		static bool Buildable(Item it)
			=> it.createTile >= 0 && it.createTile < Main.tileSolid.Length && Main.tileSolid[it.createTile];

		// 留几种建材。一种不够(挖没了就断料),留太多等于没删。
		// 木头不占名额:Concessions 每帧把它补到 9999,按 stack 排它永远第一,
		// 会把真正稀缺的那几种挤出去
		public const int BuildKinds = 3;

		// 【无条件留】的:清单上的、工具、收藏。和建材无关 —— 建材按数量另算
		public static bool Keep(Item it)
		{
			if (it == null || it.IsAir) return true;
			if (it.favorited) return true;
			if (it.pick > 0 || it.axe > 0 || it.hammer > 0) return true;
			if (it.createWall >= 0) return true;   // 墙:盖房子要,而且种类本来就少
			if (Named.Contains(it.type)) return true;
			if (Herb(it)) return true;
			if (Ore(it)) return true;
			return false;
		}

		// 删到有 want 个空格。返回真正的空格数。
		//
		// 两轮。第一轮删纯杂物(既不在清单也不是建材),第二轮才动多余的建材 ——
		// 建材还有用,只是不需要几十种,所以【留最多的几摞】,删剩下的零头
		public static int MakeRoom(int want)
		{
			var p = Main.LocalPlayer;
			if (p == null || !p.active) return 0;
			int free = Free(p);
			if (free >= want) return free;

			int dropped = 0;
			// 第一轮:杂物。从后往前 —— 靠后的槽是一路顺手捡的,靠前的是开局带的
			for (int i = InvEnd - 1; i >= HotbarEnd && free + dropped < want; i--)
			{
				var it = p.inventory[i];
				if (it == null || it.IsAir) continue;
				if (Keep(it) || Buildable(it)) continue;
				DiagLog.Write($"[keep] 删杂物 {it.Name}x{it.stack} (槽{i})");
				p.inventory[i] = new Item();
				dropped++;
			}

			// 第二轮:多余的建材。按 stack 从大到小排,前 BuildKinds 摞留着,后面的删
			if (free + dropped < want)
			{
				var mats = new List<(int stack, int slot)>();
				for (int i = HotbarEnd; i < InvEnd && i < p.inventory.Length; i++)
				{
					var it = p.inventory[i];
					if (it == null || it.IsAir) continue;
					if (Keep(it) || !Buildable(it)) continue;
					if (it.type == ItemID.Wood) continue;   // 无限供应,不占名额也不用删
					mats.Add((it.stack, i));
				}
				mats.Sort((a, b) => b.stack.CompareTo(a.stack));
				for (int k = BuildKinds; k < mats.Count && free + dropped < want; k++)
				{
					var it = p.inventory[mats[k].slot];
					if (it == null || it.IsAir) continue;
					DiagLog.Write($"[keep] 删多余建材 {it.Name}x{it.stack} (槽{mats[k].slot}) 已留{BuildKinds}种");
					p.inventory[mats[k].slot] = new Item();
					dropped++;
				}
			}

			int now = Free(p);
			if (dropped > 0) Recipe.FindRecipes();
			DiagLog.Write(dropped == 0
				? $"[keep] 要{want}格但没有可删的(全在清单里) 空格={now}"
				: $"[keep] 删了{dropped}件,空格 {free}→{now}");
			return now;
		}

		const int InvEnd = 50;      // 50..57 是钱币/弹药格,不动
		const int HotbarEnd = 10;   // 0..9 是手上要用的,别动 —— HomeInHotbar 把镐/平台钉在这儿

		public static int Free(Player p)
		{
			int n = 0;
			for (int i = 0; i < InvEnd && i < p.inventory.Length; i++)
				if (p.inventory[i] == null || p.inventory[i].IsAir) n++;
			return n;
		}
	}
}
