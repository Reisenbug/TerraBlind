using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.ModLoader;
using Terraria.UI.Chat;

namespace TerraBlind
{
    // Receding-horizon visualization — shows the DECISION at the current node, so the algorithm is legible and a
    // stuck can be diagnosed by eye. Every step the planner asks "from here, which action?"; this draws that:
    //   • WHITE box + H  = where I am, how far from goal (field cost)
    //   • BLUE line      = field compass: follow the gradient downhill to the goal (global direction)
    //   • candidate boxes= every action considered, with landing H / cost. GREEN = lowers H (eligible), GRAY = not.
    //   • YELLOW box+line= the chosen action (best ΔH / cost), with its score
    //   • RED flash+text = STUCK: candidates exist but none lowers H (depth-1 can't reach the lower cell)
    public class RecedingVis : ModSystem
    {
        static readonly object _lock = new();
        static Dictionary<(int, int), int> _field;
        static int _goalWx, _goalWy;

        // current decision snapshot
        static int _curCx, _curCy, _curH;
        static List<StateSpacePlanner.Cand> _cands = new();
        static (int, int)? _chosen;
        static float _score;
        static (float x, float y) _dS, _dM, _dL;   // multi-scale big-direction vectors (unit) for viz
        static int _ttl;

        static Texture2D _pixel;

        public static void SetField(int gx, int gy)
        {
            var f = MazeWand.GetField(gx, gy);
            lock (_lock) { _field = f; _goalWx = gx; _goalWy = gy; }
        }

        public static void SetDecision(int curCx, int curCy, int curH, int gx, int gy,
                                       List<StateSpacePlanner.Cand> cands, (int, int)? chosen, float score,
                                       (float x, float y) dS, (float x, float y) dM, (float x, float y) dL)
        {
            lock (_lock) { _curCx = curCx; _curCy = curCy; _curH = curH; _goalWx = gx; _goalWy = gy; _cands = cands; _chosen = chosen; _score = score; _dS = dS; _dM = dM; _dL = dL; _ttl = 180; }
        }

        public static void Clear() { lock (_lock) { _cands = new(); _chosen = null; _ttl = 0; } }

        public override void PostUpdateEverything() { lock (_lock) if (_ttl > 0) _ttl--; }

        public override void PostDrawTiles()
        {
            if (!PathVisSystem.Enabled) return;   // 总闸,和另一个绘制出口共用
            if (!RecedingNav.Active) return;
            Dictionary<(int, int), int> field;
            int curCx, curCy, curH; List<StateSpacePlanner.Cand> cands; (int, int)? chosen; float score; int ttl;
            (float x, float y) dS, dM, dL;
            lock (_lock) { field = _field; curCx = _curCx; curCy = _curCy; curH = _curH; cands = _cands; chosen = _chosen; score = _score; dS = _dS; dM = _dM; dL = _dL; ttl = _ttl; }

            var sb = Main.spriteBatch;
            sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp,
                     DepthStencilState.None, RasterizerState.CullNone, null, Main.GameViewMatrix.TransformationMatrix);
            if (_pixel == null) { _pixel = new Texture2D(sb.GraphicsDevice, 1, 1); _pixel.SetData(new[] { Color.White }); }
            var font = Terraria.GameContent.FontAssets.MouseText.Value;

            // BLUE compass line: gradient downhill from the player to the goal
            if (field != null)
            {
                var lp = Main.LocalPlayer;
                var c = (x: (int)(lp.Center.X / 16f), y: (int)((lp.position.Y + lp.height) / 16f) - 1);
                var seen = new HashSet<(int, int)>();
                for (int s = 0; s < 4000; s++)
                {
                    if (!field.TryGetValue(c, out int hc) || c == (_goalWx, _goalWy) || !seen.Add(c)) break;
                    Tile(sb, c.x, c.y, new Color(60, 140, 255, 70));
                    var bst = c; int bh = hc;
                    foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                        if (field.TryGetValue((c.x + dx, c.y + dy), out int hn) && hn < bh) { bh = hn; bst = (c.x + dx, c.y + dy); }
                    if (bst == c) break; c = bst;
                }
            }

            // 屏幕只画一条线和一个点。候选的 H/分数属于日志([rn-dec]),摞在格子上没人读得动
            if (ttl > 0)
            {
                bool stuck = chosen == null && cands.Count > 0;
                // the point: where the next step lands — yellow normally, red when nothing can lower H
                var pt = chosen ?? (curCx, curCy);
                Box(sb, pt.Item1, pt.Item2, stuck ? new Color(255, 40, 40, 240) : new Color(255, 230, 0, 230));
                if (chosen.HasValue)
                    Line(sb, curCx, curCy, pt.Item1, pt.Item2, new Color(255, 230, 0, 200));
                if (stuck)
                    Label(sb, font, curCx, curCy + 1, "STUCK", Color.Red);
            }

            sb.End();
        }

        static void Tile(SpriteBatch sb, int wx, int wy, Color c)
        {
            float sx = wx * 16f - Main.screenPosition.X, sy = wy * 16f - Main.screenPosition.Y;
            if (sx < -16 || sx > Main.screenWidth + 16 || sy < -16 || sy > Main.screenHeight + 16) return;
            // 和 PathVisSystem.DrawTile 一样封顶,色块底下的真实方块要看得见
            if (c.A > 90) c = c * (90f / c.A);
            sb.Draw(_pixel, new Rectangle((int)sx, (int)sy, 16, 16), c);
        }

        static void Box(SpriteBatch sb, int wx, int wy, Color c)
        {
            float sx = wx * 16f - Main.screenPosition.X, sy = wy * 16f - Main.screenPosition.Y;
            if (sx < -16 || sx > Main.screenWidth + 16 || sy < -16 || sy > Main.screenHeight + 16) return;
            var r = new Rectangle((int)sx, (int)sy, 16, 16);
            sb.Draw(_pixel, new Rectangle(r.X, r.Y, r.Width, 2), c);
            sb.Draw(_pixel, new Rectangle(r.X, r.Bottom - 2, r.Width, 2), c);
            sb.Draw(_pixel, new Rectangle(r.X, r.Y, 2, r.Height), c);
            sb.Draw(_pixel, new Rectangle(r.Right - 2, r.Y, 2, r.Height), c);
        }

        static void Line(SpriteBatch sb, int ax, int ay, int bx, int by, Color c)
        {
            float x0 = ax * 16f + 8 - Main.screenPosition.X, y0 = ay * 16f + 8 - Main.screenPosition.Y;
            float x1 = bx * 16f + 8 - Main.screenPosition.X, y1 = by * 16f + 8 - Main.screenPosition.Y;
            float dx = x1 - x0, dy = y1 - y0; int n = (int)(System.MathF.Max(System.MathF.Abs(dx), System.MathF.Abs(dy)) / 3) + 1;
            for (int i = 0; i <= n; i++) sb.Draw(_pixel, new Rectangle((int)(x0 + dx * i / n) - 1, (int)(y0 + dy * i / n) - 1, 3, 3), c);
        }

        // thick arrow from cell (ax,ay) center, extending (dxCells,dyCells) in cell units. Draws a fat shaft plus two
        // arrowhead barbs at the tip so the direction reads at a glance.
        static void Arrow(SpriteBatch sb, int ax, int ay, float dxCells, float dyCells, Color c, int thick)
        {
            float x0 = ax * 16f + 8 - Main.screenPosition.X, y0 = ay * 16f + 8 - Main.screenPosition.Y;
            float vx = dxCells * 16f, vy = dyCells * 16f;
            float len = System.MathF.Sqrt(vx * vx + vy * vy);
            if (len < 1f) return;
            float ux = vx / len, uy = vy / len;          // unit along arrow
            float px = -uy, py = ux;                       // perpendicular
            float x1 = x0 + vx, y1 = y0 + vy;
            void Seg(float sx, float sy, float ex, float ey)
            {
                float ddx = ex - sx, ddy = ey - sy;
                int n = (int)(System.MathF.Max(System.MathF.Abs(ddx), System.MathF.Abs(ddy)) / 2) + 1;
                int h = thick;
                for (int i = 0; i <= n; i++)
                    sb.Draw(_pixel, new Rectangle((int)(sx + ddx * i / n) - h / 2, (int)(sy + ddy * i / n) - h / 2, h, h), c);
            }
            Seg(x0, y0, x1, y1);                                            // shaft
            float bl = 10f;                                                 // barb length
            Seg(x1, y1, x1 - ux * bl + px * bl * 0.6f, y1 - uy * bl + py * bl * 0.6f);   // barb 1
            Seg(x1, y1, x1 - ux * bl - px * bl * 0.6f, y1 - uy * bl - py * bl * 0.6f);   // barb 2
        }

        static void Label(SpriteBatch sb, ReLogic.Graphics.DynamicSpriteFont font, int wx, int wy, string t, Color c)
        {
            float sx = wx * 16f - Main.screenPosition.X, sy = wy * 16f - Main.screenPosition.Y;
            if (sx < -60 || sx > Main.screenWidth + 60 || sy < -20 || sy > Main.screenHeight + 20) return;
            ChatManager.DrawColorCodedStringWithShadow(sb, font, t, new Vector2(sx, sy), c, 0f, Vector2.Zero, new Vector2(0.55f));
        }
    }
}
