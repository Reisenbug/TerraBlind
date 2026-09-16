namespace TerraBlind
{
	// 分诊的唯一入口。现在只观测:把现场和 baseline 的选择打进日志,不改任何行为。
	// 等 Jev 接上,这里再比对两边选得一样不一样
	public static class Triage
	{
		static readonly IStuckTriage _impl = new JevTriage();

		public static void Observe(in StuckScene s)
		{
			var r = _impl.Pick(in s);
			DiagLog.Write($"[triage] ({s.Cx},{s.Cy}) H{s.CurH} → {r.Rung} conf={r.Confidence:0.00} {r.Why}");
		}
	}
}
