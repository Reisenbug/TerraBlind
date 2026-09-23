using Terraria;

namespace TerraBlind
{
	public interface ICombatBrain
	{
		CombatCall Decide(Player p, int tcx, int tcy, int dist, bool workBusy);
	}

	// Jev 没接上之前的对照。
	public class CombatBaseline : ICombatBrain
	{
		public const int EngageCells = 12;
		public const float FleeHpFrac = 0.3f;

		public CombatCall Decide(Player p, int tcx, int tcy, int dist, bool workBusy)
		{
			var c = new CombatCall { Confidence = 1f, LatencyMs = -1, InterruptWork = false };
			if (p.statLife <= p.statLifeMax * FleeHpFrac)
			{ c.Act = CombatAct.Flee; c.Why = $"hp {p.statLife}/{p.statLifeMax}, retreat"; return c; }
			if (dist > EngageCells)
			{ c.Act = CombatAct.Ignore; c.Why = $"{dist} away, too far"; return c; }
			c.Act = CombatAct.Fight; c.Why = $"{dist} away, engage";
			return c;
		}
	}
}
