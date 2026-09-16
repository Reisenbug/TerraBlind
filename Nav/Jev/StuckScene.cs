using System.Text;
using Terraria;

namespace TerraBlind
{
	// 卡住那一刻的现场,序列化成 Jev 的 state。
	// Jev 只吃【一个文本字段】,所以所有上下文都得编进这一份 JSON(文档建议 named JSON fields)
	public struct StuckScene
	{
		public int Cx, Cy;             // 人脚下那一格
		public int GoalWx, GoalWy;     // 这一段导航的终点
		public int CurH;               // 当前格势能。终点格恒为 0,不用带
		public int FootBlockCol, FootBlockRow;   // 脚下挖不动的那一列,-1 = 没有
		public bool TrapEscapeBusy;    // A* 后台还在搜
		public bool CommitmentActive;  // 已经在承诺里了
		public bool AnyPhysicsEdge;    // 物理候选一条都发不出来 = 真封死

		// 9x9 地形。中心是人脚下那格。'#'实心 '='平台 '~'岩浆 '.'空
		public static string Terrain(int cx, int cy, int r = 4)
		{
			var sb = new StringBuilder();
			for (int dy = -r; dy <= r; dy++)
			{
				for (int dx = -r; dx <= r; dx++)
				{
					int x = cx + dx, y = cy + dy;
					if (!Predicates.InBounds(x, y)) { sb.Append('?'); continue; }
					if (Predicates.IsLava(x, y)) sb.Append('~');
					else if (Predicates.IsPlatform(x, y)) sb.Append('=');
					else if (Predicates.IsSolid(x, y)) sb.Append('#');
					else sb.Append('.');
				}
				if (dy < r) sb.Append('/');
			}
			return sb.ToString();
		}

		// 挖不动的那一格是什么。措辞要让模型看得懂,光给 tile id 它读不出"这是黑曜石"
		string FootBlockDesc()
		{
			if (FootBlockCol < 0) return "null";
			if (!Predicates.InBounds(FootBlockCol, FootBlockRow)) return "null";
			int t = Main.tile[FootBlockCol, FootBlockRow].TileType;
			return $"{{\"tile_id\":{t},\"at\":[{FootBlockCol},{FootBlockRow}],\"note\":\"当前镐挖不动\"}}";
		}

		public string ToJson()
		{
			var sb = new StringBuilder();
			sb.Append("{\"player_cell\":[").Append(Cx).Append(',').Append(Cy).Append(']')
			  .Append(",\"goal_cell\":[").Append(GoalWx).Append(',').Append(GoalWy).Append(']')
			  .Append(",\"potential_here\":").Append(CurH)
			  .Append(",\"unmineable_under_foot\":").Append(FootBlockDesc())
			  .Append(",\"astar_already_searching\":").Append(TrapEscapeBusy ? "true" : "false")
			  .Append(",\"commitment_already_active\":").Append(CommitmentActive ? "true" : "false")
			  .Append(",\"any_physics_edge_available\":").Append(AnyPhysicsEdge ? "true" : "false")
			  .Append(",\"terrain_9x9\":\"").Append(Terrain(Cx, Cy)).Append('"')
			  .Append(",\"terrain_legend\":\"# solid, = platform (pass upward, land on top), ~ lava, . air, / row break\"")
			  .Append('}');
			return sb.ToString();
		}
	}
}
