using System.Net.Http;
using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Port = TerraBlind.JevariaCombat;

namespace TerraBlind
{
	// Jev 说往哪躲
	public enum Dir { Up, UpRight, Right, DownRight, Down, DownLeft, Left, UpLeft, Stay }
	// 水平 Medium 是钩爪；垂直 Medium 是向上钩爪，Large 是坐骑
	public enum Size { Small, Medium, Large, None }
	public readonly record struct DodgeAction(Dir Direction, Size Horizontal, Size Vertical, Vector2? HookTarget = null)
	{
		public static DodgeAction Idle => new(Dir.Stay, Size.None, Size.None);
	}

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

		static readonly Port.GameSensor _sensor = new();
		static readonly Port.GameMovement _movement = new();
		static readonly Port.ProjectileAim _attack = new();
		static Port.CombatSnapshot _snapshot;
		static string _movementReason = "";
		public static string Last = "idle";
		public static DodgeAction Action = DodgeAction.Idle;
		static Dictionary<string, DodgeAction> _availableActions = new();
		static string _lastBlocked = "";
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
		static volatile Reply _pending;
		// 发出去的那份现场,答案回来时一起记进日志。
		sealed class RequestContext
		{
			public int Sequence;
			public int BossType;
			public long SentAtMs;
			public string Facts;
			public string Blocked;
			public Dictionary<string, DodgeAction> Actions;
		}
		sealed class Reply
		{
			public RequestContext Request;
			public string Packed;
		}
		static int _requestSequence;
		static int _appliedSequence;
		static DodgeAction _appliedAction = DodgeAction.Idle;
		static DodgeAction _completedAction = DodgeAction.Idle;
		static long _completedDurationMs;
		static long _releasedAtMs = -1;
		static string _lastApplied = "";
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

		public static void Tick()
		{
			if (!Enabled || !AgentPermissions.Dodge) { Release(); return; }
			var p = Main.LocalPlayer;
			if (p != null && p.active && p.dead) FightEnd("死亡");
			if (p == null || !p.active || p.dead) { Release(); return; }
			if (Manual.On) { Gate("人工"); Release(); return; }
			if (Main.gameMenu || Main.playerInventory) { Gate("paused"); _movement.Release(p); return; }
			bool manualInput = p.controlLeft || p.controlRight || p.controlUp || p.controlDown
				|| p.controlJump || p.controlHook || p.controlMount;

			var boss = Boss(p, out int dist);
			if (boss == null) { FightEnd("boss 没了(击败或离场)"); Gate("no boss"); Release(); return; }
			if (!_inFight) FightStart();
			_attack.SelectFirstWeapon(p);
			_sensor.Observe(p);

			var done = _pending;
			if (done != null)
			{
				_pending = null;
				if (!RandomBrain) Parse(done);
			}
			string key = JevCombat.Key();
			if (RandomBrain) RandomPick(boss.type);
			else if (key != null && !_busy && _pending == null)
			{
				_snapshot = _sensor.Capture(p, (ulong)(_requestSequence + 1), ToIntent(_completedAction), _completedDurationMs);
				if (_snapshot == null) { Release(); return; }
				string facts = Facts(p, boss);
				string criteria = ActionCriteria(p);
				var request = new RequestContext
				{
					Sequence = ++_requestSequence,
					BossType = boss.type,
					SentAtMs = _clock.ElapsedMilliseconds,
					Facts = facts,
					Blocked = _lastBlocked,
					Actions = _availableActions
				};
				Fire(key, Body(facts, criteria), request);
			}

			// 意图过期时原地、小幅
			bool stale = _clock.ElapsedMilliseconds - _actAt > IntentTtlMs;
			var action = stale ? DodgeAction.Idle : Action;

			// 羽落要按住 up,所以也拿 Vertical
			if (!AxisLock.Take(Owner, Ax.Move | Ax.Jump | Ax.Vertical, () => Enabled))
			{ Gate("Move axis taken by " + AxisLock.Held(Ax.Move)); _movement.Release(p); _appliedAction = DodgeAction.Idle; _lastApplied = ""; return; }

			Gate($"driving {boss.TypeName} {dist}格 act={action.Direction}/{action.Horizontal}/{action.Vertical}");
			if (manualInput) { _movement.Release(p); _movementReason = ""; return; }
			Drive(p, boss, dist, action);
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
		static string ActionName(DodgeAction action)
		{
			if (action.Direction == Dir.Stay) return "Stay";
			var (x, y) = Vec(action.Direction);
			return (x == 0 ? "" : (x < 0 ? "Left" : "Right") + action.Horizontal)
				+ (y == 0 ? "" : (y < 0 ? "Up" : "Down") + action.Vertical);
		}
		static Port.DodgeIntent ToIntent(DodgeAction action) => new(
			(Port.DodgeDirection)action.Direction,
			System.Enum.Parse<Port.Magnitude>(action.Horizontal.ToString()),
			System.Enum.Parse<Port.Magnitude>(action.Vertical.ToString()), false);

		static DodgeAction FromIntent(Port.DodgeIntent intent, Vector2? hook = null) => new(
			(Dir)intent.Direction, System.Enum.Parse<Size>(intent.HorizontalSize.ToString()),
			System.Enum.Parse<Size>(intent.VerticalSize.ToString()), hook);

		static void Drive(Player p, NPC boss, int dist, DodgeAction action)
		{
			if (_snapshot == null || _clock.ElapsedMilliseconds - _actAt > IntentTtlMs)
			{
				_movement.Release(p);
				_movementReason = "";
				_appliedAction = DodgeAction.Idle;
				return;
			}
			var result = _movement.Apply(p, ToIntent(action),
				_snapshot with { Sequence = (ulong)_appliedSequence });
			_appliedAction = FromIntent(result.Applied);
			if (_movementReason != result.Reason || _lastApplied != ActionName(_appliedAction))
			{
				_movementReason = result.Reason;
				_lastApplied = ActionName(_appliedAction);
				DiagLog.Write($"[dodge] applied #{_appliedSequence} requested={ActionName(action)} applied={_lastApplied} reason={result.Reason} vx={p.velocity.X:0.0} vy={p.velocity.Y:0.0} mount={p.mount.Active}/{p.mount.Type}");
			}
			TrackDps();
			Hurt(p, boss, dist, _appliedAction.Direction, _appliedAction.Horizontal, _appliedAction.Vertical);
			Fly(p, _appliedAction.Direction);
			Last = $"{_lastApplied} boss {dist}格 {result.Reason}";
		}

		public static bool TryAttackBoss(Player p, out Vector2 target)
		{
			target = p.Center;
			if (Main.gameMenu || Main.playerInventory) return false;
			if (p.statLife < p.statLifeMax2 * 0.5f || p.lavaWet) p.QuickHeal();
			_attack.SelectFirstWeapon(p);
			_sensor.Observe(p);
			var live = _sensor.Capture(p, (ulong)_appliedSequence, ToIntent(_appliedAction), 0);
			if (live == null) return false;
			if (_movementReason == "grapple") return false;
			return _attack.TryAttack(p, live, out target);
		}

		public static void EndBossAttack(Player p) => _attack.RestoreWeapon(p);

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
		static void Hurt(Player p, NPC boss, int dist, Dir dir, Size hSize, Size vSize)
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
				+ $" 离{boss.TypeName} {dist}格 意图{dir}/{hSize}/{vSize}"
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

		static int CeilingDistance(Player p)
		{
			int left = p.Hitbox.Left / 16, right = (p.Hitbox.Right - 1) / 16;
			int top = p.Hitbox.Top / 16;
			for (int d = 1; d <= RoomScanCells; d++)
			{
				int y = top - d;
				if (y <= EdgeCells) return d - 1;
				for (int x = left; x <= right; x++)
					if (Predicates.IsWall(x, y)) return d - 1;
			}
			return RoomScanCells;
		}

		// 脚下到实心块几格,平台能穿所以不算,地图下边缘也算
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

		static void Release()
		{
			_releasedAtMs = _clock.ElapsedMilliseconds;
			_movement.Release(Main.LocalPlayer);
			_movementReason = "";
			_appliedAction = DodgeAction.Idle;
			_completedAction = DodgeAction.Idle;
			_completedDurationMs = 0;
			_lastApplied = "";
			AxisLock.Release(Owner);
		}

		static string Facts(Player p, NPC boss) => Port.DecisionFacts.State(_snapshot, FieldBooks(boss));

		static string ActionCriteria(Player p)
		{
			if (_snapshot == null)
				_snapshot = _sensor.Capture(p, (ulong)_appliedSequence, ToIntent(_completedAction), _completedDurationMs);
			var options = new Dictionary<string, DodgeAction>();
			var sb = new StringBuilder();
			if (_snapshot != null)
				foreach (var option in _snapshot.AvailableActions)
				{
					var action = FromIntent(option.Intent, option.HookTarget);
					string name = ActionName(action);
					options[name] = action;
					if (sb.Length > 0) sb.Append(',');
					sb.Append(Quote(name)).Append(':').Append(Quote(Port.DecisionFacts.Describe(option, _snapshot)));
				}
			_availableActions = options;
			_lastBlocked = "";
			return sb.ToString();
		}

		static string Body(string state, string actions)
			=> "{\"model\":\"" + Model + "\",\"state\":" + Quote(state) + ",\"questions\":{"
			 + "\"action\":{\"type\":\"choice\",\"instructions\":"
			 + "\"Terraria boss fight. Choose one complete dodge action for the next reaction interval. "
			 + "Look at every boss part and shot, where each is heading and how soon it reaches me, and how much room I have. "
			 + "Frames until contact assumes our current velocities stay constant; use enemy health to identify its phase. "
			 + "Keep an escape route: do not run into a wall, floor or ceiling, or toward another threat. "
			 + "Follow the boss notes. Each axis has its own magnitude. Small is ordinary movement, "
			 + "horizontal or upward Medium uses a grapple, and vertical Large uses a mount. "
			 + "Pick a tool only when its extra movement helps. "
			 + "Actions continue until the next answer.\",\"criteria\":{"
			 + actions + "}"
			 + "}}}";

		static void Fire(string key, string body, RequestContext request)
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
					if (res.IsSuccessStatusCode) _pending = new Reply { Request = request, Packed = sw.ElapsedMilliseconds + "\u001f" + txt };
					else DiagLog.Write($"[dodge] HTTP #{request.Sequence} {(int)res.StatusCode} body={txt}");
				}
				catch (System.Exception e) { DiagLog.Write($"[dodge] 请求 #{request.Sequence} 炸了 {e.GetType().Name} {e.Message}"); }
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
			Dir dir = (Dir)JevPrior.Sample(bossType, "direction", 9);
			var (x, y) = Vec(dir);
			Size hSize = x == 0 ? Size.None : (Size)JevPrior.Sample(bossType, "horizontal_size", 2);
			Size vSize = y == 0 ? Size.None : y < 0
				? (Size)JevPrior.Sample(bossType, "up_size", 3)
				: JevPrior.Sample(bossType, "down_size", 2) == 0 ? Size.Small : Size.Large;
			ActionCriteria(Main.LocalPlayer);
			var sampled = new DodgeAction(dir, hSize, vSize);
			bool found = _availableActions.TryGetValue(ActionName(sampled), out var available);
			if (!found) found = _availableActions.TryGetValue(ActionName(new DodgeAction(dir,
					x == 0 ? Size.None : Size.Small, y == 0 ? Size.None : Size.Small)), out available);
			Action = found ? available : DodgeAction.Idle;
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
			_appliedSequence = ++_requestSequence;
			DiagLog.Write($"[dodge] random -> {ActionName(Action)}");
		}

		static void Parse(Reply reply)
		{
			RequestContext request = reply.Request;
			if (request.Sequence <= _appliedSequence || request.SentAtMs <= _releasedAtMs ||
				_clock.ElapsedMilliseconds - request.SentAtMs > IntentTtlMs)
			{
				DiagLog.Write($"[dodge] 丢弃过期回答 #{request.Sequence}");
				return;
			}
			string packed = reply.Packed;
			int cut = packed.IndexOf('\u001f');
			int ms = 0;
			string txt = packed;
			if (cut > 0) { int.TryParse(packed.Substring(0, cut), out ms); txt = packed.Substring(cut + 1); }

			string aq = Seg(txt, "action");
			string choice = Field(aq, "choice");
			if (choice == null)
			{
				DiagLog.Write($"[dodge] 读不出 choice: {txt.Substring(0, System.Math.Min(160, txt.Length))}");
				return;
			}
			if (!request.Actions.TryGetValue(choice, out var action))
			{
				DiagLog.Write($"[dodge] 未知动作 {choice}");
				return;
			}
			_completedAction = _appliedAction;
			_completedDurationMs = _actAt < 0 ? 0 : System.Math.Max(0, _clock.ElapsedMilliseconds - _actAt);
			Action = action;
			_appliedSequence = request.Sequence;
			DiagLog.Write($"[dodge] jev #{request.Sequence} {ms}ms -> {choice} 选择{Rank(Seg(aq, "probabilities"))}"
				+ (request.Blocked.Length > 0 ? $" 挡住的方向 {request.Blocked}" : ""));
			LatencyMs = ms;
			Confidence = Num(aq, "confidence", 0f);
			_actAt = _clock.ElapsedMilliseconds;
			_answer++;

			Probs = Seg(aq, "probabilities") ?? "";
			TopTwo = Rank(Probs);
			// 意图变了或同一意图持续 4 秒才发到聊天栏
			long now = _clock.ElapsedMilliseconds;
			var said = ActionName(Action);
			if (said != _saidAct || now - _saidAt > 4000)
			{
				string tag = said == _saidAct ? $"  [held {(now - _saidAt) / 1000}s]" : "";
				_saidAct = said; _saidAt = now;
				Main.NewText($"<Jev> {said}{tag}  ({TopTwo})  confidence {Confidence:0.00}  {ms}ms", 90, 230, 120);
			}

			var (x, y) = Vec(Action.Direction);
			var picks = new List<(string Q, int Pick, int Options)> { ("direction", (int)Action.Direction, 9) };
			if (x != 0) picks.Add(("horizontal_size", (int)Action.Horizontal, 2));
			if (y < 0) picks.Add(("up_size", (int)Action.Vertical, 3));
			if (y > 0) picks.Add(("down_size", Action.Vertical == Size.Large ? 1 : 0, 2));
			JevPrior.Record(request.BossType, picks.ToArray());

			JevLog.Add(new JevLog.Entry
			{
				Ms = _clock.ElapsedMilliseconds,
				Site = "dodge",
				State = request.Facts,
				Pick = $"#{request.Sequence} {choice}  →  " + Last,
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
