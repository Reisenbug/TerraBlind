using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;

namespace TerraBlind
{
    public class JumpCoordinator : ModSystem
    {
        private static readonly object _lock = new object();
        private static bool _active = false;
        private static bool _dirRight = true;
        private static float _launchX = 0f;
        private static float _targetX = 0f;
        private static bool _jumped = false;
        private static int _jumpFramesLeft = 0;

        public static int PredictedLandWx = -1;
        public static int PredictedLandWy = -1;

        public static bool IsActive { get { lock (_lock) { return _active; } } }

        private static void SimulateLanding(Player p, int holdFrames)
        {
            Vector2 pos = p.position;
            float vx = p.velocity.X;
            float vy = -Player.jumpSpeed;
            int w = p.width;
            int h = p.height;
            float grav = p.gravity > 0f ? p.gravity : 0.4f;
            float maxFall = p.maxFallSpeed > 0f ? p.maxFallSpeed : 10f;

            for (int frame = 0; frame < 300; frame++)
            {
                float prevVY = vy;
                if (frame < holdFrames) vy = -Player.jumpSpeed;
                else vy = System.Math.Min(vy + grav, maxFall);

                var vel = new Vector2(vx, vy);
                var result = Terraria.Collision.TileCollision(pos, vel, w, h, false, false, 1);
                pos.X += result.X;
                pos.Y += result.Y;

                bool landed = frame > holdFrames && prevVY > 0f && result.Y == 0f && vy > 0f;
                if (landed)
                {
                    PredictedLandWx = (int)((pos.X + w / 2f) / 16);
                    PredictedLandWy = (int)((pos.Y + h) / 16);
                    DiagLog.Write($"[jump] predict land wx={PredictedLandWx} wy={PredictedLandWy} frame={frame}");
                    return;
                }
                if (System.Math.Abs(result.Y - vy) > 0.01f) vy = 0f;
                if (System.Math.Abs(result.X - vx) > 0.01f) vx = 0f;
            }
            PredictedLandWx = -1;
            PredictedLandWy = -1;
        }

        public static void Start(bool dirRight, float launchX, float targetX)
        {
            lock (_lock)
            {
                _dirRight = dirRight;
                _launchX = launchX;
                _targetX = targetX;
                _jumped = false;
                _jumpFramesLeft = 0;
                _active = true;
            }
        }

        public static void Stop()
        {
            lock (_lock) { _active = false; }
        }

        public static void ApplyControls()
        {
            lock (_lock)
            {
                if (!_active) return;
                var p = Main.LocalPlayer;
                if (p == null || !p.active) return;

                if (!_jumped)
                {
                    if (_dirRight) p.controlRight = true;
                    else p.controlLeft = true;

                    float centerX = p.position.X + p.width / 2f;
                    float dist = _dirRight ? _launchX - centerX : centerX - _launchX;
                    if (dist <= 8f)
                    {
                        _jumped = true;
                        _jumpFramesLeft = Player.jumpHeight + 2;
                        SimulateLanding(p, Player.jumpHeight);
                        DiagLog.Write($"[jump] takeoff cx={centerX:0.#} vx={p.velocity.X:0.##} vy={p.velocity.Y:0.##} targetX={_targetX:0.#}");
                        p.controlJump = true;
                    }
                    return;
                }

                if (_jumpFramesLeft > 0) { p.controlJump = true; _jumpFramesLeft--; }

                float cx = p.position.X + p.width / 2f;
                bool pastTarget = _dirRight ? cx >= _targetX - 48f : cx <= _targetX + 48f;
                if (!pastTarget)
                {
                    if (_dirRight) p.controlRight = true;
                    else p.controlLeft = true;
                }
            }
        }
    }
}
