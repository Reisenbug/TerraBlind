using System.Collections.Generic;
using System.IO;
using Terraria;

namespace TerraBlind
{
    // 事件层:只记"发生了什么",一条一行,人直接读。逐帧追踪归 DiagLog(默认关)。
    // 分文件是因为同类事件要跨时间聚合 —— sentinel 的 132 次触发散在 132 个 runs 文件里,规律就看不出来。
    public enum Ev
    {
        Exec,       // 一步走完:HIT/MISS
        Plan,       // 选了哪条边、为什么没得选
        Place,      // 放置结果
        Dig,        // 挖掘结果
        Fail,       // 执行器自报:我没进展
        Sentinel,   // 卡死救援
        Field,      // 场构建耗时
        Goal,       // 目标切换/到达
        Craft,      // 合成
        House,      // 建房
        Loot,       // 开箱/拾取
        Misc,
    }

    public static class EventLog
    {
        private static readonly object _lock = new object();
        private static string _dir;
        private static readonly Dictionary<Ev, string> _paths = new();

        private static string Dir
        {
            get
            {
                if (_dir != null) return _dir;
                try
                {
                    _dir = Path.Combine(Main.SavePath, "TerraBlindLogs", "events");
                    Directory.CreateDirectory(_dir);
                }
                catch { _dir = ""; }
                return _dir;
            }
        }

        private static string PathFor(Ev e)
        {
            if (_paths.TryGetValue(e, out var p)) return p;
            string d = Dir;
            if (string.IsNullOrEmpty(d)) return "";
            p = Path.Combine(d, e.ToString().ToLowerInvariant() + ".log");
            _paths[e] = p;
            return p;
        }

        // 同一条同时进 all.log(时间线) 和 <类别>.log(聚合)。两份都是纯文本追加,搜哪个都行。
        public static void W(Ev e, string msg)
        {
            string d = Dir;
            if (string.IsNullOrEmpty(d)) return;
            string line = $"{Main.GameUpdateCount} [{e.ToString().ToLowerInvariant()}] {msg}\n";
            try
            {
                lock (_lock)
                {
                    File.AppendAllText(Path.Combine(d, "all.log"), line);
                    string cp = PathFor(e);
                    if (!string.IsNullOrEmpty(cp)) File.AppendAllText(cp, line);
                }
            }
            catch { }
        }

        public static void Clear()
        {
            string d = Dir;
            if (string.IsNullOrEmpty(d)) return;
            try { lock (_lock) { foreach (var f in Directory.GetFiles(d, "*.log")) File.Delete(f); } } catch { }
        }
    }
}
