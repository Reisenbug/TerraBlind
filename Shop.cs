using Terraria;
using Terraria.ID;

namespace TerraBlind
{
	// 商店里的钱怎么算、东西怎么卖。判据全问原版要,不自己写死价格表。
	public static class Shop
	{
		// 铜 → "1金5银5铜"。原版是 100 进制:铜/银/金/铂
		public static string Coins(long copper)
		{
			if (copper <= 0) return "0铜";
			long p = copper / 1000000; copper %= 1000000;
			long g = copper / 10000;   copper %= 10000;
			long s = copper / 100;     copper %= 100;
			var sb = new System.Text.StringBuilder();
			if (p > 0) sb.Append(p).Append('铂');
			if (g > 0) sb.Append(g).Append('金');
			if (s > 0) sb.Append(s).Append('银');
			if (copper > 0) sb.Append(copper).Append('铜');
			return sb.ToString();
		}

		// 身上有多少钱(铜)。硬币 id 71~74,面值差 100 倍
		public static long Money(Player p)
		{
			long n = 0;
			foreach (var it in p.inventory)
			{
				if (it == null || it.IsAir) continue;
				if (it.type == ItemID.CopperCoin) n += it.stack;
				else if (it.type == ItemID.SilverCoin) n += it.stack * 100L;
				else if (it.type == ItemID.GoldCoin) n += it.stack * 10000L;
				else if (it.type == ItemID.PlatinumCoin) n += it.stack * 1000000L;
			}
			return n;
		}

		// 【一件】卖多少钱。原版:calcForSelling / 5,不足 1 按 1 算(Player.cs:34513)。
		// 别拿 item.value 当售价 —— 那是原价,卖出去只有五分之一
		public static long SellUnit(Player p, Item it)
		{
			if (it == null || it.IsAir) return 0;
			p.GetItemExpectedPrice(it, out long sell, out _);
			if (sell <= 0) return 0;
			long u = sell / 5;
			return u < 1 ? 1 : u;
		}

		// 卖掉一格。【照抄原版右键那条路】(ItemSlot.cs:772) —— 光调 SellItem 只是把钱
		// 塞进背包,货架不会多出这件东西,也就没法反悔买回来。原版还会出声、发界面提示。
		// 0 价的东西原版照收(只是没钱),不能当失败
		public static bool Sell(Player p, int slot, out string why)
		{
			why = "";
			if (slot < 0 || slot >= p.inventory.Length) { why = "slot_out_of_range"; return false; }
			var it = p.inventory[slot];
			if (it == null || it.IsAir) { why = "empty"; return false; }
			if (p.talkNPC < 0 || Main.npcShop <= 0) { why = "shop_not_open"; return false; }
			var chest = Main.instance.shop[Main.npcShop];
			if (chest == null) { why = "no_shop_chest"; return false; }

			long got = SellUnit(p, it) * it.stack;
			bool paid = p.SellItem(it);
			// SellItem 失败只有两种:一分不值(那是原版的 value==0 分支,照收),
			// 或者钱塞不下 —— 后者原版会把整个背包回滚,东西还在,直接报出去
			if (!paid && it.value != 0) { why = "no_room_for_coins"; return false; }

			chest.AddItemToShop(it);                 // 放上货架,能原价买回来
			it.TurnToAir();                          // 原版用这个清,不是 new Item()
			// 18=Coins 卖出去了,7=Grab 白送(0价物品)。AnnounceTransfer 不调 ——
			// 那是鼠标操作的浮动提示,ItemSlot 的嵌套类型,对功能没影响
			Terraria.Audio.SoundEngine.PlaySound(paid ? SoundID.Coins : SoundID.Grab);
			DiagLog.Write($"[shop] 卖 {it.Name} 得 {Coins(paid ? got : 0)}");
			return true;
		}
	}
}
