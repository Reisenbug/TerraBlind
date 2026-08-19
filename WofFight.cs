using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraBlind
{
	// 打肉山:边退边扔雷管。
	//
	// 距离表是用户亲手打出来的(2026-08-19 实测,再整体上移2格),不是算出来的 ——
	// 前 12 根守在 32 格左右保证命中,第 13 根起持续拉开,因为肉山会加速追人。
	//
	// 扔的角度也是量出来的:朝肉山方向 5 格、向下 21 格。雷管是抛物线,这个角度
	// 决定了落点;别自己推物理,照抄手感。
	public static class WofFight
	{
		public static bool On;

		// 第 n 根雷管该在离肉山多远时扔出(x 轴,格)。索引 0 = 第1根
		static readonly int[] Dist =
		{
			29, 31, 33, 32, 33, 32, 32, 32, 33, 34, 32, 33,
			36, 39, 41, 42, 43, 46, 48, 50, 50, 51, 51
		};
		// 表用完之后一直按最后一格退 —— 肉山只会越来越快
		static int WantDist(int n) => n < Dist.Length ? Dist[n] : Dist[Dist.Length - 1];

		const int AimX = 5, AimY = 21;   // 瞄准偏移(格):朝肉山 5,向下 21

		static int _thrown;

		public static void Toggle()
		{
			On = !On;
			if (On) { _thrown = 0; DiagLog.Write("[wof-fight] ON"); Main.NewText("[打肉山] 开", 120, 255, 120); }
			else { DiagLog.Write($"[wof-fight] OFF 扔了{_thrown}根"); Main.NewText("[打肉山] 关", 255, 200, 120); }
		}

		static int FindWof()
		{
			for (int i = 0; i < Main.maxNPCs; i++)
				if (Main.npc[i].active && Main.npc[i].type == NPCID.WallofFlesh) return i;
			return -1;
		}

		static int DynamiteSlot(Player p)
		{
			for (int i = 0; i < 10; i++)
			{
				var it = p.inventory[i];
				if (it != null && !it.IsAir && it.type == ItemID.Dynamite) return i;
			}
			return -1;
		}

		public static void Tick()
		{
			if (!On) return;
			var p = Main.LocalPlayer;
			if (p == null || !p.active) return;

			int w = FindWof();
			if (w < 0)
			{
				if (_thrown > 0) { DiagLog.Write($"[wof-fight] 肉山没了,共扔{_thrown}根"); On = false; }
				return;
			}
			var wof = Main.npc[w];

			// 肉山在人的哪一侧:退是往【背对它】的方向,瞄是往【朝着它】的方向
			int side = wof.Center.X > p.Center.X ? 1 : -1;
			int dist = (int)System.Math.Abs(p.Center.X - wof.Center.X) / 16;
			int want = WantDist(_thrown);

			// 【一直退,永不回头】。肉山只会推进,dist 自己就会缩小;往回走是主动迎上去,
			// 白费时间还危险。表里的距离是【下限】不是靶心 —— 退过头了照扔不误
			int away = -side;
			if (away > 0) p.controlRight = true; else p.controlLeft = true;
			// 桥面有起伏就跳过去。光按方向键的话撞上一格台阶就停在那儿,
			// dist 再也拉不开,肉山直接贴脸
			var (bl, br) = Predicates.BodyCols(p);
			int fcol = away > 0 ? br + 1 : bl - 1;
			int fy = ActExecutor.OriginCy(p);
			if (p.velocity.Y == 0f && Predicates.IsWall(fcol, fy)) p.controlJump = true;

			// 【边退边扔,不停下等距离】。实测前12根距离几乎不变(30格上下)却一直在扔,
			// 说明手感是匀速后退、扔满为止,不是每根都精确停在某个位置
			if (dist < want) return;      // 还没退够就先只退不扔

			int slot = DynamiteSlot(p);
			if (slot < 0)
			{
				if (Main.GameUpdateCount % 120 == 0) DiagLog.Write("[wof-fight] 没雷管了");
				return;
			}

			// 瞄准:朝肉山 AimX 格、向下 AimY 格。这个角度是量出来的
			float ax = p.Center.X + side * AimX * 16f;
			float ay = p.Center.Y + AimY * 16f;
			Cursor.AimPx(ax, ay);
			p.selectedItem = slot;
			// 雷管 useTime=useAnimation=40(Item.cs:3386),原版自己把节奏卡死在40帧,
			// 再加一层冷却纯属多余 —— 实测相邻两根间隔40~56帧,正是这个下限
			if (p.itemTime != 0 || p.itemAnimation != 0) return;

			p.controlUseItem = true;
			_thrown++;
			DiagLog.Write($"[wof-fight] 扔第{_thrown}根 距离={dist}(要{want}) 肉山{wof.life}/{wof.lifeMax}");
		}
	}

	public class WofFightCommand : ModCommand
	{
		public override CommandType Type => CommandType.Chat;
		public override string Command => "woffight";
		public override string Description => "边退边扔雷管打肉山";
		public override string Usage => "/woffight";

		public override void Action(CommandCaller caller, string input, string[] args)
			=> WofFight.Toggle();
	}
}
