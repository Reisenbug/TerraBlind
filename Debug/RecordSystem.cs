using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;

namespace TerraBlind
{
    public class RecordSystem : ModSystem
    {
        private static readonly object _lock = new object();
        private static bool _recording = false;
        private static List<string> _frames = new List<string>();
        private static readonly List<(float wpx, float wpy, bool jump)> _trail = new();
        private static readonly List<(int cx, int cy)> _placed = new();
        private static readonly List<(int cx, int cy)> _mined = new();
        private static readonly HashSet<(int, int)> _minedSet = new();
        private static readonly HashSet<(int, int)> _prevSolid = new();
        private static bool _prevUse;
        private const int MineWatchRadius = 7; // tiles around the player to watch for HasTile→gone (≈ reach)

        public static bool IsRecording { get { lock (_lock) { return _recording; } } }
        public static int LastFrameCount { get; private set; }

        public static void Start()
        {
            lock (_lock) { _recording = true; _frames.Clear(); _trail.Clear(); _placed.Clear(); _mined.Clear(); _minedSet.Clear(); _prevSolid.Clear(); _prevUse = false; }
            DiagLog.Write("[rec] start");
        }

        public static string Stop()
        {
            lock (_lock)
            {
                _recording = false;
                var sb = new StringBuilder("{\"frames\":[");
                for (int i = 0; i < _frames.Count; i++) { if (i > 0) sb.Append(','); sb.Append(_frames[i]); }
                sb.Append("],\"placed\":[");
                for (int i = 0; i < _placed.Count; i++) { if (i > 0) sb.Append(','); sb.Append($"[{_placed[i].cx},{_placed[i].cy}]"); }
                sb.Append("],\"mined\":[");
                for (int i = 0; i < _mined.Count; i++) { if (i > 0) sb.Append(','); sb.Append($"[{_mined[i].cx},{_mined[i].cy}]"); }
                sb.Append("]}");
                string json = sb.ToString();

                var dir = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile)
                    + "/Library/Application Support/Terraria/tModLoader/TerraBlindLogs";
                try { Directory.CreateDirectory(dir); File.WriteAllText(dir + "/human_rec.json", json); } catch { }

                PathVisSystem.SetSSPath(new List<(float, float, bool)>(_trail), new List<(float, float)>(), 0, 0);
                var tiles = new List<(int, int, Color)>();
                foreach (var (cx, cy) in _placed) tiles.Add((cx, cy, new Color(170, 0, 255)));
                foreach (var (cx, cy) in _mined) tiles.Add((cx, cy, new Color(255, 60, 30)));
                PathVisSystem.SetTiles(tiles);

                LastFrameCount = _frames.Count;
                DiagLog.Write($"[rec] stop frames={_frames.Count} placed={_placed.Count} mined={_mined.Count} → human_rec.json");
                _frames.Clear();
                return json;
            }
        }

        public static void CaptureFrame(Player p, bool jumpOverride = false)
        {
            lock (_lock)
            {
                if (!_recording) return;
                bool jump = p.controlJump || jumpOverride;
                var sb = new StringBuilder("{");
                if (p.controlLeft) sb.Append("\"left\":true,");
                if (p.controlRight) sb.Append("\"right\":true,");
                if (p.controlUp) sb.Append("\"up\":true,");
                if (p.controlDown) sb.Append("\"down\":true,");
                if (jump) sb.Append("\"jump\":true,");
                if (p.controlUseItem) sb.Append("\"use_item\":true,");
                if (p.controlHook) sb.Append("\"grapple\":true,");
                sb.Append($"\"sc\":{(Main.SmartCursorWanted_Mouse ? 1 : 0)},");
                sb.Append($"\"slot\":{p.selectedItem},");
                float relX = (Main.mouseX + Main.screenPosition.X - p.position.X - p.width / 2f) / 16f;
                float relY = (Main.mouseY + Main.screenPosition.Y - p.position.Y - p.height / 2f) / 16f;
                sb.Append($"\"mx\":{relX:F1},\"my\":{relY:F1},");
                sb.Append($"\"px\":{p.position.X:F1},\"py\":{p.position.Y:F1},");
                sb.Append($"\"vx\":{p.velocity.X:F2},\"vy\":{p.velocity.Y:F2},");
                sb.Append($"\"gnd\":{(p.velocity.Y == 0f ? 1 : 0)}");
                sb.Append("}");
                _frames.Add(sb.ToString());

                _trail.Add((p.position.X + p.width / 2f, p.position.Y + p.height, jump));

                // live trail particle at the feet: green walking, orange while jumping, so the player sees the
                // path being recorded in real time.
                var feet = new Vector2(p.position.X + p.width / 2f, p.position.Y + p.height);
                int dustType = jump ? Terraria.ID.DustID.OrangeTorch : Terraria.ID.DustID.GreenTorch;
                var dust = Dust.NewDustPerfect(feet, dustType, Vector2.Zero, 0, default, 1.2f);
                dust.noGravity = true;

                // placement event: rising edge of useItem while holding a platform → record the target tile
                bool useNow = p.controlUseItem;
                if (useNow && !_prevUse && NavCoordinator.FindPlatformSlot(p) == p.selectedItem)
                {
                    int tcx = (int)((Main.mouseX + Main.screenPosition.X) / 16f);
                    int tcy = (int)((Main.mouseY + Main.screenPosition.Y) / 16f);
                    _placed.Add((tcx, tcy));
                    DiagLog.Write($"[rec-place] tile=({tcx},{tcy})");
                }

                // mining event: detect tiles that actually disappeared (HasTile last frame → gone now) in a small
                // box around the player. ground truth — independent of cursor / SmartCursor / reach, which made the
                // recorded cell drift along the dig direction.
                int pcx = (int)(p.Center.X / 16f), pcy = (int)(p.Center.Y / 16f);
                var nowSolid = new HashSet<(int, int)>();
                for (int x = pcx - MineWatchRadius; x <= pcx + MineWatchRadius; x++)
                    for (int y = pcy - MineWatchRadius; y <= pcy + MineWatchRadius; y++)
                    {
                        if (x < 0 || y < 0 || x >= Main.maxTilesX || y >= Main.maxTilesY) continue;
                        var t = Main.tile[x, y];
                        if (Predicates.IsWall(x, y))
                            nowSolid.Add((x, y));
                    }
                // a cell counts as MINED only if it vanished while still inside the watch box; cells that left the
                // box because the player walked away are not mined (that was the false-positive flooding mined[]).
                foreach (var cell in _prevSolid)
                {
                    bool inBox = cell.Item1 >= pcx - MineWatchRadius && cell.Item1 <= pcx + MineWatchRadius
                              && cell.Item2 >= pcy - MineWatchRadius && cell.Item2 <= pcy + MineWatchRadius;
                    if (inBox && !nowSolid.Contains(cell) && _minedSet.Add(cell))
                    {
                        _mined.Add(cell);
                        DiagLog.Write($"[rec-mine] tile=({cell.Item1},{cell.Item2})");
                    }
                }
                _prevSolid.Clear();
                _prevSolid.UnionWith(nowSolid);
                _prevUse = useNow;
            }
        }
    }
}
