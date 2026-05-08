using System;
using System.Collections.Generic;
using Terraria;
using Terraria.ModLoader;

namespace TerraBlind
{
    public enum NavState { Idle, Move, Fall, Jump, Bridge, BridgeFall, Pillar, Done, Failed }

    public struct NavNode
    {
        public int Wx, Wy;         // landing tile (jump) / target tile (move/fall/bridge/pillar)
        public int SourceWx, SourceWy; // jump only: tile where simulation started
        public string Action;
        public System.Collections.Generic.List<PhysicsSimulator.ControlInput> Frames;
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
        private static int _lastStallFeetY;
        private static int _stallCount;
        private static bool _jumpReplayLoaded;
        private static bool _fixedPath;
        private static int _pillarSettleTick;

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
                _lastStallFeetY = 0;
                _restartCooldown = 0;
                _jumpReplayLoaded = false;
                _fixedPath = false;
                _pillarSettleTick = 0;
                FailReason = "";
                _started = true;
                DiagLog.Write($"[nav] Start sign={sign}");
            }
        }

        public static void SetPath(int sign, List<NavNode> nodes)
        {
            lock (_lock)
            {
                _sign = sign;
                _blacklist.Clear();
                State = NavState.Idle;
                _path = nodes;
                _pathIdx = 0;
                _stallCount = 0;
                _lastStallFeetY = 0;
                _restartCooldown = 0;
                _jumpReplayLoaded = false;
                _fixedPath = true;
                _pillarSettleTick = 0;
                FailReason = "";
                _started = true;
                DiagLog.Write($"[nav] SetPath sign={sign} nodes={nodes.Count}");
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
            if (_fixedPath) { State = NavState.Done; DiagLog.Write("[nav] SetPath done"); return; }
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
            if (fwd <= 0)
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
            _jumpReplayLoaded = false;
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
            var headerRe = new System.Text.RegularExpressions.Regex(
                "\"wx\"\\s*:\\s*(-?\\d+)\\s*,\\s*\"wy\"\\s*:\\s*(-?\\d+)\\s*,\\s*\"action\"\\s*:\\s*\"([^\"]+)\"");
            var sourceRe = new System.Text.RegularExpressions.Regex(
                "\"swx\"\\s*:\\s*(-?\\d+)\\s*,\\s*\"swy\"\\s*:\\s*(-?\\d+)");
            var frameRe = new System.Text.RegularExpressions.Regex(
                "\\{\"j\"\\s*:\\s*([01])\\s*,\\s*\"r\"\\s*:\\s*([01])\\s*,\\s*\"l\"\\s*:\\s*([01])\\s*\\}");

            // find each top-level node by scanning for "wx": after the path array starts
            int pathStart = json.IndexOf("\"path\":[");
            if (pathStart < 0) return result;
            int i = json.IndexOf('[', pathStart) + 1;
            while (i < json.Length)
            {
                // skip whitespace/commas
                while (i < json.Length && (json[i] == ',' || json[i] == ' ' || json[i] == '\n' || json[i] == '\r')) i++;
                if (i >= json.Length || json[i] != '{') break;
                // find matching closing brace (depth-aware)
                int depth = 0, start = i;
                while (i < json.Length)
                {
                    if (json[i] == '{') depth++;
                    else if (json[i] == '}') { depth--; if (depth == 0) { i++; break; } }
                    i++;
                }
                string nodeJson = json.Substring(start, i - start);
                var hm = headerRe.Match(nodeJson);
                if (!hm.Success) continue;
                var node = new NavNode
                {
                    Wx = int.Parse(hm.Groups[1].Value),
                    Wy = int.Parse(hm.Groups[2].Value),
                    Action = hm.Groups[3].Value,
                };
                if (node.Action == "jump")
                {
                    var sm = sourceRe.Match(nodeJson);
                    if (sm.Success)
                    {
                        node.SourceWx = int.Parse(sm.Groups[1].Value);
                        node.SourceWy = int.Parse(sm.Groups[2].Value);
                    }
                    var frames = new List<PhysicsSimulator.ControlInput>();
                    foreach (System.Text.RegularExpressions.Match fm in frameRe.Matches(nodeJson))
                        frames.Add(new PhysicsSimulator.ControlInput
                        {
                            Jump  = fm.Groups[1].Value == "1",
                            Right = fm.Groups[2].Value == "1",
                            Left  = fm.Groups[3].Value == "1",
                        });
                    node.Frames = frames.Count > 0 ? frames : null;
                }
                result.Add(node);
            }
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

                bool stalledX = State != NavState.Idle && State != NavState.Pillar && State != NavState.Jump && pcx == _lastStallPcx;
                bool stalledY = State == NavState.Pillar && feetY == _lastStallFeetY;
                if (stalledX || stalledY)
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
                _lastStallFeetY = feetY;

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
                        if (_target.Frames != null && _target.Frames.Count > 0)
                        {
                            // re-simulate from actual current state, skip precision alignment
                            var startState = new PhysicsSimulator.State
                            {
                                Px = p.position.X,
                                Py = p.position.Y,
                                Vx = p.velocity.X,
                                Vy = 0f,
                                Grounded = true,
                                JumpFramesLeft = _target.Frames.Count,
                            };
                            int holdFrames = 0;
                            foreach (var fi in _target.Frames) { if (fi.Jump) holdFrames++; else break; }
                            var sim = PhysicsSimulator.SimulateJump(startState, _sign, holdFrames);
                            List<ReplayFrame> replayFrames;
                            if (sim.Landed)
                            {
                                replayFrames = new List<ReplayFrame>();
                                foreach (var fi in sim.Frames)
                                    replayFrames.Add(new ReplayFrame { Jump = fi.Jump, Right = fi.Right, Left = fi.Left });
                                DiagLog.Write($"[nav] jump resim landed=({sim.Cx},{sim.Cy}) target=({_target.Wx},{_target.Wy}) frames={replayFrames.Count}");
                            }
                            else
                            {
                                replayFrames = new List<ReplayFrame>();
                                foreach (var fi in _target.Frames)
                                    replayFrames.Add(new ReplayFrame { Jump = fi.Jump, Right = fi.Right, Left = fi.Left });
                                DiagLog.Write($"[nav] jump resim failed, using planned frames={replayFrames.Count}");
                            }
                            _jumpReplayLoaded = true;
                            ReplaySystem.Load(replayFrames);
                            DiagLog.Write($"[nav] jump replay start frames={replayFrames.Count} src=({_target.SourceWx},{_target.SourceWy}) target=({_target.Wx},{_target.Wy})");
                        }
                        else
                        {
                            // no planned frames: resim from current state with default hold
                            var startState = new PhysicsSimulator.State
                            {
                                Px = p.position.X, Py = p.position.Y,
                                Vx = p.velocity.X, Vy = 0f,
                                Grounded = true, JumpFramesLeft = Player.jumpHeight,
                            };
                            var sim = PhysicsSimulator.SimulateJump(startState, _sign, Player.jumpHeight);
                            var replayFrames = new List<ReplayFrame>();
                            var srcFrames = sim.Landed ? sim.Frames : new List<PhysicsSimulator.ControlInput>();
                            foreach (var fi in srcFrames)
                                replayFrames.Add(new ReplayFrame { Jump = fi.Jump, Right = fi.Right, Left = fi.Left });
                            DiagLog.Write($"[nav] jump resim(noframes) landed={sim.Landed} cx={sim.Cx} target={_target.Wx} frames={replayFrames.Count}");
                            if (replayFrames.Count > 0)
                            {
                                _jumpReplayLoaded = true;
                                ReplaySystem.Load(replayFrames);
                            }
                            else
                            {
                                Replan(p);
                                return;
                            }
                        }
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
                        SkillExecutor.StartPillarJump(_sign > 0, _target.Wy);
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
                            DiagLog.Write($"[nav] forced pillar feetY={feetY} streakEndY={streakEndY} rise={feetY - streakEndY}");
                            State = NavState.Pillar;
                            SkillExecutor.StartPillarJump(_sign > 0, streakEndY);
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
                    // if next node is a replay jump, hand off early when within 16px of source tile
                    int nextIdx = _pathIdx + 1;
                    if (nextIdx < _path.Count && _path[nextIdx].Action == "jump" && _path[nextIdx].Frames != null)
                    {
                        float launchX = _path[nextIdx].SourceWx * 16f + 8f;
                        float distToLaunch = _sign > 0 ? launchX - centerX : centerX - launchX;
                        if (distToLaunch <= 16f)
                        {
                            DiagLog.Write($"[nav] move→jump handoff cx={centerX:0.##} launchX={launchX:0.##} diff={centerX - launchX:0.##}");
                            EmitNodeExit(_pathIdx, "move", "done", _target.Wx, _target.Wy, pcx, feetY);
                            _pathIdx++;
                            State = NavState.Idle;
                            return;
                        }
                    }

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
                    if (_target.Frames != null)
                    {
                        // phase 1: JumpCoordinator doing precision alignment
                        if (JumpCoordinator.IsActive) { _prevVY = p.velocity.Y; return; }

                        // phase 2: alignment done, load replay once
                        if (!ReplaySystem.IsActive && p.velocity.Y == 0f && !_jumpReplayLoaded)
                        {
                            _jumpReplayLoaded = true;
                            var replayFrames = new List<ReplayFrame>();
                            foreach (var fi in _target.Frames)
                                replayFrames.Add(new ReplayFrame { Jump = fi.Jump, Right = fi.Right, Left = fi.Left });
                            ReplaySystem.Load(replayFrames);
                            float alignedCx = p.position.X + p.width / 2f;
                            float srcX = _target.SourceWx * 16f + 8f;
                            DiagLog.Write($"[nav] jump replay start frames={replayFrames.Count} src=({_target.SourceWx},{_target.SourceWy}) target=({_target.Wx},{_target.Wy}) align_diff={alignedCx - srcX:0.##}");
                            _prevVY = p.velocity.Y;
                            return;
                        }

                        // phase 3: replay done, wait for landing
                        if (!ReplaySystem.IsActive && _jumpReplayLoaded && p.velocity.Y == 0f)
                        {
                            int landedCx = (int)((p.position.X + p.width / 2f) / 16);
                            DiagLog.Write($"[nav] jump replay landed px={p.position.X:0.##} cx={landedCx} target_cx={_target.Wx} delta={landedCx - _target.Wx}");
                            EmitNodeExit(_pathIdx, "jump", "done", _target.Wx, _target.Wy, pcx, feetY);
                            _jumpReplayLoaded = false;
                            _pathIdx++;
                            State = NavState.Idle;
                        }
                    }
                    else
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
                    if (SkillExecutor.IsActive) { _pillarSettleTick = 0; return; }
                    _pillarSettleTick++;
                    if (_pillarSettleTick >= 6)
                    {
                        EmitNodeExit(_pathIdx, "pillar", "done", _target.Wx, _target.Wy, pcx, feetY);
                        _pathIdx++;
                        _pillarSettleTick = 0;
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
                    if (n.Frames != null && n.Frames.Count > 0)
                    {
                        var startState = new PhysicsSimulator.State
                        {
                            Px = p.position.X, Py = p.position.Y,
                            Vx = p.velocity.X, Vy = 0f,
                            Grounded = true, JumpFramesLeft = n.Frames.Count,
                        };
                        int holdFrames = 0;
                        foreach (var fi in n.Frames) { if (fi.Jump) holdFrames++; else break; }
                        var sim = PhysicsSimulator.SimulateJump(startState, _sign, holdFrames);
                        var replayFrames = new List<ReplayFrame>();
                        var srcFrames = sim.Landed ? sim.Frames : n.Frames;
                        foreach (var fi in srcFrames)
                            replayFrames.Add(new ReplayFrame { Jump = fi.Jump, Right = fi.Right, Left = fi.Left });
                        DiagLog.Write($"[nav] jump resim(adv) landed={sim.Landed} frames={replayFrames.Count}");
                        _jumpReplayLoaded = true;
                        ReplaySystem.Load(replayFrames);
                    }
                    else
                    {
                        float launchX = p.position.X + p.width / 2f;
                        float targetX = n.Wx * 16f + 8f + (_sign > 0 ? 16f : -16f);
                        JumpCoordinator.Start(_sign > 0, launchX, targetX);
                    }
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
                if (feetY - streakEndY > PillarThresh) { DiagLog.Write($"[nav] AdvanceMove forced pillar feetY={feetY} streakEndY={streakEndY} rise={feetY - streakEndY}"); State = NavState.Idle; return; }
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
