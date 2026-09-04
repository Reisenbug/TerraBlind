using System;
using System.Threading;
using System.Threading.Tasks;
using Terraria;

namespace TerraBlind
{
    // Async wrapper around PathPlanner.PlanTo to keep the main thread responsive.
    // Latest-request-wins: a new Request supersedes any pending one (we just ignore the older result on arrival).
    // FRAGILE: PathPlanner reads Main.tile directly; race with player mining is possible.
    // ASSUMPTION: a corrupted tile read at worst produces a bad path; we accept that and avoid snapshotting for now.
    public static class PlanningJob
    {
        private static readonly object _lock = new object();
        private static int _seq = 0;           // request id; result is dropped if not the latest
        private static int _completedSeq = -1; // most recent seq whose result was applied
        private static string _latestJson;     // result waiting to be picked up by main thread
        private static int _latestResultSeq = -1;
        private static int _goalWx, _goalWy;
        private static bool _running;
        private static long _startTick;
        private static int _resultGoalWx, _resultGoalWy;

        public static bool IsRunning { get { lock (_lock) return _running; } }
        public static int PendingGoalWx { get { lock (_lock) return _goalWx; } }
        public static int PendingGoalWy { get { lock (_lock) return _goalWy; } }

        // Submit a new request. If one is already running, it continues — but its result is discarded
        // unless it matches the latest seq when it completes. Caller polls TryTakeResult next tick.
        public static int Request(int goalWx, int goalWy)
        {
            int seq;
            lock (_lock)
            {
                _seq++;
                seq = _seq;
                _goalWx = goalWx;
                _goalWy = goalWy;
                _running = true;
                _startTick = Main.GameUpdateCount;
            }
            int captureSeq = seq;
            int captureGx = goalWx;
            int captureGy = goalWy;
            Task.Run(() =>
            {
                string json = null;
                try
                {
                    json = PathPlanner.PlanTo(captureGx, captureGy);
                }
                catch (Exception ex)
                {
                    DiagLog.Write($"[planjob] exception goal=({captureGx},{captureGy}) seq={captureSeq}: {ex.Message}");
                    json = "{\"path\":[],\"cost\":0,\"error\":\"exception\"}";
                }
                lock (_lock)
                {
                    if (captureSeq == _seq)
                    {
                        _latestJson = json;
                        _latestResultSeq = captureSeq;
                        _resultGoalWx = captureGx;
                        _resultGoalWy = captureGy;
                        _running = false;
                        DiagLog.Write($"[planjob] done seq={captureSeq} goal=({captureGx},{captureGy}) duration={Main.GameUpdateCount - _startTick}f");
                    }
                    else
                    {
                        DiagLog.Write($"[planjob] superseded seq={captureSeq} latest={_seq} — discarding");
                    }
                }
            });
            return seq;
        }

        // Main thread polls each tick. Returns true if a fresh result is available.
        public static bool TryTakeResult(out string json, out int goalWx, out int goalWy, out int seq)
        {
            lock (_lock)
            {
                if (_latestJson != null && _latestResultSeq > _completedSeq)
                {
                    json = _latestJson;
                    goalWx = _resultGoalWx;
                    goalWy = _resultGoalWy;
                    seq = _latestResultSeq;
                    _completedSeq = _latestResultSeq;
                    _latestJson = null;
                    return true;
                }
            }
            json = null; goalWx = 0; goalWy = 0; seq = -1;
            return false;
        }

        public static void Cancel()
        {
            lock (_lock)
            {
                _seq++;            // bumps to invalidate any in-flight task's result
                _latestJson = null;
                _running = false;
            }
        }
    }
}
