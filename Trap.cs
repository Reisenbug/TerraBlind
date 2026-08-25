using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;

namespace TerraBlind
{
    // 贪心卡死的【唯一】判据:Expand 给出的物理候选里,没有一个 H 更低。
    //
    // 注意不是"4 邻域没有更低的 H" —— 那个判据我试过,全场 231 万格一个都扫不出来,
    // 因为 Dijkstra 的每格 H 本来就是从某个更低的邻居松弛来的,4 邻域极小几乎不存在。
    // 真正卡人的是【物理够不着】:邻居 H 是更低,但人跳不过去、挖不动、站不住。
    public static class Trap
    {
        // 卡过的点。key=格,value=撞了几次
        static readonly Dictionary<(int, int), int> _hits = new();
        static (int, int) _lastReported = (int.MinValue, int.MinValue);

        public static int Count => _hits.Count;

        // 最近一次 StepAlongField 是不是卡住了。RecedingNav 读它决定要不要叫 A* 脱困
        public static bool JustTrapped;
        public static (int x, int y) JustAt;
        public static int JustH;

        // 卡住的那一帧调这里。立即报告,不等累积
        public static void Hit(int cx, int cy, int h, int cands)
        {
            JustTrapped = true; JustAt = (cx, cy); JustH = h;
            _hits.TryGetValue((cx, cy), out int n);
            _hits[(cx, cy)] = ++n;
            // 同一格连着撞不刷屏,换了格子或第一次就报
            if ((cx, cy) != _lastReported)
            {
                _lastReported = (cx, cy);
                EventLog.W(Ev.Plan, $"TRAP ({cx},{cy}) H={h} 候选{cands}个全都不降H — 贪心在这儿走不动了(第{n}次)");
                Main.NewText($"[TerraBlind] 卡点 ({cx},{cy}) H={h}:{cands} 个候选没一个降 H", 255, 90, 90);
            }
            Draw();
        }

        public static void Reset() { _hits.Clear(); _lastReported = (int.MinValue, int.MinValue); JustTrapped = false; }

        // 撞过的点画红,撞得越多越亮
        static void Draw()
        {
            var tiles = new List<(int, int, Color)>();
            // 黄 = 预测会卡(还没走到),先画,让真撞过的红盖在上面
            foreach (var (px, py) in Predicted())
                tiles.Add((px, py, new Color(255, 220, 40) * 0.7f));
            foreach (var kv in _hits)
            {
                float f = System.Math.Min(1f, kv.Value / 5f);
                tiles.Add((kv.Key.Item1, kv.Key.Item2, new Color(255, (int)(120 * (1 - f)), 60)));
            }
            PathVisSystem.SetDeck(tiles, 60 * 60 * 30);
        }

        // 提前预扫【已删除】。它在后台线程遍历 MazeWand 的 H 场,而主线程同时在建/换那张 Dictionary,
        // 于是每帧都抛 "A concurrent update was performed on this collection and corrupted its state" ——
        // 整个寻路的依据被这个诊断功能搞坏了,人第一步就掉进 109 格深的坑。
        // 预警是诊断,寻路是主线,不值得为它冒并发风险。要重做的话:主线程同步跑,或者给场加锁/传副本。
        public static System.Collections.Generic.List<(int, int)> Predicted()
            => new System.Collections.Generic.List<(int, int)>();

        public static void Report()
        {
            if (_hits.Count == 0) { Main.NewText("[TerraBlind] 这一趟还没卡过"); return; }
            Main.NewText($"[TerraBlind] 卡点 {_hits.Count} 个:", 255, 90, 90);
            foreach (var kv in _hits)
                Main.NewText($"  ({kv.Key.Item1},{kv.Key.Item2}) x{kv.Value}");
            Draw();
        }
    }
}
