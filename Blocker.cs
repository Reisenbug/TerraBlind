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
