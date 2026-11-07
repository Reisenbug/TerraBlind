using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.Map;

namespace TerraBlind;

public static class TerrainMap
{
    public static JsonObject Read(JsonObject args)
    {
        int Number(string key, int fallback) => args[key]?.GetValue<int>() ?? fallback;
        var player = Main.LocalPlayer;
        int width = Number("width", 120), height = Number("height", 80), scale = 6;
        if (width is < 20 or > 200 || height is < 20 or > 160) throw new ArgumentException("地图宽度20..200格，高度20..160格。");
        int cx = Number("x", (int)(player.Center.X / 16)), cy = Number("y", (int)(player.Center.Y / 16));
        if (!WorldGen.InWorld(cx, cy, 1)) throw new ArgumentException("地图中心须在世界内。");
        int left = Math.Clamp(cx - width / 2, 1, Main.maxTilesX - width - 1);
        int top = Math.Clamp(cy - height / 2, 1, Main.maxTilesY - height - 1);
        const int margin = 54, header = 48, footer = 46;
        int imageWidth = width * scale + margin + 12, imageHeight = height * scale + header + footer;
        var device = Main.instance.GraphicsDevice;
        var previous = device.GetRenderTargets();
        var viewport = device.Viewport;
        var legend = new Dictionary<int, (string Name, Color Color, int Cells)>();
        var markers = new JsonArray();
        using var texture = new RenderTarget2D(device, imageWidth, imageHeight, false, SurfaceFormat.Color, DepthFormat.None);
        using var batch = new SpriteBatch(device);
        try
        {
            device.SetRenderTarget(texture);
            device.Clear(new Color(8, 12, 18));
            batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, DepthStencilState.None, RasterizerState.CullNone);
            void Box(Rectangle rect, Color color) => batch.Draw(TextureAssets.MagicPixel.Value, rect, color);
            void Text(string value, int x, int y, Color color, float size = .7f)
                => batch.DrawString(FontAssets.MouseText.Value, value, new Vector2(x, y), color, 0, Vector2.Zero, size, SpriteEffects.None, 0);
            void Outline(Rectangle r, Color color)
            {
                Box(new Rectangle(r.X, r.Y, r.Width, 1), color); Box(new Rectangle(r.X, r.Bottom - 1, r.Width, 1), color);
                Box(new Rectangle(r.X, r.Y, 1, r.Height), color); Box(new Rectangle(r.Right - 1, r.Y, 1, r.Height), color);
            }
            Text($"LIVE TERRAIN   tile x={left}..{left + width - 1}   y={top}..{top + height - 1}", 10, 5, Color.White);
            Text("P=player  bright=solid  dark=wall  orange=falling  red=trap", 10, imageHeight - 39, Color.White, .6f);
            Text("Blue=water  red=lava  gold=honey  violet=shimmer  grid=10 tiles", 10, imageHeight - 21, Color.LightGray, .6f);
            for (int y = top; y < top + height; y++)
            for (int x = left; x < left + width; x++)
            {
                var tile = Main.tile[x, y];
                var map = MapHelper.CreateMapTile(x, y, 255); map.Color = 0;
                var raw = MapHelper.GetMapTileXnaColor(ref map);
                bool foreground = tile.HasUnactuatedTile;
                bool solid = foreground && Main.tileSolid[tile.TileType] && !Main.tileSolidTop[tile.TileType];
                Color color = foreground ? solid ? raw : Color.Lerp(raw, new Color(12, 20, 30), .4f)
                    : tile.WallType > 0 ? Color.Lerp(raw, new Color(12, 20, 30), .8f) : new Color(9, 15, 22);
                if (tile.LiquidAmount > 0 && !solid)
                    color = tile.LiquidType switch { LiquidID.Lava => new Color(240, 60, 25), LiquidID.Honey => new Color(218, 158, 25),
                        LiquidID.Shimmer => new Color(170, 85, 225), _ => new Color(30, 110, 195) };
                var rect = new Rectangle(margin + (x - left) * scale, header + (y - top) * scale, scale, scale);
                Box(rect, color);
                if (foreground && Main.tileSolidTop[tile.TileType]) Box(new Rectangle(rect.X, rect.Y, scale, 2), raw);
                if (foreground && TileID.Sets.Falling[tile.TileType]) Outline(rect, Color.Orange);
                if (foreground && (TileID.Sets.TouchDamageImmediate[tile.TileType] > 0
                    || tile.TileType is TileID.PressurePlates or TileID.Traps or TileID.Boulder or TileID.Explosives))
                    Outline(rect, Color.Red);
                if (foreground)
                {
                    int type = tile.TileType;
                    string name = Lang.GetMapObjectName(map.Type);
                    if (string.IsNullOrWhiteSpace(name)) name = TileID.Search.GetName(type);
                    var old = legend.GetValueOrDefault(type, (name, raw, 0));
                    legend[type] = (old.Item1, raw, old.Item3 + 1);
                }
            }
            for (int x = left + (10 - left % 10) % 10; x < left + width; x += 10)
            {
                int px = margin + (x - left) * scale;
                Box(new Rectangle(px, header, 1, height * scale), Color.White * .12f);
                Text(x.ToString(), px - 12, header - 23, Color.LightGray, .65f);
            }
            for (int y = top + (10 - top % 10) % 10; y < top + height; y += 10)
            {
                int py = header + (y - top) * scale;
                Box(new Rectangle(margin, py, width * scale, 1), Color.White * .12f);
                Text(y.ToString(), 7, py - 7, Color.LightGray, .65f);
            }
            if (args["marks"] is JsonArray marks)
            {
                if (marks.Count > 20) throw new ArgumentException("每张地图最多20个标记。");
                foreach (var marker in marks)
                {
                    int x = marker["x"].GetValue<int>(), y = marker["y"].GetValue<int>();
                    string label = marker["label"].GetValue<string>();
                    if (label.Length is < 1 or > 8 || label.Any(c => !char.IsAsciiLetterOrDigit(c)))
                        throw new ArgumentException("标记label使用1..8个英文字母或数字。");
                    bool visible = x >= left && x < left + width && y >= top && y < top + height;
                    markers.Add(new JsonObject { ["label"] = label, ["x"] = x, ["y"] = y, ["visible"] = visible });
                    if (!visible) continue;
                    int px = margin + (x - left) * scale, py = header + (y - top) * scale;
                    Outline(new Rectangle(px - 2, py - 2, scale + 4, scale + 4), Color.Cyan);
                    Text(label, px + scale, py - 12, Color.Cyan);
                }
            }
            var body = new Rectangle(margin + (int)((player.position.X / 16 - left) * scale),
                header + (int)((player.position.Y / 16 - top) * scale),
                (int)Math.Ceiling(player.width / 16f * scale), (int)Math.Ceiling(player.height / 16f * scale));
            if (body.Intersects(new Rectangle(margin, header, width * scale, height * scale)))
            { Outline(body, Color.White); Text("P", body.Right + 2, body.Top - 10, Color.White, .7f); }
            batch.End();
            using var stream = new MemoryStream();
            texture.SaveAsPng(stream, imageWidth, imageHeight);
            byte[] png = stream.ToArray();
            string id = AgentImages.Register(png);
            string directory = Path.Combine(Main.SavePath, "TerraBlindAgent", "maps"); Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"map-{Main.GameUpdateCount}-{id}.png"); File.WriteAllBytes(path, png);
            return new JsonObject { ["ok"] = true, ["image_id"] = id, ["image_path"] = path, ["tick"] = Main.GameUpdateCount,
                ["bounds"] = new JsonObject { ["x"] = left, ["y"] = top, ["width"] = width, ["height"] = height },
                ["pixel_origin"] = new JsonArray(margin, header), ["pixels_per_tile"] = scale, ["grid_tiles"] = 10,
                ["coordinate_rule"] = "世界格x=bounds.x+(图片像素x-pixel_origin[0])/pixels_per_tile；y同理，向下增大。",
                ["source"] = "当前真实方块，原版小地图配色，固定亮度；明亮前景与暗色背景墙分开显示。",
                ["player"] = new JsonObject { ["x"] = player.Center.X / 16, ["y"] = player.Center.Y / 16,
                    ["width_tiles"] = player.width / 16f, ["height_tiles"] = player.height / 16f },
                ["marks"] = markers,
                ["foreground_legend"] = new JsonArray(legend.OrderByDescending(p => p.Value.Cells).Take(16).Select(p => (JsonNode)new JsonObject
                    { ["tile_type"] = p.Key, ["name"] = p.Value.Name, ["tile_cells"] = p.Value.Cells,
                        ["rgb"] = new JsonArray((int)p.Value.Color.R, (int)p.Value.Color.G, (int)p.Value.Color.B) }).ToArray()),
                ["detail"] = "用于布局和粗略地形判断。精确施工、碰撞和危险确认使用原始方块查询与实际执行结果。" };
        }
        finally { device.SetRenderTargets(previous); device.Viewport = viewport; }
    }
}
