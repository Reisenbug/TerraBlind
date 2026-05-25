using System.Collections.Generic;
using Terraria;
using Terraria.ModLoader;

namespace TerraBlind
{
    public enum SegState { Idle, Planning, Executing, Done, Failed }

    public class SegmentedNavCoordinator : ModSystem
    {
        private static readonly object _lock = new object();

        private static SegState _state = SegState.Idle;
        private static List<(int wx, int wy)> _waypoints = new();
        private static int _wpIdx = 0;
        private static int _finalGx, _finalGy;
        private static int _segmentRadius = 25;
        private static int _executeTicks = 0;
        private static int _skipCount = 0;
        private const int MaxSkips = 3;
        private const int ExecuteTimeoutFrames = 60 * 30; // 30 sec
        private const int ArriveTolerance = 3;
        private const int MaxReplansPerSegment = 5;
        private static int _segStartReplanCount = 0;
        private const int GlobalTimeoutFrames = 60 * 300; // 5 min hard cap
        private static int _globalStartTick = 0;

        public static bool IsActive => _state != SegState.Idle && _state != SegState.Done && _state != SegState.Failed;
        public static SegState State => _state;
        public static string FailReason = "";

        public static void StartTo(int gx, int gy)
        {
            lock (_lock)
            {
                var p = Main.LocalPlayer;
                if (p == null) { _state = SegState.Failed; FailReason = "no player"; return; }
                int sx = (int)((p.position.X + p.width / 2f) / 16f);
                int sy = (int)((p.position.Y + p.height) / 16f);
                _finalGx = gx; _finalGy = gy;
                _waypoints = WaypointPlanner.Generate(sx, sy, gx, gy);
                _wpIdx = 0;
                _skipCount = 0;
                _state = SegState.Planning;
                _executeTicks = 0;
                _globalStartTick = (int)Main.GameUpdateCount;
                FailReason = "";
                DiagLog.Write($"[seg] StartTo ({gx},{gy}) waypoints={_waypoints.Count}");
                PushViz();
            }
        }

        public static void Stop()
        {
            lock (_lock)
            {
                _state = SegState.Idle;
                _waypoints.Clear();
                NavCoordinator.Stop();
            }
        }

        public override void PostUpdateEverything()
        {
            lock (_lock)
            {
                if (_state == SegState.Idle || _state == SegState.Done || _state == SegState.Failed) return;
                var p = Main.LocalPlayer;
                if (p == null) return;

                // global timeout
                if ((int)Main.GameUpdateCount - _globalStartTick > GlobalTimeoutFrames)
                {
                    DiagLog.Write($"[loop] seg global timeout reached → Failed");
                    _state = SegState.Failed;
                    FailReason = "global timeout";
                    NavCoordinator.Stop();
                    return;
                }

                if (_state == SegState.Planning)
                {
                    if (_wpIdx >= _waypoints.Count)
                    {
                        DiagLog.Write($"[seg] all waypoints done → Done");
                        _state = SegState.Done;
                        return;
                    }
                    var (wx, wy) = _waypoints[_wpIdx];
                    DiagLog.Write($"[seg] plan segment to wp[{_wpIdx}]=({wx},{wy})");
                    NavCoordinator.StartTo(wx, wy);
                    _segStartReplanCount = NavCoordinator.ReplanCount;
                    _executeTicks = 0;
                    _state = SegState.Executing;
                    return;
                }

                if (_state == SegState.Executing)
                {
                    _executeTicks++;
                    int pcx = (int)((p.position.X + p.width / 2f) / 16f);
                    int feetY = (int)((p.position.Y + p.height) / 16f);
                    var (wx, wy) = _waypoints[_wpIdx];

                    // arrival check (looser than NavCoordinator's exact match)
                    if (System.Math.Abs(pcx - wx) <= ArriveTolerance && System.Math.Abs(feetY - wy) <= ArriveTolerance)
                    {
                        DiagLog.Write($"[seg] wp[{_wpIdx}] reached at ({pcx},{feetY}) target=({wx},{wy}) vx={p.velocity.X:0.##}");
                        NavCoordinator.Stop();
                        _wpIdx++;
                        _skipCount = 0;
                        _state = SegState.Planning; // no settle wait; next segment plans from current state
                        PushViz();
                        return;
                    }

                    // NavCoordinator failed → skip waypoint
                    if (NavCoordinator.State == NavState.Failed || !NavCoordinator.IsActive)
                    {
                        DiagLog.Write($"[seg] NavCoordinator inactive/failed at wp[{_wpIdx}] ({wx},{wy}) reason='{NavCoordinator.FailReason}' → skip");
                        SkipOrFail("nav_failed");
                        return;
                    }

                    // per-segment replan limit
                    int segReplans = NavCoordinator.ReplanCount - _segStartReplanCount;
                    if (segReplans > MaxReplansPerSegment)
                    {
                        DiagLog.Write($"[loop] segment replan limit exceeded ({segReplans}) at wp[{_wpIdx}] ({wx},{wy}) → skip");
                        NavCoordinator.Stop();
                        SkipOrFail("seg_replan_limit");
                        return;
                    }

                    if (_executeTicks > ExecuteTimeoutFrames)
                    {
                        DiagLog.Write($"[seg] timeout at wp[{_wpIdx}] ({wx},{wy}) → skip");
                        NavCoordinator.Stop();
                        SkipOrFail("timeout");
                        return;
                    }
                    return;
                }

            }
        }

        private static void SkipOrFail(string reason)
        {
            _skipCount++;
            if (_skipCount >= MaxSkips)
            {
                DiagLog.Write($"[seg] too many skips → Failed ({reason})");
                _state = SegState.Failed;
                FailReason = reason;
                return;
            }
            _wpIdx++;
            _state = SegState.Planning;
            PushViz();
        }

        private static void PushViz()
        {
            var tiles = new List<(int, int, Microsoft.Xna.Framework.Color)>();
            for (int i = 0; i < _waypoints.Count; i++)
            {
                var (wx, wy) = _waypoints[i];
                Microsoft.Xna.Framework.Color c =
                    i < _wpIdx ? new Microsoft.Xna.Framework.Color(80, 80, 80, 180) :
                    i == _wpIdx ? new Microsoft.Xna.Framework.Color(255, 255, 0, 220) :
                                  new Microsoft.Xna.Framework.Color(255, 100, 255, 200);
                tiles.Add((wx, wy, c));
            }
            PathVisSystem.SetTiles(tiles, ttlFrames: 600);
        }
    }
}
