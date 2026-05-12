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
            public float AccRun, MaxRun, RunSlowdown, Gravity, MaxFall, HoldVY;

            public static Params FromPlayer(Player p)
            {
                float js = Player.jumpSpeed;
                float grav = p.gravity > 0f ? p.gravity : 0.4f;
                return new Params
                {
                    AccRun      = 0.08f,
                    MaxRun      = p.wet ? 1.5f : (p.maxRunSpeed > 0f ? p.maxRunSpeed : MaxRunSpeed),
                    RunSlowdown = 0.2f,
                    Gravity     = grav,
                    MaxFall     = 10f,
                    HoldVY      = -(js - grav),
                };
            }

            public static readonly Params Default = new Params
            {
                AccRun = 0.08f, MaxRun = MaxRunSpeed, RunSlowdown = 0.2f,
                Gravity = 0.4f, MaxFall = 10f, HoldVY = -4.61f,
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

        public static State Step(State s, ControlInput input, Params ph)
        {
            float vx = s.Vx;
            float vy = s.Vy;
            int jfl = s.JumpFramesLeft;

            if (input.Right)       vx = System.Math.Min(vx + ph.AccRun, ph.MaxRun);
            else if (input.Left)   vx = System.Math.Max(vx - ph.AccRun, -ph.MaxRun);
            else if (s.Grounded)   vx = vx > 0 ? System.Math.Max(vx - ph.RunSlowdown, 0) : System.Math.Min(vx + ph.RunSlowdown, 0);

            if (input.Jump && jfl > 0)
            {
                vy = ph.HoldVY;
                jfl--;
            }
            else
            {
                if (!input.Jump) jfl = 0;
                vy = System.Math.Min(vy + ph.Gravity, ph.MaxFall);
            }

            var pos = new Vector2(s.Px, s.Py);
            var vel = new Vector2(vx, vy);
            var result = Terraria.Collision.TileCollision(pos, vel, PlayerW, PlayerH, false, false, 1);
            float nx = s.Px + result.X;
            float ny = s.Py + result.Y;

            bool hitFloor = vy > 0f && System.Math.Abs(result.Y - vy) > 0.01f;
            if (hitFloor) vy = 0f;

            return new State
            {
                Px = nx, Py = ny,
                Vx = vx, Vy = vy,
                Grounded = hitFloor,
                JumpFramesLeft = jfl,
            };
        }

        public static State Step(State s, ControlInput input, Params ph, out bool vxClipped, out bool vyClipped)
        {
            float vx = s.Vx;
            float vy = s.Vy;
            int jfl = s.JumpFramesLeft;

            if (input.Right)       vx = System.Math.Min(vx + ph.AccRun, ph.MaxRun);
            else if (input.Left)   vx = System.Math.Max(vx - ph.AccRun, -ph.MaxRun);
            else if (s.Grounded)   vx = vx > 0 ? System.Math.Max(vx - ph.RunSlowdown, 0) : System.Math.Min(vx + ph.RunSlowdown, 0);

            if (input.Jump && jfl > 0) { vy = ph.HoldVY; jfl--; }
            else { if (!input.Jump) jfl = 0; vy = System.Math.Min(vy + ph.Gravity, ph.MaxFall); }

            var pos = new Vector2(s.Px, s.Py);
            var vel = new Vector2(vx, vy);
            var result = Terraria.Collision.TileCollision(pos, vel, PlayerW, PlayerH, false, false, 1);
            float nx = s.Px + result.X;
            float ny = s.Py + result.Y;

            vxClipped = System.Math.Abs(result.X - vx) > 0.01f;
            vyClipped = System.Math.Abs(result.Y - vy) > 0.01f;

            bool hitFloor = vy > 0f && vyClipped;
            if (hitFloor) vy = 0f;

            return new State { Px = nx, Py = ny, Vx = vx, Vy = vy, Grounded = hitFloor, JumpFramesLeft = jfl };
        }

        // overload for callers that don't need water-aware params
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
                        Cy       = (int)((s.Py + PlayerH / 2f) / 16),
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
                        Cy       = (int)((s.Py + PlayerH / 2f) / 16),
                    };
                }
            }
            return new SimResult { EndState = s, Frames = frames, Failed = true };
        }

        public static SimResult SimulateFall(State start, int dirSign, int maxFrames = 120)
            => SimulateFall(start, dirSign, Params.Default, maxFrames);
    }
}
