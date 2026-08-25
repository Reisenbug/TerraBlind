using System.Collections.Generic;
using Terraria;

namespace TerraBlind
{
    // 贪心走不动的那一刻,让 A* 接手一小段。
    //
    // 贪心的理论缺陷只有一个:它拒绝任何"先变差"的路,所以物理候选全不降 H 时必然原地弹。
    // 而 A* 不要求中间每步都变好 —— 坑会被整个展开,再从真出口出去。
    //
    // 只在【卡住那一帧】用,而且只搜出坑那一小段,不搜到终点:
    // 贪心在别处好用得很(连贯、免疫地形变化、成本与距离无关),没有理由整段换掉。
    public static class TrapEscape
    {
        // 逃生目标要比当前 H 低这么多才算"真出去了"。低太少可能还在同一个坑里
        const int MinDrop = 60;
        // 预算只当【最后的保险】,正常情况碰不到。
        // 真正的终止条件是 open 表空 —— 搜索空间穷尽,数学上确定不可达,不含任何拍脑袋的阈值。
        // 它能空掉的前提是 coarseStates(一格只留一个状态);带速度时同一格囤几百个变体,open 永远不空。
        const int Budget = 20000;

        static int _fails;
        const int MaxFails = 3;
        static int _lastH = int.MinValue;   // 上次脱困时的 H,用来判断"是不是真出去了"

        public static void Reset() { _fails = 0; _lastH = int.MinValue; }

        // 卡住的那一帧调。成功派发了一段 A* 路就返回 true,调用方直接 return
        public static bool TryEscape(Dictionary<(int, int), int> field, int curCx, int curCy, int curH,
                                     int goalWx, int goalWy)
        {
            // H 比上次卡的时候明显低了 = 中间真出去过,这是个新坑,失败计数重新开始。
            // 不清的话:一趟里前面某个坑用光了 3 次机会,后面所有坑都不再尝试
            if (_lastH != int.MinValue && curH < _lastH - MinDrop) _fails = 0;
            if (_fails >= MaxFails) return false;

            // PickTarget 里已经打了具体原因(可达区多大、区内最低 H 多少),这儿不再重复
            var t = PickTarget(field, curCx, curCy, curH);
            if (t == null) return false;
            var target = t.Value;
            int tH = field[(target.x, target.y)];

            // 场目标传【最终目标】:复用已建好的大罗盘当启发式,别为出坑另建一张场
            var res = StateSpacePlanner.Plan(target.x, target.y,
                                             fieldGoalWx: goalWx, fieldGoalWy: goalWy,
                                             maxExp: Budget, goalSnapCap: 0, coarseStates: true);
            // 【只认 Found】。partial 的落点常常就是起点本身(死胡同里 A* 哪都去不了),
            // 派发出去人绕一圈回原地,下一周期一模一样 —— 那是把贪心的死循环换成 A* 的死循环。
            if (!res.Found || res.Steps.Count == 0)
            {
                _fails++;
                DiagLog.Write($"[escape] A* 搜不出 ({curCx},{curCy})→({target.x},{target.y}) exp={res.Expansions} partial={res.Partial} 失败{_fails}/{MaxFails}");
                return false;
            }

            _fails = 0;
            _lastH = curH;
            EventLog.W(Ev.Plan, $"ESCAPE ({curCx},{curCy})H{curH} 贪心走不动 → A* 带到 ({target.x},{target.y})H{tH} 降{curH - tH} steps={res.Steps.Count} exp={res.Expansions}");
            Main.NewText($"[TerraBlind] 卡点脱困:A* → ({target.x},{target.y}) 降 H {curH - tH}", 120, 220, 255);
            StateSpacePlanner.DispatchPlan(res);
            return true;
        }

        // 目标不能用 H 挑 —— H 正是骗人的那个东西。死胡同里 H 一路降到底,岔路口在回头方向 H 更高,
        // 按"H 比现在低"选永远选不中真出口;而墙对面 H 更低的格物理上根本到不了,选中了也白搭。
        //
        // 改成按【可达性】挑:从人站的格泛洪,只走物理走得通的边,完全不看 H。
        // 这片区域就是"这个坑"。区内 H 最低的格一定能到 —— A* 不会白搜。
        static (int x, int y)? PickTarget(Dictionary<(int, int), int> field, int cx, int cy, int curH)
        {
            var region = Flood(cx, cy);
            (int x, int y)? best = null;
            int bestH = curH;
            foreach (var c in region)
            {
                if (!field.TryGetValue(c, out int h)) continue;
                if (h < bestH) { bestH = h; best = c; }
            }
            if (best != null && curH - bestH >= MinDrop)
            {
                DiagLog.Write($"[escape] 可达区 {region.Count} 格,区内最低 ({best.Value.x},{best.Value.y})H{bestH} 降{curH - bestH}");
                return best;
            }
            // 整片可达区都不比现在好 = 真死胡同,原路返回也出不去(泛洪已经含了回头路)。
            // 这时候没有"走一段就好"的解,交给调用方去做别的(挖墙/搭桥/放弃这一段)
            DiagLog.Write($"[escape] 可达区 {region.Count} 格,最低 H{bestH} vs 现在 H{curH} — 整片都不比现在好");
            return null;
        }

        // 物理可达泛洪。用 CellKind 那套【便宜】判据(纯 tile 查询),不用 Expand(0.82ms/格,几百格就半秒)。
        // 近似的地方:横走只认同高度和 ±1 阶,跳只认正上方 —— 宁可少算(可达区偏小),不能多算,
        // 多算会给出一个 A* 到不了的目标,又回到白烧预算。
        static HashSet<(int x, int y)> Flood(int sx, int sy)
        {
            var seen = new HashSet<(int x, int y)> { (sx, sy) };
            var q = new Queue<(int x, int y)>();
            q.Enqueue((sx, sy));
            while (q.Count > 0 && seen.Count < MaxRegion)
            {
                var (x, y) = q.Dequeue();
                // 横走:同高、上一阶、下一阶
                for (int dx = -1; dx <= 1; dx += 2)
                    for (int dy = -1; dy <= 1; dy++)
                        Push(seen, q, x + dx, y + dy);
                // 跳:正上方 JumpReach 格内,中途不能有挡的
                for (int d = 1; d <= JumpUp; d++)
                {
                    if (!CellKind.Passable(x, y - d)) break;
                    Push(seen, q, x, y - d);
                }
                // 掉:正下方一直到落地
                for (int d = 1; d <= FallDown; d++)
                {
                    if (CellKind.Of(x, y + d) == Cell.Solid) break;
                    if (CellKind.Stands(x, y + d)) { Push(seen, q, x, y + d); break; }
                }
            }
            return seen;
        }

        const int MaxRegion = 3000;   // 泛洪上限。坑再大也就这么大,超了说明本来就不是"坑"
        const int JumpUp = 6;         // 和 MazeWand.JumpReach 一致
        const int FallDown = 30;

        static void Push(HashSet<(int x, int y)> seen, Queue<(int x, int y)> q, int x, int y)
        {
            if (!CellKind.Stands(x, y)) return;
            if (!seen.Add((x, y))) return;
            q.Enqueue((x, y));
        }
    }
}
