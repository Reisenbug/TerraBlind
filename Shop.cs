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

		// 卖掉一格。原版 SellItem 自己处理"刚买的原价退"和把钱塞进背包
		public static bool Sell(Player p, int slot, out string why)
		{
			why = "";
			if (slot < 0 || slot >= p.inventory.Length) { why = "slot_out_of_range"; return false; }
			var it = p.inventory[slot];
			if (it == null || it.IsAir) { why = "empty"; return false; }
			if (p.talkNPC < 0) { why = "shop_not_open"; return false; }
			long got = SellUnit(p, it) * it.stack;
			if (!p.SellItem(it)) { why = "worthless"; return false; }
			DiagLog.Write($"[shop] 卖 {it.Name}x{it.stack} 得 {Coins(got)}");
			p.inventory[slot] = new Item();
			return true;
		}
	}
}
