using System.Collections.Generic;
using Terraria;
using Terraria.ModLoader;

namespace TerraBlind
{
    public class JumpExecutor : ModSystem
    {
        private static bool _active = false;
        private static bool _done = false;
        private static int _landCx = -1, _landCy = -1;
        private static float _prevVy = 0f;
        private static readonly object _lock = new object();

        public static void Start(int hold, int sign, int moveStart, int moveFrames)
        {
            lock (_lock)
            {
                var p = Main.LocalPlayer;
                if (p == null) return;
                LoadFrames(p, hold, sign, moveStart, moveFrames);
            }
        }

        // find best (hold, moveStart, moveFrames) to land on targetCx, then execute
        public static string FindAndExecute(int targetCx, int sign)
        {
            lock (_lock)
            {
                var p = Main.LocalPlayer;
                if (p == null) return "{\"error\":\"no_player\"}";
                var ph = PhysicsSimulator.Params.FromPlayer(p);

                int bestHold = 0, bestMs = 0, bestMf = 0, bestDist = int.MaxValue, bestSimCx = -1;

                foreach (int hold in PathPlanner.HoldFrameOptions)
                {
                    // get total frames for this hold with no movement
                    var simNone = SimWithControl(p, ph, hold, sign, 0, 0);
                    if (!simNone.Landed) continue;
                    int totalFrames = simNone.Frames.Count;

                    // get cx with full movement
                    var simFull = SimWithControl(p, ph, hold, sign, 0, totalFrames);
                    if (!simFull.Landed) continue;

                    int cxNone = simNone.Cx;
                    int cxFull = simFull.Cx;

                    // target must be between no-move and full-move range
                    int lo = System.Math.Min(cxNone, cxFull);
                    int hi = System.Math.Max(cxNone, cxFull);
                    if (targetCx < lo - 1 || targetCx > hi + 1) continue;

                    // binary search move_frames
                    int mfLo = 0, mfHi = totalFrames;
                    while (mfLo <= mfHi)
                    {
                        int mfMid = (mfLo + mfHi) / 2;
                        var sim = SimWithControl(p, ph, hold, sign, 0, mfMid);
                        if (!sim.Landed) { mfLo = mfMid + 1; continue; }
                        int dist = System.Math.Abs(sim.Cx - targetCx);
                        if (dist < bestDist || (dist == bestDist && hold > bestHold))
                        {
                            bestDist = dist; bestHold = hold; bestMs = 0; bestMf = mfMid; bestSimCx = sim.Cx;
                        }
                        if (sim.Cx == targetCx) break;
                        if ((sign > 0 && sim.Cx < targetCx) || (sign < 0 && sim.Cx > targetCx))
                            mfLo = mfMid + 1;
                        else
                            mfHi = mfMid - 1;
                    }
                }

                if (bestHold == 0)
                    return $"{{\"error\":\"unreachable\",\"target_cx\":{targetCx}}}";

                LoadFrames(p, bestHold, sign, bestMs, bestMf);
                return $"{{\"ok\":true,\"hold\":{bestHold},\"move_start\":{bestMs},\"move_frames\":{bestMf},\"sim_cx\":{bestSimCx}}}";
            }
        }

        private static PhysicsSimulator.SimResult SimWithControl(Player p, PhysicsSimulator.Params ph, int hold, int sign, int moveStart, int moveFrames)
        {
            // compensate one-frame delay: velocity is read before HorizontalMovement runs
            float initVx = moveStart == 0 && moveFrames > 0 ? sign * ph.AccRun : 0f;
            var startState = new PhysicsSimulator.State
            {
                Px = p.position.X, Py = p.position.Y,
                Vx = initVx, Vy = 0f, Grounded = true, JumpFramesLeft = hold,
            };
            // simulate up to 120 frames with custom per-frame control
            var frames = new List<PhysicsSimulator.ControlInput>();
            var s = startState;
            float minPy = s.Py;
            for (int f = 0; f < 120; f++)
            {
                bool doJump = f < hold;
                bool doMove = f >= moveStart && f < moveStart + moveFrames;
                var input = new PhysicsSimulator.ControlInput
                {
                    Jump  = doJump,
                    Right = doMove && sign > 0,
                    Left  = doMove && sign < 0,
                };
                s = PhysicsSimulator.Step(s, input, ph);
                input.Px = s.Px; input.Py = s.Py;
                frames.Add(input);
                if (s.Py < minPy) minPy = s.Py;
                if (f > hold && s.Grounded)
                    return new PhysicsSimulator.SimResult
                    {
                        Landed = true, Frames = frames, EndState = s, MinPy = minPy,
                        Cx = (int)((s.Px + PhysicsSimulator.PlayerW / 2f) / 16f),
                        Cy = (int)((s.Py + PhysicsSimulator.PlayerH) / 16f) - 1,
                    };
            }
            return new PhysicsSimulator.SimResult { Landed = false, Frames = frames };
        }

        private static void LoadFrames(Player p, int hold, int sign, int moveStart, int moveFrames)
        {
            var ph = PhysicsSimulator.Params.FromPlayer(p);
            var sim = SimWithControl(p, ph, hold, sign, moveStart, moveFrames);
            int totalFrames = sim.Frames != null ? sim.Frames.Count : 60;
            var replayFrames = new List<ReplayFrame>();
            for (int f = 0; f < totalFrames; f++)
            {
                bool doJump = f < hold;
                bool doMove = f >= moveStart && f < moveStart + moveFrames;
                replayFrames.Add(new ReplayFrame
                {
                    Jump  = doJump,
                    Right = doMove && sign > 0,
                    Left  = doMove && sign < 0,
                });
            }
            ReplaySystem.Load(replayFrames);
            _active = true;
            _done = false;
            _landCx = -1; _landCy = -1;
            _prevVy = 0f;
        }

        public static string GetResult()
        {
            lock (_lock)
            {
                if (!_done) return "{\"done\":false}";
                return $"{{\"done\":true,\"cx\":{_landCx},\"cy\":{_landCy}}}";
            }
        }

        public override void PostUpdateEverything()
        {
            lock (_lock)
            {
                if (!_active) return;
                var p = Main.LocalPlayer;
                if (p == null) return;
                float vy = p.velocity.Y;
                bool grounded = vy == 0f && _prevVy > 0f;
                if (grounded)
                {
                    _landCx = (int)((p.position.X + p.width / 2f) / 16f);
                    _landCy = (int)((p.position.Y + p.height) / 16f) - 1;
                    _active = false;
                    _done = true;
                }
                _prevVy = vy;
            }
        }
    }
}
