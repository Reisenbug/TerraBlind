using System.Collections.Generic;
using Terraria;
using Terraria.ModLoader;

namespace TerraBlind
{
    public class ReplayFrame
    {
        public bool Left, Right, Up, Down, Jump, UseItem, Grapple, UseAlt, UseTile, Mount;
        public int SelectedSlot = -1;
        public int SmartCursor = -1;
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
            if (frame.Jump) p.controlJump = true;
            if (frame.UseItem) p.controlUseItem = true;
            if (frame.Grapple) p.controlHook = true;
            if (frame.UseAlt) p.altFunctionUse = 2;
            if (frame.UseTile) p.controlUseTile = true;
            if (frame.Mount) p.controlMount = true;
            if (frame.SelectedSlot >= 0) p.selectedItem = frame.SelectedSlot;
            if (frame.SmartCursor >= 0) Main.SmartCursorWanted_Mouse = frame.SmartCursor == 1;
            int mx = (int)(p.position.X + p.width / 2f + frame.Mx * 16f - Main.screenPosition.X);
            int my = (int)(p.position.Y + p.height / 2f + frame.My * 16f - Main.screenPosition.Y);
            Main.mouseX = mx;
            Main.mouseY = my;
        }
    }
}
