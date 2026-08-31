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
		// 【用户点名要扔的】。不是"扔了也行",是这一路上确实用不着,占格子而已。
		// 名单会加,加在这儿就行
		static readonly HashSet<int> Junk = new()
		{
			ItemID.BreathingReed,     // 芦苇呼吸管
			ItemID.WoodenBoomerang,   // 木质回旋镖
			ItemID.Spear,             // 长矛
			ItemID.StaffofRegrowth,   // 再生法杖
			ItemID.PortableStool,     // 梯凳
			ItemID.JungleGrassSeeds,  // 丛林草种子
			ItemID.VineRopeCoil,      // 植物纤维绳索宝典
			// 【留着会招错人】。带枪会让军火商满足入住条件先来占位,爆破专家就可能不来了 ——
			// 而整条线全指望爆破专家卖雷管
			ItemID.Boomstick,         // 三发猎枪
		};

		// 草药 + 草药种子,全扔。vanilla 没有草药集合(ItemID.Sets.GrassSeeds 是草皮种子,不是这个),
		// 所以只能点名。草药 313..318 连号 + 2358,种子 307..312 连号 + 2357
		static bool Herb(Item it)
		{
			int t = it.type;
			if (t >= ItemID.Daybloom && t <= ItemID.Fireblossom) return true;             // 313..318 草药
			if (t >= ItemID.DaybloomSeeds && t <= ItemID.FireblossomSeeds) return true;   // 307..312 种子
			return t == ItemID.Shiverthorn || t == ItemID.ShiverthornSeeds;
		}

		// 矿石,全扔。判据问 vanilla 的 TileID.Sets.Ore,不点名 —— 点名必漏(锡/铅/钨/铂在另一段 ID)。
		// 【锭不扔】:用户说的是矿物不含锭。锭 createTile 是 -1,进不了这个判据,自然留着
		static bool Ore(Item it)
			=> it.createTile >= 0 && it.createTile < TileID.Sets.Ore.Length && TileID.Sets.Ore[it.createTile];

		// 扔不扔。矿石虽然能放置(是方块),但用户点名要扔 —— 所以【先判扔再判留】,
		// 顺序反了矿石会被"能放置的都是建材"那条救回来
		public static bool Drop(Item it)
		{
			if (it == null || it.IsAir) return false;
			if (it.favorited) return false;   // 收藏 = 用户自己钉的,任何名单都盖不过
			return Junk.Contains(it.type) || Herb(it) || Ore(it);
		}

		// 【拿到就扔】。人的做法是 ctrl+左键丢垃圾桶,不等背包满 —— 名单上的东西留着本身就是问题:
		// 三发猎枪会让军火商满足入住条件先来占房,爆破专家就不来了,而整条线全指望他卖雷管。
		// MakeRoom 只在"腾地方"时才动手,背包有空格就一件不删,盖不住这个语义。
		public static void Sweep()
		{
			var p = Main.LocalPlayer;
			if (p == null || !p.active) return;
			int n = 0;
			// 热键栏也扫:枪进了 0..9 照样招人。HomeInHotbar 钉的是镐/平台,不在名单上,不会被误删
			for (int i = 0; i < InvEnd && i < p.inventory.Length; i++)
			{
				var it = p.inventory[i];
				if (!Drop(it)) continue;
				DiagLog.Write($"[keep] 拿到就扔 {it.Name}x{it.stack} (槽{i})");
				p.inventory[i] = new Item();
				n++;
			}
			if (n > 0) Recipe.FindRecipes();
		}

		// 删到有 want 个空格。返回真正的空格数。
		public static int MakeRoom(int want)
		{
			var p = Main.LocalPlayer;
			if (p == null || !p.active) return 0;
			int free = Free(p);
			if (free >= want) return free;

			int dropped = 0;
			// 从后往前:靠后的槽是一路顺手捡的,靠前的是开局带的和刚掏出来的
			for (int i = InvEnd - 1; i >= HotbarEnd && free + dropped < want; i--)
			{
				var it = p.inventory[i];
				if (!Drop(it)) continue;
				DiagLog.Write($"[keep] 删 {it.Name}x{it.stack} (槽{i})");
				p.inventory[i] = new Item();
				dropped++;
			}
			int now = Free(p);
			if (dropped > 0) Recipe.FindRecipes();
			DiagLog.Write(dropped == 0
				? $"[keep] 要{want}格但名单上的一件都没有,空格={now}"
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
