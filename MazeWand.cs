using Microsoft.Xna.Framework;
using System.Collections.Generic;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraBlind
{
    public class MazeWand : ModItem
    {
        public override string Texture => "Terraria/Images/Item_" + ItemID.RodofDiscord;

        // cost ≈ 每格相对耗时。走 3px/帧,落到头 10px/帧,所以往下约是横走的 1/3;往上最慢。
        // 挖按真帧数:铜镐石头 45 帧/格,走一格 5.3 帧=3,故横挖 26。原来 120 等于"绕 40 格也比挖便宜",墙前必掉头。
        const int MoveDown = 1, MoveSide = 3, MoveUp = 9;
        const int DigDown = 26, DigSide = 26, DigUp = 26;   // 【每格】挖价,要乘实际挖的格数
        const int DigUpLift = 19;   // 向上挖额外一次性的垫脚钱:凿开还得砌东西站上去,和挖几格无关
        const int PillarUp = 45;   // vertical ascent in ANCHORLESS open air beyond jump reach: only a pillar can do it — price the pillar, not a free climb
        const int JPlaceUp = 15;   // vertical ascent beyond jump reach WITH a platform anchor nearby: a jump-place ladder does it ~3× faster than pillaring
        const int JumpReach = 6;   // cells a jump can gain above support; up-moves within this stay MoveUp
        public const int MaxMoveCost = PillarUp;   // 比这贵的边一定含挖掘 —— 别在别处硬编阈值

        // AIR penalty: without it the geometric field cuts straight through the sky. tiny debuff only — underground has
        // background walls everywhere so flight is cheap; this just nudges toward ground and stops surface sky-cruising.
        const int FreeAir = 7;       // free below this (one jump's worth)
        const int AirSat = 10;       // h'=AirSat → half AirCap
        const int AirCap = 6;        // asymptote. tiny on purpose
        const int MaxAirProbe = 60;  // must exceed deepest valley we want to measure, else it reads shallow
        // 横向浮空罚:连续悬空超过一跳的距离必须贵过"下去再上来",否则场会直接飞过大坑。
        // 超出 AirSpanFree 的每格计费,让长跨越的总价超线性;窄坑照旧免费飘过去。
        const int AirSpanFree = 10;   // cells of sideways air that stay free (within a jump's horizontal reach)
        const int AirSpanK = 6;       // per-cell cost for each air cell beyond AirSpanFree (tunes float-vs-descend)
        const int AirSpanProbe = 40;  // how far ahead to measure the continuous air span

        // 踩进岩浆就是死。大到实际不可达,但仍是有限数 —— 万不得已跨一格岩浆桥还是允许的。
        const int PlateCost = 400;   // 绕得开就绕(≈130格横走的代价),绕不开还是让它过 —— 禁行会把人困死
        // 挖不动的砖(神庙)必须真不可达。有限数的话 Dijkstra 照样穿墙算出墙后的 H,
        // 墙后看着又近又好,上层挑中它一头撞上去开挖。Impassable 让边根本不入队。
        const int Impassable = int.MaxValue;
        static bool IsLava(int x, int y)
        {
            if (x < 0 || y < 0 || x >= Main.maxTilesX || y >= Main.maxTilesY) return false;
            var t = Main.tile[x, y];
            return t.LiquidAmount > 0 && t.LiquidType == LiquidID.Lava;
        }

        // 水/蜂蜜/蛛网原先按免费空气算,线就往里钻。蛛网算过路费不算挖:走过去本身就是破网,不需要工具。
        // 只是附加费不是禁行 —— 绕路更贵时照样淌水过。物理模拟是旱地的,水里落点会短,miss 机制吸收。
        const int WaterExtra = 6, HoneyExtra = 20, WebExtra = 12;
        static int MediumExtra(int x, int y)
        {
            if (x < 0 || y < 0 || x >= Main.maxTilesX || y >= Main.maxTilesY) return 0;
            var t = Main.tile[x, y];
            int extra = 0;
            if (t.LiquidAmount > 0)
            {
                if (t.LiquidType == LiquidID.Water) extra += WaterExtra;
                else if (t.LiquidType == LiquidID.Honey) extra += HoneyExtra;
            }
            if (t.HasTile && t.TileType == TileID.Cobweb) extra += WebExtra;
            // HONEY BLOCK contact: standing ON a honey block (solid, so it's the floor below, never the entered cell)
            // slows movement as brutally as swimming in honey — charge the cell whose support is honey.
            if (y + 1 < Main.maxTilesY)
            {
                var f = Main.tile[x, y + 1];
                if (f.HasTile && f.TileType == TileID.HoneyBlock) extra += HoneyExtra;
            }
            return extra;
        }

        public override void SetDefaults()
        {
            Item.width = 28;
            Item.height = 28;
            Item.useStyle = ItemUseStyleID.Swing;
            Item.useTime = 20;
            Item.useAnimation = 20;
            Item.rare = ItemRarityID.Green;
            Item.maxStack = 1;
            Item.noMelee = true;
        }

        public override bool AltFunctionUse(Player player) => true;

        // 调试工具:左键设 point1(目标)并清空,右键设 point2(起点),两个都有就跑场画线。
        // static 是因为这是单实例手动探针,不走 nav 管线。
        static (int x, int y)? _p1, _p2;
        // 极小点工具要知道当前这张场是朝哪个目标建的
        public static (int x, int y)? Point2 => _p2;
        static volatile bool _mazeBusy;

        // J 开关朝 point2 的 receding nav,拿大场当罗盘。从玩家【当前】位置起步 ——
        // 场是按目标缓存的、哪儿都有效,所以建完场再走远也不影响。
        public static void ToggleNav()
        {
            DiagLog.Write("[maze-nav] J pressed");
            if (RecedingNav.Active) { RecedingNav.Stop(); Chatter.Say("[TerraBlind] receding nav OFF"); return; }
            if (!_p2.HasValue) { DiagLog.Write("[maze-nav] J → no point2 (goal) set"); Chatter.Say("[TerraBlind] set point2 (right-click) first"); return; }
            DiagLog.Write($"[maze-nav] J → receding toward p2=({_p2.Value.x},{_p2.Value.y})");
            RecedingNav.Start(_p2.Value.x, _p2.Value.y);
        }

        public override bool? UseItem(Player player)
        {
            if (player != Main.LocalPlayer) return null;

            int mx = (int)((Main.mouseX + Main.screenPosition.X) / 16f);
            int my = (int)((Main.mouseY + Main.screenPosition.Y) / 16f);

            if (player.altFunctionUse == 2)
                _p2 = (mx, my);
            else
            {
                _p1 = (mx, my);
                _p2 = null; // left-click resets the pair
            }
            DiagLog.Write($"[maze] p1={_p1?.ToString() ?? "-"} p2={_p2?.ToString() ?? "-"}");

            // p2 = goal, p1 = start. Flood the field FROM p2 (the goal) and cache it keyed on p2, so pressing J reuses
            // this very field instead of rebuilding. Player is expected to stay near p1 (inside the field's box).
            if (_p1.HasValue && _p2.HasValue)
                RunMazeAsync(_p2.Value, _p1.Value);
            return true;
        }

        //千格距离的 BuildField 耗时几百 ms — run it off the main thread so the game doesn't hitch. PlanCtx-free here
        // (BuildField has no shared scratch), and PathVisSystem.SetTiles is lock-guarded, so the bg thread can draw.
        static void RunMazeAsync((int x, int y) goal, (int x, int y) start)
        {
            if (_mazeBusy) { DiagLog.Write("[maze] busy, ignored"); return; }
            _mazeBusy = true;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    DiagLog.StartRun($"{start.x}_{start.y}__{goal.x}_{goal.y}");
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var field = BuildField(goal.x, goal.y, start.x, start.y, bigMargin: true);
                    _cachedField = field; _cachedGoal = (goal.x, goal.y);   // reuse on J (GetField(p2) hits this)
                    var (path, breaks) = DescendPath(field, start.x, start.y, goal.x, goal.y);
                    DiagLog.Write($"[maze] start=({start.x},{start.y}) goal=({goal.x},{goal.y}) path={path.Count} breaks={breaks} field={field.Count} ms={sw.Elapsed.TotalMilliseconds:0} startInField={field.ContainsKey(start)}");
                    var tiles = new List<(int, int, Color)>();
                    foreach (var (x, y) in path)
                        tiles.Add((x, y, PathPlanner.IsBlockPublic(x, y) ? new Color(255, 60, 60) : new Color(40, 200, 255)));
                    PathVisSystem.SetTiles(tiles);
                    DiagLog.EndRun();
                }
                catch (System.Exception e) { DiagLog.Write($"[maze] EXC {e.Message}"); DiagLog.EndRun(); }
                finally { _mazeBusy = false; }
            });
        }

        // 每格一个节点,4 连通,不管物理。执行器读的是这张图的【梯度】,不是画出来的那条线;死胡同归执行层管。
        // 按目标缓存整片:建场是秒级的,但建一次全图哪儿都能用。地形改多了/换镐/人走出盒子才 Rebuild。
        const int FieldMargin = 400;   // 太大就要 flood 整个 5M 格世界(内存+主线程 5s 卡顿),够跨图就行
        // Mod.Load 跑在游戏线程 —— 日志记 MAIN 的那次建场,就是把游戏冻住的那次
        static int _mainThreadId;
        public static void MarkMainThread() => _mainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;

        // 现在是不是游戏线程。凡是要【写】玩家状态(搬背包/换槽)的地方都得先问这个 --
        // 后台线程动 p.inventory 会和主线程撞车。IsBackground 判的是线程池属性,不是这个语义。
        public static bool OnMainThread => System.Threading.Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        static (int gx, int gy) _cachedGoal = (int.MinValue, int.MinValue);
        static Dictionary<(int, int), int> _cachedField;

        // 不重建就读活场。H 是唯一能解释路由决策的数,以前只有日志碰巧打印的地方看得见 ——
        // "那个出口为什么不走"对人没站过的格子根本答不了。
        public static Dictionary<(int, int), int> PeekField() => _cachedField;

        public static bool TryPeek(int x, int y, out int h, out (int gx, int gy) goal, out int cells)
        {
            goal = _cachedGoal; cells = _cachedField?.Count ?? 0;
            h = -1;
            return _cachedField != null && _cachedField.TryGetValue((x, y), out h);
        }
        // 第二个槽:同时有两个目标是活的(receding 朝承诺点、执行器问真目标)。单槽的话两边互相驱逐,
        // 每二十帧就在主线程重建一次 500ms 的场,永远停不下来。
        static (int gx, int gy) _prevGoal = (int.MinValue, int.MinValue);
        static Dictionary<(int, int), int> _prevField;

        public static Dictionary<(int, int), int> GetField(int gx, int gy)
        {
            var p = Main.LocalPlayer;
            int sx = p != null ? (int)(p.Center.X / 16f) : gx;
            int sy = p != null ? (int)((p.position.Y + p.height) / 16f) - 1 : gy;
            if (_cachedField != null && _cachedGoal == (gx, gy)) return _cachedField;
            if (_prevField != null && _prevGoal == (gx, gy))
            {
                // promote the other slot rather than rebuild — this is the whole point of keeping two
                var f = _prevField; var g = _prevGoal;
                _prevField = _cachedField; _prevGoal = _cachedGoal;
                _cachedField = f; _cachedGoal = g;
                return _cachedField;
            }
            _prevField = _cachedField; _prevGoal = _cachedGoal;
            _cachedField = BuildField(gx, gy, sx, sy, bigMargin: true);
            _cachedGoal = (gx, gy);
            return _cachedField;
        }
        // 只读地问缓存,【绝不】就地建场。后台预扫要用 —— 建场是秒级的,不能顺手触发
        public static Dictionary<(int, int), int> PeekFieldOrNull(int gx, int gy)
        {
            if (_cachedField != null && _cachedGoal == (gx, gy)) return _cachedField;
            if (_prevField != null && _prevGoal == (gx, gy)) return _prevField;
            return null;
        }

        public static void InvalidateField()
        {
            _cachedField = null; _cachedGoal = (int.MinValue, int.MinValue);
            _prevField = null; _prevGoal = (int.MinValue, int.MinValue);
        }

        // FRESHNESS swap-rebuild: build a NEW field for the same goal (call off the main thread) and swap the cache
        // reference when done — the old field keeps serving replans until the swap, never a null window.
        public static void Rebuild(int gx, int gy, int sx, int sy)
        {
            var f = BuildField(gx, gy, sx, sy, bigMargin: true);
            _cachedField = f; _cachedGoal = (gx, gy);
        }

        // the field priced digs with the pick power captured at build time — a pick upgrade mid-nav makes those
        // prices lies (walls it now chews through still priced near-impassable).
        public static bool FieldPickStale()
        {
            if (_cachedField == null) return false;
            return BestPickPower() != _fieldPickPower;
        }

        // 无状态:每次调用从给定格现描,不缓存。所以摔一跤/被击退/传送之后,下次重规划的线
        // 已经是"从【现在这儿】出发的最优路",不是冻结在出发点的老线。
        public static List<(int, int)> TraceFrom(Dictionary<(int, int), int> field, int sx, int sy, int gx, int gy)
            => DescendPath(field, sx, sy, gx, gy, quiet: true).Item1;

        // 场推荐的下一格 = 使 StepCost(这一步) + field[邻居] 最小的那个,也就是 Dijkstra 最优跳。
        // 不是"H 最低的邻居" —— 那个判据会被诱上一根很贵的柱子掉进井里。没有更好的邻居返回 (0,0)。
        public static (int dx, int dy) FieldDir(Dictionary<(int, int), int> field, int cx, int cy)
        {
            if (field == null) return (0, 0);
            int bestTotal = int.MaxValue; var best = (dx: 0, dy: 0);
            foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                var n = (cx + dx, cy + dy);
                if (!field.TryGetValue(n, out int dn)) continue;
                int sc0 = StepCost(n.Item1, n.Item2, cx, cy);
                if (sc0 == Impassable) continue;   // 同 DescendPath:相加会溢出
                int total = sc0 + dn;
                if (total < bestTotal) { bestTotal = total; best = (dx, dy); }
            }
            return best;
        }

        public static Dictionary<(int, int), int> BuildField(int gx, int gy, int sx, int sy, bool bigMargin = false)
        {
            // 只按【当前这把镐】给挖掘定价:一律按能挖算,线会直接穿过神庙(Picksaw 之前挖不动),
            // 边全被拒,只剩回头路,循环。
            _fieldPickPower = BestPickPower();
            _fieldLavaSurvivable = Unstick.BlockItem(Main.LocalPlayer) >= 0;
            // 边距按段长缩放:固定 120 时相隔 10 格的两个宝藏也要 flood 250x250,串起来就是好几秒。
            int span = System.Math.Abs(sx - gx) + System.Math.Abs(sy - gy);
            int m = bigMargin ? FieldMargin : System.Math.Min(120, System.Math.Max(40, span));
            int minX = System.Math.Max(0, System.Math.Min(sx, gx) - m), maxX = System.Math.Min(Main.maxTilesX - 1, System.Math.Max(sx, gx) + m);
            int minY = System.Math.Max(0, System.Math.Min(sy, gy) - m), maxY = System.Math.Min(Main.maxTilesY - 1, System.Math.Max(sy, gy) + m);

            var dist = new Dictionary<(int, int), int>();
            var closed = new HashSet<(int, int)>();
            var pq = new SortedSet<(int cost, int x, int y)>();
            dist[(gx, gy)] = 0;
            pq.Add((0, gx, gy));

            int[] dxs = { 1, -1, 0, 0 };
            int[] dys = { 0, 0, 1, -1 };

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (pq.Count > 0)
            {
                var cur = pq.Min;
                pq.Remove(cur);
                var (cost, cx, cy) = cur;
                if (!closed.Add((cx, cy))) continue;

                for (int i = 0; i < 4; i++)
                {
                    int nx = cx + dxs[i], ny = cy + dys[i];
                    if (nx < minX || nx > maxX || ny < minY || ny > maxY) continue;
                    if (closed.Contains((nx, ny))) continue;
                    int sc = StepCost(cx, cy, nx, ny);
                    if (sc == Impassable) continue;   // 挖不动 → 这条边不存在,别入队(直接相加会溢出)
                    // H 的意思是【站在 (nx,ny) 还要多少】,所以先问人站不站得住那一格。
                    // 悬空格照发 H,就在空中连成一条 H 递减的假路,把人吸进死角:腐化区那次西边
                    // 1341..1343 脚下全空,场却给了 411..433 递减,而唯一的真出口要先爬高,H 反被标成 462。
                    int stand = StandPenalty(nx, ny);
                    if (stand == Impassable) continue;
                    int nc = cost + sc + stand;
                    if (dist.TryGetValue((nx, ny), out int old) && nc >= old) continue;
                    dist[(nx, ny)] = nc;
                    pq.Add((nc, nx, ny));
                }
            }
            // 谁建的、在不在主线程:一次 ~500ms,重建风暴看得见却归不了因。
            string who = "?";
            try
            {
                var st = new System.Diagnostics.StackTrace(1, false);
                var sbw = new System.Text.StringBuilder();
                for (int i = 0; i < st.FrameCount && i < 4; i++)
                {
                    var mi = st.GetFrame(i)?.GetMethod();
                    if (mi == null) continue;
                    if (sbw.Length > 0) sbw.Append('<');
                    sbw.Append(mi.DeclaringType?.Name).Append('.').Append(mi.Name);
                }
                who = sbw.ToString();
            }
            catch { }
            bool mainThread = System.Threading.Thread.CurrentThread.ManagedThreadId == _mainThreadId;
            EventLog.W(Ev.Field, $"goal=({gx},{gy}) {sw.Elapsed.TotalMilliseconds:0}ms cells={dist.Count} {(mainThread ? "MAIN" : "bg")}");
            return dist;
        }

        // 多源 Dijkstra,每个源 cost 0。下地狱的代价是拓扑的不是逐列的:S 形洞穴从顶上进,
        // 比任何一处直挖都便宜。
        public static Dictionary<(int, int), int> BuildFieldMulti(System.Collections.Generic.List<(int x, int y)> sources, int minX, int maxX, int minY, int maxY)
        {
            _fieldPickPower = BestPickPower();
            _fieldLavaSurvivable = Unstick.BlockItem(Main.LocalPlayer) >= 0;

            var dist = new Dictionary<(int, int), int>();
            var closed = new HashSet<(int, int)>();
            var pq = new SortedSet<(int cost, int x, int y)>();
            foreach (var (sx0, sy0) in sources) { dist[(sx0, sy0)] = 0; pq.Add((0, sx0, sy0)); }

            int[] dxs = { 1, -1, 0, 0 };
            int[] dys = { 0, 0, 1, -1 };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (pq.Count > 0)
            {
                var cur = pq.Min;
                pq.Remove(cur);
                var (cost, cx, cy) = cur;
                if (!closed.Add((cx, cy))) continue;

                for (int i = 0; i < 4; i++)
                {
                    int nx = cx + dxs[i], ny = cy + dys[i];
                    if (nx < minX || nx > maxX || ny < minY || ny > maxY) continue;
                    if (closed.Contains((nx, ny))) continue;
                    int sc = StepCost(cx, cy, nx, ny);
                    if (sc == Impassable) continue;   // 挖不动 → 这条边不存在,别入队(直接相加会溢出)
                    // 和 BuildField 同一条:站不住的格不该发 H
                    int stand = StandPenalty(nx, ny);
                    if (stand == Impassable) continue;
                    int nc = cost + sc + stand;
                    if (dist.TryGetValue((nx, ny), out int old) && nc >= old) continue;
                    dist[(nx, ny)] = nc;
                    pq.Add((nc, nx, ny));
                }
            }
            DiagLog.Write($"[descent-field] sources={sources.Count} dist={dist.Count} ms={sw.Elapsed.TotalMilliseconds:0}");
            return dist;
        }

        // exposed for /descent_route to price a traced detour path edge by edge in BOTH directions
        // (going down a chasm is cheap, climbing back out is not — a one-way field hides that).
        public static int StepCostPublic(int cx, int cy, int nx, int ny) => StepCost(cx, cy, nx, ny);

        // forward cost of moving FROM (nx,ny) TO (cx,cy): direction is (cx,cy)-(nx,ny), price set by the cell
        // being entered (cx,cy). reverse BFS expands neighbor nx,ny so we cost the forward step toward goal.
        // 人要【站在】这一格得先付多少。站得住=0;站不住就得自己造落脚点,那是真代价。
        // 判据只此一份(CellKind),不在这儿另编。
        static int StandPenalty(int x, int y)
        {
            switch (CellKind.Of(x, y))
            {
                case Cell.Stand: return 0;
                case Cell.Build: return JPlaceUp;
                case Cell.Pillar: return PillarUp;
                case Cell.Lava: return Impassable;
                default: return 0;   // Solid 归 StepCost 按挖掘算,别重复收费
            }
        }

        static int _fieldPickPower;   // best pick power captured at BuildField time (field is per-goal, rebuilt on new nav)
        // 建场那一刻身上有没有方块。DropLands 是【逐格】调的(110万格),
        // 在里面扫 58 格背包会把建场从 1.5s 拖成几十秒 —— 所以和 pickPower 一样,建场前取一次
        static bool _fieldLavaSurvivable;

        // 全身上下最好的镐力。【背包也算】-- 只扫热键栏 10 格的话,镐在背包里就等于 pickPower=0,
        // MineableWith 一律 false,所有要挖的格变 Impassable,Dijkstra 到不了地狱 = "没找到路线"。
        // 三处建场/失效判定原本各抄一遍这段循环,现在共用这一份。
        public static int BestPickPower()
        {
            var pl = Main.LocalPlayer;
            if (pl == null) return 0;
            int best = 0;
            for (int i = 0; i < 54 && i < pl.inventory.Length; i++)
            {
                var it = pl.inventory[i];
                if (it != null && !it.IsAir && it.pick > best) best = it.pick;
            }
            return best;
        }

        static int StepCost(int cx, int cy, int nx, int ny)
        {
            // 岩浆=重开,所以是【真禁行】不是"贵"。以前记的是有限高价,绕路一贵线就直接从岩浆里穿过去。
            // 而且人有 3 格高:只看脚下那格,贴着岩浆面走(头胸泡在里面)照样算干燥。
            for (int r = 0; r < 3; r++)
                if (IsLava(cx, cy - r)) return Impassable;
            // 压力板踩下去就触发,有的连着岩浆陷阱 —— 能绕就绕,绕不开也别当免费。
            for (int r = 0; r < 3; r++)
            {
                int py2 = cy - r;
                if (py2 < 0 || py2 >= Main.maxTilesY || cx < 0 || cx >= Main.maxTilesX) continue;
                var pt = Main.tile[cx, py2];
                if (pt.HasTile && (pt.TileType == TileID.PressurePlates || pt.TileType == TileID.WeightedPressurePlate))
                    return PlateCost;
            }
            // 人 3 行高,只看脚下那格会把"脚下空、头顶是石"的烟囱当免费爬(场爱钻它钻不过去的缝)。
            // 斜砖只在脚那行豁免:48px 的壳装 42px 的身子只剩 6px 余量,胸口一块斜砖就卡住。
            // 上锁的门另判:MineableWith 说门能挖,可神庙门没钥匙砸不开,不特判线就直接穿过去。
            for (int r = 0; r < 3; r++)
            {
                int dy2 = cy - r;
                if (dy2 < 0 || dy2 >= Main.maxTilesY || cx < 0 || cx >= Main.maxTilesX) continue;
                if (WorldGen.IsLockedDoor(cx, dy2)) return Impassable;
            }

            // 数出到底要挖几格,不只是"要不要挖":身体 3 行高,横着进一格得挖 1~3 格,价钱差 3 倍。
            // 一律按一格收费的话,一层薄壳和三层实心同价,场就分不出该从哪儿破。
            int digCells = 0;
            for (int r = 0; r < 3; r++)
            {
                bool solid = r == 0 ? PathPlanner.IsBlockPublic(cx, cy) : SolidAnyShape(cx, cy - r);
                if (!solid) continue;
                digCells++;
                if (!DigTable.MineableWith(cx, cy - r, _fieldPickPower)) return Impassable;   // 镐挖不动 → 真不可达,不是"贵"
            }
            if (PartialFooting(cx, cy) && SolidAnyShape(cx, cy - 3))
            {
                digCells++;
                if (!DigTable.MineableWith(cx, cy - 3, _fieldPickPower)) return Impassable;
            }
            bool wall = digCells > 0;
            // BODY WIDTH: the 20px body straddles TWO columns — a cell whose own column is open but whose left AND
            // right neighbor columns are both blocked (any of the 3 body rows) is a 1-tile-wide slot the body cannot
            // occupy. Pricing it as free flow let H stream up a 1-wide temple-wall shaft the body could never enter
            // ((3393,700): all 61 candidates H-rising, shock death) — the true route was digging the mineable east
            // rock. A side column counts as widenable if every solid row in it is mineable: then entering costs a dig;
            // if neither side is widenable the slot is impassable.
            if (!wall && !ColumnOpen(cx - 1, cy) && !ColumnOpen(cx + 1, cy))
            {
                wall = true;
                digCells = 1;   // 拓宽一格宽的缝:至少凿掉一侧的一格
                // 挖不开【不等于】进不去:人 20px 站在 16px 格心时左右各只探出 2px,
                // 一格宽的缝站得住,只是横着走不动,得靠竖直动作进出。
                // 一律 Impassable 会把这种格从场里整个抹掉 --- 腐化区被困死就是这样:
                // 唯一的出口 (1343,232) 探针说 can_stand=True,场却说不可达。
                if (!ColumnWidenable(cx - 1, cy) && !ColumnWidenable(cx + 1, cy))
                {
                    if (!CellKind.Stands(cx, cy)) return Impassable;
                    // 【按普通格收费】。挤进去本身不慢,慢的是出来 --- 而出来是下一格的事,
                    // StepCost 到那一格自然会按 pillar/跳放定价。在这儿再加价等于同一件事收两次钱。
                    wall = false; digCells = 0;
                }
            }
            // 上下走还要算【另外那半个身子】:人宽 20px 跨两列,竖直穿过去得挖两列。
            // 取左右里实心行少的那侧 —— 人可以站偏,挑便宜的那半边走。
            if (wall && cx == nx) digCells += System.Math.Min(SolidRows(cx - 1, cy), SolidRows(cx + 1, cy));
            bool horizontal = cx != nx;
            // 进这一格要不要自己造落脚点,由 CellKind 一处说了算。原来三个方向各判各的
            // (往下 DropLands、往上 nearSupport、横向不判),同一格在不同方向价钱不一样。
            var kind = wall ? Cell.Solid : CellKind.Of(cx, cy);
            // 造落脚点的价和方向无关:铺一块平台就是一块平台的钱
            int buildCost = kind == Cell.Build ? JPlaceUp : kind == Cell.Pillar ? PillarUp : 0;

            int baseCost;
            if (horizontal) baseCost = wall ? DigSide * digCells : MoveSide + buildCost;
            else if (cy > ny)
            {
                // y+ is down。掉下去便宜的【前提是掉得到底】:落得住才是 MoveDown,
                // 落不住就得自己铺一路下去,那是平台梯/柱子的价。
                if (wall) baseCost = DigDown * digCells;
                else baseCost = DropLands(cx, cy) ? MoveDown : MoveDown + buildCost;
            }
            else if (wall) baseCost = DigUp * digCells + DigUpLift;
            else
            {
                // 往上:一跳只能上 JumpReach 格,且要有东西垫脚。超出跳跃范围就只能自己搭,
                // 价按锚不锚得住分档。不加这条,天空就是全图最便宜的高速路(曾在 823,283 砌塔)
                baseCost = MoveUp;
                bool nearSupport = false;
                for (int d = 1; d <= JumpReach + 1; d++)
                    if (PathPlanner.IsFloorPublic(cx, cy + d)) { nearSupport = true; break; }
                if (!nearSupport) baseCost = MoveUp + buildCost;
            }
            // air penalty ONLY on HORIZONTAL entry into open air — that's "flying sideways", which doesn't exist.
            // VERTICAL moves are exempt: falling is the cheap intended descent, and climbing/jumping straight up a
            // wall face has the feet briefly unsupported too — penalizing it made an 18-cell climb-around (1458)
            // cost more than digging through the wall (1440), which is backwards.
            if (!wall && horizontal) baseCost += AirCost(cx, cy, nx - cx);
            return baseCost + MediumExtra(cx, cy);
        }

        // 从 (x,y) 掉下去,落不落得住。探到实处/平台=落得住;探完 DropProbe 还没底=不算,那是无底洞。
        //
        // 岩浆【不再是"落不住"】:身上有方块时掉进去有救(SurvivalReflex.LavaLevee 填方块把人抬出来)。
        // 没方块才照旧算落不住 —— 那种情况下去了是真出不来。
        // 注意这只放宽"往下掉",岩浆当【通路】那条(StepCost/StandPenalty)照旧 Impassable:
        // 松了的话贪心会主动领人趟岩浆湖,一路填方块过去。
        static bool DropLands(int x, int y)
        {
            for (int k = 1; k <= DropProbe; k++)
            {
                int yy = y + k;
                if (yy >= Main.maxTilesY) return false;
                if (IsLava(x, yy)) return _fieldLavaSurvivable;
                if (PathPlanner.IsFloorPublic(x, yy) || SolidAnyShape(x, yy)) return true;
            }
            return false;
        }
        const int DropProbe = 24;   // 比一次自由落体够用的深度;探太深每格都做会拖垮 110 万格的场

        // 放得住吗。【判据跟着料走】:
        //   平台 -> PlatformAnchor:3x3 邻域(含斜角),任何 tile 或背景墙(宽松)
        //   方块 -> BlockAnchor:只认【四邻】的实心,背景墙【也认】(严格在邻域,不在墙)
        // 岩浆格只能用方块(平台放进去当场烧没),所以那些格按方块的规则判 --
        // 按平台判的话场说能放、执行放不上,人对着同一格反复挥手。
        public static bool AnchorFor(int x, int y)
            => IsLava(x, y) ? BlockAnchor(x, y) : PlatformAnchor(x, y);

        // 方块放得住吗。四邻(不含斜角)有实心,【或者本格有背景墙】。
        //
        // 【墙这一条是必须的】。原来借用 ItemUseCoordinator.HasAnchor,可那份是给【绳子】
        // 写的(注释写着"绳子只能从已有绳子或天花板往下接"),绳子不认墙 —— 拿它当方块的判据,
        // 在有墙的地方会把放得下的格判成放不下。地狱要塞(Ruined Houses)就是【有墙又有岩浆】的
        // 地形,正好踩中:场说不可达,人却明明能在那儿架方块过去。
        public static bool BlockAnchor(int x, int y)
        {
            if (x < 0 || y < 0 || x >= Main.maxTilesX || y >= Main.maxTilesY) return false;
            // 本格有背景墙就锚得住 —— 墙是贴在这一格上的,不用看邻居
            if (Main.tile[x, y].WallType > 0) return true;
            int[] dx = { 0, 0, -1, 1 }, dy = { -1, 1, 0, 0 };
            for (int i = 0; i < 4; i++)
            {
                int nx = x + dx[i], ny = y + dy[i];
                if (nx < 0 || ny < 0 || nx >= Main.maxTilesX || ny >= Main.maxTilesY) continue;
                var t = Main.tile[nx, ny];
                if (!t.HasTile) continue;
                // 实心和平台都撑得住方块;草/藤那些 tileCut 的撑不住
                if (Main.tileSolid[t.TileType] || Main.tileSolidTop[t.TileType]) return true;
            }
            return false;
        }

        // a platform placed at (x,y) would have something to attach to — same 3x3 neighborhood rule as the planner's
        // CanPlaceReal: ANY tile (grass, vine, rubble, tree trunk...) or back wall anchors it.
        public static bool PlatformAnchor(int x, int y)
        {
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= Main.maxTilesX || ny >= Main.maxTilesY) continue;
                    var t = Main.tile[nx, ny];
                    if (t.HasTile || t.WallType > 0) return true;
                }
            return false;
        }

        // 3 body rows of column c are open at stand height cy (feet row keeps the slope/half footing exemption)
        static bool ColumnOpen(int c, int cy)
            => !PathPlanner.IsBlockPublic(c, cy) && !SolidAnyShape(c, cy - 1) && !SolidAnyShape(c, cy - 2);

        // c 列在站立高度 cy 上有几行实心 = 竖直穿过去要在这一列挖几格
        static int SolidRows(int c, int cy)
        {
            int n = 0;
            for (int r = 0; r < 3; r++) if (SolidAnyShape(c, cy - r)) n++;
            return n;
        }

        // column c can be MINED open at stand height cy: every solid body row is mineable with the field's pick
        static bool ColumnWidenable(int c, int cy)
        {
            for (int r = 0; r < 3; r++)
                if (SolidAnyShape(c, cy - r) && !DigTable.MineableWith(c, cy - r, _fieldPickPower)) return false;
            return true;
        }

        // solid of ANY shape (full, slope, half-brick) — what the body envelope collides with above the feet row
        static bool SolidAnyShape(int x, int y)
        {
            // 越界当实心(和别处相反):场的边界外不许走
            if (x < 0 || y < 0 || x >= Main.maxTilesX || y >= Main.maxTilesY) return true;
            return Predicates.IsWall(x, y);
        }

        // slope/half-brick in the feet cell: standable, but the feet ride 6-16px up inside the row
        static bool PartialFooting(int x, int y)
        {
            if (x < 0 || y < 0 || x >= Main.maxTilesX || y >= Main.maxTilesY) return false;
            var t = Main.tile[x, y];
            return Predicates.IsWall(x, y)
                && ((int)t.Slope != 0 || t.IsHalfBlock);
        }

        // Cost of stepping sideways into open air. Two factors, both real costs (NO judgement here — the field weighs
        // "float across" vs "go down the slope and back up" itself; we only price floating honestly):
        //   • DEPTH h: how far below is the floor. Small h (a sub-jump dip, ≤ FreeAir≈9) is free — a real jump clears it.
        //   • SPAN L: how many cells of CONTINUOUS sideways air lie ahead in the travel direction before the ground
        //     returns. A long unsupported horizontal run is what the body physically can't do (no walk/jump crosses 13
        //     blank cells) — the longer the span, the costlier per cell, so a long float's TOTAL price climbs past the
        //     down-slope-and-up alternative and the field switches to descending. A short span (≤ a jump's reach) stays
        //     cheap so narrow pits are still floated/jumped. Span dominates depth: a deep-but-narrow chasm is jumpable;
        //     a long shallow ledge-gap is the thing to avoid sailing over.
        static int AirCost(int cx, int cy, int dir)
        {
            int h = MaxAirProbe;
            for (int d = 1; d <= MaxAirProbe; d++)
                if (PathPlanner.IsFloorPublic(cx, cy + d)) { h = d; break; }
            if (h <= FreeAir) return 0;                       // shallow dip a jump clears → free, regardless of span
            // span: continuous sideways air ahead before the floor (at this row's reach) returns
            int span = 0;
            for (int s = 1; s <= AirSpanProbe; s++)
            {
                int tx = cx + dir * s;
                bool airHere = !PathPlanner.IsBlockPublic(tx, cy) && !PathPlanner.IsFloorPublic(tx, cy + 1);
                if (!airHere) break;
                span++;
            }
            float hp = h - FreeAir;
            float depthW = AirCap * hp / (hp + AirSat);        // saturating depth term (unchanged shape)
            // span term: per-cell cost grows with span so a long float is super-linear in total. AirSpanFree cells are
            // free (a jump's horizontal reach); beyond that each cell costs AirSpanK*(span-AirSpanFree).
            float spanExtra = span > AirSpanFree ? AirSpanK * (span - AirSpanFree) : 0f;
            return (int)(depthW + spanExtra);
        }

        static (List<(int, int)>, int) DescendPath(Dictionary<(int, int), int> field, int sx, int sy, int gx, int gy, bool quiet = false)
        {
            var path = new List<(int, int)>();
            int breaks = 0;
            var cur = (sx, sy);
            if (!field.ContainsKey(cur)) return (path, breaks);

            var seen = new HashSet<(int, int)>();
            for (int step = 0; step < 20000; step++)
            {
                path.Add(cur);
                if (cur == (gx, gy)) break;
                if (!seen.Add(cur)) break;
                // pick the neighbor that reconstructs the Dijkstra-optimal path: minimize (cost OF this step) +
                // (neighbor's remaining cost to goal). Using only field[n] ignores the step cost, so the greedy walk
                // can slide onto a cheap-looking neighbor via an expensive edge (e.g. a horizontal hop into deep air)
                // — drawing a route the field never actually priced that way.
                int bestTotal = int.MaxValue; var best = cur;
                foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    var n = (cur.Item1 + dx, cur.Item2 + dy);
                    if (!field.TryGetValue(n, out int dn)) continue;
                    int sc0 = StepCost(n.Item1, n.Item2, cur.Item1, cur.Item2);
                    if (sc0 == Impassable) continue;   // int.MaxValue + dn 会溢出成大负数,那一格反而变成"最优"
                    int total = sc0 + dn;
                    if (total < bestTotal) { bestTotal = total; best = n; }
                }
                if (best == cur) break;
                cur = best;
            }
            // per-step cost breakdown for a small probe (a few dozen cells with a wall in the middle): show why the
            // field picked THIS route — direction, walk vs dig, the air penalty, and the running field value.
            if (!quiet && path.Count <= 3000)
            {
                int walk = 0, dig = 0;
                for (int i = 1; i < path.Count; i++)
                {
                    var (px, py) = path[i - 1];
                    var (cxk, cyk) = path[i];
                    bool wall = PathPlanner.IsBlockPublic(cxk, cyk);
                    bool down = cyk > py;
                    string dir = cxk != px ? (cxk > px ? "R" : "L") : (down ? "D" : "U");
                    int sc = StepCost(cxk, cyk, px, py);
                    int air = (!wall && !down) ? AirCost(cxk, cyk, cxk - px) : 0;
                    if (wall) dig++; else walk++;
                    DiagLog.Trc($"[maze-step] {i} {dir} ({cxk},{cyk}) {(wall ? "DIG" : "walk")} stepCost={sc} air={air} field={field[path[i]]}");
                }
                DiagLog.Trc($"[maze-detail] len={path.Count} walk={walk} dig={dig} totalCost={(field.TryGetValue((sx, sy), out int tc) ? tc : -1)}");
            }
            return (path, breaks);
        }

        static void DrawHeatmap(Dictionary<(int, int), int> field, int sx, int sy, int gx, int gy)
        {
            // normalize by the start cell's cost so the gradient spreads across the actual start→goal range;
            // cells farther than start clamp to red. (using global max washes everything green — a few far
            // dig-heavy cells blow up the scale.)
            float scale = field.TryGetValue((sx, sy), out int sc) && sc > 0 ? sc : 1f;
            var tiles = new List<(int, int, Color)>();
            foreach (var kv in field)
            {
                float t = System.Math.Min(1f, kv.Value / scale);
                var c = new Color(t, 1f - t, 0.2f) * 0.5f;
                tiles.Add((kv.Key.Item1, kv.Key.Item2, c));
            }
            DiagLog.Write($"[maze-field] fieldSize={field.Count} scale={scale} startInField={field.ContainsKey((sx, sy))}");
            PathVisSystem.SetTiles(tiles);
        }

        public override void AddRecipes()
        {
            CreateRecipe().AddIngredient(ItemID.DirtBlock, 1).AddTile(TileID.WorkBenches).Register();
        }
    }
}
