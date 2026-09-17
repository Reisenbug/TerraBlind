using Terraria;

namespace TerraBlind
{
	public enum CombatAct { Ignore, Fight, Flee, WallOff, Heal }

	public struct CombatCall
	{
		public CombatAct Act;
		public bool InterruptWork;   // 值不值得打断手上的放置/挖掘
		public float Confidence;
		public string Why;
		public string Probs;
		public int LatencyMs;
	}

	// 战斗层。只抢 Ax.Use,绝不碰 Move -- 边走边挥是合法的,而放置/挖掘同样要 Use,天然互斥
	public static class Combat
	{
		public static bool Enabled = false;
		const string Owner = "combat";
		const int ScanEvery = 20;      // 三分之一秒扫一次,不用每帧
		const int SwingTicks = 30;

		static int _tick;
		static int _target = -1;
		public static string Last = "idle";

		static ICombatBrain _brain = new CombatBaseline();

		// 手上有活在干:这几样都占 Use,打断了要付代价
		static bool WorkBusy =>
			PlaceAction.IsRunning || MineCoordinator.IsActive || PlatformDown.IsRunning
			|| PillarUp.IsRunning || BridgeBuilder.IsRunning || PlaceAnywhere.IsRunning;

		public static int SlotOf(Player p, int typeId)
		{
			for (int i = 0; i < 58 && i < p.inventory.Length; i++)
			{
				var it = p.inventory[i];
				if (it != null && !it.IsAir && it.type == typeId) return i;
			}
			return -1;
		}

		// 最近的活敌人。星怒直接对着它的格子挥
		static int Nearest(Player p, out int cx, out int cy, out int dist)
		{
			cx = cy = 0; dist = int.MaxValue;
			int best = -1;
			int pcx = (int)(p.Center.X / 16f), pcy = (int)(p.Center.Y / 16f);
			for (int i = 0; i < Main.maxNPCs; i++)
			{
				var npc = Main.npc[i];
				if (npc == null || !npc.active || npc.townNPC || npc.friendly) continue;
				if (npc.lifeMax <= 5 && npc.damage == 0) continue;
				int ncx = (int)(npc.Center.X / 16f), ncy = (int)(npc.Center.Y / 16f);
				int d = System.Math.Abs(ncx - pcx) + System.Math.Abs(ncy - pcy);
				if (d >= dist || d > ThreatScan.RangeCells) continue;
				dist = d; best = i; cx = ncx; cy = ncy;
			}
			return best;
		}

		public static void Tick()
		{
			if (!Enabled) return;
			var p = Main.LocalPlayer;
			if (p == null || !p.active || p.dead) { Release(); return; }
			if (++_tick % ScanEvery != 0) return;

			int n = Nearest(p, out int tcx, out int tcy, out int dist);
			if (n < 0) { Last = "没敌人"; Release(); return; }

			var call = _brain.Decide(p, tcx, tcy, dist, WorkBusy);
			JevLog.Add(new JevLog.Entry
			{
				Ms = _tick,
				Site = "combat",
				State = Facts(p, tcx, tcy, dist),
				Pick = call.Act.ToString() + (call.InterruptWork ? "+打断" : ""),
				Confidence = call.Confidence,
				Probs = call.Probs ?? "",
				Why = call.Why,
				LatencyMs = call.LatencyMs,
			});

			if (call.Act != CombatAct.Fight) { Last = call.Act + ":" + call.Why; Release(); return; }
			if (WorkBusy && !call.InterruptWork) { Last = "手上有活,先不打"; return; }

			int slot = SlotOf(p, Concessions.StartWeapon);
			if (slot < 0)
			{ Last = "背包里没有武器"; DiagLog.Write($"[combat] 不挥:背包里找不到 id{Concessions.StartWeapon}"); Release(); return; }
			if (!AxisLock.Take(Owner, Ax.Use, () => Enabled))
			{ Last = "Use 被占着"; DiagLog.Write($"[combat] 不挥:Use 被 {AxisLock.Held(Ax.Use)} 占着 {AxisLock.Dump()}"); return; }
			if (ItemUseCoordinator.IsActive)
			{ Last = "上一挥还没完"; DiagLog.Write($"[combat] 不挥:ItemUse 还在跑 outcome={ItemUseCoordinator.Outcome}"); return; }

			_target = n;
			if (!ItemUseCoordinator.IsActive)
				ItemUseCoordinator.Start(new ItemUseRequest
				{ TargetWx = tcx, TargetWy = tcy, Slot = slot, DurationTicks = SwingTicks, Strict = false });
			Last = $"打 {Main.npc[n].TypeName} {dist}格";
			DiagLog.Write($"[combat] 挥 {Main.npc[n].TypeName} ({tcx},{tcy}) {dist}格 血{p.statLife}/{p.statLifeMax}");
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
			if (_target < 0) return;
			_target = -1;
			ItemUseCoordinator.Stop();
			AxisLock.Release(Owner);
		}

		public static void Stop() { Release(); Last = "stopped"; }
	}
}
