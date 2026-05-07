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
        private static bool _done = false;
        private static float _prevVY = 0f;
        private static int _airFrames = 0;
        private static float _jumpStartX = 0f;
        private static float _jumpStartY = 0f;

        public static int PredictedLandWx = -1;
        public static int PredictedLandWy = -1;

        public static bool IsActive { get { lock (_lock) { return _active; } } }
        public static bool Done { get { lock (_lock) { return _done; } } }

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
                _done = false;
                _prevVY = 0f;
                _active = true;
            }
        }

        public static void Stop()
        {
            lock (_lock) { _active = false; _done = false; }
        }

        public static void ApplyControls()
        {
            lock (_lock)
            {
                if (!_active) return;
                var p = Main.LocalPlayer;
                if (p == null || !p.active) return;

                if (_done) { _active = false; return; }

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
                        _airFrames = 0;
                        _jumpStartX = p.position.X + p.width / 2f;
                        _jumpStartY = p.position.Y + p.height;
                        SimulateLanding(p, Player.jumpHeight);
                        DiagLog.Write($"[jump] takeoff cx={_jumpStartX:0.#} cy={_jumpStartY:0.#} vx={p.velocity.X:0.##} vy={p.velocity.Y:0.##} targetX={_targetX:0.#}");
                        p.controlJump = true;
                    }
                    return;
                }

                if (_jumpFramesLeft > 0) { p.controlJump = true; _jumpFramesLeft--; }

                _airFrames++;
                float cx = p.position.X + p.width / 2f;
                bool pastTarget = _dirRight ? cx >= _targetX - 48f : cx <= _targetX + 48f;
                if (!pastTarget)
                {
                    if (_dirRight) p.controlRight = true;
                    else p.controlLeft = true;
                }

                if (_airFrames % 10 == 0)
                    DiagLog.Write($"[jump] frame={_airFrames} cx={cx:0.#} cy={(p.position.Y + p.height):0.#} vx={p.velocity.X:0.##} vy={p.velocity.Y:0.##}");

                if (_airFrames > 15 && _prevVY > 0f && p.velocity.Y == 0f)
                {
                    float landedCx = p.position.X + p.width / 2f;
                    DiagLog.Write($"[jump] landed cx={landedCx:0.#} targetX={_targetX:0.#} dx_px={landedCx - _targetX:0.#} frames={_airFrames} vx0_est={(landedCx - _jumpStartX) / System.Math.Max(_airFrames, 1):0.##}");
                    _done = true;
                }
                _prevVY = p.velocity.Y;
            }
        }
    }
}
