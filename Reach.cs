using Terraria;

namespace TerraBlind
{
	// 【够不够得着,只有这一份】。原来全用 IsInTileInteractionRange(Simple),而 vanilla 的挖和放
	// 各有各的公式,三把尺子互不相同 ——
	//   Simple  : tileRangeX                       (还封顶 20)
	//   挖(46562): tileRangeX + tileBoost
	//   放(38990): tileRangeX + tileBoost + blockRange
	// 后果是两边都错、方向还相反:
	//   挖 —— 镐子 tileBoost=-1 时 Simple 更松,放行了 vanilla 不许的事,ClearWay 每 45 帧
	//         重挥一次挖不动的格子,人杵在 (1098,1050) 对着 (1103,1052) 挥了 2400 帧。
	//   放 —— Simple 少算 blockRange(Concessions 的 8 格全在这儿),比 vanilla 更严,
	//         明明伸手就能放却先走过去。
	// 公式照抄 Player.IsTargetTileInItemRange / PlaceThing,一个字都不改。
	public static class Reach
	{
		// 挖:vanilla Player.cs:46562 IsTargetTileInItemRange
		public static bool CanMine(Player p, int tx, int ty) => Box(p, tx, ty, HeldBoost(p), 0);

		// 放:vanilla Player.cs:38990,比挖多一项 blockRange
		public static bool CanPlace(Player p, int tx, int ty) => Box(p, tx, ty, HeldBoost(p), p.blockRange);

		// 手上那件的 tileBoost。手是空的就按 0 —— 没东西可用,范围谈不上加成
		static int HeldBoost(Player p)
		{
			var it = p.HeldItem;
			return it == null || it.IsAir ? 0 : it.tileBoost;
		}

		static bool Box(Player p, int tx, int ty, int boost, int extra)
		{
			float rx = Player.tileRangeX + boost + extra;
			float ry = Player.tileRangeY + boost + extra;
			return p.position.X / 16f - rx <= tx
				&& (p.position.X + p.width) / 16f + rx - 1f >= tx
				&& p.position.Y / 16f - ry <= ty
				&& (p.position.Y + p.height) / 16f + ry - 2f >= ty;
		}
	}
}
