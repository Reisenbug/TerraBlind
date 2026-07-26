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
		private enum Ph { Align, Hop, Done }
		private static Ph _ph = Ph.Done;
		private static bool _running;
		private static int _targetCy;     // the row we want to be STANDING on (the surface's own row)
		private static int _col;          // column to line up under before hopping; int.MinValue = no alignment
		private static int _frames, _stall, _lastCy;
		private static bool _wasFalling;  // to detect the moment we land, so a multi-hop can push off again

		private const int MaxFrames = 300;

		public static bool IsRunning => _running;
		public static string Outcome = "idle";   // idle running done timeout
		public static int TargetCy => _targetCy;

		// targetWy = the row of the surface to land on. col (optional) = the column to line the body up under first,
		// so the hop rises straight into a platform column. The player is "up" once their own cell sits just above the
		// target row. A single jump may not clear the whole column — landing on an intermediate platform and pushing
		// off again ("跳两下") is expected, so the hop re-jumps each time it lands short.
		public static bool Start(int targetWy, int col, out string why)
		{
			why = "";
			var p = Main.LocalPlayer;
			if (p == null) { why = "no_player"; return false; }
			_targetCy = targetWy;
			_col = col;
			_frames = 0; _stall = 0;
			_lastCy = ActExecutor.OriginCy(p);
			_wasFalling = false;
			_running = true;
			_ph = col > int.MinValue ? Ph.Align : Ph.Hop;
			Outcome = "running";
			DiagLog.Write($"[hop] start target_row={targetWy} col={col} from=({ActExecutor.OriginCx(p)},{_lastCy})");
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

			int cx = ActExecutor.OriginCx(p);
			int cy = ActExecutor.OriginCy(p);

			// ALIGN — walk until the body's column sits under the target column, so the jump rises straight into it.
			if (_ph == Ph.Align)
			{
				if (cx == _col) { _ph = Ph.Hop; return; }
				if (cx < _col) p.controlRight = true; else p.controlLeft = true;
				return;
			}

			// standing ON the target surface: our own cell is the one directly above it, and we are not falling.
			if (cy <= _targetCy - 1 && p.velocity.Y == 0f)
			{
				Outcome = "done"; _running = false;
				DiagLog.Write($"[hop] done at row {cy} after {_frames}f");
				return;
			}

			// HOP. Hold jump to rise. A single jump may not clear the whole column, so on landing short we must let go
			// for ONE frame so the next controlJump is a fresh press — the "跳两下". The trap this replaced: standing
			// still before the first jump ALSO has velocity.Y == 0, and must not be mistaken for "landed, release".
			// So the release only fires on a real landing = we were descending last frame and are grounded now.
			// (Terraria y is DOWN-positive: rising is velocity.Y < 0, falling is > 0. I had this backwards.)
			bool grounded = p.velocity.Y == 0f;
			bool justLanded = grounded && _wasFalling;
			_wasFalling = p.velocity.Y > 0f;    // descending this frame → a later grounding is a real landing
			if (justLanded)
			{
				// landed below the target → skip jump this one frame so the next is a new press.
				return;
			}
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
