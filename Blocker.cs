using Terraria;

namespace TerraBlind
{
	// 做不成一件事的原因。原语报失败必须交出这个 --- reason 字符串只能给人看,
	// 不能给代码判断,于是每处失败都各写一套补救。
	//
	// 把 17 个动作类的失败原因归并完,只剩这 6 种,而且它们互相递归:
	// 缺料 -> 合成 -> 缺材料(还是缺料) + 缺工作台(还是够不着)。链条不是几百条,是 6 条。
	public enum BlockKind
	{
		Terrain,      // 目标格被占着 -> 挖掉
		SelfInWay,    // 人自己压着要动的格子 -> 让开
		OutOfReach,   // 够不着 -> 走过去;走不过去就先造落脚点
		// 【够得着但没站上去】。和 OutOfReach 分开是因为救法不同:那个用 Mode.Reach(够得着就算到),
		// 这个必须 Mode.Stand(脚真踩在那一格)。ReachBoost=8 让手隔 3 行就够得着,
		// 拿 Reach 去救"回桥面"会每帧报"到了"却一步没动 —— deck 就是这么死循环三轮的
		NotStanding,
		// 【脚下有一列挖不动,得换个站位】。往下挖要求身体压的 2~3 列【全部】挖空才掉得下去,
		// 一列是黑曜石(铜镐 pick35 < 55)整条边就不成立 —— 而往旁边挪一格,两列可能都是普通石头。
		// 现场:(1102,742) 左脚下 (1101,743) 是黑曜石,西边一整片都是,东边 1103+ 全能挖;
		// 它没往东挪,反而往西跑了 8 格再往上砌柱子。
		FootColUnmineable,
		NoFooting,    // 没地方站 -> 造:平台/pillar/桥
		NoItem,       // 背包没料 -> 合成(递归:材料+工作台)
		NoTool,       // 没镐/斧 -> 合成工具(递归)
		Hopeless      // 真无解(岩浆/材料的材料也没有) -> 这才允许失败
	}

	public struct Blocker
	{
		public BlockKind Kind;
		public int Wx, Wy;      // 哪一格。挡路=挡着的那格,够不着=想够的那格
		public int ItemId;      // NoItem/NoTool 时:缺哪样东西
		public int Count;       // NoItem 时:缺几个
		public string Detail;   // 只进日志,不参与决策

		public Blocker(BlockKind k, int wx, int wy, string detail = "")
		{ Kind = k; Wx = wx; Wy = wy; ItemId = 0; Count = 0; Detail = detail ?? ""; }

		public static Blocker Item(int itemId, int count, string detail = "")
			=> new Blocker(BlockKind.NoItem, 0, 0, detail) { ItemId = itemId, Count = count };

		public static Blocker Tool(int itemId, string detail = "")
			=> new Blocker(BlockKind.NoTool, 0, 0, detail) { ItemId = itemId, Count = 1 };

		public override string ToString()
		{
			string what = ItemId != 0 ? $"物品{ItemId}x{Count}" : $"({Wx},{Wy})";
			return $"{Kind}{what}{(Detail.Length > 0 ? " " + Detail : "")}";
		}
	}
}
