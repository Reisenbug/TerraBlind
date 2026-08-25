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
        // 找目标的搜索半径(格)。坑通常很小,超出这个距离就不是"出坑"而是"走完全程"了
        const int Radius = 40;
        // 出坑用的展开预算。坑小,不需要 2 万 —— 给多了反而在失败时白烧几百 ms
        const int Budget = 4000;

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

            var t = PickTarget(field, curCx, curCy, curH);
            if (t == null)
            {
                DiagLog.Write($"[escape] ({curCx},{curCy})H{curH} 半径{Radius}内找不到低 {MinDrop} 的落脚点");
                return false;
            }
            var target = t.Value;
            int tH = field[(target.x, target.y)];

            // 场目标传【最终目标】:复用已建好的大罗盘当启发式,别为出坑另建一张场
            var res = StateSpacePlanner.Plan(target.x, target.y,
                                             fieldGoalWx: goalWx, fieldGoalWy: goalWy,
                                             maxExp: Budget, goalSnapCap: 0);
            if ((!res.Found && !res.Partial) || res.Steps.Count == 0)
            {
                _fails++;
                DiagLog.Write($"[escape] A* 搜不出 ({curCx},{curCy})→({target.x},{target.y}) exp={res.Expansions} 失败{_fails}/{MaxFails}");
                return false;
            }

            _fails = 0;
            _lastH = curH;
            EventLog.W(Ev.Plan, $"ESCAPE ({curCx},{curCy})H{curH} 贪心走不动 → A* 带到 ({target.x},{target.y})H{tH} 降{curH - tH} steps={res.Steps.Count} exp={res.Expansions}");
            Main.NewText($"[TerraBlind] 卡点脱困:A* → ({target.x},{target.y}) 降 H {curH - tH}", 120, 220, 255);
            StateSpacePlanner.DispatchPlan(res);
            return true;
        }

        // 找一个"确实在坑外"的落脚点:H 比现在低 MinDrop 以上,人站得住,离得最近的那个。
        // 按 H 降幅挑会挑到很远的地方 —— 那等于让 A* 走完全程,失去"只出坑"的意义。
        static (int x, int y)? PickTarget(Dictionary<(int, int), int> field, int cx, int cy, int curH)
        {
            (int x, int y)? best = null;
            int bestDist = int.MaxValue;
            for (int dx = -Radius; dx <= Radius; dx++)
                for (int dy = -Radius; dy <= Radius; dy++)
                {
                    int d = System.Math.Abs(dx) + System.Math.Abs(dy);
                    if (d == 0 || d > Radius || d >= bestDist) continue;
                    int x = cx + dx, y = cy + dy;
                    if (!field.TryGetValue((x, y), out int h)) continue;
                    if (curH - h < MinDrop) continue;
                    if (!CellKind.Stands(x, y)) continue;
                    best = (x, y); bestDist = d;
                }
            return best;
        }
    }
}
