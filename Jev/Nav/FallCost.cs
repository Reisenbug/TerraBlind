using Terraria;
using Terraria.ID;

namespace TerraBlind
{
	// 摔伤:10×(格数-25),25 格以内白摔。落地前往脚下放蜘蛛网能免掉
	public static class FallCost
	{
		public const int SafeCells = 25;
		public const int PerCell = 10;

		public static int Damage(int fallCells)
			=> fallCells <= SafeCells ? 0 : (fallCells - SafeCells) * PerCell;

		public static int WebCount(Player p)
		{
			int n = 0;
			for (int i = 0; i < p.inventory.Length; i++)
			{
				var it = p.inventory[i];
				if (it != null && !it.IsAir && it.type == ItemID.Cobweb) n += it.stack;
			}
			return n;
		}

		// 给 Jev 的事实:伤害是算出来的,血量是读出来的。它只判"这个数在这个血量下算什么"
		public static string Json(Player p, int fallCells)
		{
			int dmg = Damage(fallCells);
			return "{\"fall_cells\":" + fallCells
				 + ",\"over_safe_by\":" + System.Math.Max(0, fallCells - SafeCells)
				 + ",\"damage\":" + dmg
				 + ",\"hp\":" + p.statLife + ",\"hp_max\":" + p.statLifeMax
				 + ",\"hp_left_after\":" + (p.statLife - dmg)
				 + ",\"lethal\":" + (dmg >= p.statLife ? "true" : "false")
				 + ",\"cobwebs\":" + WebCount(p)
				 + ",\"rule\":\"damage = 10 * (fall_cells - 25), no damage at or under 25; laying a cobweb underfoot before landing cancels it\"}";
		}
	}
}
