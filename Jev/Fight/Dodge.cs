using System.Net.Http;
using System.Text;
using Terraria;

namespace TerraBlind
{
	// Jev 说往哪躲
	public enum Dir { Up, UpRight, Right, DownRight, Down, DownLeft, Left, UpLeft, Stay }
	// Jev 说躲多大:Small 跑跳落,Medium 加冲刺或空中跳,Large 甩钩爪
	public enum Size { Small, Medium, Large }

	// boss 战的走位。Jev 每次反应给方向和幅度,反射层每帧把它翻成按键
	public static class Dodge
	{
		public static bool Enabled = false;
		// 对照组:把 Jev 换成随机选择器
		public static bool RandomBrain = false;
		const string Owner = "dodge";
		// 别把远处的 boss 当不存在
		// 走位层每隔几帧就 Release 一次,没有任何东西再把人拉回来
		const int CareCells = 200;
		// 量墙用另一把尺子。
		const int RoomScanCells = 60;
		// 离地图边界这么近就当撞墙。
		const int EdgeCells = 40;
		// 意图过期就退回保守行为。
		const long IntentTtlMs = 1500;

		static bool _jumpHeld;
		static int _holdFrames;
		// 按住的兜底上限。防"某种状态下一直升不完"
		const int MaxHoldFrames = 30;
		// 钩爪甩出去这么多帧还没勾上就放弃,别一直按着钩子不打人
		const int HookGiveUpFrames = 45;
		static int _hookFrames;
		static bool _hookHeld;
		// 荡完歇一会儿
		// 勾上就跳、跳完就勾，等于玩家原地不动
		const int HookCooldownFrames = 30;
		static int _hookCooldown;
		// 冲刺要"按→松→按"三帧。因为这不是1.4.5。TMod更新1.4.5后得改。现在的模组覆盖掉了的话也得改
		static int _dashDir;
		// 这次回答已经记过"想冲但没就绪"
		static int _dashWaitSaid = -1;
		static bool _dashGap;

		public static string Last = "idle";
		public static Dir Way = Dir.Stay;
		public static Size Amount = Size.Small;
		public static float Confidence;
		public static int LatencyMs;
		// jev判的概率分布。
		public static string Probs = "";
		public static string TopTwo = "";
		// 上一条播报过的意图。
		static string _saidAct = "";
		static long _saidAt;

		public const string Url = "https://api.typesafe.ai/v1/systemone";
		public const string Model = "jev-latest";
		static readonly HttpClient _http = new() { Timeout = System.TimeSpan.FromSeconds(5) };
		static volatile bool _busy;
		static volatile string _pending;
		// 发出去的那份现场,答案回来时一起记进日志。
		static string _lastFacts = "";
		// 发出去时被挡住的方向,答案回来时一起记进日志
		static string _lastBlocked = "";
		static long _actAt = -100000;
		// 第几次回答,一次回答只触发一次的动作用它去重
		static int _answer;
		static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

		// 部件大多没有 boss 标志。见Conbat.cs
		static bool IsBossLike(NPC npc)
			=> npc.boss || Combat.BossPart(npc.type) || Combat.DodgeOnlyPart(npc.type)
			|| Combat.DestroyerSegment(npc.type) || npc.type == Terraria.ID.NPCID.Probe;

		// 返回最近的那一段
		static NPC Boss(Player p, out int dist)
		{
			dist = -1;
			NPC best = null;
			int pcx = (int)(p.Center.X / 16f), pcy = (int)(p.Center.Y / 16f);
			for (int i = 0; i < Main.maxNPCs; i++)
			{
				var npc = Main.npc[i];
				if (npc == null || !npc.active || npc.friendly || !IsBossLike(npc)) continue;
				int d = System.Math.Abs((int)(npc.Center.X / 16f) - pcx)
					  + System.Math.Abs((int)(npc.Center.Y / 16f) - pcy);
				if (d > CareCells) continue;
				if (best != null && d >= dist) continue;
				dist = d;
				best = npc;
			}
			return best;
		}

		static string ThreatId(int type)
			=> type < Terraria.ID.NPCID.Count ? Terraria.ID.NPCID.Search.GetName(type)
			 : Terraria.ModLoader.NPCLoader.GetNPC(type).Name;

		// 这种部件里离我最近的一个
		static NPC Nearest(Player p, int type)
		{
			NPC best = null;
			float bd = float.MaxValue;
			for (int i = 0; i < Main.maxNPCs; i++)
			{
				var n = Main.npc[i];
				if (n == null || !n.active || n.type != type) continue;
				float d = Microsoft.Xna.Framework.Vector2.DistanceSquared(n.Center, p.Center);
				if (d < bd) { bd = d; best = n; }
			}
			return best;
		}

		// 每种部件最近 60 帧的速度(格/秒),报最近一秒的峰值
		static readonly System.Collections.Generic.Dictionary<int, float[]> _speedRings = new();
		static int _speedAt;
		static float PartSpeed(NPC n) => (System.Math.Abs(n.velocity.X) + System.Math.Abs(n.velocity.Y)) * 60f / 16f;
		static void TrackPartSpeeds(Player p)
		{
			_speedAt = (_speedAt + 1) % 60;
			var seen = new System.Collections.Generic.HashSet<int>();
			for (int i = 0; i < Main.maxNPCs; i++)
			{
				var m = Main.npc[i];
				if (m == null || !m.active || m.friendly || !IsBossLike(m) || !seen.Add(m.type)) continue;
				if (!_speedRings.TryGetValue(m.type, out var ring)) _speedRings[m.type] = ring = new float[60];
				ring[_speedAt] = PartSpeed(Nearest(p, m.type));
			}
		}
		static int TopSpeed(int type)
		{
			if (!_speedRings.TryGetValue(type, out var ring)) return 0;
			float top = 0f;
			foreach (float s in ring) if (s > top) top = s;
			return (int)top;
		}

		// 每种部件取最近的一个,报位置、速度、伤害、几帧撞到我
		static string ThreatsJson(Player p)
		{
			var seen = new System.Collections.Generic.HashSet<int>();
			var sb = new StringBuilder("[");
			int pcx = (int)(p.Center.X / 16f), pcy = (int)(p.Center.Y / 16f);
			for (int i = 0; i < Main.maxNPCs; i++)
			{
				var m = Main.npc[i];
				if (m == null || !m.active || m.friendly || !IsBossLike(m) || !seen.Add(m.type)) continue;
				var n = Nearest(p, m.type);
				int cx = (int)(n.Center.X / 16f) - pcx, cy = pcy - (int)(n.Center.Y / 16f);
				if (System.Math.Abs(cx) + System.Math.Abs(cy) > CareCells) continue;
				int f = FramesToHit(p, n);
				if (sb.Length > 1) sb.Append(',');
				sb.Append("{\"id\":\"").Append(JsonStr(ThreatId(n.type))).Append('"')
				  .Append(",\"name\":\"").Append(JsonStr(n.TypeName)).Append('"')
				  .Append(",\"contact_damage_percent_of_my_hp\":").Append(n.damage * 100 / System.Math.Max(1, p.statLife))
				  .Append(",\"cells_to_my_right\":").Append(cx)
				  .Append(",\"cells_above_me\":").Append(cy)
				  .Append(",\"speed_to_the_right\":").Append((int)(n.velocity.X * 60f / 16f))
				  .Append(",\"speed_upward\":").Append((int)(-n.velocity.Y * 60f / 16f))
				  .Append(",\"speed_cells_per_second\":").Append((int)PartSpeed(n))
				  .Append(",\"top_speed_in_the_last_second\":").Append(TopSpeed(n.type))
				  .Append(",\"frames_until_it_hits_me\":").Append(f < 0 ? "\"not heading at me\"" : f.ToString())
				  .Append('}');
			}
			return sb.Append(']').ToString();
		}

		public static void Tick()
		{
			if (!Enabled) return;
			var p = Main.LocalPlayer;
			if (p != null && p.active && p.dead) FightEnd("死亡");
			if (p == null || !p.active || p.dead) { Release(); return; }
			if (Manual.On) { Gate("人工"); Release(); return; }

			var boss = Boss(p, out int dist);
			if (boss == null) { FightEnd("boss 没了(击败或离场)"); Gate("no boss"); Release(); return; }
			if (!_inFight) FightStart();
			TrackPartSpeeds(p);

			var done = _pending;
			if (done != null) { _pending = null; Parse(done, boss.type); }
			string key = JevCombat.Key();
			if (RandomBrain) RandomPick(boss.type);
			else if (key != null && !_busy)
			{
				_lastFacts = Facts(p, boss);
				Fire(key, Body(_lastFacts, DirCriteria(p)));
			}

			// 意图过期时原地、小幅
			bool stale = _clock.ElapsedMilliseconds - _actAt > IntentTtlMs;
			var dir = stale ? Dir.Stay : Way;
			var size = stale ? Size.Small : Amount;

			// 羽落要按住 up,所以也拿 Vertical
			if (!AxisLock.Take(Owner, Ax.Move | Ax.Jump | Ax.Vertical, () => Enabled))
			{ Gate("Move axis taken by " + AxisLock.Held(Ax.Move)); return; }

			Gate($"driving {boss.TypeName} {dist}格 act={dir}/{size}");
			Drive(p, boss, dist, dir, size);
		}

		// 写 HUD 状态,变了才进日志
		static string _gate = "";
		static void Gate(string why)
		{
			Last = why;
			int cut = why.IndexOf(" act=", System.StringComparison.Ordinal);
			string key = cut > 0 ? why.Substring(0, cut) : why;
			if (key == _gate) return;
			_gate = key;
			DiagLog.Write("[dodge] " + why);
		}

		// 方向拆成左右和上下,上是 -1
		static (int X, int Y) Vec(Dir d) => d switch
		{
			Dir.Up => (0, -1),
			Dir.UpRight => (1, -1),
			Dir.Right => (1, 0),
			Dir.DownRight => (1, 1),
			Dir.Down => (0, 1),
			Dir.DownLeft => (-1, 1),
			Dir.Left => (-1, 0),
			Dir.UpLeft => (-1, -1),
			_ => (0, 0),
		};

		// 反射层,每帧把方向和幅度翻成按键
		static void Drive(Player p, NPC boss, int dist, Dir dir, Size size)
		{
			bool onGround = p.velocity.Y == 0f;
			var (hx, vy) = Vec(dir);
			bool moving = dir != Dir.Stay;

			bool hooking = Hook(p, dir, moving && size == Size.Large, out bool hookJump, out bool hookMiss);
			// 勾不到就退一档
			bool medium = moving && (size == Size.Medium || (size == Size.Large && hookMiss));
			bool hop = medium && dir == Dir.Up && _airJumpUsed != _answer;
			bool jump = Jump(p, onGround, vy < 0, hop, hookJump);

			int go = Dash(p, hx, medium && hx != 0);
			bool dashing = go != hx || _dashGap;

			if (go < 0) p.controlLeft = true;
			else if (go > 0) p.controlRight = true;
			if (jump) p.controlJump = true;

			// 往下按住下一路穿平台;不上不下时空中按住上,靠羽落保持高度;挂着钩子时都不按
			if (p.grapCount == 0 && vy > 0) p.controlDown = true;
			else if (p.grapCount == 0 && vy == 0 && !onGround) p.controlUp = true;

			Fly(p, dir);
			TrackDps();
			Hurt(p, boss, dist, dir, size);
			Last = $"{dir}/{size} boss {dist}格 go {(go == 0 ? "-" : go < 0 ? "L" : "R")}"
				 + (jump ? (onGround ? " +jump" : " +airjump") : "") + (hooking ? " +hook" : "")
				 + (dashing ? " +dash" : "") + (p.dashDelay < 0 ? " [dashing]" : "");
		}

		// 最近 60 帧 boss 掉的血
		static int _bossPrevHp = -1;
		static readonly int[] _dpsRing = new int[60];
		static int _dpsAt;
		// 最近 30 秒每秒的 dps,取中位当常驻
		static readonly int[] _dpsHist = new int[30];
		static int _histAt, _histCount, _secFrames;
		public static int BossDps, DpsTypical, DpsPct = 100;
		// 场上所有 boss 的血量和
		static int TotalBossHp()
		{
			int sum = 0;
			for (int i = 0; i < Main.maxNPCs; i++)
			{
				var n = Main.npc[i];
				if (n == null || !n.active || n.friendly || !IsBossLike(n)) continue;
				// 蠕虫各节共用 realLife,只算一次
				if (n.realLife >= 0 && n.realLife != i) continue;
				sum += n.life;
			}
			return sum;
		}

		// 每秒记一行输出和高度,只进日志
		static void TrackDps()
		{
			int hp = TotalBossHp();
			if (_bossPrevHp < 0) { _bossPrevHp = hp; return; }
			int d = _bossPrevHp - hp;
			_bossPrevHp = hp;
			// 回血或 boss 离场算出负数,当 0
			_dpsRing[_dpsAt] = d > 0 ? d : 0;
			if (d > 0) _fightBossDmg += d;
			_dpsAt = (_dpsAt + 1) % _dpsRing.Length;
			int sum = 0;
			foreach (int v in _dpsRing) sum += v;
			BossDps = sum;

			if (++_secFrames < 60) return;
			_secFrames = 0;
			_dpsHist[_histAt] = BossDps;
			_histAt = (_histAt + 1) % _dpsHist.Length;
			if (_histCount < _dpsHist.Length) _histCount++;
			var s = new int[_histCount];
			System.Array.Copy(_dpsHist, s, _histCount);
			System.Array.Sort(s);
			DpsTypical = s[_histCount / 2];
			DpsPct = DpsTypical > 0 ? BossDps * 100 / DpsTypical : 100;
			var me = Main.LocalPlayer;
			DiagLog.Write($"[dodge] dps {BossDps} 常驻{DpsTypical} {DpsPct}%"
				+ $" | 脚在第{(int)((me.position.Y + me.height) / 16f)}行 离地{CellsAboveGround(me)} 头顶{CeilingDistance(me)}");
		}

		// 一场 boss 战的累计,结束时记一行小结
		static bool _inFight;
		static ulong _fightStart;
		static int _fightHits, _fightHitHp, _fightDotHp, _fightBossDmg;
		static void FightStart()
		{
			_inFight = true;
			_fightStart = Main.GameUpdateCount;
			_fightHits = _fightHitHp = _fightDotHp = _fightBossDmg = 0;
			_prevHp = -1;
			DiagLog.Write("[dodge] 战斗开始");
		}
		static void FightEnd(string result)
		{
			if (!_inFight) return;
			_inFight = false;
			float secs = (Main.GameUpdateCount - _fightStart) / 60f;
			string line = $"战斗小结 {result} 用时{secs:0.0}秒 被打中{_fightHits}次{_fightHitHp}血 持续掉血{_fightDotHp}血"
				+ $" 打出{_fightBossDmg} 平均秒伤{(secs > 0 ? _fightBossDmg / secs : 0):0}";
			DiagLog.Write("[dodge] " + line);
			Main.NewText("[TerraBlind] " + line, 255, 220, 120);
		}

		// 每次掉血记一行,带最近的 NPC 和弹幕
		static int _prevHp = -1;
		static int _prevImmune;
		static void Hurt(Player p, NPC boss, int dist, Dir dir, Size size)
		{
			// 被打中会重新给无敌帧,持续掉血不会
			bool hit = p.immuneTime > _prevImmune;
			_prevImmune = p.immuneTime;
			if (_prevHp < 0) { _prevHp = p.statLife; return; }
			int lost = _prevHp - p.statLife;
			_prevHp = p.statLife;
			if (lost <= 0) return;
			if (hit) { _fightHits++; _fightHitHp += lost; }
			else _fightDotHp += lost;
			int nd = 999, pd = 999;
			string nn = "无", pn = "无";
			foreach (var n in Main.npc)
			{
				if (n == null || !n.active || n.friendly || n.damage <= 0) continue;
				int d = (int)(Microsoft.Xna.Framework.Vector2.Distance(n.Center, p.Center) / 16f);
				if (d < nd) { nd = d; nn = $"{n.TypeName}(伤{n.damage})"; }
			}
			foreach (var pr in Main.projectile)
			{
				if (pr == null || !pr.active || !pr.hostile || pr.damage <= 0) continue;
				int d = (int)(Microsoft.Xna.Framework.Vector2.Distance(pr.Center, p.Center) / 16f);
				if (d < pd) { pd = d; pn = $"{pr.Name}(伤{pr.damage})"; }
			}
			DiagLog.Write($"[dodge] 掉血 {lost} {(hit ? "被打中" : "持续")} 无敌帧{p.immuneTime} 回血{p.lifeRegen} 剩{p.statLife}/{p.statLifeMax}"
				+ $" 离{boss.TypeName} {dist}格 意图{dir}/{size}"
				+ $" | 最近NPC {nn} {nd}格 | 最近弹幕 {pn} {pd}格 | debuff {Debuffs(p)}");
		}

		// 身上的 debuff 名字,没有就是"无"
		static string Debuffs(Player p)
		{
			var sb = new StringBuilder();
			for (int i = 0; i < Player.MaxBuffs; i++)
			{
				int t = p.buffType[i];
				if (t <= 0 || p.buffTime[i] <= 0 || !Main.debuff[t]) continue;
				if (sb.Length > 0) sb.Append(',');
				sb.Append(Terraria.Lang.GetBuffName(t));
			}
			return sb.Length > 0 ? sb.ToString() : "无";
		}

		// 上一次用过空中跳的回答
		static int _airJumpUsed = -1;
		static int _jumpBlockedSaid = -1;
		static bool _flying;
		static float _prevWing;
		// 飞行开始和结束各记一行。wingTime 在掉而且在上升才算飞,缓降也烧 wingTime
		static void Fly(Player p, Dir dir)
		{
			bool now = p.wingTime < _prevWing - 0.01f && p.velocity.Y < -0.5f;
			_prevWing = p.wingTime;
			if (now == _flying) return;
			_flying = now;
			DiagLog.Write($"[dodge] 飞行{(now ? "开始" : "结束")} 方向={dir}"
				+ $" wingMax={p.wingTimeMax} wingTime={p.wingTime:0.0} vy={p.velocity.Y:0.0}");
		}

		// 钩爪。只在发射那一帧占用光标。miss 表示这个方向射程内勾不到东西
		static bool Hook(Player p, Dir dir, bool want, out bool hookJump, out bool miss)
		{
			hookJump = false;
			miss = false;
			if (p.grapCount > 0)
			{
				// 拉到不再靠近落点才跳
				float d = Microsoft.Xna.Framework.Vector2.Distance(p.Center, _anchorPx);
				bool pulling = d < _anchorPrev;
				_anchorPrev = d;
				if (pulling) return true;
				if (!_hookSaid) { _hookSaid = true; DiagLog.Write($"[dodge] 钩子拉到位 离落点{d / 16f:0.0}格 跳开"); }
				hookJump = true;
				_hookFrames = 0;
				_hookCooldown = HookCooldownFrames;
				return true;
			}
			_anchorPrev = float.MaxValue;
			_hookSaid = false;
			if (!want) { _hookFrames = 0; return false; }
			if (_hookCooldown > 0)
			{
				_hookCooldown--;
				if (_hookWaitSaid != _answer) { _hookWaitSaid = _answer; DiagLog.Write($"[dodge] 想往{dir}甩钩爪 但还在冷却 剩{_hookCooldown}帧"); }
				return false;
			}
			// 按一帧松一帧,vanilla 要 releaseHook 才认新按压
			if (_hookFrames++ > HookGiveUpFrames) return false;
			if (_hookHeld) { _hookHeld = false; return true; }
			if (!HookTarget(p, dir, out int ax, out int ay))
			{
				if (_hookMissSaid != _answer) { _hookMissSaid = _answer; DiagLog.Write($"[dodge] 想往{dir}甩钩爪 但射程内勾不到东西 退到Medium"); }
				_hookFrames = 0;
				miss = true;
				return false;
			}
			if (_hookFrames == 1) DiagLog.Write($"[dodge] 甩钩爪 往{dir} 瞄第({ax},{ay})格");
			Cursor.AimTile(ax, ay);
			_anchorPx = new Microsoft.Xna.Framework.Vector2(ax * 16 + 8, ay * 16 + 8);
			p.controlHook = true;
			_hookHeld = true;
			return true;
		}
		static Microsoft.Xna.Framework.Vector2 _anchorPx;
		static float _anchorPrev = float.MaxValue;
		// 这次挂钩已经记过"拉到位"
		static bool _hookSaid;
		static int _hookMissSaid = -1;
		static int _hookWaitSaid = -1;

		// 装备的钩爪能飞多远(像素),照 Player.QuickGrapple 找钩爪、AI_007_GrapplingHooks 的射程表
		static float HookRangePx(Player p)
		{
			int t = p.miscEquips[4].shoot;
			if (t <= 0 || !Main.projHook[t])
			{
				t = 0;
				for (int i = 0; i < 58 && t == 0; i++)
					if (p.inventory[i].shoot > 0 && Main.projHook[p.inventory[i].shoot]) t = p.inventory[i].shoot;
				if (t == 0) return 0f;
			}
			if (t >= Terraria.ID.ProjectileID.Count) return Terraria.ModLoader.ProjectileLoader.GetProjectile(t)?.GrappleRange() ?? 0f;
			return t switch
			{
				13 or 396 or 865 => 300f,
				32 or 331 or 372 => 400f,
				73 or 74 => 440f,
				165 => 375f,
				256 => 350f,
				315 or 446 or 935 => 500f,
				322 or 332 => 550f,
				>= 646 and <= 649 => 550f,
				652 => 600f,
				>= 486 and <= 489 => 480f,
				>= 230 and <= 235 => 300f + (t - 230) * 30f,
				753 => 420f,
				_ => 2500f,
			};
		}

		// 从身体中心往这个方向飞,射程内第一个能勾的格子
		static bool HookTarget(Player p, Dir dir, out int tx, out int ty)
		{
			tx = ty = 0;
			var (x0, y0) = Vec(dir);
			if (x0 == 0 && y0 == 0) return false;
			var step = Microsoft.Xna.Framework.Vector2.Normalize(new Microsoft.Xna.Framework.Vector2(x0, y0)) * 4f;
			float range = HookRangePx(p);
			var pt = p.Center;
			for (float d = 0f; d <= range; d += 4f, pt += step)
			{
				int x = (int)(pt.X / 16f), y = (int)(pt.Y / 16f);
				if (Hookable(x, y)) { tx = x; ty = y; return true; }
			}
			return false;
		}

		// 跳键。rise 一直按住往上飞,hop 这次回答用一段空中跳。空中新按一下,有空中跳 vanilla 先用空中跳,没有才是翅膀(Player.cs:25618)
		static bool Jump(Player p, bool onGround, bool rise, bool hop, bool hookJump)
		{
			if (_jumpHeld)
			{
				// 要解钩或要空中跳就先松一帧,下一帧的新按压才算数
				if (hookJump || hop) { _jumpHeld = false; return false; }
				_holdFrames++;
				bool landed = onGround && _holdFrames > 2;
				if (rise && p.grapCount == 0 && !landed) return true;
				if (p.velocity.Y < 0f && _holdFrames < MaxHoldFrames && !landed) return true;
				// 松一帧,vanilla 要 releaseJump 才认新按压
				_jumpHeld = false;
				return false;
			}
			bool air = !onGround && p.grapCount == 0;
			string why = hookJump ? "解钩"
				: p.grapCount > 0 ? null
				: hop ? "再跳一段"
				: rise ? "飞"
				: null;
			if (why == null) return false;
			bool extra = air && p.AnyExtraJumpUsable();
			if (why == "再跳一段")
			{
				_airJumpUsed = _answer;
				if (air && !extra)
				{
					if (_jumpBlockedSaid != _answer)
					{
						_jumpBlockedSaid = _answer;
						DiagLog.Write($"[dodge] 想再跳一段但空中跳已用完 退回飞 vy={p.velocity.Y:0.0} 翅膀{p.wingTime:0}");
					}
					why = "飞";
				}
			}
			DiagLog.Write($"[dodge] 起跳 {why} {(onGround ? "地面" : p.grapCount > 0 ? "钩上" : extra ? "空中跳" : "翅膀")}"
				+ $" vy={p.velocity.Y:0.0} 翅膀{p.wingTime:0}");
			_jumpHeld = true;
			_holdFrames = 0;
			return true;
		}

		// 按当前速度还有几帧碰到我,不会碰到就是 -1
		static int FramesToHit(Player p, NPC boss)
		{
			float gapX = System.Math.Abs(boss.Center.X - p.Center.X) - (boss.width + p.width) * 0.5f;
			float gapY = System.Math.Abs(boss.Center.Y - p.Center.Y) - (boss.height + p.height) * 0.5f;
			float closeX = (boss.Center.X > p.Center.X) == (boss.velocity.X < 0) ? System.Math.Abs(boss.velocity.X) : 0f;
			float closeY = (boss.Center.Y > p.Center.Y) == (boss.velocity.Y < 0) ? System.Math.Abs(boss.velocity.Y) : 0f;
			// 已经重叠就是正在碰,不管它往哪动
			if (gapX <= 0f && gapY <= 0f) return 0;
			if (closeX < 0.1f && closeY < 0.1f) return -1;
			// 已经重叠的轴算 0 帧,不重叠又不靠近的轴撞不上
			float fx = gapX <= 0f ? 0f : (closeX > 0.1f ? gapX / closeX : -1f);
			float fy = gapY <= 0f ? 0f : (closeY > 0.1f ? gapY / closeY : -1f);
			// 两个轴都重叠才算碰到,取晚的那个
			if (fx < 0f || fy < 0f) return -1;
			float f = System.Math.Max(fx, fy);
			return f > 600f ? -1 : (int)f;
		}

		// 冲刺是方向键双击:松一帧再按。没就绪就照常跑
		static int Dash(Player p, int go, bool want)
		{
			// 冲刺中保持方向
			if (_dashDir != 0 && p.dashDelay < 0) return _dashDir;

			// dashDelay 大于 0 冷却,小于 0 正在冲
			bool ready = p.dashType != 0 && p.dashDelay == 0;
			if (!ready)
			{
				if (want && go != 0 && _dashWaitSaid != _answer)
				{
					_dashWaitSaid = _answer;
					DiagLog.Write($"[dodge] 想冲但没就绪 退回跑 dashType={p.dashType} delay={p.dashDelay}");
				}
				_dashGap = false; _dashDir = 0; return go;
			}

			if (_dashGap) { _dashGap = false; return _dashDir; }
			_dashDir = 0;

			if (!want || go == 0) return go;

			DiagLog.Write($"[dodge] 冲刺 方向{(go < 0 ? "左" : "右")}");
			_dashGap = true; _dashDir = go;
			return 0;
		}

		// 钩子能不能勾这一格,照 AI_007_GrapplingHooks_CanTileBeLatchedOnTo
		static bool Hookable(int x, int y)
		{
			if (!Predicates.InBounds(x, y)) return false;
			var t = Main.tile[x, y];
			if (!t.HasTile) return false;
			return Main.tileSolid[t.TileType] || t.TileType == Terraria.ID.TileID.MinecartTrack;
		}

		// 这一侧齐胸高度离墙几格,地图边缘也算墙
		static int WallDistance(Player p, int dir)
		{
			int cx = (int)(p.Center.X / 16f);
			int cy = (int)((p.position.Y + p.height * 0.5f) / 16f);
			for (int d = 1; d <= RoomScanCells; d++)
			{
				int x = cx + dir * d;
				if (x <= EdgeCells || x >= Main.maxTilesX - EdgeCells) return d - 1;
				if (Predicates.IsWall(x, cy)) return d - 1;
			}
			return RoomScanCells;
		}

		// 头顶离天花板几格,地图上边缘也算
		static int CeilingDistance(Player p)
		{
			int cx = (int)(p.Center.X / 16f);
			int top = (int)(p.position.Y / 16f);
			for (int d = 1; d <= RoomScanCells; d++)
			{
				int y = top - d;
				if (y <= EdgeCells) return d - 1;
				if (Predicates.IsWall(cx, y)) return d - 1;
			}
			return RoomScanCells;
		}

		// 脚下到实心块几格,平台能穿所以不算,地图下边缘也算
		static int FloorDistance(Player p)
		{
			int feet = (int)((p.position.Y + p.height) / 16f);
			for (int d = 0; d < RoomScanCells; d++)
			{
				if (feet + d >= Main.maxTilesY - EdgeCells) return d;
				for (int x = (int)(p.position.X / 16f); x <= (int)((p.position.X + p.width - 1) / 16f); x++)
					if (Predicates.IsWall(x, feet + d)) return d;
			}
			return RoomScanCells;
		}

		// 脚下离地几格,平台和地图下边缘也算地,最多探 40 格
		static int CellsAboveGround(Player p)
		{
			int cx = (int)(p.Center.X / 16f);
			int feet = (int)((p.position.Y + p.height) / 16f);
			for (int d = 0; d < 40; d++)
			{
				if (feet + d >= Main.maxTilesY - EdgeCells) return d;
				if (Predicates.IsSolid(cx, feet + d) || Predicates.IsPlatform(cx, feet + d)) return d;
			}
			return 40;
		}

		static void Release() => AxisLock.Release(Owner);

		static string Facts(Player p, NPC boss)
		{
			int pcx = (int)(p.Center.X / 16f), pcy = (int)(p.Center.Y / 16f);
			return "{\"hp_percent\":" + (p.statLife * 100 / System.Math.Max(1, p.statLifeMax))
				 + ",\"i_am_losing_health_over_time\":" + (p.lifeRegen < 0 ? "true" : "false")
				 + ",\"my_speed_to_the_right\":" + (int)(p.velocity.X * 60f / 16f)
				 + ",\"my_speed_upward\":" + (int)(-p.velocity.Y * 60f / 16f)
				 + ",\"threats\":" + ThreatsJson(p)
				 + ",\"incoming_projectiles\":" + ThreatScan.ProjJson(p, pcx, pcy)
				 + ",\"room\":{\"left\":" + WallDistance(p, -1) + ",\"right\":" + WallDistance(p, 1)
				 + ",\"above\":" + CeilingDistance(p) + ",\"below\":" + FloorDistance(p) + "}"
				 + ",\"boss_notes\":" + FieldBooks(boss)
				 + "}";
		}

		// 八个方向的选项,被墙、地面或天花板挡住的写明走不动
		static string DirCriteria(Player p)
		{
			bool left = WallDistance(p, -1) == 0, right = WallDistance(p, 1) == 0;
			bool up = CeilingDistance(p) == 0, down = FloorDistance(p) == 0;
			var sb = new StringBuilder();
			var blocked = new StringBuilder();
			for (var d = Dir.Up; d <= Dir.UpLeft; d++)
			{
				var (x, y) = Vec(d);
				bool xStop = x < 0 ? left : x > 0 && right;
				bool yStop = y < 0 ? up : y > 0 && down;
				string h = x < 0 ? "left" : "right", v = y < 0 ? "up" : "down";
				string move = x == 0 ? $"Move straight {v}." : y == 0 ? $"Move straight {h}." : $"Move {v} and to the {h}.";
				string note = (x == 0 || xStop) && (y == 0 || yStop)
					? $" Blocked: {(y < 0 ? "a ceiling" : y > 0 ? "the floor" : "a wall")}{(x != 0 && y != 0 ? " and a wall are" : " is")} right there, so moving this way does nothing."
					: xStop ? $" The {h} part is blocked by a wall; I would only move {v}."
					: yStop ? $" The {v} part is blocked by {(y < 0 ? "a ceiling" : "the floor")}; I would only move {h}."
					: "";
				sb.Append('"').Append(d).Append("\":\"").Append(move).Append(note).Append("\",");
				if (note.Length > 0) blocked.Append(d).Append(' ');
			}
			_lastBlocked = blocked.ToString();
			return sb.ToString();
		}

		// 同一份 state 的两道题一次发出
		static string Body(string state, string dirs)
			=> "{\"model\":\"" + Model + "\",\"state\":" + Quote(state) + ",\"questions\":{"
			 + "\"direction\":{\"type\":\"choice\",\"instructions\":"
			 + "\"Terraria boss fight. Which way should I move right now so that nothing hits me? "
			 + "Look at every boss part and shot, where each is heading and how soon it reaches me, and how much room I have. "
			 + "Keep an escape route: do not run into a wall, floor or ceiling, or toward another threat. "
			 + "Follow the boss notes.\",\"criteria\":{"
			 + dirs
			 + "\"Stay\":\"Stay where I am.\"}},"
			 + "\"size\":{\"type\":\"choice\",\"instructions\":"
			 + "\"Same moment. How big a move does this dodge need? "
			 + "Ignore which direction; only judge how far and how fast I must get away.\",\"criteria\":{"
			 + "\"Small\":\"A small step. Keeps me in control and free to adjust again right away. "
			 + "Fits when threats are still far or slow, or I am only fine-tuning my position.\","
			 + "\"Medium\":\"A quick burst of a few cells. "
			 + "Fits when something reaches me soon and a small step would not clear it.\","
			 + "\"Large\":\"One big relocation that cannot change direction on the way. "
			 + "Fits when I am cornered, caught between threats, or an attack covers a wide area.\"}}"
			 + "}}";

		static void Fire(string key, string body)
		{
			_busy = true;
			var sw = System.Diagnostics.Stopwatch.StartNew();
			System.Threading.Tasks.Task.Run(async () =>
			{
				try
				{
					using var req = new HttpRequestMessage(HttpMethod.Post, Url);
					req.Headers.Add("Authorization", "Bearer " + key);
					req.Content = new StringContent(body, Encoding.UTF8, "application/json");
					var res = await _http.SendAsync(req);
					string txt = await res.Content.ReadAsStringAsync();
					if (res.IsSuccessStatusCode) _pending = sw.ElapsedMilliseconds + "\u001f" + txt;
					else DiagLog.Write($"[dodge] HTTP {(int)res.StatusCode}");
				}
				catch (System.Exception e) { DiagLog.Write($"[dodge] 请求炸了 {e.GetType().Name} {e.Message}"); }
				finally { _busy = false; }
			});
		}

		// 对照组:按 JevPrior 里这个 boss 的选择比例抽,没记录就均匀抽。间隔取 Jev 实测延迟中位
		const int RandomEveryMs = 300;
		static bool _priorSaid;
		static void RandomPick(int bossType)
		{
			long now = _clock.ElapsedMilliseconds;
			if (now - _actAt < RandomEveryMs) return;
			int samples = JevPrior.Samples(bossType);
			Way = (Dir)JevPrior.Sample(bossType, "direction", 9);
			Amount = (Size)JevPrior.Sample(bossType, "size", 3);
			bool fromJev = samples > 0;
			if (!_priorSaid || !fromJev)
			{
				_priorSaid = fromJev;
				Gate(fromJev ? $"随机组按 Jev 对这个 boss 的 {samples} 次选择抽" : "随机组:这个 boss 没有 Jev 记录,均匀抽");
			}
			Confidence = 0f;
			LatencyMs = 0;
			_actAt = now;
			_answer++;
			DiagLog.Write($"[dodge] random -> {Way}/{Amount}");
		}

		static void Parse(string packed, int bossType)
		{
			int cut = packed.IndexOf('\u001f');
			int ms = 0;
			string txt = packed;
			if (cut > 0) { int.TryParse(packed.Substring(0, cut), out ms); txt = packed.Substring(cut + 1); }

			string dq = Seg(txt, "direction");
			string dp = Field(dq, "choice");
			string sq = Seg(txt, "size");
			string sp = Field(sq, "choice");
			if (dp == null)
			{
				DiagLog.Write($"[dodge] 读不出 choice: {txt.Substring(0, System.Math.Min(160, txt.Length))}");
				return;
			}
			// 没答上来就原地、小幅
			Way = System.Enum.TryParse<Dir>(dp, out var d) ? d : Dir.Stay;
			Amount = System.Enum.TryParse<Size>(sp, out var s) ? s : Size.Small;
			DiagLog.Write($"[dodge] jev {ms}ms -> {dp}/{sp} 方向{Rank(Seg(dq, "probabilities"))} 幅度{Rank(Seg(sq, "probabilities"))}"
				+ (_lastBlocked.Length > 0 ? $" 挡住的方向 {_lastBlocked}" : ""));
			LatencyMs = ms;
			Confidence = Num(dq, "confidence", 0f);
			_actAt = _clock.ElapsedMilliseconds;
			_answer++;

			Probs = Seg(dq, "probabilities") ?? "";
			TopTwo = Rank(Probs);
			// 意图变了或同一意图持续 4 秒才发到聊天栏
			long now = _clock.ElapsedMilliseconds;
			var said = $"{Way}/{Amount}";
			if (said != _saidAct || now - _saidAt > 4000)
			{
				string tag = said == _saidAct ? $"  [held {(now - _saidAt) / 1000}s]" : "";
				_saidAct = said; _saidAt = now;
				Main.NewText($"<Jev> {said}{tag}  ({TopTwo})  confidence {Confidence:0.00}  {ms}ms", 90, 230, 120);
			}

			JevPrior.Record(bossType, ("direction", (int)Way, 9), ("size", (int)Amount, 3));

			JevLog.Add(new JevLog.Entry
			{
				Ms = _clock.ElapsedMilliseconds,
				Site = "dodge",
				State = _lastFacts,
				Pick = $"{dp}/{sp}  →  " + Last,
				Confidence = Confidence,
				Probs = Probs,
				Why = "jev",
				LatencyMs = ms,
			});
		}

		// 按问题名取出响应里那一段 {...}
		static string Seg(string s, string question)
		{
			if (s == null) return null;
			int i = s.IndexOf("\"" + question + "\"", System.StringComparison.Ordinal);
			if (i < 0) return null;
			int a = s.IndexOf('{', i);
			if (a < 0) return null;
			int depth = 0;
			for (int j = a; j < s.Length; j++)
			{
				if (s[j] == '{') depth++;
				else if (s[j] == '}' && --depth == 0) return s.Substring(a, j - a + 1);
			}
			return null;
		}

		static string Field(string s, string name)
		{
			if (s == null) return null;
			int i = s.IndexOf("\"" + name + "\"", System.StringComparison.Ordinal);
			if (i < 0) return null;
			i = s.IndexOf(':', i);
			if (i < 0) return null;
			i++;
			while (i < s.Length && (s[i] == ' ' || s[i] == '"')) i++;
			int j = i;
			while (j < s.Length && s[j] != '"' && s[j] != ',' && s[j] != '}') j++;
			return s.Substring(i, j - i).Trim();
		}

		static float Num(string seg, string name, float dflt)
			=> seg != null && float.TryParse(Field(seg, name), out float v) ? v : dflt;

		// 从 probabilities 里挑最高的两个,如 "Away:0.61 Hold:0.30"
		static string Rank(string probs)
		{
			if (string.IsNullOrEmpty(probs)) return "";
			string ka = "", kb = "";
			float va = -1f, vb = -1f;
			foreach (string part in probs.Split(','))
			{
				int c = part.LastIndexOf(':');
				if (c < 0) continue;
				string k = part.Substring(0, c).Replace("\"", "").Replace("{", "").Trim();
				if (!float.TryParse(part.Substring(c + 1).Replace("}", "").Trim(), out float v)) continue;
				if (v > va) { kb = ka; vb = va; ka = k; va = v; }
				else if (v > vb) { kb = k; vb = v; }
			}
			if (va < 0f) return "";
			return vb < 0f ? $"{ka}:{va:0.00}" : $"{ka}:{va:0.00} {kb}:{vb:0.00}";
		}

		static string JsonStr(string s) => s == null ? "" : s.Replace("\\", "").Replace("\"", "");

		static string FieldBooks(NPC locked)
		{
			var sb = new StringBuilder("{");
			foreach (var (name, text) in BossBook.OnField(locked.type))
			{
				if (sb.Length > 1) sb.Append(',');
				sb.Append('"').Append(JsonStr(name)).Append("\":\"").Append(JsonStr(text)).Append('"');
			}
			return sb.Append('}').ToString();
		}

		static string Quote(string s)
		{
			var sb = new StringBuilder("\"");
			foreach (char c in s)
			{
				if (c == '"' || c == '\\') sb.Append('\\').Append(c);
				else if (c == '\n' || c == '\r') sb.Append(' ');
				else sb.Append(c);
			}
			return sb.Append('"').ToString();
		}
	}
}
