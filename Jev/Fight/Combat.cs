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
		// 提前量最多外推这么多帧。再远全是误差,boss 早拐弯了
		const float MaxLeadFrames = 45f;
		public static bool UseLead = true;
		// 瞄准抖一点,别整批偏同一个方向
		const float JitterPx = 8f;
		// 换 boss 目标要近出这么多格才换
		const int StickCells = 5;

		// 不挥的原因变了才记一行。这两支原来是静默 return,看不出武器为什么停
		static string _noFire = "";
		static void NoFire(string why)
		{
			if (why == _noFire) return;
			_noFire = why;
			DiagLog.Write("[combat] 不挥:" + why);
		}

		public static bool DestroyerSegment(int type)
			=> type == Terraria.ID.NPCID.TheDestroyerBody || type == Terraria.ID.NPCID.TheDestroyerTail;

		static int _target = -1;
		static bool _swinging;
		static string _lastSig = "";
		static CombatCall _call;
		static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
		public static string Last = "idle";

		// 只有放置不能打断。挖掘、寻路、砸网停了都能重来
		static bool WorkBusy => PlaceAction.IsRunning || PlaceAnywhere.IsRunning;

		static int SlotOf(Player p, int typeId)
		{
			for (int i = 0; i < 58 && i < p.inventory.Length; i++)
			{
				var it = p.inventory[i];
				if (it != null && !it.IsAir && it.type == typeId) return i;
			}
			return -1;
		}

		// boss 的部件:自己不带 boss 标志,但打它就是在打 boss
		public static bool BossPart(int type)
			=> type == Terraria.ID.NPCID.SkeletronHand
			|| type == Terraria.ID.NPCID.EaterofWorldsHead
			|| type == Terraria.ID.NPCID.EaterofWorldsBody
			|| type == Terraria.ID.NPCID.EaterofWorldsTail
			|| type == Terraria.ID.NPCID.WallofFleshEye
			|| type == Terraria.ID.NPCID.TheHungry
			|| type == Terraria.ID.NPCID.TheHungryII;

		// 要躲但不该打的部件。【触手伤 206 比本体还疼】,走位层看不见它就永远躲不开;
		// 但它是本体的挂件,打它等于整场不输出
		public static bool DodgeOnlyPart(int type)
			=> type == Terraria.ID.NPCID.PlanterasTentacle
			|| type == Terraria.ID.NPCID.PlanterasHook
			|| type == Terraria.ID.NPCID.PrimeCannon
			|| type == Terraria.ID.NPCID.PrimeSaw
			|| type == Terraria.ID.NPCID.PrimeVice
			|| type == Terraria.ID.NPCID.PrimeLaser;

		// 【肉山在场就只打本体】。眼睛是独立 NPC,血少又离得近,威胁分必然赢过本体 --
		// 而肉山一动起来,瞄眼睛十发九空。嘴(本体)是个大目标,跑着也打得中
		static int WallBody()
		{
			for (int i = 0; i < Main.maxNPCs; i++)
				if (Main.npc[i].active && Main.npc[i].type == Terraria.ID.NPCID.WallofFlesh) return i;
			return -1;
		}

		static bool Hostile(NPC npc)
			=> npc != null && npc.active && !npc.townNPC && !npc.friendly
			   && !(npc.lifeMax <= 5 && npc.damage == 0);

		// 【小怪优先,按威胁分排序;清光了才打 boss】。混在一起比,服务者贴着脸咬,人还在对着 boss 挥
		static int Worst(Player p, out int cx, out int cy, out int dist)
		{
			int wall = WallBody();
			if (wall >= 0)
			{
				var w = Main.npc[wall];
				cx = (int)(w.Center.X / 16f); cy = (int)(w.Center.Y / 16f);
				dist = System.Math.Abs(cx - (int)(p.Center.X / 16f))
					 + System.Math.Abs(cy - (int)(p.Center.Y / 16f));
				return wall;
			}
			int n = Pick(p, false, out cx, out cy, out dist);
			return n >= 0 ? n : Pick(p, true, out cx, out cy, out dist);
		}

		// bossPass=false 只看小怪(有射程限制);true 只看 boss(不限射程,它会飞远再冲回来)
		static int Pick(Player p, bool bossPass, out int cx, out int cy, out int dist)
		{
			cx = cy = 0; dist = 0;
			int best = -1;
			float bestScore = float.MinValue;
			int pcx = (int)(p.Center.X / 16f), pcy = (int)(p.Center.Y / 16f);
			// 【双子只打魔焰眼】。分头打等于两个都不死,而它贴脸喷火比激光眼危险
			// 【只在场上就这一场时锁】。三王同召时恒为真会让另外两个一枪不挨
			bool twinLock = bossPass && Alive(Terraria.ID.NPCID.Spazmatism) && OnlyTwins();
			for (int i = 0; i < Main.maxNPCs; i++)
			{
				var npc = Main.npc[i];
				if (!Hostile(npc)) continue;
				// 毁灭者只有头带 boss 标志,身体算小怪就被 30 格射程滤掉
				if ((npc.boss || DestroyerSegment(npc.type)) != bossPass) continue;
				// 【毁灭者打最近的体节,不打头】。头只有一个,常在 200 格外
				if (npc.type == Terraria.ID.NPCID.TheDestroyer) continue;
				if (twinLock && npc.type != Terraria.ID.NPCID.Spazmatism) continue;
				// 只躲不打的部件。打它等于整场不输出
				if (DodgeOnlyPart(npc.type)) continue;
				int ncx = (int)(npc.Center.X / 16f), ncy = (int)(npc.Center.Y / 16f);
				int d = System.Math.Abs(ncx - pcx) + System.Math.Abs(ncy - pcy);
				// 【boss 的部件不限射程】。骷髅王的手没有 boss 标志,走的是小怪这一趟 --
				// 手荡到 30 格外就看不见了,于是又去打那个打不动的头
				bool part = BossPart(npc.type);
				if (!bossPass && !part && d > ThreatScan.RangeCells) continue;
				// 【boss 之间只比远近】,当前目标让几格,别在两个差不多近的之间每帧换
				float sc = bossPass ? -d + (i == _target ? StickCells : 0) : ThreatScan.Score(p, npc, d);
				if (sc <= bestScore) continue;
				bestScore = sc; best = i; cx = ncx; cy = ncy; dist = d;
			}
			return best;
		}

		public static bool Alive(int type)
		{
			for (int i = 0; i < Main.maxNPCs; i++)
				if (Main.npc[i] != null && Main.npc[i].active && Main.npc[i].type == type) return true;
			return false;
		}

		// 场上的 boss 是不是只有双子这一场。别的机械王在场时不能再锁魔焰眼
		// 【毁灭者的身体和尾巴没有 boss 标志】,漏掉就等于没看见它在场
		static bool OnlyTwins()
		{
			for (int i = 0; i < Main.maxNPCs; i++)
			{
				var n = Main.npc[i];
				if (n == null || !n.active || n.friendly) continue;
				if (n.type == Terraria.ID.NPCID.TheDestroyer
				 || n.type == Terraria.ID.NPCID.TheDestroyerBody
				 || n.type == Terraria.ID.NPCID.TheDestroyerTail
				 || n.type == Terraria.ID.NPCID.SkeletronPrime) return false;
				if (n.boss && n.type != Terraria.ID.NPCID.Spazmatism
				 && n.type != Terraria.ID.NPCID.Retinazer) return false;
			}
			return true;
		}

		// 打小怪用 0 号位,打 boss 本体用 1 号位。【目标类型天然就是阶段】--
		// 克脑一阶段本体打不动,Worst() 那时只会返回爬行者,不用存阶段变量
		static int WeaponSlot(Player p, bool atBoss)
		{
			int want = atBoss ? 1 : 0;
			if (Usable(p, want)) return want;
			for (int i = 0; i < 10; i++)
				if (Usable(p, i)) return i;
			return -1;
		}

		static bool Usable(Player p, int i)
		{
			var it = p.inventory[i];
			if (it == null || it.IsAir || it.damage <= 0 || it.useStyle == 0) return false;
			return it.pick == 0 && it.axe == 0 && it.hammer == 0;
		}

		static bool BossOnField()
		{
			for (int i = 0; i < Main.maxNPCs; i++)
				if (Main.npc[i] != null && Main.npc[i].active && Main.npc[i].boss) return true;
			return false;
		}

		public static void Tick()
		{
			if (!Enabled) return;
			var p = Main.LocalPlayer;
			if (p == null || !p.active || p.dead) { Release(); return; }

			int n = Worst(p, out int tcx, out int tcy, out int dist);
			if (n < 0) { Last = "no enemies"; _target = -1; Release(); return; }

			// 【有目标就打,不问 Jev】。问的是"要不要停下赶路",boss 战里它对探测器和手答了 36% 的 Ignore,
			// 武器就那么松着。boss 在场时连放置也打断:全程开火
			bool bossFight = BossOnField();
			_call = new CombatCall { Act = CombatAct.Fight, InterruptWork = bossFight, Confidence = 1f, Why = bossFight ? "boss present, just fight" : "enemy in range" };
			string sig = "fight|" + n;
			if (sig != _lastSig)
			{
				_lastSig = sig;
				JevLog.Add(new JevLog.Entry
				{
					Ms = _clock.ElapsedMilliseconds,
					Site = "combat",
					State = Facts(p, tcx, tcy, dist),
					Pick = "Fight(" + Main.npc[n].TypeName + ")",
					Confidence = 1f,
					Why = _call.Why,
				});
			}
			_target = n;

			if (WorkBusy && !_call.InterruptWork) { Last = "placing, hold fire"; NoFire("在放置"); return; }
			_noFire = "";

			int slot = WeaponSlot(p, Main.npc[n].boss || DestroyerSegment(Main.npc[n].type));
			if (slot < 0)
			{ Last = "no weapon"; DiagLog.Write("[combat] 不挥:热键栏里没有纯武器"); Release(); return; }

			// 放置以外的持有者一律抢:寻路砸网砸罐、挖矿都能重来,挨打不能等
			if (!AxisLock.Take(Owner, Ax.Use, () => Enabled))
			{
				string h = AxisLock.Held(Ax.Use);
				AxisLock.Release(h);
				if (!AxisLock.Take(Owner, Ax.Use, () => Enabled))
				{ Last = "Use axis taken"; DiagLog.Write($"[combat] 不挥:抢不到 Use {AxisLock.Dump()}"); return; }
				DiagLog.Write($"[combat] 抢过 Use(原持有 {h})");
			}

			// 【自己按键,不走 ItemUseCoordinator】。那套是为挖和放做的:会把光标吸附到附近的 tile、
			// 按挖掘距离判够不着。武器要的只是"对着这个坐标一直挥",怪那格通常是空气
			if (slot >= 10) { Last = "weapon not in hotbar"; NoFire("武器不在热键栏"); return; }
			p.selectedItem = slot;
			Main.SmartCursorWanted_Mouse = false;
			var aim = UseLead ? Lead(p, Main.npc[n], p.inventory[slot].shootSpeed) : Main.npc[n].Center;
			aim.X += Main.rand.NextFloat(-JitterPx, JitterPx);
			aim.Y += Main.rand.NextFloat(-JitterPx, JitterPx);
			Cursor.AimPx(aim.X, aim.Y);
			// 【看 itemAnimation 不看 itemTime】。itemTime 是整个使用周期(星怒要等星星落完),
			// 拿它当条件就是挥一下等一轮。动画结束就能再挥,这才是连挥
			if (p.itemAnimation == 0) p.controlUseItem = true;
			if (_target != n || !_swinging)
			{
				_swinging = true;
				DiagLog.Write($"[combat] 挥 {Main.npc[n].TypeName} ({tcx},{tcy}) {dist}格 血{p.statLife}/{p.statLifeMax}");
			}
			Last = $"hitting {Main.npc[n].TypeName} at {dist}";
		}

		// 提前量。每帧瞄 boss 当前位置的话,子弹飞过去时它早走了 -- 快 boss 全空。
		// 解 |d + v*t| = s*t 取最小正根,追不上就退回当前位置
		static Microsoft.Xna.Framework.Vector2 Lead(Player p, NPC npc, float shootSpeed)
		{
			var d = npc.Center - p.Center;
			if (shootSpeed <= 0.01f) return npc.Center;
			// 【别减玩家速度】。出膛点在发射那一帧就定死了(Player.cs:7161 取当时的中心),
			// 之后子弹独立飞,射手动不动都一样 -- 减过一次,起飞时反而全空
			var v = npc.velocity;
			float a = Microsoft.Xna.Framework.Vector2.Dot(v, v) - shootSpeed * shootSpeed;
			float b = 2f * Microsoft.Xna.Framework.Vector2.Dot(d, v);
			float c = Microsoft.Xna.Framework.Vector2.Dot(d, d);
			float t;
			// a==0 是 boss 速度正好等于子弹速度,二次项没了,退化成一次方程
			if (System.MathF.Abs(a) < 0.0001f)
			{
				if (System.MathF.Abs(b) < 0.0001f) return npc.Center;
				t = -c / b;
			}
			else
			{
				float disc = b * b - 4f * a * c;
				// 判别式为负 = 追不上。boss 比子弹快而且在跑
				if (disc < 0f) return npc.Center;
				float sq = System.MathF.Sqrt(disc);
				float t1 = (-b - sq) / (2f * a), t2 = (-b + sq) / (2f * a);
				t = System.MathF.Min(t1 > 0f ? t1 : float.MaxValue, t2 > 0f ? t2 : float.MaxValue);
				if (t == float.MaxValue) return npc.Center;
			}
			// 【封顶】。t 很大时那点全是外推误差,boss 早拐弯了
			if (t > MaxLeadFrames) t = MaxLeadFrames;
			return npc.Center + v * t;
		}

		static string Facts(Player p, int tcx, int tcy, int dist)
			=> "{\"hp\":" + p.statLife + ",\"hp_max\":" + p.statLifeMax
			 + ",\"player_cell\":[" + (int)(p.Center.X / 16f) + "," + (int)(p.Center.Y / 16f) + "]"
			 + ",\"work_busy\":" + (WorkBusy ? "true" : "false")
			 + ",\"distance_to_the_one_i_would_attack\":" + dist
			 + ",\"enemies\":" + ThreatScan.Json(p, (int)(p.Center.X / 16f), (int)(p.Center.Y / 16f))
			 + "}";

		public static void Release()
		{
			if (_target < 0 && !AxisLock.Has(Owner, Ax.Use)) return;
			_target = -1; _swinging = false;
			AxisLock.Release(Owner);
		}

		public static void Stop() { Release(); _lastSig = ""; Last = "stopped"; }
	}
}
