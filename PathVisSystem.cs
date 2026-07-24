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

        // GHOST TILES: draw a recorded structure as faint real-tile sprites (the actual block's look, half-transparent
        // "air version"), not solid debug squares. Each entry carries the tile type + frame so the correct sprite +
        // orientation renders. mineGhost cells (recorded removals) draw as a faint red outline instead.
        private static List<(int wx, int wy, ushort type, short frameX, short frameY, bool mine)> _ghosts = new();
        private static int _ghostTtl = 0;
        public static void SetGhosts(List<(int wx, int wy, ushort type, short frameX, short frameY, bool mine)> g, int ttlFrames = 7200)
        {
            lock (_lock) { _ghosts = g; _ghostTtl = ttlFrames; }
        }
        public static void ClearGhosts() { lock (_lock) { _ghosts = new(); _ghostTtl = 0; } }

        private static List<NavNode> _planPath = new();
        private static int[] _planEnvelope;

        // state-space planner viz: per-frame world positions
        private static List<(float wpx, float wpy, bool isJump)> _ssTrail = new();
        private static List<(float wpx, float wpy)> _ssExplored = new();
        private static (float wpx, float wpy)? _ssGoal;
        private static List<(int cx, int cy)> _ssPlaced = new();
        private static List<(int wx, int wy)> _ssMineTiles = new();
        private static int _ssTtl = 0;

        // LOOKAHEAD viz: the next leg the background thread pre-planned while the current leg walks. Drawn dim so it
        // reads as "tentative future" vs the bright current leg. _laStart = predicted landing the bg plan branched
        // from (white ring) — eyeball whether the bg trail actually connects to where the current leg ends.
        private static List<(float wpx, float wpy, bool isJump)> _laTrail = new();
        private static (float wpx, float wpy)? _laGoal;
        private static (float wpx, float wpy)? _laStart;
        private static int _laTtl = 0;

        private static Texture2D _pixel;

        public static void SetSSPath(List<(float wpx, float wpy, bool isJump)> trail,
                                     List<(float wpx, float wpy)> explored,
                                     float goalPx, float goalPy,
                                     List<(int cx, int cy)> placed = null,
                                     List<(int wx, int wy)> mineTiles = null, int ttlFrames = 1200)
        {
            lock (_lock) { _ssTrail = trail; _ssExplored = explored; _ssGoal = (goalPx, goalPy); _ssPlaced = placed ?? new(); _ssMineTiles = mineTiles ?? new(); _ssTtl = ttlFrames; }
        }

        // called from the background lookahead thread → must stay lock-guarded. Pass startPx/Py = the predicted
        // landing the plan branched from (drawn as a white ring to spot prediction-vs-reality drift).
        public static void SetLookaheadPath(List<(float wpx, float wpy, bool isJump)> trail,
                                            float goalPx, float goalPy, float startPx, float startPy, int ttlFrames = 1200)
        {
            lock (_lock) { _laTrail = trail; _laGoal = (goalPx, goalPy); _laStart = (startPx, startPy); _laTtl = ttlFrames; }
        }

        public static void ClearLookahead()
        {
            lock (_lock) { _laTtl = 0; _laTrail = new(); _laGoal = null; _laStart = null; }
        }

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
            lock (_lock) { if (_ttl > 0) _ttl--; if (_ssTtl > 0) _ssTtl--; if (_laTtl > 0) _laTtl--; if (_ghostTtl > 0) _ghostTtl--; }
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
            var spriteBatch = Main.spriteBatch;
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp,
                     DepthStencilState.None, RasterizerState.CullNone, null, Main.GameViewMatrix.TransformationMatrix);

            if (_pixel == null)
            {
                _pixel = new Texture2D(spriteBatch.GraphicsDevice, 1, 1);
                _pixel.SetData(new[] { Color.White });
            }

            // state-space planner visualization (dots): explored dim, walk yellow, jump green, goal red
            {
                List<(float, float, bool)> ssTrail; List<(float, float)> ssExp; (float, float)? ssGoal; List<(int, int)> ssPlaced; List<(int, int)> ssMine; int ssTtl;
                lock (_lock) { ssTrail = _ssTrail; ssExp = _ssExplored; ssGoal = _ssGoal; ssPlaced = _ssPlaced; ssMine = _ssMineTiles; ssTtl = _ssTtl; }
                if (ssTtl > 0)
                {
                    foreach (var (px, py) in ssExp) DrawDot(spriteBatch, px, py, new Color(70, 100, 200, 70));
                    foreach (var (mx, my) in ssMine) DrawTile(spriteBatch, mx, my, new Color(255, 60, 30, 180));
                    foreach (var (cx, cy) in ssPlaced) DrawTile(spriteBatch, cx, cy, new Color(180, 120, 255, 160));
                    foreach (var (px, py, isJump) in ssTrail)
                        DrawDot(spriteBatch, px, py, isJump ? new Color(0, 230, 90, 220) : new Color(255, 220, 0, 200));
                    if (ssGoal.HasValue) DrawDot(spriteBatch, ssGoal.Value.Item1, ssGoal.Value.Item2, new Color(255, 40, 40, 240));
                }
            }

            // LOOKAHEAD: the background-planned NEXT leg, drawn dim. walk=dim amber, jump=dim teal, goal=orange,
            // start=white ring (the predicted landing it branched from — should sit on the current leg's end).
            {
                List<(float, float, bool)> laTrail; (float, float)? laGoal; (float, float)? laStart; int laTtl;
                lock (_lock) { laTrail = _laTrail; laGoal = _laGoal; laStart = _laStart; laTtl = _laTtl; }
                if (laTtl > 0)
                {
                    foreach (var (px, py, isJump) in laTrail)
                        DrawDot(spriteBatch, px, py, isJump ? new Color(0, 150, 150, 130) : new Color(200, 150, 40, 120));
                    if (laGoal.HasValue) DrawDot(spriteBatch, laGoal.Value.Item1, laGoal.Value.Item2, new Color(255, 140, 0, 200));
                    if (laStart.HasValue) DrawDot(spriteBatch, laStart.Value.Item1, laStart.Value.Item2, new Color(255, 255, 255, 220));
                }
            }

            var envelope = NavCoordinator.IsActive ? PathPlanner.GetEnvelopeCache() : _planEnvelope;

            var lp = Main.LocalPlayer;
            if (lp != null)
            {
                int lpx = (int)((lp.position.X + lp.width / 2f) / 16f);
                int lpy = (int)((lp.position.Y + lp.height) / 16f);
                DrawTile(spriteBatch, lpx, lpy, new Color(255, 255, 255, 160));
            }

            List<(int wx, int wy, Color color)> extraTiles;
            lock (_lock) { extraTiles = new List<(int, int, Color)>(_tiles); }
            foreach (var (tx, ty, tc) in extraTiles)
                DrawTile(spriteBatch, tx, ty, tc);

            List<(int wx, int wy, ushort type, short frameX, short frameY, bool mine)> ghosts; int ghostTtl;
            lock (_lock) { ghosts = _ghosts; ghostTtl = _ghostTtl; }
            if (ghostTtl > 0)
                foreach (var g in ghosts)
                    DrawGhost(spriteBatch, g.wx, g.wy, g.type, g.frameX, g.frameY, g.mine);

            if (path != null)
                foreach (var node in path)
                    if (node.MineTiles != null)
                        foreach (var (mx, my) in node.MineTiles)
                        {
                            var tc = (node.Action == "platform_walk" || node.Action == "jump_bridge")
                                ? new Color(0, 255, 180, 180)
                                : (node.Action == "jump_x" || node.Action == "jump_y")
                                    ? new Color(0, 220, 100, 220)
                                    : new Color(255, 165, 0, 180);
                            DrawTile(spriteBatch, mx, my, tc);
                        }

            int prevWx = -1, prevWy = -1;
            if (path != null)
            for (int i = 0; i < path.Count; i++)
            {
                int wx = path[i].Wx, wy = path[i].Wy;
                string action = path[i].Action;

                if (action == "jump" && path[i].Frames != null && path[i].Frames.Count > 0)
                {
                    int swx = path[i].SourceWx, swy = path[i].SourceWy;
                    int jsign = wx > swx ? 1 : -1;
                    float arcVx = path[i].StartVx.HasValue ? jsign * System.Math.Abs(path[i].StartVx.Value) : jsign * PhysicsSimulator.MaxRunSpeed;
                    var s = new PhysicsSimulator.State
                    {
                        Px = PathPlanner.CanonicalPx(swx, (SubPx)path[i].SourceSub, PhysicsSimulator.PlayerW),
                        Py = (swy + 1) * 16f - PhysicsSimulator.PlayerH,
                        Vx = arcVx,
                        Vy = 0f, Grounded = true,
                        JumpFramesLeft = path[i].Frames.Count,
                    };
                    var ph = lp != null ? PhysicsSimulator.Params.FromPlayer(lp) : PhysicsSimulator.Params.Default;
                    foreach (var fi in path[i].Frames)
                    {
                        s = PhysicsSimulator.Step(s, fi, ph);
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
                    {
                        DrawTile(spriteBatch, wx - 1, py, new Color(255, 50, 50, 100));
                        DrawTile(spriteBatch, wx,     py, new Color(255, 50, 50, 100));
                    }
                }
                else if (action == "jump_x" && prevWx >= 0)
                {
                    int swx = path[i].SourceWx, swy = path[i].SourceWy;
                    int jsign = wx > swx ? 1 : -1;
                    var s = new PhysicsSimulator.State
                    {
                        Px = PathPlanner.CanonicalPx(swx, (SubPx)path[i].SourceSub, PhysicsSimulator.PlayerW),
                        Py = (swy + 1) * 16f - PhysicsSimulator.PlayerH,
                        Vx = 0f, Vy = 0f, Grounded = true, JumpFramesLeft = 15,
                    };
                    var ph = lp != null ? PhysicsSimulator.Params.FromPlayer(lp) : PhysicsSimulator.Params.Default;
                    for (int f = 0; f < 60; f++)
                    {
                        var inp = new PhysicsSimulator.ControlInput { Jump = f < 15, Right = jsign > 0, Left = jsign < 0 };
                        s = PhysicsSimulator.Step(s, inp, ph);
                        DrawDot(spriteBatch, s.Px + PhysicsSimulator.PlayerW / 2f, s.Py + PhysicsSimulator.PlayerH, new Color(0, 220, 100, 200));
                        int feetY = (int)((s.Py + PhysicsSimulator.PlayerH) / 16f);
                        if (f > 15 && feetY >= swy) break;
                    }
                }
                else if (action == "jump_y" && prevWy > wy)
                {
                    for (int py = wy; py <= prevWy; py++)
                        DrawTile(spriteBatch, wx, py, new Color(0, 220, 100, 100));
                }

                if (action.StartsWith("mine_") && path[i].MineTiles != null)
                {
                    var orange = new Color(255, 165, 0, 200);
                    foreach (var (mtx, mty) in path[i].MineTiles)
                        DrawTile(spriteBatch, mtx, mty, orange);
                }

                Color c;
                if (i < curIdx)
                    c = new Color(60, 60, 60, 60);
                else if (action == "jump")
                    c = new Color(255, 220, 0, 120);
                else if (action == "bridge")
                    c = new Color(180, 0, 255, 120);
                else if (action == "platform_walk")
                    c = new Color(0, 255, 120, 160);
                else if (action == "jump_bridge")
                    c = new Color(100, 200, 255, 160);
                else if (action == "jump_x" || action == "jump_y")
                    c = new Color(0, 220, 100, 200);
                else if (action == "fall")
                    c = new Color(0, 180, 255, 120);
                else if (action == "pillar")
                    c = new Color(255, 50, 50, 160);
                else if (action.StartsWith("mine_"))
                    c = new Color(255, 165, 0, 160);
                else
                    c = new Color(100, 220, 255, 80);

                DrawTile(spriteBatch, wx, wy, c);
                prevWx = wx; prevWy = wy;
            }

            if (path == null || path.Count == 0) { spriteBatch.End(); return; }
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

        // GHOST: the recorded block's ACTUAL sprite drawn faint (a half-transparent "air version"), so the preview
        // reads as the real structure — wood looks like wood, a platform like a platform, with correct orientation
        // from the recorded frameX/Y. A mine cell has no sprite (it's a removal) → faint red hollow square instead.
        private void DrawGhost(SpriteBatch sb, int wx, int wy, ushort type, short frameX, short frameY, bool mine)
        {
            float sx = wx * 16f - Main.screenPosition.X;
            float sy = wy * 16f - Main.screenPosition.Y;
            if (sx < -16 || sx > Main.screenWidth + 16) return;
            if (sy < -16 || sy > Main.screenHeight + 16) return;
            if (mine)
            {
                var red = new Color(255, 60, 60) * 0.35f;
                sb.Draw(_pixel, new Rectangle((int)sx, (int)sy, 16, 1), red);
                sb.Draw(_pixel, new Rectangle((int)sx, (int)sy + 15, 16, 1), red);
                sb.Draw(_pixel, new Rectangle((int)sx, (int)sy, 1, 16), red);
                sb.Draw(_pixel, new Rectangle((int)sx + 15, (int)sy, 1, 16), red);
                return;
            }
            if (type >= Terraria.ID.TileID.Count) { sb.Draw(_pixel, new Rectangle((int)sx, (int)sy, 16, 16), new Color(170, 0, 255) * 0.3f); return; }
            Main.instance.LoadTiles(type);   // vanilla's own ensure-loaded before a single-tile draw
            var tex = Terraria.GameContent.TextureAssets.Tile[type];
            if (tex == null || !tex.IsLoaded) { sb.Draw(_pixel, new Rectangle((int)sx, (int)sy, 16, 16), new Color(170, 0, 255) * 0.3f); return; }
            var src = new Rectangle(frameX, frameY, 16, 16);
            sb.Draw(tex.Value, new Vector2(sx, sy), src, Color.White * 0.4f, 0f, Vector2.Zero, 1f, SpriteEffects.None, 0f);
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
