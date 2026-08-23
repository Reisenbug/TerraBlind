using Terraria;

namespace TerraBlind
{
	// 卡住的现场。原语报 stuck 必须交出这个 —— reason 字符串只能给人看,
	// 不能给代码判断,于是每处失败都各写一套补救。类型只有四种,解法也只有四套。
	public enum BlockKind
	{
		Terrain,      // 地形挡着 → 挖掉
		SelfInWay,    // 人自己站在要动的格子上 → 让开
		OutOfReach,   // 够不着 → 造个落脚点再够
		Hopeless      // 真无解(没料/没镐/岩浆) → 这才允许失败
	}

	public struct Blocker
	{
		public BlockKind Kind;
		public int Wx, Wy;      // 哪一格。挡路=挡着的那格,够不着=想够的那格
		public string Detail;   // 只进日志,不参与决策

		public Blocker(BlockKind k, int wx, int wy, string detail = "")
		{ Kind = k; Wx = wx; Wy = wy; Detail = detail ?? ""; }

		public override string ToString() => $"{Kind}({Wx},{Wy}){(Detail.Length > 0 ? " " + Detail : "")}";
	}
}
