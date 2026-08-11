using Terraria;
using Terraria.ModLoader;

namespace TerraBlind
{
    public enum SkillState { Idle, PillarBuild, PillarWait, PillarLaunch, DigDown, DigLeft, DigRight, DigUp }

    public class SkillExecutor : ModSystem
    {
        public static volatile SkillState State = SkillState.Idle;
        public static volatile bool DirectionRight = true;

        private static int _jumpFramesLeft;
        private static int _cycleTick;
        private static int _cyclesDone;
        private static int _phaseTick;
        private static bool _placeStarted;
        private static int _targetWy;
        private static int _stalledCycles;
        private static int _cycleStartFeetY;
        private static int _anchorWy;      // 下一块要放的行(锚点上面那格)
        private static int _pillarCol;     // 柱子那一列
        private static int _pinnedCol;     // 调用方指定的列(int.MinValue=没指定,每跳自己找)
        private static int _airFrames;     // 这一跳飞了多久,落地判据
        private static int _jumpStartFeetY;
        private static int _jumps, _totalFrames;

        private static float _pillarWaitPrevVY;
        private static int _pillarWaitFellTicks;

        private const int WaitFrames = 10;
        private const int JumpHoldFrames = 9;   // 实测 10 格:7帧=156, 9帧=133(3,2,3,2), 11帧更慢。手是瓶颈,跳太高只是干等
        private const int LaunchFrames = 20;


        public static bool IsActive => State != SkillState.Idle;

        // col:柱子钉在哪一列。不传就每跳按脚下支撑现找 —— 人在两列间飘时那个结果会跳变,
        // 锚点跟着换列,一块也砌不稳(规划说 2906,执行给 2907,50 条 pillar 只中 5 条)。
        public static void StartPillarJump(bool dirRight, int targetWy, bool trace = true, int col = int.MinValue)
        {
            PillarTrace = trace;
            _pinnedCol = col;
            DirectionRight = dirRight;
            _targetWy = targetWy;
            _jumpFramesLeft = 0;
            _cycleTick = 0;
            _cyclesDone = 0;
            _phaseTick = 0;
            _placeStarted = false;
            _stalledCycles = 0;
            _cycleStartFeetY = 0;
            _airFrames = 0;
            _jumpStartFeetY = 0;
            _anchorWy = int.MaxValue;
            _pillarCol = _pinnedCol != int.MinValue ? _pinnedCol : 0;
            _jumps = 0; _totalFrames = 0;
            _nudgeFrames = 0;
            State = SkillState.PillarBuild;
            DiagLog.Write($"[pillar] start dirRight={dirRight} targetWy={targetWy}");
        }

        public static void StartDigDown()  { State = SkillState.DigDown; }
        public static void StartDigLeft()  { State = SkillState.DigLeft; }
        public static void StartDigRight() { State = SkillState.DigRight; }
        public static void StartDigUp()    { State = SkillState.DigUp; }

        public static void Stop()
        {
            State = SkillState.Idle;
            PlaceCoordinator.Stop();
        }


        static string _placeVeto = "";
        static string _lastAirVeto = "";
        static int _nudgeFrames;
        private const int NudgeGrace = 20;
        // 逐帧拍下整个放置窗口:跳多高、箱子在哪、够不够得着、手好了没 —— 定不下窗口就别改跳跃帧数
        public static bool PillarTrace = false;

        // 头顶到第一个实心块还有几格:跳不满就是被它挡的
        static int HeadClear(Player p, int col)
        {
            int top = (int)(p.position.Y / 16f);
            for (int n = 0; n < 12; n++)
                if (Predicates.IsSolid(col, top - 1 - n)) return n;
            return 12;
        }

        // 这一格现在放得进去吗:身体没占着 + 够得着 + 有邻居可贴 + 那格是空的
        static bool CanPlaceNow(Player p, int x, int y)
        {
            if (!Predicates.InBounds(x, y)) { _placeVeto = "oob"; return false; }
            if (Main.tile[x, y].HasTile) { _placeVeto = "occupied"; return false; }
            int bl = (int)(p.position.X / 16f), br = (int)((p.position.X + p.width - 1) / 16f);
            int bt = (int)(p.position.Y / 16f), bb = (int)((p.position.Y + p.height - 1) / 16f);
            if (x >= bl && x <= br && y >= bt && y <= bb) { _placeVeto = $"in_body[{bl}..{br}]x[{bt}..{bb}]"; return false; }
            if (!p.IsInTileInteractionRange(x, y, Terraria.DataStructures.TileReachCheckSettings.Simple)) { _placeVeto = "out_of_reach"; return false; }
            if (!PlatAnchor(x, y)) { _placeVeto = "no_anchor"; return false; }
            _placeVeto = ""; return true;
        }

        // 身体跨的那几列里,脚下真踩着东西的是哪一列。都没有就退回中心列。
        static int FootedCol(Player p, int feetY)
        {
            int bl = (int)(p.position.X / 16f), br = (int)((p.position.X + p.width - 1) / 16f);
            int mid = Pcx(p);
            if (Predicates.IsSolid(mid, SupportRow(mid, feetY))) return mid;   // 中心列站得住就用它
            for (int c = bl; c <= br; c++)
                if (Predicates.IsSolid(c, SupportRow(c, feetY))) return c;
            return mid;
        }

        // 撑住人的是哪一行:feetY 自己算不得数,斜坡上人陷进去了。往下找到第一个实心行。
        static int SupportRow(int col, int feetY)
        {
            for (int y = feetY; y <= feetY + 2; y++)
                if (Predicates.IsSolid(col, y)) return y;
            return feetY;
        }

        // 平台要贴着邻居才放得出(原版会拒 no_anchor)
        static bool PlatAnchor(int x, int y)
        {
            (int dx, int dy)[] n = { (0, -1), (0, 1), (-1, 0), (1, 0) };
            foreach (var (dx, dy) in n)
            {
                int a = x + dx, b = y + dy;
                if (!Predicates.InBounds(a, b)) continue;
                var t = Main.tile[a, b];
                if (t.HasTile && (Main.tileSolid[t.TileType] || Main.tileSolidTop[t.TileType])) return true;
            }
            return false;
        }

        private static int Pcx(Player p) => (int)((p.position.X + p.width / 2f) / 16f);

        // 规划和执行共用这一个判据,否则图里会有执行不了的假边。返回脚能升到的最高行。
        public static bool CanPillarFrom(int feetCx, int feetCy, out int topFeetY)
        {
            topFeetY = feetCy;
            var p = Main.LocalPlayer;
            if (p == null || !p.active) return false;
            if (FindPlatformSlot(p) < 0) return false;

            // player box left/right columns when standing centered on feetCx
            float px = feetCx * 16f + 8f - p.width / 2f;
            int leftCol = (int)(px / 16f);
            int rightCol = (int)((px + p.width - 1) / 16f);

            // a tree (or other non-cuttable non-solid tile) in the body columns blocks platform placement → the
            // pillar would stall in place. reject the edge so the planner routes around instead of replanning.
            for (int y = feetCy; y >= feetCy - 2; y--)
                if (BlocksPlacement(leftCol, y) || BlocksPlacement(rightCol, y)) { topFeetY = feetCy; return false; }

            // 升 2 格后身体新进入 feetY-3/-4 两行,这两行不干净就会把头卡进墙里
            int feetY = feetCy;
            int reached = feetCy;
            for (int step = 0; step < 40; step++)
            {
                int newRowA = feetY - 3, newRowB = feetY - 4; // rows the body newly enters on the next 2-tile climb
                bool blocked = IsSolidNonPlatform(leftCol, newRowA) || IsSolidNonPlatform(rightCol, newRowA)
                             || IsSolidNonPlatform(leftCol, newRowB) || IsSolidNonPlatform(rightCol, newRowB);
                if (blocked) break;
                feetY -= 2;
                reached = feetY;
            }
            topFeetY = reached;
            if (reached >= feetCy) return false; // can't gain any height

            // jump must not be boxed in at the start (hold=15 vertical rise ≥ 10px, allowing a small left/right nudge)
            var ph = PhysicsSimulator.Params.FromPlayer(p);
            float startPy = feetCy * 16f + 16f - p.height; // feet on (feetCy+1) floor top → top-left py
            bool RiseOk(float ox)
            {
                var sim = PhysicsSimulator.SimulateJump(new PhysicsSimulator.State { Px = px + ox, Py = startPy, Vx = 0f, Vy = 0f, Grounded = true, JumpFramesLeft = 15 }, 0, 15, ph);
                return startPy - sim.MinPy >= 10f;
            }
            if (!RiseOk(0f) && !RiseOk(-4f) && !RiseOk(4f)) return false;
            return true;
        }

        static bool IsSolidNonPlatform(int x, int y)
        {
            if (x < 0 || y < 0 || x >= Main.maxTilesX || y >= Main.maxTilesY) return false;
            var t = Main.tile[x, y];
            return t.HasTile && Main.tileSolid[t.TileType] && !Main.tileSolidTop[t.TileType];
        }

        // a tile the platform can't be placed into: HasTile and not cuttable (trees, vines etc. are non-solid so
        // IsSolidNonPlatform misses them, but they still block UseItem placement → pillar stalls in place).
        static bool BlocksPlacement(int x, int y)
        {
            if (x < 0 || y < 0 || x >= Main.maxTilesX || y >= Main.maxTilesY) return false;
            var t = Main.tile[x, y];
            return t.HasTile && !Main.tileCut[t.TileType] && !Main.tileSolid[t.TileType];
        }

        static bool IsPlatformItem(Terraria.Item item)
        {
            if (item == null || item.IsAir || item.createTile < 0) return false;
            var td = Terraria.ID.TileID.Sets.Platforms;
            return td != null && item.createTile < td.Length && td[item.createTile];
        }

        // 热键栏没有就从背包搬一叠上来 —— selectedItem 只认 0-9。补货是 PlatformStock 的事,这里不管。
        private static int FindPlatformSlot(Player p)
        {
            for (int i = 0; i < 10; i++)
                if (IsPlatformItem(p.inventory[i])) return i;

            int fromBag = -1;
            for (int i = 10; i < p.inventory.Length; i++)
                if (IsPlatformItem(p.inventory[i])) { fromBag = i; break; }
            if (fromBag < 0) return -1;

            int hb = 0;
            for (int i = 0; i < 10; i++)
                if (p.inventory[i] == null || p.inventory[i].IsAir) { hb = i; break; }
            var tmp = p.inventory[hb]; p.inventory[hb] = p.inventory[fromBag]; p.inventory[fromBag] = tmp;
            return hb;
        }

        private static int FindPickaxeSlot(Player p)
        {
            for (int i = 0; i < 10; i++)
            {
                var item = p.inventory[i];
                if (item != null && !item.IsAir && item.pick > 0)
                    return i;
            }
            return -1;
        }

        public static void ApplyControls()
        {
            if (State == SkillState.Idle) return;
            var p = Main.LocalPlayer;
            if (p == null || !p.active) { DiagLog.Write("[pillar] stop: no player"); Stop(); return; }

            int platformSlot = FindPlatformSlot(p);

            if (State == SkillState.PillarBuild)
            {
                if (platformSlot < 0) { DiagLog.Write("[pillar] stop: no platform slot"); Stop(); return; }
                int feetYNow = (int)((p.position.Y + p.height) / 16f);
                int pcxNow = Pcx(p);
                // 只有脚踏实地时问"能不能起跳"才有意义:人在空中头顶本来就贴着方块,一算就说跳不起来,
                // 于是一跳到半路把自己判死 —— 平台其实已经放出去了
                var nudgePh = PhysicsSimulator.Params.FromPlayer(p);
                bool onGround = p.velocity.Y == 0f;
                bool jumpBlocked = false;
                if (onGround)
                {
                    var nudgeState = new PhysicsSimulator.State { Px = p.position.X, Py = p.position.Y, Vx = 0f, Vy = 0f, Grounded = true, JumpFramesLeft = 15 };
                    var nudgeSim = PhysicsSimulator.SimulateJump(nudgeState, 0, 15, nudgePh);
                    jumpBlocked = p.position.Y - nudgeSim.MinPy < 10f; // 正常无遮挡上升 93.46px;<10 就是被挡住了
                }
                // 挪一下是【下一帧】才生效的:同一帧拿没变的位置再判一次,必然还是挡着 —— 以前就是这样
                // 把自己判死的,nudge 写了等于没写。所以设完输入就让开这一帧,给它时间挪。
                if (jumpBlocked)
                {
                    float shift = 4f;
                    var simL = PhysicsSimulator.SimulateJump(new PhysicsSimulator.State { Px = p.position.X - shift, Py = p.position.Y, Vx = 0f, Vy = 0f, Grounded = true, JumpFramesLeft = 15 }, 0, 15, nudgePh);
                    var simR = PhysicsSimulator.SimulateJump(new PhysicsSimulator.State { Px = p.position.X + shift, Py = p.position.Y, Vx = 0f, Vy = 0f, Grounded = true, JumpFramesLeft = 15 }, 0, 15, nudgePh);
                    bool clearLeft = p.position.Y - simL.MinPy >= 10f;
                    bool clearRight = p.position.Y - simR.MinPy >= 10f;
                    if (clearLeft != clearRight && ++_nudgeFrames <= NudgeGrace)
                    {
                        if (_nudgeFrames == 1)
                            DiagLog.Write($"[pillar_nudge] blocked px={p.position.X:0.##} clearLeft={clearLeft} clearRight={clearRight} 挪开试试");
                        if (clearLeft) p.controlLeft = true; else p.controlRight = true;
                        return;
                    }
                    if (_nudgeFrames > NudgeGrace)
                    {
                        DiagLog.Write($"[pillar] 挪了{NudgeGrace}帧还是跳不起来 px={p.position.X:0.##},放弃");
                        Stop(); return;
                    }
                    // 两边都不行(或两边都行却还是跳不动):挪也没用,如实放弃
                    DiagLog.Write($"[pillar] 跳不起来 px={p.position.X:0.##} clearLeft={clearLeft} clearRight={clearRight},放弃");
                    Stop(); return;
                }
                _nudgeFrames = 0;
                // 闭环:跳一次能放几格就放几格。人一路上升,射程窗口跟着走,头顶那格进射程就放,
                // 放成功锚点上移,继续找下一格。不回放录制 —— 录制写死一 cycle 2 格,地形一变就错。
                _totalFrames++;
                // 脚下真有东西才算到 —— 只比行号会在起步那一帧就"到了"(脚下地面行本来就等于目标行),
                // 调用方按身体行判又说没到,两边基准差一格 → start/done 空转
                if (feetYNow <= _targetWy && p.velocity.Y == 0f
                    && Predicates.IsGround(Pcx(p), feetYNow))
                {
                    DiagLog.Write($"[pillar] done feetY={feetYNow} target={_targetWy} placed={_cyclesDone} jumps={_jumps} frames={_totalFrames} hold={JumpHoldFrames}");
                    Stop(); return;
                }
                bool grounded = p.velocity.Y == 0f;
                if (grounded)
                {
                    if (_airFrames > 0)   // 刚落地:这一跳到底升了没有
                    {
                        if (feetYNow >= _jumpStartFeetY)
                        {
                            _stalledCycles++;
                            DiagLog.Write($"[pillar] stall feetY={feetYNow} was={_jumpStartFeetY} stalls={_stalledCycles} anchor=({_pillarCol},{_anchorWy}) target={_targetWy} veto={_placeVeto} placed={_cyclesDone}");
                            if (_stalledCycles >= 3) { DiagLog.Write("[pillar] stop: no progress"); Stop(); return; }
                        }
                        else _stalledCycles = 0;
                        _airFrames = 0;
                    }
                    if (_jumpStartFeetY != 0 && _jumpStartFeetY != feetYNow)
                        DiagLog.Write($"[pillar] jump#{_jumps} rose={_jumpStartFeetY - feetYNow} feetY={feetYNow}");
                    _jumpStartFeetY = feetYNow;
                    // 优先用调用方钉的列;那列脚下真没支撑才现找【踩着东西】的列 ——
                    // 中心列可能悬空在两列之间,锚点贴不住,一块也放不出来(2711站着,却往2712放)。
                    bool pinnedOk = _pinnedCol != int.MinValue && Predicates.IsSolid(_pinnedCol, SupportRow(_pinnedCol, feetYNow));
                    _pillarCol = pinnedOk ? _pinnedCol : FootedCol(p, feetYNow);
                    _anchorWy = SupportRow(_pillarCol, feetYNow) - 1;
                    _jumpFramesLeft = JumpHoldFrames;
                    _lastAirVeto = "";
                    _jumps++;
                }
                if (_jumpFramesLeft > 0) { p.controlJump = true; _jumpFramesLeft--; }
                if (!grounded) _airFrames++;

                // 锚点已经有东西了(上一帧放上的)→ 往上找下一个空格
                while (_anchorWy > _targetWy - 1 && Main.tile[_pillarCol, _anchorWy].HasTile) _anchorWy--;

                bool canPlace = _anchorWy >= _targetWy - 1 && CanPlaceNow(p, _pillarCol, _anchorWy);
                if (PillarTrace)
                {
                    int bl = (int)(p.position.X / 16f), br = (int)((p.position.X + p.width - 1) / 16f);
                    int bt = (int)(p.position.Y / 16f), bb = (int)((p.position.Y + p.height - 1) / 16f);
                    var itNow = p.inventory[platformSlot];
                    int useT = (itNow != null && !itNow.IsAir) ? itNow.useTime : -1;
                    float ddx = (_pillarCol * 16f + 8f) - p.Center.X, ddy = (_anchorWy * 16f + 8f) - p.Center.Y;
                    float dPx = System.MathF.Sqrt(ddx * ddx + ddy * ddy) / 16f;
                    DiagLog.Trc($"[ptrace] f={_totalFrames} feetY={feetYNow} vy={p.velocity.Y:0.##} hold={_jumpFramesLeft} "
                        + $"box=[{bl}..{br}]x[{bt}..{bb}] anchor=({_pillarCol},{_anchorWy}) dist={dPx:0.0}格 "
                        + $"itemTime={p.itemTime} useTime={useT} head={HeadClear(p, _pillarCol)} veto={(canPlace ? "OK" : _placeVeto)}");
                }
                if (canPlace)
                {
                    p.selectedItem = platformSlot;
                    Main.SmartCursorWanted_Mouse = false;
                    Main.mouseX = (int)(_pillarCol * 16f + 8f - Main.screenPosition.X);
                    Main.mouseY = (int)(_anchorWy * 16f + 8f - Main.screenPosition.Y);
                    if (p.itemTime == 0) { p.controlUseItem = true; _cyclesDone++; }
                }
                return;
            }

            if (State == SkillState.PillarWait)
            {
                float vy = p.velocity.Y;
                int feetYNow = (int)((p.position.Y + p.height) / 16f);
                bool grounded = vy == 0f && _pillarWaitPrevVY >= 0f;
                bool atTarget = feetYNow <= _targetWy + 1;
                DiagLog.Write($"[pillar_wait_tick] tick={Main.GameUpdateCount} vy={vy} prevVY={_pillarWaitPrevVY} grounded={grounded} atTarget={atTarget}");
                if (grounded && atTarget)
                {
                    DiagLog.Write($"[pillar_wait_exit] tick={Main.GameUpdateCount} reason=grounded_at_target vy={vy} prevVY={_pillarWaitPrevVY} feetY={feetYNow} targetWy={_targetWy}");
                    Stop();
                }
                // 掉回目标下面 = 这次爬完了,交还控制权重新规划;没这个分支会干等 77 秒
                else if (grounded && !atTarget && ++_pillarWaitFellTicks >= 10)
                {
                    DiagLog.Write($"[pillar_wait_exit] tick={Main.GameUpdateCount} reason=fell_back_below_target feetY={feetYNow} targetWy={_targetWy}");
                    Stop();
                }
                _pillarWaitPrevVY = vy;
                return;
            }

            if (State == SkillState.DigDown)
            {
                int slot = FindPickaxeSlot(p);
                if (slot < 0) { Stop(); return; }
                int feetTileY = (int)((p.position.Y + p.height) / 16f);
                int leftTileX  = (int)(p.position.X / 16f);
                int rightTileX = (int)((p.position.X + p.width - 1f) / 16f);
                int targetX = -1;
                for (int col = leftTileX; col <= rightTileX; col++)
                    if (Main.tile[col, feetTileY].HasTile) { targetX = col; break; }
                if (targetX < 0) return;
                SetMouse(p, targetX * 16f + 8f, feetTileY * 16f + 8f);
                p.selectedItem = slot;
                if (p.itemTime == 0) p.controlUseItem = true;
            }

            if (State == SkillState.DigLeft || State == SkillState.DigRight)
            {
                int slot = FindPickaxeSlot(p);
                if (slot < 0) { Stop(); return; }
                int headTileY = (int)(p.position.Y / 16f);
                int sideTileX = State == SkillState.DigLeft
                    ? (int)((p.position.X - 1f) / 16f)
                    : (int)((p.position.X + p.width + 1f) / 16f);
                int bodyTiles = (int)System.Math.Ceiling(p.height / 16f);
                int targetY = -1;
                for (int dy = 0; dy < bodyTiles; dy++)
                {
                    if (Main.tile[sideTileX, headTileY + dy].HasTile)
                    { targetY = headTileY + dy; break; }
                }
                if (targetY < 0) return;
                SetMouse(p, sideTileX * 16f + 8f, targetY * 16f + 8f);
                p.selectedItem = slot;
                if (p.itemTime == 0) p.controlUseItem = true;
            }

            if (State == SkillState.DigUp)
            {
                int slot = FindPickaxeSlot(p);
                if (slot < 0) { Stop(); return; }
                int headTileY = (int)(p.position.Y / 16f);
                int leftTileX  = (int)(p.position.X / 16f);
                int rightTileX = (int)((p.position.X + p.width - 1f) / 16f);
                int targetRow = -1;
                for (int dy = -1; dy >= -2; dy--)
                {
                    if (Main.tile[leftTileX, headTileY + dy].HasTile || Main.tile[rightTileX, headTileY + dy].HasTile)
                    { targetRow = headTileY + dy; break; }
                }
                if (targetRow < 0) return;
                int targetX;
                if (Main.tile[leftTileX, targetRow].HasTile)
                    targetX = leftTileX;
                else
                    targetX = rightTileX;
                SetMouse(p, targetX * 16f + 8f, targetRow * 16f + 8f);
                p.selectedItem = slot;
                if (p.itemTime == 0) p.controlUseItem = true;
            }
        }

        private static void SetMouse(Player p, float worldX, float worldY)
        {
            Main.mouseX = (int)(worldX - Main.screenPosition.X);
            Main.mouseY = (int)(worldY - Main.screenPosition.Y);
            Main.SmartCursorWanted_Mouse = false;
        }
    }
}
