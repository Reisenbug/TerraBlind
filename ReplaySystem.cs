using System.Collections.Generic;
using Terraria;
using Terraria.ModLoader;

namespace TerraBlind
{
    public class ReplayFrame
    {
        public bool Left, Right, Up, Down, Jump, UseItem, Grapple;
        public int SelectedSlot = -1;
        public float Mx, My;
    }

    public class ReplaySystem : ModSystem
    {
        private static readonly object _lock = new object();
        private static Queue<ReplayFrame> _frames = new Queue<ReplayFrame>();
        public static bool IsActive { get { lock (_lock) { return _frames.Count > 0; } } }

        public static void Load(List<ReplayFrame> frames)
        {
            lock (_lock)
            {
                _frames.Clear();
                foreach (var f in frames) _frames.Enqueue(f);
            }
        }

        public static void Stop()
        {
            lock (_lock) { _frames.Clear(); }
        }

        private static int _jumpFrames = 0;

        public static void ApplyControls()
        {
            ReplayFrame frame;
            lock (_lock)
            {
                if (_frames.Count == 0) return;
                frame = _frames.Dequeue();
            }
            var p = Main.LocalPlayer;
            if (p == null || !p.active) return;
            if (frame.Left) p.controlLeft = true;
            if (frame.Right) p.controlRight = true;
            if (frame.Up) p.controlUp = true;
            if (frame.Down) p.controlDown = true;
            if (frame.Jump && _jumpFrames == 0) _jumpFrames = 15;
            if (_jumpFrames > 0) { p.controlJump = true; _jumpFrames--; if (_jumpFrames == 0) _jumpFrames = -1; }
            else if (_jumpFrames == -1) { _jumpFrames = 0; }
            if (frame.UseItem) p.controlUseItem = true;
            if (frame.Grapple) p.controlHook = true;
            if (frame.SelectedSlot >= 0) p.selectedItem = frame.SelectedSlot;
            int mx = (int)(p.position.X + p.width / 2f + frame.Mx * 16f - Main.screenPosition.X);
            int my = (int)(p.position.Y + p.height / 2f + frame.My * 16f - Main.screenPosition.Y);
            Main.mouseX = mx;
            Main.mouseY = my;
            if (_frames.Count % 30 == 0)
                Main.NewText($"[Replay] mouse=({mx},{my}) tile=({frame.Mx:F1},{frame.My:F1}) px={p.position.X:F0} sp={Main.screenPosition.X:F0}");
        }
    }
}
