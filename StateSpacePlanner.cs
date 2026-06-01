using System;
using System.Collections.Generic;
using Terraria;

namespace TerraBlind
{
    // State-space physics search: node = real physics state, expansion = enumerate inputs × simulate.
    // Standalone prototype; does not touch the grid A*.
    public static class StateSpacePlanner
    {
        const float PxQuant = 4f;
        const float VxQuant = 0.5f;
        const int   MaxExpansions = 20000;
        const int   MaxSegFrames = 120;
        const int   HoldStep = 2;

        // ASSUMPTION: Player.jumpHeight is the hold-frame cap and accessories raise it.
        // Reading it (vs hardcoding 15) keeps planning correct as gear changes.
        static int[] BuildHoldOptions()
        {
            int maxHold = Player.jumpHeight > 0 ? Player.jumpHeight : 15;
            var opts = new List<int> { 0 };
            for (int h = HoldStep; h < maxHold; h += HoldStep) opts.Add(h);
            opts.Add(maxHold);
            return opts.ToArray();
        }

        public struct SSNode
        {
            public float Px, Py, Vx, Vy;
            public bool Grounded;
        }

        struct NodeKey : IEquatable<NodeKey>
        {
            public int Qpx, Qpy, Qvx, Qvy; public bool G;
            public bool Equals(NodeKey o) => Qpx == o.Qpx && Qpy == o.Qpy && Qvx == o.Qvx && Qvy == o.Qvy && G == o.G;
            public override int GetHashCode() => HashCode.Combine(Qpx, Qpy, Qvx, Qvy, G);
        }

        static NodeKey Key(SSNode s) => new NodeKey
        {
            Qpx = (int)MathF.Round(s.Px / PxQuant),
            Qpy = (int)MathF.Round(s.Py / PxQuant),
            Qvx = (int)MathF.Round(s.Vx / VxQuant),
            Qvy = (int)MathF.Round(s.Vy / VxQuant),
            G = s.Grounded,
        };

        public class SSResult
        {
            public bool Found;
            public int Expansions;
            public double Millis;
            public List<(float px, float py)> Path = new();
            public List<PathSeg> Segments = new();
            public List<PhysicsSimulator.ControlInput> ExecFrames = new();
            public float BestPx, BestPy, BestDx, BestDy;
            public List<(float px, float py)> Explored = new();
        }

        public class PathSeg
        {
            public bool IsJump;
            public int Hold;
            public int FrameCount;
            public List<(float px, float py)> Trail = new();
        }

        public static SSResult Plan(int goalWx, int goalWy)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var res = new SSResult();
            var p = Main.LocalPlayer;
            if (p == null || !p.active) return res;
            var ph = PhysicsSimulator.Params.FromPlayer(p);
            var holdOptions = BuildHoldOptions();

            float goalCx = goalWx * 16f + 8f;
            float goalFeetY = (goalWy + 1) * 16f;

            var start = new SSNode
            {
                Px = p.position.X, Py = p.position.Y,
                Vx = p.velocity.X, Vy = 0f, Grounded = true,
            };

            var g = new Dictionary<NodeKey, float>();
            var came = new Dictionary<NodeKey, (NodeKey prev, SSNode node, List<PhysicsSimulator.ControlInput> frames)>();
            var open = new PriorityQueue<SSNode, float>();
            var startKey = Key(start);
            g[startKey] = 0f;
            came[startKey] = (startKey, start, null);
            open.Enqueue(start, Heuristic(start, goalCx, goalFeetY, ph));

            int expansions = 0;
            NodeKey goalKey = default; bool found = false;
            float bestDist = float.MaxValue;

            while (open.Count > 0 && expansions < MaxExpansions)
            {
                var cur = open.Dequeue();
                var curKey = Key(cur);
                float curG = g.TryGetValue(curKey, out var gv) ? gv : float.MaxValue;

                {
                    float ccx = cur.Px + PhysicsSimulator.PlayerW / 2f;
                    float cfy = cur.Py + PhysicsSimulator.PlayerH;
                    float dist = MathF.Abs(ccx - goalCx) + MathF.Abs(cfy - goalFeetY);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        res.BestPx = cur.Px; res.BestPy = cur.Py;
                        res.BestDx = ccx - goalCx; res.BestDy = cfy - goalFeetY;
                    }
                }

                if (ReachedGoal(cur, goalCx, goalFeetY))
                {
                    found = true; goalKey = curKey; break;
                }

                expansions++;
                if (res.Explored.Count < 3000) res.Explored.Add((cur.Px, cur.Py));
                foreach (var (next, frames, cost) in Expand(cur, ph, goalCx, holdOptions))
                {
                    var nk = Key(next);
                    float ng = curG + cost;
                    if (ng < g.GetValueOrDefault(nk, float.MaxValue))
                    {
                        g[nk] = ng;
                        came[nk] = (curKey, next, frames);
                        open.Enqueue(next, ng + Heuristic(next, goalCx, goalFeetY, ph));
                    }
                }
            }

            sw.Stop();
            res.Expansions = expansions;
            res.Millis = sw.Elapsed.TotalMilliseconds;
            res.Found = found;
            if (found)
            {
                var k = goalKey;
                var revPts = new List<(float, float)>();
                var revSegs = new List<PathSeg>();
                var revFrameLists = new List<List<PhysicsSimulator.ControlInput>>();
                while (came.TryGetValue(k, out var e) && !e.prev.Equals(k))
                {
                    revPts.Add((e.node.Px, e.node.Py));
                    if (e.frames != null)
                    {
                        var seg = new PathSeg();
                        int hold = 0; bool counting = true;
                        foreach (var fr in e.frames)
                        {
                            seg.IsJump |= fr.Jump;
                            if (counting && fr.Jump) hold++; else counting = false;
                            seg.Trail.Add((fr.Px, fr.Py));
                        }
                        seg.Hold = hold;
                        seg.FrameCount = e.frames.Count;
                        revSegs.Add(seg);
                        revFrameLists.Add(e.frames);
                    }
                    k = e.prev;
                }
                revPts.Reverse();
                revSegs.Reverse();
                revFrameLists.Reverse();
                res.Path = revPts;
                res.Segments = revSegs;
                foreach (var fl in revFrameLists) res.ExecFrames.AddRange(fl);

                var segDesc = new System.Text.StringBuilder();
                foreach (var sg in revSegs)
                    segDesc.Append(sg.IsJump ? $" jump(h{sg.Hold},{sg.FrameCount}f)" : $" walk({sg.FrameCount}f)");
                DiagLog.Write($"[ss-path] segs={revSegs.Count}{segDesc}");
            }
            return res;
        }

        static IEnumerable<(SSNode next, List<PhysicsSimulator.ControlInput> frames, float cost)> Expand(
            SSNode cur, PhysicsSimulator.Params ph, float goalCx, int[] holdOptions)
        {
            if (!cur.Grounded) yield break;

            int dirToGoal = goalCx >= cur.Px ? 1 : -1;
            foreach (int dir in new[] { dirToGoal, -dirToGoal })
            {
                foreach (int hold in holdOptions)
                {
                    var seg = SimulateSegment(cur, dir, hold, ph);
                    if (seg.HasValue)
                        yield return (seg.Value.node, seg.Value.frames, seg.Value.frames.Count);
                }
            }
        }

        static (SSNode node, List<PhysicsSimulator.ControlInput> frames)? SimulateSegment(
            SSNode cur, int dir, int hold, PhysicsSimulator.Params ph)
        {
            var s = new PhysicsSimulator.State
            {
                Px = cur.Px, Py = cur.Py, Vx = cur.Vx, Vy = cur.Vy,
                Grounded = true, JumpFramesLeft = hold,
            };
            var frames = new List<PhysicsSimulator.ControlInput>();
            bool everAirborne = false;
            float startPx = s.Px;
            for (int f = 0; f < MaxSegFrames; f++)
            {
                var input = new PhysicsSimulator.ControlInput
                {
                    Right = dir > 0, Left = dir < 0, Jump = f < hold,
                };
                float prevPx = s.Px;
                s = PhysicsSimulator.Step(s, input, ph);
                input.Px = s.Px; input.Py = s.Py;
                frames.Add(input);
                if (!s.Grounded) everAirborne = true;
                if (s.Grounded && everAirborne)
                    break;
                if (s.Grounded && hold == 0)
                {
                    if (MathF.Abs(s.Px - startPx) >= 24f) break;
                    if (MathF.Abs(s.Px - prevPx) < 0.05f && f >= 2) break; // wall: not advancing
                }
            }
            if (frames.Count == 0) return null;
            var node = new SSNode { Px = s.Px, Py = s.Py, Vx = s.Vx, Vy = s.Vy, Grounded = s.Grounded };
            if (MathF.Abs(node.Px - cur.Px) < 1f && MathF.Abs(node.Py - cur.Py) < 1f) return null; // no self-loops
            return (node, frames);
        }

        static bool ReachedGoal(SSNode s, float goalCx, float goalFeetY)
        {
            float cx = s.Px + PhysicsSimulator.PlayerW / 2f;
            float feetY = s.Py + PhysicsSimulator.PlayerH;
            return s.Grounded && MathF.Abs(cx - goalCx) <= 12f && MathF.Abs(feetY - goalFeetY) <= 12f;
        }

        static float Heuristic(SSNode s, float goalCx, float goalFeetY, PhysicsSimulator.Params ph)
        {
            float cx = s.Px + PhysicsSimulator.PlayerW / 2f;
            float feetY = s.Py + PhysicsSimulator.PlayerH;
            float dx = MathF.Abs(cx - goalCx);
            float dy = MathF.Abs(feetY - goalFeetY);
            return dx / MathF.Max(ph.MaxRun, 0.1f) + dy / 5f;
        }

        public static void Visualize(SSResult res, int goalWx, int goalWy)
        {
            const float CX = PhysicsSimulator.PlayerW / 2f, FY = PhysicsSimulator.PlayerH;
            var trail = new List<(float, float, bool)>();
            foreach (var seg in res.Segments)
                foreach (var (px, py) in seg.Trail)
                    trail.Add((px + CX, py + FY, seg.IsJump));
            var explored = new List<(float, float)>();
            foreach (var (px, py) in res.Explored) explored.Add((px + CX, py + FY));
            float goalPx = goalWx * 16f + 8f;
            float goalPy = (goalWy + 1) * 16f;
            PathVisSystem.SetSSPath(trail, explored, goalPx, goalPy, ttlFrames: 1200);
        }

        // ── execution ──
        static List<PhysicsSimulator.ControlInput> _execFrames;
        static int _execIdx;
        static int _execGoalWx, _execGoalWy;
        const float ReplanDriftPx = 24f;
        const int ReplanCooldown = 10;
        static int _replanCooldownLeft;
        static int _replanCount;
        const int MaxReplans = 40;

        public static bool IsActive => _execFrames != null && _execIdx < _execFrames.Count;

        public static void StopExec() { _execFrames = null; _execIdx = 0; }

        public static SSResult Execute(int goalWx, int goalWy)
        {
            var res = Plan(goalWx, goalWy);
            Visualize(res, goalWx, goalWy);
            DiagLog.Write($"[ss-plan] target=({goalWx},{goalWy}) found={res.Found} exp={res.Expansions} ms={res.Millis:0.#} frames={res.ExecFrames.Count} best_dx={res.BestDx:0.#} best_dy={res.BestDy:0.#}");
            if (!res.Found || res.ExecFrames.Count == 0) { StopExec(); return res; }
            _execFrames = res.ExecFrames;
            _execIdx = 0;
            _execGoalWx = goalWx; _execGoalWy = goalWy;
            _replanCooldownLeft = 0;
            _replanCount = 0;
            return res;
        }

        static bool Replan(string reason)
        {
            if (_replanCount >= MaxReplans) { DiagLog.Write("[ss-replan] max replans hit → stop"); return false; }
            _replanCount++;
            var res = Plan(_execGoalWx, _execGoalWy);
            Visualize(res, _execGoalWx, _execGoalWy);
            DiagLog.Write($"[ss-replan] reason={reason} #{_replanCount} found={res.Found} ms={res.Millis:0.#} frames={res.ExecFrames.Count}");
            if (!res.Found || res.ExecFrames.Count == 0) return false;
            _execFrames = res.ExecFrames;
            _execIdx = 0;
            _replanCooldownLeft = ReplanCooldown;
            return true;
        }

        public static void ApplyControls()
        {
            if (_execFrames == null) return;
            var p = Main.LocalPlayer;
            if (p == null || !p.active) { StopExec(); return; }
            if (_execIdx >= _execFrames.Count)
            {
                float cx = p.position.X + p.width / 2f, fy = p.position.Y + p.height;
                float gx = _execGoalWx * 16f + 8f, gy = (_execGoalWy + 1) * 16f;
                DiagLog.Write($"[ss-land] goal=({_execGoalWx},{_execGoalWy}) actual_px=({cx:0.#},{fy:0.#}) dx={(cx-gx):0.#} dy={(fy-gy):0.#}");
                StopExec();
                return;
            }
            var f = _execFrames[_execIdx];
            float dxp = p.position.X - f.Px;
            float dyp = p.position.Y - f.Py;
            float drift = MathF.Sqrt(dxp * dxp + dyp * dyp);

            if (_execIdx % 15 == 0)
                DiagLog.Write($"[ss-exec] frame={_execIdx}/{_execFrames.Count} expect=({f.Px:0.#},{f.Py:0.#}) actual=({p.position.X:0.#},{p.position.Y:0.#}) drift={drift:0.#}");

            if (_replanCooldownLeft > 0) _replanCooldownLeft--;

            // replan only when grounded: airborne states aren't expansion points, so mid-jump replan can't help
            if (drift > ReplanDriftPx && _replanCooldownLeft == 0 && p.velocity.Y == 0f)
            {
                if (Replan("drift")) return;
            }

            if (f.Left) p.controlLeft = true;
            if (f.Right) p.controlRight = true;
            if (f.Jump) p.controlJump = true;
            _execIdx++;
        }
    }
}
