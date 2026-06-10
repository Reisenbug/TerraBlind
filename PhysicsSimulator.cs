using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;

namespace TerraBlind
{
    public static class PhysicsSimulator
    {
        public const float MaxRunSpeed = 3.0f;
        public const int PlayerW = 20;
        public const int PlayerH = 42;

        public struct Params
        {
            public float AccRun, MaxRun, AccRunSpeed, RunSlowdown, Gravity, MaxFall, JumpSpeed;

            public float HoldVY => -JumpSpeed;

            public static Params FromPlayer(Player p)
            {
                float grav, js, maxFall, maxRun, accRunSpeed;
                float accRun = p.runAcceleration > 0f ? p.runAcceleration : 0.08f;
                float slow   = p.runSlowdown   > 0f ? p.runSlowdown   : 0.2f;
                if (p.wet && !p.honeyWet && !p.merman)
                {
                    grav       = 0.2f * 0.5f;
                    js         = 6.01f * 0.5f;
                    maxFall    = 5f * 0.5f;
                    maxRun     = 1.5f * p.moveSpeed;
                    accRunSpeed = maxRun;
                    accRun     = 0.08f * 0.5f;
                    slow       = 0.2f * 0.5f;
                }
                else
                {
                    grav       = p.gravity > 0f ? p.gravity : 0.4f;
                    js         = Player.jumpSpeed;
                    maxFall    = p.maxFallSpeed > 0f ? p.maxFallSpeed : 10f;
                    maxRun     = p.moveSpeed > 0f ? 3f * p.moveSpeed : 3f;
                    float accRunRaw = p.accRunSpeed > 0f ? p.accRunSpeed : 3f;
                    accRunSpeed = accRunRaw > maxRun ? accRunRaw : maxRun;
                }
                return new Params
                {
                    AccRun      = accRun,
                    MaxRun      = maxRun,
                    AccRunSpeed = accRunSpeed,
                    RunSlowdown = slow,
                    Gravity     = grav,
                    MaxFall     = maxFall,
                    JumpSpeed   = js,
                };
            }

            public static readonly Params Default = new Params
            {
                AccRun = 0.08f, MaxRun = MaxRunSpeed, AccRunSpeed = MaxRunSpeed,
                RunSlowdown = 0.2f, Gravity = 0.4f, MaxFall = 10f, JumpSpeed = 5.01f,
            };
        }

        public struct State
        {
            public float Px, Py, Vx, Vy;
            public bool Grounded;
            public int JumpFramesLeft;
        }

        public struct ControlInput
        {
            public bool Left, Right, Jump;
            public float Px, Py; // player position (top-left px) after this frame
            public float Vx, Vy; // player velocity after this frame (for plan-vs-exec divergence diagnosis)
            public bool Place;   // place a platform this frame (execution-only; ignored by Step)
            public int PlaceCx, PlaceCy;
        }

        public struct SimResult
        {
            public State EndState;
            public List<ControlInput> Frames;
            public bool Landed;
            public bool Failed;
            public int Cx, Cy;
            public float MinPy;
            public int WallContactFrames;    // frames where vx was clipped during ascent (vy<0)
            public int CeilingContactFrames; // frames where vy was clipped during ascent (vy<0)
        }

        public static State Step(State s, ControlInput input, Params ph, out bool vxClipped, out bool vyClipped)
        {
            float vx = s.Vx;
            float vy = s.Vy;
            int jfl = s.JumpFramesLeft;

            // ASSUMPTION: Terraria's accel/friction is ONE else-if chain, NOT clamped. holding a key at vx>=maxRun
            // falls through to friction → cruise sawtooths around maxRun (mean==maxRun). a flat clamp drifts vs exec.
            if (input.Right && vx < ph.MaxRun)
            {
                if (vx < -ph.RunSlowdown) vx += ph.RunSlowdown;
                vx += ph.AccRun;
            }
            else if (input.Right && vx < ph.AccRunSpeed)
            {
                if (vx < -ph.RunSlowdown) vx += ph.RunSlowdown;
                vx += ph.AccRun * 0.2f;
            }
            else if (input.Left && vx > -ph.MaxRun)
            {
                if (vx > ph.RunSlowdown) vx -= ph.RunSlowdown;
                vx -= ph.AccRun;
            }
            else if (input.Left && vx > -ph.AccRunSpeed)
            {
                if (vx > ph.RunSlowdown) vx -= ph.RunSlowdown;
                vx -= ph.AccRun * 0.2f;
            }
            else if (s.Grounded)
            {
                if (vx > ph.RunSlowdown)       vx -= ph.RunSlowdown;
                else if (vx < -ph.RunSlowdown)  vx += ph.RunSlowdown;
                else                            vx  = 0f;
            }
            else
            {
                float airSlow = ph.RunSlowdown * 0.5f;
                if (vx > airSlow)        vx -= airSlow;
                else if (vx < -airSlow)  vx += airSlow;
                else                     vx  = 0f;
            }

            if (input.Jump && jfl > 0)
            {
                // Terraria's hold phase rises at jumpSpeed - gravity (a constant 4.61 for bare player),
                // not the raw jumpSpeed; the gravity term is already folded in, not applied per-frame.
                vy = -(ph.JumpSpeed - ph.Gravity);
                jfl--;
            }
            else
            {
                if (!input.Jump) jfl = 0;
                vy = System.Math.Min(vy + ph.Gravity, ph.MaxFall);
            }

            var pos = new Vector2(s.Px, s.Py);
            var vel = new Vector2(vx, vy);
            float stepSpeed = 0f, gfxOffY = 0f;
            // StepUp only near ground: if not grounded and falling but no floor within 2px, skip
            bool nearGround = s.Grounded || (vy > 0f && Terraria.Collision.TileCollision(pos, new Vector2(0f, 2f), PlayerW, PlayerH, false, false, 1).Y < 2f);
            if (vx != 0f && nearGround)
                Terraria.Collision.StepUp(ref pos, ref vel, PlayerW, PlayerH, ref stepSpeed, ref gfxOffY);
            var result = Terraria.Collision.TileCollision(pos, vel, PlayerW, PlayerH, false, false, 1);

            if (vy < 0f && System.Math.Abs(result.Y - vel.Y) > 0.01f) jfl = 0;

            float nx = pos.X + result.X;
            float ny = pos.Y + result.Y;

            vxClipped = System.Math.Abs(result.X - vel.X) > 0.01f;
            vyClipped = System.Math.Abs(result.Y - vel.Y) > 0.01f;

            // game keeps the clipped fall residual on the landing frame (vy≠0); zeroing here lands one frame
            // early → +0.1px/seam drift. next frame's gravity re-clips it to ~0.
            bool hitFloor = vel.Y > 0f && vyClipped;
            vy = result.Y;

            vx = result.X;

            return new State { Px = nx, Py = ny, Vx = vx, Vy = vy, Grounded = hitFloor, JumpFramesLeft = jfl };
        }

        public static State Step(State s, ControlInput input, Params ph)
        {
            return Step(s, input, ph, out _, out _);
        }

        public static State Step(State s, ControlInput input) => Step(s, input, Params.Default);

        public static SimResult SimulateJump(State start, int dirSign, int holdFrames, Params ph, int maxFrames = 120)
        {
            var frames = new List<ControlInput>();
            var s = start;
            s.JumpFramesLeft = holdFrames;
            float minPy = start.Py;
            int wallContactFrames = 0, ceilingContactFrames = 0;

            for (int f = 0; f < maxFrames; f++)
            {
                var input = new ControlInput
                {
                    Right = dirSign > 0,
                    Left  = dirSign < 0,
                    Jump  = f < holdFrames,
                };
                float preVy = s.Vy;
                s = Step(s, input, ph, out bool vxClipped, out bool vyClipped);
                input.Px = s.Px; input.Py = s.Py;
                if (s.Py < minPy) minPy = s.Py;
                if (preVy < 0f)
                {
                    if (vxClipped) wallContactFrames++;
                    if (vyClipped) ceilingContactFrames++;
                }
                frames.Add(input);

                if (f > holdFrames && s.Grounded)
                {
                    return new SimResult
                    {
                        EndState = s,
                        Frames   = frames,
                        Landed   = true,
                        Cx       = (int)((s.Px + PlayerW / 2f) / 16),
                        Cy       = (int)((s.Py + PlayerH) / 16f) - 1,
                        MinPy    = minPy,
                        WallContactFrames    = wallContactFrames,
                        CeilingContactFrames = ceilingContactFrames,
                    };
                }
            }
            return new SimResult { EndState = s, Frames = frames, Failed = true, MinPy = minPy, WallContactFrames = wallContactFrames, CeilingContactFrames = ceilingContactFrames };
        }

        public static SimResult SimulateJump(State start, int dirSign, int holdFrames, int maxFrames = 120)
            => SimulateJump(start, dirSign, holdFrames, Params.Default, maxFrames);

        public static SimResult SimulateFall(State start, int dirSign, Params ph, int maxFrames = 120)
        {
            var frames = new List<ControlInput>();
            var s = start;

            for (int f = 0; f < maxFrames; f++)
            {
                var input = new ControlInput { Right = dirSign > 0, Left = dirSign < 0 };
                s = Step(s, input, ph);
                frames.Add(input);

                if (f > 0 && s.Grounded)
                {
                    return new SimResult
                    {
                        EndState = s,
                        Frames   = frames,
                        Landed   = true,
                        Cx       = (int)((s.Px + PlayerW / 2f) / 16),
                        Cy       = (int)((s.Py + PlayerH) / 16f) - 1,
                    };
                }
            }
            return new SimResult { EndState = s, Frames = frames, Failed = true };
        }

        public static SimResult SimulateFall(State start, int dirSign, int maxFrames = 120)
            => SimulateFall(start, dirSign, Params.Default, maxFrames);
    }
}
