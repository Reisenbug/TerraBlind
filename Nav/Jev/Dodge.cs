using System.Net.Http;
using System.Text;
using Terraria;

namespace TerraBlind
{
	public enum DodgeAct { Stand, Left, Right, JumpLeft, JumpRight, Jump }

	// boss 战的移动。【不走寻路】:场子是平的,没有地形问题,要判的只有
	// "它在哪、朝哪冲、我该往哪闪"。形状照 JevCombat:后台发请求,主线程拿上一次的结果
	public static class Dodge
	{
		public static bool Enabled = false;
		const string Owner = "dodge";
		// 这么多格以内才谈得上躲。再远它还没冲过来,站着挥就行
		const int CareCells = 60;

		public static string Last = "idle";
		public static DodgeAct Act = DodgeAct.Stand;
		public static float Confidence;
		public static int LatencyMs;

		public const string Url = "https://api.typesafe.ai/v1/systemone";
		public const string Model = "jev-latest";
		static readonly HttpClient _http = new() { Timeout = System.TimeSpan.FromSeconds(5) };
		static volatile bool _busy;
		static volatile string _pending;
		static string _lastSig = "";
		static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

		// 【读一次记住】。每帧跑的东西不能碰磁盘
		static string _key;
		static string Key()
		{
			if (_key != null) return _key.Length == 0 ? null : _key;
			try { _key = System.IO.File.Exists(JevCombat.KeyPath) ? System.IO.File.ReadAllText(JevCombat.KeyPath).Trim() : ""; }
			catch { _key = ""; }
			return _key.Length == 0 ? null : _key;
		}

		static int Boss(Player p, out NPC found)
		{
			found = null;
			int pcx = (int)(p.Center.X / 16f), pcy = (int)(p.Center.Y / 16f);
			for (int i = 0; i < Main.maxNPCs; i++)
			{
				var npc = Main.npc[i];
				if (npc == null || !npc.active || !npc.boss || npc.friendly) continue;
				int d = System.Math.Abs((int)(npc.Center.X / 16f) - pcx)
					  + System.Math.Abs((int)(npc.Center.Y / 16f) - pcy);
				if (d > CareCells) continue;
				found = npc;
				return d;
			}
			return -1;
		}

		public static void Tick()
		{
			if (!Enabled) return;
			var p = Main.LocalPlayer;
			if (p == null || !p.active || p.dead) { Release(); return; }

			int dist = Boss(p, out var boss);
			if (boss == null) { Last = "没有boss"; Act = DodgeAct.Stand; Release(); return; }

			var done = _pending;
			if (done != null) { _pending = null; Parse(done); }
			string key = Key();
			if (key != null && !_busy) Fire(key, Facts(p, boss, dist));

			// 移动和跳,攻击那边只占 Use,互不干扰
			if (!AxisLock.Take(Owner, Ax.Move | Ax.Jump, () => Enabled))
			{ Last = "Move 抢不到:" + AxisLock.Held(Ax.Move); return; }

			switch (Act)
			{
				case DodgeAct.Left: p.controlLeft = true; break;
				case DodgeAct.Right: p.controlRight = true; break;
				case DodgeAct.JumpLeft: p.controlLeft = true; p.controlJump = true; break;
				case DodgeAct.JumpRight: p.controlRight = true; p.controlJump = true; break;
				case DodgeAct.Jump: p.controlJump = true; break;
			}
			Last = $"{Act} 离boss{dist}格 血{p.statLife}/{p.statLifeMax}";
		}

		static void Release() => AxisLock.Release(Owner);

		// 【给方向不给标量】。ThreatScan 那份 speed 是绝对值,丢了符号,
		// 而躲避要判的正是"它朝哪飞"
		static string Facts(Player p, NPC boss, int dist)
		{
			float dx = (boss.Center.X - p.Center.X) / 16f;
			float dy = (boss.Center.Y - p.Center.Y) / 16f;
			bool closing = (dx > 0 && boss.velocity.X < 0) || (dx < 0 && boss.velocity.X > 0);
			return "{\"hp\":" + p.statLife + ",\"hp_max\":" + p.statLifeMax
				 + ",\"hp_percent\":" + (p.statLife * 100 / System.Math.Max(1, p.statLifeMax))
				 + ",\"on_ground\":" + (p.velocity.Y == 0f ? "true" : "false")
				 + ",\"boss\":\"" + JsonStr(boss.TypeName) + "\""
				 + ",\"boss_hp_percent\":" + (boss.life * 100 / System.Math.Max(1, boss.lifeMax))
				 + ",\"boss_is\":\"" + (dx > 0 ? "右边" : "左边") + (System.Math.Abs(dy) < 3 ? "" : (dy > 0 ? "下方" : "上方")) + "\""
				 + ",\"boss_cells_right\":" + (int)dx
				 + ",\"boss_cells_below\":" + (int)dy
				 + ",\"boss_distance_cells\":" + dist
				 + ",\"boss_vx\":" + boss.velocity.X.ToString("0.0")
				 + ",\"boss_vy\":" + boss.velocity.Y.ToString("0.0")
				 + ",\"boss_charging_at_me\":" + (closing ? "true" : "false")
				 + ",\"boss_damage\":" + boss.damage
				 + ",\"my_vx\":" + p.velocity.X.ToString("0.0")
				 + ",\"arena\":\"一整片平台,左右都能跑,没有坑\""
				 + "}";
		}

		static string Body(string state)
			=> "{\"model\":\"" + Model + "\",\"state\":" + Quote(state) + ",\"questions\":{"
			 + "\"move\":{\"type\":\"choice\",\"instructions\":"
			 + "\"泰拉瑞亚 boss 战。这个自动玩家站在一整片平台搭的战斗场上,武器会自己瞄准开火,"
			 + "所以它只需要决定怎么走位躲开 boss 的冲撞。boss 撞到身上才掉血,拉开距离就安全。"
			 + "选它这一刻最该做的动作。\",\"criteria\":{"
			 + "\"Stand\":\"站着不动。boss 还远,或者它正飞开,没必要动\","
			 + "\"Left\":\"往左跑。boss 在右边,拉开距离\","
			 + "\"Right\":\"往右跑。boss 在左边,拉开距离\","
			 + "\"JumpLeft\":\"往左跳。boss 贴着地面冲过来,跳起来同时往左闪\","
			 + "\"JumpRight\":\"往右跳。boss 贴着地面冲过来,跳起来同时往右闪\","
			 + "\"Jump\":\"原地跳。boss 从下方上来,或者要跳上更高一层平台\"}}"
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
				"Left" => DodgeAct.Left,
				"Right" => DodgeAct.Right,
				"JumpLeft" => DodgeAct.JumpLeft,
				"JumpRight" => DodgeAct.JumpRight,
				"Jump" => DodgeAct.Jump,
				_ => DodgeAct.Stand,
			};
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
