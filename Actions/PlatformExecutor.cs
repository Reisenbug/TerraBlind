using System.Collections.Generic;
using Terraria;
using Terraria.ModLoader;

namespace TerraBlind
{
    public static class PlatformExecutor
    {
        public static List<ReplayFrame> BuildPlatUpFrames(Player p, int slot)
        {
            var ph = PhysicsSimulator.Params.FromPlayer(p);
            var startState = new PhysicsSimulator.State
            {
                Px = p.position.X, Py = p.position.Y,
                Vx = 0f, Vy = 0f, Grounded = true, JumpFramesLeft = 15,
            };
            var sim = PhysicsSimulator.SimulateJump(startState, 0, 15, ph);
            int minIdx = 0;
            float minPy = float.MaxValue;
            for (int i = 0; i < sim.Frames.Count; i++)
                if (sim.Frames[i].Py < minPy) { minPy = sim.Frames[i].Py; minIdx = i; }
            int placeFrame = minIdx;
            int feetTileY = (int)((minPy + PhysicsSimulator.PlayerH) / 16f);
            int feetCx = (int)((sim.Frames[minIdx].Px + PhysicsSimulator.PlayerW / 2f) / 16f);
            int placeTx = feetCx, placeTy = feetTileY + 1;
            var replay = new List<ReplayFrame>();
            for (int f = 0; f < sim.Frames.Count; f++)
            {
                var rf = new ReplayFrame { Jump = f < 15 };
                if (f == placeFrame)
                {
                    rf.UseItem = true;
                    rf.SelectedSlot = slot;
                    rf.Mx = (placeTx * 16f + 8f - (sim.Frames[f].Px + PhysicsSimulator.PlayerW / 2f)) / 16f;
                    rf.My = (placeTy * 16f + 8f - (sim.Frames[f].Py + PhysicsSimulator.PlayerH / 2f)) / 16f;
                    rf.SmartCursor = 0;
                }
                replay.Add(rf);
            }
            return replay;
        }

        private static (List<PhysicsSimulator.ControlInput> frames, int landIdx, int landCx) SimAirJump(
            Player p, PhysicsSimulator.Params ph, int sign, int moveEnd, int startFeetY)
        {
            var s = new PhysicsSimulator.State
            {
                Px = p.position.X, Py = p.position.Y,
                Vx = 0f, Vy = 0f, Grounded = true, JumpFramesLeft = 15,
            };
            var frames = new List<PhysicsSimulator.ControlInput>();
            int landIdx = -1;
            for (int f = 0; f < 120; f++)
            {
                var input = new PhysicsSimulator.ControlInput
                {
                    Jump  = f < 15,
                    Right = sign > 0 && f < moveEnd,
                    Left  = sign < 0 && f < moveEnd,
                };
                s = PhysicsSimulator.Step(s, input, ph);
                input.Px = s.Px; input.Py = s.Py;
                frames.Add(input);
                if (f > 15)
                {
                    int feetY = (int)((s.Py + PhysicsSimulator.PlayerH) / 16f);
                    if (feetY >= startFeetY) { landIdx = f; break; }
                }
            }
            if (landIdx < 0) landIdx = frames.Count - 1;
            int landCx = (int)((frames[landIdx].Px + PhysicsSimulator.PlayerW / 2f) / 16f);
            return (frames, landIdx, landCx);
        }

        public static List<ReplayFrame> BuildPlatJumpFrames(Player p, int slot, int sign, out int placeTx, out int placeTy, out int landFrame)
        {
            var ph = PhysicsSimulator.Params.FromPlayer(p);
            int startFeetY = (int)((p.position.Y + PhysicsSimulator.PlayerH) / 16f);

            var full = SimAirJump(p, ph, sign, 120, startFeetY);
            int targetCx = full.landCx;

            int bestMoveEnd = full.landIdx;
            var bestSim = full;
            int lo = 1, hi = full.landIdx;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                var sim = SimAirJump(p, ph, sign, mid, startFeetY);
                if (sim.landCx == targetCx)
                {
                    bestMoveEnd = mid;
                    bestSim = sim;
                    hi = mid - 1;
                }
                else if ((sign > 0 && sim.landCx < targetCx) || (sign < 0 && sim.landCx > targetCx))
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            int landIdx = bestSim.landIdx;
            placeTy = startFeetY - 1;
            landFrame = landIdx;

            // fixed cursor offset: mx = sign*0.5, my = 3.0
            // cursor tile y = floor((Py + PlayerH/2 + my*16) / 16) = floor((Py + 69) / 16)
            // need cursor tile y == placeTy = startFeetY - 1
            // → Py ∈ [(startFeetY-1)*16 - 69 + 16, ...) actually [(placeTy)*16 - 69, (placeTy+1)*16 - 69)
            // i.e. Py ∈ [(startFeetY-1)*16 - 69, startFeetY*16 - 69)
            float pyLo = (startFeetY - 1) * 16f - 69f;
            float pyHi = startFeetY * 16f - 69f;
            int placeFrame = -1;
            for (int f = bestSim.frames.Count - 1; f >= 0; f--)
            {
                float py = bestSim.frames[f].Py;
                if (py >= pyLo && py < pyHi) { placeFrame = f; break; }
            }
            if (placeFrame < 0) placeFrame = System.Math.Max(0, landIdx - 2);
            float fixedMx = sign * 0.5f;
            float fixedMy = 3.0f;
            placeTx = (int)((bestSim.frames[placeFrame].Px + PhysicsSimulator.PlayerW / 2f + fixedMx * 16f) / 16f);

            var replay = new List<ReplayFrame>();
            for (int f = 0; f < bestSim.frames.Count; f++)
            {
                var rf = new ReplayFrame
                {
                    Jump  = f < 15,
                    Right = sign > 0 && f < bestMoveEnd,
                    Left  = sign < 0 && f < bestMoveEnd,
                };
                if (f == placeFrame)
                {
                    rf.UseItem = true;
                    rf.SelectedSlot = slot;
                    rf.Mx = fixedMx;
                    rf.My = fixedMy;
                    rf.SmartCursor = 0;
                }
                replay.Add(rf);
            }
            return replay;
        }
    }

    public class PlatJumpExecutor : ModSystem
    {
        private static readonly object _lock = new object();
        private static int _remaining = 0;
        private static int _sign = 1;
        private static int _slot = -1;
        private static float _prevVy = 0f;
        private static bool _waitingGround = false;

        public static string StartN(int n, int sign)
        {
            lock (_lock)
            {
                var p = Main.LocalPlayer;
                if (p == null) return "{\"error\":\"no_player\"}";
                int slot = NavCoordinator.FindPlatformSlot(p);
                if (slot < 0) return "{\"error\":\"no_platform_item\"}";
                _sign = sign >= 0 ? 1 : -1;
                _slot = slot;
                _remaining = n;
                _prevVy = 0f;
                var frames = PlatformExecutor.BuildPlatJumpFrames(p, slot, _sign, out _, out _, out _);
                ReplaySystem.Load(frames);
                _remaining--;
                _waitingGround = true;
                return $"{{\"ok\":true,\"remaining\":{_remaining}}}";
            }
        }

        public static void Stop()
        {
            lock (_lock) { _remaining = 0; _waitingGround = false; }
        }

        public override void PostUpdateEverything()
        {
            lock (_lock)
            {
                if (!_waitingGround) return;
                var p = Main.LocalPlayer;
                if (p == null) return;
                if (ReplaySystem.IsActive) return;
                if (p.velocity.Y != 0f) return;
                if (_remaining <= 0) { _waitingGround = false; return; }
                var frames = PlatformExecutor.BuildPlatJumpFrames(p, _slot, _sign, out _, out _, out _);
                ReplaySystem.Load(frames);
                _remaining--;
            }
        }
    }
}
