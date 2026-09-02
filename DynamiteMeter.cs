using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraBlind
{
	// 把"凭感觉扔雷管"变成数字。
	//
	// 【盯血量,不盯雷管数】。雷管有引信,扔出去到炸开隔着好几秒 ——
	// 按数量减少去读血,读到的永远是上一根还没炸时的血,差值恒等于 0(第一版就是这么废的)。
	// 血一掉就记一行,同时记下那一刻人离肉山多远。扔和炸是两条时间线,只认后一条。
	//
	// 只记【观测到的】,不猜伤害公式。
	public static class DynamiteMeter
	{
		public static bool On;

		static int _lastLife = -1, _lastMax;
		static int _hits;
		static long _dmgSum;
		static int _bestDmg, _worstDmg;
		static int _thrown, _lastCount = -1;

		static bool ReadWof(out int life, out int max, out float cx)
		{
			life = 0; max = 0; cx = 0f;
			for (int i = 0; i < Main.maxNPCs; i++)
			{
				var n = Main.npc[i];
				if (!n.active || n.type != NPCID.WallofFlesh) continue;
				life = n.life; max = n.lifeMax; cx = n.Center.X;
				return true;
			}
			return false;
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
			_lastLife = -1; _lastCount = -1;
			if (On)
			{
				_hits = 0; _dmgSum = 0; _thrown = 0;
				_bestDmg = 0; _worstDmg = int.MaxValue;
				Chatter.Say("[雷管表] 开。盯血量,炸一次记一行", 120, 255, 120);
				DiagLog.Write("[dyn] ON");
			}
			else { Report(); Chatter.Say("[雷管表] 关", 255, 200, 120); }
		}

		public static void Tick()
		{
			if (!On) return;
			var p = Main.LocalPlayer;
			if (p == null || !p.active) return;

			bool live = ReadWof(out int life, out int max, out float wcx);

			// 【扔】这条线:数量一少就记,带距离。和爆炸各记各的 ——
			// 引信隔着好几秒,拿一条去触发另一条就是上一版掉血恒等于0的原因
			int cnt = CountDynamite(p);
			if (_lastCount >= 0 && cnt < _lastCount)
			{
				_thrown += _lastCount - cnt;
				string tl = live
					? $"第{_thrown}根雷管 x距离={(int)System.Math.Abs(p.Center.X - wcx) / 16}格 肉山血量:{life}/{max}"
					: $"第{_thrown}根雷管 肉山不在场";
				Chatter.Say("[扔] " + tl, 180, 220, 255);
				DiagLog.Write("[dyn-throw] " + tl);
			}
			_lastCount = cnt;

			if (!live)
			{
				// 肉山没了。血条上一帧还剩多少,如实记下来 —— 死亡那一瞬 active 就没了,
				// 不补这一行的话日志永远停在"还剩一千多血"
				if (_lastLife > 0)
				{
					DiagLog.Write($"[dyn] 肉山消失,上一帧还剩{_lastLife}/{_lastMax} 共扔{_thrown}根");
					Chatter.Say($"[雷管] 结束 剩{_lastLife}", 255, 220, 120);
				}
				_lastLife = -1;
				return;
			}

			if (_lastLife < 0) { _lastLife = life; _lastMax = max; return; }

			if (life < _lastLife)
			{
				int dmg = _lastLife - life;
				_hits++; _dmgSum += dmg;
				if (dmg > _bestDmg) _bestDmg = dmg;
				if (dmg < _worstDmg) _worstDmg = dmg;
				string line = $"第{_hits}次爆炸 肉山血量:{life}/{max} 掉血={dmg}";
				Chatter.Say("[炸] " + line, 255, 220, 120);
				DiagLog.Write("[dyn-hit] " + line);
			}
			_lastLife = life; _lastMax = max;
		}

		static void Report()
		{
			if (_hits <= 0) { DiagLog.Write("[dyn] OFF 没记到掉血"); return; }
			string s = $"扔了{_thrown}根,{_hits}次爆炸 共掉血{_dmgSum} " +
				$"平均{_dmgSum / _hits}/次 最狠{_bestDmg} 最弱{_worstDmg}";
			Chatter.Say("[雷管表] " + s, 120, 255, 120);
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
