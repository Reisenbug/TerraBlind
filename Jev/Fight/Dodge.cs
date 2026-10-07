using System.Net.Http;
using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

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
		const int HorizontalReserveCells = 35;
		const int UpperReserveCells = 18;
		const int LowerReserveCells = 4;
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
		static int _hookAnswer = -1;
		// 荡完歇一会儿
		// 勾上就跳、跳完就勾，等于玩家原地不动
		const int HookCooldownFrames = 30;
		static int _hookCooldown;
		static bool _autoMountActive;
		static int _autoMountType = MountID.None;
		static bool _mountForDown;
		static bool _mountRiseSeen;
		static bool _mountFallSeen;
		static ulong _mountStartTick;
		static int _upMountAnswer = -1;
		static int _downMountAnswer = -1;
		static bool _wasDashing;
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
				  .Append(",\"health_percent\":").Append((long)n.life * 100 / System.Math.Max(1, n.lifeMax))
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
			if (!Enabled) { Release(); return; }
			var p = Main.LocalPlayer;
			if (p != null && p.active && p.dead) FightEnd("死亡");
			if (p == null || !p.active || p.dead) { Release(); return; }
			if (Manual.On) { Gate("人工"); Release(); return; }

			var boss = Boss(p, out int dist);
			if (boss == null) { FightEnd("boss 没了(击败或离场)"); Gate("no boss"); Release(); return; }
			if (!_inFight) FightStart();
			TrackPartSpeeds(p);

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
			{ Gate("Move axis taken by " + AxisLock.Held(Ax.Move)); DismountAuto(p); _appliedAction = DodgeAction.Idle; _lastApplied = ""; return; }

			Gate($"driving {boss.TypeName} {dist}格 act={action.Direction}/{action.Horizontal}/{action.Vertical}");
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
		static Dir Direction(int x, int y) => (x, y) switch
		{
			(0, -1) => Dir.Up, (1, -1) => Dir.UpRight,
			(1, 0) => Dir.Right, (1, 1) => Dir.DownRight,
			(0, 1) => Dir.Down, (-1, 1) => Dir.DownLeft,
			(-1, 0) => Dir.Left, (-1, -1) => Dir.UpLeft,
			_ => Dir.Stay,
		};
		static bool Blocked(Player p, int x, int y, int left, int right, int above, int below)
		{
			float horizontalReserve = System.Math.Max(HorizontalReserveCells,
				System.Math.Abs(p.velocity.X) * 60f * 1.2f / 16f)
				+ System.Math.Max(0f, x * p.velocity.X) * 18f / 16f;
			float upperReserve = System.Math.Max(UpperReserveCells,
				System.Math.Max(0f, -p.velocity.Y) * 60f * 0.6f / 16f)
				+ 5f + System.Math.Max(0f, -p.velocity.Y) * 18f / 16f;
			return x < 0 && left <= horizontalReserve || x > 0 && right <= horizontalReserve
				|| y < 0 && above <= upperReserve || y > 0 && below <= LowerReserveCells;
		}
		static string ActionName(DodgeAction action)
		{
			if (action.Direction == Dir.Stay) return "Stay";
			var (x, y) = Vec(action.Direction);
			return (x == 0 ? "" : (x < 0 ? "Left" : "Right") + action.Horizontal)
				+ (y == 0 ? "" : (y < 0 ? "Up" : "Down") + action.Vertical);
		}
		static int EquippedVerticalMount(Player p) => p.miscEquips[3].type switch
		{
			ItemID.SlimySaddle => MountID.Slime,
			ItemID.QueenSlimeMountSaddle => MountID.QueenSlime,
			_ => MountID.None,
		};
		static bool MountAuto(Player p, int type, bool down)
		{
			if (p.mount.Active || !p.mount.CanMount(type, p)) return false;
			float before = p.velocity.Y;
			p.mount.SetMount(type, p);
			if (!p.mount.Active || p.mount.Type != type) return false;
			_autoMountActive = true;
			_autoMountType = type;
			_mountForDown = down;
			_mountRiseSeen = _mountFallSeen = false;
			_mountStartTick = Main.GameUpdateCount;
			DiagLog.Write($"[dodge] 坐骑开始 type={type} 方向={(down ? "下" : "上")} vy={before:0.0}->{p.velocity.Y:0.0}");
			return true;
		}
		static void DismountAuto(Player p)
		{
			if (!_autoMountActive) return;
			if (p != null && p.mount.Active && p.mount.Type == _autoMountType)
			{
				p.mount.Dismount(p);
				DiagLog.Write("[dodge] 坐骑结束");
			}
			_autoMountActive = false;
			_autoMountType = MountID.None;
			_mountForDown = _mountRiseSeen = _mountFallSeen = false;
		}

		// 反射层,每帧把方向和幅度翻成按键
		static void Drive(Player p, NPC boss, int dist, DodgeAction action)
		{
			bool onGround = p.velocity.Y == 0f;
			Dir dir = action.Direction;
			var (hx, vy) = Vec(dir);
			Size hSize = action.Horizontal, vSize = action.Vertical;
			int left = WallDistance(p, -1), right = WallDistance(p, 1);
			int above = CeilingDistance(p), below = FloorDistance(p);
			if (hx != 0 && Blocked(p, hx, 0, left, right, above, below)) hx = 0;
			if (vy != 0 && Blocked(p, 0, vy, left, right, above, below)) vy = 0;
			if (hx == 0) hSize = Size.None;
			if (vy == 0) vSize = Size.None;
			dir = Direction(hx, vy);
			int mountType = EquippedVerticalMount(p);
			if (vSize == Size.Large && mountType == MountID.None) vSize = Size.Small;
			bool upMount = vy < 0 && vSize == Size.Large && mountType != MountID.None;
			bool downMount = vy > 0 && vSize == Size.Large && mountType != MountID.None;
			if (_autoMountActive && (!p.mount.Active || p.mount.Type != _autoMountType ||
				(!upMount && !downMount) || _mountForDown != downMount ||
				_mountForDown && _mountFallSeen && p.velocity.Y <= 0f ||
				!_mountForDown && (_mountRiseSeen && p.velocity.Y >= 0f ||
					!_mountRiseSeen && Main.GameUpdateCount - _mountStartTick > 6)))
				DismountAuto(p);
			bool hookH = hx != 0 && hSize == Size.Medium && !_autoMountActive;
			bool hookV = vy < 0 && vSize == Size.Medium && !_autoMountActive;
			Dir hookDir = Direction(hookH ? hx : 0, hookV ? vy : 0);
			bool hooking = Hook(p, hookDir, (hookH || hookV) && !downMount,
				action.HookTarget, out bool hookJump, out bool hookMiss);
			if (hookMiss)
			{
				if (hookH) hSize = Size.Small;
				if (hookV) vSize = Size.Small;
			}
			bool hop = vy < 0 && _airJumpUsed != _answer && p.AnyExtraJumpUsable();
			bool jump = Jump(p, onGround, vy < 0, hop, hookJump);

			if (hx < 0) p.controlLeft = true;
			else if (hx > 0) p.controlRight = true;
			if (jump) p.controlJump = true;

			if (p.grapCount == 0 && vy > 0) p.controlDown = true;
			else if (p.grapCount == 0 && (vy < 0 || vy == 0 && !onGround)) p.controlUp = true;
			if (upMount && !_autoMountActive && _upMountAnswer != _answer &&
				p.velocity.Y <= -4f && !hooking && p.grapCount == 0)
			{
				_upMountAnswer = _answer;
				if (!MountAuto(p, mountType, false)) vSize = Size.Small;
			}
			if (downMount && !_autoMountActive && _downMountAnswer != _answer &&
				!hooking && p.grapCount == 0)
			{
				_downMountAnswer = _answer;
				if (!MountAuto(p, mountType, true)) vSize = Size.Small;
			}
			if (vSize == Size.Large && !_autoMountActive &&
				(vy < 0 && _upMountAnswer == _answer || vy > 0 && _downMountAnswer == _answer))
				vSize = Size.Small;
			if (_autoMountActive && !_mountForDown && p.velocity.Y < -0.1f) _mountRiseSeen = true;
			if (_autoMountActive && _mountForDown && p.velocity.Y > 0.1f) _mountFallSeen = true;

			Fly(p, dir);
			TrackDps();
			Hurt(p, boss, dist, dir, hSize, vSize);
			bool dashing = p.dashDelay < 0;
			if (dashing && !_wasDashing) DiagLog.Write($"[dodge] 非预期冲刺 方向{(p.velocity.X < 0 ? "左" : "右")} vx={p.velocity.X:0.0} 意图{ActionName(action)}");
			_wasDashing = dashing;
			Last = $"{dir}/{hSize}/{vSize} boss {dist}格 go {(hx == 0 ? "-" : hx < 0 ? "L" : "R")}"
				 + (jump ? (onGround ? " +jump" : " +airjump") : "") + (hooking ? " +hook" : "")
				 + (_autoMountActive ? " +mount" : "") + (dashing ? " [dashing]" : "");
			_appliedAction = new DodgeAction(dir, hSize, vSize);
			string applied = ActionName(_appliedAction);
			if (applied != _lastApplied)
			{
				_lastApplied = applied;
				DiagLog.Write($"[dodge] applied #{_appliedSequence} requested={ActionName(action)} applied={applied} room={left}/{right}/{above}/{below} vx={p.velocity.X:0.0} vy={p.velocity.Y:0.0}");
			}
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
			_wasDashing = false;
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

		// 钩爪发射时瞄准选中的落点；miss 表示射程内勾不到东西
		static bool Hook(Player p, Dir dir, bool want, Vector2? planned, out bool hookJump, out bool miss)
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
			if (_hookCooldown > 0) _hookCooldown--;
			if (_hookAnswer != _answer)
			{
				_hookAnswer = _answer;
				_hookFrames = 0;
				_hookHeld = false;
			}
			if (!want) { _hookFrames = 0; return false; }
			if (_hookCooldown > 0)
			{
				if (_hookWaitSaid != _answer) { _hookWaitSaid = _answer; DiagLog.Write($"[dodge] 想往{dir}甩钩爪 但还在冷却 剩{_hookCooldown}帧"); }
				return false;
			}
			// 按一帧松一帧,vanilla 要 releaseHook 才认新按压
			if (_hookFrames++ > HookGiveUpFrames) return false;
			if (_hookHeld) { _hookHeld = false; return true; }
			var (x, y) = Vec(dir);
			Vector2 target = planned ?? default;
			if (_hookFrames == 1 && !(planned.HasValue && ValidHookPoint(p, target, x, y)) &&
				!HookTarget(p, x, y, y == 0, out target))
			{
				if (_hookMissSaid != _answer) { _hookMissSaid = _answer; DiagLog.Write($"[dodge] 想往{dir}甩钩爪 但射程内勾不到东西 退到Small"); }
				_hookFrames = 0;
				miss = true;
				return false;
			}
			if (_hookFrames == 1)
			{
				_anchorPx = target;
				DiagLog.Write($"[dodge] 甩钩爪 往{dir} 瞄第({(int)(target.X / 16f)},{(int)(target.Y / 16f)})格");
			}
			Cursor.AimTile((int)(_anchorPx.X / 16f), (int)(_anchorPx.Y / 16f));
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

		static bool HookTarget(Player p, int x, int y, bool nearHorizontal, out Vector2 target)
		{
			target = default;
			if (x == 0 && y == 0) return false;
			float range = HookRangePx(p);
			foreach (float tilt in nearHorizontal ? new[] { 0f, -0.35f, 0.35f } : new[] { 0f })
			{
				Vector2 step = Vector2.Normalize(new Vector2(x, nearHorizontal ? tilt : y)) * 4f;
				Vector2 pt = p.Center;
				for (float d = 0f; d <= range; d += 4f, pt += step)
				{
					int tx = (int)(pt.X / 16f), ty = (int)(pt.Y / 16f);
					if (!Predicates.InBounds(tx, ty)) break;
					if (!Hookable(tx, ty)) continue;
					Vector2 found = new(tx * 16f + 8f, ty * 16f + 8f);
					if (Vector2.DistanceSquared(p.Center, found) <= 6f * 16f * 6f * 16f) break;
					target = found;
					return true;
				}
			}
			return false;
		}
		static bool ValidHookPoint(Player p, Vector2 target, int x, int y)
		{
			Vector2 offset = target - p.Center;
			float distance = offset.Length();
			return distance > 6f * 16f && distance <= HookRangePx(p)
				&& (x == 0 || offset.X * x > 0f) && (y == 0 || offset.Y * y > 0f)
				&& Hookable((int)(target.X / 16f), (int)(target.Y / 16f));
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
			float closeX = boss.Center.X > p.Center.X ? p.velocity.X - boss.velocity.X : boss.velocity.X - p.velocity.X;
			float closeY = boss.Center.Y > p.Center.Y ? p.velocity.Y - boss.velocity.Y : boss.velocity.Y - p.velocity.Y;
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

		// 钩子能不能勾这一格,照 AI_007_GrapplingHooks_CanTileBeLatchedOnTo
		static bool Hookable(int x, int y)
		{
			if (!Predicates.InBounds(x, y)) return false;
			var t = Main.tile[x, y];
			if (!t.HasTile || t.IsActuated) return false;
			return Main.tileSolid[t.TileType] || t.TileType == Terraria.ID.TileID.MinecartTrack;
		}

		// 这一侧齐胸高度离墙几格,地图边缘也算墙
		static int WallDistance(Player p, int dir)
		{
			int edge = (dir < 0 ? p.Hitbox.Left : p.Hitbox.Right - 1) / 16;
			int top = p.Hitbox.Top / 16, bottom = (p.Hitbox.Bottom - 1) / 16;
			for (int d = 1; d <= RoomScanCells; d++)
			{
				int x = edge + dir * d;
				if (x <= EdgeCells || x >= Main.maxTilesX - EdgeCells) return d - 1;
				for (int y = top; y <= bottom; y++)
					if (Predicates.IsWall(x, y)) return d - 1;
			}
			return RoomScanCells;
		}

		// 头顶离天花板几格,地图上边缘也算
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

		static void Release()
		{
			_releasedAtMs = _clock.ElapsedMilliseconds;
			DismountAuto(Main.LocalPlayer);
			_appliedAction = DodgeAction.Idle;
			_completedAction = DodgeAction.Idle;
			_completedDurationMs = 0;
			_lastApplied = "";
			_jumpHeld = false;
			_holdFrames = _hookFrames = 0;
			_hookHeld = false;
			AxisLock.Release(Owner);
		}

		static string Facts(Player p, NPC boss)
		{
			int pcx = (int)(p.Center.X / 16f), pcy = (int)(p.Center.Y / 16f);
			return "{\"hp_percent\":" + (p.statLife * 100 / System.Math.Max(1, p.statLifeMax))
				 + ",\"i_am_losing_health_over_time\":" + (p.lifeRegen < 0 ? "true" : "false")
				 + ",\"my_speed_to_the_right\":" + (int)(p.velocity.X * 60f / 16f)
				 + ",\"my_speed_upward\":" + (int)(-p.velocity.Y * 60f / 16f)
				 + ",\"movement\":{\"can_extra_jump\":" + (p.AnyExtraJumpUsable() ? "true" : "false")
				 + ",\"can_fly\":" + (p.wingTime > 0f ? "true" : "false")
				 + ",\"vertical_mount\":" + Quote(EquippedVerticalMount(p) switch
				 { MountID.Slime => "Slimy Saddle", MountID.QueenSlime => "Gelatinous Pillion", _ => "none" }) + "}"
				 + ",\"threats\":" + ThreatsJson(p)
				 + ",\"incoming_projectiles\":" + ThreatScan.ProjJson(p, pcx, pcy)
				 + ",\"room\":{\"left\":" + WallDistance(p, -1) + ",\"right\":" + WallDistance(p, 1)
				 + ",\"above\":" + CeilingDistance(p) + ",\"below\":" + FloorDistance(p) + "}"
					 + ",\"previous_action\":" + Quote(ActionName(_completedAction))
					 + ",\"previous_action_ms\":" + _completedDurationMs
				 + ",\"boss_notes\":" + FieldBooks(boss)
				 + "}";
		}

		static string RouteDescription(Player p, int x, int y, Size hSize, Size vSize,
			int left, int right, int above, int below, List<NPC> threats, List<Projectile> shots)
		{
			float horizontal = x * (hSize == Size.Medium ? 20f : 12f);
			float verticalRoom = y < 0 ? above - UpperReserveCells - 5f : below - LowerReserveCells;
			float vertical = y * System.Math.Max(0f, System.Math.Min(vSize == Size.Large
				? (y < 0 ? 18f : 22f) : (y < 0 ? 10f : 18f), verticalRoom));
			Vector2 displacement = new(horizontal * 16f, vertical * 16f);
			Vector2 after = p.Center + displacement;
			var trends = new StringBuilder();
			foreach (NPC npc in threats)
			{
				float current = Vector2.Distance(npc.Center, p.Center);
				float future = Vector2.Distance(npc.Center + npc.velocity * 36f, after);
				if (trends.Length > 0) trends.Append("; ");
				trends.Append(npc.TypeName).Append(": ").Append(future - current > 5f * 16f ? "opens space"
					: future - current < -5f * 16f ? "closes space" : "little change");
			}
			int exposed = 0;
			foreach (Projectile shot in shots)
			{
				Vector2 relative = shot.Center - p.Center;
				Vector2 step = shot.velocity * (shot.extraUpdates + 1) - displacement / 36f;
				float speedSquared = step.LengthSquared();
				float frame = speedSquared < 0.01f ? 36f :
					System.Math.Clamp(-Vector2.Dot(relative, step) / speedSquared, 4f, 36f);
				if ((relative + step * frame).LengthSquared() < 10f * 16f * 10f * 16f) exposed++;
			}
			int horizontalRoom = x < 0 ? left : right;
			return $"Approximate displacement in 0.6s: {horizontal:0} cells right, {vertical:0} cells down. "
				+ $"Boss clearance if current motion continues: {(trends.Length == 0 ? "no nearby boss" : trends)}. "
				+ $"Projectile corridor: {(exposed == 0 ? "clear" : exposed < 3 ? "exposed" : "crowded")}. "
				+ $"Room beyond reserve: horizontal {(x == 0 ? 0 : System.Math.Max(0, horizontalRoom - HorizontalReserveCells))}, "
				+ $"vertical {(y == 0 ? 0 : System.Math.Max(0, (int)verticalRoom))} cells. ";
		}

		static string ActionCriteria(Player p)
		{
			int left = WallDistance(p, -1), right = WallDistance(p, 1);
			int above = CeilingDistance(p), below = FloorDistance(p);
			var sb = new StringBuilder();
			var blocked = new HashSet<Dir>();
			var options = new Dictionary<string, DodgeAction>();
			var nearest = new Dictionary<int, NPC>();
			var shots = new List<Projectile>();
			foreach (NPC npc in Main.npc)
				if (npc != null && npc.active && !npc.friendly && IsBossLike(npc) &&
					Vector2.DistanceSquared(npc.Center, p.Center) < 70f * 16f * 70f * 16f &&
					(!nearest.TryGetValue(npc.type, out NPC old) ||
					Vector2.DistanceSquared(npc.Center, p.Center) < Vector2.DistanceSquared(old.Center, p.Center)))
					nearest[npc.type] = npc;
			foreach (Projectile shot in Main.projectile)
				if (shot != null && shot.active && shot.hostile && shot.damage > 0 &&
					Vector2.DistanceSquared(shot.Center, p.Center) < 80f * 16f * 80f * 16f)
					shots.Add(shot);
			var threats = new List<NPC>(nearest.Values);
			int mountType = EquippedVerticalMount(p);
			(int X, Size Size)[] horizontal = { (0, Size.None), (-1, Size.Small), (-1, Size.Medium), (1, Size.Small), (1, Size.Medium) };
			(int Y, Size Size)[] vertical = { (0, Size.None), (-1, Size.Small), (-1, Size.Medium), (-1, Size.Large), (1, Size.Small), (1, Size.Large) };
			foreach (var (x, hSize) in horizontal)
			foreach (var (y, vSize) in vertical)
			{
				if (x == 0 && y == 0) continue;
				Dir d = Direction(x, y);
				if (Blocked(p, x, y, left, right, above, below))
				{
					blocked.Add(d);
					continue;
				}
				bool hookH = hSize == Size.Medium;
				bool hookV = y < 0 && vSize == Size.Medium;
				bool mount = vSize == Size.Large;
				if (mount && (mountType == MountID.None || p.mount.Active && !_autoMountActive) ||
					y > 0 && mount && hookH) continue;
				Vector2? target = null;
				if (hookH || hookV)
				{
					if (_hookCooldown > 0 || p.grapCount > 0 ||
						!HookTarget(p, hookH ? x : 0, hookV ? y : 0, hookH && !hookV, out Vector2 point)) continue;
					target = point;
				}
				var action = new DodgeAction(d, hSize, vSize, target);
				string name = ActionName(action);
				options[name] = action;
				string description = $"Move {d}. " + RouteDescription(p, x, y, hSize, vSize,
					left, right, above, below, threats, shots)
					+ (hookH && hookV ? $"Grapple diagonally toward a surface {Vector2.Distance(p.Center, target!.Value) / 16f:0.#} cells away. " :
						x == 0 ? "" : hookH ? $"Grapple horizontally toward a surface {Vector2.Distance(p.Center, target!.Value) / 16f:0.#} cells away. " : "Run horizontally. ")
					+ (y < 0 ? mount ? "Jump, then mount for a fast rise; dismount at the apex. " : hookV && !hookH ? "Grapple upward. " : hookV ? "" : "Jump or fly upward. "
						: y > 0 ? mount ? "Mount for a fast descent; dismount when descent ends. " : "Descend or pass through a platform. " : "")
					+ "Continue until the next answer.";
				sb.Append(Quote(name)).Append(':').Append(Quote(description)).Append(',');
			}
			options["Stay"] = DodgeAction.Idle;
			_availableActions = options;
			_lastBlocked = string.Join(" ", blocked);
			return sb.Append("\"Stay\":\"Stay only if no approaching threat needs movement.\"").ToString();
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
