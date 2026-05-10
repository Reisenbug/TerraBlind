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
        private static bool _replayMode = false;

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

        public static void StartReplay(bool dirRight, float launchX)
        {
            lock (_lock)
            {
                _dirRight = dirRight;
                _launchX = launchX;
                _targetX = launchX;
                _jumped = false;
                _jumpFramesLeft = 0;
                _replayMode = true;
                _active = true;
            }
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
                _replayMode = false;
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

                float centerX = p.position.X + p.width / 2f;
                float targetVx = _dirRight ? p.maxRunSpeed : -p.maxRunSpeed;

                if (!_jumped)
                {
                    float dist = _dirRight ? _launchX - centerX : centerX - _launchX;

                    if (dist > 16f)
                    {
                        if (_dirRight) p.controlRight = true;
                        else p.controlLeft = true;
                        return;
                    }

                    float vxDiff = p.velocity.X - targetVx;
                    bool vxOk = System.Math.Abs(vxDiff) < 0.5f;
                    bool posOk = System.Math.Abs(centerX - _launchX) <= 4f;

                    if (posOk && vxOk)
                    {
                        _jumped = true;
                        if (_replayMode)
                        {
                            DiagLog.Write($"[jump] takeoff replay cx={centerX:0.##} vx={p.velocity.X:0.##} launchX={_launchX:0.##} diff={centerX-_launchX:0.##}");
                            _active = false;
                            return;
                        }
                        _jumpFramesLeft = Player.jumpHeight + 2;
                        SimulateLanding(p, Player.jumpHeight);
                        DiagLog.Write($"[jump] takeoff cx={centerX:0.##} vx={p.velocity.X:0.##} launchX={_launchX:0.##} diff={centerX-_launchX:0.##}");
                        p.controlJump = true;
                        return;
                    }

                    bool needRight = centerX < _launchX;
                    if (needRight) p.controlRight = true;
                    else p.controlLeft = true;
                    return;
                }

                if (_jumpFramesLeft > 0) { p.controlJump = true; _jumpFramesLeft--; }

                float cx2 = p.position.X + p.width / 2f;
                bool pastTarget = _dirRight ? cx2 >= _targetX - 48f : cx2 <= _targetX + 48f;
                if (!pastTarget)
                {
                    if (_dirRight) p.controlRight = true;
                    else p.controlLeft = true;
                }
            }
        }
    }
}
