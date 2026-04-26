using System.Collections.Generic;
using System.Text;
using Terraria;
using Terraria.ModLoader;

namespace TerraBlind
{
    public class RecordSystem : ModSystem
    {
        private static readonly object _lock = new object();
        private static bool _recording = false;
        private static List<string> _frames = new List<string>();

        public static bool IsRecording { get { lock (_lock) { return _recording; } } }

        public static void Start()
        {
            lock (_lock) { _recording = true; _frames.Clear(); }
        }

        public static string Stop()
        {
            lock (_lock)
            {
                _recording = false;
                var sb = new StringBuilder("[");
                for (int i = 0; i < _frames.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(_frames[i]);
                }
                sb.Append("]");
                _frames.Clear();
                return sb.ToString();
            }
        }

        public static void CaptureFrame(Player p, bool jumpOverride = false)
        {
            lock (_lock)
            {
                if (!_recording) return;
                var sb = new StringBuilder("{");
                if (p.controlLeft) sb.Append("\"left\":true,");
                if (p.controlRight) sb.Append("\"right\":true,");
                if (p.controlUp) sb.Append("\"up\":true,");
                if (p.controlDown) sb.Append("\"down\":true,");
                if (p.controlJump || jumpOverride) sb.Append("\"jump\":true,");
                if (p.controlUseItem) sb.Append("\"use_item\":true,");
                if (p.controlHook) sb.Append("\"grapple\":true,");
                float relX = (Main.mouseX + Main.screenPosition.X - p.position.X - p.width / 2f) / 16f;
                float relY = (Main.mouseY + Main.screenPosition.Y - p.position.Y - p.height / 2f) / 16f;
                sb.Append($"\"mx\":{relX:F1},\"my\":{relY:F1}");
                sb.Append("}");
                _frames.Add(sb.ToString());
            }
        }
    }
}
