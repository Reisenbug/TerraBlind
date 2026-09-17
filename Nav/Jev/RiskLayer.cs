using Terraria;

namespace TerraBlind
{
	// 选边时的风险加价。默认关:它改的是调了几个月的热路径,要开就明着开
	public static class RiskLayer
	{
		public static bool Enabled = false;

		// 权重是策略,归代码管。Jev 只给判断,不给策略
		public const float FallPerHp = 3f;
		public const float MobNear = 40f;
		public const float MobFlying = 25f;

		public static float Penalty(Player p, int fromCx, int fromCy, int toCx, int toCy)
		{
			float pen = 0f;
			int drop = toCy - fromCy;
			if (drop > FallCost.SafeCells)
			{
				int dmg = FallCost.Damage(drop);
				if (dmg >= p.statLife) return 100000f;
				pen += dmg * FallPerHp;
			}
			for (int i = 0; i < Main.maxNPCs; i++)
			{
				var npc = Main.npc[i];
				if (npc == null || !npc.active || npc.townNPC || npc.friendly) continue;
				if (npc.lifeMax <= 5 && npc.damage == 0) continue;
				int ncx = (int)(npc.Center.X / 16f), ncy = (int)(npc.Center.Y / 16f);
				int d = System.Math.Abs(ncx - toCx) + System.Math.Abs(ncy - toCy);
				if (d > 8) continue;
				pen += (npc.noGravity ? MobFlying : MobNear) / (d + 1);
			}
			return pen;
		}
	}
}
