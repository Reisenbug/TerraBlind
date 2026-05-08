using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;

namespace TerraBlind
{
    public static class PhysicsSimulator
    {
        private const float AccRun = 0.08f;
        public const float MaxRunSpeed = 3.0f;
        private const float RunSlowdown = 0.2f;
        private const float Gravity = 0.4f;
        private const float MaxFall = 10f;
        private const float HoldVY = -4.61f; // -(jumpSpeed - gravity) = -(5.01 - 0.4), verified by PhysicsRecorder
        public const int PlayerW = 20;
        public const int PlayerH = 42;

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
        }

        public static State Step(State s, ControlInput input)
        {
            float vx = s.Vx;
            float vy = s.Vy;
            int jfl = s.JumpFramesLeft;

            if (input.Right)       vx = System.Math.Min(vx + AccRun, MaxRunSpeed);
            else if (input.Left)   vx = System.Math.Max(vx - AccRun, -MaxRunSpeed);
            else if (s.Grounded)   vx = vx > 0 ? System.Math.Max(vx - RunSlowdown, 0) : System.Math.Min(vx + RunSlowdown, 0);

            if (input.Jump && jfl > 0)
            {
                vy = HoldVY;
                jfl--;
            }
            else
            {
                if (!input.Jump) jfl = 0;
                vy = System.Math.Min(vy + Gravity, MaxFall);
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

        // simulate jump from grounded state, hold jump for holdFrames, move in dirSign direction
        public static SimResult SimulateJump(State start, int dirSign, int holdFrames, int maxFrames = 120)
        {
            var frames = new List<ControlInput>();
            var s = start;
            s.JumpFramesLeft = holdFrames;

            for (int f = 0; f < maxFrames; f++)
            {
                var input = new ControlInput
                {
                    Right = dirSign > 0,
                    Left = dirSign < 0,
                    Jump = f < holdFrames,
                };
                bool wasGrounded = s.Grounded;
                s = Step(s, input);
                frames.Add(input);

                if (f > holdFrames && s.Grounded)
                {
                    return new SimResult
                    {
                        EndState = s,
                        Frames = frames,
                        Landed = true,
                        Cx = (int)((s.Px + PlayerW / 2f) / 16),
                        Cy = (int)((s.Py + PlayerH) / 16),
                    };
                }
            }
            return new SimResult { EndState = s, Frames = frames, Failed = true };
        }

        public static SimResult SimulateFall(State start, int dirSign, int maxFrames = 120)
        {
            var frames = new List<ControlInput>();
            var s = start;

            for (int f = 0; f < maxFrames; f++)
            {
                var input = new ControlInput { Right = dirSign > 0, Left = dirSign < 0 };
                s = Step(s, input);
                frames.Add(input);

                if (f > 0 && s.Grounded)
                {
                    return new SimResult
                    {
                        EndState = s,
                        Frames = frames,
                        Landed = true,
                        Cx = (int)((s.Px + PlayerW / 2f) / 16),
                        Cy = (int)((s.Py + PlayerH) / 16),
                    };
                }
            }
            return new SimResult { EndState = s, Frames = frames, Failed = true };
        }
    }
}
