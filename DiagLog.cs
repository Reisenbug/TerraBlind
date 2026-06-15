using System.Collections.Generic;
using System.IO;
using Terraria;

namespace TerraBlind
{
    public static class DiagLog
    {
        private static readonly object _lock = new object();
        private static string _logPath;
        private static string _eventsPath;
        private static string _runPath;   // when set, Write goes here (one file per execution) instead of the shared log
        private static string _runsDir;

        // Begin a per-execution log file. name = caller-built "sx_sy__gx_gy"; collisions get _2, _3, ... suffixes so
        // repeated runs between the same cells stay separate. all subsequent Write() calls land in this file until the
        // next StartRun / EndRun. returns the final path.
        public static string StartRun(string name)
        {
            _ = LogPath;
            lock (_lock)
            {
                try
                {
                    if (_runsDir == null)
                    {
                        _runsDir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_logPath), "runs");
                        Directory.CreateDirectory(_runsDir);
                    }
                    string p = System.IO.Path.Combine(_runsDir, name + ".log");
                    for (int n = 2; File.Exists(p); n++) p = System.IO.Path.Combine(_runsDir, $"{name}_{n}.log");
                    _runPath = p;
                    File.WriteAllText(_runPath, "");
                }
                catch { _runPath = null; }
                return _runPath;
            }
        }

        public static void EndRun() { lock (_lock) { _runPath = null; } }

        private const int RingSize = 1800;
        private static readonly string[] _ring = new string[RingSize];
        private static int _ringHead = 0;

        private static string LogPath
        {
            get
            {
                if (_logPath != null) return _logPath;
                try
                {
                    string dir = System.IO.Path.Combine(Main.SavePath, "TerraBlindLogs");
                    Directory.CreateDirectory(dir);
                    _logPath = System.IO.Path.Combine(dir, "jump_trace.log");
                    _eventsPath = System.IO.Path.Combine(dir, "nav_events.jsonl");
                }
                catch { _logPath = ""; }
                return _logPath;
            }
        }

        public static void Write(string msg)
        {
            string p = _runPath ?? LogPath;
            if (string.IsNullOrEmpty(p)) return;
            try { lock (_lock) { File.AppendAllText(p, $"{Main.GameUpdateCount} {msg}\n"); } } catch { }
        }

        public static void JumpTrace(string msg) => Write(msg);

        public static void WriteEvent(string json)
        {
            _ = LogPath;
            if (string.IsNullOrEmpty(_eventsPath)) return;
            try
            {
                lock (_lock)
                {
                    _ring[_ringHead] = json;
                    _ringHead = (_ringHead + 1) % RingSize;
                    File.AppendAllText(_eventsPath, json + "\n");
                }
            }
            catch { }
        }

        public static List<string> FlushWindow(int maxEvents = 1200)
        {
            lock (_lock)
            {
                var result = new List<string>();
                for (int i = 0; i < RingSize; i++)
                {
                    int idx = (_ringHead - 1 - i + RingSize) % RingSize;
                    if (_ring[idx] == null) break;
                    result.Add(_ring[idx]);
                    if (result.Count >= maxEvents) break;
                }
                result.Reverse();
                return result;
            }
        }

        public static string BuildReportDir()
        {
            _ = LogPath;
            if (string.IsNullOrEmpty(_eventsPath)) return "";
            return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_eventsPath), "debug_reports");
        }

        public static void WriteDebugReport(string triggeredBy, string triggerReason)
        {
            string dir = BuildReportDir();
            if (string.IsNullOrEmpty(dir)) return;
            try
            {
                Directory.CreateDirectory(dir);
                var p = Main.LocalPlayer;
                int pcx = p != null ? (int)((p.position.X + p.width / 2f) / 16f) : 0;
                int pcy = p != null ? (int)((p.position.Y + p.height) / 16f) : 0;
                float vx = p?.velocity.X ?? 0f;
                float vy = p?.velocity.Y ?? 0f;

                var window = FlushWindow(300);
                var sb = new System.Text.StringBuilder();
                sb.Append("{\"triggered_by\":{\"reason\":\"").Append(triggerReason)
                  .Append("\",\"triggered_by\":\"").Append(triggeredBy)
                  .Append("\",\"tick\":").Append(Main.GameUpdateCount).Append("}");

                sb.Append(",\"pre_window\":[");
                for (int i = 0; i < window.Count; i++) { if (i > 0) sb.Append(','); sb.Append(window[i]); }
                sb.Append("]");

                sb.Append(",\"state\":{\"px\":").Append(pcx).Append(",\"py\":").Append(pcy)
                  .Append(",\"vx\":").Append(vx.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture))
                  .Append(",\"vy\":").Append(vy.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture))
                  .Append(",\"nav_state\":\"").Append(NavCoordinator.State).Append("\"")
                  .Append(",\"fail_reason\":\"").Append(NavCoordinator.FailReason).Append("\"")
                  .Append("}");

                sb.Append("}");

                string fname = System.IO.Path.Combine(dir, $"{Main.GameUpdateCount}_{triggeredBy}.json");
                lock (_lock) { File.WriteAllText(fname, sb.ToString()); }
                Write($"[debug] report written: {fname}");
            }
            catch { }
        }
    }
}
