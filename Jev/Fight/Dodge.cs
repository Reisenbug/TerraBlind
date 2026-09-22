using System.Net.Http;
using System.Text;
using Terraria;

namespace TerraBlind
{
	// Jev 给【意图】,不给按键。按键由下面那个每帧跑的反射层算
	// 两个正交的轴,九种组合。【旋转不是第三个轴】:绕着 boss 走就是这两个轴的一个组合。
	// 钩爪/冲刺/二段跳/翅膀都不在这里 -- 那些是"怎么做",由代码挑
	public enum Horiz { Away, Hold, Near }
	public enum Vert { Rise, Level, Drop }

	// 旧的九选一。BossBook 的 Banned 还按它写,映射到两个轴上
	public enum DodgeAct { Keep, Back, Close, Evade, Up, Float, Grapple, Orbit, Dive }

	// boss 战的走位。【两层】:Jev 每 200ms 说"该拉开还是该贴脸",反射层每帧算
	// "这一刻往左还是往右、跳不跳"。让 250ms 的判断直接当按键,就是站着挨撞
	public static class Dodge
	{
		public static bool Enabled = false;
		const string Owner = "dodge";
		// 【别把远处的 boss 当不存在】。原来 60 格截断,而肉山退到 60 格外就"消失",
		// 走位层每隔几帧就 Release 一次,没有任何东西再把人拉回来
		const int CareCells = 200;
		// 量墙用另一把尺子。跟着 CareCells 涨的话,扫描量翻三倍,报出去的"还剩多少空间"也变了意思
		const int RoomScanCells = 60;
		// 离地图边界这么近就当撞墙。原版自己拿 maxTilesX-38 当边(Player.cs:20648),取整到 40
		const int EdgeCells = 40;
		// 意图过期就退回保守行为。拿 3 秒前的判断当真比没有判断更糟
		const long IntentTtlMs = 1500;

		// 二段跳:落地才回充,空中再按一次触发,而且【必须松一帧】才算新按压
		// 这次滞空已经跳过空中跳了。只用来分"第一段"和"后续段",能不能跳问 AnyExtraJumpUsable
		static bool _airJumped;
		static bool _jumpHeld;
		static int _holdFrames;
		// 按住的兜底上限。正常到顶就松了,这个数只防"某种状态下一直升不完"
		const int MaxHoldFrames = 30;
		// 钩爪甩出去这么多帧还没勾上就放弃,别一直按着钩子不打人
		const int HookGiveUpFrames = 45;
		static int _hookFrames;
		static bool _hookHeld;
		// 荡完歇一会儿。钩爪的价值是荡出去那段位移,而位移要时间兑现 --
		// 勾上就跳、跳完就勾,人只会在同一格上下震荡,一格都没挪
		const int HookCooldownFrames = 30;
		static int _hookCooldown;
		// 冲刺要"按→松→按"三帧。这一帧是不是该松手,下一帧再按下去触发双击
		static int _dashDir;
		static bool _dashGap;
		// 飘太久就强制落地。羽落 + 按住 up 能悬到天荒地老,而悬着既打不到 boss
		// 也躲不开从上面压下来的东西 -- 判据只有一条:能不能躲开 boss
		const int MaxAirborneFrames = 150;
		static int _airborneFrames;
		static bool _tooLongAirborne;
		// 这一帧要不要靠翅膀爬升。跳到顶之后还按着才会起飞
		static bool _wantFly;
		// 找落点时往上探多少格。不是钩爪的真实射程(那个数我不知道),只是个搜索上界:
		// 猜远了钩子够不着,自己会空手回来,HookGiveUpFrames 收场
		const int HookReachCells = 20;
		// 认准一个方向至少跑这么多帧。掉头要先把速度减到 0,转得勤等于原地踏步

		public static string Last = "idle";
		public static Horiz Hor = Horiz.Away;
		public static Vert Ver = Vert.Level;
		public static float Confidence;
		public static int LatencyMs;
		// 【距离不再写死】。原来 6/18 两个数是我编的,对所有 boss 一视同仁。
		// 现在问 Jev "该离多远",0=贴脸 4=远远躲开,反射层照着走
		public static float Danger = 2f;
		public static bool TacticWorking = true;
		public static bool SafeToAttack = true;
		public static bool JevSaysJump;
		public static bool JevSaysDash;
		public static bool JevSaysHook;
		// 【概率分布才是"这是模型判的"的证据】。一个结论谁都能编,七个选项各占多少编不出来
		public static string Probs = "";
		public static string TopTwo = "";
		// 上一条播报过的意图。没变就不再刷屏
		static DodgeAct _saidAct = (DodgeAct)(-1);
		static long _saidAt;

		public const string Url = "https://api.typesafe.ai/v1/systemone";
		public const string Model = "jev-latest";
		static readonly HttpClient _http = new() { Timeout = System.TimeSpan.FromSeconds(5) };
		static volatile bool _busy;
		static volatile string _pending;
		// 发出去的那份现场,答案回来时一起记进日志 -- 只看结论看不出它为什么这么选
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

		// 【蠕虫三段和骷髅王的手都没有 boss 标志】,只认 npc.boss 的话走位层整场退出
		// 清单只存 Combat.BossPart 一份,各存一份的话下个 boss 只会被加进一边
		static bool IsBossLike(NPC npc) => npc.boss || Combat.BossPart(npc.type);

		// 【返回最近的那一段,不是第一个】。蠕虫几十节,锁到 40 格外的尾巴上
		// 距离和 FramesToHit 就全是错的 -- 要躲的永远是离自己最近的那节
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

		// 全场 boss 里最早撞到我的那一下还有几帧。都不朝我来就 -1
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

		// 四面各有多挤。【几何不该让 Jev 自己做】:逐发报坐标和速度,
		// 它得在脑子里叠加才知道哪边空,而这个数代码顺手就算出来了
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
				// 【认 hostile 不认 !friendly】。两个标志互相独立,可以都为假
				if (pr == null || !pr.active || !pr.hostile || pr.damage <= 0) continue;
				int d = System.Math.Abs((int)(pr.Center.X / 16f) - pcx)
					  + System.Math.Abs((int)(pr.Center.Y / 16f) - pcy);
				if (d > ThreatScan.RangeCells) continue;
				Add(pr.Center, p.Center, Weight(pr.damage, d));
			}
		}

		// 【按伤害算,不只按远近】。原来一发算一份,于是一串 19 伤的激光
		// 压过一只 190 伤的碰撞箱,"远离"就退进喷火里了
		static float Weight(int damage, int cells)
			=> damage / 50f / System.Math.Max(1, cells);

		// 一个威胁同时投给水平和竖直,正上方的东西不该只算"上"不算别的
		static void Add(Microsoft.Xna.Framework.Vector2 at, Microsoft.Xna.Framework.Vector2 me, float w)
		{
			if (at.X > me.X) PressRight += w; else PressLeft += w;
			if (at.Y > me.Y) PressDown += w; else PressUp += w;
		}

		// 被两边夹住时往哪退。【没有空的那一侧了】:两边都有东西,
		// 按压力和挑只会挑中"弹幕少但碰撞疼"的那边,所以直接背对更疼的那个
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
			// 只有一侧有东西就不是夹击,交回给压力和去挑
			if (left == null || right == null) return 0;
			worst = left.damage >= right.damage ? left : right;
			return worst.Center.X > p.Center.X ? -1 : 1;
		}

		static int AwaySide(Player p, NPC boss)
		{
			// 【夹击优先】。被夹住的时候"哪边空"是个伪命题
			int pincer = PincerSide(p, out var worst);
			if (pincer != 0)
			{
				if (worst != _lastPincer) { _lastPincer = worst; Gate($"夹击 背对{worst.TypeName}(伤{worst.damage})"); }
				return pincer;
			}
			_lastPincer = null;
			// 一样重就退回"背对锁定的那个",至少不会站着不动
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
			if (done != null) { _pending = null; Parse(done); }
			string key = Key();
			if (key != null && !_busy)
			{
				_lastFacts = Facts(p, boss, dist);
				Fire(key, _lastFacts);
			}

			// 意图过期:退回"拉开距离 + 保持高度",那是任何时候都不会送命的默认
			bool stale = _clock.ElapsedMilliseconds - _actAt > IntentTtlMs;
			var hor = stale ? Horiz.Away : Hor;
			var ver = stale ? Vert.Level : Ver;
			// 【两个轴各禁各的】。肉山只禁竖直,一起清掉会把该退开的水平也抹平
			// 【禁用退回"不变",不是反向】:禁 Near 的 boss 往往正是"太远也危险"那种
			if (ver == Vert.Rise && BossBook.IsBanned(boss.type, DodgeAct.Up)) ver = Vert.Level;
			if (ver == Vert.Drop && BossBook.IsBanned(boss.type, DodgeAct.Dive)) ver = Vert.Level;
			if (hor == Horiz.Near && BossBook.IsBanned(boss.type, DodgeAct.Close)) hor = Horiz.Hold;
			if (hor == Horiz.Away && BossBook.IsBanned(boss.type, DodgeAct.Back)) hor = Horiz.Hold;

			// Vertical 也要:羽落靠按住 up 才慢降
			if (!AxisLock.Take(Owner, Ax.Move | Ax.Jump | Ax.Vertical, () => Enabled))
			{ Gate("Move axis taken by " + AxisLock.Held(Ax.Move)); return; }

			Gate($"driving {boss.TypeName} {dist}格 act={hor}/{ver}");
			Drive(p, boss, dist, hor, ver);
		}

		// BossBook 的 Banned 还是按旧的九选一写的。两个轴映射回去查一次
		static DodgeAct ToAct(Horiz h, Vert v)
		{
			if (v == Vert.Rise) return DodgeAct.Up;
			if (v == Vert.Drop) return DodgeAct.Dive;
			if (h == Horiz.Away) return DodgeAct.Back;
			if (h == Horiz.Near) return DodgeAct.Close;
			return DodgeAct.Keep;
		}

		// 【拦在门口要出声】。Last 只有 HUD 看得见,日志里一片空白时分不清
		// "没跑"和"跑了没报错" -- 只在状态变化时写,免得每帧刷屏
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

		// 危险分换成格数。0=贴脸输出,4=离远点
		// 0 = 这一场没有目标距离。【别编兜底公式】:编出来的数会当成指令喂回给 Jev
		const int NoWant = 0;

		// 这一场该保持多远,没填就是 NoWant。反射层和报给 Jev 的字段都走这一个入口
		static int Want(NPC boss) => BossBook.WantCellsFor(boss.type);

		// 反射层。【每帧重算方向】-- Jev 说"拉开"的那一刻 boss 在右边,
		// 200ms 后它可能已经绕到左边,照着旧按键跑就是迎头撞上去
		static void Drive(Player p, NPC boss, int dist, Horiz hor, Vert ver)
		{
			float dx = boss.Center.X - p.Center.X;
			bool bossRight = dx > 0;
			// 【"远离"要看全场,不是只看锁定的那个】。躲开一只常常是撞进另一只
			// 每帧重算:Facts 那次是发请求时的,这里要的是此刻的
			Pressure(p);
			int away = AwaySide(p, boss);
			int toward = bossRight ? 1 : -1;
			int go = 0;
			bool onGround = p.velocity.Y == 0f;

			// 【踩到东西就清零,不只是 velocity.Y==0】。斜坡上人一直在微微下滑,那一帧永远等不到
			if (onGround || CellsAboveGround(p) <= 1) { _airborneFrames = 0; _tooLongAirborne = false; _riseHold = 0; }
			// 【飞行不算滞空】。翅膀能飞 3 秒而上限 150 帧,不豁免就永远飞不到耗尽;
			// 判据用 wingTime 不用 _wantFly,否则飞干了计数还冻着,再也落不了地
			else if (!(_wantFly && p.wingTime > 0f) && ++_airborneFrames > MaxAirborneFrames) _tooLongAirborne = true;

			int want = Want(boss);
			// 【每帧自己算全场最早的那一下】。等下一个意图就晚了,而只算锁定那只的话,
			// 另一只冲过来时反射层一无所知
			int framesToHit = SoonestBossHit(p);
			// 多早算"快打中了"。【按反应时间定,不按危险度】:意图 300ms 才回来,
			// 留不出这段时间的预警等于没有预警
			const int soon = 24;
			// 【弹幕也算"快被打中了"】。原来只看 boss 本体,弹幕贴脸反射层一无所知
			int projHit = ThreatScan.SoonestHit(p);
			bool incoming = (framesToHit >= 0 && framesToHit <= soon)
						 || (projHit >= 0 && projHit <= soon);
			// 【预警响没响要能查】。上一局 2125 帧里 incoming 一次没触发,而人被撞死了
			if (incoming && !_wasIncoming)
				DiagLog.Write($"[dodge] 预警 撞击还有{framesToHit}帧 弹幕还有{projHit}帧 阈值{soon}"
					+ $" 最快的是{ThreatScan.SoonestName(p)}");
			_wasIncoming = incoming;

			switch (hor)
			{
				// 【别"够远就停"】。冲撞型 boss 锁的是起冲那一刻的位置,
				// 横向一直有速度才躲得开
				case Horiz.Away:
					go = away;
					break;
				// 【正在挨打就不准靠近】。火焰流站进去每帧掉血,而按距离算那里"够远"(实测吃 78)
				case Horiz.Near:
					if (_hurtRecently > 0) go = away;
					else if (want == NoWant || System.Math.Abs(dx) / 16f > want) go = toward;
					else if (dist < want / 2) go = away;
					break;
				// 【贴太近还是要退】。Hold 是"距离正好",不是"站着不动"
				// 【只对指定了距离的 boss 生效】,别的 boss 没填 WantCells,行为照旧
				case Horiz.Hold:
					if (want != NoWant && dist < want / 2) go = away;
					else if (want != NoWant && dist > want) go = toward;
					break;
			}

			// 【竖直动了就必须横移】。原地上下只是换个高度挨打,
			// 而斜着走才是这次重构要的那个方向
			if (ver != Vert.Level && go == 0 && dist < want) go = away;

			// 钩爪是手段不是意图,时机问 Jev,方向归代码
			// 【Drop 不甩钩】。72% 的帧都是 Drop 且人一直在空中,那条兜底常年成立
			bool wantHook = JevSaysHook
						 || (ver == Vert.Rise && !onGround && !p.AnyExtraJumpUsable() && p.wingTime <= 0f);
			bool hooking = Hook(p, boss, wantHook, ver, onGround, out bool hookJump);

			// 【noJump 要连反射层一起禁】。Banned 只改意图,而 JevSaysJump/incoming 跟意图无关
			bool noJump = BossBook.IsBanned(boss.type, DodgeAct.Up);
			// 【一直打不中就换个高度】。站位不对时 Jev 只看得到"打不出伤害",
			// 看不到自己卡在哪 -- 换一层比站着耗强,3 秒一翻
			if (TooFar)
			{
				var was = ver;
				ver = (_lowSecs / PoseSecs) % 2 == 0 ? Vert.Rise : Vert.Drop;
				if (ver != was) Gate($"打不中{_lowSecs}秒 换姿势 {was}->{ver}");
			}

			// 【顶到了就别再往上顶】。这个数以前只报给 Jev,反射层不看,于是对着方块烧翅膀
			int headroom = CeilingDistance(p);
			if (ver == Vert.Rise && !noJump && headroom > 2) _riseHold = RiseHoldFrames;
			else if (_riseHold > 0) _riseHold--;
			if (headroom <= 1) _riseHold = 0;
			bool rise = _riseHold > 0 && !noJump;
			bool wantJump = hookJump || ((rise || JevSaysJump || incoming) && !_tooLongAirborne && !noJump);
			// 【只有"想往上"才烧翅膀】。incoming 几乎恒真,拿它当飞行条件就是一有威胁就烧光
			_wantFly = rise && !_tooLongAirborne && p.grapCount == 0;
			bool jump = Jump(p, onGround, wantJump, rise && !_tooLongAirborne);
			if (rise && !_wantFly)
				Gate($"Rise 但不飞: 滞空{_airborneFrames}帧 tooLong={_tooLongAirborne}"
					+ $" grap={p.grapCount} wing={p.wingTime:0}");

			// 【堵死了就往空的那侧走】。站定会被顶在墙上当靶子(两次 20%/12% 的大掉血都是 L0+go-)
			// 两侧都堵才停。哪边空是算得出来的,不用猜
			if (go != 0 && WallDistance(p, go) <= 0)
				go = WallDistance(p, -go) > 0 ? -go : 0;

			int want0 = go;
			// 【应急冲刺只认碰撞】。盾冲的免伤只在 NPC 碰撞那一趟里(Player.cs:29501),
			// 对弹幕没有任何无敌 -- 拿弹幕触发是白冲一次还进 30 帧冷却
			bool bossAboutToHit = framesToHit >= 0 && framesToHit <= soon;
			go = Dash(p, go, bossAboutToHit, ver);
			bool dashing = go != want0 || _dashGap;

			if (go < 0) p.controlLeft = true;
			else if (go > 0) p.controlRight = true;
			if (jump) p.controlJump = true;

			// 【down 干两件事】(fallThrough = controlDown):穿平台 + 取消缓降;勾着时不能按
			bool dive = (ver == Vert.Drop || _tooLongAirborne) && p.grapCount == 0;
			// 【incoming 不进缓降】。它几乎恒真,全程按住上就是人飘在高处下不来
			bool hover = !dive && !_wantFly && (rise || hooking);
			if (dive) p.controlDown = true;
			else if (!onGround && hover) p.controlUp = true;

			Fly(p);
			TrackDps();
			Hurt(p, boss, dist, hor, ver);
			Last = $"{hor}/{ver} boss {(bossRight ? "R" : "L")}{dist} (want {want}) go {(go == 0 ? "-" : go < 0 ? "L" : "R")}"
				 + (jump ? (onGround ? " +jump" : " +airjump") : "") + (hooking ? " +hook" : "")
				 + (dashing ? " +dash" : "") + (p.dashDelay < 0 ? " [dashing]" : "")
				 + (incoming ? $" hit in {framesToHit}f" : "")
				 + (TacticWorking ? "" : " [not working]");
		}

		// boss 每秒掉多少血。【"够不够得着打"用实测不用猜距离】--
		// 装备和武器一换,那个距离就变了,而这个数直接说明打没打中
		static int _bossPrevHp = -1;
		static readonly int[] _dpsRing = new int[60];
		static int _dpsAt;
		// 最近 30 秒里每秒的 dps。【要的是分位不是极值】:最高值整场只出现一次,
		// 最低值几乎恒为 0(冲刺、无敌帧),拿它俩当参照等于没有参照
		static readonly int[] _dpsHist = new int[30];
		static int _histAt, _histCount, _secFrames;
		public static int BossDps, DpsGood, DpsTypical, DpsPct = 100;
		// 【按总血量算,不跟着锁定目标】。Boss() 返回最近的那只,双子之间每局切 78 次,
		// 每切一次就清空累积 -- dps 永远是 0,于是它一直以为自己够不着,全程选 Near
		static int TotalBossHp()
		{
			int sum = 0;
			for (int i = 0; i < Main.maxNPCs; i++)
			{
				var n = Main.npc[i];
				if (n != null && n.active && !n.friendly && IsBossLike(n)) sum += n.life;
			}
			return sum;
		}

		static void TrackDps()
		{
			int hp = TotalBossHp();
			if (_bossPrevHp < 0) { _bossPrevHp = hp; return; }
			int d = _bossPrevHp - hp;
			_bossPrevHp = hp;
			// 换目标或者 boss 回血都会算出负数,当 0 -- 那不是我们打的
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
			// 打得好的时候能到多少(p80),平常是多少(中位)。差的那头不取:那是 0
			DpsGood = s[(int)(_histCount * 0.8f) >= _histCount ? _histCount - 1 : (int)(_histCount * 0.8f)];
			DpsTypical = s[_histCount / 2];
			// 【拿常驻当尺子,不拿 0 当尺子】。常驻 300 时 200 照样在打,只是那一秒没满 --
			// 掉到常驻的三分之一以下才是真的够不着
			DpsPct = DpsTypical > 0 ? BossDps * 100 / DpsTypical : 100;
			// 【连续低才算,头几秒不算】。掠过/错开/无敌帧都会让某一秒归零,样本少时中位也是噪音
			if (_histCount >= 5 && DpsTypical > 0 && DpsPct < 35) _lowSecs++; else _lowSecs = 0;
			// 每秒一行。dps 是"该不该靠近"的唯一判据,它坏了整场都会往前凑
			DiagLog.Write($"[dodge] dps {BossDps} 常驻{DpsTypical} {DpsPct}% 低了{_lowSecs}秒");
		}
		static int _lowSecs;
		// 攒够 3 秒才认。【够不着是个持续状态】,不是某一秒的抖动
		public static bool TooFar => _lowSecs >= 3;
		// 打不中的时候每隔这么多秒翻一次高度
		const int PoseSecs = 3;

		// 【每一下掉血都要记】。走位看着不错还是死了,分不清是被撞一下还是被弹幕磨的 --
		// 掉的量和当时的距离一起记下来,一眼就能看出是哪种
		static int _prevHp = -1;
		// 挨打之后这么多帧内不许往它那边走。半秒够走出一道火焰流
		const int HurtCooldown = 30;
		static int _hurtRecently;
		static void Hurt(Player p, NPC boss, int dist, Horiz hor, Vert ver)
		{
			if (_prevHp < 0) { _prevHp = p.statLife; return; }
			int lost = _prevHp - p.statLife;
			_prevHp = p.statLife;
			if (lost <= 0) { if (_hurtRecently > 0) _hurtRecently--; return; }
			// 【挨打就是挨打,不用问】。一道火焰流站进去每帧掉血,而按距离算那里"够远"
			if (lost > 5) _hurtRecently = HurtCooldown;
			// 【名字和伤害都要报】。只有距离的话分不出是哪一发打的,也不知道该躲的是什么
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

		// 【判据是 wingTime 在掉】。"有翅膀+在上升"会把每次起跳都算成飞行,翅膀其实一格没烧
		// 说了往上就至少按这么多帧。够走完 跳→到顶→接着按 那一套
		const int RiseHoldFrames = 30;
		static int _riseHold;
		static bool _wasIncoming;
		static bool _flying;
		static float _prevWing;
		static void Fly(Player p)
		{
			// 【要在爬升,不是在缓降】。羽落也烧 wingTime,把它算成飞行就看不出翅膀有没有真用上
			bool now = p.wingTime < _prevWing - 0.01f && p.velocity.Y < -0.5f;
			_prevWing = p.wingTime;
			if (now == _flying) return;
			_flying = now;
			DiagLog.Write($"[dodge] 飞行{(now ? "开始" : "结束")} wantFly={_wantFly}"
				+ $" wingMax={p.wingTimeMax} wingTime={p.wingTime:0.0} vy={p.velocity.Y:0.0}");
		}

		// 钩爪。【勾住之后一定要跳一次】,否则会被直接拉过去,那就不是位移是送死。
		// 光标是全局的,攻击层每帧在瞄 boss -- 只有发射那一帧抢过来指个方向,之后不用再指
		static bool Hook(Player p, NPC boss, bool want, Vert ver, bool onGround, out bool hookJump)
		{
			hookJump = false;
			if (p.grapCount > 0)
			{
				// 勾住了就跳,顺便进冷却。【不再自己重置空中跳】:能不能跳归 vanilla 管,
				// 清我们这个 bool 只会让它以为还有第一段
				hookJump = true;
				_hookFrames = 0;
				_hookCooldown = HookCooldownFrames;
				return true;
			}
			if (_hookCooldown > 0) { _hookCooldown--; return false; }
			if (!want) { _hookFrames = 0; return false; }
			// 【钩爪也要松一帧】。vanilla 是 if(controlHook){ if(releaseHook) 发射; releaseHook=false; }
			// else releaseHook=true -- 一直按住只发射一次,之后全是空按。和二段跳同一个坑
			if (_hookFrames++ > HookGiveUpFrames) return false;
			if (_hookHeld) { _hookHeld = false; return true; }
			// 【必须瞄到真能勾住的格子】。对着空气甩,钩子飞完全程再空手回来,
			// 这期间人既没位移也没输出 -- 找不到落点就干脆不甩
			if (!FindAnchor(p, boss, ver, out int ax, out int ay)) { _hookFrames = 0; return false; }
			Cursor.AimTile(ax, ay);
			p.controlHook = true;
			_hookHeld = true;
			return true;
		}

		// 【按住到上升结束,不数帧】。按满才跳得最高,而每种跳的满按时长不一样,
		// 硬编码必错。velocity.Y 转正那一刻就是到顶,这个判据对两种跳都成立
		static bool Jump(Player p, bool onGround, bool want, bool wantMore)
		{
			if (onGround) { _airJumped = false; _holdFrames = 0; }

			bool rising = p.velocity.Y < 0f;
			if (_jumpHeld && rising && _holdFrames < MaxHoldFrames)
			{ _holdFrames++; _jumpHeld = true; return true; }

			// 【跳到顶还按着就会起飞】(Player.cs:25618 要 jump==0 && controlJump && wingTime>0)。
			// 想往上而且还有翅膀时就接着按,不松手 -- 松了就只是跳了一下
			if (_jumpHeld && _wantFly && p.wingTimeMax > 0 && p.wingTime > 0f && !onGround)
			{ _holdFrames = 0; return true; }

			// 【按住了就必须先松一帧】。站在地上时 velocity.Y==0,上面那条永不命中,
			// 而 vanilla 要 releaseJump 才认新按压 -- 不松手就是每帧空按,钩爪也取消不掉
			if (_jumpHeld) { _jumpHeld = false; _holdFrames = 0; return false; }

			if (!want) return false;
			if (onGround) { _jumpHeld = true; _holdFrames = 1; return true; }
			// 【挂着钩子那一跳不花空中跳】。vanilla 自己会解钩并刷新跳(Player.cs:20975),
			// 走下面 AnyExtraJumpUsable 那道门会让人永远吊在天花板上
			if (p.grapCount > 0) { _jumpHeld = true; _holdFrames = 1; return true; }
			// 【能不能跳问 vanilla】。原来只记"这次滞空跳过没有",没跳就当有 --
			// 没云朵瓶也照按,白扔一次。AnyExtraJumpUsable 连模组跳一起算,还认 blockExtraJumps
			if (!p.AnyExtraJumpUsable()) return false;
			// 【有多段不等于要烧多段】。第一段照旧,第二段起只认 act==Up:
			// incoming 几乎恒真,拿它放行就是一滞空把储备全烧完
			if (_airJumped && !wantMore) return false;
			// 【空中跳要落日志】。原来只进 HUD 的状态串,事后查不出到底跳没跳
			DiagLog.Write($"[dodge] 空中跳 第{(_airJumped ? 2 : 1)}段 vy={p.velocity.Y:0.0}");
			_airJumped = true; _jumpHeld = true; _holdFrames = 1; return true;
		}

		// boss 朝我飞过来的话,按当前速度还有几帧接触。不朝我来就返回 -1。
		// 【只用确定的量】:位置和速度。它的攻击模式我不知道,不猜
		static int FramesToHit(Player p, NPC boss)
		{
			float gapX = System.Math.Abs(boss.Center.X - p.Center.X) - (boss.width + p.width) * 0.5f;
			float gapY = System.Math.Abs(boss.Center.Y - p.Center.Y) - (boss.height + p.height) * 0.5f;
			float closeX = (boss.Center.X > p.Center.X) == (boss.velocity.X < 0) ? System.Math.Abs(boss.velocity.X) : 0f;
			float closeY = (boss.Center.Y > p.Center.Y) == (boss.velocity.Y < 0) ? System.Math.Abs(boss.velocity.Y) : 0f;
			if (closeX < 0.1f && closeY < 0.1f) return -1;
			// 【一个轴已经重叠就不算那个轴】。平飞冲过来时 closeY 是 0,
			// 老代码给它 9999 再取 Max,于是"马上撞上"被报成"没威胁"-- 实测 2125 帧里一次没响
			float fx = gapX <= 0f ? 0f : (closeX > 0.1f ? gapX / closeX : -1f);
			float fy = gapY <= 0f ? 0f : (closeY > 0.1f ? gapY / closeY : -1f);
			// 两个轴都要在同一时刻重叠才算撞上,所以取晚的那个;但没在靠近的轴
			// 只有已经重叠才算数,否则这一下压根撞不上
			if (fx < 0f || fy < 0f) return -1;
			float f = System.Math.Max(fx, fy);
			return f > 600f ? -1 : (int)f;
		}

		// 克苏鲁之盾的冲刺。【vanilla 要双击】(Player.cs: flag5 = controlLeft && releaseLeft,
		// 15 帧内第二次按下才算),所以必须空出一帧不按方向键,下一帧再按下去
		static int Dash(Player p, int go, bool incoming, Vert ver)
		{
			// 【冲刺中要先于就绪判断】。正在冲的时候 dashDelay<0、dash!=0,
			// 就绪判据必然为假 -- 写在它后面这一行永远执行不到,方向也就保持不住
			if (_dashDir != 0 && p.dashDelay < 0) return _dashDir;

			// dashDelay==0 就是就绪(>0 冷却,<0 正在冲)。【别加 dash==0】:
			// dash 是 dashType 的副本(Player.cs:19764),装了盾恒为 2,那条等于永远不准冲
			bool ready = p.dashType != 0 && p.dashDelay == 0;
			if (!ready)
			{
				if ((JevSaysDash || incoming) && go != 0)
					Gate($"想冲但没就绪 dashType={p.dashType} delay={p.dashDelay}");
				_dashGap = false; _dashDir = 0; return go;
			}

			// 上一帧空了手,这一帧按下去 -- 双击成立。【按住不放】,
			// 冲完直接接着走,不然冲刺结束会有一段没速度的真空
			if (_dashGap) { _dashGap = false; return _dashDir; }
			_dashDir = 0;

			// 【快撞上了就自己冲,不等 Jev】。意图 300ms 才回来,实测预警中位只提前 23 帧
			if ((!JevSaysDash && !incoming) || go == 0) return go;

			DiagLog.Write($"[dodge] 冲刺 {(JevSaysDash ? "jev" : "应急")} 方向{(go < 0 ? "左" : "右")}");
			_dashGap = true; _dashDir = go;
			return 0;   // 这一帧松手
		}

		// 钩子能不能勾这一格。照 vanilla 的 AI_007_GrapplingHooks_CanTileBeLatchedOnTo:
		// 实心或者铁轨(314),而且 tile 得是实打实存在的。铁轨不实心但勾得住
		static bool Hookable(int x, int y)
		{
			if (!Predicates.InBounds(x, y)) return false;
			var t = Main.tile[x, y];
			if (!t.HasTile) return false;
			return Main.tileSolid[t.TileType] || t.TileType == Terraria.ID.TileID.MinecartTrack;
		}

		// 【上下都找,挑离 boss 最远的那个】。原来只扫头顶,人贴着天花板时上面没别的落点了
		// 找不到就让 Hook 放弃,总比对着空气甩强
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
					// 【勾的方向要和意图一致】。钩爪是实现"升/降"的手段,
					// 想上去却勾到脚下,等于把自己按回原处
					if (ver == Vert.Rise && dy >= 0) continue;
					if (ver == Vert.Drop && dy <= 0) continue;
					// 【往下勾要够斜】。太接近正下方的落点,勾上只是把自己钉在原地,
					// 换不来位移。和垂线夹角至少 60 度: |dx| >= dy * tan60 = dy * 1.73
					if (dy > 0 && System.Math.Abs(dx2) * 100 < dy * 173) continue;
					if (!Hookable(x, y)) continue;
					// 【远离 boss 就是好落点】。同方向的落点里挑离它最远的那个
					int score = System.Math.Abs(x - bcx) + System.Math.Abs(y - bcy);
					if (score <= best) continue;
					best = score; ax = x; ay = y;
				}
			return best >= 0;
		}

		// 往这一侧还能跑多远才撞墙。【不能用 ClearWidth】:那个量的是"站得住的连续地面",
		// 悬崖和平台边缘都会截断,而那些地方人照样跑得过去 -- 这里要的是"有东西挡着"
		static int WallDistance(Player p, int dir)
		{
			int cx = (int)(p.Center.X / 16f);
			// 齐胸那一行。贴地扫的话一格台阶就读成墙
			int cy = (int)((p.position.Y + p.height * 0.5f) / 16f);
			for (int d = 1; d <= RoomScanCells; d++)
			{
				int x = cx + dir * d;
				// 【世界边界也是墙】。边界外没有方块,IsWall 一路返回 false,
				// 于是贴着地图边缘反而报"还有 60 格"-- 往那边走是走进死角
				if (x <= EdgeCells || x >= Main.maxTilesX - EdgeCells) return d - 1;
				if (Predicates.IsWall(x, cy)) return d - 1;
			}
			return RoomScanCells;
		}

		// 头顶到天花板几格。【只往上,不往下】:脚下永远有地,往下扫出来的数没有意义
		static int CeilingDistance(Player p)
		{
			int cx = (int)(p.Center.X / 16f);
			int top = (int)(p.position.Y / 16f);
			for (int d = 1; d <= RoomScanCells; d++)
			{
				int y = top - d;
				// 天上飞出地图会被太空的低重力和边界卡住,当成天花板
				if (y <= EdgeCells) return d - 1;
				if (Predicates.IsWall(cx, y)) return d - 1;
			}
			return RoomScanCells;
		}

		// 脚下到最近一块实心的格数。往下探够 MaxAirborneFrames 那点高度就行,
		// 探不到就报这个上限 -- "很高"和"极高"对走位是一回事
		static int CellsAboveGround(Player p)
		{
			int cx = (int)(p.Center.X / 16f);
			int feet = (int)((p.position.Y + p.height) / 16f);
			for (int d = 0; d < 40; d++)
			{
				// 地图底部就是岩浆和虚空,当成地面:再往下没有可去的地方
				if (feet + d >= Main.maxTilesY - EdgeCells) return d;
				// 【平台也是地】。IsSolid 只认 tileSolid,平台是 tileSolidTop --
				// 整个场地都是平台时这里会一路报 40,人明明站着却被当成在半空
				if (Predicates.IsSolid(cx, feet + d) || Predicates.IsPlatform(cx, feet + d)) return d;
			}
			return 40;
		}

		static void Release() => AxisLock.Release(Owner);

		// 格/秒,外加最近一秒的峰值和加速度。【窗口只有一秒】:十秒会跨阶段,
		// 一阶段的速度污染二阶段的参照
		static readonly float[] _spdRing = new float[60];
		static int _spdAt;
		static float _bossRecentTop, _bossAccel, _bossPrevSpd;
		static int BossSpeed(NPC boss)
		{
			float v = (System.Math.Abs(boss.velocity.X) + System.Math.Abs(boss.velocity.Y)) * 60f / 16f;
			_spdRing[_spdAt] = v;
			_spdAt = (_spdAt + 1) % _spdRing.Length;
			float top = 0f;
			foreach (float s in _spdRing) if (s > top) top = s;
			_bossRecentTop = top;
			_bossAccel = v - _bossPrevSpd;
			_bossPrevSpd = v;
			return (int)v;
		}

		static string Facts(Player p, NPC boss, int dist)
		{
			float dx = (boss.Center.X - p.Center.X) / 16f;
			float dy = (boss.Center.Y - p.Center.Y) / 16f;
			bool closing = (dx > 0 && boss.velocity.X < 0) || (dx < 0 && boss.velocity.X > 0);
			int bossSpd = BossSpeed(boss);
			Pressure(p);
			int hit = FramesToHit(p, boss);
			int pcx = (int)(p.Center.X / 16f), pcy = (int)(p.Center.Y / 16f);
			return "{\"hp_percent\":" + (p.statLife * 100 / System.Math.Max(1, p.statLifeMax))
				 + ",\"boss\":\"" + JsonStr(boss.TypeName) + "\""
				 + ",\"boss_hp_percent\":" + (boss.life * 100 / System.Math.Max(1, boss.lifeMax))
				 // 【只报实测量】。"在不在冲刺"要编阈值,而没有冲刺的 boss 那个字段本身就是错的
				 + ",\"boss_speed_cells_per_second\":" + bossSpd
				 + ",\"boss_fastest_in_the_last_second\":" + (int)_bossRecentTop
				 + ",\"boss_acceleration\":" + _bossAccel.ToString("0.0")
				 // 【够不够得着打用实测】。距离多少算够是跟着武器和装备变的,这个数直接说明打没打中
				 + ",\"damage_i_am_dealing_per_second\":" + BossDps
				 + ",\"my_usual_damage_per_second_this_fight\":" + DpsTypical
				 + ",\"my_best_damage_per_second_this_fight\":" + DpsGood
				 // 【百分比才有参照】。光给个 300 它不知道那是高是低
				 + ",\"percent_of_my_usual_damage_right_now\":" + DpsPct
				 + ",\"seconds_i_have_been_unable_to_hit_it\":" + _lowSecs
				 + ",\"i_am_too_far_to_hit_it\":" + (TooFar ? "true" : "false")
				 + ",\"boss_side\":\"" + (dx > 0 ? "右边" : "左边") + "\""
				 + ",\"boss_cells_horizontal\":" + (int)System.Math.Abs(dx)
				 // 只有实测过的 boss 才报目标距离。【编出来的数会被当指令执行】
				 + (Want(boss) != NoWant
					? ",\"distance_i_asked_for\":" + Want(boss)
					  + ",\"cells_further_than_i_asked_for\":" + (dist - Want(boss))
					: "")
				 + ",\"boss_cells_vertical\":" + (int)dy
				 // 【正上方也算"远"】。原来只给一个曼哈顿距离,人悬在 boss 头顶 40 格时
				 // 它读到的是"离得远",于是一直想靠近 -- 而横着走一辈子也下不来
				 + ",\"i_am_above_the_boss_by\":" + (int)(-dy)
				 + ",\"same_height_as_boss\":" + (System.Math.Abs(dy) <= 3 ? "true" : "false")
				 + ",\"boss_coming_at_me\":" + (closing ? "true" : "false")
				 + ",\"frames_until_it_hits_me\":" + (hit < 0 ? "\"它没朝我来\"" : hit.ToString())
				 + ",\"contact_damage_percent_of_my_hp\":" + (boss.damage * 100 / System.Math.Max(1, p.statLife))
				 + ",\"i_am_airborne\":" + (p.velocity.Y != 0f ? "true" : "false")
				 // 【飘了多久、离地多高】。原来只说"在空中",于是它每次都在答
				 // "现在要不要滞空",而不是"要不要继续滞空" -- 悬了三秒也看不出来
				 + ",\"frames_airborne\":" + _airborneFrames
				 + ",\"cells_above_ground\":" + CellsAboveGround(p)
				 // 【离墙还有几格】。arena 那段话只说了"这是个封闭房间",
				 // 而它不知道此刻离墙多近 -- 于是 Back 一路跑到墙根还在按方向键
				 + ",\"cells_of_room_to_my_left\":" + WallDistance(p, -1)
				 + ",\"cells_of_room_to_my_right\":" + WallDistance(p, 1)
				 + ",\"cells_of_room_above_me\":" + CeilingDistance(p)
				 // 【四面的压力】。同一份数反射层也在用来挑往哪边退,两边看的是一个东西。
				 // 数越大越挤,近的威胁权重高(按 1/距离 加)
				 + ",\"how_crowded_each_side_is\":{\"left\":" + PressLeft.ToString("0.00")
				 + ",\"right\":" + PressRight.ToString("0.00")
				 + ",\"above\":" + PressUp.ToString("0.00")
				 + ",\"below\":" + PressDown.ToString("0.00") + "}"
				 // 【退开是有代价的】。武器自动瞄准,所以从它的角度看后退全是好处 --
				 // 不说一声就会一路退到天上去,全场只剩 Away/Rise
				 + ",\"i_am_pinned_against_the_ceiling\":" + (CeilingDistance(p) <= 2 ? "true" : "false")
				 // 【问真值】。原来报的是"我们还没跳过",没云朵瓶时也说 true -- 骗了 Jev
				 + ",\"air_jump_ready\":" + (p.AnyExtraJumpUsable() ? "true" : "false")
				 // 【弹幕也要看见】。只扫 NPC 的话,打得到我的东西有一半不在视野里
				 + ",\"incoming_projectiles\":" + ThreatScan.ProjJson(p, pcx, pcy)
				 // 【逐发列表之外还要给汇总】。十几发各自的 vx/vy 看不出该往哪躲
				 + ",\"projectile_pressure\":" + ThreatScan.PressureJson(p)
				 + ",\"other_enemies\":" + ThreatScan.Json(p, pcx, pcy)
				 + ",\"my_weapon_fires_by_itself\":true"
				 // 【只描述地形,不替 boss 下结论】。"站着不动就会被撞"是克苏鲁之眼的事,
				 // 写在这里等于对每个 boss 都这么说 -- 该由 how_this_boss_fights 去讲
				 + ",\"arena\":\"" + JsonStr(BossBook.ArenaOf(boss.type)) + "\""
				 // 【背板交给它,不写成 if】。这些阈值我一个都不知道,而它读得懂一段话
				 + ",\"how_this_boss_fights\":\"" + JsonStr(BossBook.For(boss.type)) + "\""
				 + ",\"what_i_can_do\":\"" + JsonStr(BossBook.Abilities) + "\""
				 + ",\"grapple_attached\":" + (p.grapCount > 0 ? "true" : "false")
				 + "}";
		}

		// 同一份 state 一次问完。【并行求值不加延迟】,多问几个等于白捡
		static string Body(string state)
			=> "{\"model\":\"" + Model + "\",\"state\":" + Quote(state) + ",\"questions\":{"
			 + "\"horizontal\":{\"type\":\"choice\",\"instructions\":"
			 + "\"泰拉瑞亚 boss 战。这个自动玩家的武器会自己瞄准开火,所以它只要决定走位。"
			 + "【撞到 boss 身上掉的血远比吃一发弹幕多】,躲开碰撞永远排在最前面;"
			 + "但离太远子弹就打不中,所以目标是停在一个够得着打、又不会被撞到的距离上。"
			 + "那个距离没有固定的数,只能从结果看:伤害还在出就是够得着,在挨打就是太近了"
			 + "(有 distance_i_asked_for 这个字段时,它是这一场实测过的距离)。"
			 + "这一题只管【和 boss 的距离该怎么变】,"
			 + "高度另有一题,两题合起来才是完整方向 -- 所以斜着走是这一题和那一题各选一个。"
			 + "【说的是意图不是按键】,往左还是往右由代码按 boss 此刻在哪一侧每帧算。\",\"criteria\":{"
			 + "\"Away\":\"拉开距离。换来的是反应时间:离得越远,冲过来的东西路上花的时间越长,"
			 + "弹幕的轨迹也越早看得出来。代码会按 how_crowded_each_side_is 往空的那侧退。"
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
			 + "\"danger\":{\"type\":\"score\",\"instructions\":"
			 + "\"眼下有多危险,决定它该离 boss 多远。越危险越该拉开。\",\"criteria\":["
			 + "\"很安全,可以贴上去输出\",\"一般,保持中距\",\"有点险,拉开一些\","
			 + "\"很险,离远点\",\"随时会死,能躲多远躲多远\"]},"
			 + "\"should_dash_now\":{\"type\":\"noul\",\"instructions\":"
			 + "\"就这一刻该用克苏鲁之盾冲刺吗?冲刺是朝当前移动方向猛冲一小段,有内置冷却。"
			 + "它能瞬间拉开一段距离、或者穿过一片危险区域;撞到敌人还会免掉那一下伤害。"
			 + "但冲刺中方向不好改,乱冲会一头撞进本来躲得开的攻击里。\"},"
			 + "\"should_grapple_now\":{\"type\":\"noul\",\"instructions\":"
			 + "\"接下来这一秒该甩钩爪吗?钩子勾住后会把人整个拽过去,是一段跑不出来的位移,"
			 + "勾住的瞬间还能跳一下取消、顺带拿到那段速度。往哪勾由代码按当前意图挑落点,"
			 + "这里只判该不该用。它换位置比跑快得多,横着跑来不及躲开追过来的东西时特别有用;"
			 + "但拽过去的路上人没法改方向,而且钩子飞出去到勾上有一段空窗,"
			 + "贴脸的时候甩等于把自己钉在原地挨那一下。\"},"
			 + "\"should_jump_now\":{\"type\":\"noul\",\"instructions\":"
			 + "\"就这一刻该起跳吗?比如有东西贴着地面冲过来,或者弹幕从下方上来。\"},"
			 + "\"safe_to_attack\":{\"type\":\"noul\",\"instructions\":"
			 + "\"现在靠近输出安全吗?\"},"
			 + "\"tactic_working\":{\"type\":\"noul\",\"instructions\":"
			 + "\"现在这套打法有效吗?boss 的血在掉,而自己没有一直挨打。\"}"
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

		static void Parse(string packed)
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
			// 【延迟要落日志】。只进 HUD 的话,事后没法回答"200ms 够不够"
			DiagLog.Write($"[dodge] jev {ms}ms -> {hp}/{vp}");
			LatencyMs = ms;
			Confidence = Num(hq, "confidence", 0f);
			Hor = hp switch
			{
				"Hold" => Horiz.Hold,
				"Near" => Horiz.Near,
				_ => Horiz.Away,
			};
			// 竖直那题没答上来就保持高度。【不猜】:瞎升瞎降都是白白换位置
			Ver = vp switch
			{
				"Rise" => Vert.Rise,
				"Drop" => Vert.Drop,
				_ => Vert.Level,
			};
			_actAt = _clock.ElapsedMilliseconds;

			Probs = Seg(hq, "probabilities") ?? "";
			TopTwo = Rank(Probs);
			// 意图变了才播报。每 200ms 一条会把聊天刷没,那就不是证据是噪音。
			// 【但卡在一个意图上时也要出声】,否则最该看见的那种局面反而一片安静
			long now = _clock.ElapsedMilliseconds;
			var said = ToAct(Hor, Ver);
			if (said != _saidAct || now - _saidAt > 4000)
			{
				string tag = said == _saidAct ? $"  [held {(now - _saidAt) / 1000}s]" : "";
				_saidAct = said; _saidAt = now;
				Main.NewText($"<Jev> {Hor}/{Ver}{tag}  ({TopTwo})  confidence {Confidence:0.00}  {ms}ms", 90, 230, 120);
			}

			// Noul 【没有 confidence】,概率本身就是答案。0.7 当"是"
			Danger = Num(Seg(txt, "danger"), "score", Danger);
			JevSaysJump = Num(Seg(txt, "should_jump_now"), "noul", 0f) > 0.7f;
			float dashN = Num(Seg(txt, "should_dash_now"), "noul", 0f);
			float hookN = Num(Seg(txt, "should_grapple_now"), "noul", 0f);
			JevSaysDash = dashN > 0.7f;
			JevSaysHook = hookN > 0.7f;
			// 【noul 的原值要能看见】。只记"过没过 0.7"的话,常年 0.6 和常年 0.05 长得一样
			DiagLog.Write($"[dodge] noul 冲{dashN:0.00} 勾{hookN:0.00} 跳{Num(Seg(txt, "should_jump_now"), "noul", 0f):0.00}");
			SafeToAttack = Num(Seg(txt, "safe_to_attack"), "noul", 1f) > 0.5f;
			TacticWorking = Num(Seg(txt, "tactic_working"), "noul", 1f) > 0.4f;

			// 【每次回答都记】。原来只在意图变了才记 -- 于是"一直选 Float"在日志里
			// 只有孤零零一条,看不出它卡了多久,也看不出反射层这期间在干什么
			JevLog.Add(new JevLog.Entry
			{
				Ms = _clock.ElapsedMilliseconds,
				Site = "dodge",
				State = _lastFacts,
				Pick = $"{hp}/{vp} 危险{Danger:0.0}" + (JevSaysJump ? " 该跳" : "")
					 + (JevSaysDash ? " 该冲" : "") + (JevSaysHook ? " 该勾" : "")
					 + (SafeToAttack ? "" : " 别贴脸") + (TacticWorking ? "" : " 这套没用")
					 + "  →  " + Last,
				Confidence = Confidence,
				Probs = Seg(hq, "probabilities") ?? "",
				Why = "jev",
				LatencyMs = ms,
			});
		}

		// 【多问题必须按问题名定位】。响应是 {"answers":{"intent":{...},"danger":{...}}},
		// 全文找第一个 "choice" 会把别的问题的答案读进来
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

		// 意图的人话。【聊天栏一律英文】,录像给外面的人看

		// "Float:0.41 Grapple:0.19" -- 从 probabilities 里挑最高的两个
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
