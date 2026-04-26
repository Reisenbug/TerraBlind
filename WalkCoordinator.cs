using System;
using Terraria;
using Terraria.ModLoader;

namespace TerraBlind
{
    public class WalkCoordinator : ModSystem
    {
        private static readonly object _lock = new object();
        private static bool _active = false;
        private static bool _dirRight = true;
        private static float _extraTiles = 2f;
        private static int _extraFrames = 0;
        private static bool _foundEdge = false;

        public static bool IsActive { get { lock (_lock) { return _active; } } }
        public static bool Done { get { lock (_lock) { return !_active && _foundEdge; } } }

        public static void Start(bool dirRight, float extraTiles)
        {
            lock (_lock)
            {
                _dirRight = dirRight;
                _extraTiles = extraTiles;
                _extraFrames = 0;
                _foundEdge = false;
                _active = true;
            }
        }

        public static void Stop()
        {
            lock (_lock) { _active = false; _foundEdge = false; }
        }

        public static void ApplyControls()
        {
            lock (_lock)
            {
                if (!_active) return;
                var p = Main.LocalPlayer;
                if (p == null || !p.active) return;

                if (!_foundEdge)
                {
                    if (!HasOverhead(p))
                    {
                        _foundEdge = true;
                        _extraFrames = (int)Math.Round(_extraTiles * 16f / Math.Max(1f, Math.Abs(p.velocity.X == 0 ? 3f : p.velocity.X)));
                    }
                    else
                    {
                        if (_dirRight) p.controlRight = true;
                        else p.controlLeft = true;
                        return;
                    }
                }

                if (_extraFrames > 0)
                {
                    if (_dirRight) p.controlRight = true;
                    else p.controlLeft = true;
                    _extraFrames--;
                    return;
                }

                _active = false;
            }
        }

        private static bool HasOverhead(Player p)
        {
            int tileX = (int)(p.position.X / 16f);
            int tileY = (int)(p.position.Y / 16f);
            int widthTiles = (int)Math.Ceiling(p.width / 16f);
            for (int dx = 0; dx < widthTiles; dx++)
            {
                for (int dy = 1; dy <= 20; dy++)
                {
                    var t = Main.tile[tileX + dx, tileY - dy];
                    if (t != null && t.HasTile && !IsPlatform(t.TileType))
                        return true;
                }
            }
            return false;
        }

        private static bool IsPlatform(int type)
        {
            return Main.tileSolidTop[type];
        }
    }
}
