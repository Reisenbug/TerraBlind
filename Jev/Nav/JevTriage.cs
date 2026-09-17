namespace TerraBlind
{
	// Jev 版分诊。还没拿到 key,Pick 现在一律落到 baseline --
	// 问题措辞和阈值先钉在这里,等 key 到了只要填 Ask()
	public class JevTriage : IStuckTriage
	{
		// 低于这个就不信它,走 baseline。Choice 的 confidence 是分布集中度,不是"对不对"
		public const float MinConfidence = 0.6f;
		// 问题 id 不发给模型,措辞要自带完整含义
		public const string QuestionId = "stuck_rung";

		readonly IStuckTriage _fallback = new BaselineTriage();

		// 问题定义。criteria 的 key 和 Rung 一一对应,Unknown 是文档要求的 no-match 出口。
		// 【这段措辞要人过一遍】:文档自己说 agents aren't great at writing questions
		public static string QuestionJson()
			=> "{\"type\":\"choice\""
			 + ",\"instructions\":\"泰拉瑞亚里一个自动玩家卡住了:贪心寻路找不到任何能降低势能的动作。"
			 + "势能是到终点的估计代价,越低越近。根据现场,选下一步最该做什么。\""
			 + ",\"criteria\":{"
			 + "\"AstarEscape\":\"让 A* 搜一小段出坑的路。适合看着能出去、只是每条路都要先绕远或先变差的坑\","
			 + "\"DigFootBlock\":\"脚下那一列有当前镐挖不动的方块,挡住了往下的路。横着挪一格避开它\","
			 + "\"Commit\":\"认准附近一个势能明显更低的格,一路走过去,中途变差也不回头。适合来回弹的盆地\","
			 + "\"WalledIn\":\"真的被封死了,人也过不去。只在四周全是挖不动的东西时选\","
			 + "\"Unknown\":\"以上都不像\"}}";

		public TriageResult Pick(in StuckScene s)
		{
			// 没接上之前不假装有判断。返回 baseline 的结果,行为和现在完全一致
			return _fallback.Pick(in s);
		}
	}
}
