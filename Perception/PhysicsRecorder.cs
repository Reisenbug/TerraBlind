using System.Collections.Generic;
using System.Text;
using Terraria;
using Terraria.ModLoader;

namespace TerraBlind
{
    public static class PhysicsRecorder
    {
        private static volatile bool _active = false;
        private static readonly List<FrameData> _frames = new List<FrameData>();
        private static readonly object _lock = new object();

        private struct FrameData
        {
            public int Frame;
            public float Px, Py, Vx, Vy;
            public bool Grounded, Jump;
        }

        public static void Start()
        {
            lock (_lock) { _frames.Clear(); _active = true; }
        }

        public static string Stop()
        {
            lock (_lock)
            {
                _active = false;
                var sb = new StringBuilder();
                sb.Append("{\"frames\":[");
                for (int i = 0; i < _frames.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    var f = _frames[i];
                    sb.Append("{\"f\":").Append(f.Frame)
                      .Append(",\"px\":").Append(f.Px.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture))
                      .Append(",\"py\":").Append(f.Py.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture))
                      .Append(",\"vx\":").Append(f.Vx.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture))
                      .Append(",\"vy\":").Append(f.Vy.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture))
                      .Append(",\"g\":").Append(f.Grounded ? "1" : "0")
                      .Append(",\"j\":").Append(f.Jump ? "1" : "0")
                      .Append("}");
                }
                sb.Append("]}");
                return sb.ToString();
            }
        }

        public static void Capture(Player p, bool jumpPressed)
        {
            if (!_active) return;
            lock (_lock)
            {
                _frames.Add(new FrameData
                {
                    Frame = _frames.Count,
                    Px = p.position.X,
                    Py = p.position.Y,
                    Vx = p.velocity.X,
                    Vy = p.velocity.Y,
                    Grounded = p.velocity.Y == 0f,
                    Jump = jumpPressed,
                });
            }
        }
    }
}
