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
		const int ThrowGap = 20;         // 两根之间至少隔这么多帧,不然一帧连按会连扔好几根
		const int Tol = 1;               // 距离容差,到位就扔

		static int _thrown, _cool;

		public static void Toggle()
		{
			On = !On;
			if (On) { _thrown = 0; _cool = 0; DiagLog.Write("[wof-fight] ON"); Main.NewText("[打肉山] 开", 120, 255, 120); }
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

			if (_cool > 0) _cool--;

			// 太近就退。肉山会加速,所以退这件事优先于扔 —— 被贴上就没得打了
			if (dist < want - Tol)
			{
				if (side > 0) p.controlLeft = true; else p.controlRight = true;
				return;
			}
			// 退过头了就靠回去:表里的距离是命中率最高的位置,离太远白扔
			if (dist > want + Tol)
			{
				if (side > 0) p.controlRight = true; else p.controlLeft = true;
				return;
			}

			if (_cool > 0) return;
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
			if (p.itemTime != 0 || p.itemAnimation != 0) return;

			p.controlUseItem = true;
			_thrown++;
			_cool = ThrowGap;
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
