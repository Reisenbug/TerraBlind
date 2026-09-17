using System.Collections.Generic;
using Terraria;

namespace TerraBlind
{
	public enum CombatAct { Ignore, Fight, Flee, WallOff, Heal }

	public struct CombatCall
	{
		public CombatAct Act;
		public bool InterruptWork;
		public float Confidence;
		public string Why;
		public string Probs;
		public int LatencyMs;
	}

	// 战斗层。【每帧本地锁敌,按需才问 Jev】:瞄准要跟着怪走,判断不用
	public static class Combat
	{
		public static bool Enabled = false;
		const string Owner = "combat";
		// 名单里任何一只走了这么多格就重新判断。局面没动就沿用上次的结论
		const int MoveRedecide = 3;
		// 血量跨档也要重判:同样的怪,满血该打,残血该跑
		const int HpBuckets = 5;

		static int _target = -1;
		static bool _swinging;
		static string _lastSig = "";
		static int _lastHpBucket = -1;
		static readonly Dictionary<int, (int cx, int cy)> _askedAt = new();
		static CombatCall _call;
		static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
		public static string Last = "idle";

		// 没 key 或者请求还没回来时,JevCombat 自己会退回 baseline
		static ICombatBrain _brain = new JevCombat();

		// 只有放置不能打断。挖掘、寻路、砸网停了都能重来
		static bool WorkBusy => PlaceAction.IsRunning || PlaceAnywhere.IsRunning;

		public static int SlotOf(Player p, int typeId)
		{
			for (int i = 0; i < 58 && i < p.inventory.Length; i++)
			{
				var it = p.inventory[i];
				if (it != null && !it.IsAir && it.type == typeId) return i;
			}
			return -1;
		}

		static bool Hostile(NPC npc)
			=> npc != null && npc.active && !npc.townNPC && !npc.friendly
			   && !(npc.lifeMax <= 5 && npc.damage == 0);

		// 威胁最高的那只,不是最近那只
		static int Worst(Player p, out int cx, out int cy, out int dist)
		{
			cx = cy = 0; dist = 0;
			int best = -1;
			float bestScore = -1f;
			int pcx = (int)(p.Center.X / 16f), pcy = (int)(p.Center.Y / 16f);
			for (int i = 0; i < Main.maxNPCs; i++)
			{
				var npc = Main.npc[i];
				if (!Hostile(npc)) continue;
				int ncx = (int)(npc.Center.X / 16f), ncy = (int)(npc.Center.Y / 16f);
				int d = System.Math.Abs(ncx - pcx) + System.Math.Abs(ncy - pcy);
				if (d > ThreatScan.RangeCells) continue;
				float sc = ThreatScan.Score(p, npc, d);
				if (sc <= bestScore) continue;
				bestScore = sc; best = i; cx = ncx; cy = ncy; dist = d;
			}
			return best;
		}

		// 局面变了没有。【编号 + 移动距离】:同一批怪原地小动不算变,走远了才算
		static bool Changed(Player p, int worst)
		{
			int bucket = p.statLife * HpBuckets / System.Math.Max(1, p.statLifeMax);
			if (bucket != _lastHpBucket) { _lastHpBucket = bucket; return true; }
			if (worst != _target) return true;

			int live = 0;
			for (int i = 0; i < Main.maxNPCs; i++)
			{
				var npc = Main.npc[i];
				if (!Hostile(npc)) continue;
				int ncx = (int)(npc.Center.X / 16f), ncy = (int)(npc.Center.Y / 16f);
				int pcx = (int)(p.Center.X / 16f), pcy = (int)(p.Center.Y / 16f);
				if (System.Math.Abs(ncx - pcx) + System.Math.Abs(ncy - pcy) > ThreatScan.RangeCells) continue;
				live++;
				if (!_askedAt.TryGetValue(npc.whoAmI, out var was)) return true;   // 新来的
				if (System.Math.Abs(ncx - was.cx) + System.Math.Abs(ncy - was.cy) >= MoveRedecide) return true;
			}
			return live != _askedAt.Count;   // 走掉了/死了
		}

		static void Remember(Player p)
		{
			_askedAt.Clear();
			int pcx = (int)(p.Center.X / 16f), pcy = (int)(p.Center.Y / 16f);
			for (int i = 0; i < Main.maxNPCs; i++)
			{
				var npc = Main.npc[i];
				if (!Hostile(npc)) continue;
				int ncx = (int)(npc.Center.X / 16f), ncy = (int)(npc.Center.Y / 16f);
				if (System.Math.Abs(ncx - pcx) + System.Math.Abs(ncy - pcy) > ThreatScan.RangeCells) continue;
				_askedAt[npc.whoAmI] = (ncx, ncy);
			}
		}

		public static void Tick()
		{
			if (!Enabled) return;
			var p = Main.LocalPlayer;
			if (p == null || !p.active || p.dead) { Release(); return; }

			int n = Worst(p, out int tcx, out int tcy, out int dist);
			if (n < 0) { Last = "没敌人"; _askedAt.Clear(); _target = -1; Release(); return; }

			// 判断:局面变了才重新问。没变就沿用上次的结论,一个请求都不发
			if (Changed(p, n))
			{
				_call = _brain.Decide(p, tcx, tcy, dist, WorkBusy);
				_target = n;
				Remember(p);
				string sig = _call.Act + "|" + n + "|" + _call.InterruptWork;
				if (sig != _lastSig)
				{
					_lastSig = sig;
					JevLog.Add(new JevLog.Entry
					{
						Ms = _clock.ElapsedMilliseconds,
						Site = "combat",
						State = Facts(p, tcx, tcy, dist),
						Pick = _call.Act.ToString() + (_call.InterruptWork ? "+打断" : ""),
						Confidence = _call.Confidence,
						Probs = _call.Probs ?? "",
						Why = _call.Why,
						LatencyMs = _call.LatencyMs,
					});
				}
			}

			if (_call.Act != CombatAct.Fight) { Last = _call.Act + ":" + _call.Why; Release(); return; }
			if (WorkBusy && !_call.InterruptWork) { Last = "在放置,先不打"; return; }

			int slot = SlotOf(p, Concessions.StartWeapon);
			if (slot < 0)
			{ Last = "背包里没有武器"; DiagLog.Write($"[combat] 不挥:找不到 id{Concessions.StartWeapon}"); Release(); return; }

			// 放置以外的持有者一律抢:寻路砸网砸罐、挖矿都能重来,挨打不能等
			if (!AxisLock.Take(Owner, Ax.Use, () => Enabled))
			{
				string h = AxisLock.Held(Ax.Use);
				AxisLock.Release(h);
				if (!AxisLock.Take(Owner, Ax.Use, () => Enabled))
				{ Last = "Use 抢不到"; DiagLog.Write($"[combat] 不挥:抢不到 Use {AxisLock.Dump()}"); return; }
				DiagLog.Write($"[combat] 抢过 Use(原持有 {h})");
			}

			// 【自己按键,不走 ItemUseCoordinator】。那套是为挖和放做的:会把光标吸附到附近的 tile、
			// 按挖掘距离判够不着。武器要的只是"对着这个坐标一直挥",怪那格通常是空气
			if (slot >= 10) { Last = "武器不在快捷栏"; return; }
			p.selectedItem = slot;
			Main.SmartCursorWanted_Mouse = false;
			Cursor.AimTile(tcx, tcy);
			if (p.itemTime == 0) p.controlUseItem = true;
			if (_target != n || !_swinging)
			{
				_swinging = true;
				DiagLog.Write($"[combat] 挥 {Main.npc[n].TypeName} ({tcx},{tcy}) {dist}格 血{p.statLife}/{p.statLifeMax}");
			}
			Last = $"打 {Main.npc[n].TypeName} {dist}格";
		}

		static string Facts(Player p, int tcx, int tcy, int dist)
			=> "{\"hp\":" + p.statLife + ",\"hp_max\":" + p.statLifeMax
			 + ",\"player_cell\":[" + (int)(p.Center.X / 16f) + "," + (int)(p.Center.Y / 16f) + "]"
			 + ",\"work_busy\":" + (WorkBusy ? "true" : "false")
			 + ",\"nearest_distance\":" + dist
			 + ",\"enemies\":" + ThreatScan.Json(p, (int)(p.Center.X / 16f), (int)(p.Center.Y / 16f))
			 + "}";

		public static void Release()
		{
			if (_target < 0 && !AxisLock.Has(Owner, Ax.Use)) return;
			_target = -1; _swinging = false;
			AxisLock.Release(Owner);
		}

		public static void Stop() { Release(); _askedAt.Clear(); _lastSig = ""; Last = "stopped"; }
	}
}
