using System.Collections.Generic;

namespace TerraBlind
{
    public struct Breakpoint
    {
        public string Id;
        public string On;
        public string Value;
        public int X0, X1, Y0, Y1;
        public string Field;
        public float Threshold;
        public int N;
    }

    public static class BreakpointSystem
    {
        private static readonly object _lock = new object();
        private static readonly List<Breakpoint> _bps = new List<Breakpoint>();

        public static void Set(Breakpoint bp)
        {
            lock (_lock)
            {
                for (int i = 0; i < _bps.Count; i++)
                    if (_bps[i].Id == bp.Id) { _bps[i] = bp; return; }
                _bps.Add(bp);
            }
        }

        public static void Clear(string id)
        {
            lock (_lock)
            {
                for (int i = _bps.Count - 1; i >= 0; i--)
                    if (_bps[i].Id == id) { _bps.RemoveAt(i); return; }
            }
        }

        public static List<Breakpoint> GetAll()
        {
            lock (_lock) { return new List<Breakpoint>(_bps); }
        }

        public static void CheckNavFailed()
        {
            lock (_lock)
            {
                foreach (var bp in _bps)
                    if (bp.On == "nav_failed") { Trigger(bp); return; }
            }
        }

        public static void CheckNodeAction(string action)
        {
            lock (_lock)
            {
                foreach (var bp in _bps)
                    if (bp.On == "node_action_eq" && bp.Value == action) { Trigger(bp); return; }
            }
        }

        public static void CheckPosition(int pcx, int pcy)
        {
            lock (_lock)
            {
                foreach (var bp in _bps)
                {
                    if (bp.On == "position_in_box" && pcx >= bp.X0 && pcx <= bp.X1 && pcy >= bp.Y0 && pcy <= bp.Y1)
                    { Trigger(bp); return; }
                }
            }
        }

        public static void CheckDelta(string field, float value)
        {
            lock (_lock)
            {
                foreach (var bp in _bps)
                    if (bp.On == "delta_gt" && bp.Field == field && System.Math.Abs(value) > bp.Threshold)
                    { Trigger(bp); return; }
            }
        }

        public static void CheckBlacklistSize(int size)
        {
            lock (_lock)
            {
                foreach (var bp in _bps)
                    if (bp.On == "blacklist_size_gte" && size >= bp.N) { Trigger(bp); return; }
            }
        }

        private static void Trigger(Breakpoint bp)
        {
            DiagLog.Write($"[bp] triggered id={bp.Id} on={bp.On}");
            DiagLog.WriteDebugReport(bp.Id, bp.On);
            FreezeSystem.Freeze(); // DragonLens: comment out when not debugging
        }
    }
}
