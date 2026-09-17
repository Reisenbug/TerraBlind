using System.Collections.Generic;

namespace TerraBlind
{
	// 每个 boss 的打法知识,【只是一段话,不是规则】。人打 boss 靠背板,
	// 而板子写成 if 就得为每个 boss 编阈值 -- 那些数我一个都不知道。
	// 直接把话给 Jev,它自己判断现在处在哪个阶段。加新 boss = 加一行字
	public static class BossBook
	{
		static readonly Dictionary<int, string> Book = new()
		{
			[Terraria.ID.NPCID.EyeofCthulhu] =
				"克苏鲁之眼通常先悬停在玩家头顶上方,蓄一会儿,然后朝玩家所在的位置直线冲刺。"
				+ "所以它悬停不动的时候正是最危险的时候,要提前把横向速度拉起来,"
				+ "站着不动等它冲下来必然被撞。它冲过去之后会有一段收招,那时可以贴近输出。"
				+ "它还会召唤服务者小怪,小怪贴身也掉血。",
		};

		public static string For(int npcType)
			=> Book.TryGetValue(npcType, out string s) ? s : "";
	}
}
