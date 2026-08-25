using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;

namespace TerraBlind
{
    // 把【贪心一定会卡死的地方】画出来。
    //
    // 局部极小的定义纯粹是局部的:四个邻居的 H 都不比自己低。裸贪心走到这儿必然 A→B→A 无限弹,
    // 因为它选出的邻居 H 更高,而从那格再选,最低的又是回来这格。这是算法性质,不是补丁不够。
    //
    // 盆地 = 顺着梯度会流进同一个极小点的所有格子。人只要踏进盆地就会被吸到极小点,
    // 所以真正危险的是整片盆地,不是那一个格子。
    public static class MinimaVis
    {
        // 一次最多画多少格:整张场几十万格全画会淹掉屏幕也拖慢绘制
        const int MaxDraw = 20000;

        public static void Toggle(int goalWx, int goalWy)
        {
            // 缓存里通常已经有(右键设点时建过)。没有就现建 —— 这是手按的调试工具,卡一下可以接受
            var field = MazeWand.GetField(goalWx, goalWy);
            if (field == null || field.Count == 0)
            {
                Main.NewText("[TerraBlind] 场是空的 —— 先右键设点");
                return;
            }
            var (minima, basin) = Scan(field);
            if (minima.Count == 0)
            {
                PathVisSystem.ClearDeck();
                Main.NewText($"[TerraBlind] 场里没有局部极小(共 {field.Count} 格)");
                return;
            }

            var tiles = new List<(int, int, Color)>();
            // 盆地画淡橙,极小点画亮红 —— 极小点后画,盖在盆地上面
            foreach (var kv in basin)
            {
                if (tiles.Count >= MaxDraw) break;
                tiles.Add((kv.Key.Item1, kv.Key.Item2, new Color(255, 140, 40) * 0.45f));
            }
            foreach (var m in minima)
                tiles.Add((m.Item1, m.Item2, new Color(255, 40, 40)));

            PathVisSystem.SetDeck(tiles, 60 * 60 * 10);
            var labels = new List<(int, int, string, Color)>();
            foreach (var m in minima)
            {
                if (labels.Count >= 60) break;   // 标签比色块贵得多,只标前 60 个
                labels.Add((m.Item1, m.Item2, $"H{field[m]}", Color.White));
            }
            PathVisSystem.SetLabels(labels, 60 * 60 * 10);

            DiagLog.Write($"[minima] goal=({goalWx},{goalWy}) 场={field.Count} 极小={minima.Count} 盆地={basin.Count}");
            foreach (var m in minima)
                DiagLog.Write($"[minima] 极小点 ({m.Item1},{m.Item2}) H={field[m]}");
            Main.NewText($"[TerraBlind] 局部极小 {minima.Count} 个,盆地 {basin.Count} 格(红=极小,橙=盆地)");
        }

        public static void Clear()
        {
            PathVisSystem.ClearDeck();
            PathVisSystem.SetLabels(new List<(int, int, string, Color)>(), 1);
            Main.NewText("[TerraBlind] 极小点图层已清");
        }

        static readonly (int dx, int dy)[] N4 = { (1, 0), (-1, 0), (0, 1), (0, -1) };

        // 扫出所有极小点,以及每个格子顺梯度流向哪个极小点(=盆地归属)
        static (List<(int, int)> minima, Dictionary<(int, int), (int, int)> basin) Scan(Dictionary<(int, int), int> field)
        {
            var minima = new List<(int, int)>();
            var isMin = new HashSet<(int, int)>();
            foreach (var kv in field)
            {
                int h = kv.Value;
                bool anyLower = false;
                foreach (var (dx, dy) in N4)
                    if (field.TryGetValue((kv.Key.Item1 + dx, kv.Key.Item2 + dy), out int nh) && nh < h)
                    { anyLower = true; break; }
                // 终点自己 H=0,四邻都更高,但那不是"卡住" —— 到了就结束了
                if (!anyLower && h > 0) { minima.Add(kv.Key); isMin.Add(kv.Key); }
            }

            // 盆地:每格顺着"H 最低的邻居"往下走,看最后落到哪个极小点。路径上的格子共享同一个结果。
            var basin = new Dictionary<(int, int), (int, int)>();
            var path = new List<(int, int)>();
            foreach (var kv in field)
            {
                if (basin.ContainsKey(kv.Key)) continue;
                path.Clear();
                var cur = kv.Key;
                (int, int)? sink = null;
                var guard = new HashSet<(int, int)>();
                while (true)
                {
                    if (basin.TryGetValue(cur, out var known)) { sink = known; break; }
                    if (isMin.Contains(cur)) { sink = cur; break; }
                    if (!guard.Add(cur)) { sink = cur; break; }   // 平台/环:就地当汇,别死循环
                    path.Add(cur);
                    int h = field[cur];
                    var best = cur; int bestH = h;
                    foreach (var (dx, dy) in N4)
                    {
                        var n = (cur.Item1 + dx, cur.Item2 + dy);
                        if (field.TryGetValue(n, out int nh) && nh < bestH) { bestH = nh; best = n; }
                    }
                    if (best == cur) { sink = cur; break; }   // 没有更低的邻居 = 自己就是汇
                    cur = best;
                }
                foreach (var c in path) basin[c] = sink.Value;
            }

            // 只留【流向真极小点】的格子;流向终点的是正常路,不该标红
            var danger = new Dictionary<(int, int), (int, int)>();
            foreach (var kv in basin)
                if (isMin.Contains(kv.Value) && kv.Key != kv.Value) danger[kv.Key] = kv.Value;
            return (minima, danger);
        }
    }
}
