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

        // ===== 提前预警 =====
        // 沿场推荐的路线往前探 AheadCells 格,逐格问 WouldTrap。人还没走到就知道前面哪儿会卡。
        // 全场扫不现实(231 万格 x 0.82ms = 32 分钟),但一条线上几百格只要几百毫秒,丢后台无感。
        const int AheadCells = 200;
        static volatile bool _scanning;
        static readonly HashSet<(int, int)> _predicted = new();

        public static void ScanAhead(int curCx, int curCy, int goalWx, int goalWy)
        {
            if (_scanning) return;
            var p = Main.LocalPlayer;
            if (p == null || !p.active) return;
            var field = MazeWand.PeekFieldOrNull(goalWx, goalWy);
            if (field == null) return;
            // 玩家相关的东西必须在【主线程】读完再传进去,后台读会撕裂
            var ph = PhysicsSimulator.Params.FromPlayer(p);
            int slot = NavCoordinator.FindPlatformSlot(p);
            int platformTile = slot >= 0 ? p.inventory[slot].createTile : -1;
            bool hasPick = false;
            for (int i = 0; i < 10; i++) { var it = p.inventory[i]; if (it != null && !it.IsAir && it.pick > 0) { hasPick = true; break; } }
            float gcx = goalWx * 16f + 8f, gfy = (goalWy + 1) * 16f;
            _scanning = true;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var line = MazeWand.TraceFrom(field, curCx, curCy, goalWx, goalWy);
                    var found = new List<(int, int)>();
                    int n = System.Math.Min(line.Count, AheadCells);
                    for (int i = 0; i < n; i++)
                    {
                        var (x, y) = line[i];
                        if (StateSpacePlanner.WouldTrap(field, x, y, ph, platformTile, hasPick, gcx, gfy))
                            found.Add((x, y));
                    }
                    lock (_predicted)
                    {
                        _predicted.Clear();
                        foreach (var f in found) _predicted.Add(f);
                    }
                    if (found.Count > 0)
                    {
                        DiagLog.Write($"[trap] 前方 {n} 格里有 {found.Count} 个卡点:" +
                                      string.Join(" ", found.ConvertAll(f => $"({f.Item1},{f.Item2})")));
                        EventLog.W(Ev.Plan, $"AHEAD 前方 {n} 格预测到 {found.Count} 个卡点,第一个 ({found[0].Item1},{found[0].Item2})");
                    }
                }
                catch (System.Exception e) { DiagLog.Write($"[trap] scan EXC {e.Message}"); }
                finally { _scanning = false; }
            });
        }

        // 预测到的卡点(黄),和已经撞过的(红)一起画
        public static List<(int, int)> Predicted()
        {
            lock (_predicted) return new List<(int, int)>(_predicted);
        }

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
