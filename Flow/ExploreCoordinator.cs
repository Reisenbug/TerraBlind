using Terraria;
using Terraria.ModLoader;

namespace TerraBlind
{
	// Open-ended direction explore: walk toward a sign forever. No endpoint, no biome stop — Python flips it on
	// (/nav_start{sign}) and Ctrl-C's it off (/nav_stop). Each leg picks the furthest reachable standable cell
	// ahead and drives the NEW StateSpacePlanner there; on arrival, picks the next leg. Reuses the physics-faithful
	// planner (unlike the legacy NavCoordinator direction explore which used old pathing).
	public class ExploreCoordinator : ModSystem
	{
		private static volatile bool _active;
		private static int _sign;
		private static int _failStreak;
		private static string _failCode;
		private static bool _dispatched;          // a leg is currently being executed by StateSpacePlanner

		// LOOKAHEAD: while the current leg walks, a background thread plans the NEXT leg from the current leg's
		// PREDICTED landing. On arrival, if the prediction matched reality the cached plan dispatches with zero
		// stop-and-replan stall (the stall the user saw at odd terrain). Mismatch / not-ready → fall back to
		// planning fresh from the real position. Re-entrancy (PlanCtx) makes the background Plan safe to run
		// concurrently with the foreground executor's own distField.
		private static volatile System.Threading.Tasks.Task _bgTask;
		private static volatile StateSpacePlanner.SSResult _bgResult;   // set by the bg thread when its Plan finishes
		private static int _bgFromCx, _bgFromCy;   // the predicted landing cell the bg plan started from (for validation)
		private static bool _bgLaunched;           // a bg plan was kicked off for the in-flight leg
		private const int LandMatchTol = 2;        // predicted vs real landing within this many cells → cache is valid

		// goal-selection values copied from the legacy PathPlanner.Plan(sign) (which picked good surface goals).
		private const int MinFwd = 5;             // MinGoalDist: min forward distance, prevents staying in place
		private const int MaxFwd = 60;            // GoalRangeFwd: forward search window
		private const int ScanUp = 50;            // AStarScanUp
		private const int ScanDown = 50;          // AStarScanDown
		private const int MaxFailStreak = 8;      // consecutive unreachable legs → give up this direction

		public static bool Active => _active;
		public static string FailCode => _failCode;

		public static void Start(int sign)
		{
			StateSpacePlanner.StopExec();
			_active = true;
			_sign = sign >= 0 ? 1 : -1;
			_failStreak = 0;
			_failCode = null;
			_dispatched = false;
			_bgResult = null; _bgLaunched = false;
			DiagLog.Write($"[explore] start sign={_sign}");
		}

		public static void Stop()
		{
			_active = false;
			_dispatched = false;
			_bgResult = null; _bgLaunched = false;
			PathVisSystem.ClearLookahead();
			StateSpacePlanner.StopExec();
			DiagLog.Write("[explore] stop");
		}

		public static void ApplyControls()
		{
			if (!_active) return;
			var p = Main.LocalPlayer;
			if (p == null || !p.active) { Stop(); return; }

			// a leg is in flight: let StateSpacePlanner run until it finishes or fails.
			if (_dispatched)
			{
				MaybeLaunchLookahead();   // once per leg, plan the NEXT leg in the background while this one walks
				if (StateSpacePlanner.ExecRunning) return;
				if (StateSpacePlanner.ExecDone)
				{
					_failStreak = 0;
					_dispatched = false;
					if (TryDispatchLookahead(p)) return;   // cached next leg matched reality → zero-stall handoff
					// else fall through to pick the next leg fresh this same frame
				}
				else
				{
					// leg failed to reach — count it; the next pick will try a nearer/different cell.
					_failStreak++;
					_dispatched = false;
					DiagLog.Write($"[explore] leg failed ({StateSpacePlanner.ExecFailCode}) streak={_failStreak}");
					if (_failStreak >= MaxFailStreak)
					{
						_failCode = "explore_stuck";
						_active = false;
						DiagLog.Write("[explore] stuck → stop");
						return;
					}
				}
			}

			// only dispatch a new leg from rest on the ground (StateSpacePlanner expects a grounded start).
			if (p.velocity.Y != 0f) return;

			int pcx = (int)((p.position.X + p.width / 2f) / 16f);
			int feetY = (int)((p.position.Y + p.height) / 16f);

			var goal = PickAhead(pcx, feetY);
			if (goal == null)
			{
				_failStreak++;
				DiagLog.Write($"[explore] no goal ahead sign={_sign} streak={_failStreak}");
				if (_failStreak >= MaxFailStreak) { _failCode = "explore_stuck"; _active = false; DiagLog.Write("[explore] stuck → stop"); }
				return;
			}

			var (gx, gy) = goal.Value;
			DiagLog.Write($"[explore] leg → ({gx},{gy})");
			StateSpacePlanner.Execute(gx, gy);
			_dispatched = true;
			_bgResult = null; _bgLaunched = false;   // this leg planned fresh; its own lookahead starts from scratch
		}

		// Kick off ONE background plan per in-flight leg: from the current leg's PREDICTED landing, pick the next
		// goal and Plan toward it on a thread pool thread. The result is cached; we never block on it — if it isn't
		// ready (or doesn't match) on arrival, we just plan fresh. Exceptions are swallowed: a failed lookahead is
		// indistinguishable from "no cache" downstream, so it can never break the foreground walk.
		private static void MaybeLaunchLookahead()
		{
			if (_bgLaunched) return;
			var cur = StateSpacePlanner.LastExecResult;
			if (cur == null || !cur.Found || cur.Steps == null || cur.Steps.Count == 0) return;

			// predicted landing of the current leg = the snapped goal cell it's driving toward.
			int fromCx = cur.GoalWx, fromCy = cur.GoalWy;
			var (px, py, vx) = PredictedLanding(cur, fromCx, fromCy);

			var nextGoal = PickAhead(fromCx, fromCy);
			if (nextGoal == null) { _bgLaunched = true; return; }   // nothing ahead from there; don't retry this leg
			var (ngx, ngy) = nextGoal.Value;

			_bgLaunched = true;
			_bgResult = null;
			_bgFromCx = fromCx; _bgFromCy = fromCy;
			DiagLog.Write($"[explore-bg] launch from predicted ({fromCx},{fromCy}) vx={vx:0.##} → goal ({ngx},{ngy})");
			_bgTask = System.Threading.Tasks.Task.Run(() =>
			{
				try
				{
					var r = StateSpacePlanner.Plan(ngx, ngy, (px, py, vx));
					_bgResult = r;
					DiagLog.Write($"[explore-bg] done found={r.Found} steps={(r.Steps?.Count ?? 0)} ms={r.Millis:0.#}");
					if (r.Found) VisualizeLookahead(r, px, py);
				}
				catch (System.Exception e) { DiagLog.Write($"[explore-bg] EXC {e.Message}"); _bgResult = null; }
			});
		}

		// Build the dim lookahead overlay from a bg plan's segments (same px-center convention as Visualize).
		// startPx/Py = the predicted landing the plan branched from → white ring, to eyeball prediction drift.
		private static void VisualizeLookahead(StateSpacePlanner.SSResult r, float startPx, float startPy)
		{
			const float CX = PhysicsSimulator.PlayerW / 2f, FY = PhysicsSimulator.PlayerH;
			var trail = new System.Collections.Generic.List<(float, float, bool)>();
			foreach (var seg in r.Segments)
				foreach (var (px, py) in seg.Trail)
					trail.Add((px + CX, py + FY, seg.IsJump));
			PathVisSystem.SetLookaheadPath(trail, r.GoalWx * 16f + 8f, (r.GoalWy + 1) * 16f, startPx + CX, startPy + FY);
		}

		// Use the cached next-leg plan IF the player's real landing cell matches the cell the bg plan started from.
		// Mismatch = the leg ended somewhere the prediction didn't expect (drift / different terrain) → the cached
		// plan's start is wrong, so discard it and let the caller plan fresh. Returns true iff it dispatched.
		private static bool TryDispatchLookahead(Player p)
		{
			var r = _bgResult;
			_bgResult = null;
			if (r == null || !r.Found || r.Steps == null || r.Steps.Count == 0) return false;

			int rcx = (int)((p.position.X + p.width / 2f) / 16f);
			int rcy = (int)((p.position.Y + p.height) / 16f);
			if (System.Math.Abs(rcx - _bgFromCx) > LandMatchTol || System.Math.Abs(rcy - _bgFromCy) > LandMatchTol)
			{
				DiagLog.Write($"[explore-bg] discard: real ({rcx},{rcy}) ≠ predicted ({_bgFromCx},{_bgFromCy})");
				PathVisSystem.ClearLookahead();
				return false;
			}
			DiagLog.Write($"[explore-bg] HIT → dispatch cached leg goal ({r.GoalWx},{r.GoalWy}) (zero-stall)");
			PathVisSystem.ClearLookahead();   // the dim overlay becomes the bright current leg via DispatchPlan→Visualize
			StateSpacePlanner.DispatchPlan(r);
			_dispatched = true;
			_bgLaunched = false;   // arm lookahead for this newly-dispatched leg
			return true;
		}

		// the (px,py,vx) the player is predicted to be in when the current leg's last move frame ends. Falls back to
		// the cell center at rest if the leg ends on a non-frame step (pillar/dig land at a clean cell, vx≈0).
		private static (float px, float py, float vx) PredictedLanding(StateSpacePlanner.SSResult res, int cx, int cy)
		{
			for (int i = res.Steps.Count - 1; i >= 0; i--)
			{
				var f = res.Steps[i].Frames;
				if (f != null && f.Count > 0)
				{
					var last = f[f.Count - 1];
					return (last.Px, last.Py, last.Vx);
				}
			}
			float px = cx * 16f + 8f - PhysicsSimulator.PlayerW / 2f;
			float py = (cy + 1) * 16f - PhysicsSimulator.PlayerH;
			return (px, py, 0f);
		}

		// goal selection copied verbatim from legacy PathPlanner.Plan(sign) L511-527 — the version that picked GOOD
		// surface goals (didn't dive into pits). per column ahead: take the FIRST standable scanning top→down (=
		// highest surface). score = forward + max(0,rise)*2, which BIASES toward going up, so a pit (negative rise)
		// scores low and is never chosen over flat/rising ground. pick the highest-scoring column across the window
		// (NOT the furthest — that was my bug, it picked isolated cells across pits). Execute judges reachability.
		private static (int gx, int gy)? PickAhead(int pcx, int feetY)
		{
			int goalX = -1, goalY = -1;
			int yTop = System.Math.Max(50, feetY - ScanUp);
			int yMax = feetY + ScanDown;
			for (int wx = pcx + _sign * MinFwd; _sign > 0 ? wx <= pcx + MaxFwd : wx >= pcx - MaxFwd; wx += _sign)
			{
				for (int wy = yTop; wy <= yMax; wy++)
				{
					if (!PathPlanner.StandablePublic(wx, wy)) continue;
					int fwd = _sign * (wx - pcx);
					int rise = feetY - wy;
					int score = fwd + System.Math.Max(0, rise) * 2;
					int bestScore = goalX < 0 ? int.MinValue : _sign * (goalX - pcx) + System.Math.Max(0, feetY - goalY) * 2;
					if (score > bestScore) { goalX = wx; goalY = wy; }
					break; // first standable in this column (the surface) only
				}
			}
			if (goalX < 0) return null;
			return (goalX, goalY);
		}
	}
}
