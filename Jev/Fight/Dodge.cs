using System.Net.Http;
using System.Text;
using Terraria;

namespace TerraBlind
{
	// Jev 给意图，不给按键
	// 钩爪/冲刺/二段跳/飞行，由代码挑
	public enum Horiz { Away, Hold, Near }
	public enum Vert { Rise, Level, Drop }

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
		// 这一帧要不要靠翅膀爬升。跳到顶之后还按着才会起飞
		static bool _wantFly;
		// 找钩爪落点的搜索半径,不是钩爪射程
		const int HookReachCells = 20;

		public static string Last = "idle";
		public static Horiz Hor = Horiz.Away;
		public static Vert Ver = Vert.Level;
		public static float Confidence;
		public static int LatencyMs;
		public static bool JevSaysJump;
		public static bool JevSaysDash;
		public static bool JevSaysHook;
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
		static long _actAt = -100000;
		static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

		static string _key;
		static string Key()
		{
			if (_key != null) return _key.Length == 0 ? null : _key;
			try { _key = System.IO.File.Exists(JevCombat.KeyPath) ? System.IO.File.ReadAllText(JevCombat.KeyPath).Trim() : ""; }
			catch { _key = ""; }
			return _key.Length == 0 ? null : _key;
		}

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

		// 四面各有多“挤”。
		public static float PressLeft, PressRight, PressUp, PressDown;
		static void Pressure(Player p)
		{
			PressLeft = PressRight = PressUp = PressDown = 0f;
			int pcx = (int)(p.Center.X / 16f), pcy = (int)(p.Center.Y / 16f);
			for (int i = 0; i < Main.maxNPCs; i++)
			{
				var npc = Main.npc[i];
				if (npc == null || !npc.active || npc.friendly || !IsBossLike(npc)) continue;
				int d = System.Math.Abs((int)(npc.Center.X / 16f) - pcx)
					  + System.Math.Abs((int)(npc.Center.Y / 16f) - pcy);
				if (d > CareCells) continue;
				Add(npc.Center, p.Center, Weight(npc.damage, d));
			}
			foreach (var pr in Main.projectile)
			{
				// hostile 和 friendly 可以都为假,所以认 hostile
				if (pr == null || !pr.active || !pr.hostile || pr.damage <= 0) continue;
				int d = System.Math.Abs((int)(pr.Center.X / 16f) - pcx)
					  + System.Math.Abs((int)(pr.Center.Y / 16f) - pcy);
				if (d > ThreatScan.RangeCells) continue;
				Add(pr.Center, p.Center, Weight(pr.damage, d));
			}
		}

		// 按伤害算
		static float Weight(int damage, int cells)
			=> damage / 50f / System.Math.Max(1, cells);

		// 一个威胁同时投给水平和竖直
		static void Add(Microsoft.Xna.Framework.Vector2 at, Microsoft.Xna.Framework.Vector2 me, float w)
		{
			if (at.X > me.X) PressRight += w; else PressLeft += w;
			if (at.Y > me.Y) PressDown += w; else PressUp += w;
		}

		// 两侧都有 boss 时背对伤害高的那个
		static int PincerSide(Player p, out NPC worst)
		{
			worst = null;
			NPC left = null, right = null;
			for (int i = 0; i < Main.maxNPCs; i++)
			{
				var npc = Main.npc[i];
				if (npc == null || !npc.active || npc.friendly || !IsBossLike(npc)) continue;
				int d = System.Math.Abs((int)(npc.Center.X / 16f) - (int)(p.Center.X / 16f))
					  + System.Math.Abs((int)(npc.Center.Y / 16f) - (int)(p.Center.Y / 16f));
				if (d > CareCells) continue;
				if (npc.Center.X > p.Center.X)
				{ if (right == null || npc.damage > right.damage) right = npc; }
				else
				{ if (left == null || npc.damage > left.damage) left = npc; }
			}
			if (left == null || right == null) return 0;
			worst = left.damage >= right.damage ? left : right;
			return worst.Center.X > p.Center.X ? -1 : 1;
		}

		// 机甲混战里，要远离的，按顺序:骷髅王的头 = 二阶段魔焰眼 = 毁灭者的头 > 毁灭者的身体。（期待更好的解决方案。）
		static int FleeRank(NPC n)
		{
			if (n.type == Terraria.ID.NPCID.SkeletronPrime) return 0;
			if (n.type == Terraria.ID.NPCID.Spazmatism && n.ai[0] != 0f) return 0;
			if (n.type == Terraria.ID.NPCID.TheDestroyer) return 0;
			if (Combat.DestroyerSegment(n.type)) return 1;
			return -1;
		}

		// Away 时背对谁。
		static NPC MustFlee(Player p)
		{
			NPC best = null, nearest = null;
			int bestRank = int.MaxValue;
			float bestD = float.MaxValue, nearD = float.MaxValue;
			for (int i = 0; i < Main.maxNPCs; i++)
			{
				var n = Main.npc[i];
				if (n == null || !n.active) continue;
				int rank = FleeRank(n);
				if (rank < 0) continue;
				float d = Microsoft.Xna.Framework.Vector2.Distance(n.Center, p.Center);
				if (d < nearD) { nearD = d; nearest = n; }
				if (FramesToHit(p, n) < 0) continue;
				if (rank < bestRank || (rank == bestRank && d < bestD)) { bestRank = rank; bestD = d; best = n; }
			}
			return best ?? nearest;
		}
		static NPC _lastFlee;

		static int AwaySide(Player p, NPC boss)
		{
			// 顺序:要远离的 > 夹击 > 压力
			var flee = MustFlee(p);
			if (flee != _lastFlee) { _lastFlee = flee; if (flee != null) Gate($"退的时候背对 {flee.TypeName}"); }
			if (flee != null) return flee.Center.X > p.Center.X ? -1 : 1;
			int pincer = PincerSide(p, out var worst);
			if (pincer != 0)
			{
				if (worst != _lastPincer) { _lastPincer = worst; Gate($"夹击 背对{worst.TypeName}(伤{worst.damage})"); }
				return pincer;
			}
			_lastPincer = null;
			// 两侧一样挤就背对锁定的那个
			if (System.Math.Abs(PressLeft - PressRight) < 0.0001f) return boss.Center.X > p.Center.X ? -1 : 1;
			return PressRight > PressLeft ? -1 : 1;
		}
		static NPC _lastPincer;

		public static void Tick()
		{
			if (!Enabled) return;
			var p = Main.LocalPlayer;
			if (p == null || !p.active || p.dead) { Release(); return; }

			var boss = Boss(p, out int dist);
			if (boss == null) { Gate("no boss"); Release(); return; }

			var done = _pending;
			if (done != null) { _pending = null; Parse(done, boss.type); }
			string key = Key();
			if (RandomBrain) RandomPick(boss.type);
			else if (key != null && !_busy)
			{
				_lastFacts = Facts(p, boss, dist);
				Fire(key, _lastFacts);
			}

			// 意图过期时用 Away/Level
			bool stale = _clock.ElapsedMilliseconds - _actAt > IntentTtlMs;
			var hor = stale ? Horiz.Away : Hor;
			var ver = stale ? Vert.Level : Ver;

			// 羽落要按住 up,所以也拿 Vertical
			if (!AxisLock.Take(Owner, Ax.Move | Ax.Jump | Ax.Vertical, () => Enabled))
			{ Gate("Move axis taken by " + AxisLock.Held(Ax.Move)); return; }

			Gate($"driving {boss.TypeName} {dist}格 act={hor}/{ver}");
			Drive(p, boss, dist, hor, ver);
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
		static void Drive(Player p, NPC boss, int dist, Horiz hor, Vert ver)
		{
			float dx = boss.Center.X - p.Center.X;
			bool bossRight = dx > 0;
			Pressure(p);
			int away = AwaySide(p, boss);
			int toward = bossRight ? 1 : -1;
			int go = 0;
			bool onGround = p.velocity.Y == 0f;

			// 斜坡上 velocity.Y 不为 0,所以也看脚下
			if (onGround || CellsAboveGround(p) <= 1) { _airborneFrames = 0; _riseHold = 0; }
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

			bool hooking = Hook(p, boss, JevSaysHook, ver, onGround, out bool hookJump);

			// 打不中时每 PoseSecs 秒在 Rise/Drop 之间翻
			if (TooFar)
			{
				var was = ver;
				ver = (_lowSecs / PoseSecs) % 2 == 0 ? Vert.Rise : Vert.Drop;
				if (ver != was) Gate($"打不中{_lowSecs}秒 换姿势 {was}->{ver}");
			}

			// 头顶没空间就不再往上
			int headroom = CeilingDistance(p);
			if (ver == Vert.Rise && headroom > 2) _riseHold = RiseHoldFrames;
			else if (_riseHold > 0) _riseHold--;
			if (headroom <= 1) _riseHold = 0;
			bool rise = _riseHold > 0;
			// 挂着钩子时只有 Hook 能喊跳,跳会解钩
			bool wantJump = hookJump || (p.grapCount == 0 && (rise || JevSaysJump || incoming));
			// 只有 Rise 才用翅膀
			_wantFly = rise && p.grapCount == 0;
			bool jump = Jump(p, onGround, wantJump, rise);

			// 撞墙就掉头,两侧都堵才停
			if (go != 0 && WallDistance(p, go) <= 0)
				go = WallDistance(p, -go) > 0 ? -go : 0;

			int want0 = go;
			go = Dash(p, go, ver);
			bool dashing = go != want0 || _dashGap;

			if (go < 0) p.controlLeft = true;
			else if (go > 0) p.controlRight = true;
			if (jump) p.controlJump = true;

			// down 会穿平台并取消缓降,勾着时不按
			bool dive = ver == Vert.Drop && p.grapCount == 0;
			// 缓降只在 Rise 或挂钩时
			bool hover = !dive && !_wantFly && (rise || hooking);
			if (dive) p.controlDown = true;
			else if (!onGround && hover) p.controlUp = true;

			Fly(p);
			TrackDps();
			Hurt(p, boss, dist, hor, ver);
			Last = $"{hor}/{ver} boss {(bossRight ? "R" : "L")}{dist} go {(go == 0 ? "-" : go < 0 ? "L" : "R")}"
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
			DiagLog.Write($"[dodge] dps {BossDps} 常驻{DpsTypical} {DpsPct}% 低了{_lowSecs}秒");
		}
		static int _lowSecs;
		public static bool TooFar => _lowSecs >= 3;
		const int PoseSecs = 3;

		// 每次掉血记一行,带最近的 NPC 和弹幕
		static int _prevHp = -1;
		static void Hurt(Player p, NPC boss, int dist, Horiz hor, Vert ver)
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
				+ $" 离{boss.TypeName} {dist}格 意图{hor}/{ver}"
				+ $" | 最近NPC {nn} {nd}格 | 最近弹幕 {pn} {pd}格");
		}

		// Rise 之后至少往上按这么多帧
		const int RiseHoldFrames = 30;
		static int _riseHold;
		static bool _wasIncoming;
		static bool _flying;
		static float _prevWing;
		// 飞行开始和结束各记一行。wingTime 在掉而且在上升才算飞,缓降也烧 wingTime
		static void Fly(Player p)
		{
			bool now = p.wingTime < _prevWing - 0.01f && p.velocity.Y < -0.5f;
			_prevWing = p.wingTime;
			if (now == _flying) return;
			_flying = now;
			DiagLog.Write($"[dodge] 飞行{(now ? "开始" : "结束")} wantFly={_wantFly}"
				+ $" wingMax={p.wingTimeMax} wingTime={p.wingTime:0.0} vy={p.velocity.Y:0.0}");
		}

		// 钩爪。只在发射那一帧占用光标
		static bool Hook(Player p, NPC boss, bool want, Vert ver, bool onGround, out bool hookJump)
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
			if (!FindAnchor(p, boss, ver, out int ax, out int ay)) { _hookFrames = 0; return false; }
			Cursor.AimTile(ax, ay);
			_anchorPx = new Microsoft.Xna.Framework.Vector2(ax * 16 + 8, ay * 16 + 8);
			p.controlHook = true;
			_hookHeld = true;
			return true;
		}
		static Microsoft.Xna.Framework.Vector2 _anchorPx;
		static float _anchorPrev = float.MaxValue;

		// 跳。按住直到开始下落,Rise 且有翅膀时接着按住起飞
		static bool Jump(Player p, bool onGround, bool want, bool wantMore)
		{
			if (onGround) { _airJumped = false; _holdFrames = 0; }

			bool rising = p.velocity.Y < 0f;
			if (_jumpHeld && rising && _holdFrames < MaxHoldFrames)
			{ _holdFrames++; _jumpHeld = true; return true; }

			// 起飞条件见 Player.cs:25618
			if (_jumpHeld && _wantFly && p.wingTimeMax > 0 && p.wingTime > 0f && !onGround)
			{ _holdFrames = 0; return true; }

			// 松一帧,vanilla 要 releaseJump 才认新按压
			if (_jumpHeld) { _jumpHeld = false; _holdFrames = 0; return false; }

			if (!want) return false;
			if (onGround) { _jumpHeld = true; _holdFrames = 1; return true; }
			// 挂钩时的跳是解钩跳,不花空中跳(Player.cs:20975)
			if (p.grapCount > 0) { _jumpHeld = true; _holdFrames = 1; return true; }
			if (!p.AnyExtraJumpUsable()) return false;
			// 第二段起的空中跳只在 Rise 时用
			if (_airJumped && !wantMore) return false;
			DiagLog.Write($"[dodge] 空中跳 第{(_airJumped ? 2 : 1)}段 vy={p.velocity.Y:0.0}");
			_airJumped = true; _jumpHeld = true; _holdFrames = 1; return true;
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
		static int Dash(Player p, int go, Vert ver)
		{
			// 冲刺中保持方向
			if (_dashDir != 0 && p.dashDelay < 0) return _dashDir;

			// dashDelay 大于 0 冷却,小于 0 正在冲
			bool ready = p.dashType != 0 && p.dashDelay == 0;
			if (!ready)
			{
				if (JevSaysDash && go != 0)
					Gate($"想冲但没就绪 dashType={p.dashType} delay={p.dashDelay}");
				_dashGap = false; _dashDir = 0; return go;
			}

			if (_dashGap) { _dashGap = false; return _dashDir; }
			_dashDir = 0;

			if (!JevSaysDash || go == 0) return go;

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

		// 顺着竖直意图找能勾的格子,挑离 boss 最远的
		static bool FindAnchor(Player p, NPC boss, Vert ver, out int ax, out int ay)
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
					if (ver == Vert.Rise && dy >= 0) continue;
					if (ver == Vert.Drop && dy <= 0) continue;
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
			Pressure(p);
			int hit = FramesToHit(p, boss);
			int pcx = (int)(p.Center.X / 16f), pcy = (int)(p.Center.Y / 16f);
			return "{\"hp_percent\":" + (p.statLife * 100 / System.Math.Max(1, p.statLifeMax))
				 + ",\"boss\":\"" + JsonStr(boss.TypeName) + "\""
				 + ",\"boss_hp_percent\":" + (boss.life * 100 / System.Math.Max(1, boss.lifeMax))
				 + ",\"boss_speed_cells_per_second\":" + bossSpd
				 + ",\"boss_fastest_in_the_last_second\":" + (int)_bossRecentTop
				 + ",\"percent_of_my_usual_damage_right_now\":" + DpsPct
				 + ",\"seconds_i_have_been_unable_to_hit_it\":" + _lowSecs
				 + ",\"i_am_too_far_to_hit_it\":" + (TooFar ? "true" : "false")
				 + ",\"boss_cells_horizontal\":" + (int)System.Math.Abs(dx)
				 + ",\"i_am_above_the_boss_by\":" + (int)(-dy)
				 + ",\"frames_until_it_hits_me\":" + (hit < 0 ? "\"它没朝我来\"" : hit.ToString())
				 + ",\"contact_damage_percent_of_my_hp\":" + (boss.damage * 100 / System.Math.Max(1, p.statLife))
				 + ",\"frames_airborne\":" + _airborneFrames
				 + ",\"cells_above_ground\":" + CellsAboveGround(p)
				 + ",\"cells_of_room_to_my_left\":" + WallDistance(p, -1)
				 + ",\"cells_of_room_to_my_right\":" + WallDistance(p, 1)
				 + ",\"cells_of_room_above_me\":" + CeilingDistance(p)
				 + ",\"how_crowded_each_side_is\":{\"left\":" + PressLeft.ToString("0.00")
				 + ",\"right\":" + PressRight.ToString("0.00")
				 + ",\"above\":" + PressUp.ToString("0.00")
				 + ",\"below\":" + PressDown.ToString("0.00") + "}"
				 + ",\"air_jump_ready\":" + (p.AnyExtraJumpUsable() ? "true" : "false")
				 + ",\"incoming_projectiles\":" + ThreatScan.ProjJson(p, pcx, pcy)
				 + ",\"projectile_pressure\":" + ThreatScan.PressureJson(p)
				 + ",\"other_enemies\":" + ThreatScan.Json(p, pcx, pcy)
				 + ",\"how_the_bosses_here_fight\":" + FieldBooks(boss)
				 + ",\"what_i_can_do\":\"" + JsonStr(BossBook.Abilities) + "\""
				 + "}";
		}

		// 同一份 state 的所有问题一次发出
		static string Body(string state)
			=> "{\"model\":\"" + Model + "\",\"state\":" + Quote(state) + ",\"questions\":{"
			 + "\"horizontal\":{\"type\":\"choice\",\"instructions\":"
			 + "\"泰拉瑞亚 boss 战。这个自动玩家的武器会自己瞄准开火,所以它只要决定走位。"
			 + "【撞到 boss 身上掉的血远比吃一发弹幕多】,躲开碰撞永远排在最前面;"
			 + "但离太远子弹就打不中,所以目标是停在一个够得着打、又不会被撞到的距离上。"
			 + "那个距离没有固定的数,只能从结果看:伤害还在出就是够得着,在挨打就是太近了"
			 + "(背板里写了距离的,按背板来)。"
			 + "这一题只管【和 boss 的距离该怎么变】,"
			 + "高度另有一题,两题合起来才是完整方向 -- 所以斜着走是这一题和那一题各选一个。"
			 + "【说的是意图不是按键】,往左还是往右由代码每帧算。\",\"criteria\":{"
			 + "\"Away\":\"拉开距离。换来的是反应时间:离得越远,冲过来的东西路上花的时间越长,"
			 + "弹幕的轨迹也越早看得出来。"
			 + "付出的是输出 -- 退到打不中就是一直不输出,boss 的血不掉这一场不会结束;"
			 + "而且左右都挤的时候根本退不出去,那种局面退是白退\","
			 + "\"Hold\":\"距离不变。换来的是稳定的输出和一个已知安全的位置 --"
			 + "此刻既够得着打又没被逼近,说明这个距离是对的。"
			 + "付出的是这一刻没有在改善处境:boss 在动,同一个距离下一秒可能就不安全了\","
			 + "\"Near\":\"靠近一点。换来的是打得中 -- 看 percent_of_my_usual_damage_right_now,"
			 + "它是当前输出占这一场常驻水平的百分比,这个数在 100 上下波动都算正常,"
			 + "60% 只是那一秒没打满。单看某一秒低没有意义(boss 掠过、弹道错开都会让它归零),"
			 + "要 i_am_too_far_to_hit_it 为真、或 seconds_i_have_been_unable_to_hit_it 攒到几秒,"
			 + "才说明是位置的问题。付出的是反应时间:越近,冲撞从起手到打到身上的帧数越少,"
			 + "而撞一下掉的血比少打几秒多得多\"}},"
			 + "\"vertical\":{\"type\":\"choice\",\"instructions\":"
			 + "\"同一场战斗,这一题只管【高度该怎么变】。怎么上去(跳、二段跳、翅膀、钩爪)由代码挑,"
			 + "这里只说要不要上去。和水平那一题是独立的两个轴:两边都选'变'就是斜着走。\",\"criteria\":{"
			 + "\"Rise\":\"往上。换掉的是横向攻击瞄准的那条线:冲撞和贴地扫过来的东西"
			 + "锁的是起冲那一刻的高度,升一层就从它的路线上让开了。"
			 + "付出的是头顶的余量,cells_of_room_above_me 每升一次就少一截;"
			 + "这个数归零之后竖直方向再无处可去,那时候来一下只能硬吃。"
			 + "上方本来就有东西时(how_crowded_each_side_is 的 above 大),往上是迎上去\","
			 + "\"Level\":\"保持现在的高度。换来的是手里的余地:"
			 + "在半空时翅膀和空中跳都还没用,上下两个方向随时能走;"
			 + "在地面上时跑动最快最稳。付出的是这一刻没有在躲 --"
			 + "如果已经有东西朝自己来了,不动就是站在原地等它到\","
			 + "\"Drop\":\"往下。换掉的是头顶那片区域:压下来的东西、从上方来的弹幕"
			 + "(看 projectile_pressure 的 from_above)掉一层就扑空了,"
			 + "而且 cells_of_room_above_me 会变大,竖直方向重新有空间。"
			 + "付出的是下落途中翅膀和空中跳都在消耗或已经用掉,"
			 + "真正落到地面才重新充满 -- 在那之前竖直方向是空的,只剩左右能躲;"
			 + "frames_airborne 很大而局面没变好时,落地重来反而是划算的\"}},"
			 + "\"should_dash_now\":{\"type\":\"noul\",\"instructions\":"
			 + "\"就这一刻该冲刺吗?冲刺是朝当前移动方向猛冲一小段,有内置冷却。"
			 + "它能瞬间拉开一段距离、或者穿过一片危险区域。"
			 + "但冲刺中方向不好改,乱冲会一头撞进本来躲得开的攻击里。\"},"
			 + "\"should_grapple_now\":{\"type\":\"noul\",\"instructions\":"
			 + "\"接下来这一秒该甩钩爪吗?钩子勾住后会把人整个拽过去,是一段跑不出来的位移。"
			 + "往哪勾、什么时候松由代码管,"
			 + "这里只判该不该用。它换位置比跑快得多,横着跑来不及躲开追过来的东西时特别有用;"
			 + "但拽过去的路上人没法改方向,而且钩子飞出去到勾上有一段空窗,"
			 + "贴脸的时候甩等于把自己钉在原地挨那一下。\"},"
			 + "\"should_jump_now\":{\"type\":\"noul\",\"instructions\":"
			 + "\"就这一刻该起跳吗?比如有东西贴着地面冲过来,或者弹幕从下方上来。\"}"
			 + "}}";

		static void Fire(string key, string state)
		{
			_busy = true;
			var sw = System.Diagnostics.Stopwatch.StartNew();
			System.Threading.Tasks.Task.Run(async () =>
			{
				try
				{
					using var req = new HttpRequestMessage(HttpMethod.Post, Url);
					req.Headers.Add("Authorization", "Bearer " + key);
					req.Content = new StringContent(Body(state), Encoding.UTF8, "application/json");
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
		static void RandomPick(int bossType)
		{
			long now = _clock.ElapsedMilliseconds;
			if (now - _actAt < RandomEveryMs) return;
			var r = Main.rand;
			float jumpN, dashN, hookN;
			bool fromJev = JevPrior.Sample(bossType, out var h, out var v, out dashN, out hookN, out jumpN, out int samples);
			if (fromJev) { Hor = h; Ver = v; }
			else
			{
				Hor = (Horiz)r.Next(3);
				Ver = (Vert)r.Next(3);
				jumpN = r.NextFloat(); dashN = r.NextFloat(); hookN = r.NextFloat();
			}
			if (!_priorSaid || !fromJev)
			{
				_priorSaid = fromJev;
				Gate(fromJev ? $"随机组按 Jev 对这个 boss 的 {samples} 次选择抽" : "随机组:这个 boss 没有 Jev 记录,均匀抽");
			}
			JevSaysJump = jumpN > 0.7f;
			JevSaysDash = dashN > 0.5f;
			JevSaysHook = hookN > 0.7f;
			Confidence = 0f;
			LatencyMs = 0;
			_actAt = now;
			DiagLog.Write($"[dodge] random -> {Hor}/{Ver}");
			DiagLog.Write($"[dodge] noul 冲{dashN:0.00} 勾{hookN:0.00} 跳{jumpN:0.00}");
		}

		static void Parse(string packed, int bossType)
		{
			int cut = packed.IndexOf('');
			int ms = 0;
			string txt = packed;
			if (cut > 0) { int.TryParse(packed.Substring(0, cut), out ms); txt = packed.Substring(cut + 1); }

			string hq = Seg(txt, "horizontal"), vq = Seg(txt, "vertical");
			string hp = Field(hq, "choice"), vp = Field(vq, "choice");
			if (hp == null)
			{
				DiagLog.Write($"[dodge] 读不出 choice: {txt.Substring(0, System.Math.Min(160, txt.Length))}");
				return;
			}
			DiagLog.Write($"[dodge] jev {ms}ms -> {hp}/{vp}");
			LatencyMs = ms;
			Confidence = Num(hq, "confidence", 0f);
			Hor = hp switch
			{
				"Hold" => Horiz.Hold,
				"Near" => Horiz.Near,
				_ => Horiz.Away,
			};
			// 竖直那题没答上来就 Level
			Ver = vp switch
			{
				"Rise" => Vert.Rise,
				"Drop" => Vert.Drop,
				_ => Vert.Level,
			};
			_actAt = _clock.ElapsedMilliseconds;

			Probs = Seg(hq, "probabilities") ?? "";
			TopTwo = Rank(Probs);
			// 意图变了或同一意图持续 4 秒才发到聊天栏
			long now = _clock.ElapsedMilliseconds;
			var said = $"{Hor}/{Ver}";
			if (said != _saidAct || now - _saidAt > 4000)
			{
				string tag = said == _saidAct ? $"  [held {(now - _saidAt) / 1000}s]" : "";
				_saidAct = said; _saidAt = now;
				Main.NewText($"<Jev> {Hor}/{Ver}{tag}  ({TopTwo})  confidence {Confidence:0.00}  {ms}ms", 90, 230, 120);
			}

			float jumpN = Num(Seg(txt, "should_jump_now"), "noul", 0f);
			float dashN = Num(Seg(txt, "should_dash_now"), "noul", 0f);
			float hookN = Num(Seg(txt, "should_grapple_now"), "noul", 0f);
			JevSaysJump = jumpN > 0.7f;
			JevSaysDash = dashN > 0.5f;
			JevSaysHook = hookN > 0.7f;
			DiagLog.Write($"[dodge] noul 冲{dashN:0.00} 勾{hookN:0.00} 跳{jumpN:0.00}");
			JevPrior.Record(bossType, Hor, Ver, dashN, hookN, jumpN);

			JevLog.Add(new JevLog.Entry
			{
				Ms = _clock.ElapsedMilliseconds,
				Site = "dodge",
				State = _lastFacts,
				Pick = $"{hp}/{vp}" + (JevSaysJump ? " 该跳" : "")
					 + (JevSaysDash ? " 该冲" : "") + (JevSaysHook ? " 该勾" : "")
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
