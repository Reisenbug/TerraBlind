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
        static int _ttl;

        static Texture2D _pixel;

        public static void SetField(int gx, int gy)
        {
            var f = MazeWand.GetField(gx, gy);
            lock (_lock) { _field = f; _goalWx = gx; _goalWy = gy; }
        }

        public static void SetDecision(int curCx, int curCy, int curH, int gx, int gy,
                                       List<StateSpacePlanner.Cand> cands, (int, int)? chosen, float score)
        {
            lock (_lock) { _curCx = curCx; _curCy = curCy; _curH = curH; _goalWx = gx; _goalWy = gy; _cands = cands; _chosen = chosen; _score = score; _ttl = 180; }
        }

        public static void Clear() { lock (_lock) { _cands = new(); _chosen = null; _ttl = 0; } }

        public override void PostUpdateEverything() { lock (_lock) if (_ttl > 0) _ttl--; }

        public override void PostDrawTiles()
        {
            if (!RecedingNav.Active) return;
            Dictionary<(int, int), int> field;
            int curCx, curCy, curH; List<StateSpacePlanner.Cand> cands; (int, int)? chosen; float score; int ttl;
            lock (_lock) { field = _field; curCx = _curCx; curCy = _curCy; curH = _curH; cands = _cands; chosen = _chosen; score = _score; ttl = _ttl; }

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

            if (ttl > 0)
            {
                bool stuck = chosen == null && cands.Count > 0;

                // candidates: green=lowers H (eligible), gray=not. label H/cost.
                foreach (var cd in cands)
                {
                    Color col = cd.Descends ? new Color(40, 200, 60, 160) : new Color(110, 110, 110, 130);
                    Box(sb, cd.Cx, cd.Cy, col);
                    Label(sb, font, cd.Cx, cd.Cy, $"{cd.Kind} H{cd.H} c{cd.Cost}", cd.Descends ? Color.LightGreen : Color.Gray);
                }

                // chosen action: yellow box + line from current + score
                if (chosen.HasValue)
                {
                    Box(sb, chosen.Value.Item1, chosen.Value.Item2, new Color(255, 230, 0, 220));
                    Line(sb, curCx, curCy, chosen.Value.Item1, chosen.Value.Item2, new Color(255, 230, 0, 200));
                    Label(sb, font, chosen.Value.Item1, chosen.Value.Item2 - 1, $"★{score:0.0}", Color.Yellow);
                }

                // current cell: white box + H (red if stuck)
                Box(sb, curCx, curCy, stuck ? new Color(255, 40, 40, 240) : new Color(255, 255, 255, 220));
                Label(sb, font, curCx, curCy + 1, stuck ? $"STUCK H{curH} {cands.Count} cands none↓" : $"H{curH}",
                      stuck ? Color.Red : Color.White);
            }

            // legend
            ChatManager.DrawColorCodedStringWithShadow(sb, font,
                "RECEDING  white=here  blue=field compass  green=action↓H  gray=action not↓  yellow=chosen  red=STUCK",
                new Vector2(10, 30), Color.White, 0f, Vector2.Zero, new Vector2(0.7f));

            sb.End();
        }

        static void Tile(SpriteBatch sb, int wx, int wy, Color c)
        {
            float sx = wx * 16f - Main.screenPosition.X, sy = wy * 16f - Main.screenPosition.Y;
            if (sx < -16 || sx > Main.screenWidth + 16 || sy < -16 || sy > Main.screenHeight + 16) return;
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

        static void Label(SpriteBatch sb, ReLogic.Graphics.DynamicSpriteFont font, int wx, int wy, string t, Color c)
        {
            float sx = wx * 16f - Main.screenPosition.X, sy = wy * 16f - Main.screenPosition.Y;
            if (sx < -60 || sx > Main.screenWidth + 60 || sy < -20 || sy > Main.screenHeight + 20) return;
            ChatManager.DrawColorCodedStringWithShadow(sb, font, t, new Vector2(sx, sy), c, 0f, Vector2.Zero, new Vector2(0.55f));
        }
    }
}
