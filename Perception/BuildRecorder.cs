using System.Collections.Generic;
using System.IO;
using System.Text;
using Terraria;
using Terraria.ModLoader;

namespace TerraBlind
{
    // SEMANTIC build recorder — records the FINAL STRUCTURE, not the build process. Detection is WORLD DIFF for BOTH
    // directions (symmetric): each frame, compare the watched box to last frame. A cell that gained a tile → PLACE
    // (recorded with its real tile type + frame for the ghost preview, and the held buildable item's id/name for
    // replay's inventory lookup). A cell that lost a tile → MINE. This replaces the old cursor/useItem-edge guess,
    // which missed held-down multi-places and drifted a cell off — the world diff catches every cell exactly where
    // it landed, just like the mining half always did.
    //
    // Storage is a PER-CELL map (relative coords), so only the FINAL state of each cell survives — build churn
    // (place then re-place then mine) collapses to the end result. Net rule: mining a cell THIS recording placed
    // cancels it (never happened); mining a pre-existing cell records a removal.
    //   DIFF   — a cell stores the item TYPE+NAME, never a hotbar slot (replay finds it wherever it sits).
    //   REUSE  — coords RELATIVE to an anchor (feet cell at Start); straight runs collapse into a `groups` hint.
    //   GHOST  — a cell also stores tile type+frameX/Y so the preview draws the real block faint, not a debug square.
    public class BuildRecorder : ModSystem
    {
        private static readonly object _lock = new object();
        private static bool _recording;

        // final state of one cell: a placement (Type/Name = item, Tile* = the tile that appeared) or a removal (Mine).
        private struct Cell { public bool Mine; public int Type; public string Name; public ushort TileType; public short FrameX, FrameY; }
        private static readonly Dictionary<(int cx, int cy), Cell> _cells = new();
        private static readonly HashSet<(int cx, int cy)> _placedHere = new();   // cells THIS recording placed

        private static int _anchorX, _anchorY;
        // last frame's tile-present snapshot of the watch box: cell → (type, frameX, frameY). "present" = has a tile.
        // Diffed each frame to find appeared/disappeared cells.
        private static readonly Dictionary<(int, int), (ushort type, short fx, short fy)> _prev = new();
        // the watch box is FIXED at the anchor (the player's cell at Start), NOT trailing the player. A trailing box
        // scanned pre-existing terrain into the diff as the player walked (誤录周围地形) and missed cells beyond
        // its reach; an anchored box only ever watches the region you framed at record time.
        private const int WatchRadius = 40;   // tiles around the anchor watched for tile appear/disappear

        public static bool IsRecording { get { lock (_lock) { return _recording; } } }
        public static int LastEventCount { get; private set; }

        public static void Start()
        {
            lock (_lock)
            {
                var p = Main.LocalPlayer;
                _anchorX = p != null ? (int)(p.Center.X / 16f) : 0;
                _anchorY = p != null ? (int)((p.position.Y + p.height) / 16f) : 0;   // feet cell
                _recording = true;
                _cells.Clear(); _placedHere.Clear();
                _prev.Clear();
                SnapshotBox(p);   // seed so the first frame's diff sees no spurious appears/disappears
            }
            DiagLog.Write($"[build-rec] start anchor=({_anchorX},{_anchorY})");
        }

        public static string Stop()
        {
            lock (_lock)
            {
                _recording = false;
                string json = BuildJson();
                var dir = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile)
                    + "/Library/Application Support/Terraria/tModLoader/TerraBlindLogs";
                try { Directory.CreateDirectory(dir); File.WriteAllText(dir + "/build_rec.json", json); } catch { }

                // GHOST preview: draw the captured structure as faint real-tile sprites (placements) + red outlines
                // (removals), rebased at the anchor. This is the "淡色版本的方块" — the actual blocks, half-transparent.
                var ghosts = new List<(int, int, ushort, short, short, bool)>();
                foreach (var kv in _cells)
                    ghosts.Add((_anchorX + kv.Key.cx, _anchorY + kv.Key.cy, kv.Value.TileType, kv.Value.FrameX, kv.Value.FrameY, kv.Value.Mine));
                PathVisSystem.SetGhosts(ghosts);

                LastEventCount = _cells.Count;
                DiagLog.Write($"[build-rec] stop cells={_cells.Count} → build_rec.json");
                return json;
            }
        }

        // per-cell final-state list + a reuse hint, sorted (cy,cx) so a platform row is contiguous.
        private static string BuildJson()
        {
            var keys = new List<(int cx, int cy)>(_cells.Keys);
            keys.Sort((a, b) => a.cy != b.cy ? a.cy.CompareTo(b.cy) : a.cx.CompareTo(b.cx));

            var sb = new StringBuilder();
            sb.Append("{\"anchor\":[").Append(_anchorX).Append(',').Append(_anchorY).Append("],\"events\":[");
            for (int i = 0; i < keys.Count; i++)
            {
                var k = keys[i]; var c = _cells[k];
                if (i > 0) sb.Append(',');
                sb.Append("{\"act\":\"").Append(c.Mine ? "mine" : "place").Append('"');
                if (!c.Mine)
                    sb.Append(",\"item\":\"").Append(JsonEsc(c.Name)).Append("\",\"id\":").Append(c.Type)
                      .Append(",\"tile\":").Append(c.TileType).Append(",\"fx\":").Append(c.FrameX).Append(",\"fy\":").Append(c.FrameY);
                sb.Append(",\"cx\":").Append(k.cx).Append(",\"cy\":").Append(k.cy).Append('}');
            }
            sb.Append("],\"groups\":[");
            AppendGroups(sb, keys);
            sb.Append("]}");
            return sb.ToString();
        }

        private static void AppendGroups(StringBuilder sb, List<(int cx, int cy)> keys)
        {
            bool first = true;
            int i = 0;
            while (i < keys.Count)
            {
                var ci = _cells[keys[i]];
                if (ci.Mine) { i++; continue; }
                int j = i + 1;
                int stepDx = 0, stepDy = 0; bool haveStep = false;
                while (j < keys.Count)
                {
                    var cj = _cells[keys[j]];
                    if (cj.Mine || cj.Type != ci.Type) break;
                    int dx = keys[j].cx - keys[j - 1].cx, dy = keys[j].cy - keys[j - 1].cy;
                    if (System.Math.Abs(dx) + System.Math.Abs(dy) != 1) break;
                    if (!haveStep) { stepDx = dx; stepDy = dy; haveStep = true; }
                    else if (dx != stepDx || dy != stepDy) break;
                    j++;
                }
                int count = j - i;
                if (count >= 3)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("{\"item\":\"").Append(JsonEsc(ci.Name)).Append("\",\"id\":").Append(ci.Type)
                      .Append(",\"from\":[").Append(keys[i].cx).Append(',').Append(keys[i].cy)
                      .Append("],\"step\":[").Append(stepDx).Append(',').Append(stepDy)
                      .Append("],\"count\":").Append(count).Append('}');
                    i = j;
                }
                else i++;
            }
        }

        // WORLD DIFF, both directions. Compare this frame's watch box to last frame's snapshot: a cell that gained a
        // tile → place (with the held buildable item as the placer); a cell that lost its tile → mine.
        public static void Tick(Player p)
        {
            lock (_lock)
            {
                if (!_recording || p == null) return;
                var held = p.inventory[p.selectedItem];
                bool buildItemHeld = held != null && !held.IsAir && (held.createTile >= 0 || held.createWall >= 0);

                // FIXED box at the anchor — never trails the player.
                var now = new Dictionary<(int, int), (ushort, short, short)>();
                for (int x = _anchorX - WatchRadius; x <= _anchorX + WatchRadius; x++)
                    for (int y = _anchorY - WatchRadius; y <= _anchorY + WatchRadius; y++)
                    {
                        if (!InBounds(x, y)) continue;
                        var t = Main.tile[x, y];
                        if (t.HasTile) now[(x, y)] = (t.TileType, (short)t.TileFrameX, (short)t.TileFrameY);
                    }

                // APPEARED → placement. Use the held buildable item as the placer identity; if somehow none is held
                // (grew a tile passively), fall back to a tile-only record (id -1) so the ghost still draws.
                foreach (var kv in now)
                    if (!_prev.ContainsKey(kv.Key))
                        RecordPlace(kv.Key.Item1, kv.Key.Item2, kv.Value.Item1, kv.Value.Item2, kv.Value.Item3, buildItemHeld ? held : null);

                // DISAPPEARED → mining (the box is fixed, so any prev cell now gone was removed, not walked away from).
                foreach (var kv in _prev)
                    if (!now.ContainsKey(kv.Key))
                        RecordMine(kv.Key.Item1, kv.Key.Item2);

                _prev.Clear();
                foreach (var kv in now) _prev[kv.Key] = kv.Value;
            }
        }

        // seed _prev with the FIXED anchor box so the first diff frame sees no change (and pre-existing terrain in
        // the box is baseline, never mistaken for a placement).
        private static void SnapshotBox(Player p)
        {
            _prev.Clear();
            for (int x = _anchorX - WatchRadius; x <= _anchorX + WatchRadius; x++)
                for (int y = _anchorY - WatchRadius; y <= _anchorY + WatchRadius; y++)
                {
                    if (!InBounds(x, y)) continue;
                    var t = Main.tile[x, y];
                    if (t.HasTile) _prev[(x, y)] = (t.TileType, (short)t.TileFrameX, (short)t.TileFrameY);
                }
        }

        private static void RecordPlace(int wx, int wy, ushort tileType, short fx, short fy, Item placer)
        {
            var key = (wx - _anchorX, wy - _anchorY);
            _cells[key] = new Cell
            {
                Mine = false,
                Type = placer != null ? placer.type : -1,
                Name = placer != null ? placer.Name : "",
                TileType = tileType, FrameX = fx, FrameY = fy,
            };
            _placedHere.Add(key);
            DiagLog.Write($"[build-rec] place tile={tileType} item={(placer != null ? placer.Name : "?")} rel=({key.Item1},{key.Item2})");
        }

        private static void RecordMine(int wx, int wy)
        {
            var key = (wx - _anchorX, wy - _anchorY);
            if (_placedHere.Remove(key)) { _cells.Remove(key); DiagLog.Write($"[build-rec] cancel (placed→mined) rel=({key.Item1},{key.Item2})"); return; }
            _cells[key] = new Cell { Mine = true, Type = -1, Name = "" };
            DiagLog.Write($"[build-rec] mine rel=({key.Item1},{key.Item2})");
        }

        private static bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Main.maxTilesX && y < Main.maxTilesY;

        private static string JsonEsc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
