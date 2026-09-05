using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraBlind
{
    // REPLAY-SIDE preview + conflict check for a build_rec.json. Two jobs, both read-only (no world edits):
    //   OVERLAY, draw the recorded structure FAINT at the target location (anchor = the player's feet now, or a
    //              passed cell), so the human sees where the house will land before replay drives a single step.
    //   CONFLICT, for every PLACE cell, is it already occupied (a block, a wall, or a TREE)? Trees are special:
    //              a tree-trunk tile can't be placed into or plainly mined (vanilla forbids mining a tree's support),
    //              so a placement landing on one needs the tree felled first, flagged distinctly.
    // Conflict cells draw in a warning colour; the replayer skips them (occupied place = already satisfied) and the
    // human sees exactly which cells clash. Kept parallel to BuildRecorder's format (anchor + events list).
    public static class BuildOverlay
    {
        public struct Ev { public string Act; public int Type; public int Rcx, Rcy; public ushort TileType; public short Fx, Fy; }   // relative coords + tile sprite
        public struct Conflict { public int Wx, Wy; public string Kind; }               // "block" | "wall" | "tree"

        private static readonly List<Ev> _events = new();
        private static int _anchorX, _anchorY;
        private static bool _loaded;
        private static readonly HashSet<(int, int)> _conflictCells = new();   // world cells flagged by the last preview
        public static int ConflictCount => _conflictCells.Count;
        public static bool IsConflict(int wx, int wy) => _conflictCells.Contains((wx, wy));

        // load build_rec.json from disk and rebase at an anchor. anchorX/Y < 0 → use the player's current feet cell.
        // returns false if the file is missing/empty.
        public static bool Load(int anchorX, int anchorY)
        {
            _events.Clear(); _loaded = false;
            string path = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile)
                + "/Library/Application Support/Terraria/tModLoader/TerraBlindLogs/build_rec.json";
            string json;
            try { json = File.ReadAllText(path); } catch { return false; }

            var evMatches = System.Text.RegularExpressions.Regex.Matches(json,
                "\\{\"act\":\"(place|mine)\"(?:,\"item\":\"(?:[^\"]*)\",\"id\":(-?\\d+),\"tile\":(\\d+),\"fx\":(-?\\d+),\"fy\":(-?\\d+))?,\"cx\":(-?\\d+),\"cy\":(-?\\d+)\\}");
            foreach (System.Text.RegularExpressions.Match m in evMatches)
            {
                _events.Add(new Ev
                {
                    Act = m.Groups[1].Value,
                    Type = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : -1,
                    TileType = m.Groups[3].Success ? (ushort)int.Parse(m.Groups[3].Value) : (ushort)0,
                    Fx = m.Groups[4].Success ? (short)int.Parse(m.Groups[4].Value) : (short)0,
                    Fy = m.Groups[5].Success ? (short)int.Parse(m.Groups[5].Value) : (short)0,
                    Rcx = int.Parse(m.Groups[6].Value),
                    Rcy = int.Parse(m.Groups[7].Value),
                });
            }
            if (_events.Count == 0) return false;

            var p = Main.LocalPlayer;
            _anchorX = anchorX >= 0 ? anchorX : (p != null ? (int)(p.Center.X / 16f) : 0);
            _anchorY = anchorY >= 0 ? anchorY : (p != null ? (int)((p.position.Y + p.height) / 16f) : 0);
            _loaded = true;
            return true;
        }

        // draw the ghost preview (the recorded blocks, faint real sprites) AND return the conflict list. A place cell
        // draws as its ghost tile; a conflict cell (occupied block/wall, or a tree) gets a coloured outline on top so
        // it stands out. mine cells draw as a red-outline ghost. Same faint "air version" as the recorder's stop draw.
        public static List<Conflict> PreviewAndConflicts()
        {
            var conflicts = new List<Conflict>();
            _conflictCells.Clear();
            if (!_loaded) return conflicts;

            var ghosts = new List<(int, int, ushort, short, short, bool)>();
            var tint = new List<(int, int, Color)>();   // thin conflict outlines drawn over the ghosts
            foreach (var e in _events)
            {
                int wx = _anchorX + e.Rcx, wy = _anchorY + e.Rcy;
                if (e.Act == "mine")
                {
                    ghosts.Add((wx, wy, (ushort)0, (short)0, (short)0, true));   // red-outline ghost
                    continue;
                }
                ghosts.Add((wx, wy, e.TileType, e.Fx, e.Fy, false));
                string kind = ConflictAt(wx, wy);
                if (kind != null)
                {
                    Color c = kind == "tree" ? new Color(255, 150, 0, 200) : new Color(255, 40, 40, 200);
                    tint.Add((wx, wy, c));
                    conflicts.Add(new Conflict { Wx = wx, Wy = wy, Kind = kind });
                    _conflictCells.Add((wx, wy));
                }
            }
            PathVisSystem.SetGhosts(ghosts, 7200);   // ~2 min
            PathVisSystem.SetTiles(tint, 7200);
            return conflicts;
        }

        // occupancy of a place target: a tree trunk (special, must be felled), else any solid/existing tile
        // ("block"), else a background wall ("wall"), else null (free, placement will succeed).
        private static string ConflictAt(int wx, int wy)
        {
            if (wx < 0 || wy < 0 || wx >= Main.maxTilesX || wy >= Main.maxTilesY) return null;
            var t = Main.tile[wx, wy];
            if (t.HasTile)
            {
                if (TileID.Sets.IsATreeTrunk[t.TileType]) return "tree";
                return "block";
            }
            if (t.WallType > 0) return "wall";
            return null;
        }

        public static int AnchorX => _anchorX;
        public static int AnchorY => _anchorY;
        public static IReadOnlyList<Ev> Events => _events;
    }
}
