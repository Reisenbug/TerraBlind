using Terraria;

namespace TerraBlind
{
	public interface ICombatBrain
	{
		CombatCall Decide(Player p, int tcx, int tcy, int dist, bool workBusy);
	}

	// Jev 没接上之前的对照组。保守:够得着才打,手上有活一律不打断,血少了跑
	public class CombatBaseline : ICombatBrain
	{
		public const int EngageCells = 12;
		public const float FleeHpFrac = 0.3f;

		public CombatCall Decide(Player p, int tcx, int tcy, int dist, bool workBusy)
		{
			var c = new CombatCall { Confidence = 1f, LatencyMs = -1, InterruptWork = false };
			if (p.statLife <= p.statLifeMax * FleeHpFrac)
			{ c.Act = CombatAct.Flee; c.Why = $"血{p.statLife}/{p.statLifeMax},撤"; return c; }
			int pcx = (int)(p.Center.X / 16f), pcy = (int)(p.Center.Y / 16f);
			if (ThreatScan.Blocked(pcx, pcy, tcx, tcy))
			{ c.Act = CombatAct.Ignore; c.Why = "隔着方块,够不着也打不着"; return c; }
			if (dist > EngageCells)
			{ c.Act = CombatAct.Ignore; c.Why = $"{dist}格,还远"; return c; }
			c.Act = CombatAct.Fight; c.Why = $"{dist}格,打";
			return c;
		}
	}
}
