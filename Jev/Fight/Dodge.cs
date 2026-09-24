using System.Net.Http;
using System.Text;
using Terraria;

namespace TerraBlind
{
	// Jev 给意图，不给按键
	// 钩爪/冲刺/二段跳/飞行，由代码挑
	public enum Horiz { Away, Hold, Near }
	// 竖直方向往哪动,地上空中都有效
	public enum Vert { Rise, HopUp, Hover, Drift, DropLayer, Plunge }
	// 冲刺,或者往八个方向之一甩钩爪
	public enum Skill { None, Dash, HookUp, HookUpRight, HookRight, HookDownRight, HookDown, HookDownLeft, HookLeft, HookUpLeft }

	// boss 战的走位。两层:Jev 每次反应说"该拉开还是该贴脸",反射层每帧算
	// "这一刻往左还是往右、跳不跳"。让 大约250ms 的判断直接当按键会吃满伤害。对，我测过。
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

		// 二段跳:落地或钩爪中了才充能。,空中再按一次触发,而且必须松一帧才算新按压
		// 这次滞空已经跳过空中跳了。只用来分"第一段"和"后续段",能不能跳问 AnyExtraJumpUsable
		static bool _airJumped;
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
		public static Horiz Hor = Horiz.Away;
		public static Vert Vt = Vert.Drift;
		public static Skill Sk = Skill.None;
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
		// 发出去的各选项现算后果,答案回来时记进日志
		static string _lastConseq = "";
		static readonly System.Text.RegularExpressions.Regex ConseqRx =
			new("\"(\\w+)\":\"[^\"]*?Right now: ([^\"]*)\"");

		// 从题目里抠出每个选项的"现在选它"那一段
		static string Conseq(string body)
		{
			var sb = new StringBuilder();
			foreach (System.Text.RegularExpressions.Match m in ConseqRx.Matches(body))
				sb.Append(m.Groups[1].Value).Append('=').Append(m.Groups[2].Value).Append(" | ");
			return sb.ToString();
		}
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

		// 场上所有 boss 部件里最快撞到我的帧数,没有朝我来的就是 -1
		static int SoonestBossHit(Player p)
		{
			int best = -1;
			for (int i = 0; i < Main.maxNPCs; i++)
			{
				var npc = Main.npc[i];
				if (npc == null || !npc.active || npc.friendly || !IsBossLike(npc)) continue;
				int f = FramesToHit(p, npc);
				if (f >= 0 && (best < 0 || f < best)) best = f;
			}
			return best;
		}

		// 场上每种 boss 部件一项,Jev 从里面选 Away 时背对谁
		static readonly System.Collections.Generic.List<(string Id, int Type)> _threats = new();
		// Jev 选的那种部件,-1 就背对锁定的那个
		public static int FleeType = -1;

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

		// 刷新 _threats,每种取最近的一个报位置、速度、伤害
		static string ThreatsJson(Player p)
		{
			_threats.Clear();
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
				string id = ThreatId(n.type);
				_threats.Add((id, n.type));
				int f = FramesToHit(p, n);
				if (sb.Length > 1) sb.Append(',');
				sb.Append("{\"id\":\"").Append(JsonStr(id)).Append('"')
				  .Append(",\"name\":\"").Append(JsonStr(n.TypeName)).Append('"')
				  .Append(",\"contact_damage_percent_of_my_hp\":").Append(n.damage * 100 / System.Math.Max(1, p.statLife))
				  .Append(",\"cells_to_my_right\":").Append(cx)
				  .Append(",\"cells_above_me\":").Append(cy)
				  .Append(",\"speed_to_the_right\":").Append((int)(n.velocity.X * 60f / 16f))
				  .Append(",\"speed_upward\":").Append((int)(-n.velocity.Y * 60f / 16f))
				  .Append(",\"frames_until_it_hits_me\":").Append(f < 0 ? "\"not heading at me\"" : f.ToString())
				  .Append('}');
			}
			return sb.Append(']').ToString();
		}

		// Away 时往哪边:背对 Jev 选的那种部件里最近的一个
		static int _saidFlee = -2;
		static int AwaySide(Player p, NPC boss)
		{
			var from = (FleeType >= 0 ? Nearest(p, FleeType) : null) ?? boss;
			if (from.type != _saidFlee) { _saidFlee = from.type; Gate($"退的时候背对 {from.TypeName}"); }
			return from.Center.X > p.Center.X ? -1 : 1;
		}

		public static void Tick()
		{
			if (!Enabled) return;
			var p = Main.LocalPlayer;
			if (p == null || !p.active || p.dead) { Release(); return; }
			if (Manual.On) { Gate("人工"); Release(); return; }

			var boss = Boss(p, out int dist);
			if (boss == null) { Gate("no boss"); Release(); return; }

			var done = _pending;
			if (done != null) { _pending = null; Parse(done, boss.type); }
			string key = JevCombat.Key();
			if (RandomBrain) RandomPick(p, boss.type);
			else if (key != null && !_busy)
			{
				_lastFacts = Facts(p, boss, dist);
				string body = Body(_lastFacts, FleeQuestion(p), HorizCriteria(p, boss), VertCriteria(p), HookCriteria(p));
				_lastConseq = Conseq(body);
				Fire(key, body);
			}

			// 意图过期时用 Away/Drift/None
			bool stale = _clock.ElapsedMilliseconds - _actAt > IntentTtlMs;
			var hor = stale ? Horiz.Away : Hor;
			var vt = stale ? Vert.Drift : Vt;
			var sk = stale ? Skill.None : Sk;

			// 羽落要按住 up,所以也拿 Vertical
			if (!AxisLock.Take(Owner, Ax.Move | Ax.Jump | Ax.Vertical, () => Enabled))
			{ Gate("Move axis taken by " + AxisLock.Held(Ax.Move)); return; }

			Gate($"driving {boss.TypeName} {dist}格 act={hor}/{vt}/{sk}");
			Drive(p, boss, dist, hor, vt, sk);
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

		// 反射层,每帧把意图翻译成按键
		static void Drive(Player p, NPC boss, int dist, Horiz hor, Vert vt, Skill sk)
		{
			float dx = boss.Center.X - p.Center.X;
			bool bossRight = dx > 0;
			int away = AwaySide(p, boss);
			int toward = bossRight ? 1 : -1;
			int go = 0;
			bool onGround = p.velocity.Y == 0f;

			int framesToHit = SoonestBossHit(p);
			// 这么多帧内会被 boss 或弹幕碰到就算 incoming
			const int soon = 24;
			int projHit = ThreatScan.SoonestHit(p);
			bool incoming = (framesToHit >= 0 && framesToHit <= soon)
						 || (projHit >= 0 && projHit <= soon);
			if (incoming && !_wasIncoming)
				DiagLog.Write($"[dodge] 预警 撞击还有{framesToHit}帧 弹幕还有{projHit}帧 阈值{soon}"
					+ $" 最快的是{ThreatScan.SoonestName(p)}");
			_wasIncoming = incoming;

			switch (hor)
			{
				// 一直跑,不因为够远就停
				case Horiz.Away:
					go = away;
					break;
				case Horiz.Near:
					go = toward;
					break;
			}

			// 打不中时每 PoseSecs 秒在一直往上和一直往下之间翻
			if (TooFar)
			{
				bool up = (_lowSecs / PoseSecs) % 2 == 0;
				var was = vt;
				vt = up ? Vert.Rise : Vert.Plunge;
				if (vt != _pose) DiagLog.Write($"[dodge] 打不中{_lowSecs}秒 换姿势 {was}->{vt}");
				_pose = vt;
			}
			else _pose = null;

			bool hooking = Hook(p, sk, out bool hookJump);
			bool jump = Jump(p, onGround, vt, hookJump, incoming);

			int want0 = go;
			go = Dash(p, go, sk == Skill.Dash);
			bool dashing = go != want0 || _dashGap;

			if (go < 0) p.controlLeft = true;
			else if (go > 0) p.controlRight = true;
			if (jump) p.controlJump = true;

			// 挂着钩子时上下键不按
			bool layer = DropLayer(p, vt == Vert.DropLayer);
			bool dive = p.grapCount == 0 && (vt == Vert.Plunge || layer);
			if (dive) p.controlDown = true;
			else if (vt == Vert.Hover && !onGround && p.grapCount == 0) p.controlUp = true;

			Fly(p, vt);
			TrackDps();
			Hurt(p, boss, dist, hor, vt, sk);
			Last = $"{hor}/{vt}/{sk} boss {(bossRight ? "R" : "L")}{dist} go {(go == 0 ? "-" : go < 0 ? "L" : "R")}"
				 + (jump ? (onGround ? " +jump" : " +airjump") : "") + (hooking ? " +hook" : "")
				 + (dashing ? " +dash" : "") + (p.dashDelay < 0 ? " [dashing]" : "")
				 + (incoming ? $" hit in {framesToHit}f" : "");
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

		static void TrackDps()
		{
			int hp = TotalBossHp();
			if (_bossPrevHp < 0) { _bossPrevHp = hp; return; }
			int d = _bossPrevHp - hp;
			_bossPrevHp = hp;
			// 回血或 boss 离场算出负数,当 0
			_dpsRing[_dpsAt] = d > 0 ? d : 0;
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
			// 攒够 5 秒样本后,低于常驻 35% 的连续秒数
			if (_histCount >= 5 && DpsTypical > 0 && DpsPct < 35) _lowSecs++; else _lowSecs = 0;
			var me = Main.LocalPlayer;
			DiagLog.Write($"[dodge] dps {BossDps} 常驻{DpsTypical} {DpsPct}% 低了{_lowSecs}秒"
				+ $" | 脚在第{(int)((me.position.Y + me.height) / 16f)}行 离地{CellsAboveGround(me)} 头顶{CeilingDistance(me)}");
		}
		static int _lowSecs;
		// 打不中时上一次换到的姿势,变了才记日志
		static Vert? _pose;
		public static bool TooFar => _lowSecs >= 3;
		const int PoseSecs = 3;

		// 每次掉血记一行,带最近的 NPC 和弹幕
		static int _prevHp = -1;
		static void Hurt(Player p, NPC boss, int dist, Horiz hor, Vert vt, Skill sk)
		{
			if (_prevHp < 0) { _prevHp = p.statLife; return; }
			int lost = _prevHp - p.statLife;
			_prevHp = p.statLife;
			if (lost <= 0) return;
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
			DiagLog.Write($"[dodge] 掉血 {lost} 剩{p.statLife}/{p.statLifeMax}"
				+ $" 离{boss.TypeName} {dist}格 意图{hor}/{vt}/{sk}"
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

		// 往下一层:按住下穿过脚下或下方最近的一层平台,脚过了就松开。一次回答穿一层
		static int _dropRow = -1;
		static int _dropUsed = -1;
		static bool DropLayer(Player p, bool want)
		{
			int feet = (int)((p.position.Y + p.height) / 16f);
			if (!want || p.grapCount > 0) { _dropRow = -1; return false; }
			if (_dropRow < 0 && _dropUsed != _answer)
			{
				_dropUsed = _answer;
				_dropRow = PlatformBelow(p, feet);
				DiagLog.Write(_dropRow >= 0 ? $"[dodge] 往下一层 脚在第{feet}行 穿第{_dropRow}行"
					: $"[dodge] 往下一层 但脚下第{feet}行起到实心块或 {RoomScanCells} 格之间没有平台");
			}
			if (_dropRow >= 0 && feet > _dropRow) _dropRow = -1;
			return _dropRow >= 0;
		}

		// 从脚这一行往下第一层平台,先碰到实心块或探满 RoomScanCells 就是 -1
		static int PlatformBelow(Player p, int feet)
		{
			for (int y = feet; y < feet + RoomScanCells && y < Main.maxTilesY - EdgeCells; y++)
			{
				if (OnPlatform(p, y)) return y;
				for (int x = (int)(p.position.X / 16f); x <= (int)((p.position.X + p.width - 1) / 16f); x++)
					if (Predicates.IsWall(x, y)) return -1;
			}
			return -1;
		}

		// 脚下这一行踩着的是平台而不是实心块
		static bool OnPlatform(Player p, int feet)
		{
			bool plat = false;
			for (int x = (int)(p.position.X / 16f); x <= (int)((p.position.X + p.width - 1) / 16f); x++)
			{
				if (Predicates.IsWall(x, feet)) return false;
				if (Predicates.IsPlatform(x, feet)) plat = true;
			}
			return plat;
		}

		// 上一次再跳一段的回答
		static int _airJumpUsed = -1;
		static int _jumpBlockedSaid = -1;
		static bool _wasIncoming;
		static bool _flying;
		static float _prevWing;
		// 飞行开始和结束各记一行。wingTime 在掉而且在上升才算飞,缓降也烧 wingTime
		static void Fly(Player p, Vert vt)
		{
			bool now = p.wingTime < _prevWing - 0.01f && p.velocity.Y < -0.5f;
			_prevWing = p.wingTime;
			if (now == _flying) return;
			_flying = now;
			DiagLog.Write($"[dodge] 飞行{(now ? "开始" : "结束")} 竖直动作={vt}"
				+ $" wingMax={p.wingTimeMax} wingTime={p.wingTime:0.0} vy={p.velocity.Y:0.0}");
		}

		// 钩爪。只在发射那一帧占用光标
		static bool Hook(Player p, Skill dir, out bool hookJump)
		{
			bool want = HookVector(dir) != null;
			hookJump = false;
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
			if (_hookCooldown > 0) { _hookCooldown--; return false; }
			if (!want) { _hookFrames = 0; return false; }
			// 按一帧松一帧,vanilla 要 releaseHook 才认新按压
			if (_hookFrames++ > HookGiveUpFrames) return false;
			if (_hookHeld) { _hookHeld = false; return true; }
			if (!HookTarget(p, dir, out int ax, out int ay))
			{
				if (_hookMissSaid != _answer) { _hookMissSaid = _answer; DiagLog.Write($"[dodge] 想往{dir}甩钩爪 但射程内勾不到东西"); }
				_hookFrames = 0;
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

		// 钩爪选项对应的方向,不是钩爪选项就是 null
		static Microsoft.Xna.Framework.Vector2? HookVector(Skill s) => s switch
		{
			Skill.HookUp => new(0, -1),
			Skill.HookUpRight => new(1, -1),
			Skill.HookRight => new(1, 0),
			Skill.HookDownRight => new(1, 1),
			Skill.HookDown => new(0, 1),
			Skill.HookDownLeft => new(-1, 1),
			Skill.HookLeft => new(-1, 0),
			Skill.HookUpLeft => new(-1, -1),
			_ => null,
		};

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
		static bool HookTarget(Player p, Skill dir, out int tx, out int ty)
		{
			tx = ty = 0;
			var v = HookVector(dir);
			if (v == null) return false;
			var step = Microsoft.Xna.Framework.Vector2.Normalize(v.Value) * 4f;
			float range = HookRangePx(p);
			var pt = p.Center;
			for (float d = 0f; d <= range; d += 4f, pt += step)
			{
				int x = (int)(pt.X / 16f), y = (int)(pt.Y / 16f);
				if (Hookable(x, y)) { tx = x; ty = y; return true; }
			}
			return false;
		}

		// 跳键。空中新按一下,有空中跳 vanilla 先用空中跳,没有才是翅膀(Player.cs:25618)
		static bool Jump(Player p, bool onGround, Vert vt, bool hookJump, bool incoming)
		{
			if (onGround) _airJumped = false;
			if (_jumpHeld)
			{
				// 要解钩就先松一帧,下一帧的新按压才会解钩
				if (hookJump) { _jumpHeld = false; return false; }
				_holdFrames++;
				bool landed = onGround && _holdFrames > 2;
				if (vt == Vert.Rise && p.grapCount == 0 && !landed) return true;
				if (p.velocity.Y < 0f && _holdFrames < MaxHoldFrames && !landed) return true;
				// 松一帧,vanilla 要 releaseJump 才认新按压
				_jumpHeld = false;
				return false;
			}
			bool air = !onGround && p.grapCount == 0;
			string why = hookJump ? "解钩"
				: p.grapCount > 0 ? null
				: vt == Vert.Rise ? "飞"
				: vt == Vert.HopUp && _airJumpUsed != _answer ? "再跳一段"
				: incoming && (!air || !_airJumped) ? "预警"
				: null;
			if (why == null) return false;
			bool extra = air && p.AnyExtraJumpUsable();
			if (air && why != "飞" && !extra)
			{
				if (_jumpBlockedSaid != _answer)
				{
					_jumpBlockedSaid = _answer;
					DiagLog.Write($"[dodge] 想起跳({why})但空中跳已用完 vy={p.velocity.Y:0.0} 翅膀{p.wingTime:0}");
				}
				return false;
			}
			if (vt == Vert.HopUp) _airJumpUsed = _answer;
			if (extra) _airJumped = true;
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

		// 冲刺是方向键双击:松一帧再按
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
					DiagLog.Write($"[dodge] 想冲但没就绪 dashType={p.dashType} delay={p.dashDelay}");
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

		// boss 速度(格/秒)和最近一秒的峰值
		static readonly float[] _spdRing = new float[60];
		static int _spdAt;
		static float _bossRecentTop;
		static int BossSpeed(NPC boss)
		{
			float v = (System.Math.Abs(boss.velocity.X) + System.Math.Abs(boss.velocity.Y)) * 60f / 16f;
			_spdRing[_spdAt] = v;
			_spdAt = (_spdAt + 1) % _spdRing.Length;
			float top = 0f;
			foreach (float s in _spdRing) if (s > top) top = s;
			_bossRecentTop = top;
			return (int)v;
		}

		static string Facts(Player p, NPC boss, int dist)
		{
			float dx = (boss.Center.X - p.Center.X) / 16f;
			float dy = (boss.Center.Y - p.Center.Y) / 16f;
			int bossSpd = BossSpeed(boss);
			int pcx = (int)(p.Center.X / 16f), pcy = (int)(p.Center.Y / 16f);
			return "{\"hp_percent\":" + (p.statLife * 100 / System.Math.Max(1, p.statLifeMax))
				 + ",\"nearest_part\":\"" + JsonStr(boss.TypeName) + "\""
				 + ",\"nearest_part_hp_percent\":" + (boss.life * 100 / System.Math.Max(1, boss.lifeMax))
				 + ",\"nearest_part_speed_cells_per_second\":" + bossSpd
				 + ",\"nearest_part_fastest_in_the_last_second\":" + (int)_bossRecentTop
				 + ",\"nearest_part_cells_horizontal\":" + (int)System.Math.Abs(dx)
				 + ",\"i_am_above_nearest_part_by\":" + (int)(-dy)
				 + ",\"percent_of_my_usual_damage_right_now\":" + DpsPct
				 + ",\"seconds_i_have_been_unable_to_hit_it\":" + _lowSecs
				 + ",\"i_am_too_far_to_hit_it\":" + (TooFar ? "true" : "false")
				 + ",\"cells_of_room_to_my_left\":" + WallDistance(p, -1)
				 + ",\"cells_of_room_to_my_right\":" + WallDistance(p, 1)
				 + ",\"cells_of_room_above_me\":" + CeilingDistance(p)
				 + ",\"threats\":" + ThreatsJson(p)
				 + ",\"incoming_projectiles\":" + ThreatScan.ProjJson(p, pcx, pcy)
				 + ",\"projectile_pressure\":" + ThreatScan.PressureJson(p)
				 + ",\"how_the_bosses_here_fight\":" + FieldBooks(boss)
				 + "}";
		}

		// 场上不止一种部件时才问背对谁,选项就是 _threats
		static string FleeQuestion(Player p)
		{
			if (_threats.Count < 2) return "";
			var sb = new StringBuilder("\"flee_from\":{\"type\":\"choice\",\"instructions\":"
				+ "\"When the horizontal question picks Away, the code runs directly away from the part picked here, every frame. "
				+ "Which part in threats should I get away from right now? "
				+ "Higher contact damage, closer, and fewer frames_until_it_hits_me all mean more reason to flee it. "
				+ "If the boss notes say which part to stay away from, follow them. "
				+ "Also check whether running from one part carries me straight into another.\",\"criteria\":{");
			for (int i = 0; i < _threats.Count; i++)
			{
				if (i > 0) sb.Append(',');
				var t = _threats[i];
				var n = Nearest(p, t.Type);
				sb.Append('"').Append(JsonStr(t.Id)).Append("\":\"Flee from ")
				  .Append(JsonStr(n.TypeName)).Append(". Right now: ")
				  .Append(SideText(p, n.Center.X > p.Center.X ? -1 : 1)).Append('"');
			}
			return sb.Append("}},").ToString();
		}

		// 往 dir 那边跑的后果:还剩几格,那边有哪些 boss 部件
		static string SideText(Player p, int dir)
		{
			string side = dir < 0 ? "left" : "right";
			var sb = new StringBuilder($"I run {side}, with {WallDistance(p, dir)} cells of room on the {side}; ");
			int pcx = (int)(p.Center.X / 16f), pcy = (int)(p.Center.Y / 16f);
			int count = 0;
			foreach (var t in _threats)
			{
				var n = Nearest(p, t.Type);
				int cx = (int)(n.Center.X / 16f) - pcx, cy = pcy - (int)(n.Center.Y / 16f);
				if (cx * dir <= 0) continue;
				sb.Append(count++ == 0 ? "on that side: " : ", ").Append(JsonStr(n.TypeName))
				  .Append($" ({System.Math.Abs(cx)} cells across, {System.Math.Abs(cy)} cells {(cy >= 0 ? "above" : "below")} me)");
			}
			if (count == 0) sb.Append("no boss part on that side");
			return sb.ToString();
		}

		// 水平不动的后果:最快撞到我的部件和弹幕还有几帧
		static string HoldText(Player p)
		{
			string who = null;
			int best = -1;
			foreach (var t in _threats)
			{
				var n = Nearest(p, t.Type);
				int f = FramesToHit(p, n);
				if (f >= 0 && (best < 0 || f < best)) { best = f; who = n.TypeName; }
			}
			var sb = new StringBuilder(who == null ? "no boss part is heading at me"
				: $"the first part to hit me is {JsonStr(who)}, in {best} frames");
			int proj = ThreatScan.SoonestHit(p);
			sb.Append(proj < 0 ? "; no shot is heading at me" : $"; the first shot, {JsonStr(ThreatScan.SoonestName(p))}, hits me in {proj} frames");
			return sb.ToString();
		}

		// 水平那一题的选项,每个后面接现算的后果
		static string HorizCriteria(Player p, NPC boss)
		{
			int toward = boss.Center.X > p.Center.X ? 1 : -1;
			string away = _threats.Count < 2 ? " Right now: " + SideText(p, -toward)
				: " Left or right is set by the flee_from question; each of its options says which way I would run and what is there";
			return "\"Away\":\"Open the distance. Buys reaction time: anything charging at me takes longer to arrive, "
			 + "and shots are easier to read. Costs damage: back off until I cannot hit it and the fight never ends; "
			 + "and when both sides are crowded there is nowhere to back off to." + away + "\","
			 + "\"Hold\":\"Stand still horizontally: press neither left nor right. The distance changes whenever the boss moves. "
			 + "Buys not running into anything on either side. Costs any horizontal dodging; "
			 + "only the vertical question can get me out of the way. Right now: " + HoldText(p) + "\","
			 + "\"Near\":\"Close in. Buys hitting it: percent_of_my_usual_damage_right_now is my damage as a percent of my usual "
			 + "for this fight, and swinging around 100 is normal; one low second means nothing. "
			 + "Only i_am_too_far_to_hit_it being true, or seconds_i_have_been_unable_to_hit_it adding up, means my position is the problem. "
			 + "Costs reaction time: the closer I am, the fewer frames between a charge starting and it hitting me, "
			 + "and one body hit costs far more than a few seconds of lost damage. Right now: " + SideText(p, toward) + "\"";
		}

		// 三道题共用的常识:给自己留退路
		const string KeepRoom = " A human fighting a boss always keeps an escape route: stay where there is room above, below, left and right. "
			+ "Hugging a ceiling, a wall or the floor gives up dodging in that direction, so the next attack from the other side leaves one way out. "
			+ "When everything is on one side, running from it the whole time ends in a corner.";

		// 竖直那一题的选项,每个后面接现算的后果
		static string VertCriteria(Player p)
		{
			int feet = (int)((p.position.Y + p.height) / 16f);
			int room = CeilingDistance(p);
			string up = room == 0 ? "my head is already against a solid block" : $"{room} cells of room above my head";
			int layer = PlatformBelow(p, feet);
			int next = layer < 0 ? -1 : PlatformBelow(p, layer + 1);
			string drop = layer < 0 ? $"no platform between me and solid ground (looked {RoomScanCells} cells down), so nothing happens"
				: (layer == feet ? "I drop through the platform under my feet" : $"I drop through the platform {layer - feet} cells below")
				  + (next >= 0 ? $", and the next one is {next - layer} cells below that" : ", and there is no platform below that one");
			int floor = SolidBelow(p, feet);
			string plunge = floor < 0 ? $"no solid ground within {RoomScanCells} cells below"
				: floor == 0 ? "I am standing on solid ground, so nothing happens" : $"I fall {floor} cells to solid ground";
			string still = NearestHitVertical(p);
			return "\"Rise\":\"Keep going up: hold jump, jumping from the ground and then flying on wings until another option is picked. "
			 + "Buys steady height gain; a charge locks the height I was at when it started, so climbing gets out of its line. "
			 + "Costs wing time and headroom; with my head against a block I cannot climb and only burn wings. "
			 + $"Right now: {up}, {(p.wingTimeMax > 0 ? (int)(p.wingTime * 100 / p.wingTimeMax) : 0)}% wing time left\","
			 + "\"HopUp\":\"One hop up: jump from the ground, or use one air jump in the air, released at the top. One hop per answer. "
			 + "Buys a quick short step up without burning wings. Costs one air jump; with none left, nothing happens in the air. "
			 + $"Right now: {(room == 0 ? "my head is already against a solid block, so a hop gains no height and only spends an air jump" : up)}, "
			 + $"{(p.velocity.Y == 0f ? "I am standing, so I will jump" : p.AnyExtraJumpUsable() ? "an air jump is available" : "no air jump left, so nothing happens")}\","
			 + "\"Hover\":\"Hold this height: hold up in the air, falling at a tenth of normal speed; on the ground I just stand. "
			 + "Buys staying put in the air without wings. Costs almost no vertical movement, and a charge is aimed at exactly this height. "
			 + $"Right now: {still}\","
			 + "\"Drift\":\"Sink slowly: press neither up nor down, falling at a third of normal speed; on the ground I just stand. "
			 + "Buys losing a little height while staying free to change my mind: unlike Plunge I do not drop several layers at once, "
			 + "and unlike Hover I do not sit at one height for a charge to lock onto. "
			 + "Costs nothing but speed: leaving a dangerous area this way takes a long time. "
			 + $"Right now: {still}\","
			 + "\"DropLayer\":\"Down one layer: hold down to pass through the platform under me (in the air, the nearest one below), "
			 + "then let go and float down to the next one. One layer per answer. "
			 + "Buys leaving this layer: whatever is pressing down, shots from above and fire on this layer miss once I am a layer lower; "
			 + "and it loses only one layer of height, unlike Plunge, leaving a platform to stand and shoot from. "
			 + "Costs this footing; with no platform between me and solid ground, nothing happens. "
			 + $"Right now: {drop}\","
			 + "\"Plunge\":\"Keep going down: hold down, cancelling the slow fall, falling at full speed through every platform "
			 + "until I land on solid ground or another option is picked. "
			 + "Buys the fastest way down and away from things pressing from above, shots from above (from_above in projectile_pressure) "
			 + "or a ceiling with no room; landing refills air jumps and wings. "
			 + "Costs height, and it does not stop at any platform on the way. "
			 + $"Right now: {plunge}\"";
		}

		// 脚下到实心块几格,平台不算,探满 RoomScanCells 就是 -1
		static int SolidBelow(Player p, int feet)
		{
			for (int d = 0; d < RoomScanCells && feet + d < Main.maxTilesY - EdgeCells; d++)
				for (int x = (int)(p.position.X / 16f); x <= (int)((p.position.X + p.width - 1) / 16f); x++)
					if (Predicates.IsWall(x, feet + d)) return d;
			return -1;
		}

		// 最快撞到我的部件在上面还是下面、还有几帧
		static string NearestHitVertical(Player p)
		{
			NPC who = null;
			int best = -1;
			foreach (var t in _threats)
			{
				var n = Nearest(p, t.Type);
				int f = FramesToHit(p, n);
				if (f >= 0 && (best < 0 || f < best)) { best = f; who = n; }
			}
			if (who == null) return "no boss part is heading at me";
			int cy = (int)(p.Center.Y / 16f) - (int)(who.Center.Y / 16f);
			string where = cy > 0 ? $"{cy} cells above me" : cy < 0 ? $"{-cy} cells below me" : "at my height";
			return $"the first part to hit me is {JsonStr(who.TypeName)}, {where}, in {best} frames";
		}

		// 八个钩爪方向,每个后面接现算的落点
		static string HookCriteria(Player p)
		{
			var sb = new StringBuilder();
			int range = (int)(HookRangePx(p) / 16f);
			for (var s = Skill.HookUp; s <= Skill.HookUpLeft; s++)
			{
				string name = HookName(s);
				sb.Append('"').Append(s).Append("\":\"Throw the hook ").Append(name).Append(". Right now: ");
				if (!HookTarget(p, s, out int x, out int y))
					sb.Append(range == 0 ? "I have no hook, so nothing happens" : $"nothing to latch onto within {range} cells {name}, so nothing happens");
				else
				{
					var at = new Microsoft.Xna.Framework.Vector2(x * 16 + 8, y * 16 + 8);
					int cells = (int)(Microsoft.Xna.Framework.Vector2.Distance(at, p.Center) / 16f);
					sb.Append($"it latches onto a block {cells} cells {name} and pulls me there");
					string who = null;
					float best = float.MaxValue;
					foreach (var t in _threats)
					{
						var n = Nearest(p, t.Type);
						float d = Microsoft.Xna.Framework.Vector2.Distance(n.Center, at);
						if (d < best) { best = d; who = n.TypeName; }
					}
					if (who != null) sb.Append($"; the closest boss part to that spot is {JsonStr(who)}, {(int)(best / 16f)} cells away");
				}
				sb.Append("\",");
			}
			return sb.ToString();
		}

		static string HookName(Skill s) => s switch
		{
			Skill.HookUp => "straight up",
			Skill.HookUpRight => "up and right at 45 degrees",
			Skill.HookRight => "straight right",
			Skill.HookDownRight => "down and right at 45 degrees",
			Skill.HookDown => "straight down",
			Skill.HookDownLeft => "down and left at 45 degrees",
			Skill.HookLeft => "straight left",
			_ => "up and left at 45 degrees",
		};

		static string Body(string state, string flee, string horiz, string vert, string skill)
			=> "{\"model\":\"" + Model + "\",\"state\":" + Quote(state) + ",\"questions\":{" + flee
			 + "\"horizontal\":{\"type\":\"choice\",\"instructions\":"
			 + "\"Terraria boss fight. My weapon aims and fires by itself, so I only decide how to move." + KeepRoom
			 + " Touching a boss part costs far more health than one shot, so avoiding contact always comes first; "
			 + "but too far away my shots miss, so the goal is a distance where I can hit it without being hit. "
			 + "There is no fixed number for it; judge by results: damage still landing means close enough, getting hit means too close "
			 + "(if the boss notes give a distance, follow them). "
			 + "This question only decides how my distance to the nearest boss part (nearest_part) should change; "
			 + "danger also comes from every other part in threats, so read the consequence at the end of each option. "
			 + "Vertical movement and skills are two other questions; together they make one move. "
			 + "This is an intent, not a key press; the code works out left or right every frame. "
			 + "Each option ends with what picking it does right now, computed from my current position.\",\"criteria\":{"
			 + horiz + "}},"
			 + "\"vertical\":{\"type\":\"choice\",\"instructions\":"
			 + "\"Same fight. This question only decides where I move vertically; the horizontal and skill questions are separate, "
			 + "and together they make one move." + KeepRoom
			 + " Up and down matter equally; vertical position is the other half of dodging. "
			 + "Every option works both standing and in the air; the code handles whichever I am. "
			 + "I have a featherfall potion. Air jumps and wings refill only on landing. While hooked, this question has no effect. "
			 + "Each option ends with what picking it does right now, computed from my current position.\",\"criteria\":{"
			 + vert + "}},"
			 + "\"skill\":{\"type\":\"choice\",\"instructions\":"
			 + "\"Same fight. This question only decides whether to use the grappling hook or a dash; the horizontal and vertical questions are separate, "
			 + "and together they make one move." + KeepRoom
			 + " Options starting with Hook throw the hook that way: it flies in a straight line, latches onto the first block in its path, "
			 + "pulls me all the way there, and I jump off on arrival. "
			 + "Buys moving much faster than running, which helps most when running cannot outpace something chasing me. "
			 + "Costs steering: I cannot change direction while being pulled, there is a gap before it latches, and a short cooldown after; "
			 + "I stop wherever it pulls me, so hooking a ceiling pins me under the ceiling. While hooked, the vertical question has no effect. "
			 + "Each option ends with what picking it does right now, computed from my current position.\",\"criteria\":{"
			 + skill
			 + "\"Dash\":\"Dash a short burst in the direction the horizontal question chose, with a built-in cooldown; no dash if I am standing still horizontally. "
			 + "Buys an instant gap, or a way through a dangerous area. "
			 + "Costs control: a dash is hard to redirect, and a careless one runs straight into an attack I could have avoided\","
			 + "\"None\":\"No hook, no dash. Buys keeping the cooldowns for the next moment. "
			 + "Costs not using a move faster than running right now\"}}"
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
					if (res.IsSuccessStatusCode) _pending = sw.ElapsedMilliseconds + "" + txt;
					else DiagLog.Write($"[dodge] HTTP {(int)res.StatusCode}");
				}
				catch (System.Exception e) { DiagLog.Write($"[dodge] 请求炸了 {e.GetType().Name} {e.Message}"); }
				finally { _busy = false; }
			});
		}

		// 对照组:按 JevPrior 里这个 boss 的选择比例抽,没记录就均匀抽。间隔取 Jev 实测延迟中位
		const int RandomEveryMs = 300;
		static bool _priorSaid;
		static void RandomPick(Player p, int bossType)
		{
			long now = _clock.ElapsedMilliseconds;
			if (now - _actAt < RandomEveryMs) return;
			var r = Main.rand;
			ThreatsJson(p);
			FleeType = _threats.Count < 2 ? -1 : _threats[r.Next(_threats.Count)].Type;
			int samples = JevPrior.Samples(bossType);
			Hor = (Horiz)JevPrior.Sample(bossType, "horizontal", 3);
			Vt = (Vert)JevPrior.Sample(bossType, "vertical", 6);
			Sk = (Skill)JevPrior.Sample(bossType, "skill", 10);
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
			DiagLog.Write($"[dodge] random -> {Hor}/{Vt}/{Sk}");
		}

		static void Parse(string packed, int bossType)
		{
			int cut = packed.IndexOf('');
			int ms = 0;
			string txt = packed;
			if (cut > 0) { int.TryParse(packed.Substring(0, cut), out ms); txt = packed.Substring(cut + 1); }

			string hq = Seg(txt, "horizontal");
			string hp = Field(hq, "choice");
			string mp = Field(Seg(txt, "vertical"), "choice"), lp = Field(Seg(txt, "skill"), "choice");
			if (hp == null)
			{
				DiagLog.Write($"[dodge] 读不出 choice: {txt.Substring(0, System.Math.Min(160, txt.Length))}");
				return;
			}
			DiagLog.Write($"[dodge] jev {ms}ms -> {hp}/{mp}/{lp}");
			DiagLog.Write($"[dodge] 当时的后果 {_lastConseq}");
			LatencyMs = ms;
			Confidence = Num(hq, "confidence", 0f);
			Hor = hp switch
			{
				"Hold" => Horiz.Hold,
				"Near" => Horiz.Near,
				_ => Horiz.Away,
			};
			// 没答上来就慢慢落、不用技能
			Vt = System.Enum.TryParse<Vert>(mp, out var v) ? v : Vert.Drift;
			Sk = System.Enum.TryParse<Skill>(lp, out var s) ? s : Skill.None;
			_actAt = _clock.ElapsedMilliseconds;
			_answer++;

			string fp = Field(Seg(txt, "flee_from"), "choice");
			FleeType = -1;
			foreach (var t in _threats) if (t.Id == fp) FleeType = t.Type;
			if (fp != null) DiagLog.Write($"[dodge] jev 背对 {fp}");

			Probs = Seg(hq, "probabilities") ?? "";
			TopTwo = Rank(Probs);
			// 意图变了或同一意图持续 4 秒才发到聊天栏
			long now = _clock.ElapsedMilliseconds;
			var said = $"{Hor}/{Vt}/{Sk}";
			if (said != _saidAct || now - _saidAt > 4000)
			{
				string tag = said == _saidAct ? $"  [held {(now - _saidAt) / 1000}s]" : "";
				_saidAct = said; _saidAt = now;
				Main.NewText($"<Jev> {said}{tag}  ({TopTwo})  confidence {Confidence:0.00}  {ms}ms", 90, 230, 120);
			}

			JevPrior.Record(bossType, ("horizontal", (int)Hor, 3), ("vertical", (int)Vt, 6), ("skill", (int)Sk, 10));

			JevLog.Add(new JevLog.Entry
			{
				Ms = _clock.ElapsedMilliseconds,
				Site = "dodge",
				State = _lastFacts,
				Pick = $"{hp}/{mp}/{lp}" + (fp != null ? $" 背对{fp}" : "")
					 + "  →  " + Last,
				Confidence = Confidence,
				Probs = Seg(hq, "probabilities") ?? "",
				Why = "jev",
				LatencyMs = ms,
			});
		}

		// 按问题名取出响应里那一段 {...}
		static string Seg(string s, string question)
		{
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
