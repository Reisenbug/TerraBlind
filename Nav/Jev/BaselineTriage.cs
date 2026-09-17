namespace TerraBlind
{
	// 现在的固定瀑布顺序,照抄 RecedingNav 里那一段。它是 baseline:
	// Jev 接上之后拿它当对照组,而没拿到 key 之前整条链的行为和现在一字不差
	public class BaselineTriage : IStuckTriage
	{
		public TriageResult Pick(in StuckScene s)
		{
			// 一条物理边都发不出来 = 人也过不去。这个判据在最前面,它不是"选择"是事实
			if (!s.AnyPhysicsEdge)
				return new TriageResult { Rung = Rung.WalledIn, Confidence = 1f, LatencyMs = -1, Why = "物理候选一条都没有" };
			// A* 在搜的时候什么都别做。它刚开搜也返回 false,和"搜失败"长得一样
			if (s.TrapEscapeBusy)
				return new TriageResult { Rung = Rung.AstarEscape, Confidence = 1f, LatencyMs = -1, Why = "A* 还在后台搜" };
			if (!s.CommitmentActive)
				return new TriageResult { Rung = Rung.AstarEscape, Confidence = 1f, LatencyMs = -1, Why = "先让 A* 试出坑" };
			// 脚下有挖不动的列排在承诺前面:有明确解法的具体障碍,而承诺是没辙了才用的瞎猜
			if (s.FootBlockCol >= 0)
				return new TriageResult { Rung = Rung.DigFootBlock, Confidence = 1f, LatencyMs = -1, Why = "脚下那列挖不动,挪一格" };
			return new TriageResult { Rung = Rung.Commit, Confidence = 1f, LatencyMs = -1, Why = "A* 也出不去,退回承诺" };
		}
	}
}
