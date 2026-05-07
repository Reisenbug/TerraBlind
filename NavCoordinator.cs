using System;
using System.Collections.Generic;
using Terraria;
using Terraria.ModLoader;

namespace TerraBlind
{
    public enum NavState { Idle, Move, Fall, Jump, Bridge, Pillar, Done, Failed }

    public struct NavNode
    {
        public int Wx, Wy;
        public string Action;
    }

    public class NavCoordinator : ModSystem
    {
        private static readonly object _lock = new object();

        public static NavState State = NavState.Idle;
        private static bool _started = false;
        public static bool IsActive => _started && State != NavState.Done && State != NavState.Failed;
        public static bool Done => State == NavState.Done;
        public static string FailReason = "";

        private static int _sign;
        private static List<NavNode> _path = new List<NavNode>();
        private static int _pathIdx;
        private static NavNode _target;
        private static int _segStartY;
        private static float _prevVY;

        private static int _stallCheckTick;
        private static int _lastStallPcx;
        private static int _stallCount;

        private const int ArriveX = 8;
        private const int DeviateY = 10;
        private const int StallFrames = 60;
        private const int StallLimit = 4;
        private const int PillarThresh = 5;

        public static void Start(int sign)
        {
            lock (_lock)
            {
                _sign = sign;
                State = NavState.Idle;
                _path.Clear();
                _pathIdx = 0;
                _stallCount = 0;
                _stallCheckTick = 0;
                FailReason = "";
                _started = true;
                DiagLog.Write($"[nav] Start sign={sign}");
            }
        }

        public static void Stop()
        {
            lock (_lock)
            {
                _started = false;
                State = NavState.Idle;
                _path.Clear();
                JumpCoordinator.Stop();
                ReplaySystem.Stop();
            }
        }

        public static string GetPathJson()
        {
            lock (_lock)
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("{\"idx\":").Append(_pathIdx).Append(",\"path\":[");
                for (int i = 0; i < _path.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"wx\":").Append(_path[i].Wx)
                      .Append(",\"wy\":").Append(_path[i].Wy)
                      .Append(",\"action\":\"").Append(_path[i].Action).Append("\"}");
                }
                sb.Append("]}");
                return sb.ToString();
            }
        }

        public static (List<NavNode> path, int idx) GetPathSnapshot()
        {
            lock (_lock)
            {
                return (new List<NavNode>(_path), _pathIdx);
            }
        }

        private static int Pcx(Player p) => (int)((p.position.X + p.width / 2f) / 16f);
        private static int FeetY(Player p)
        {
            int fy = (int)((p.position.Y + p.height) / 16f);
            while (fy > 0 && PathPlanner.SolidPublic(Pcx(p), fy)) fy--;
            return fy;
        }

        private static bool Standable(int wx, int wy) => !PathPlanner.SolidPublic(wx, wy) && PathPlanner.SolidPublic(wx, wy + 1);

        private static void Replan(Player p)
        {
            int pcx = Pcx(p);
            int feetY = FeetY(p);
            DiagLog.Write($"[nav] Replan at ({pcx},{feetY}) state={State}");
            string json = PathPlanner.Plan(_sign);
            var newPath = ParsePath(json);
            if (newPath == null || newPath.Count == 0)
            {
                State = NavState.Failed;
                FailReason = $"replan empty at ({pcx},{feetY})";
                DiagLog.Write($"[nav] Replan FAILED");
                return;
            }
            var last = newPath[newPath.Count - 1];
            int fwd = _sign * (last.Wx - pcx);
            if (fwd <= 3)
            {
                State = NavState.Failed;
                FailReason = $"replan no progress fwd={fwd} at ({pcx},{feetY})";
                DiagLog.Write($"[nav] Replan no-progress FAILED fwd={fwd}");
                return;
            }
            DiagLog.Write($"[nav] Replan ok len={newPath.Count}");
            _path = newPath;
            _pathIdx = 0;
            _stallCount = 0;
            State = NavState.Idle;
        }

        public static List<NavNode> ParsePathPublic(string json) => ParsePath(json);
        private static List<NavNode> ParsePath(string json)
        {
            var result = new List<NavNode>();
            if (string.IsNullOrEmpty(json)) return result;
            var matches = System.Text.RegularExpressions.Regex.Matches(
                json, "\"wx\"\\s*:\\s*(-?\\d+)\\s*,\\s*\"wy\"\\s*:\\s*(-?\\d+)\\s*,\\s*\"action\"\\s*:\\s*\"([^\"]+)\"");
            foreach (System.Text.RegularExpressions.Match m in matches)
                result.Add(new NavNode {
                    Wx = int.Parse(m.Groups[1].Value),
                    Wy = int.Parse(m.Groups[2].Value),
                    Action = m.Groups[3].Value
                });
            return result;
        }

        public static void ApplyControls()
        {
            lock (_lock)
            {
                if (!_started) return;
                if (State == NavState.Done || State == NavState.Failed) return;

                var p = Main.LocalPlayer;
                if (p == null || !p.active) return;

                int pcx = Pcx(p);
                int feetY = FeetY(p);
                float centerX = p.position.X + p.width / 2f;

                _stallCheckTick++;
                if (_stallCheckTick >= StallFrames)
                {
                    _stallCheckTick = 0;
                    if (State != NavState.Idle && _lastStallPcx == pcx)
                    {
                        _stallCount++;
                        if (_stallCount >= StallLimit)
                        {
                            Replan(p);
                            return;
                        }
                    }
                    else _stallCount = 0;
                    _lastStallPcx = pcx;
                }

                if (State == NavState.Idle)
                {
                    if (_pathIdx >= _path.Count)
                    {
                        Replan(p);
                        return;
                    }
                    _target = _path[_pathIdx];
                    _segStartY = feetY;
                    _prevVY = p.velocity.Y;

                    DiagLog.Write($"[nav] node[{_pathIdx}] ({_target.Wx},{_target.Wy}) {_target.Action} from ({pcx},{feetY})");
                    if (_target.Action == "jump")
                    {
                        if (p.velocity.Y > 1f)
                        {
                            DiagLog.Write($"[nav] jump aborted vy={p.velocity.Y:0.#} feetY={feetY} → replan");
                            Replan(p);
                            return;
                        }
                        var jp = Main.LocalPlayer;
                        float launchX = jp.position.X + jp.width / 2f;
                        bool dirRight = _target.Wx > pcx;
                        float targetX = _target.Wx * 16f + 8f + (dirRight ? 16f : -16f);
                        JumpCoordinator.Start(dirRight, launchX, targetX);
                        State = NavState.Jump;
                    }
                    else if (_target.Action == "bridge")
                    {
                        State = NavState.Bridge;
                    }
                    else if (_target.Action == "pillar")
                    {
                        int rise = feetY - _target.Wy;
                        DiagLog.Write($"[nav] pillar rise={rise} from ({pcx},{feetY}) to ({_target.Wx},{_target.Wy})");
                        State = NavState.Pillar;
                        SkillExecutor.StartPillarJump(_sign > 0, rise);
                    }
                    else if (_target.Action == "fall")
                    {
                        State = NavState.Fall;
                    }
                    else
                    {
                        int streakEndY = GetStreakEndY(_pathIdx);
                        if (feetY - streakEndY > PillarThresh)
                        {
                            State = NavState.Pillar;
                            SkillExecutor.StartPillarJump(_sign > 0, feetY - streakEndY);
                        }
                        else
                        {
                            State = NavState.Move;
                        }
                    }
                    return;
                }

                if (State == NavState.Move)
                {
                    if (feetY > _segStartY + DeviateY)
                    {
                        Replan(p);
                        return;
                    }
                    float targetCX = _target.Wx * 16f + 8f;
                    float dist = _sign > 0 ? targetCX - centerX : centerX - targetCX;
                    if (dist <= ArriveX)
                    {
                        _pathIdx++;
                        AdvanceMoveNodes(p, pcx, feetY);
                        return;
                    }
                    if (_sign > 0) p.controlRight = true;
                    else p.controlLeft = true;
                    return;
                }

                if (State == NavState.Fall)
                {
                    float targetCX = _target.Wx * 16f + 8f;
                    float dist = _sign > 0 ? targetCX - centerX : centerX - targetCX;
                    if (_sign > 0) p.controlRight = true;
                    else p.controlLeft = true;
                    bool onGround = p.velocity.Y == 0f;
                    bool landed = (_prevVY > 0f && onGround) || (onGround && dist <= ArriveX * 2);
                    _prevVY = p.velocity.Y;
                    if (landed && dist <= ArriveX * 2)
                    {
                        _pathIdx++;
                        State = NavState.Idle;
                    }
                    return;
                }

                if (State == NavState.Jump)
                {
                    if (JumpCoordinator.Done)
                    {
                        DiagLog.Write($"[nav] jump landed ({pcx},{feetY}) expected ({_target.Wx},{_target.Wy})");
                        _pathIdx++;
                        State = NavState.Idle;
                    }
                    return;
                }

                if (State == NavState.Bridge)
                {
                    float targetCX = _target.Wx * 16f + 8f;
                    float dist = _sign > 0 ? targetCX - centerX : centerX - targetCX;
                    if (dist <= ArriveX)
                    {
                        ReplaySystem.Stop();
                        _pathIdx++;
                        State = NavState.Idle;
                        return;
                    }
                    int aheadX = pcx + _sign;
                    if (PathPlanner.SolidPublic(aheadX, feetY) || PathPlanner.SolidPublic(aheadX, feetY - 1) || PathPlanner.SolidPublic(aheadX, feetY - 2))
                    {
                        ReplaySystem.Stop();
                        DiagLog.Write($"[nav] bridge blocked at ({pcx},{feetY}) ahead=({aheadX},{feetY}) → replan");
                        Replan(p);
                        return;
                    }
                    Player.SmartCursorSettings.SmartBlocksEnabled = false;
                    int platformSlot = FindPlatformSlot(p);
                    if (platformSlot < 0) { State = NavState.Failed; FailReason = "no platform"; return; }
                    if (!ReplaySystem.IsActive)
                    {
                        bool right = _sign > 0;
                        float mx0 = right ? 1.2f : -1.2f;
                        float mx1 = right ? 0.8f : -0.8f;
                        var frames = new System.Collections.Generic.List<ReplayFrame>();
                        frames.Add(new ReplayFrame { UseItem = true, SelectedSlot = platformSlot, Mx = mx0, My = 1.7f });
                        var moveFrame = new ReplayFrame { Right = right, Left = !right, UseItem = true, SelectedSlot = platformSlot, Mx = mx1, My = 1.7f };
                        for (int i = 0; i < 15; i++) frames.Add(moveFrame);
                        var holdFrame = new ReplayFrame { UseItem = true, SelectedSlot = platformSlot, Mx = mx1, My = 1.7f };
                        for (int i = 0; i < 10; i++) frames.Add(holdFrame);
                        ReplaySystem.Load(frames);
                    }
                    return;
                }

                if (State == NavState.Pillar)
                {
                    if (!SkillExecutor.IsActive)
                    {
                        _pathIdx++;
                        State = NavState.Idle;
                    }
                    return;
                }
            }
        }

        private static void AdvanceMoveNodes(Player p, int pcx, int feetY)
        {
            while (_pathIdx < _path.Count)
            {
                var n = _path[_pathIdx];
                if (n.Action != "move" && n.Action != "fall") { State = NavState.Idle; return; }
                if (n.Action == "fall")
                {
                    _target = n;
                    _segStartY = feetY;
                    _prevVY = p.velocity.Y;
                    State = NavState.Fall;
                    return;
                }
                float targetCX = n.Wx * 16f + 8f;
                float centerX = p.position.X + p.width / 2f;
                float dist = _sign > 0 ? targetCX - centerX : centerX - targetCX;
                if (dist <= ArriveX) { _pathIdx++; continue; }
                int streakEndY = GetStreakEndY(_pathIdx);
                if (feetY - streakEndY > PillarThresh) { State = NavState.Idle; return; }
                _target = n;
                State = NavState.Move;
                return;
            }
            Replan(p);
        }

        private static int GetStreakEndY(int idx)
        {
            int wy = _path[idx].Wy;
            for (int i = idx; i < _path.Count; i++)
            {
                if (_path[i].Action != "move" && _path[i].Action != "fall") break;
                wy = _path[i].Wy;
            }
            return wy;
        }

        private static int FindPlatformSlot(Player p)
        {
            for (int i = 0; i < 10; i++)
            {
                var item = p.inventory[i];
                if (item != null && !item.IsAir && item.createTile >= 0)
                {
                    var td = Terraria.ID.TileID.Sets.Platforms;
                    if (td != null && item.createTile < td.Length && td[item.createTile])
                        return i;
                }
            }
            return -1;
        }
    }
}
