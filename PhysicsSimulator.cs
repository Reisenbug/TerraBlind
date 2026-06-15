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

        // dry/wet variants for the current plan, filled by Params.FromPlayer; Step switches per-frame at the
        // water surface. single-threaded planner, so static is safe.
        static Params _dry, _wet;
        static bool _wetReady;

        public struct Params
        {
            public float AccRun, MaxRun, AccRunSpeed, RunSlowdown, Gravity, MaxFall, JumpSpeed;
            public int JumpHeight; // hold-frame cap: water 30, air 15 (verified from jump-trace)

            public float HoldVY => -JumpSpeed;

            // FromPlayer returns the variant for the player's CURRENT wetness, but also stashes BOTH the dry and
            // wet variants in PhysicsSimulator so Step can switch per-frame as a simulated jump crosses the water
            // surface (params differ: water halves gravity/jumpSpeed/maxFall/run). Without per-frame switching a
            // jump that leaves or enters water keeps the wrong params and the planned arc diverges at the surface.
            public static Params FromPlayer(Player p)
            {
                _dry = BuildDry(p);
                _wet = BuildWet(p);
                _wetReady = true;
                bool wetNow = p.wet && !p.honeyWet && !p.merman;
                return wetNow ? _wet : _dry;
            }

            static Params BuildWet(Player p) => new Params
            {
                Gravity = 0.2f * 0.5f, JumpSpeed = 6.01f * 0.5f, MaxFall = 5f * 0.5f,
                MaxRun = 1.5f * p.moveSpeed, AccRunSpeed = 1.5f * p.moveSpeed,
                AccRun = 0.08f * 0.5f, RunSlowdown = 0.2f * 0.5f, JumpHeight = 30,
            };

            static Params BuildDry(Player p)
            {
                // FRAGILE: do NOT read p.gravity / jumpSpeed / jumpHeight / runAccel / accRunSpeed here — when
                // FromPlayer is called while the player is IN water those globals hold the WATER values
                // (jumpHeight=30, accRun halved, ...), poisoning the "air" variant. the air variant must be the
                // medium-independent bare-player values. moveSpeed is a gear stat (not water-modified) so it's safe.
                float maxRun = p.moveSpeed > 0f ? 3f * p.moveSpeed : 3f;
                return new Params
                {
                    AccRun = 0.08f,
                    MaxRun = maxRun,
                    AccRunSpeed = maxRun,
                    RunSlowdown = 0.2f,
                    Gravity = 0.4f,
                    MaxFall = 10f,
                    JumpSpeed = 5.01f,
                    JumpHeight = 15,
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
            public int JumpHStart; // hold-frame cap of the medium the jump STARTED in (water=30, air=15)
            public bool WasWet;    // medium last frame, to detect the water-surface crossing
        }

        public struct ControlInput
        {
            public bool Left, Right, Jump, Down;
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

            // per-frame water-surface switch: pick the wet/dry variant by where the player IS this frame, so a jump
            // crossing the surface uses the right gravity/jumpSpeed/hold-cap on each side. uses vanilla WetCollision.
            bool wetNow = s.WasWet;
            if (_wetReady)
            {
                wetNow = Terraria.Collision.WetCollision(new Vector2(s.Px, s.Py), PlayerW, PlayerH);
                ph = wetNow ? _wet : _dry;
                // crossing OUT of water mid-hold: the hold cap drops from water's 30 to air's 15, and the game
                // re-clamps the remaining hold frames. verified from jump-trace: jfl 22(water) → 6(air) on exit.
                // used = how many hold frames already spent; remaining air hold = airCap - used.
                if (s.WasWet && !wetNow && jfl > 0 && s.JumpHStart > 0)
                {
                    int used = s.JumpHStart - jfl;
                    int airRemain = ph.JumpHeight - used;
                    if (airRemain < jfl) jfl = airRemain < 0 ? 0 : airRemain;
                }
            }

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

            int jumpHStart = s.JumpHStart;
            if (input.Jump && jfl > 0)
            {
                // clamp the requested hold to the LAUNCH medium's cap: BuildHoldOptions offers holds up to
                // Player.jumpHeight (==30 when planning while submerged), but an air launch can only hold 15
                // frames. without this an air jump runs ~24 hold frames and peaks ~10 tiles (real air max ~6.3).
                if (jumpHStart == 0) { jumpHStart = ph.JumpHeight; if (jfl > ph.JumpHeight) jfl = ph.JumpHeight; }
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
            // holding Down drops through platforms (solidTop): pass fallThrough=true to TileCollision and skip
            // StepUp (it would lift the player back onto the platform). matches vanilla controlDown behavior.
            bool ft = input.Down;
            // StepUp only near ground: if not grounded and falling but no floor within 2px, skip
            bool nearGround = s.Grounded || (vy > 0f && Terraria.Collision.TileCollision(pos, new Vector2(0f, 2f), PlayerW, PlayerH, ft, ft, 1).Y < 2f);
            if (vx != 0f && nearGround && !ft)
                Terraria.Collision.StepUp(ref pos, ref vel, PlayerW, PlayerH, ref stepSpeed, ref gfxOffY);
            var result = Terraria.Collision.TileCollision(pos, vel, PlayerW, PlayerH, ft, ft, 1);

            if (vy < 0f && System.Math.Abs(result.Y - vel.Y) > 0.01f) jfl = 0;

            float nx = pos.X + result.X;
            float ny = pos.Y + result.Y;

            // vanilla Player.SlopingCollision (Player.cs L27716) runs AFTER position += velocity: Collision.SlopeCollision
            // lifts the player along a slope / half-brick face. without this the sim "walked through" slope half-bricks
            // the real game pushes up — the player gets lifted ~8px and, if a ceiling is above, jams (vx clips next
            // frame). modelling it makes the planner see the genuine block instead of a phantom flat walk.
            {
                var slope = Terraria.Collision.SlopeCollision(new Vector2(nx, ny), new Vector2(result.X, result.Y), PlayerW, PlayerH, ph.Gravity, ft);
                nx = slope.X; ny = slope.Y; result.X = slope.Z; result.Y = slope.W;
            }

            vxClipped = System.Math.Abs(result.X - vel.X) > 0.01f;
            vyClipped = System.Math.Abs(result.Y - vel.Y) > 0.01f;

            // game keeps the clipped fall residual on the landing frame (vy≠0); zeroing here lands one frame
            // early → +0.1px/seam drift. next frame's gravity re-clips it to ~0.
            bool hitFloor = vel.Y > 0f && vyClipped;
            vy = result.Y;

            vx = result.X;

            return new State { Px = nx, Py = ny, Vx = vx, Vy = vy, Grounded = hitFloor, JumpFramesLeft = jfl, JumpHStart = jfl > 0 ? jumpHStart : 0, WasWet = wetNow };
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
