using System.Net.Http;
using System.Text;
using Terraria;

namespace TerraBlind
{
	// Jev 给【意图】,不给按键。按键由下面那个每帧跑的反射层算
	public enum DodgeAct { Keep, Back, Close, Evade, Up, Float, Grapple }

	// boss 战的走位。【两层】:Jev 每 200ms 说"该拉开还是该贴脸",反射层每帧算
	// "这一刻往左还是往右、跳不跳"。让 250ms 的判断直接当按键,就是站着挨撞
	public static class Dodge
	{
		public static bool Enabled = false;
		const string Owner = "dodge";
		const int CareCells = 60;
		// 意图过期就退回保守行为。拿 3 秒前的判断当真比没有判断更糟
		const long IntentTtlMs = 1500;

		// 二段跳:落地才回充,空中再按一次触发,而且【必须松一帧】才算新按压
		static bool _airJumpUsed;
		static bool _jumpHeld;
		static int _holdFrames;
		// 按住的兜底上限。正常到顶就松了,这个数只防"某种状态下一直升不完"
		const int MaxHoldFrames = 30;
		// 钩爪甩出去这么多帧还没勾上就放弃,别一直按着钩子不打人
		const int HookGiveUpFrames = 45;
		static int _hookFrames;
		static bool _hookHeld;
		// 飘太久就强制落地。羽落 + 按住 up 能悬到天荒地老,而悬着既打不到 boss
		// 也躲不开从上面压下来的东西 -- 判据只有一条:能不能躲开 boss
		const int MaxAirborneFrames = 150;
		static int _airborneFrames;
		static bool _tooLongAirborne;

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

		static NPC Boss(Player p, out int dist)
		{
			dist = -1;
			int pcx = (int)(p.Center.X / 16f), pcy = (int)(p.Center.Y / 16f);
			for (int i = 0; i < Main.maxNPCs; i++)
			{
				var npc = Main.npc[i];
				if (npc == null || !npc.active || !npc.boss || npc.friendly) continue;
				int d = System.Math.Abs((int)(npc.Center.X / 16f) - pcx)
					  + System.Math.Abs((int)(npc.Center.Y / 16f) - pcy);
				if (d > CareCells) continue;
				dist = d;
				return npc;
			}
			return null;
		}

		public static void Tick()
		{
			if (!Enabled) return;
			var p = Main.LocalPlayer;
			if (p == null || !p.active || p.dead) { Release(); return; }

			var boss = Boss(p, out int dist);
			if (boss == null) { Last = "没有boss"; Release(); return; }

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

			// Vertical 也要:羽落靠按住 up 才慢降
			if (!AxisLock.Take(Owner, Ax.Move | Ax.Jump | Ax.Vertical, () => Enabled))
			{ Last = "Move 抢不到:" + AxisLock.Held(Ax.Move); return; }

			Drive(p, boss, dist, act);
		}

		// 危险分换成格数。0=贴脸输出,4=离远点
		static int WantCells(float danger) => 4 + (int)(danger * 4f);

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
			else if (++_airborneFrames > MaxAirborneFrames) _tooLongAirborne = true;

			int want = WantCells(Danger);
			// 【撞上还有几帧】。躲晚不是因为判断慢,是因为收到意图那一刻才跳一次 --
			// 该跳的时机在那之后。所以每帧自己算,不等下一个意图
			int framesToHit = FramesToHit(p, boss);
			// 危险时提前跳,安全时晚点跳。原来这也是个写死的 12
			int soon = 8 + (int)(Danger * 4f);
			bool incoming = framesToHit >= 0 && framesToHit <= soon;

			switch (act)
			{
				// 【别"够远就停"】。克苏鲁之眼是冲撞型,站定就是等撞 --
				// 它锁的是冲刺开始那一刻的位置,横向一直有速度才躲得开
				case DodgeAct.Back:
					go = away;
					break;
				case DodgeAct.Close:
					if (dist > want / 2) go = toward;
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
				case DodgeAct.Keep:
					if (dist < want / 2) go = away;
					break;
			}

			// 钩爪:发射 → 勾住 → 【必须跳一次取消】。跳完拿到那段速度,二段跳也回来了
			bool hooking = Hook(p, boss, act, onGround, out bool hookJump);

			// 跳的时机归反射层。Evade/Up 是"该闪",快撞上才是"现在闪"
			bool wantJump = hookJump || act == DodgeAct.Up || JevSaysJump || incoming;
			bool jump = Jump(p, onGround, wantJump);

			if (go < 0) p.controlLeft = true;
			else if (go > 0) p.controlRight = true;
			if (jump) p.controlJump = true;

			// 【羽落只在要躲的时候按】。离地就按住 up 的话下落只剩 1/10,等于永不落地 --
			// 落不了地二段跳就永远不回充,人就一直挂在天上。钩爪那一下要按,拉高度靠它
			bool hover = act == DodgeAct.Float || act == DodgeAct.Up || hooking || incoming;
			if (!onGround && hover && !_tooLongAirborne) p.controlUp = true;

			Last = $"{act} boss在{(bossRight ? "右" : "左")}{dist}格(想要{want}) 走{(go == 0 ? "停" : go < 0 ? "左" : "右")}"
				 + (jump ? (onGround ? "+跳" : "+二段") : "") + (hooking ? "+钩" : "")
				 + (!onGround && act != DodgeAct.Close ? "+飘" : "")
				 + (incoming ? $" 撞击{framesToHit}帧" : "")
				 + (TacticWorking ? "" : " [这套没用]");
		}

		// 钩爪。【勾住之后一定要跳一次】,否则会被直接拉过去,那就不是位移是送死。
		// 光标是全局的,攻击层每帧在瞄 boss -- 只有发射那一帧抢过来指个方向,之后不用再指
		static bool Hook(Player p, NPC boss, DodgeAct act, bool onGround, out bool hookJump)
		{
			hookJump = false;
			if (p.grapCount > 0)
			{
				// 勾住了,这一帧就跳。跳完二段跳重置,等于白赚一次滞空
				hookJump = true;
				_airJumpUsed = false;
				_hookFrames = 0;
				return true;
			}
			if (act != DodgeAct.Grapple) { _hookFrames = 0; return false; }
			// 【钩爪也要松一帧】。vanilla 是 if(controlHook){ if(releaseHook) 发射; releaseHook=false; }
			// else releaseHook=true -- 一直按住只发射一次,之后全是空按。和二段跳同一个坑
			if (_hookFrames++ > HookGiveUpFrames) return false;
			if (_hookHeld) { _hookHeld = false; return true; }
			// 【往上勾】。配合羽落按住上键,跳取消之后能飞很高,整片地面攻击都躲得掉。
			// 横向只带一点,躲开 boss 那一侧;主要是拿高度
			float dir = boss.Center.X > p.Center.X ? -1f : 1f;
			Cursor.AimPx(p.Center.X + dir * 16f * 6f, p.Center.Y - 16f * 16f);
			p.controlHook = true;
			_hookHeld = true;
			return true;
		}

		// 【按住到上升结束,不数帧】。按满才跳得最高,而每种跳的满按时长不一样,
		// 硬编码必错。velocity.Y 转正那一刻就是到顶,这个判据对两种跳都成立
		static bool Jump(Player p, bool onGround, bool want)
		{
			if (onGround) { _airJumpUsed = false; _holdFrames = 0; }

			bool rising = p.velocity.Y < 0f;
			if (_jumpHeld && rising && _holdFrames < MaxHoldFrames)
			{ _holdFrames++; _jumpHeld = true; return true; }

			// 到顶了就松开。松开这一帧本身也是二段跳要的"新按压"前置
			if (_jumpHeld) { _jumpHeld = false; _holdFrames = 0; return false; }

			if (!want) return false;
			if (onGround) { _jumpHeld = true; _holdFrames = 1; return true; }
			if (!_airJumpUsed) { _airJumpUsed = true; _jumpHeld = true; _holdFrames = 1; return true; }
			return false;
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

		// 脚下到最近一块实心的格数。往下探够 MaxAirborneFrames 那点高度就行,
		// 探不到就报这个上限 -- "很高"和"极高"对走位是一回事
		static int CellsAboveGround(Player p)
		{
			int cx = (int)(p.Center.X / 16f);
			int feet = (int)((p.position.Y + p.height) / 16f);
			for (int d = 0; d < 40; d++)
				if (Predicates.IsSolid(cx, feet + d)) return d;
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
				 + ",\"boss_cells_vertical\":" + (int)dy
				 + ",\"boss_coming_at_me\":" + (closing ? "true" : "false")
				 + ",\"frames_until_it_hits_me\":" + (hit < 0 ? "\"它没朝我来\"" : hit.ToString())
				 + ",\"contact_damage_percent_of_my_hp\":" + (boss.damage * 100 / System.Math.Max(1, p.statLife))
				 + ",\"i_am_airborne\":" + (p.velocity.Y != 0f ? "true" : "false")
				 // 【飘了多久、离地多高】。原来只说"在空中",于是它每次都在答
				 // "现在要不要滞空",而不是"要不要继续滞空" -- 悬了三秒也看不出来
				 + ",\"frames_airborne\":" + _airborneFrames
				 + ",\"cells_above_ground\":" + CellsAboveGround(p)
				 + ",\"double_jump_ready\":" + (!_airJumpUsed ? "true" : "false")
				 // 【弹幕也要看见】。只扫 NPC 的话,打得到我的东西有一半不在视野里
				 + ",\"incoming_projectiles\":" + ThreatScan.ProjJson(p, pcx, pcy)
				 + ",\"other_enemies\":" + ThreatScan.Json(p, pcx, pcy)
				 + ",\"my_weapon_fires_by_itself\":true"
				 + ",\"arena\":\"一整片平台,左右都能跑,没有坑也没有墙。站着不动就会被撞\""
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
			 + "\"Close\":\"靠近一点。它飞远了打不到,或者它现在不动正好多打几下\","
			 + "\"Evade\":\"横向闪开。它已经贴脸或者马上要撞上,先把这一下躲过去\","
			 + "\"Up\":\"往上跳。它从下方上来,或者该上更高一层平台\","
			 + "\"Float\":\"【只为躲开眼前这一下】跳起来滞空,让贴着地面冲过来的那一击从脚下穿过去。"
			 + "看 frames_airborne:已经飘了一阵子说明那一下早就过去了,该落地跑动而不是接着飘。"
			 + "飘在半空移动慢、够不着它、也躲不开从上面压下来的东西\","
			 + "\"Grapple\":\"甩钩爪。往上勾,勾住的瞬间跳起来取消,配合羽落按住上键能飞得很高,"
			 + "整片地面攻击都躲得掉,而且二段跳会重置。想快速脱离险境或者拉高度时用\"}},"
			 + "\"danger\":{\"type\":\"score\",\"instructions\":"
			 + "\"眼下有多危险,决定它该离 boss 多远。越危险越该拉开。\",\"criteria\":["
			 + "\"很安全,可以贴上去输出\",\"一般,保持中距\",\"有点险,拉开一些\","
			 + "\"很险,离远点\",\"随时会死,能躲多远躲多远\"]},"
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
				_ => DodgeAct.Keep,
			};
			_actAt = _clock.ElapsedMilliseconds;

			// Noul 【没有 confidence】,概率本身就是答案。0.7 当"是"
			Danger = Num(Seg(txt, "danger"), "score", Danger);
			JevSaysJump = Num(Seg(txt, "should_jump_now"), "noul", 0f) > 0.7f;
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
