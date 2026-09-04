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

        // dry/wet/honey variants for the current plan, filled by Params.FromPlayer; Step switches per-frame at the
        // liquid surface. single-threaded planner, so static is safe.
        static Params _dry, _wet, _honey;
        static bool _wetReady;

        // BAKED speed fields, captured at PostUpdate. Vanilla resets maxRunSpeed=3 every frame (ResetEffects), then
        // multiplies moveSpeed in LATE in the update (Player.cs 1.4.5.4 L25491: runAcceleration *= moveSpeed;
        // maxRunSpeed *= moveSpeed). Our planning hook (SetControls) sits between the reset and the bake, so reading
        // p.maxRunSpeed there returns bare 3.0 even under Happy! (buff 146: moveSpeed += 0.1 then *= 1.1 → +21%,
        // exactly the measured 3.64/3.0) — every plan near a sunflower undershot. PostUpdate reads the fully-baked
        // values of the SAME frame; planning prefers this snapshot when fresh.
        static float _bakedMaxRun, _bakedAccRunSpeed, _bakedRunAccel;
        static uint _bakedTick;
        public static void CaptureBaked(Player p)
        {
            _bakedMaxRun = p.maxRunSpeed; _bakedAccRunSpeed = p.accRunSpeed; _bakedRunAccel = p.runAcceleration;
            _bakedTick = Main.GameUpdateCount;
        }
        static bool BakedFresh => _bakedMaxRun > 0f && Main.GameUpdateCount - _bakedTick <= 2;
        static float BakedMaxRun(Player p) => BakedFresh ? _bakedMaxRun : p.maxRunSpeed;
        static float BakedAccRunSpeed(Player p) => BakedFresh ? _bakedAccRunSpeed : p.accRunSpeed;
        static float BakedRunAccel(Player p) => BakedFresh ? _bakedRunAccel : p.runAcceleration;

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
                _honey = BuildHoney(p);
                _wetReady = true;
                if (p.honeyWet) return _honey;
                return p.wet && !p.merman ? _wet : _dry;
            }

            // vanilla water: the VERTICAL velocity algorithm IS scaled (Update L23958: gravity=0.2, jumpSpeed=6.01,
            // maxFall=5) but the HORIZONTAL one is NOT (maxRun/accel stay bare). ON TOP of that, Player.WetCollision
            // (L22960) applies position += velocity*0.5 on both axes (moved axis full-speed if TileCollision clipped).
            // so: vertical params = water values, horizontal params = bare, plus Step's 0.5 position multiplier when wet.
            static Params BuildWet(Player p)
            {
                // vertical = water values; horizontal = live baked fields (water never modifies them), same as BuildDry.
                return new Params
                {
                    Gravity = 0.2f, JumpSpeed = 6.01f, MaxFall = 5f,
                    MaxRun = BakedMaxRun(p), AccRunSpeed = BakedAccRunSpeed(p),
                    AccRun = BakedRunAccel(p), RunSlowdown = 0.2f, JumpHeight = 30,
                };
            }

            // vanilla honey liquid (Update L23934): gravity=0.1, maxFall=3, jump params UNTOUCHED (unlike water's
            // 30-frame hold) — the crawl comes from the 0.25 position multiplier (L27588; water is 0.5), applied
            // in Step the same way as water's.
            static Params BuildHoney(Player p)
            {
                return new Params
                {
                    Gravity = 0.1f, JumpSpeed = 5.01f, MaxFall = 3f,
                    MaxRun = BakedMaxRun(p), AccRunSpeed = BakedAccRunSpeed(p),
                    AccRun = BakedRunAccel(p), RunSlowdown = 0.2f, JumpHeight = 15,
                };
            }

            static Params BuildDry(Player p)
            {
                // FRAGILE: the VERTICAL fields (gravity/jumpSpeed/jumpHeight/maxFallSpeed) are water-modified — when
                // FromPlayer runs while IN water they hold water values (gravity=0.2, jumpHeight=30, ...), so the air
                // variant must hardcode bare values, NOT read p.*. The HORIZONTAL fields are NEVER touched by water
                // (verified: no wet/liquid-gated write to runAcceleration/maxRunSpeed/accRunSpeed in Player.cs).
                // NOT baked at SetControls though (the old probe note claiming so was wrong): vanilla resets them
                // each frame and multiplies moveSpeed in only late in the update — so use the PostUpdate-captured
                // snapshot (Baked*), which covers all speed buffs/accessories (sunflower, swiftness, boots...).
                return new Params
                {
                    AccRun = BakedRunAccel(p),
                    MaxRun = BakedMaxRun(p),
                    AccRunSpeed = BakedAccRunSpeed(p),
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

            // per-frame liquid-surface switch: pick the dry/wet/honey variant by where the player IS this frame, so
            // a jump crossing the surface uses the right gravity/jumpSpeed/hold-cap on each side. WetCollision also
            // sets Collision.honey when the touched liquid is honey (vanilla reads it the same way).
            bool wetNow = s.WasWet;
            bool honeyNow = false;
            if (_wetReady)
            {
                wetNow = Terraria.Collision.WetCollision(new Vector2(s.Px, s.Py), PlayerW, PlayerH);
                honeyNow = wetNow && Terraria.Collision.honey;
                ph = honeyNow ? _honey : wetNow ? _wet : _dry;
                // crossing OUT of water mid-hold: vanilla (Player.cs L27354) caps remaining hold to jumpHeight/5 of
                // the AIR jumpHeight (15/5 = 3), regardless of frames already spent — NOT airCap-used.
                if (s.WasWet && !wetNow && jfl > ph.JumpHeight / 5)
                    jfl = ph.JumpHeight / 5;
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
                // vanilla L19527: weak-accel inner gate is velocity.Y==0 (on ground). use frame-start vy (pre-gravity),
                // matching HorizontalMovement's read. NOT a self-invented Grounded flag.
                if (vy == 0f)
                {
                    if (vx < -ph.RunSlowdown) vx += ph.RunSlowdown;
                    vx += ph.AccRun * 0.2f;
                }
            }
            else if (input.Left && vx > -ph.MaxRun)
            {
                if (vx > ph.RunSlowdown) vx -= ph.RunSlowdown;
                vx -= ph.AccRun;
            }
            else if (input.Left && vx > -ph.AccRunSpeed)
            {
                if (vy == 0f)
                {
                    if (vx > ph.RunSlowdown) vx -= ph.RunSlowdown;
                    vx -= ph.AccRun * 0.2f;
                }
            }
            else if (vy == 0f) // vanilla L19591 ground friction: velocity.Y==0, not a Grounded flag
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

            // vanilla runs JumpMovement (sets vy = -jumpSpeed) and the gravity block (vy += gravity, then clamp to
            // maxFall) as TWO separate steps in Update — and the gravity block runs EVERY frame, hold frames included
            // (Player.cs: JumpMovement L20212/L20322 → gravity L26830 → maxFall clamp L26841). keep them separate here
            // so each medium switch / future modifier lands on the right step instead of a pre-folded -4.61 constant.
            int jumpHStart = s.JumpHStart;
            // step 1 — JumpMovement (vanilla): two distinct cases. LAUNCH (vanilla L20316 `velocity.Y==0` → fire) and
            // HOLD-continue (vanilla L20204 `jump>0`, already airborne). vanilla's `if(velocity.Y==0)jump=0` (L20206)
            // is the HOLD case ending on landing — it must NOT fire on the launch frame (which IS at velocity.Y==0).
            // we distinguish them by jumpHStart: 0 = not launched yet (this is the launch frame), >0 = mid-hold.
            if (input.Jump && jfl > 0)
            {
                if (jumpHStart == 0)
                {
                    // LAUNCH frame: fire. clamp hold to launch-medium cap (planning submerged offers up to 30).
                    jumpHStart = ph.JumpHeight; if (jfl > ph.JumpHeight) jfl = ph.JumpHeight;
                    vy = -ph.JumpSpeed;
                    jfl--;
                }
                else if (vy == 0f)
                {
                    // mid-hold landed (vanilla L20206 `if(velocity.Y==0)jump=0`): end the hold.
                    jfl = 0;
                }
                else
                {
                    vy = -ph.JumpSpeed;
                    jfl--;
                }
            }
            else if (!input.Jump)
            {
                jfl = 0;
            }
            // step 2 — gravity block: applied every frame (hold's -jumpSpeed + gravity = -4.61 net for bare player).
            vy = System.Math.Min(vy + ph.Gravity, ph.MaxFall);

            // vanilla StickyMovement (Update L27137: after gravity, BEFORE the move): a honey block touching the
            // hitbox clamps and damps velocity every frame — vx to ±1 then ×0.85 (|vx|>0.75) / ×0.6, vy ≤1 / ≥−5
            // then ×0.96 rising / ×0.3 falling. This is what makes walking on a honey floor crawl at ≤1px/f; the
            // dry sim promised full-speed landings across honey that reality missed by cells. Cobweb (51) is NOT
            // modelled: the web reflex smashes it in one hit, so plans should treat it as passable.
            if (TouchesHoneyBlock(s.Px, s.Py))
            {
                if (vx > 1f) vx = 1f; else if (vx < -1f) vx = -1f;
                vx *= (vx > 0.75f || vx < -0.75f) ? 0.85f : 0.6f;
                if (vy > 1f) vy = 1f;
                if (vy < -5f) vy = -5f;
                vy *= vy < 0f ? 0.96f : 0.3f;
            }

            var pos = new Vector2(s.Px, s.Py);
            var vel = new Vector2(vx, vy);
            float stepSpeed = 0f, gfxOffY = 0f;
            // holding Down drops through platforms (solidTop): pass fallThrough=true to TileCollision and skip
            // StepUp (it would lift the player back onto the platform). matches vanilla controlDown behavior.
            bool ft = input.Down;
            // vanilla per-frame order (Player.cs L27536-27557): SlopeDownMovement → StepDown → StepUp, BEFORE the
            // move. these are the down-direction counterparts of StepUp — without them a walk off a downward slope or
            // a 1-tile down-step floats out and free-falls instead of hugging the ground like the real player.
            // SlopeDownMovement (WalkDownSlope): glue to a downward slope as you walk onto it.
            {
                var ds = Terraria.Collision.WalkDownSlope(pos, vel, PlayerW, PlayerH, ph.Gravity);
                pos.X = ds.X; pos.Y = ds.Y; vel.X = ds.Z; vel.Y = ds.W;
            }
            // StepDown (vanilla L27544): condition is exactly velocity.Y == gravity. NO !controlDown gate — vanilla
            // runs StepDown regardless of crouch; drop-through is handled by TileCollision's fallThrough below.
            if (vel.Y == ph.Gravity)
                Terraria.Collision.StepDown(ref pos, ref vel, PlayerW, PlayerH, ref stepSpeed, ref gfxOffY);
            // StepUp (vanilla L27555, gravDir=1 branch): exactly `(velocity.Y >= gravity) && !controlDown`. NOT a
            // self-invented vx!=0 + Grounded + 2px-probe. holdsMatching = controlUp (bare nav never presses up → false).
            if (vel.Y >= ph.Gravity && !ft)
                Terraria.Collision.StepUp(ref pos, ref vel, PlayerW, PlayerH, ref stepSpeed, ref gfxOffY);
            var result = Terraria.Collision.TileCollision(pos, vel, PlayerW, PlayerH, ft, ft, 1);

            if (vy < 0f && System.Math.Abs(result.Y - vel.Y) > 0.01f) jfl = 0;

            // vanilla Player.WetCollision (Player.cs L22962-22973): in liquid position += velocity×mult, but an axis
            // that TileCollision CLIPPED (hit a wall/floor) moves at full speed, not scaled. velocity itself stays
            // full-speed (so leaving liquid restores full motion instantly — fixes the out-of-water seam drift where
            // a speed-capped wet model crawled back to maxRun over ~18 frames). mult: water 0.5, honey 0.25 (L27588).
            float moveX = result.X, moveY = result.Y;
            if (wetNow)
            {
                float mult = honeyNow ? 0.25f : 0.5f;
                if (System.Math.Abs(result.X - vel.X) <= 0.01f) moveX = result.X * mult; // not clipped → scale
                if (System.Math.Abs(result.Y - vel.Y) <= 0.01f) moveY = result.Y * mult;
            }

            float nx = pos.X + moveX;
            float ny = pos.Y + moveY;

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

        // vanilla Collision.StickyTiles honey-block test (L3447-3467): un-sloped honey block (half-brick allowed),
        // tile box expanded 1px horizontally, hitbox nudged 0.01 up so standing exactly ON the block counts.
        static bool TouchesHoneyBlock(float px, float py)
        {
            float y0 = py - 0.01f;
            int c0 = (int)(px / 16f) - 1, c1 = (int)((px + PlayerW) / 16f) + 1;
            int r0 = (int)(y0 / 16f) - 1, r1 = (int)((y0 + PlayerH) / 16f) + 1;
            for (int i = c0; i <= c1; i++)
                for (int j = r0; j <= r1; j++)
                {
                    if (i < 0 || j < 0 || i >= Main.maxTilesX || j >= Main.maxTilesY) continue;
                    var t = Main.tile[i, j];
                    if (!t.HasTile || t.TileType != Terraria.ID.TileID.HoneyBlock || t.Slope != Terraria.ID.SlopeType.Solid) continue;
                    float ty = j * 16f, th = 16.01f;
                    if (t.IsHalfBlock) { ty += 8f; th -= 8f; }
                    if (px + PlayerW > i * 16f - 1f && px < i * 16f + 17f && y0 + PlayerH > ty && y0 < ty + th)
                        return true;
                }
            return false;
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
