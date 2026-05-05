using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.ModLoader;
using Terraria.UI.Chat;

namespace TerraBlind
{
    public class PathVisSystem : ModSystem
    {
        private static readonly object _lock = new object();
        private static List<(int wx, int wy)> _nodes = new();
        private static List<(int wx, int wy)> _pillarBlocks = new();
        private static List<(int wx, int wy)> _bridgeBlocks = new();
        private static List<(int wx, int wy, string text, Color color)> _labels = new();
        private static int _ttl = 0;

        public static void SetPath(List<(int wx, int wy)> nodes, int ttlFrames = 600)
        {
            lock (_lock) { _nodes = nodes; _ttl = ttlFrames; }
        }

        public static void SetBlocks(List<(int wx, int wy)> pillar, List<(int wx, int wy)> bridge, int ttlFrames = 600)
        {
            lock (_lock) { _pillarBlocks = pillar; _bridgeBlocks = bridge; _ttl = ttlFrames; }
        }

        public static void SetLabels(List<(int wx, int wy, string text, Color color)> labels, int ttlFrames = 600)
        {
            lock (_lock) { _labels = labels; _ttl = System.Math.Max(_ttl, ttlFrames); }
        }

        private static void SpawnDust(float px, float py, Color color)
        {
            int d = Dust.NewDust(new Vector2(px - 2, py - 2), 4, 4, 267, 0f, 0f, 0, color, 0.8f);
            Main.dust[d].noGravity = true;
            Main.dust[d].velocity = Vector2.Zero;
        }

        public override void PostUpdateEverything()
        {
            lock (_lock)
            {
                if (_ttl <= 0) return;
                _ttl--;

                for (int i = 0; i < _nodes.Count; i++)
                {
                    var (wx, wy) = _nodes[i];
                    float x1 = wx * 16f + 8f, y1 = wy * 16f - 8f;
                    float x0 = i == 0 ? x1 : _nodes[i - 1].wx * 16f + 8f;
                    float y0 = i == 0 ? y1 : _nodes[i - 1].wy * 16f - 8f;
                    float ddx = x1 - x0, ddy = y1 - y0;
                    int steps = (int)(System.Math.Sqrt(ddx * ddx + ddy * ddy) / 8f) + 1;
                    for (int s = 0; s <= steps; s++)
                    {
                        float t = steps == 0 ? 0f : (float)s / steps;
                        SpawnDust(x0 + ddx * t, y0 + ddy * t, new Color(0, 220, 255));
                    }
                }

                foreach (var (wx, wy) in _pillarBlocks)
                    SpawnDust(wx * 16f + 8f, wy * 16f + 8f, new Color(255, 200, 0));

                foreach (var (wx, wy) in _bridgeBlocks)
                    SpawnDust(wx * 16f + 8f, wy * 16f + 8f, new Color(0, 255, 100));
            }
        }

        public override void PostDrawTiles()
        {
            List<(int wx, int wy, string text, Color color)> snapshot;
            lock (_lock)
            {
                if (_labels.Count == 0) return;
                snapshot = new List<(int wx, int wy, string text, Color color)>(_labels);
            }

            var sb = Main.spriteBatch;
            sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp,
                     DepthStencilState.None, RasterizerState.CullNone, null, Main.GameViewMatrix.TransformationMatrix);

            var font = Terraria.GameContent.FontAssets.MouseText.Value;
            foreach (var (wx, wy, text, color) in snapshot)
            {
                float screenX = wx * 16f - Main.screenPosition.X;
                float screenY = (wy - 1) * 16f - Main.screenPosition.Y;
                if (screenX < -200 || screenX > Main.screenWidth + 200) continue;
                if (screenY < -100 || screenY > Main.screenHeight + 100) continue;
                ChatManager.DrawColorCodedStringWithShadow(sb, font, text, new Vector2(screenX, screenY), color, 0f, Vector2.Zero, new Vector2(0.7f));
            }

            sb.End();
        }
    }
}
