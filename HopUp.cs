using System.Text;
using Terraria;
using Terraria.ModLoader;

namespace TerraBlind
{
	// HOP UP — jump and end up standing on the surface directly above. Used to get off the top of a rope onto the
	// platform just placed above it: on a rope the player hangs, and the platform is passed through from below, so
	// simply holding jump does not settle them on top of it.
	//
	// Ends on the world fact that matters — feet resting on the target row, not moving vertically — so it is right
	// whatever the jump height happens to be. A fixed number of jump frames would be wrong the moment boots, wings,
	// or a gravity change enter the picture.
	public static class HopUp
	{
		private static bool _running;
		private static int _targetCy;     // the row we want to be STANDING on (the surface's own row)
		private static int _frames, _stall, _lastCy;

		private const int MaxFrames = 300;

		public static bool IsRunning => _running;
		public static string Outcome = "idle";   // idle running done timeout
		public static int TargetCy => _targetCy;

		// targetWy = the row of the surface to land on. The player is "up" once their own cell sits just above it.
		public static bool Start(int targetWy, out string why)
		{
			why = "";
			var p = Main.LocalPlayer;
			if (p == null) { why = "no_player"; return false; }
			_targetCy = targetWy;
			_frames = 0; _stall = 0;
			_lastCy = ActExecutor.OriginCy(p);
			_running = true;
			Outcome = "running";
			DiagLog.Write($"[hop] start target_row={targetWy} from=({ActExecutor.OriginCx(p)},{_lastCy})");
			return true;
		}

		public static void Stop()
		{
			if (Outcome == "running") Outcome = "stopped";
			_running = false;
		}

		public static void Tick()
		{
			if (!_running) return;
			var p = Main.LocalPlayer;
			if (p == null || !p.active) { _running = false; Outcome = "timeout"; return; }

			_frames++;
			if (_frames > MaxFrames) { Outcome = "timeout"; _running = false; return; }

			int cy = ActExecutor.OriginCy(p);

			// standing ON the target surface: our own cell is the one directly above it, and we are not falling.
			if (cy == _targetCy - 1 && p.velocity.Y == 0f)
			{
				Outcome = "done"; _running = false;
				DiagLog.Write($"[hop] done at row {cy} after {_frames}f");
				return;
			}

			// hold jump while below it. Releasing down is never wanted here — pressing down on a platform drops
			// through it, which would undo the hop.
			p.controlJump = true;
			if (cy != _lastCy) { _lastCy = cy; _stall = 0; }
			else _stall++;
		}

		public static string StatusJson()
		{
			var p = Main.LocalPlayer;
			var sb = new StringBuilder();
			sb.Append("{\"outcome\":\"").Append(Outcome).Append('"')
			  .Append(",\"running\":").Append(_running ? "true" : "false")
			  .Append(",\"target_row\":").Append(_targetCy)
			  .Append(",\"frames\":").Append(_frames);
			if (p != null)
				sb.Append(",\"origin\":[").Append(ActExecutor.OriginCx(p)).Append(',').Append(ActExecutor.OriginCy(p)).Append(']')
				  .Append(",\"on_ground\":").Append(p.velocity.Y == 0f ? "true" : "false")
				  .Append(",\"vel_y\":").Append(p.velocity.Y.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
			sb.Append('}');
			return sb.ToString();
		}
	}
}
