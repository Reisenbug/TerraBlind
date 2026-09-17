namespace TerraBlind
{
	// 贪心走不动时,下一步交给谁。顺序现在钉死在 RecedingNav 里,这个接口把"选哪个"抽出来
	public enum Rung
	{
		AstarEscape,    // TrapEscape.TryEscape:后台 A* 搜一小段出坑路
		DigFootBlock,   // Unstick(FootColUnmineable):脚下那列挖不动,挪一格
		Commit,         // Commitment.Begin:认准一个远处更低点,一路走过去不回头
		WalledIn,       // 物理候选一条都没有,真封死了
		Unknown         // 没有一个像。文档要求给 no-match 出口,低置信度时也走这儿
	}

	public struct TriageResult
	{
		public Rung Rung;
		public float Confidence;   // Choice 的 confidence,0..1。Baseline 恒为 1
		public string Why;
		public string Probs;       // "AstarEscape:0.72,Commit:0.21" 给观测页看。baseline 为空
		public int LatencyMs;      // -1 = 没真发请求
	}

	public interface IStuckTriage
	{
		TriageResult Pick(in StuckScene s);
	}
}
