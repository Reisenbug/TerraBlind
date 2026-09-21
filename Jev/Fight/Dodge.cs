using System.Net.Http;
using System.Text;
using Terraria;

namespace TerraBlind
{
	// Jev 给【意图】,不给按键。按键由下面那个每帧跑的反射层算
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
		public static DodgeAct Act = DodgeAct.Back;
		public static float Confidence;
		public static int LatencyMs;
		// 【距离不再写死】。原来 6/18 两个数是我编的,对所有 boss 一视同仁。
		// 现在问 Jev "该离多远",0=贴脸 4=远远躲开,反射层照着走
		public static float Danger = 2f;
		public static bool TacticWorking = true;
		public static bool SafeToAttack = true;
		public static bool JevSaysJump;
		public static bool JevSaysDash;
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

			// 意图过期:退回"拉开距离",那是任何时候都不会送命的默认
			var act = _clock.ElapsedMilliseconds - _actAt > IntentTtlMs ? DodgeAct.Back : Act;
			// 【禁用的意图退回 Keep,不是 Back】。禁 Close 的 boss 往往正是"太远也危险"那种,
			// 自动后退等于换个方向送
			if (BossBook.IsBanned(boss.type, act)) act = DodgeAct.Keep;

			// Vertical 也要:羽落靠按住 up 才慢降
			if (!AxisLock.Take(Owner, Ax.Move | Ax.Jump | Ax.Vertical, () => Enabled))
			{ Gate("Move axis taken by " + AxisLock.Held(Ax.Move)); return; }

			Gate($"driving {boss.TypeName} {dist}格 act={act}");
			Drive(p, boss, dist, act);
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
		static int WantCells(float danger) => 4 + (int)(danger * 4f);

		// 这一场该保持多远。boss 指定了就用它的,否则按 danger 算 -- 反射层和
		// 报给 Jev 的字段都走这一个入口,免得两边各算各的
		static int Want(NPC boss)
		{
			int fixedWant = BossBook.WantCellsFor(boss.type);
			return fixedWant > 0 ? fixedWant : WantCells(Danger);
		}

		// 反射层。【每帧重算方向】-- Jev 说"拉开"的那一刻 boss 在右边,
		// 200ms 后它可能已经绕到左边,照着旧按键跑就是迎头撞上去
		static void Drive(Player p, NPC boss, int dist, DodgeAct act)
		{
			float dx = boss.Center.X - p.Center.X;
			bool bossRight = dx > 0;
			int away = bossRight ? -1 : 1;
			int toward = -away;
			int go = 0;
			bool onGround = p.velocity.Y == 0f;

			if (onGround) { _airborneFrames = 0; _tooLongAirborne = false; }
			// 【飞行不算滞空】。翅膀能飞 3 秒而上限 150 帧,不豁免就永远飞不到耗尽;
			// 判据用 wingTime 不用 _wantFly,否则飞干了计数还冻着,再也落不了地
			else if (!(_wantFly && p.wingTime > 0f) && ++_airborneFrames > MaxAirborneFrames) _tooLongAirborne = true;

			int fixedWant = BossBook.WantCellsFor(boss.type);
			int want = fixedWant > 0 ? fixedWant : WantCells(Danger);
			// 【撞上还有几帧】。躲晚不是因为判断慢,是因为收到意图那一刻才跳一次 --
			// 该跳的时机在那之后。所以每帧自己算,不等下一个意图
			int framesToHit = FramesToHit(p, boss);
			// 危险时提前跳,安全时晚点跳。原来这也是个写死的 12
			int soon = 8 + (int)(Danger * 4f);
			// 【弹幕也算"快被打中了"】。原来只看 boss 本体,弹幕贴脸反射层一无所知
			int projHit = ThreatScan.SoonestHit(p);
			bool incoming = (framesToHit >= 0 && framesToHit <= soon)
						 || (projHit >= 0 && projHit <= soon);

			switch (act)
			{
				// 【别"够远就停"】。克苏鲁之眼是冲撞型,站定就是等撞 --
				// 它锁的是冲刺开始那一刻的位置,横向一直有速度才躲得开
				case DodgeAct.Back:
					go = away;
					break;
				// 【按横向差判】。dist 是 |dx|+|dy|,悬在头顶 40 格也算"远",横着走收不掉高低差
				// 【收到 want 就停,不是 want/2】。砍一半等于主动贴到脸上
				case DodgeAct.Close:
					if (System.Math.Abs(dx) / 16f > want) go = toward;
					break;
				case DodgeAct.Evade:
					go = away;
					break;
				// 【飘着也要横移】。羽落是边飘边躲,不是站桩 -- 悬在半空不动就是靶子
				case DodgeAct.Up:
				case DodgeAct.Float:
					if (dist < want) go = away;
					break;
				case DodgeAct.Grapple:
					go = away;
					break;
				// 【绕着走,不是退开】。场地封闭时退只能退到墙上,垂直于连线才躲得开
				// 【真正的切向】。原来 go = p.direction 跟 boss 在哪无关,是沿惯性直走
				case DodgeAct.Orbit:
					go = boss.Center.Y < p.Center.Y ? (dx > 0 ? 1 : -1) : (dx > 0 ? -1 : 1);
					break;
				// 【下坠时也要横移】。站着往下掉只是换个高度挨打
				case DodgeAct.Dive:
					if (dist < want) go = away;
					break;
				// 【远端只对指定了距离的 boss 生效】。那是肉山的需求(跑太远吃激光),
				// 别的 boss 没填 WantCells,行为和以前一模一样
				case DodgeAct.Keep:
					if (dist < want / 2) go = away;
					else if (fixedWant > 0 && dist > want) go = toward;
					break;
			}

			// 钩爪:发射 → 勾住 → 【必须跳一次取消】。跳完拿到那段速度,二段跳也回来了
			bool hooking = Hook(p, boss, act, onGround, out bool hookJump);

			// 【飘太久连跳也不许,但 hookJump 例外】。挂在钩子上不是滞空,拦住就永远下不来
			// 【noJump 要连反射层一起禁】。Banned 只改 act,而 JevSaysJump/incoming 跟 act 无关
			bool noJump = BossBook.IsBanned(boss.type, DodgeAct.Up);
			bool wantJump = hookJump || ((act == DodgeAct.Up || JevSaysJump || incoming) && !_tooLongAirborne && !noJump);
			// 【只有"想往上"才烧翅膀】。incoming 几乎恒真,拿它当飞行条件就是一有威胁就烧光
			_wantFly = act == DodgeAct.Up && !_tooLongAirborne && !noJump && p.grapCount == 0;
			bool jump = Jump(p, onGround, wantJump, act == DodgeAct.Up && !_tooLongAirborne && !noJump);

			// 【堵死了就往空的那侧走】。站定会被顶在墙上当靶子(两次 20%/12% 的大掉血都是 L0+go-)
			// 两侧都堵才停。哪边空是算得出来的,不用猜
			if (go != 0 && WallDistance(p, go) <= 0)
				go = WallDistance(p, -go) > 0 ? -go : 0;

			int want0 = go;
			go = Dash(p, go, incoming, act);
			bool dashing = go != want0 || _dashGap;

			if (go < 0) p.controlLeft = true;
			else if (go > 0) p.controlRight = true;
			if (jump) p.controlJump = true;

			// 【down 干两件事】(fallThrough = controlDown):穿平台 + 取消缓降;勾着时不能按
			bool dive = (act == DodgeAct.Dive || _tooLongAirborne) && p.grapCount == 0;
			// 飞的时候不按上:那是羽落缓降,跟爬升抢同一个方向键
			bool hover = !dive && !_wantFly && (act == DodgeAct.Float || act == DodgeAct.Up || hooking || incoming);
			if (dive) p.controlDown = true;
			else if (!onGround && hover) p.controlUp = true;

			Fly(p);
			Last = $"{act} boss {(bossRight ? "R" : "L")}{dist} (want {want}) go {(go == 0 ? "-" : go < 0 ? "L" : "R")}"
				 + (jump ? (onGround ? " +jump" : " +airjump") : "") + (hooking ? " +hook" : "")
				 + (dashing ? " +dash" : "") + (p.dashDelay < 0 ? " [dashing]" : "")
				 + (!onGround && act != DodgeAct.Close ? " +float" : "")
				 + (incoming ? $" hit in {framesToHit}f" : "")
				 + (TacticWorking ? "" : " [not working]");
		}

		// 【判据是 wingTime 在掉】。"有翅膀+在上升"会把每次起跳都算成飞行,翅膀其实一格没烧
		static bool _flying;
		static float _prevWing;
		static void Fly(Player p)
		{
			bool now = p.wingTime < _prevWing - 0.01f;
			_prevWing = p.wingTime;
			if (now == _flying) return;
			_flying = now;
			DiagLog.Write($"[dodge] 飞行{(now ? "开始" : "结束")} wantFly={_wantFly}"
				+ $" wingMax={p.wingTimeMax} wingTime={p.wingTime:0.0} vy={p.velocity.Y:0.0}");
		}

		// 钩爪。【勾住之后一定要跳一次】,否则会被直接拉过去,那就不是位移是送死。
		// 光标是全局的,攻击层每帧在瞄 boss -- 只有发射那一帧抢过来指个方向,之后不用再指
		static bool Hook(Player p, NPC boss, DodgeAct act, bool onGround, out bool hookJump)
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
			if (act != DodgeAct.Grapple) { _hookFrames = 0; return false; }
			// 【钩爪也要松一帧】。vanilla 是 if(controlHook){ if(releaseHook) 发射; releaseHook=false; }
			// else releaseHook=true -- 一直按住只发射一次,之后全是空按。和二段跳同一个坑
			if (_hookFrames++ > HookGiveUpFrames) return false;
			if (_hookHeld) { _hookHeld = false; return true; }
			// 【必须瞄到真能勾住的格子】。对着空气甩,钩子飞完全程再空手回来,
			// 这期间人既没位移也没输出 -- 找不到落点就干脆不甩
			if (!FindAnchor(p, boss, out int ax, out int ay)) { _hookFrames = 0; return false; }
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
			// 【能不能跳问 vanilla】。原来只记"这次滞空跳过没有",没跳就当有 --
			// 没云朵瓶也照按,白扔一次。AnyExtraJumpUsable 连模组跳一起算,还认 blockExtraJumps
			if (!p.AnyExtraJumpUsable()) return false;
			// 【有多段不等于要烧多段】。第一段照旧,第二段起只认 act==Up:
			// incoming 几乎恒真,拿它放行就是一滞空把储备全烧完
			if (_airJumped && !wantMore) return false;
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
			float fx = closeX > 0.1f ? gapX / closeX : 9999f;
			float fy = closeY > 0.1f ? gapY / closeY : 9999f;
			float f = System.Math.Max(fx <= 0f ? 0f : fx, fy <= 0f ? 0f : fy);
			return f > 600f ? -1 : (int)f;
		}

		// 克苏鲁之盾的冲刺。【vanilla 要双击】(Player.cs: flag5 = controlLeft && releaseLeft,
		// 15 帧内第二次按下才算),所以必须空出一帧不按方向键,下一帧再按下去
		static int Dash(Player p, int go, bool incoming, DodgeAct act)
		{
			// 【冲刺中要先于就绪判断】。正在冲的时候 dashDelay<0、dash!=0,
			// 就绪判据必然为假 -- 写在它后面这一行永远执行不到,方向也就保持不住
			if (_dashDir != 0 && p.dashDelay < 0) return _dashDir;

			// dashDelay==0 才是就绪。>0 是内置冷却,<0 是正在冲
			bool ready = p.dashType != 0 && p.dashDelay == 0 && p.dash == 0;
			if (!ready) { _dashGap = false; _dashDir = 0; return go; }

			// 上一帧空了手,这一帧按下去 -- 双击成立。【按住不放】,
			// 冲完直接接着走,不然冲刺结束会有一段没速度的真空
			if (_dashGap) { _dashGap = false; return _dashDir; }
			_dashDir = 0;

			// 【时机交给 Jev】。反射层判不了:FramesToHit 只做直线外推,
			// 对荡着走的手那种圆周运动完全失真,拿它当冲刺时机就是乱冲
			if (!JevSaysDash || go == 0) return go;

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
		static bool FindAnchor(Player p, NPC boss, out int ax, out int ay)
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
					if (!Hookable(x, y)) continue;
					// 【远离 boss 就是好落点】。往上还是往下让局面自己定,
					// 不编"高过 N 格就往下勾"那种阈值 -- 那些数我一个都不知道
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
				if (Predicates.IsSolid(cx, feet + d)) return d;
			}
			return 40;
		}

		static void Release() => AxisLock.Release(Owner);

		// 【给方向不给标量】。ThreatScan 那份 speed 是绝对值,丢了符号,
		// 而走位要判的正是"它朝哪飞"
		static string Facts(Player p, NPC boss, int dist)
		{
			float dx = (boss.Center.X - p.Center.X) / 16f;
			float dy = (boss.Center.Y - p.Center.Y) / 16f;
			bool closing = (dx > 0 && boss.velocity.X < 0) || (dx < 0 && boss.velocity.X > 0);
			int hit = FramesToHit(p, boss);
			int pcx = (int)(p.Center.X / 16f), pcy = (int)(p.Center.Y / 16f);
			return "{\"hp_percent\":" + (p.statLife * 100 / System.Math.Max(1, p.statLifeMax))
				 + ",\"boss\":\"" + JsonStr(boss.TypeName) + "\""
				 + ",\"boss_hp_percent\":" + (boss.life * 100 / System.Math.Max(1, boss.lifeMax))
				 + ",\"boss_side\":\"" + (dx > 0 ? "右边" : "左边") + "\""
				 + ",\"boss_cells_horizontal\":" + (int)System.Math.Abs(dx)
				 // 【"偏了多少"要直说】,不然它不知道此刻离想要的距离有多远,只会一直答 Back
				 // 【和反射层用同一个数】。两边各算各的话,它以为要 12 格而人在奔向 60
				 + ",\"distance_i_asked_for\":" + Want(boss)
				 + ",\"cells_further_than_i_asked_for\":" + (dist - Want(boss))
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
			 + "\"intent\":{\"type\":\"choice\",\"instructions\":"
			 + "\"泰拉瑞亚 boss 战。这个自动玩家的武器会自己瞄准开火,所以它只要决定走位。"
			 + "碰到 boss 或者吃到弹幕才掉血。【说的是意图不是按键】,具体往左往右由代码每帧算。\",\"criteria\":{"
			 + "\"Keep\":\"保持现在的位置。够得着打,又没有被逼近,站稳输出\","
			 + "\"Back\":\"拉开距离。它正冲过来,或者血不多了要留余地\","
			 + "\"Close\":\"靠近一点。它飞远了打不到,或者它现在不动正好多打几下。"
			 + "看 cells_further_than_i_asked_for:这个数大就说明已经比自己要的距离远出不少,"
			 + "有的 boss 离太远反而更危险(远程攻击正好覆盖那一带),那就该收回来\","
			 + "\"Evade\":\"横向闪开。它已经贴脸或者马上要撞上,先把这一下躲过去。"
			 + "弹幕从某一侧压过来时也用这个 -- 看 projectile_pressure 的左右两边哪边发数少,往空的那边闪\","
			 + "\"Up\":\"往上跳。它从下方上来,或者该上更高一层平台\","
			 + "\"Float\":\"跳起来滞空,让贴着地面来的那一击从脚下穿过去 -- 弹幕贴着地面扫过来时也一样。"
			 + "滞空时横向移动速度正常,照样能边飘边躲。"
			 + "但对从上往下砸的、从上方压下来的弹幕、或者会瞬移到人身上的没用,那种情况滞空是把自己定在落点上。"
			 + "看 frames_airborne:已经飘了一阵子说明那一下早就过去了,该落地跑动而不是接着飘\","
			 + "\"Dive\":\"降到下面一层去。踩着平台时会穿下去,在空中时会快速落回地面。"
			 + "头顶压下来的东西、或者从上方来的弹幕(看 projectile_pressure 的 from_above),"
			 + "掉下去一层往往就扑空了;在空中飘了一阵子而局面没有变好时也用它落地重来\","
			 + "\"Orbit\":\"绕着它走。沿垂直于'自己到它连线'的方向横移,让它的冲撞擦身而过。"
			 + "场地封闭、退无可退的时候用这个 -- 往后退只会退到墙上,而绕开既保持了移动又不撞上去\","
			 + "\"Grapple\":\"甩钩爪换位。勾住的瞬间跳起来取消,拿到那一段速度,二段跳也会重置。"
			 + "往上勾能拔高,往左下右下勾能快速落到另一个高度 -- 跑动中来不及掉头、"
			 + "或者横着跑躲不开追过来的东西时,换个高度往往比继续跑有用\"}},"
			 + "\"danger\":{\"type\":\"score\",\"instructions\":"
			 + "\"眼下有多危险,决定它该离 boss 多远。越危险越该拉开。\",\"criteria\":["
			 + "\"很安全,可以贴上去输出\",\"一般,保持中距\",\"有点险,拉开一些\","
			 + "\"很险,离远点\",\"随时会死,能躲多远躲多远\"]},"
			 + "\"should_dash_now\":{\"type\":\"noul\",\"instructions\":"
			 + "\"就这一刻该用克苏鲁之盾冲刺吗?冲刺是朝当前移动方向猛冲一小段,有内置冷却。"
			 + "它能瞬间拉开一段距离、或者穿过一片危险区域;撞到敌人还会免掉那一下伤害。"
			 + "但冲刺中方向不好改,乱冲会一头撞进本来躲得开的攻击里。\"},"
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

			string intent = Seg(txt, "intent");
			string pick = Field(intent, "choice");
			if (pick == null) return;
			LatencyMs = ms;
			Confidence = Num(intent, "confidence", 0f);
			Act = pick switch
			{
				"Back" => DodgeAct.Back,
				"Close" => DodgeAct.Close,
				"Evade" => DodgeAct.Evade,
				"Up" => DodgeAct.Up,
				"Float" => DodgeAct.Float,
				"Grapple" => DodgeAct.Grapple,
				"Orbit" => DodgeAct.Orbit,
				"Dive" => DodgeAct.Dive,
				_ => DodgeAct.Keep,
			};
			_actAt = _clock.ElapsedMilliseconds;

			Probs = Seg(intent, "probabilities") ?? "";
			TopTwo = Rank(Probs);
			// 意图变了才播报。每 200ms 一条会把聊天刷没,那就不是证据是噪音。
			// 【但卡在一个意图上时也要出声】,否则最该看见的那种局面反而一片安静
			long now = _clock.ElapsedMilliseconds;
			if (Act != _saidAct || now - _saidAt > 4000)
			{
				string tag = Act == _saidAct ? $"  [held {(now - _saidAt) / 1000}s]" : "";
				_saidAct = Act; _saidAt = now;
				Main.NewText($"<Jev> {Say(Act)}{tag}  ({TopTwo})  confidence {Confidence:0.00}  {ms}ms", 90, 230, 120);
			}

			// Noul 【没有 confidence】,概率本身就是答案。0.7 当"是"
			Danger = Num(Seg(txt, "danger"), "score", Danger);
			JevSaysJump = Num(Seg(txt, "should_jump_now"), "noul", 0f) > 0.7f;
			JevSaysDash = Num(Seg(txt, "should_dash_now"), "noul", 0f) > 0.7f;
			SafeToAttack = Num(Seg(txt, "safe_to_attack"), "noul", 1f) > 0.5f;
			TacticWorking = Num(Seg(txt, "tactic_working"), "noul", 1f) > 0.4f;

			// 【每次回答都记】。原来只在意图变了才记 -- 于是"一直选 Float"在日志里
			// 只有孤零零一条,看不出它卡了多久,也看不出反射层这期间在干什么
			JevLog.Add(new JevLog.Entry
			{
				Ms = _clock.ElapsedMilliseconds,
				Site = "dodge",
				State = _lastFacts,
				Pick = pick + $" 危险{Danger:0.0}" + (JevSaysJump ? " 该跳" : "")
					 + (SafeToAttack ? "" : " 别贴脸") + (TacticWorking ? "" : " 这套没用")
					 + "  →  " + Last,
				Confidence = Confidence,
				Probs = Seg(intent, "probabilities") ?? "",
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
		static string Say(DodgeAct a) => a switch
		{
			DodgeAct.Back => "back off",
			DodgeAct.Close => "close in and attack",
			DodgeAct.Evade => "dodge sideways",
			DodgeAct.Up => "jump up",
			DodgeAct.Float => "hover, let it pass underneath",
			DodgeAct.Grapple => "grapple for height",
			DodgeAct.Orbit => "orbit around it",
			DodgeAct.Dive => "drop down fast",
			_ => "hold position",
		};

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
