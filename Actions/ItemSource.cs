using System.Collections.Generic;
using Terraria;
using Terraria.ID;

namespace TerraBlind
{
	// 东西从哪来 --- 合不出来的时候查这里。
	//
	// 一个物品可以有【好几个】来源,按表里的顺序试,前一个不行就下一个。
	// 每个来源自己还有前置(买要钱、砍要斧),前置照样递归回 Unstick 的栈。
	//
	// 现在只编必需的两条。将来 LLM 接 wiki 之后往这张表里填即可,执行逻辑不用动。
	public enum SourceKind
	{
		Tile,    // 砍/挖某种方块 (木头<-树)
		Chest,   // 开箱子
		Npc,     // 找 NPC 买
		Enemy    // 打怪掉落。声明了,还没实现
	}

	public struct Source
	{
		public SourceKind Kind;
		public int Id;         // Tile=TileID, Npc=NPCID, Enemy=NPCID, Chest=0 表示任意
		public string Note;

		public Source(SourceKind k, int id, string note = "") { Kind = k; Id = id; Note = note ?? ""; }
		public override string ToString() => $"{Kind}({Id}){(Note.Length > 0 ? " " + Note : "")}";
	}

	public static class ItemSource
	{
		static readonly Dictionary<int, List<Source>> _table = new()
		{
			[ItemID.Wood] = new() { new Source(SourceKind.Tile, TileID.Trees, "砍树") },
		};

		// 钱不是物品,单独走:身边有商人且有值钱的东西就卖,否则开箱子
		public const int MoneyPseudoItem = -1;

		public static List<Source> For(int itemId)
			=> _table.TryGetValue(itemId, out var v) ? v : null;

		// 表随时可以加。LLM 查完 wiki 往这儿塞,执行逻辑不用改
		public static void Add(int itemId, Source s)
		{
			if (!_table.TryGetValue(itemId, out var list)) _table[itemId] = list = new List<Source>();
			list.Add(s);
		}
	}
}
