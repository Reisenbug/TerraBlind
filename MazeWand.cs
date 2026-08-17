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
        const int LavaCost = 100000;
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
        static volatile bool _mazeBusy;

        // J 开关朝 point2 的 receding nav,拿大场当罗盘。从玩家【当前】位置起步 ——
        // 场是按目标缓存的、哪儿都有效,所以建完场再走远也不影响。
        public static void ToggleNav()
        {
            DiagLog.Write("[maze-nav] J pressed");
            if (RecedingNav.Active) { RecedingNav.Stop(); Main.NewText("[TerraBlind] receding nav OFF"); return; }
            if (!_p2.HasValue) { DiagLog.Write("[maze-nav] J → no point2 (goal) set"); Main.NewText("[TerraBlind] set point2 (right-click) first"); return; }
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
            int best = 0; var pl = Main.LocalPlayer;
            if (pl != null)
                for (int i = 0; i < 10; i++) { var it = pl.inventory[i]; if (it != null && !it.IsAir && it.pick > best) best = it.pick; }
            return best != _fieldPickPower;
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
            // the field must only price digs the CURRENT pick can actually perform — a flat DigSide/DigDown for every
            // wall routed the line straight through the Lihzahrd temple (pick damage 0 before Picksaw): Expand's
            // generators correctly refuse the unmineable dig, only backward edges remain, loop. Capture the pick
            // power once per build; StepCost prices undiggable walls like lava (impassable-expensive, still finite
            // so a genuinely sealed goal degrades to "walled in" instead of a broken field).
            _fieldPickPower = 0;
            var pl = Main.LocalPlayer;
            if (pl != null)
                for (int i = 0; i < 10; i++) { var it = pl.inventory[i]; if (it != null && !it.IsAir && it.pick > _fieldPickPower) _fieldPickPower = it.pick; }
            // 边距 = 让路线有绕开障碍的余地。固定 120 是按长途定的:两个相隔 10 格的宝藏之间也要 flood
            // 250×250,一趟 80-160ms,穿宝线上二十几段串起来就是好几秒(/descent_route 10s 超时的主因)。
            // 按段长缩放:短段给小盒子,长段照旧。下限 40 够绕开一般地形。
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
                    int nc = cost + sc;
                    if (dist.TryGetValue((nx, ny), out int old) && nc >= old) continue;
                    dist[(nx, ny)] = nc;
                    pq.Add((nc, nx, ny));
                }
            }
            // WHO built this, and was it on the main thread? Field builds are ~500ms over ~650k cells; several
            // callers reach BuildField by different routes and the log recorded none of them, so a rebuild storm
            // could be seen but not attributed.
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

        // Multi-source Dijkstra: every source seeds cost 0. /find_descent floods UP from the whole hell band at
        // once — the surface cell with the lowest H then marks the cheapest REAL route down. Descent cost is
        // topological, not per-column: an S-shaped cave entered from its top beats digging straight anywhere.
        public static Dictionary<(int, int), int> BuildFieldMulti(System.Collections.Generic.List<(int x, int y)> sources, int minX, int maxX, int minY, int maxY)
        {
            _fieldPickPower = 0;
            var pl = Main.LocalPlayer;
            if (pl != null)
                for (int i = 0; i < 10; i++) { var it = pl.inventory[i]; if (it != null && !it.IsAir && it.pick > _fieldPickPower) _fieldPickPower = it.pick; }

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
                    int nc = cost + sc;
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
        static int _fieldPickPower;   // best pick power captured at BuildField time (field is per-goal, rebuilt on new nav)

        static int StepCost(int cx, int cy, int nx, int ny)
        {
            if (IsLava(cx, cy)) return LavaCost;   // entering lava = death; treat as impassable
            // BODY CLEARANCE: the player is 3 tiles tall — "standing in cell (cx,cy)" occupies rows cy..cy-2 of the
            // column. Judging wall-ness by the feet cell alone priced a chimney whose feet-row is air but whose head
            // rows are rock as a FREE climb (MoveUp=9) when the body actually has to mine its way up — the field
            // loved tight shafts it couldn't fit through, the line threaded them, and the bot obediently dug upward
            // where pillar/bridge routes were genuinely cheaper. A move is a dig if ANY of the 3 body rows is solid,
            // and every solid row must be mineable with the current pick.
            // SLOPES/HALF-BRICKS: the 48px 3-row envelope holds the 42px body with only 6px of slack. IsBlock exempts
            // sloped/half tiles (walkable FOOTING via StepUp), but that exemption is a lie anywhere else: a diagonal at
            // chest/head level eats the slack ~6px into the tile (real SlopeCollision jams the walk), and PARTIAL FOOTING
            // (feet standing 6-8px up a slope/half-brick) pushes the head into the 4th row (42+6 > 48). So: feet row
            // keeps the exemption, upper rows count ANY solid shape, and partial footing extends the envelope to cy-3.
            // 上锁的门(神庙门)身体3行里有一个就过不去:钥匙没有、砸也砸不开。
            // MineableWith 对门返回 true(门本身能挖),所以不特判的话线会直接穿过去。
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
                if (!ColumnWidenable(cx - 1, cy) && !ColumnWidenable(cx + 1, cy)) return Impassable;
            }
            // 上下走还要算【另外那半个身子】:人宽 20px 跨两列,竖直穿过去得挖两列。
            // 取左右里实心行少的那侧 —— 人可以站偏,挑便宜的那半边走。
            if (wall && cx == nx) digCells += System.Math.Min(SolidRows(cx - 1, cy), SolidRows(cx + 1, cy));
            bool horizontal = cx != nx;
            int baseCost;
            if (horizontal) baseCost = wall ? DigSide * digCells : MoveSide;
            else if (cy > ny) baseCost = wall ? DigDown * digCells : MoveDown;    // y+ is down
            else if (wall) baseCost = DigUp * digCells + DigUpLift;
            else
            {
                // ascending into open air: a jump only reaches ~JumpReach cells above support — beyond that the body
                // can't climb air, it can only PILLAR (slow, consumes platforms). Pricing all vertical air at MoveUp=9
                // made the open sky the cheapest highway on the map the moment ground routes got honest body-clearance
                // pricing: the line went skyward and the bot built a tower at (823,283→257). Price beyond-jump ascent
                // as the pillar it really is (also sits well below DigUp=160, so pillar beats digging up — as it should).
                baseCost = MoveUp;
                bool nearSupport = false;
                for (int d = 1; d <= JumpReach + 1; d++)
                    if (PathPlanner.IsFloorPublic(cx, cy + d)) { nearSupport = true; break; }
                // PLATFORMS ARE POWERFUL: they anchor to nearly anything — grass, vines, rubble, tree trunks, back
                // walls. Air beyond jump reach that has an anchor nearby supports a jump-place LADDER (one jump gains
                // 4-6 cells), ~3× faster than the anchorless pillar cycle — price it as the ladder, not the pillar.
                // One rate for all unsupported air made a bare cliff column beat the tree-side ladder next to it.
                if (!nearSupport) baseCost = PlatformAnchor(cx, cy) ? JPlaceUp : PillarUp;
            }
            // air penalty ONLY on HORIZONTAL entry into open air — that's "flying sideways", which doesn't exist.
            // VERTICAL moves are exempt: falling is the cheap intended descent, and climbing/jumping straight up a
            // wall face has the feet briefly unsupported too — penalizing it made an 18-cell climb-around (1458)
            // cost more than digging through the wall (1440), which is backwards.
            if (!wall && horizontal) baseCost += AirCost(cx, cy, nx - cx);
            return baseCost + MediumExtra(cx, cy);
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
