using System.Net.Http;
using System.Text;
using Terraria;

namespace TerraBlind
{
	// Jev 给【意图】,不给按键。按键由下面那个每帧跑的反射层算
	public enum DodgeAct { Keep, Back, Close, Evade, Up }

	// boss 战的走位。【两层】:Jev 每 200ms 说"该拉开还是该贴脸",反射层每帧算
	// "这一刻往左还是往右、跳不跳"。让 250ms 的判断直接当按键,就是站着挨撞
	public static class Dodge
	{
		public static bool Enabled = false;
		const string Owner = "dodge";
		const int CareCells = 60;
		// 意图过期就退回保守行为。拿 3 秒前的判断当真比没有判断更糟
		const long IntentTtlMs = 1500;
		// 贴脸/拉开各自的舒适距离(格)
		const int CloseCells = 6, BackCells = 18;
		// 还有这么多帧就撞上,该跳了。60fps 下 12 帧 = 0.2 秒,够起跳也不至于跳太早
		const int HitSoonFrames = 12;

		// 二段跳:落地才回充,空中再按一次触发,而且【必须松一帧】才算新按压
		static bool _airJumpUsed;
		static bool _jumpHeld;

		public static string Last = "idle";
		public static DodgeAct Act = DodgeAct.Back;
		public static float Confidence;
		public static int LatencyMs;

		public const string Url = "https://api.typesafe.ai/v1/systemone";
		public const string Model = "jev-latest";
		static readonly HttpClient _http = new() { Timeout = System.TimeSpan.FromSeconds(5) };
		static volatile bool _busy;
		static volatile string _pending;
		static string _lastSig = "";
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
			if (key != null && !_busy) Fire(key, Facts(p, boss, dist));

			// 意图过期:退回"拉开距离",那是任何时候都不会送命的默认
			var act = _clock.ElapsedMilliseconds - _actAt > IntentTtlMs ? DodgeAct.Back : Act;

			if (!AxisLock.Take(Owner, Ax.Move | Ax.Jump, () => Enabled))
			{ Last = "Move 抢不到:" + AxisLock.Held(Ax.Move); return; }

			Drive(p, boss, dist, act);
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
			if (onGround) _airJumpUsed = false;   // 落地回充二段

			// 【撞上还有几帧】。躲晚不是因为判断慢,是因为收到意图那一刻才跳一次 --
			// 该跳的时机在那之后。所以每帧自己算,不等下一个意图
			int framesToHit = FramesToHit(p, boss);
			bool incoming = framesToHit >= 0 && framesToHit <= HitSoonFrames;

			switch (act)
			{
				case DodgeAct.Back:
					if (dist < BackCells) go = away;
					break;
				case DodgeAct.Close:
					if (dist > CloseCells) go = toward;
					break;
				case DodgeAct.Evade:
					go = away;
					break;
				case DodgeAct.Up:
					break;
				case DodgeAct.Keep:
					if (dist < CloseCells) go = away;
					break;
			}

			// 跳的时机归反射层。Evade/Up 是"该闪",快撞上才是"现在闪"
			bool wantJump = act == DodgeAct.Up
				|| (act == DodgeAct.Evade && incoming)
				|| incoming;

			bool jump = false;
			if (wantJump)
			{
				// 【松一帧再按】。连着按住,游戏不认第二次按压,二段跳永远放不出来
				if (_jumpHeld) jump = false;
				else if (onGround) jump = true;
				else if (!_airJumpUsed) { jump = true; _airJumpUsed = true; }
			}

			if (go < 0) p.controlLeft = true;
			else if (go > 0) p.controlRight = true;
			if (jump) p.controlJump = true;
			_jumpHeld = jump;

			Last = $"{act} boss在{(bossRight ? "右" : "左")}{dist}格 走{(go == 0 ? "停" : go < 0 ? "左" : "右")}"
				 + (jump ? (onGround ? "+跳" : "+二段") : "") + (incoming ? $" 撞击{framesToHit}帧" : "");
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

		static void Release() => AxisLock.Release(Owner);

		// 【给方向不给标量】。ThreatScan 那份 speed 是绝对值,丢了符号,
		// 而走位要判的正是"它朝哪飞"
		static string Facts(Player p, NPC boss, int dist)
		{
			float dx = (boss.Center.X - p.Center.X) / 16f;
			float dy = (boss.Center.Y - p.Center.Y) / 16f;
			bool closing = (dx > 0 && boss.velocity.X < 0) || (dx < 0 && boss.velocity.X > 0);
			int hit = FramesToHit(p, boss);
			// 【给它能直接用的量】。vx=7.3 这种像素/帧模型没有尺度感,
			// "还有 14 帧撞上"才是能拿来决定闪不闪的数
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
				 + ",\"double_jump_ready\":" + (!_airJumpUsed ? "true" : "false")
				 + ",\"my_weapon_fires_by_itself\":true"
				 + ",\"arena\":\"一整片平台,左右都能跑,没有坑也没有墙。站着不动就会被撞\""
				 + "}";
		}

		static string Body(string state)
			=> "{\"model\":\"" + Model + "\",\"state\":" + Quote(state) + ",\"questions\":{"
			 + "\"intent\":{\"type\":\"choice\",\"instructions\":"
			 + "\"泰拉瑞亚 boss 战。这个自动玩家的武器会自己瞄准开火,所以它只要决定走位。"
			 + "boss 撞到身上才掉血。【说的是意图不是按键】,具体往左往右由下面的代码每帧算。\",\"criteria\":{"
			 + "\"Keep\":\"保持现在的位置。够得着打,又没有被逼近,站稳输出\","
			 + "\"Back\":\"拉开距离。它正冲过来,或者血不多了要留余地\","
			 + "\"Close\":\"靠近一点。它飞远了打不到,或者它现在不动正好多打几下\","
			 + "\"Evade\":\"横向闪开。它已经贴脸或者马上要撞上,先把这一下躲过去\","
			 + "\"Up\":\"往上跳。它从下方上来,或者该上更高一层平台\"}}"
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
			string pick = Field(txt, "choice");
			if (pick == null) return;
			float.TryParse(Field(txt, "confidence"), out float c);
			LatencyMs = ms;
			Confidence = c;
			Act = pick switch
			{
				"Back" => DodgeAct.Back,
				"Close" => DodgeAct.Close,
				"Evade" => DodgeAct.Evade,
				"Up" => DodgeAct.Up,
				_ => DodgeAct.Keep,
			};
			_actAt = _clock.ElapsedMilliseconds;
			string sig = pick + "|" + c.ToString("0.00");
			if (sig != _lastSig)
			{
				_lastSig = sig;
				JevLog.Add(new JevLog.Entry
				{
					Ms = _clock.ElapsedMilliseconds,
					Site = "dodge",
					State = "",
					Pick = pick,
					Confidence = c,
					Probs = "",
					Why = "jev",
					LatencyMs = ms,
				});
			}
		}

		static string Field(string s, string name)
		{
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
