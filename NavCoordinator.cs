using System;
using System.Collections.Generic;
using Terraria;
using Terraria.ModLoader;

namespace TerraBlind
{
    public enum NavState { Idle, Move, Fall, Jump, JumpAlign, Bridge, BridgeFall, PillarAlign, Pillar, MineAlign, Mine, PlatformWalk, Done, Failed }

    public struct NavNode
    {
        public int Wx, Wy;
        public int SourceWx, SourceWy;
        public string Action;
        public System.Collections.Generic.List<PhysicsSimulator.ControlInput> Frames;
        public System.Collections.Generic.List<(int wx, int wy)> MineTiles;
        public float? StartVx;
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
        private static int _invariantCheckCooldown;
        private static int _pillarAlignTick;
        private static int _actualWallFrames;
        private static int _actualCeilFrames;
        private static float _prevJumpVx;
        private static bool _pillarCollideX;
        private static float _prevPillarVx;
        private static int _pillarNudgeFrames;
        private static float _lastPlanMaxRun = -1f;
        private static int _vxWaitFrames = 0;
        private const int VxWaitMax = 20;
        private static int _jumpPlaceFrameIdx = -1;  // frame index to place platform during jump
        private static int _jumpPlaceTileX, _jumpPlaceTileY;
        private static int _jumpReplayFrameCount;

        // realtime jump control state
        private static bool _jumpAirborne;
        private static int _jumpHoldRemaining;
        private static int _jumpSign;
        private static int _jumpAlignTick;
        private const int JumpAlignMax = 120;
        private static int _fixedGoalWx = -1;
        private static int _fixedGoalWy = -1;

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
                _fixedGoalWx = -1;
                _fixedGoalWy = -1;
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
                _lastPlanMaxRun = -1f;
                FailReason = "";
                _started = true;
                DiagLog.Write($"[nav] Start sign={sign}");
            }
        }

        public static void StartTo(int goalWx, int goalWy)
        {
            lock (_lock)
            {
                var p = Main.LocalPlayer;
                _sign = p != null && goalWx >= (int)((p.position.X + p.width / 2f) / 16f) ? 1 : -1;
                _fixedGoalWx = goalWx;
                _fixedGoalWy = goalWy;
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
                _lastPlanMaxRun = -1f;
                FailReason = "";
                _started = true;
                DiagLog.Write($"[nav] StartTo ({goalWx},{goalWy})");
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
            return PathPlanner.IsFloorPublic(wx, wy);
        }

        private static int FeetY(Player p)
        {
            int fy = (int)((p.position.Y + p.height) / 16f);
            while (fy > 0 && PathPlanner.IsBlockPublic(Pcx(p), fy)) fy--;
            return fy;
        }

        private static bool Standable(int wx, int wy) => !PathPlanner.IsBlockPublic(wx, wy) && !PathPlanner.PlatformPublic(wx, wy) && !PathPlanner.IsHalfBrickPublic(wx, wy) && PathPlanner.IsFloorPublic(wx, wy + 1);

        // Returns pixel offset (-4, 0, or +4) to shift pillar align target away from side walls.
        // Checks columns cx-1 and cx+1 over the rise range; if only one side is blocked, shift away.
        private static float PillarXOffset(int cx, int feetY, int rise)
        {
            bool leftBlocked = false, rightBlocked = false;
            int checkTop = Math.Max(feetY - rise - 1, 0);
            for (int cy = feetY - 1; cy >= checkTop; cy--)
            {
                if (PathPlanner.IsBlockPublic(cx - 1, cy)) leftBlocked = true;
                if (PathPlanner.IsBlockPublic(cx + 1, cy)) rightBlocked = true;
            }
            if (leftBlocked && !rightBlocked) return 4f;
            if (rightBlocked && !leftBlocked) return -4f;
            return 0f;
        }

        // re-simulate jump from current player state, pick hold that lands closest to targetWx
        private static (List<ReplayFrame> frames, int simCx, int simCy, int hold) ResimJump(Player p, int sign, int targetWx, int fallbackHold, float? startVx = null)
        {
            var ph = PhysicsSimulator.Params.FromPlayer(p);
            var startState = new PhysicsSimulator.State
            {
                Px = p.position.X, Py = p.position.Y,
                Vx = startVx ?? p.velocity.X, Vy = 0f,
                Grounded = true,
            };
            PhysicsSimulator.SimResult best = default;
            int bestHold = fallbackHold;
            int bestDist = int.MaxValue;
            var holdOptions = p.wet && !p.honeyWet && !p.merman ? PathPlanner.HoldFrameOptionsWet : PathPlanner.HoldFrameOptions;
            foreach (int hold in holdOptions)
            {
                startState.JumpFramesLeft = hold;
                var sim = PhysicsSimulator.SimulateJump(startState, sign, hold, ph);
                if (!sim.Landed) continue;
                int dist = Math.Abs(sim.Cx - targetWx);
                if (dist < bestDist || (dist == bestDist && hold > bestHold)) { bestDist = dist; best = sim; bestHold = hold; }
            }
            // if (best.Frames != null && p.wet)
            // {
            //     var sb = new System.Text.StringBuilder();
            //     var dbgState = new PhysicsSimulator.State { Px = p.position.X, Py = p.position.Y, Vx = startVx ?? p.velocity.X, Vy = 0f, Grounded = true, JumpFramesLeft = bestHold };
            //     var ph2 = PhysicsSimulator.Params.FromPlayer(p);
            //     for (int fi = 0; fi < best.Frames.Count; fi++)
            //     {
            //         dbgState = PhysicsSimulator.Step(dbgState, best.Frames[fi], ph2);
            //         if (fi < 5 || fi % 10 == 0 || fi == best.Frames.Count - 1)
            //             sb.Append($" f{fi}:vx={dbgState.Vx:0.##},r={( best.Frames[fi].Right?1:0)}");
            //     }
            //     DiagLog.Write($"[jump] wet resim frames={best.Frames.Count}{sb}");
            // }
            var frames = new List<ReplayFrame>();
            if (best.Frames != null)
                foreach (var fi in best.Frames)
                    frames.Add(new ReplayFrame { Jump = fi.Jump, Right = fi.Right, Left = fi.Left });
            return (frames, best.Landed ? best.Cx : -1, best.Landed ? best.Cy : -1, bestHold);
        }

        private static void EmitNodeEnter(int idx, NavNode node, int pcx, int feetY, float vx, float vy)
        {
            int expEndWx = idx + 1 < _path.Count ? _path[idx + 1].Wx : node.Wx;
            int expEndWy = idx + 1 < _path.Count ? _path[idx + 1].Wy : node.Wy;
            string mode = node.Action == "jump" ? (node.Frames != null ? "replay" : "align") : "plain";
            DiagLog.WriteEvent(
                $"{{\"e\":\"node_enter\",\"tick\":{Main.GameUpdateCount},\"node_idx\":{idx}" +
                $",\"action\":\"{node.Action}\"" +
                $",\"exp_start_wx\":{node.Wx},\"exp_start_wy\":{node.Wy}" +
                $",\"exp_end_wx\":{expEndWx},\"exp_end_wy\":{expEndWy}" +
                $",\"actual_px\":{pcx},\"actual_py\":{feetY}" +
                $",\"vx\":{vx.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}" +
                $",\"vy\":{vy.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}" +
                $",\"vx_bucket\":{BehaviorContract.VxBucket(vx)}" +
                $",\"grounded\":{(vy == 0f ? "true" : "false")}" +
                $",\"mode\":\"{mode}\"}}");
            _nodeEnterTick = Main.GameUpdateCount;
        }

        private static void EmitNodeExit(int idx, string action, string status, int expWx, int expWy, int actualWx, int actualWy)
        {
            int dx = Math.Abs(actualWx - expWx);
            int dy = Math.Abs(actualWy - expWy);
            var p = Main.LocalPlayer;
            float vxEnd = p?.velocity.X ?? 0f;
            float vyEnd = p?.velocity.Y ?? 0f;
            DiagLog.WriteEvent(
                $"{{\"e\":\"node_exit\",\"tick\":{Main.GameUpdateCount},\"node_idx\":{idx}" +
                $",\"action\":\"{action}\",\"status\":\"{status}\"" +
                $",\"exp_end_wx\":{expWx},\"exp_end_wy\":{expWy}" +
                $",\"actual_end_wx\":{actualWx},\"actual_end_wy\":{actualWy}" +
                $",\"delta_x\":{actualWx - expWx},\"delta_y\":{actualWy - expWy}" +
                $",\"duration_ticks\":{(int)(Main.GameUpdateCount - _nodeEnterTick)}" +
                $",\"vx_end\":{vxEnd.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}" +
                $",\"vy_end\":{vyEnd.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}" +
                $",\"vx_bucket_end\":{BehaviorContract.VxBucket(vxEnd)}" +
                $",\"grounded_end\":{(vyEnd == 0f ? "true" : "false")}}}");
            bool deviated = BehaviorContract.Deviated(action, actualWx - expWx, actualWy - expWy);
            if (deviated)
                DiagLog.WriteEvent(
                    $"{{\"e\":\"nav_failed\",\"tick\":{Main.GameUpdateCount}" +
                    $",\"reason\":\"deviate\",\"last_node_idx\":{idx},\"last_action\":\"{action}\"" +
                    $",\"px\":{actualWx},\"py\":{actualWy},\"dx\":{dx},\"dy\":{dy},\"stall_count\":-1}}");
        }

        private static void EmitPreconditionCheck(
            int idx, string fromAction, string toAction,
            bool passed, string reason, string severity,
            int pcx, int feetY, float vx, float vy, bool grounded, float distToLaunchPx)
        {
            DiagLog.WriteEvent(
                $"{{\"e\":\"precondition_check\",\"tick\":{Main.GameUpdateCount}" +
                $",\"node_idx\":{idx},\"from_action\":\"{fromAction}\",\"to_action\":\"{toAction}\"" +
                $",\"passed\":{(passed ? "true" : "false")}" +
                $",\"reason\":\"{reason ?? ""}\",\"severity\":\"{severity ?? ""}\"" +
                $",\"px\":{pcx},\"py\":{feetY}" +
                $",\"vx\":{vx.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}" +
                $",\"vy\":{vy.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}" +
                $",\"vx_bucket\":{BehaviorContract.VxBucket(vx)}" +
                $",\"grounded\":{(grounded ? "true" : "false")}" +
                $",\"dist_to_launch_px\":{distToLaunchPx.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}}}");
        }

        private static void EmitInvariantViolation(int idx, string action, string axis, int expected, int actual, int pcx, int feetY)
        {
            DiagLog.WriteEvent(
                $"{{\"e\":\"runtime_invariant_violation\",\"tick\":{Main.GameUpdateCount}" +
                $",\"node_idx\":{idx},\"action\":\"{action}\",\"axis\":\"{axis}\"" +
                $",\"expected\":{expected},\"actual\":{actual},\"delta\":{actual - expected}" +
                $",\"px\":{pcx},\"py\":{feetY},\"action_taken\":\"replan\"}}");
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
            if (p.velocity.Y != 0f)
            {
                _restartCooldown = 6;
                State = NavState.Idle;
                _path.Clear();
                _pathIdx = 0;
                return;
            }
            int pcx = Pcx(p);
            int feetY = FeetY(p);
            PurgeBlacklist();
            DiagLog.Write($"[nav] Replan at ({pcx},{feetY}) state={State} blacklist={_blacklist.Count}");
            _lastPlanMaxRun = PhysicsSimulator.Params.FromPlayer(p).MaxRun;
            string json;
            if (_fixedGoalWx >= 0)
            {
                if (Math.Abs(pcx - _fixedGoalWx) <= 2 && Math.Abs(feetY - _fixedGoalWy) <= 2)
                {
                    DiagLog.Write($"[nav] reached fixed goal ({_fixedGoalWx},{_fixedGoalWy})");
                    State = NavState.Done;
                    return;
                }
                json = PathPlanner.PlanTo(_fixedGoalWx, _fixedGoalWy);
            }
            else
            {
                json = PathPlanner.Plan(_sign, BlacklistSet());
            }
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
            int down = last.Wy - feetY;
            bool hasMine = newPath.Exists(n => n.Action.StartsWith("mine_"));
            bool hasProgress = _fixedGoalWx >= 0 || fwd > 0 || down > 0 || hasMine;
            if (!hasProgress)
            {
                BlacklistGoal();
                EmitNavFailed("replan_no_progress", _pathIdx, _target.Action ?? "", pcx, feetY);
                DiagLog.Write($"[nav] Replan no-progress fwd={fwd} down={down}, retrying");
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
                if (node.Action == "platform_walk" || node.Action == "jump_bridge")
                {
                    var ptRe2 = new System.Text.RegularExpressions.Regex("\\[\\s*(-?\\d+)\\s*,\\s*(-?\\d+)\\s*\\]");
                    int ptStart2 = nodeJson.IndexOf("\"place_tiles\"");
                    if (ptStart2 >= 0)
                    {
                        int ptArrStart2 = nodeJson.IndexOf('[', ptStart2);
                        int ptDepth2 = 0, ptEnd2 = ptArrStart2;
                        for (int mi = ptArrStart2; mi < nodeJson.Length; mi++)
                        {
                            if (nodeJson[mi] == '[') ptDepth2++;
                            else if (nodeJson[mi] == ']') { ptDepth2--; if (ptDepth2 == 0) { ptEnd2 = mi + 1; break; } }
                        }
                        string ptSeg2 = nodeJson.Substring(ptArrStart2, ptEnd2 - ptArrStart2);
                        var ptList2 = new List<(int, int)>();
                        foreach (System.Text.RegularExpressions.Match mm in ptRe2.Matches(ptSeg2))
                            ptList2.Add((int.Parse(mm.Groups[1].Value), int.Parse(mm.Groups[2].Value)));
                        node.MineTiles = ptList2;
                    }
                }
                if (node.Action.StartsWith("mine_"))
                {
                    var mtRe = new System.Text.RegularExpressions.Regex("\\[\\s*(-?\\d+)\\s*,\\s*(-?\\d+)\\s*\\]");
                    int mtStart = nodeJson.IndexOf("\"mine_tiles\"");
                    if (mtStart >= 0)
                    {
                        int mtArrStart = nodeJson.IndexOf('[', mtStart);
                        int mtDepth = 0, mtEnd = mtArrStart;
                        for (int mi = mtArrStart; mi < nodeJson.Length; mi++)
                        {
                            if (nodeJson[mi] == '[') mtDepth++;
                            else if (nodeJson[mi] == ']') { mtDepth--; if (mtDepth == 0) { mtEnd = mi + 1; break; } }
                        }
                        string mtSeg = nodeJson.Substring(mtArrStart, mtEnd - mtArrStart);
                        var mtList = new List<(int, int)>();
                        foreach (System.Text.RegularExpressions.Match mm in mtRe.Matches(mtSeg))
                            mtList.Add((int.Parse(mm.Groups[1].Value), int.Parse(mm.Groups[2].Value)));
                        node.MineTiles = mtList;
                    }
                }
                if (node.Action == "jump" || node.Action == "jump_bridge" || node.Action == "pillar")
                {
                    var sm = sourceRe.Match(nodeJson);
                    if (sm.Success)
                    {
                        node.SourceWx = int.Parse(sm.Groups[1].Value);
                        node.SourceWy = int.Parse(sm.Groups[2].Value);
                    }
                    var svxm = System.Text.RegularExpressions.Regex.Match(nodeJson, "\"start_vx\"\\s*:\\s*(-?[0-9.]+)");
                    if (svxm.Success) node.StartVx = float.Parse(svxm.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    else node.StartVx = null;
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
                if (node.Action == "jump_bridge")
                {
                    var ptRe = new System.Text.RegularExpressions.Regex("\\[\\s*(-?\\d+)\\s*,\\s*(-?\\d+)\\s*\\]");
                    int ptStart = nodeJson.IndexOf("\"place_tiles\"");
                    if (ptStart >= 0)
                    {
                        int ptArrStart = nodeJson.IndexOf('[', ptStart);
                        int ptDepth = 0, ptEnd = ptArrStart;
                        for (int mi = ptArrStart; mi < nodeJson.Length; mi++)
                        {
                            if (nodeJson[mi] == '[') ptDepth++;
                            else if (nodeJson[mi] == ']') { ptDepth--; if (ptDepth == 0) { ptEnd = mi + 1; break; } }
                        }
                        string ptSeg = nodeJson.Substring(ptArrStart, ptEnd - ptArrStart);
                        var ptList = new List<(int, int)>();
                        foreach (System.Text.RegularExpressions.Match mm in ptRe.Matches(ptSeg))
                            ptList.Add((int.Parse(mm.Groups[1].Value), int.Parse(mm.Groups[2].Value)));
                        node.MineTiles = ptList;
                    }
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

                bool stalledX = State != NavState.Idle && State != NavState.Pillar && State != NavState.PillarAlign && State != NavState.Jump && State != NavState.JumpAlign && State != NavState.Mine && State != NavState.MineAlign && State != NavState.PlatformWalk && pcx == _lastStallPcx;
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

                {
                    float curMaxRun = PhysicsSimulator.Params.FromPlayer(p).MaxRun;
                    if (_lastPlanMaxRun < 0f)
                    {
                        _lastPlanMaxRun = curMaxRun;
                        // DiagLog.Write($"[nav] maxrun init maxRunSpeed={p.maxRunSpeed:0.##} accRunSpeed={p.accRunSpeed:0.##}");
                    }
                    else if (Math.Abs(curMaxRun - _lastPlanMaxRun) > 0.05f && p.velocity.Y == 0f)
                    {
                        DiagLog.Write($"[nav] maxrun changed {_lastPlanMaxRun:0.##}→{curMaxRun:0.##} → replan");
                        _lastPlanMaxRun = curMaxRun;
                        _path.Clear();
                        _pathIdx = 0;
                        State = NavState.Idle;
                        JumpCoordinator.Stop();
                        ReplaySystem.Stop();
                    }
                }

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

                    {
                        string fromAction = _pathIdx > 0 ? _path[_pathIdx - 1].Action : "start";
                        string toAction = _target.Action;
                        float launchX = 0f;
                        bool checkDist = false;
                        if (toAction == "jump" && _target.SourceWx != 0)
                        {
                            launchX = _target.SourceWx * 16f + 8f;
                            checkDist = true;
                        }
                        else if (toAction == "pillar")
                        {
                            launchX = _target.Wx * 16f + 8f;
                            checkDist = true;
                        }
                        float distToLaunch = checkDist ? (centerX - launchX) : 0f;
                        bool grounded = p.velocity.Y == 0f;
                        int vxBucket = BehaviorContract.VxBucket(p.velocity.X);
                        var (pcPassed, pcReason, pcSeverity) = BehaviorContract.CheckPrecondition(
                            toAction, grounded, checkDist ? distToLaunch : 0f, vxBucket);
                        EmitPreconditionCheck(_pathIdx, fromAction, toAction, pcPassed, pcReason, pcSeverity,
                            pcx, feetY, p.velocity.X, p.velocity.Y, grounded, checkDist ? distToLaunch : 0f);
                        if (!pcPassed && pcSeverity == "abort")
                        {
                            DiagLog.Write($"[nav] precondition abort: {pcReason} → replan");
                            EmitNavFailed("precondition_abort", _pathIdx, toAction, pcx, feetY);
                            Replan(p);
                            return;
                        }
                    }

                    _vxWaitFrames = 0;
                    if (_target.Action == "jump" || _target.Action == "jump_bridge")
                    {
                        if (p.velocity.Y > 1f)
                        {
                            DiagLog.Write($"[nav] jump aborted vy={p.velocity.Y:0.#} feetY={feetY} → replan");
                            EmitNavFailed("jump_abort", _pathIdx, "jump", pcx, feetY);
                            Replan(p);
                            return;
                        }
                        _jumpSign = _target.Wx >= pcx ? 1 : -1;
                        _jumpAirborne = false;
                        _jumpHoldRemaining = 0;
                        _jumpAlignTick = 0;
                        _jumpReplayLoaded = false;
                        DiagLog.Write($"[nav] jump enter realtime target=({_target.Wx},{_target.Wy}) sign={_jumpSign} vx={p.velocity.X:0.##} startVx={_target.StartVx:0.##}");
                        // zero-speed jump: must align to SourceWx first
                        if (_target.StartVx.HasValue && Math.Abs(_target.StartVx.Value) < 0.5f)
                        {
                            DiagLog.Write($"[nav] jump needs align (startVx≈0) → JumpAlign");
                            _pillarAlignTick = 0;
                            State = NavState.JumpAlign;
                        }
                        else
                        {
                            State = NavState.Jump;
                        }
                    }
                    else if (_target.Action == "bridge")
                    {
                        State = NavState.Bridge;
                    }
                    else if (_target.Action == "platform_walk")
                    {
                        State = NavState.PlatformWalk;
                        _invariantCheckCooldown = 10;
                    }
                    else if (_target.Action == "pillar")
                    {
                        State = NavState.PillarAlign;
                        _pillarAlignTick = 0;
                    }
                    else if (_target.Action == "fall")
                    {
                        State = NavState.Fall;
                    }
                    else if (_target.Action.StartsWith("mine_"))
                    {
                        if (_target.Action == "mine_down")
                        {
                            State = NavState.MineAlign;
                            _pillarAlignTick = 0;
                        }
                        else
                        {
                            State = NavState.Mine;
                        }
                        _invariantCheckCooldown = 0;
                    }
                    else
                    {
                        if (feetY - _target.Wy > PillarThresh)
                        {
                            DiagLog.Write($"[nav] forced pillar feetY={feetY} targetWy={_target.Wy} rise={feetY - _target.Wy}");
                            State = NavState.PillarAlign;
                            _pillarAlignTick = 0;
                        }
                        else
                        {
                            State = NavState.Move;
                            _invariantCheckCooldown = 10;
                        }
                    }
                    return;
                }

                if (State == NavState.Move)
                {
                    int moveSign = _target.Wx >= pcx ? 1 : -1;

                    // if next node is a replay jump, hand off early when within 16px of source tile
                    int nextIdx = _pathIdx + 1;
                    if (nextIdx < _path.Count && _path[nextIdx].Action == "jump" && _path[nextIdx].Frames != null)
                    {
                        float launchX = _path[nextIdx].SourceWx * 16f + 8f;
                        float distToLaunch = moveSign > 0 ? launchX - centerX : centerX - launchX;
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
                    bool arrived;
                    if (_target.Action == "pillar")
                        arrived = pcx == _target.Wx;
                    else
                        arrived = feetLeft <= _target.Wx && _target.Wx <= feetRight;
                    if (arrived)
                    {
                        EmitNodeExit(_pathIdx, "move", "done", _target.Wx, _target.Wy, pcx, feetY);
                        _pathIdx++;
                        AdvanceMoveNodes(p, pcx, feetY);
                        return;
                    }
                    _invariantCheckCooldown--;
                    if (_invariantCheckCooldown <= 0)
                    {
                        _invariantCheckCooldown = BehaviorContract.RuntimeInvariant["move"].CheckEveryNFrames;
                        int dyInv = feetY - _target.Wy;
                        if (Math.Abs(dyInv) > BehaviorContract.RuntimeInvariant["move"].MaxDyDuringExec)
                        {
                            EmitInvariantViolation(_pathIdx, "move", "y", _target.Wy, feetY, pcx, feetY);
                            Replan(p);
                            return;
                        }
                    }
                    if (moveSign > 0) p.controlRight = true;
                    else p.controlLeft = true;
                    return;
                }

                if (State == NavState.Fall)
                {
                    if (_sign > 0) p.controlRight = true;
                    else p.controlLeft = true;
                    p.controlDown = true;
                    bool onGround = p.velocity.Y == 0f;
                    bool landed = (_prevVY >= 0f && onGround) || (onGround && _pathIdx == 0);
                    _prevVY = p.velocity.Y;
                    if (landed)
                    {
                        int lastFallIdx = _pathIdx;
                        while (lastFallIdx + 1 < _path.Count && _path[lastFallIdx + 1].Action == "fall")
                            lastFallIdx++;
                        EmitNodeExit(lastFallIdx, "fall", "done", _path[lastFallIdx].Wx, _path[lastFallIdx].Wy, pcx, feetY);
                        _pathIdx = lastFallIdx + 1;
                        State = NavState.Idle;
                    }
                    return;
                }

                if (State == NavState.Jump)
                {
                    bool grounded = p.velocity.Y == 0f;

                    if (!_jumpAirborne)
                    {
                        // grounded phase: try to find a good moment to jump
                        _jumpAlignTick++;
                        if (_jumpAlignTick > JumpAlignMax)
                        {
                            DiagLog.Write($"[nav] jump align timeout → replan");
                            EmitNavFailed("jump_align_timeout", _pathIdx, "jump", pcx, feetY);
                            Replan(p);
                            return;
                        }

                        if (!grounded)
                        {
                            // fell off edge while aligning, just hold direction
                            if (_jumpSign > 0) p.controlRight = true;
                            else p.controlLeft = true;
                            _prevVY = p.velocity.Y;
                            return;
                        }

                        // try each hold option, pick best landing
                        var ph = PhysicsSimulator.Params.FromPlayer(p);
                        // show target tile in green
                        PathVisSystem.SetTiles(new System.Collections.Generic.List<(int, int, Microsoft.Xna.Framework.Color)>
                        {
                            (_target.Wx, _target.Wy, new Microsoft.Xna.Framework.Color(0, 255, 80, 180))
                        }, ttlFrames: 4);

                        var holdOptions = p.wet && !p.honeyWet && !p.merman ? PathPlanner.HoldFrameOptionsWet : PathPlanner.HoldFrameOptions;
                        int bestHold = 0;
                        int bestDist = int.MaxValue;
                        int bestSimCx = -1, bestSimCy = -1;
                        foreach (int hold in holdOptions)
                        {
                            // velocity is read before Player.Update accelerates, compensate one frame
                            float simVx = p.velocity.X + _jumpSign * ph.AccRun;
                            if (simVx > ph.AccRunSpeed) simVx = ph.AccRunSpeed;
                            if (simVx < -ph.AccRunSpeed) simVx = -ph.AccRunSpeed;
                            var startState = new PhysicsSimulator.State
                            {
                                Px = p.position.X, Py = p.position.Y,
                                Vx = simVx, Vy = 0f,
                                Grounded = true, JumpFramesLeft = hold,
                            };
                            var sim = PhysicsSimulator.SimulateJump(startState, _jumpSign, hold, ph);
                            if (!sim.Landed) continue;
                            int dist = Math.Abs(sim.Cx - _target.Wx);
                            if (dist < bestDist || (dist == bestDist && hold > bestHold))
                            {
                                bestDist = dist;
                                bestHold = hold;
                                bestSimCx = sim.Cx; bestSimCy = sim.Cy;
                            }
                        }

                        // show predicted landing in yellow, target in green
                        {
                            var visTiles = new System.Collections.Generic.List<(int, int, Microsoft.Xna.Framework.Color)>
                            {
                                (_target.Wx, _target.Wy, new Microsoft.Xna.Framework.Color(0, 255, 80, 180)),
                            };
                            if (bestSimCx >= 0)
                                visTiles.Add((bestSimCx, bestSimCy, new Microsoft.Xna.Framework.Color(255, 220, 0, 200)));
                            PathVisSystem.SetTiles(visTiles, ttlFrames: 4);
                        }

                        if (bestHold == 0 || bestDist > 3)
                        {
                            // move to reduce simDist: if sim lands short, move toward target; if overshoots, move back
                            int adjustSign;
                            if (bestSimCx < 0)
                                adjustSign = _jumpSign; // no valid sim, just move toward target
                            else
                                adjustSign = (bestSimCx < _target.Wx) ? 1 : -1;
                            if (adjustSign > 0) p.controlRight = true;
                            else p.controlLeft = true;
                            _prevVY = p.velocity.Y;
                            return;
                        }

                        // good to jump now
                        DiagLog.Write($"[nav] jump fire vx={p.velocity.X:0.##} hold={bestHold} simDist={bestDist} target=({_target.Wx},{_target.Wy}) js={Player.jumpSpeed:0.###} grav={p.gravity:0.###} maxRun={p.maxRunSpeed:0.###}");
                        _jumpAirborne = true;
                        _jumpHoldRemaining = bestHold;
                        p.controlJump = true;
                        if (_jumpSign > 0) p.controlRight = true;
                        else p.controlLeft = true;
                        _jumpHoldRemaining--;
                    }
                    else
                    {
                        // airborne phase
                        if (_jumpHoldRemaining > 0)
                        {
                            p.controlJump = true;
                            _jumpHoldRemaining--;
                        }
                        if (_jumpSign > 0) p.controlRight = true;
                        else p.controlLeft = true;

                        // landing detection
                        if (_prevVY > 0f && grounded)
                        {
                            DiagLog.Write($"[verify] edge_actual type=jump from=({_target.SourceWx},{_target.SourceWy}) to=({_target.Wx},{_target.Wy}) actual_landing=({pcx},{feetY}) tick={Main.GameUpdateCount}");
                            EmitNodeExit(_pathIdx, _target.Action, "done", _target.Wx, _target.Wy, pcx, feetY);
                            if (BehaviorContract.Deviated("jump", pcx - _target.Wx, feetY - _target.Wy))
                            {
                                DiagLog.Write($"[nav] jump deviated landing=({pcx},{feetY}) target=({_target.Wx},{_target.Wy}) → blacklist+replan");
                                BlacklistNode(_target.Wx, _target.Wy);
                                Replan(p);
                            }
                            else
                            {
                                _pathIdx++;
                                State = NavState.Idle;
                            }
                        }
                    }

                    _prevVY = p.velocity.Y;
                    return;
                }

                if (State == NavState.JumpAlign)
                {
                    _pillarAlignTick++;
                    if (_pillarAlignTick > 120)
                    {
                        DiagLog.Write($"[nav] JumpAlign timeout vx={p.velocity.X:0.##} → replan");
                        EmitNavFailed("jump_align_timeout", _pathIdx, "jump", pcx, feetY);
                        Replan(p);
                        return;
                    }
                    int jumpSign = _target.Wx >= pcx ? 1 : -1;
                    float targetCenterX = _target.SourceWx * 16f + 8f;
                    bool aligned = Math.Abs(centerX - targetCenterX) <= 8f && Math.Abs(p.velocity.X) < 0.3f;
                    if (aligned)
                    {
                        DiagLog.Write($"[nav] JumpAlign done vx={p.velocity.X:0.##} cx={centerX:0.#} → realtime jump");
                        _jumpAirborne = false;
                        _jumpHoldRemaining = 0;
                        _jumpAlignTick = 0;
                        State = NavState.Jump;
                        return;
                    }
                    float vx = p.velocity.X;
                    float stopDist = vx != 0f ? (vx * vx / 0.4f) * Math.Sign(vx) : 0f;
                    float predictedStopX = centerX + stopDist;
                    float diff = targetCenterX - predictedStopX;
                    if (Math.Abs(diff) <= 8f) return;
                    if (diff > 0) p.controlRight = true;
                    else p.controlLeft = true;
                    return;
                }

                if (State == NavState.PlatformWalk)
                {
                    int pwSign = _target.Wx >= pcx ? 1 : -1;
                    int feetLeft = (int)(p.position.X / 16);
                    int feetRight = (int)((p.position.X + p.width - 1) / 16);
                    bool arrived = feetLeft <= _target.Wx && _target.Wx <= feetRight;
                    if (arrived)
                    {
                        EmitNodeExit(_pathIdx, "platform_walk", "done", _target.Wx, _target.Wy, pcx, feetY);
                        _pathIdx++;
                        State = NavState.Idle;
                        return;
                    }
                    // place platform under feet if needed
                    if (_target.MineTiles != null && p.itemTime == 0)
                    {
                        int slot = FindPlatformSlot(p);
                        if (slot >= 0)
                        {
                            foreach (var (ptx, pty) in _target.MineTiles)
                            {
                                // place 1 tile ahead so player doesn't fall before placing
                                if (ptx == pcx + pwSign && pty == feetY + 1)
                                {
                                    p.selectedItem = slot;
                                    p.controlUseItem = true;
                                    Main.SmartCursorWanted_Mouse = false;
                                    Main.mouseX = (int)(ptx * 16f + 8f - Main.screenPosition.X);
                                    Main.mouseY = (int)(pty * 16f + 8f - Main.screenPosition.Y);
                                    DiagLog.Write($"[nav] platform_walk place ({ptx},{pty}) player=({pcx},{feetY})");
                                    break;
                                }
                            }
                        }
                    }
                    if (pwSign > 0) p.controlRight = true;
                    else p.controlLeft = true;
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
                    if (PathPlanner.IsBlockPublic(aheadX, feetY) || PathPlanner.IsBlockPublic(aheadX, feetY - 1) || PathPlanner.IsBlockPublic(aheadX, feetY - 2))
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

                if (State == NavState.PillarAlign)
                {
                    _pillarAlignTick++;
                    if (_pillarAlignTick > 120)
                    {
                        DiagLog.Write($"[nav] pillar align timeout pcx={pcx} target={_target.Wx} → replan");
                        EmitNavFailed("pillar_align_timeout", _pathIdx, "pillar", pcx, feetY);
                        Replan(p);
                        return;
                    }
                    float vx = p.velocity.X;
                    float targetCenterX = _target.Wx * 16f + 8f + PillarXOffset(_target.Wx, feetY, feetY - _target.Wy);
                    bool arrived = Math.Abs(centerX - targetCenterX) <= 8f && Math.Abs(vx) < 0.3f;
                    if (arrived)
                    {
                        int rise = feetY - _target.Wy;
                        DiagLog.Write($"[nav] pillar align done pcx={pcx} cx={centerX:0.#} target={targetCenterX:0.#} rise={rise}");
                        State = NavState.Pillar;
                        _invariantCheckCooldown = 10;
                        _pillarCollideX = false;
                        _prevPillarVx = p.velocity.X;
                        _pillarNudgeFrames = 0;
                        SkillExecutor.StartPillarJump(_sign > 0, _target.Wy);
                        return;
                    }
                    float stopDist = vx != 0f ? (vx * vx / 0.4f) * Math.Sign(vx) : 0f;
                    float predictedStopX = centerX + stopDist;
                    float diff = targetCenterX - predictedStopX;
                    if (Math.Abs(diff) <= 8f)
                        return; // coast, predicted stop is close enough
                    if (diff > 0) p.controlRight = true;
                    else p.controlLeft = true;
                    return;
                }

                if (State == NavState.Pillar)
                {
                    if (SkillExecutor.IsActive)
                    {
                        if (Math.Abs(p.velocity.X) < Math.Abs(_prevPillarVx) - 0.05f) _pillarCollideX = true;
                        _prevPillarVx = p.velocity.X;
                        _pillarSettleTick = 0;
                        float pillarLeft = (_target.Wx - 1) * 16f;
                        float pillarRight = (_target.Wx + 1) * 16f;
                        float playerLeft = p.position.X;
                        float playerRight = p.position.X + p.width;
                        if (_pillarNudgeFrames == 0)
                        {
                            if (playerLeft < pillarLeft) _pillarNudgeFrames = 3;
                            else if (playerRight > pillarRight) _pillarNudgeFrames = -3;
                        }
                        if (_pillarNudgeFrames > 0) { p.controlRight = true; _pillarNudgeFrames--; }
                        else if (_pillarNudgeFrames < 0) { p.controlLeft = true; _pillarNudgeFrames++; }
                        return;
                    }
                    _invariantCheckCooldown--;
                    if (_invariantCheckCooldown <= 0)
                    {
                        _invariantCheckCooldown = BehaviorContract.RuntimeInvariant["pillar"].CheckEveryNFrames;
                        int dxInv = Math.Abs(pcx - _target.Wx);
                        if (dxInv > BehaviorContract.RuntimeInvariant["pillar"].MaxDxDuringExec)
                        {
                            EmitInvariantViolation(_pathIdx, "pillar", "x", _target.Wx, pcx, pcx, feetY);
                            Replan(p);
                            return;
                        }
                    }
                    _pillarSettleTick++;
                    if (_pillarSettleTick >= 6)
                    {
                        _pillarSettleTick = 0;
                        bool reachedHeight = feetY <= _target.Wy + 1;
                        DiagLog.Write($"[verify] edge_actual type=pillar from=({_target.SourceWx},{_target.SourceWy}) to=({_target.Wx},{_target.Wy}) actual_landing=({pcx},{feetY}) pillar_collide_x={_pillarCollideX} reached_height={reachedHeight} tick={Main.GameUpdateCount}");
                        _pillarCollideX = false;
                        if (reachedHeight)
                        {
                            EmitNodeExit(_pathIdx, "pillar", "done", _target.Wx, _target.Wy, pcx, feetY);
                            _pathIdx++;
                            State = NavState.Idle;
                        }
                        else
                        {
                            DiagLog.Write($"[nav] pillar height fail feetY={feetY} targetWy={_target.Wy} delta={feetY - _target.Wy} → blacklist+replan");
                            BlacklistNode(_target.Wx, _target.Wy);
                            EmitNavFailed("pillar_height_fail", _pathIdx, "pillar", pcx, feetY);
                            Replan(p);
                        }
                    }
                    return;
                }

                if (State == NavState.MineAlign)
                {
                    _pillarAlignTick++;
                    if (_pillarAlignTick > 120)
                    {
                        DiagLog.Write($"[nav] mine align timeout → replan");
                        Replan(p);
                        return;
                    }
                    float targetCenterX = _target.Wx * 16f + 10f;
                    bool aligned = Math.Abs(centerX - targetCenterX) <= 8f && Math.Abs(p.velocity.X) < 0.3f;
                    if (aligned)
                    {
                        State = NavState.Mine;
                        return;
                    }
                    float diff = targetCenterX - centerX;
                    float stopDist = p.velocity.X != 0f ? (p.velocity.X * p.velocity.X / 0.4f) * Math.Sign(p.velocity.X) : 0f;
                    if (Math.Abs(targetCenterX - (centerX + stopDist)) <= 8f) return;
                    if (diff > 0) p.controlRight = true;
                    else p.controlLeft = true;
                    return;
                }

                if (State == NavState.Mine)
                {
                    int slot = -1;
                    for (int si = 0; si < 10; si++) { var it = p.inventory[si]; if (it != null && !it.IsAir && it.pick > 0) { slot = si; break; } }
                    if (slot < 0) { State = NavState.Failed; FailReason = "no pickaxe"; return; }

                    string mineAction = _target.Action;
                    bool mineRight = mineAction == "mine_right";
                    bool mineLeft  = mineAction == "mine_left";
                    bool mineDown  = mineAction == "mine_down";

                    Main.SmartCursorWanted_Mouse = true;
                    p.selectedItem = slot;
                    if (p.itemTime == 0) p.controlUseItem = true;

                    float midY = p.position.Y + p.height / 2f;
                    bool mineUp = mineAction == "mine_up";
                    if (mineRight)
                    {
                        p.controlRight = true;
                        Main.mouseX = (int)(centerX + 160f - Main.screenPosition.X);
                        Main.mouseY = (int)(midY - Main.screenPosition.Y);
                    }
                    else if (mineLeft)
                    {
                        p.controlLeft = true;
                        Main.mouseX = (int)(centerX - 160f - Main.screenPosition.X);
                        Main.mouseY = (int)(midY - Main.screenPosition.Y);
                    }
                    else if (mineDown) { Main.mouseX = (int)(centerX - Main.screenPosition.X); Main.mouseY = (int)(p.position.Y + p.height + 160f - Main.screenPosition.Y); }
                    else if (mineUp) { Main.mouseX = (int)(centerX - Main.screenPosition.X); Main.mouseY = (int)(p.position.Y - 160f - Main.screenPosition.Y); }

                    bool done = false;
                    if (mineDown)
                        done = feetY >= _target.Wy && p.velocity.Y == 0f;
                    else if (mineRight)
                        done = pcx >= _target.Wx;
                    else if (mineLeft)
                        done = pcx <= _target.Wx;
                    else if (mineUp)
                        done = !PathPlanner.IsBlockPublic(pcx, feetY - 2) && !PathPlanner.IsBlockPublic(pcx, feetY - 3)
                            && !PathPlanner.IsBlockPublic(pcx + 1, feetY - 2) && !PathPlanner.IsBlockPublic(pcx + 1, feetY - 3);

                    if (done)
                    {
                        Main.SmartCursorWanted_Mouse = false;
                        EmitNodeExit(_pathIdx, mineAction, "done", _target.Wx, _target.Wy, pcx, feetY);
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
                if (n.Action == "jump" || n.Action == "jump_bridge")
                {
                    _target = n;
                    EmitNodeEnter(_pathIdx, _target, pcx, feetY, p.velocity.X, p.velocity.Y);
                    _jumpSign = n.Wx >= pcx ? 1 : -1;
                    _jumpAirborne = false;
                    _jumpHoldRemaining = 0;
                    _jumpAlignTick = 0;
                    _jumpReplayLoaded = false;
                    if (n.StartVx.HasValue && Math.Abs(n.StartVx.Value) < 0.5f)
                    {
                        _pillarAlignTick = 0;
                        State = NavState.JumpAlign;
                    }
                    else
                    {
                        State = NavState.Jump;
                    }
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
                if (feetY - n.Wy > PillarThresh) { DiagLog.Write($"[nav] AdvanceMove forced pillar feetY={feetY} targetWy={n.Wy} rise={feetY - n.Wy}"); State = NavState.Idle; return; }
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

        // find frame index during descent where player x-center first reaches target landing tile
        private static void ComputeJumpPlaceFrame(NavNode target, int holdFrames, int playerW, int playerH)
        {
            _jumpPlaceFrameIdx = -1;
            var frames = target.Frames;
            if (frames == null || frames.Count == 0) return;
            int targetTileX = target.Wx;
            int sign = target.Wx >= target.SourceWx ? 1 : -1;
            for (int i = holdFrames; i < frames.Count; i++)
            {
                float cx = (frames[i].Px + playerW / 2f) / 16f;
                int tileCx = (int)cx;
                if (sign > 0 ? tileCx >= targetTileX : tileCx <= targetTileX)
                {
                    _jumpPlaceFrameIdx = i;
                    _jumpPlaceTileX = targetTileX;
                    _jumpPlaceTileY = target.Wy + 1;
                    DiagLog.Write($"[nav] jump_place frame={i} tile=({_jumpPlaceTileX},{_jumpPlaceTileY}) total_frames={frames.Count}");
                    return;
                }
            }
        }
    }
}
