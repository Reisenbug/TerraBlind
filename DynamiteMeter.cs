using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraBlind
{
	// 把"凭感觉扔雷管"变成数字。
	//
	// 开着的时候盯两样东西:背包里的雷管数,和肉山。雷管少一根就记一行 ——
	// 那一刻人离肉山多远(x)、肉山还剩多少血。扔完看汇总:每根打掉多少血、
	// 在什么距离扔的。感觉就此具象化。
	//
	// 只记【观测到的】,不猜伤害公式:一根雷管扣多少血受护甲/难度/多人等一堆东西影响,
	// 算出来的数还不如量出来的准。
	public static class DynamiteMeter
	{
		public static bool On;

		static int _lastCount = -1;
		static int _lastLife = -1;
		static int _thrown;
		static long _dmgSum;
		static int _bestDist, _worstDist, _bestDmg, _worstDmg = int.MaxValue;

		// 肉山本体(113)。眼睛(114)是同一个血条的另一半,不单独算
		static int FindWof()
		{
			for (int i = 0; i < Main.maxNPCs; i++)
			{
				var n = Main.npc[i];
				if (n.active && n.type == NPCID.WallofFlesh) return i;
			}
			return -1;
		}

		static int CountDynamite(Player p)
		{
			int n = 0;
			foreach (var it in p.inventory)
				if (it != null && !it.IsAir && it.type == ItemID.Dynamite) n += it.stack;
			return n;
		}

		public static void Toggle()
		{
			On = !On;
			_lastCount = -1; _lastLife = -1;
			if (On)
			{
				_thrown = 0; _dmgSum = 0;
				_bestDmg = 0; _worstDmg = int.MaxValue; _bestDist = 0; _worstDist = 0;
				Main.NewText("[雷管表] 开。扔一根记一行", 120, 255, 120);
				DiagLog.Write("[dyn] ON");
			}
			else { Report(); Main.NewText("[雷管表] 关", 255, 200, 120); }
		}

		public static void Tick()
		{
			if (!On) return;
			var p = Main.LocalPlayer;
			if (p == null || !p.active) return;

			int w = FindWof();
			int cnt = CountDynamite(p);
			if (w < 0)
			{
				// 肉山不在:只跟着数量走,不记录 —— 没有血量可对照,记了也没意义
				if (_lastCount >= 0 && cnt < _lastCount)
					DiagLog.Write($"[dyn] 少了{_lastCount - cnt}根,但肉山不在场");
				_lastCount = cnt; _lastLife = -1;
				return;
			}

			var wof = Main.npc[w];
			int life = wof.life;
			// 头一帧只对齐基准,不当成"扔了一根"
			if (_lastCount < 0) { _lastCount = cnt; _lastLife = life; return; }

			if (cnt < _lastCount)
			{
				int used = _lastCount - cnt;
				// x 距离才是手感所在:肉山横着推过来,纵向差多少无所谓
				int dx = (int)System.Math.Abs(p.Center.X - wof.Center.X) / 16;
				int dmg = _lastLife - life;
				_thrown += used;
				_dmgSum += dmg > 0 ? dmg : 0;
				if (dmg > _bestDmg) { _bestDmg = dmg; _bestDist = dx; }
				if (dmg < _worstDmg) { _worstDmg = dmg; _worstDist = dx; }
				string line = $"第{_thrown}根 x距离={dx}格 掉血={dmg} 剩{life}/{wof.lifeMax}";
				Main.NewText("[雷管] " + line, 255, 220, 120);
				DiagLog.Write("[dyn] " + line);
			}
			_lastCount = cnt;
			_lastLife = life;
		}

		static void Report()
		{
			if (_thrown <= 0) { DiagLog.Write("[dyn] OFF 没扔过"); return; }
			string s = $"共{_thrown}根 掉血{_dmgSum} 平均{_dmgSum / _thrown}/根 " +
				$"最狠{_bestDmg}@{_bestDist}格 最弱{_worstDmg}@{_worstDist}格";
			Main.NewText("[雷管表] " + s, 120, 255, 120);
			DiagLog.Write("[dyn] OFF " + s);
		}
	}

	public class DynamiteMeterCommand : ModCommand
	{
		public override CommandType Type => CommandType.Chat;
		public override string Command => "dyn";
		public override string Description => "雷管炸肉山的距离/伤害记录";
		public override string Usage => "/dyn";

		public override void Action(CommandCaller caller, string input, string[] args)
			=> DynamiteMeter.Toggle();
	}
}
