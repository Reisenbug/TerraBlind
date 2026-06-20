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
			DiagLog.Write($"[explore] start sign={_sign}");
		}

		public static void Stop()
		{
			_active = false;
			_dispatched = false;
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
				if (StateSpacePlanner.ExecRunning) return;
				if (StateSpacePlanner.ExecDone)
				{
					_failStreak = 0;
					_dispatched = false;
					// fall through to pick the next leg this same frame
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
