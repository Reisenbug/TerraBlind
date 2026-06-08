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
        private static bool _prevUse;

        public static bool IsRecording { get { lock (_lock) { return _recording; } } }
        public static int LastFrameCount { get; private set; }

        public static void Start()
        {
            lock (_lock) { _recording = true; _frames.Clear(); _trail.Clear(); _placed.Clear(); _prevUse = false; }
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
                sb.Append("]}");
                string json = sb.ToString();

                var dir = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile)
                    + "/Library/Application Support/Terraria/tModLoader/TerraBlindLogs";
                try { Directory.CreateDirectory(dir); File.WriteAllText(dir + "/human_rec.json", json); } catch { }

                PathVisSystem.SetSSPath(new List<(float, float, bool)>(_trail), new List<(float, float)>(), 0, 0);
                var tiles = new List<(int, int, Color)>();
                foreach (var (cx, cy) in _placed) tiles.Add((cx, cy, new Color(170, 0, 255)));
                PathVisSystem.SetTiles(tiles);

                LastFrameCount = _frames.Count;
                DiagLog.Write($"[rec] stop frames={_frames.Count} placed={_placed.Count} → human_rec.json");
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
                _prevUse = useNow;
            }
        }
    }
}
