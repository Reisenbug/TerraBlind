using System;
using System.Collections.Generic;
using Terraria;
using Terraria.ModLoader;

namespace TerraBlind
{
    public enum NavState { Idle, Move, Fall, Jump, Bridge, BridgeFall, Pillar, Done, Failed }

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
        private static float _prevVY;
        private static uint _nodeEnterTick;

        private static int _lastStallPcx;
        private static int _stallCount;

        private static readonly Dictionary<(int, int), long> _blacklist = new Dictionary<(int, int), long>();
        private static (int, int) _lastGoal;
        private const int BlacklistTTL = 60 * 60;
        private const int BlacklistMax = 20;
        private static int _restartCooldown = 0;

        private const int ArriveX = 8;
        private const int StallFrames = 60;
        private const int PillarThresh = 8; // slightly above max jump height (7 tiles)

        public static void Start(int sign)
        {
            lock (_lock)
            {
                _sign = sign;
                _blacklist.Clear();
                State = NavState.Idle;
                _path.Clear();
                _pathIdx = 0;
                _stallCount = 0;
                _restartCooldown = 0;
                FailReason = "";
                _started = true;
                DiagLog.Write($"[nav] Start sign={sign}");
            }
        }

        private static HashSet<(int, int)> BlacklistSet()
        {
            var s = new HashSet<(int, int)>();
            long now = Main.GameUpdateCount;
            foreach (var kv in _blacklist)
                if (kv.Value > now) s.Add(kv.Key);
            return s;
        }

        private static void PurgeBlacklist()
        {
            long now = Main.GameUpdateCount;
            var expired = new List<(int, int)>();
            foreach (var kv in _blacklist)
                if (kv.Value <= now) expired.Add(kv.Key);
            foreach (var k in expired) _blacklist.Remove(k);
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

        private static bool BlocksStanding(int wx, int wy)
        {
            if (wx < 0 || wy < 0 || wx >= Main.maxTilesX || wy >= Main.maxTilesY) return true;
            var t = Main.tile[wx, wy];
            if (t == null || !t.HasTile) return false;
            return Main.tileSolid[t.TileType] || Main.tileSolidTop[t.TileType];
        }

        private static int FeetY(Player p)
        {
            int fy = (int)((p.position.Y + p.height) / 16f);
            while (fy > 0 && PathPlanner.SolidPublic(Pcx(p), fy)) fy--;
            return fy;
        }

        private static bool Standable(int wx, int wy) => !PathPlanner.SolidPublic(wx, wy) && !PathPlanner.PlatformPublic(wx, wy) && (PathPlanner.SolidPublic(wx, wy + 1) || PathPlanner.PlatformPublic(wx, wy + 1));

        private static void EmitNodeEnter(int idx, NavNode node, int pcx, int feetY, float vx, float vy)
        {
            int expEndWx = idx + 1 < _path.Count ? _path[idx + 1].Wx : node.Wx;
            int expEndWy = idx + 1 < _path.Count ? _path[idx + 1].Wy : node.Wy;
            DiagLog.WriteEvent(
                $"{{\"e\":\"node_enter\",\"tick\":{Main.GameUpdateCount},\"node_idx\":{idx}" +
                $",\"action\":\"{node.Action}\"" +
                $",\"exp_start_wx\":{node.Wx},\"exp_start_wy\":{node.Wy}" +
                $",\"exp_end_wx\":{expEndWx},\"exp_end_wy\":{expEndWy}" +
                $",\"actual_px\":{pcx},\"actual_py\":{feetY}" +
                $",\"vx\":{vx.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}" +
                $",\"vy\":{vy.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}}}");
            _nodeEnterTick = Main.GameUpdateCount;
        }

        private static void EmitNodeExit(int idx, string action, string status, int expWx, int expWy, int actualWx, int actualWy)
        {
            int dx = Math.Abs(actualWx - expWx);
            int dy = Math.Abs(actualWy - expWy);
            DiagLog.WriteEvent(
                $"{{\"e\":\"node_exit\",\"tick\":{Main.GameUpdateCount},\"node_idx\":{idx}" +
                $",\"action\":\"{action}\",\"status\":\"{status}\"" +
                $",\"exp_end_wx\":{expWx},\"exp_end_wy\":{expWy}" +
                $",\"actual_end_wx\":{actualWx},\"actual_end_wy\":{actualWy}" +
                $",\"delta_x\":{actualWx - expWx},\"delta_y\":{actualWy - expWy}" +
                $",\"duration_ticks\":{(int)(Main.GameUpdateCount - _nodeEnterTick)}}}");
            bool deviated = action switch
            {
                "move"   => dx > 4 || dy > 6,
                "jump"   => dx > 3 || dy > 2,
                "fall"   => dx > 2 || dy > 3,
                "bridge" => dx > 3 || dy > 2,
                "pillar" => dx > 2 || dy > 4,
                _        => false,
            };
            if (deviated)
                DiagLog.WriteEvent(
                    $"{{\"e\":\"nav_failed\",\"tick\":{Main.GameUpdateCount}" +
                    $",\"reason\":\"deviate\",\"last_node_idx\":{idx},\"last_action\":\"{action}\"" +
                    $",\"px\":{actualWx},\"py\":{actualWy},\"dx\":{dx},\"dy\":{dy},\"stall_count\":-1}}");
        }

        private static void EmitNavFailed(string reason, int lastIdx, string lastAction, int pcx, int feetY, int stallCount = -1)
        {
            DiagLog.WriteEvent(
                $"{{\"e\":\"nav_failed\",\"tick\":{Main.GameUpdateCount}" +
                $",\"reason\":\"{reason}\"" +
                $",\"last_node_idx\":{lastIdx},\"last_action\":\"{lastAction}\"" +
                $",\"px\":{pcx},\"py\":{feetY},\"stall_count\":{stallCount}}}");
            BreakpointSystem.CheckNavFailed();
        }

        private static void Replan(Player p)
        {
            int pcx = Pcx(p);
            int feetY = FeetY(p);
            PurgeBlacklist();
            DiagLog.Write($"[nav] Replan at ({pcx},{feetY}) state={State} blacklist={_blacklist.Count}");
            string json = PathPlanner.Plan(_sign, BlacklistSet());
            var newPath = ParsePath(json);
            if (newPath == null || newPath.Count == 0)
            {
                BlacklistGoal();
                if (_blacklist.Count >= BlacklistMax)
                {
                    State = NavState.Failed;
                    FailReason = $"blacklist full at ({pcx},{feetY})";
                    EmitNavFailed("blacklist_full", _pathIdx, _target.Action ?? "", pcx, feetY);
                    DiagLog.Write($"[nav] blacklist full, stopping");
                    return;
                }
                EmitNavFailed("replan_empty", _pathIdx, _target.Action ?? "", pcx, feetY);
                DiagLog.Write($"[nav] Replan empty, will retry");
                _restartCooldown = 30;
                State = NavState.Idle;
                _path.Clear();
                _pathIdx = 0;
                return;
            }
            var last = newPath[newPath.Count - 1];
            int fwd = _sign * (last.Wx - pcx);
            if (fwd <= 3)
            {
                BlacklistGoal();
                EmitNavFailed("replan_no_progress", _pathIdx, _target.Action ?? "", pcx, feetY);
                DiagLog.Write($"[nav] Replan no-progress fwd={fwd}, retrying");
                _restartCooldown = 30;
                State = NavState.Idle;
                _path.Clear();
                _pathIdx = 0;
                return;
            }
            var newGoal = (last.Wx, last.Wy);
            _lastGoal = newGoal;
            DiagLog.Write($"[nav] Replan ok len={newPath.Count} goal={newGoal}");
            _path = newPath;
            _pathIdx = 0;
            _stallCount = 0;
            State = NavState.Idle;
        }

        private static void BlacklistGoal()
        {
            if (_lastGoal != default)
            {
                _blacklist[_lastGoal] = Main.GameUpdateCount + BlacklistTTL;
                DiagLog.Write($"[nav] blacklisted {_lastGoal}");
            }
        }

        private static void BlacklistNode(int wx, int wy)
        {
            var key = (wx, wy);
            _blacklist[key] = Main.GameUpdateCount + BlacklistTTL;
            DiagLog.Write($"[nav] blacklisted node ({wx},{wy})");
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
                if (State == NavState.Failed) return;

                if (_restartCooldown > 0)
                {
                    _restartCooldown--;
                    return;
                }

                var p = Main.LocalPlayer;
                if (p == null || !p.active) return;

                int pcx = Pcx(p);
                int feetY = FeetY(p);
                float centerX = p.position.X + p.width / 2f;

                if (State != NavState.Idle && pcx == _lastStallPcx)
                {
                    _stallCount++;
                    if (_stallCount >= StallFrames)
                    {
                        EmitNavFailed("stall", _pathIdx, _target.Action ?? "", pcx, feetY, _stallCount);
                        _stallCount = 0;
                        Replan(p);
                        return;
                    }
                }
                else
                {
                    _stallCount = 0;
                }
                _lastStallPcx = pcx;

                if (State == NavState.Idle)
                {
                    if (_pathIdx >= _path.Count)
                    {
                        Replan(p);
                        return;
                    }
                    _target = _path[_pathIdx];
                    _prevVY = p.velocity.Y;

                    DiagLog.Write($"[nav] node[{_pathIdx}] ({_target.Wx},{_target.Wy}) {_target.Action} from ({pcx},{feetY})");
                    EmitNodeEnter(_pathIdx, _target, pcx, feetY, p.velocity.X, p.velocity.Y);
                    BreakpointSystem.CheckNodeAction(_target.Action);
                    BreakpointSystem.CheckPosition(pcx, feetY);

                    if (_target.Action == "jump")
                    {
                        if (p.velocity.Y > 1f)
                        {
                            DiagLog.Write($"[nav] jump aborted vy={p.velocity.Y:0.#} feetY={feetY} → replan");
                            EmitNavFailed("jump_abort", _pathIdx, "jump", pcx, feetY);
                            Replan(p);
                            return;
                        }
                        bool overshoot = _sign > 0 ? pcx > _target.Wx : pcx < _target.Wx;
                        if (overshoot)
                        {
                            DiagLog.Write($"[nav] jump overshoot pcx={pcx} target={_target.Wx} → replan");
                            EmitNavFailed("jump_overshoot", _pathIdx, "jump", pcx, feetY);
                            Replan(p);
                            return;
                        }
                        var jp = Main.LocalPlayer;
                        float launchX = jp.position.X + jp.width / 2f;
                        float targetX = _target.Wx * 16f + 8f + (_sign > 0 ? 16f : -16f);
                        JumpCoordinator.Start(_sign > 0, launchX, targetX);
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
                    int feetLeft = (int)(p.position.X / 16);
                    int feetRight = (int)((p.position.X + p.width - 1) / 16);
                    bool arrived = feetLeft <= _target.Wx && _target.Wx <= feetRight;
                    if (arrived)
                    {
                        EmitNodeExit(_pathIdx, "move", "done", _target.Wx, _target.Wy, pcx, feetY);
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
                        EmitNodeExit(_pathIdx, "fall", "done", _target.Wx, _target.Wy, pcx, feetY);
                        _pathIdx++;
                        State = NavState.Idle;
                    }
                    return;
                }

                if (State == NavState.Jump)
                {
                    if (_prevVY > 0f && p.velocity.Y == 0f)
                    {
                        int expWx = JumpCoordinator.PredictedLandWx >= 0 ? JumpCoordinator.PredictedLandWx : _target.Wx;
                        int expWy = JumpCoordinator.PredictedLandWy >= 0 ? JumpCoordinator.PredictedLandWy : _target.Wy;
                        DiagLog.Write($"[nav] jump landed ({pcx},{feetY}) predicted ({expWx},{expWy}) astar ({_target.Wx},{_target.Wy})");
                        EmitNodeExit(_pathIdx, "jump", "done", expWx, expWy, pcx, feetY);
                        JumpCoordinator.Stop();
                        _pathIdx++;
                        State = NavState.Idle;
                    }
                    _prevVY = p.velocity.Y;
                    return;
                }

                if (State == NavState.Bridge)
                {
                    if (Math.Abs(p.velocity.X) > 0.1f && !ReplaySystem.IsActive)
                        return;
                    if (p.velocity.Y > 0f)
                    {
                        ReplaySystem.Stop();
                        DiagLog.Write($"[nav] bridge fall detected vy={p.velocity.Y:0.#} feetY={feetY} → BridgeFall");
                        State = NavState.BridgeFall;
                        return;
                    }
                    float targetCX = _target.Wx * 16f + 8f;
                    float dist = _sign > 0 ? targetCX - centerX : centerX - targetCX;
                    if (dist <= ArriveX && p.velocity.Y == 0f)
                    {
                        EmitNodeExit(_pathIdx, "bridge", "done", _target.Wx, _target.Wy, pcx, feetY);
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
                        EmitNavFailed("bridge_blocked", _pathIdx, "bridge", pcx, feetY);
                        BlacklistNode(_target.Wx, _target.Wy);
                        Replan(p);
                        return;
                    }
                    int platformSlot = FindPlatformSlot(p);
                    if (platformSlot < 0)
                    {
                        EmitNavFailed("no_platform", _pathIdx, "bridge", pcx, feetY);
                        State = NavState.Failed;
                        FailReason = "no platform";
                        return;
                    }
                    if (!ReplaySystem.IsActive)
                    {
                        bool right = _sign > 0;
                        float mx = right ? 0.4f : -0.4f;
                        var moveFrame = new ReplayFrame { Right = right, Left = !right, UseItem = true, SelectedSlot = platformSlot, SmartCursor = 0, Mx = mx, My = 1.7f };
                        var holdFrame = new ReplayFrame { UseItem = true, SelectedSlot = platformSlot, SmartCursor = 0, Mx = mx, My = 1.7f };
                        var frames = new System.Collections.Generic.List<ReplayFrame>();
                        for (int i = 0; i < 30; i++) frames.Add(moveFrame);
                        for (int i = 0; i < 5; i++) frames.Add(holdFrame);
                        ReplaySystem.Load(frames);
                    }
                    return;
                }

                if (State == NavState.BridgeFall)
                {
                    int platformSlot = FindPlatformSlot(p);
                    if (platformSlot < 0)
                    {
                        EmitNavFailed("no_platform", _pathIdx, "bridge_fall", pcx, feetY);
                        State = NavState.Failed;
                        FailReason = "no platform";
                        return;
                    }
                    if (!ReplaySystem.IsActive)
                    {
                        var fallFrame = new ReplayFrame { UseItem = true, SelectedSlot = platformSlot, SmartCursor = 0, Mx = -0.6f, My = 3.2f };
                        var frames = new System.Collections.Generic.List<ReplayFrame>();
                        for (int i = 0; i < 8; i++) frames.Add(fallFrame);
                        ReplaySystem.Load(frames);
                    }
                    if (p.velocity.Y == 0f)
                    {
                        DiagLog.Write($"[nav] bridge_fall landed feetY={feetY} → replan");
                        EmitNavFailed("bridge_deviate", _pathIdx, "bridge_fall", pcx, feetY);
                        ReplaySystem.Stop();
                        Replan(p);
                    }
                    return;
                }

                if (State == NavState.Pillar)
                {
                    if (!SkillExecutor.IsActive)
                    {
                        EmitNodeExit(_pathIdx, "pillar", "done", _target.Wx, _target.Wy, pcx, feetY);
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
                if (n.Action == "jump")
                {
                    _target = n;
                    EmitNodeEnter(_pathIdx, _target, pcx, feetY, p.velocity.X, p.velocity.Y);
                    float launchX = p.position.X + p.width / 2f;
                    float targetX = n.Wx * 16f + 8f + (_sign > 0 ? 16f : -16f);
                    JumpCoordinator.Start(_sign > 0, launchX, targetX);
                    State = NavState.Jump;
                    return;
                }
                if (n.Action != "move" && n.Action != "fall") { State = NavState.Idle; return; }
                if (n.Action == "fall")
                {
                    _target = n;
                    _prevVY = p.velocity.Y;
                    EmitNodeEnter(_pathIdx, _target, pcx, feetY, p.velocity.X, p.velocity.Y);
                    State = NavState.Fall;
                    return;
                }
                int fLeft = (int)(p.position.X / 16);
                int fRight = (int)((p.position.X + p.width - 1) / 16);
                bool passed = fLeft <= n.Wx && n.Wx <= fRight;
                if (passed) { _pathIdx++; continue; }
                int streakEndY = GetStreakEndY(_pathIdx);
                if (feetY - streakEndY > PillarThresh) { State = NavState.Idle; return; }
                _target = n;
                EmitNodeEnter(_pathIdx, _target, pcx, feetY, p.velocity.X, p.velocity.Y);
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
