using System;
using System.Collections.Generic;
using Terraria;

namespace TerraBlind
{
    // Phase 0 prototype: state-space physics search. Standalone, does not touch the grid A*.
    // node = real physics state (px,py,vx,vy,grounded), expansion = enumerate inputs × simulate.
    // Goal: validate feasibility + performance on real terrain before committing to replace grid A*.
    public static class StateSpacePlanner
    {
        // ── tuning ──
        const float PxQuant = 4f;      // position quantization for dedup (px)
        const float VxQuant = 0.5f;    // velocity quantization for dedup
        const int   MaxExpansions = 20000;
        const int   MaxSegFrames = 120; // max frames to simulate one macro-action
        static readonly int[] HoldOptions = { 0, 8, 12, 15 }; // 0 = no jump

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

        // result for HTTP/debug
        public class SSResult
        {
            public bool Found;
            public int Expansions;
            public double Millis;
            public List<(float px, float py)> Path = new();
            // debug: closest-to-goal state reached during search
            public float BestPx, BestPy, BestDx, BestDy;
        }

        // Plan from player's current real state to tile (goalWx, goalWy).
        public static SSResult Plan(int goalWx, int goalWy)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var res = new SSResult();
            var p = Main.LocalPlayer;
            if (p == null || !p.active) return res;
            var ph = PhysicsSimulator.Params.FromPlayer(p);

            float goalCx = goalWx * 16f + 8f;
            float goalFeetY = (goalWy + 1) * 16f; // floor top under goal tile

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
                foreach (var (next, frames, cost) in Expand(cur, ph, goalCx))
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
                var rev = new List<(float, float)>();
                while (came.TryGetValue(k, out var e) && !e.prev.Equals(k))
                {
                    rev.Add((e.node.Px, e.node.Py));
                    k = e.prev;
                }
                rev.Reverse();
                res.Path = rev;
            }
            return res;
        }

        // expand: enumerate {left,right,none} × {hold options}, simulate to next decision point
        static IEnumerable<(SSNode next, List<PhysicsSimulator.ControlInput> frames, float cost)> Expand(
            SSNode cur, PhysicsSimulator.Params ph, float goalCx)
        {
            // only expand from grounded states (decision points); airborne states are intermediate
            if (!cur.Grounded) yield break;

            int dirToGoal = goalCx >= cur.Px ? 1 : -1;
            // bias: try toward goal and away; both directions allowed for backtracking
            foreach (int dir in new[] { dirToGoal, -dirToGoal })
            {
                foreach (int hold in HoldOptions)
                {
                    var seg = SimulateSegment(cur, dir, hold, ph);
                    if (seg.HasValue)
                        yield return (seg.Value.node, seg.Value.frames, seg.Value.frames.Count);
                }
            }
        }

        // simulate one macro-action: move in `dir` with optional jump (hold frames), until landed again or timeout
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
                frames.Add(input);
                if (!s.Grounded) everAirborne = true;
                // decision point: landed after being airborne
                if (s.Grounded && everAirborne)
                    break;
                // grounded walk (hold==0): continue until advanced ~1.5 tiles, or stuck against a wall
                if (s.Grounded && hold == 0)
                {
                    if (MathF.Abs(s.Px - startPx) >= 24f) break;       // advanced ~1.5 tiles
                    if (MathF.Abs(s.Px - prevPx) < 0.05f && f >= 2) break; // wall: not advancing
                }
            }
            if (frames.Count == 0) return null;
            var node = new SSNode { Px = s.Px, Py = s.Py, Vx = s.Vx, Vy = s.Vy, Grounded = s.Grounded };
            // reject if didn't move meaningfully (avoid self-loops)
            if (MathF.Abs(node.Px - cur.Px) < 1f && MathF.Abs(node.Py - cur.Py) < 1f) return null;
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
            // rough frame estimate: horizontal at maxRun, vertical at jump speed
            return dx / MathF.Max(ph.MaxRun, 0.1f) + dy / 5f;
        }
    }
}
