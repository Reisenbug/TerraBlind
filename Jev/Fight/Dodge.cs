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
	// 钩爪和冲刺
	public enum Skill { Grapple, Dash, None }

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
		static bool _dashGap;
		// 这次滞空了多少帧,报给 Jev
		static int _airborneFrames;
		// 找钩爪落点的搜索半径,不是钩爪射程
		const int HookReachCells = 20;

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
			new("\"(\\w+)\":\"[^\"]*?现在选它[就:]?([^\"]*)\"");

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
		static readonly System.Collections.Generic.List<(string Id, int Type, int Damage)> _threats = new();
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
				_threats.Add((id, n.type, n.damage));
				int f = FramesToHit(p, n);
				if (sb.Length > 1) sb.Append(',');
				sb.Append("{\"id\":\"").Append(JsonStr(id)).Append('"')
				  .Append(",\"name\":\"").Append(JsonStr(n.TypeName)).Append('"')
				  .Append(",\"damage\":").Append(n.damage)
				  .Append(",\"cells_to_my_right\":").Append(cx)
				  .Append(",\"cells_above_me\":").Append(cy)
				  .Append(",\"speed_to_the_right\":").Append((int)(n.velocity.X * 60f / 16f))
				  .Append(",\"speed_upward\":").Append((int)(-n.velocity.Y * 60f / 16f))
				  .Append(",\"frames_until_it_hits_me\":").Append(f < 0 ? "\"它没朝我来\"" : f.ToString())
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
				string body = Body(_lastFacts, FleeQuestion(p), HorizCriteria(p, boss), VertCriteria(p));
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

			// 斜坡上 velocity.Y 不为 0,所以也看脚下
			if (onGround || CellsAboveGround(p) <= 1) _airborneFrames = 0;
			else _airborneFrames++;

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

			// 钩爪落点跟着竖直那一题:往上的勾上面,往下的勾下面
			int hookDir = vt is Vert.Rise or Vert.HopUp ? -1 : vt is Vert.DropLayer or Vert.Plunge ? 1 : 0;
			bool hooking = Hook(p, boss, sk == Skill.Grapple, hookDir, onGround, out bool hookJump);
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
		static bool Hook(Player p, NPC boss, bool want, int dir, bool onGround, out bool hookJump)
		{
			hookJump = false;
			if (p.grapCount > 0)
			{
				// 拉到不再靠近落点才跳
				float d = Microsoft.Xna.Framework.Vector2.Distance(p.Center, _anchorPx);
				bool pulling = d < _anchorPrev;
				_anchorPrev = d;
				if (pulling) return true;
				Gate($"钩子拉到位 离落点{d / 16f:0.0}格 跳开");
				hookJump = true;
				_hookFrames = 0;
				_hookCooldown = HookCooldownFrames;
				return true;
			}
			_anchorPrev = float.MaxValue;
			if (_hookCooldown > 0) { _hookCooldown--; return false; }
			if (!want) { _hookFrames = 0; return false; }
			// 按一帧松一帧,vanilla 要 releaseHook 才认新按压
			if (_hookFrames++ > HookGiveUpFrames) return false;
			if (_hookHeld) { _hookHeld = false; return true; }
			// 找不到能勾的格子就不甩
			if (!FindAnchor(p, boss, dir, out int ax, out int ay)) { _hookFrames = 0; return false; }
			Cursor.AimTile(ax, ay);
			_anchorPx = new Microsoft.Xna.Framework.Vector2(ax * 16 + 8, ay * 16 + 8);
			p.controlHook = true;
			_hookHeld = true;
			return true;
		}
		static Microsoft.Xna.Framework.Vector2 _anchorPx;
		static float _anchorPrev = float.MaxValue;

		// 跳键。空中新按一下,有空中跳 vanilla 先用空中跳,没有才是翅膀(Player.cs:25618)
		static bool Jump(Player p, bool onGround, Vert vt, bool hookJump, bool incoming)
		{
			if (onGround) _airJumped = false;
			if (_jumpHeld)
			{
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
				if (want && go != 0)
					Gate($"想冲但没就绪 dashType={p.dashType} delay={p.dashDelay}");
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

		// 找能勾的格子,挑离 boss 最远的。dir -1 只找上面,1 只找下面,0 都行
		static bool FindAnchor(Player p, NPC boss, int dir, out int ax, out int ay)
		{
			ax = ay = 0;
			int pcx = (int)(p.Center.X / 16f), pcy = (int)(p.Center.Y / 16f);
			int bcx = (int)(boss.Center.X / 16f), bcy = (int)(boss.Center.Y / 16f);
			int best = -1;
			for (int dy = -HookReachCells; dy <= HookReachCells; dy++)
				for (int dx2 = -HookReachCells; dx2 <= HookReachCells; dx2++)
				{
					int reach = System.Math.Abs(dx2) + System.Math.Abs(dy);
					if (reach < 4 || reach > HookReachCells) continue;
					int x = pcx + dx2, y = pcy + dy;
					if (dir < 0 && dy >= 0) continue;
					if (dir > 0 && dy <= 0) continue;
					// 往下勾和垂线至少差 60 度
					if (dy > 0 && System.Math.Abs(dx2) * 100 < dy * 173) continue;
					if (!Hookable(x, y)) continue;
					int score = System.Math.Abs(x - bcx) + System.Math.Abs(y - bcy);
					if (score <= best) continue;
					best = score; ax = x; ay = y;
				}
			return best >= 0;
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
			int hit = FramesToHit(p, boss);
			int pcx = (int)(p.Center.X / 16f), pcy = (int)(p.Center.Y / 16f);
			return "{\"hp_percent\":" + (p.statLife * 100 / System.Math.Max(1, p.statLifeMax))
				 + ",\"nearest_part\":\"" + JsonStr(boss.TypeName) + "\""
				 + ",\"nearest_part_hp_percent\":" + (boss.life * 100 / System.Math.Max(1, boss.lifeMax))
				 + ",\"nearest_part_speed_cells_per_second\":" + bossSpd
				 + ",\"nearest_part_fastest_in_the_last_second\":" + (int)_bossRecentTop
				 + ",\"percent_of_my_usual_damage_right_now\":" + DpsPct
				 + ",\"seconds_i_have_been_unable_to_hit_it\":" + _lowSecs
				 + ",\"i_am_too_far_to_hit_it\":" + (TooFar ? "true" : "false")
				 + ",\"nearest_part_cells_horizontal\":" + (int)System.Math.Abs(dx)
				 + ",\"i_am_above_nearest_part_by\":" + (int)(-dy)
				 + ",\"frames_until_nearest_part_hits_me\":" + (hit < 0 ? "\"它没朝我来\"" : hit.ToString())
				 + ",\"nearest_part_contact_damage_percent_of_my_hp\":" + (boss.damage * 100 / System.Math.Max(1, p.statLife))
				 + ",\"frames_airborne\":" + _airborneFrames
				 + ",\"cells_above_ground\":" + CellsAboveGround(p)
				 + ",\"cells_of_room_to_my_left\":" + WallDistance(p, -1)
				 + ",\"cells_of_room_to_my_right\":" + WallDistance(p, 1)
				 + ",\"cells_of_room_above_me\":" + CeilingDistance(p)
				 + ",\"my_speed_to_the_right_cells_per_second\":" + (int)(p.velocity.X * 60f / 16f)
				 + ",\"my_speed_upward_cells_per_second\":" + (int)(-p.velocity.Y * 60f / 16f)
				 + ",\"threats\":" + ThreatsJson(p)
				 + ",\"air_jump_ready\":" + (p.AnyExtraJumpUsable() ? "true" : "false")
				 + ",\"wings_left_percent\":" + (p.wingTimeMax > 0 ? (int)(p.wingTime * 100 / p.wingTimeMax) : 0)
				 + ",\"incoming_projectiles\":" + ThreatScan.ProjJson(p, pcx, pcy)
				 + ",\"projectile_pressure\":" + ThreatScan.PressureJson(p)
				 + ",\"other_enemies\":" + ThreatScan.Json(p, pcx, pcy)
				 + ",\"how_the_bosses_here_fight\":" + FieldBooks(boss)
				 + ",\"what_i_can_do\":\"" + JsonStr(BossBook.Abilities) + "\""
				 + "}";
		}

		// 场上不止一种部件时才问背对谁,选项就是 _threats
		static string FleeQuestion(Player p)
		{
			if (_threats.Count < 2) return "";
			var sb = new StringBuilder("\"flee_from\":{\"type\":\"choice\",\"instructions\":"
				+ "\"水平那一题选 Away 时,代码每帧背对这一题选中的那个跑。此刻最该远离 threats 里的哪一个?"
				+ "碰一下的伤害(damage)越高、离得越近、越快撞上(frames_until_it_hits_me 越小)越该远离;"
				+ "背板里说了要远离谁的,按背板来。背对一个跑会不会正好撞进另一个,也要算进去。\",\"criteria\":{");
			for (int i = 0; i < _threats.Count; i++)
			{
				if (i > 0) sb.Append(',');
				var t = _threats[i];
				var n = Nearest(p, t.Type);
				sb.Append('"').Append(JsonStr(t.Id)).Append("\":\"远离 ")
				  .Append(JsonStr(Terraria.Lang.GetNPCNameValue(t.Type))).Append(",碰一下伤害 ").Append(t.Damage)
				  .Append("。现在选它就").Append(SideText(p, n.Center.X > p.Center.X ? -1 : 1)).Append('"');
			}
			return sb.Append("}},").ToString();
		}

		// 往 dir 那边跑的后果:还剩几格,那边有哪些 boss 部件
		static string SideText(Player p, int dir)
		{
			var sb = new StringBuilder(dir < 0 ? "往左跑,左边" : "往右跑,右边");
			sb.Append("还剩 ").Append(WallDistance(p, dir)).Append(" 格空间;");
			int pcx = (int)(p.Center.X / 16f), pcy = (int)(p.Center.Y / 16f);
			int count = 0;
			foreach (var t in _threats)
			{
				var n = Nearest(p, t.Type);
				int cx = (int)(n.Center.X / 16f) - pcx, cy = pcy - (int)(n.Center.Y / 16f);
				if (cx * dir <= 0) continue;
				sb.Append(count++ == 0 ? "那边有 " : "、").Append(JsonStr(n.TypeName))
				  .Append("(横着 ").Append(System.Math.Abs(cx)).Append(" 格,")
				  .Append(cy >= 0 ? "高我 " : "低我 ").Append(System.Math.Abs(cy)).Append(" 格,碰一下 ").Append(n.damage).Append(')');
			}
			if (count == 0) sb.Append("那边没有 boss 部件");
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
			var sb = new StringBuilder(who == null ? "此刻没有 boss 部件朝我来"
				: $"此刻最快撞到我的是 {JsonStr(who)},还有 {best} 帧");
			int proj = ThreatScan.SoonestHit(p);
			sb.Append(proj < 0 ? ";没有朝我来的弹幕" : $";最快的弹幕 {JsonStr(ThreatScan.SoonestName(p))} 还有 {proj} 帧打到");
			return sb.ToString();
		}

		// 水平那一题的选项,每个后面接现算的后果
		static string HorizCriteria(Player p, NPC boss)
		{
			int toward = boss.Center.X > p.Center.X ? 1 : -1;
			string away = _threats.Count < 2 ? "现在选它就" + SideText(p, -toward)
				: "往左还是往右由 flee_from 那一题定,那一题每个选项都写了往哪边跑、那边还剩几格、那边有什么";
			return "\"Away\":\"拉开距离。换来的是反应时间:离得越远,冲过来的东西路上花的时间越长,"
			 + "弹幕的轨迹也越早看得出来。"
			 + "付出的是输出 -- 退到打不中就是一直不输出,boss 的血不掉这一场不会结束;"
			 + "而且左右都挤的时候根本退不出去,那种局面退是白退。" + away + "\","
			 + "\"Hold\":\"水平不动:左右键都不按,人停在原地,boss 动了距离就跟着变。"
			 + "换来的是不往任何一边撞,输出的位置不变。"
			 + "付出的是水平方向完全没在躲,朝我来的东西只能靠竖直那一题让开。现在选它:" + HoldText(p) + "\","
			 + "\"Near\":\"靠近一点。换来的是打得中 -- 看 percent_of_my_usual_damage_right_now,"
			 + "它是当前输出占这一场常驻水平的百分比,这个数在 100 上下波动都算正常,"
			 + "60% 只是那一秒没打满。单看某一秒低没有意义(boss 掠过、弹道错开都会让它归零),"
			 + "要 i_am_too_far_to_hit_it 为真、或 seconds_i_have_been_unable_to_hit_it 攒到几秒,"
			 + "才说明是位置的问题。付出的是反应时间:越近,冲撞从起手到打到身上的帧数越少,"
			 + "而撞一下掉的血比少打几秒多得多。现在选它就" + SideText(p, toward) + "\"";
		}

		// 同一份 state 的所有问题一次发出
		// 竖直那一题的选项,每个后面接现算的后果
		static string VertCriteria(Player p)
		{
			int feet = (int)((p.position.Y + p.height) / 16f);
			int room = CeilingDistance(p);
			string up = room == 0 ? "头已经顶着实心块" : $"头顶还剩 {room} 格";
			int layer = PlatformBelow(p, feet);
			int next = layer < 0 ? -1 : PlatformBelow(p, layer + 1);
			string drop = layer < 0 ? $"下方到实心块(最多看 {RoomScanCells} 格)之间没有平台,什么都不会发生"
				: (layer == feet ? "穿过脚下这一层" : $"穿过下方 {layer - feet} 格处的那一层")
				  + (next >= 0 ? $",再下一层在它下面 {next - layer} 格" : $",它下面 {RoomScanCells} 格内到实心块之间没有平台了");
			int floor = SolidBelow(p, feet);
			string plunge = floor < 0 ? $"下方 {RoomScanCells} 格内没有实心块"
				: floor == 0 ? "脚下就是实心块,什么都不会发生" : $"一路落 {floor} 格碰到实心块";
			string still = NearestHitVertical(p);
			return "\"Rise\":\"一直往上:按住跳键不放,站着就起跳,跳到顶接着用翅膀往上飞,一直到改选别的。"
			 + "换来的是持续往上:横着扫过来的东西锁的是起冲那一刻的高度,升上去就让开了。"
			 + "付出的是翅膀(wings_left_percent)和头顶的余量(cells_of_room_above_me);"
			 + "cells_of_room_above_me 为 0 时头已经顶着实心块,不会再升高,只会白白烧掉翅膀。"
			 + $"现在选它:{up},翅膀还剩 {(p.wingTimeMax > 0 ? (int)(p.wingTime * 100 / p.wingTimeMax) : 0)}%\","
			 + "\"HopUp\":\"往上一段:站着就起跳,在空中就用一段空中跳,跳到顶就松开。一次回答只跳一段。"
			 + "换来的是一下子往上让开一小段,不烧翅膀。"
			 + "付出的是一段空中跳;air_jump_ready 为 false 时在空中什么都不会发生。"
			 + $"现在选它:{up},{(p.velocity.Y == 0f ? "站着,会起跳" : p.AnyExtraJumpUsable() ? "空中跳还有" : "空中跳已用完,不会发生")}\","
			 + "\"Hover\":\"停在这个高度:在空中按住上,下落速度只有正常的十分之一;站着就是站着。"
			 + "换来的是在空中停得住,不花翅膀。付出的是竖直方向几乎不动,冲过来的东西锁的正是这个高度。"
			 + $"现在选它:{still}\","
			 + "\"Drift\":\"慢慢往下:上下都不按,在空中下落速度是正常的三分之一;站着就是站着。"
			 + "换来的是不花任何东西。付出的是往下很慢,离开一片危险区域要很久。"
			 + $"现在选它:{still}\","
			 + "\"DropLayer\":\"往下一层:按住下,穿过脚下的那一层平台(在空中就是下方最近的那一层),穿过就松开,"
			 + "之后靠羽落慢慢落到再下一层。一次回答穿一层。"
			 + "换来的是离开这一层:压下来的东西、从上方来的弹幕、烧在这一层的火,往下一层就扑空了,"
			 + "cells_of_room_above_me 也会变大。付出的是这个落脚点;下方到实心块之间没有平台时什么都不会发生。"
			 + $"现在选它:{drop}\","
			 + "\"Plunge\":\"一直往下:按住下,取消羽落,按正常速度下落,脚下和途中的平台一路穿过,"
			 + "直到落在实心块上或者改选别的。"
			 + "换来的是最快地往下离开:上方压下来的东西、从上面来的弹幕(projectile_pressure 的 from_above)、"
			 + "头顶没空间的时候都靠它;落地会把空中跳和翅膀充满。"
			 + "付出的是高度,一路穿过的每一层平台都不会停。"
			 + $"现在选它:{plunge}\"";
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
			if (who == null) return "此刻没有 boss 部件朝我来";
			int cy = (int)(p.Center.Y / 16f) - (int)(who.Center.Y / 16f);
			string where = cy > 0 ? $"在我上方 {cy} 格" : cy < 0 ? $"在我下方 {-cy} 格" : "和我同一高度";
			return $"此刻最快撞到我的是 {JsonStr(who.TypeName)},{where},还有 {best} 帧";
		}

		static string Body(string state, string flee, string horiz, string vert)
			=> "{\"model\":\"" + Model + "\",\"state\":" + Quote(state) + ",\"questions\":{" + flee
			 + "\"horizontal\":{\"type\":\"choice\",\"instructions\":"
			 + "\"泰拉瑞亚 boss 战。这个自动玩家的武器会自己瞄准开火,所以它只要决定走位。"
			 + "【撞到 boss 身上掉的血远比吃一发弹幕多】,躲开碰撞永远排在最前面;"
			 + "但离太远子弹就打不中,所以目标是停在一个够得着打、又不会被撞到的距离上。"
			 + "那个距离没有固定的数,只能从结果看:伤害还在出就是够得着,在挨打就是太近了"
			 + "(背板里写了距离的,按背板来)。"
			 + "这一题只管【和离我最近的 boss 部件(nearest_part)的距离该怎么变】,"
			 + "危险不只来自它,threats 里每个部件和每个选项后面写的后果都要看。"
			 + "竖直方向和技能另有两题,合起来才是完整的动作。"
			 + "【说的是意图不是按键】,往左还是往右由代码每帧算。"
			 + "每个选项最后写了现在选它会怎样,那是按这一刻的位置算出来的。\",\"criteria\":{"
			 + horiz + "}},"
			 + "\"vertical\":{\"type\":\"choice\",\"instructions\":"
			 + "\"同一场战斗,这一题只管竖直方向往哪动。和水平那一题、技能那一题各自独立,合起来才是完整的动作。"
			 + "往上和往下一样重要,竖直方向的位置是躲攻击的另一半。每个选项站着和在空中都有效,代码按当时的状态去做。"
			 + "身上有羽落药水。空中跳和翅膀落地才会充满。挂着钩子时这一题不起作用。"
			 + "每个选项最后写了现在选它会怎样,那是按这一刻的位置算出来的。\",\"criteria\":{"
			 + vert + "}},"
			 + "\"skill\":{\"type\":\"choice\",\"instructions\":"
			 + "\"同一场战斗,这一题只管要不要用钩爪或冲刺,和水平、竖直两题各自独立,合起来才是完整的动作。\",\"criteria\":{"
			 + "\"Grapple\":\"甩钩爪:勾住后把人整个拽过去,是一段跑不出来的位移。往哪勾由代码挑,"
			 + "竖直那一题选 Rise 或 HopUp 就勾上面,选 DropLayer 或 Plunge 就勾下面,其余上下都行。"
			 + "换来的是比跑快得多的换位置,横着跑来不及躲开追过来的东西时特别有用。"
			 + "付出的是拽过去的路上没法改方向,钩子飞出去到勾上有一段空窗,拽完有一小段冷却\","
			 + "\"Dash\":\"冲刺:朝水平那一题决定的方向猛冲一小段,有内置冷却。水平方向不动时冲不出去。"
			 + "换来的是一瞬间拉开一段距离,或者穿过一片危险区域。"
			 + "付出的是冲刺中方向不好改,乱冲会一头撞进本来躲得开的攻击里\","
			 + "\"None\":\"不甩钩、不冲刺。换来的是留着冷却给下一刻用。"
			 + "付出的是这一刻没有用上比跑快的位移\"}}"
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
			Sk = (Skill)JevPrior.Sample(bossType, "skill", 3);
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

			JevPrior.Record(bossType, ("horizontal", (int)Hor, 3), ("vertical", (int)Vt, 6), ("skill", (int)Sk, 3));

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
