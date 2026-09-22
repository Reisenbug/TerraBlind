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
		// 提前量最多外推这么多帧。再远全是误差,boss 早拐弯了
		const float MaxLeadFrames = 45f;

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

		// 【小怪优先,按威胁分排序;清光了才打 boss】。boss 血几千,ThreatScan.Score 里
		// 那项 life*0.01 让它永远碾压小怪 -- 于是服务者贴着脸咬,人还在对着 boss 挥
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
			float bestScore = -1f;
			int pcx = (int)(p.Center.X / 16f), pcy = (int)(p.Center.Y / 16f);
			// 【双子只打魔焰眼】。分头打等于两个都不死,而它贴脸喷火比激光眼危险
			bool twinLock = bossPass && Alive(Terraria.ID.NPCID.Spazmatism);
			for (int i = 0; i < Main.maxNPCs; i++)
			{
				var npc = Main.npc[i];
				if (!Hostile(npc)) continue;
				if (npc.boss != bossPass) continue;
				if (twinLock && npc.type != Terraria.ID.NPCID.Spazmatism) continue;
				int ncx = (int)(npc.Center.X / 16f), ncy = (int)(npc.Center.Y / 16f);
				int d = System.Math.Abs(ncx - pcx) + System.Math.Abs(ncy - pcy);
				// 【boss 的部件不限射程】。骷髅王的手没有 boss 标志,走的是小怪这一趟 --
				// 手荡到 30 格外就看不见了,于是又去打那个打不动的头
				bool part = BossPart(npc.type);
				if (!bossPass && !part && d > ThreatScan.RangeCells) continue;
				float sc = ThreatScan.Score(p, npc, d);
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
			if (n < 0) { Last = "no enemies"; _askedAt.Clear(); _target = -1; Release(); return; }

			// 【boss 和它的部件都不问打不打】。骷髅王的手没有 boss 标志,走的是小怪那套措辞,
			// 而那套问的是"要不要停下赶路" -- boss 战里根本没有赶路,于是 9 格也答 Ignore
			if (Main.npc[n].boss || BossPart(Main.npc[n].type))
			{
				_call = new CombatCall { Act = CombatAct.Fight, InterruptWork = true, Confidence = 1f, Why = "boss present, just fight" };
				// 【这一支也要记】。日志只写在问 Jev 那一支里,走捷径就整场零条 --
				// 看上去像没在打,其实是没在记
				string bsig = "boss|" + n;
				if (bsig != _lastSig)
				{
					_lastSig = bsig;
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
			}
			// 判断:局面变了才重新问。没变就沿用上次的结论,一个请求都不发
			else if (Changed(p, n))
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
			if (WorkBusy && !_call.InterruptWork) { Last = "placing, hold fire"; return; }

			int slot = WeaponSlot(p, Main.npc[n].boss);
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
			if (slot >= 10) { Last = "weapon not in hotbar"; return; }
			p.selectedItem = slot;
			Main.SmartCursorWanted_Mouse = false;
			var aim = Lead(p, Main.npc[n], p.inventory[slot].shootSpeed);
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
			// 【要用相对速度】。子弹不继承玩家速度(Player.cs:7180 只按 shootSpeed 给),
			// 而起飞时人每帧上升近 10px -- 只算 boss 的速度就会整段打在它下方
			var v = npc.velocity - p.velocity;
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

		public static void Stop() { Release(); _askedAt.Clear(); _lastSig = ""; Last = "stopped"; }
	}
}
