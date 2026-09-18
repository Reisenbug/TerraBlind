using System.Net.Http;
using System.Text;
using Terraria;

namespace TerraBlind
{
	// 真正问 Jev 的 brain。【后台发请求,主线程拿上一次的结果】-- 形状照 TrapEscape,
	// 游戏主线程等 70ms 就是掉帧
	public class JevCombat : ICombatBrain
	{
		public const string Url = "https://api.typesafe.ai/v1/systemone";
		public const string Model = "jev-latest";
		// key 放在家目录,不进仓库也不进模组配置
		public static string KeyPath => System.IO.Path.Combine(
			System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".typesafe_key");

		static readonly HttpClient _http = new() { Timeout = System.TimeSpan.FromSeconds(5) };
		static readonly CombatBaseline _fallback = new();
		static string _key;
		static volatile bool _busy;
		static volatile string _pending;     // 后台写完整一份才赋值,读到非 null 就是完整的
		static CombatCall _last;
		static bool _haveLast;
		static int _sent, _failed;

		public static string Stats => $"发了{_sent}次 失败{_failed}次 {(HasKey ? "key已加载" : "没有key")}";
		public static bool HasKey => Key() != null;

		// 配置 > 环境变量 > 文件。【配置不进缓存】:游戏里随时能改,缓存了就要重开才生效
		static string Key()
		{
			string cfg = Config.I?.TypeSafeKey?.Trim();
			if (!string.IsNullOrEmpty(cfg)) { Warn(cfg, "模组配置"); return cfg; }
			if (_key != null) return _key.Length == 0 ? null : _key;
			_key = System.Environment.GetEnvironmentVariable("TYPESAFE_API_KEY")?.Trim() ?? "";
			string from = _key.Length > 0 ? "TYPESAFE_API_KEY" : null;
			if (_key.Length == 0)
			{
				try { _key = System.IO.File.Exists(KeyPath) ? System.IO.File.ReadAllText(KeyPath).Trim() : ""; }
				catch { _key = ""; }
				if (_key.Length == 0)
					DiagLog.Write($"[jev] 没有 key。填进模组配置,或设 TYPESAFE_API_KEY,或放一个在 {KeyPath}");
				else from = KeyPath;
			}
			if (from != null) Warn(_key, from);
			return _key.Length == 0 ? null : _key;
		}

		// build.txt 里 includeSource=true,.tmod 会连源码一起发出去。【写死在代码里的 key 会跟着发布】
		static bool _warned;
		static void Warn(string key, string from)
		{
			if (_warned) return;
			_warned = true;
			DiagLog.Write($"[jev] key 来自 {from},长度 {key.Length}");
			foreach (var f in typeof(JevCombat).GetFields(System.Reflection.BindingFlags.Static
				| System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public))
			{
				if (f.FieldType != typeof(string) || !f.IsLiteral) continue;
				string v = f.GetRawConstantValue() as string;
				if (v != null && v.Length > 20 && (v.StartsWith("sk-") || v == key))
					DiagLog.Write($"[jev] 警告:常量 {f.Name} 看着像 key。includeSource=true,发布会把它一起带走");
			}
		}

		// 问题措辞固定不变,写死在这儿好让人审。criteria 的 key 和 CombatAct 一一对应
		static string Body(string state)
			=> "{\"model\":\"" + Model + "\",\"state\":" + Quote(state) + ",\"questions\":{"
			 + "\"act\":{\"type\":\"choice\",\"instructions\":"
			 + "\"泰拉瑞亚里一个自动玩家正在赶路,附近出现了敌人。它手上的武器是星怒,全屏攻击,隔着方块也打得到。"
			 + "根据敌人的威胁和它自己的血量,选它现在最该做的一件事。\",\"criteria\":{"
			 + "\"Fight\":\"转身打。敌人够得着、打得过,或者不打就会一直挨打\","
			 + "\"Ignore\":\"不理它,继续赶路。太远、或者威胁小到不值得停下来\","
			 + "\"Flee\":\"走开。血少了,或者这群敌人打不过\","
			 + "\"WallOff\":\"用方块把自己封起来躲开\","
			 + "\"Heal\":\"先回血\"}},"
			 + "\"interrupt_placement\":{\"type\":\"noul\",\"instructions\":"
			 + "\"这个威胁大到值得中断手上正在进行的方块放置吗?放置中断会留下半截工程。\"}"
			 + "}}";

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

		public CombatCall Decide(Player p, int tcx, int tcy, int dist, bool workBusy)
		{
			var done = _pending;
			if (done != null) { _pending = null; Parse(done); }
			string key = Key();
			if (key != null && !_busy) Fire(key, Facts(p, tcx, tcy, dist, workBusy));
			// 结果还没回来就先用 baseline,绝不阻塞主线程
			return _haveLast ? _last : _fallback.Decide(p, tcx, tcy, dist, workBusy);
		}

		static string Facts(Player p, int tcx, int tcy, int dist, bool workBusy)
			=> "{\"hp\":" + p.statLife + ",\"hp_max\":" + p.statLifeMax
			 + ",\"hp_percent\":" + (p.statLife * 100 / System.Math.Max(1, p.statLifeMax))
			 + ",\"player_cell\":[" + (int)(p.Center.X / 16f) + "," + (int)(p.Center.Y / 16f) + "]"
			 + ",\"placing_now\":" + (workBusy ? "true" : "false")
			 + ",\"weapon\":\"Star Wrath, hits the whole screen, ignores walls\""
			 + ",\"enemies\":" + ThreatScan.Json(p, (int)(p.Center.X / 16f), (int)(p.Center.Y / 16f))
			 + "}";

		static void Fire(string key, string state)
		{
			_busy = true;
			_sent++;
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
					if (!res.IsSuccessStatusCode)
					{
						_failed++;
						DiagLog.Write($"[jev] HTTP {(int)res.StatusCode} {txt.Substring(0, System.Math.Min(200, txt.Length))}");
					}
					else _pending = sw.ElapsedMilliseconds + "" + txt;
				}
				catch (System.Exception e) { _failed++; DiagLog.Write($"[jev] 请求炸了 {e.GetType().Name} {e.Message}"); }
				finally { _busy = false; }
			});
		}

		// 手写取值:mod 里没有 JSON 库,而要读的就三个字段
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

		static void Parse(string packed)
		{
			int cut = packed.IndexOf('');
			int ms = 0;
			string txt = packed;
			if (cut > 0) { int.TryParse(packed.Substring(0, cut), out ms); txt = packed.Substring(cut + 1); }

			string act = Field(txt, "choice");
			string conf = Field(txt, "confidence");
			string noul = Field(txt, "noul");
			if (act == null) { DiagLog.Write($"[jev] 读不出 choice: {txt.Substring(0, System.Math.Min(200, txt.Length))}"); return; }

			var call = new CombatCall { LatencyMs = ms, Why = "jev" };
			call.Act = act switch
			{
				"Fight" => CombatAct.Fight,
				"Flee" => CombatAct.Flee,
				"WallOff" => CombatAct.WallOff,
				"Heal" => CombatAct.Heal,
				_ => CombatAct.Ignore,
			};
			float.TryParse(conf, out float c);
			call.Confidence = c;
			call.InterruptWork = noul != null && float.TryParse(noul, out float nv) && nv > 0.7f;
			call.Probs = Probs(txt);
			// 置信太低就不听它的,退回 baseline。0.6 和 python 侧 fastjudge.CONF_ACT 一个数:
			// 实测 45 血遇恶魔眼它自己也只有 0.51~0.56,那种判断不值得照着动
			if (c > 0f && c < 0.6f) { call.Why = $"jev confidence {c:0.00} too low"; _haveLast = false; return; }
			_last = call; _haveLast = true;
		}

		// "Fight:0.72,Ignore:0.21" 这样一行,给观测页看
		static string Probs(string txt)
		{
			int i = txt.IndexOf("\"probabilities\"", System.StringComparison.Ordinal);
			if (i < 0) return "";
			int a = txt.IndexOf('{', i), b = txt.IndexOf('}', a < 0 ? i : a);
			if (a < 0 || b < 0) return "";
			return txt.Substring(a + 1, b - a - 1).Replace("\"", "").Replace(":", ":").Trim();
		}
	}
}
