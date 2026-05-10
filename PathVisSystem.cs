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
        private static List<(int wx, int wy, Color color)> _tiles = new();
        private static List<(int wx, int wy, string text, Color color)> _labels = new();
        private static int _ttl = 0;

        private static List<NavNode> _planPath = new();
        private static int[] _planEnvelope;

        private static Texture2D _pixel;

        public static void SetTiles(List<(int wx, int wy, Color color)> tiles, int ttlFrames = 600)
        {
            lock (_lock) { _tiles = tiles; _ttl = ttlFrames; }
        }

        public static void SetLabels(List<(int wx, int wy, string text, Color color)> labels, int ttlFrames = 600)
        {
            lock (_lock) { _labels = labels; _ttl = System.Math.Max(_ttl, ttlFrames); }
        }

        public static void SetPath(List<(int wx, int wy)> nodes, int ttlFrames = 600) { }
        public static void SetBlocks(List<(int wx, int wy)> pillar, List<(int wx, int wy)> bridge, int ttlFrames = 600) { }

        public static void SetPlanPath(List<NavNode> path, int[] envelope)
        {
            lock (_lock) { _planPath = path; _planEnvelope = envelope; }
        }

        public override void PostUpdateEverything()
        {
            lock (_lock) { if (_ttl > 0) _ttl--; }
        }

        public override void PostDrawTiles()
        {
            List<NavNode> path;
            int curIdx;
            if (NavCoordinator.IsActive)
            {
                var (p, i) = NavCoordinator.GetPathSnapshot();
                path = p; curIdx = i;
            }
            else
            {
                lock (_lock) { path = new List<NavNode>(_planPath); }
                curIdx = 0;
            }
            if (path == null || path.Count == 0) return;

            var spriteBatch = Main.spriteBatch;
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp,
                     DepthStencilState.None, RasterizerState.CullNone, null, Main.GameViewMatrix.TransformationMatrix);

            if (_pixel == null)
            {
                _pixel = new Texture2D(spriteBatch.GraphicsDevice, 1, 1);
                _pixel.SetData(new[] { Color.White });
            }

            var envelope = NavCoordinator.IsActive ? PathPlanner.GetEnvelopeCache() : _planEnvelope;

            var lp = Main.LocalPlayer;
            if (lp != null)
            {
                int lpx = (int)((lp.position.X + lp.width / 2f) / 16f);
                int lpy = (int)((lp.position.Y + lp.height) / 16f);
                DrawTile(spriteBatch, lpx, lpy, new Color(255, 255, 255, 160));
            }
            int prevWx = -1, prevWy = -1;
            for (int i = 0; i < path.Count; i++)
            {
                int wx = path[i].Wx, wy = path[i].Wy;
                string action = path[i].Action;

                if (action == "jump" && path[i].Frames != null && path[i].Frames.Count > 0)
                {
                    int swx = path[i].SourceWx, swy = path[i].SourceWy;
                    int jsign = wx > swx ? 1 : -1;
                    bool prevIsPillar = i > 0 && path[i - 1].Action == "pillar";
                    float arcVx = prevIsPillar ? 0f : jsign * PhysicsSimulator.MaxRunSpeed;
                    var s = new PhysicsSimulator.State
                    {
                        Px = swx * 16f - PhysicsSimulator.PlayerW / 2f + 8f,
                        Py = swy * 16f - PhysicsSimulator.PlayerH,
                        Vx = arcVx,
                        Vy = 0f, Grounded = true,
                        JumpFramesLeft = path[i].Frames.Count,
                    };
                    foreach (var fi in path[i].Frames)
                    {
                        s = PhysicsSimulator.Step(s, fi);
                        DrawDot(spriteBatch, s.Px + PhysicsSimulator.PlayerW / 2f, s.Py + PhysicsSimulator.PlayerH, new Color(100, 255, 100, 160));
                    }
                }
                else if (action == "jump" && prevWx >= 0 && envelope != null)
                {
                    int js = wx > prevWx ? 1 : -1;
                    int adx = System.Math.Abs(wx - prevWx);
                    for (int col = 1; col <= System.Math.Min(adx, envelope.Length - 1); col++)
                    {
                        int bx = prevWx + js * col;
                        int by = prevWy + envelope[col];
                        DrawTile(spriteBatch, bx, by, new Color(100, 255, 100, 80));
                    }
                }
                else if (action == "bridge")
                {
                    int bx0 = prevWx >= 0 ? prevWx : (lp != null ? (int)((lp.position.X + lp.width / 2f) / 16f) : wx);
                    int by0 = prevWx >= 0 ? prevWy : wy;
                    int js = wx > bx0 ? 1 : -1;
                    int adx = System.Math.Abs(wx - bx0);
                    for (int col = 1; col < adx; col++)
                    {
                        int bx = bx0 + js * col;
                        int by = adx > 0 ? by0 + (wy - by0) * col / adx : wy;
                        DrawTile(spriteBatch, bx, by, new Color(180, 0, 255, 60));
                    }
                }
                else if (action == "pillar" && prevWy > wy)
                {
                    for (int py = wy; py <= prevWy; py++)
                        DrawTile(spriteBatch, wx, py, new Color(255, 50, 50, 100));
                }

                Color c;
                if (i < curIdx)
                    c = new Color(60, 60, 60, 60);
                else if (action == "jump")
                    c = new Color(255, 220, 0, 120);
                else if (action == "bridge")
                    c = new Color(180, 0, 255, 120);
                else if (action == "fall")
                    c = new Color(0, 180, 255, 120);
                else if (action == "pillar")
                    c = new Color(255, 50, 50, 160);
                else
                    c = new Color(100, 220, 255, 80);

                DrawTile(spriteBatch, wx, wy, c);
                prevWx = wx; prevWy = wy;
            }

            var last = path[path.Count - 1];
            float gsx = last.Wx * 16f - Main.screenPosition.X;
            float gsy = (last.Wy - 1) * 16f - Main.screenPosition.Y;
            if (gsx >= -200 && gsx <= Main.screenWidth + 200 && gsy >= -100 && gsy <= Main.screenHeight + 100)
            {
                var font = Terraria.GameContent.FontAssets.MouseText.Value;
                ChatManager.DrawColorCodedStringWithShadow(spriteBatch, font,
                    $"goal ({last.Wx},{last.Wy}) n={path.Count} idx={curIdx}",
                    new Vector2(gsx, gsy), Color.Yellow, 0f, Vector2.Zero, new Vector2(0.7f));
            }

            spriteBatch.End();
        }

        private void DrawTile(SpriteBatch sb, int wx, int wy, Color c)
        {
            float sx = wx * 16f - Main.screenPosition.X;
            float sy = wy * 16f - Main.screenPosition.Y;
            if (sx < -16 || sx > Main.screenWidth + 16) return;
            if (sy < -16 || sy > Main.screenHeight + 16) return;
            sb.Draw(_pixel, new Rectangle((int)sx, (int)sy, 16, 16), c);
        }

        private void DrawDot(SpriteBatch sb, float px, float py, Color c)
        {
            float sx = px - Main.screenPosition.X;
            float sy = py - Main.screenPosition.Y;
            if (sx < -4 || sx > Main.screenWidth + 4) return;
            if (sy < -4 || sy > Main.screenHeight + 4) return;
            sb.Draw(_pixel, new Rectangle((int)sx - 2, (int)sy - 2, 4, 4), c);
        }
    }
}
