using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using Terraria;
using Terraria.GameInput;
using Terraria.ModLoader;

namespace TerraBlind
{
	// 按按键名往 KeyStatus 里写,和键盘输入并存:只写 true,不写 false
	public class KeyPress : ModSystem
	{
		// 每个元素是一帧按不按
		static readonly ConcurrentQueue<(string Key, bool[] Frames)> _inbox = new();
		static readonly List<(string Key, bool[] Frames, int At)> _running = new();
		// 所有按键名,主线程拍的快照
		public static volatile string KeysJson = "[]";
		static int _logLeft;
		static string _logKey = "";

		public static void Tap(string key) => _inbox.Enqueue((key, new[] { true }));
		public static void Hold(string key, int frames)
		{
			var f = new bool[System.Math.Max(1, frames)];
			for (int i = 0; i < f.Length; i++) f[i] = true;
			_inbox.Enqueue((key, f));
		}
		public static void DoubleTap(string key) => _inbox.Enqueue((key, new[] { true, false, true }));

		public override void PostUpdateInput()
		{
			var cur = PlayerInput.Triggers.Current.KeyStatus;
			if (KeysJson == "[]") KeysJson = Snapshot(cur);
			while (_inbox.TryDequeue(out var c))
			{
				if (!cur.ContainsKey(c.Key)) { DiagLog.Write($"[key] 没有这个按键 {c.Key}"); continue; }
				_running.Add((c.Key, c.Frames, 0));
				_logKey = c.Key;
				_logLeft = c.Frames.Length + 40;
				DiagLog.Write($"[key] 开始 {c.Key} {c.Frames.Length}帧");
			}
			bool wrote = false;
			for (int i = _running.Count - 1; i >= 0; i--)
			{
				var r = _running[i];
				if (r.Frames[r.At]) { cur[r.Key] = true; wrote = true; }
				r.At++;
				if (r.At >= r.Frames.Length) _running.RemoveAt(i); else _running[i] = r;
			}
			// JustPressed/JustReleased 在 UpdateInput 里已经算过一次,写完要重算
			if (wrote) PlayerInput.Triggers.Update();
			Log(cur);
		}

		// 这一帧写入的按键状态,和上一帧更新后的玩家状态
		static void Log(Dictionary<string, bool> cur)
		{
			if (_logLeft <= 0) return;
			_logLeft--;
			var p = Main.LocalPlayer;
			if (p == null || !p.active) return;
			DiagLog.Write($"[key] f{Main.GameUpdateCount} {_logKey}={cur[_logKey]}"
				+ $" just={PlayerInput.Triggers.JustPressed.KeyStatus[_logKey]}"
				+ $" ctlJump={p.controlJump} vy={p.velocity.Y:0.00} jump={p.jump}"
				+ $" wing={p.wingTime:0.0} dashDelay={p.dashDelay} dashType={p.dashType}"
				+ $" vx={p.velocity.X:0.00} mount={p.mount.Active}");
		}

		static string Snapshot(Dictionary<string, bool> cur)
		{
			var sb = new StringBuilder("[");
			foreach (var k in cur.Keys)
			{
				if (sb.Length > 1) sb.Append(',');
				sb.Append('"').Append(k.Replace("\"", "")).Append('"');
			}
			return sb.Append(']').ToString();
		}
	}
}
