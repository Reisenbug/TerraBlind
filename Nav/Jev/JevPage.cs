using System.Text;

namespace TerraBlind
{
	// http://127.0.0.1:17878/jev 。页面自己每秒拉一次 /jev_log,不用手动刷
	public static class JevPage
	{
		static string Esc(string s)
		{
			if (string.IsNullOrEmpty(s)) return "";
			return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", "");
		}

		public static string Json()
		{
			var rows = JevLog.Snapshot();
			var sb = new StringBuilder("{\"count\":").Append(rows.Count).Append(",\"entries\":[");
			// 新的在前:现场要看最近发生了什么
			for (int i = rows.Count - 1, n = 0; i >= 0; i--, n++)
			{
				if (n > 0) sb.Append(',');
				var e = rows[i];
				sb.Append("{\"ms\":").Append(e.Ms)
				  .Append(",\"site\":\"").Append(Esc(e.Site)).Append('"')
				  .Append(",\"pick\":\"").Append(Esc(e.Pick)).Append('"')
				  .Append(",\"confidence\":").Append(e.Confidence.ToString("0.000"))
				  .Append(",\"probs\":\"").Append(Esc(e.Probs)).Append('"')
				  .Append(",\"why\":\"").Append(Esc(e.Why)).Append('"')
				  .Append(",\"latency_ms\":").Append(e.LatencyMs)
				  .Append(",\"state\":\"").Append(Esc(e.State)).Append('"')
				  .Append('}');
			}
			return sb.Append("]}").ToString();
		}

		// 单文件页面,不引任何外部资源:游戏机器上未必有网,而且断网时更需要看这个
		public static string Html() => @"<!doctype html><html><head><meta charset=""utf-8"">
<title>TerraBlind / Jev</title><style>
body{background:#14161a;color:#d8dee9;font:13px ui-monospace,Menlo,Consolas,monospace;margin:0;padding:16px}
h1{font-size:15px;margin:0 0 4px;font-weight:600}
.sub{color:#7a8595;margin-bottom:14px}
.sub b{color:#a3be8c}
button{background:#2a2f3a;color:#d8dee9;border:1px solid #3b424f;border-radius:4px;padding:4px 10px;font:inherit;cursor:pointer}
button:hover{background:#353b48}
table{border-collapse:collapse;width:100%}
th{text-align:left;color:#7a8595;font-weight:500;border-bottom:1px solid #2a2f3a;padding:6px 8px;position:sticky;top:0;background:#14161a}
td{padding:5px 8px;border-bottom:1px solid #1e222a;vertical-align:top}
tr:hover{background:#181c22}
.pick{color:#88c0d0;font-weight:600}
.base{color:#616e88}
.conf{color:#a3be8c}
.low{color:#bf616a}
.state{color:#6b7484;font-size:11px;word-break:break-all;max-width:640px}
.why{color:#d0b070}
.empty{color:#616e88;padding:24px 8px}
</style></head><body>
<h1>Jev 判断</h1>
<div class=""sub"">每秒自动刷新 &middot; <b id=""n"">0</b> 条 &middot; <span class=""base"">灰色 = baseline,没真发请求</span>
&nbsp; <button onclick=""clr()"">清空</button></div>
<table><thead><tr><th>时刻</th><th>调用点</th><th>选择</th><th>置信</th><th>分布</th><th>理由</th><th>耗时</th><th>现场</th></tr></thead>
<tbody id=""b""><tr><td colspan=""8"" class=""empty"">还没有判断。跑一局,贪心卡住时这里会出现。</td></tr></tbody></table>
<script>
function fmt(ms){var s=Math.floor(ms/1000);return Math.floor(s/60)+':'+String(s%60).padStart(2,'0');}
function clr(){fetch('/jev_clear',{method:'POST'}).then(load);}
function load(){fetch('/jev_log').then(r=>r.json()).then(d=>{
  document.getElementById('n').textContent=d.count;
  var b=document.getElementById('b');
  if(!d.entries.length){b.innerHTML='<tr><td colspan=""8"" class=""empty"">还没有判断。跑一局,贪心卡住时这里会出现。</td></tr>';return;}
  b.innerHTML=d.entries.map(e=>{
    var base=e.latency_ms<0;
    var conf=base?'<span class=""base"">--</span>'
      :'<span class=""'+(e.confidence<0.6?'low':'conf')+'"">'+e.confidence.toFixed(2)+'</span>';
    return '<tr><td>'+fmt(e.ms)+'</td><td>'+e.site+'</td>'
      +'<td class=""'+(base?'base':'pick')+'"">'+e.pick+'</td>'
      +'<td>'+conf+'</td><td class=""base"">'+(e.probs||'--')+'</td>'
      +'<td class=""why"">'+e.why+'</td>'
      +'<td class=""base"">'+(base?'--':e.latency_ms+'ms')+'</td>'
      +'<td class=""state"">'+e.state+'</td></tr>';
  }).join('');
});}
load();setInterval(load,1000);
</script></body></html>";
	}
}
