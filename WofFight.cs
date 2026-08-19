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

		// 该和肉山保持多远(格),【按血量查】不按第几根。
		// 肉山血越少跑得越快(8→9→11→14→17 mph),所以必须拉得越开 ——
		// 实测的距离曲线和这张速度表严丝合缝:
		//   ≥75%血 扔在30~32格 | <75% 31~39 | <50% 40~46 | <25% 48~49 | <10% 49
		static int WantDist(int life, int max)
		{
			float f = max > 0 ? (float)life / max : 1f;
			if (f >= 0.75f) return 31;
			if (f >= 0.50f) return 36;
			if (f >= 0.25f) return 44;
			if (f >= 0.10f) return 48;
			return 50;
		}

		const int AimX = 5, AimY = 21;   // 瞄准偏移(格):朝肉山 5,向下 21
		// 容差从实测量出来:前12根扔在 27~32 格(去掉起手那根是 29~32),抖动就是 ±2。
		// 再大就超过单次后退的跨度(后段每次退 2~3 格),会一步跨过整个窗口扔不出去
		const int Tol = 2;

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
			int want = WantDist(wof.life, wof.lifeMax);

			// 规则只有一条:【雷管冷却好了就扔】。距离不是扔的条件 ——
			// 上一版把距离当成必须命中的靶心,于是"不到位就不扔、不扔 want 就不涨、
			// 涨不动就永远不扔",第2根之后卡死一直走(日志 5201 之后再无投掷)。
			// 距离只用来决定【要不要退】。
			bool tooClose = dist < want - Tol;
			if (tooClose)
			{
				int away = -side;
				if (away > 0) p.controlRight = true; else p.controlLeft = true;
				// 桥面有起伏就跳过去。光按方向键的话撞上一格台阶就停在那儿,
				// dist 再也拉不开,肉山直接贴脸
				var (bl, br) = Predicates.BodyCols(p);
				int fcol = away > 0 ? br + 1 : bl - 1;
				int fy = ActExecutor.OriginCy(p);
				if (p.velocity.Y == 0f && Predicates.IsWall(fcol, fy)) p.controlJump = true;
			}
			// 太远就站着等它靠近。【绝不往回走】—— 肉山只会推进,迎上去纯属危险
			if (dist > want + Tol && Main.GameUpdateCount % 120 == 0)
				DiagLog.Write($"[wof-fight] 等肉山靠近 距离={dist} 想要{want}");

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
			DiagLog.Write($"[wof-fight] 扔第{_thrown}根 距离={dist}(想要{want}) 肉山{wof.life}/{wof.lifeMax}");
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
