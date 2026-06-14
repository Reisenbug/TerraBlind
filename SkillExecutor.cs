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

        private static float _pillarWaitPrevVY;

        private const int WaitFrames = 10;
        private const int JumpHoldFrames = 15;
        private const int LaunchFrames = 20;

        // pillar_jump_2_height.json fully expanded (43 frames)
        private static readonly (bool jump, bool use, float mx, float my)[] PillarCycleFrames = {
            (true,  false, -0.1f, 1.7f),
            (true,  false, -0.1f, 1.7f),
            (true,  true,  -0.1f, 1.6f),
            (true,  true,  -0.1f, 1.7f),
            (true,  true,  -0.1f, 1.7f),
            (true,  true,  -0.1f, 1.9f),
            (true,  true,  -0.1f, 1.7f),
            (false, true,  -0.1f, 1.6f),
            (false, true,  -0.1f, 1.7f),
            (false, true,  -0.1f, 1.6f),
            (false, true,  -0.1f, 1.7f),
            (false, true,  -0.1f, 1.7f),
            (false, true,  -0.1f, 1.6f),
            (false, true,  -0.1f, 1.7f),
            (false, true,  -0.1f, 1.6f),
            (false, true,  -0.1f, 1.7f),
            (false, true,  -0.1f, 1.7f),
            (false, true,  -0.1f, 1.6f),
            (false, true,  -0.1f, 1.7f),
            (false, true,  -0.1f, 1.6f),
            (false, true,  -0.1f, 1.7f),
            (false, true,  -0.1f, 1.7f),
            (false, true,  -0.1f, 1.7f),
            (false, true,  -0.1f, 1.7f),
            (false, true,  -0.1f, 1.7f),
            (false, true,  -0.1f, 1.7f),
            (false, true,  -0.1f, 1.7f),
            (false, true,  -0.1f, 1.7f),
            (false, true,  -0.1f, 1.7f),
            (false, true,  -0.1f, 1.7f),
            (false, true,  -0.1f, 1.7f),
            (false, true,  -0.1f, 1.7f),
            (false, true,  -0.1f, 1.7f),
            (false, true,  -0.1f, 1.7f),
            (false, true,  -0.1f, 1.7f),
            (false, true,  -0.1f, 1.7f),
            (false, true,  -0.1f, 1.7f),
            (false, true,  -0.1f, 1.7f),
            (false, true,  -0.1f, 1.7f),
            (false, true,  -0.1f, 1.7f),
            (false, true,  -0.1f, 1.7f),
            (false, true,  -0.1f, 1.7f),
            (false, true,  -0.1f, 1.7f),
        };

        public static bool IsActive => State != SkillState.Idle;

        public static void StartPillarJump(bool dirRight, int targetWy)
        {
            DirectionRight = dirRight;
            _targetWy = targetWy;
            _jumpFramesLeft = 0;
            _cycleTick = 0;
            _cyclesDone = 0;
            _phaseTick = 0;
            _placeStarted = false;
            _stalledCycles = 0;
            _cycleStartFeetY = 0;
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

        private static int Pcx(Player p) => (int)((p.position.X + p.width / 2f) / 16f);

        // SHARED legality test for the pillar macro: planner (Expand) and executor both call this so the graph edge
        // exists exactly when the executor can actually pillar (no fake edges). Mirrors ApplyControls' PillarBuild
        // checks: must have a platform item, the head's two columns (20px body) must be clear of solid non-platform
        // tiles going up, and a hold=15 vertical jump must rise (not be boxed in). Returns how high the feet can
        // reach (topFeetY = the highest feet row before the head hits a ceiling), and whether pillaring is possible.
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

            // scan upward: head occupies the row above the feet. stop where either column hits a solid (non-platform)
            // tile — that's the ceiling the executor's leftHeadBlocked/rightHeadBlocked check would stop at.
            // body of height 42px occupies 3 rows: feetY, feetY-1, feetY-2. climbing 2 tiles to feetY-2 makes the
            // body occupy feetY-2, feetY-3, feetY-4 — so the two NEW rows (feetY-3, feetY-4) must both be clear in
            // both columns, else the head ends up inside a wall (the clip bug). only advance when they're clear.
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
            DiagLog.Write($"[pillar-can] from feet=({feetCx},{feetCy}) cols[{leftCol}..{rightCol}] reachedFeetY={reached} (box would occupy {reached-2}..{reached})");
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

        private static int FindPlatformSlot(Player p)
        {
            for (int i = 0; i < 10; i++)
            {
                var item = p.inventory[i];
                if (item != null && !item.IsAir && item.createTile >= 0)
                {
                    var td = Terraria.ID.TileID.Sets.Platforms;
                    if (td != null && item.createTile < td.Length && td[item.createTile])
                        return i;
                }
            }
            return -1;
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
                // simulate hold=15 vx=0 jump; if end_py > start_py the path is blocked
                var nudgeState = new PhysicsSimulator.State { Px = p.position.X, Py = p.position.Y, Vx = 0f, Vy = 0f, Grounded = true, JumpFramesLeft = 15 };
                var nudgePh = PhysicsSimulator.Params.FromPlayer(p);
                var nudgeSim = PhysicsSimulator.SimulateJump(nudgeState, 0, 15, nudgePh);
                bool jumpBlocked = p.position.Y - nudgeSim.MinPy < 10f; // normal unobstructed rise = 93.46px; <10 = blocked
                if (jumpBlocked)
                {
                    float shift = 4f;
                    var simL = PhysicsSimulator.SimulateJump(new PhysicsSimulator.State { Px = p.position.X - shift, Py = p.position.Y, Vx = 0f, Vy = 0f, Grounded = true, JumpFramesLeft = 15 }, 0, 15, nudgePh);
                    var simR = PhysicsSimulator.SimulateJump(new PhysicsSimulator.State { Px = p.position.X + shift, Py = p.position.Y, Vx = 0f, Vy = 0f, Grounded = true, JumpFramesLeft = 15 }, 0, 15, nudgePh);
                    bool clearLeft = p.position.Y - simL.MinPy >= 10f;
                    bool clearRight = p.position.Y - simR.MinPy >= 10f;
                    DiagLog.Write($"[pillar_nudge] blocked px={p.position.X:0.##} clearLeft={clearLeft} clearRight={clearRight}");
                    if (clearLeft && !clearRight) p.controlLeft = true;
                    else if (clearRight && !clearLeft) p.controlRight = true;
                }
                int leftCol = (int)(p.position.X / 16f);
                int rightCol = (int)((p.position.X + p.width - 1) / 16f);
                int headTileY = (int)(p.position.Y / 16f);
                bool leftHeadBlocked = Main.tile[leftCol, headTileY - 1] is { HasTile: true } lh && Main.tileSolid[lh.TileType] && !Main.tileSolidTop[lh.TileType];
                bool rightHeadBlocked = Main.tile[rightCol, headTileY - 1] is { HasTile: true } rh && Main.tileSolid[rh.TileType] && !Main.tileSolidTop[rh.TileType];
                if (leftHeadBlocked || rightHeadBlocked)
                {
                    DiagLog.Write($"[pillar] head blocked at leftCol={leftCol} rightCol={rightCol} headY={headTileY - 1}, stopping");
                    Stop();
                    return;
                }
                if (!ReplaySystem.IsActive)
                {
                    if (_cyclesDone > 0 && feetYNow <= _targetWy)
                    {
                        float vyAtEntry = p.velocity.Y;
                        DiagLog.Write($"[pillar] reached target feetY={feetYNow} targetWy={_targetWy} cycles={_cyclesDone}");
                        DiagLog.Write($"[pillar_wait_enter] tick={Main.GameUpdateCount} cyclesDone={_cyclesDone} feetY={feetYNow} targetWy={_targetWy} vy={vyAtEntry} prevVY={_pillarWaitPrevVY}");
                        _phaseTick = 0;
                        _pillarWaitPrevVY = p.velocity.Y;
                        State = SkillState.PillarWait;
                        return;
                    }
                    if (_cyclesDone > 0)
                    {
                        if (feetYNow >= _cycleStartFeetY)
                        {
                            _stalledCycles++;
                            DiagLog.Write($"[pillar] stall cycle={_cyclesDone} feetY={feetYNow} prev={_cycleStartFeetY} stalls={_stalledCycles}");
                            if (_stalledCycles >= 2) { Stop(); return; }
                        }
                        else
                        {
                            _stalledCycles = 0;
                        }
                    }
                    _cycleStartFeetY = feetYNow;
                    _cyclesDone++;
                    DiagLog.Write($"[pillar] cycle={_cyclesDone} feetY={feetYNow} targetWy={_targetWy}");
                    var frames = new System.Collections.Generic.List<ReplayFrame>();
                    foreach (var (jump, use, mx, my) in PillarCycleFrames)
                        frames.Add(new ReplayFrame { Jump = jump, UseItem = use, SelectedSlot = platformSlot, SmartCursor = 0, Mx = mx, My = my });
                    ReplaySystem.Load(frames);
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
