using System.Text;
using Terraria;
using Terraria.ModLoader;

namespace TerraBlind
{
	// SETTLE — come to a full stop with the player CENTERED on a target column, without overshooting off it.
	//
	// The stopping DISTANCE is computed from vanilla's own friction, so it holds under any mobility. Release with no
	// input, on the ground, decays velocity.X by `runSlowdown` per frame (Player.cs ~19591) — a linear ramp, so the
	// slide distance is the arithmetic series sum ≈ v² / (2·runSlowdown). runSlowdown is a LIVE player field that
	// already bakes in boots/wings/honey/etc., so predicting with it never goes stale the way a hard-coded distance
	// would.
	//
	// Each frame compare the remaining distance to that predicted slide:
	//   remaining > slide  → not far enough; keep pushing toward the target
	//   remaining ≤ slide  → release (or brake) — momentum will carry us the rest of the way onto the cell
	// Done when centered within half a tile, ~stopped, on the ground, for a few frames.
	public static class SettleAt
	{
		private static bool _running;
		private static int _col;
		private static float _targetPx;          // world-x of the target column's CENTER
		private static int _frames, _stableFrames;

		private const int MaxFrames = 300;
		private const int StableNeeded = 5;      // frames centered & ~stopped to call it settled
		private const float HalfTile = 8f;       // half a tile — the target tolerance (geometry, not mobility)
		private const float VxDead = 0.05f;      // treat |vx| below this as stopped

		public static bool IsRunning => _running;
		public static string Outcome = "idle";   // idle running done timeout
		public static string Reason = "";

		public static bool Start(int col, out string why)
		{
			why = "";
			var p = Main.LocalPlayer;
			if (p == null) { why = "no_player"; return false; }
			_col = col;
			_targetPx = col * 16f + 8f;         // center of the target column
			_frames = 0; _stableFrames = 0;
			_running = true;
			Outcome = "running"; Reason = "";
			DiagLog.Write($"[settle] start col={col} from_cx={ActExecutor.OriginCx(p)}");
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
			if (p == null || !p.active) { Reason = "no_player"; Outcome = "timeout"; _running = false; return; }

			_frames++;
			if (_frames > MaxFrames) { Outcome = "timeout"; _running = false; return; }

			float center = p.position.X + p.width / 2f;
			float err = _targetPx - center;    // remaining distance, signed: >0 target is to the right
			float vx = p.velocity.X;

			// SETTLED — centered within half a tile, ~stopped, on the ground, held a few frames.
			if (System.MathF.Abs(err) <= HalfTile && System.MathF.Abs(vx) <= VxDead && p.velocity.Y == 0f)
			{
				if (++_stableFrames >= StableNeeded)
				{
					Outcome = "done"; _running = false;
					DiagLog.Write($"[settle] done centered on col {_col} after {_frames}f");
					return;
				}
				return;
			}
			_stableFrames = 0;

			// PREDICTED SLIDE from vanilla's live friction — how far a release now would carry us, in the direction we
			// are actually moving. runSlowdown is read every frame, so this tracks whatever mobility is in effect.
			float slow = System.MathF.Max(p.runSlowdown, 0.01f);
			float slide = vx * vx / (2f * slow);               // magnitude
			float slideSigned = System.MathF.Sign(vx) * slide; // along the current motion

			// If letting go now would carry us PAST the target, stop feeding speed — release, or brake if we are
			// overshooting fast. Otherwise we still need more distance, so push toward the target.
			bool wouldReachOrPass = System.MathF.Abs(slideSigned) >= System.MathF.Abs(err)
									 && System.MathF.Sign(slideSigned) == System.MathF.Sign(err);

			if (wouldReachOrPass)
			{
				// coasting will land us on (or past) the cell. If we are clearly going to overshoot, brake against
				// the motion; otherwise release and let friction do the rest.
				float overshoot = System.MathF.Abs(slideSigned) - System.MathF.Abs(err);
				if (overshoot > HalfTile)
				{
					if (vx > 0f) p.controlLeft = true; else if (vx < 0f) p.controlRight = true;
				}
				// else release — the coast lands within tolerance.
			}
			else
			{
				// not enough momentum to reach the target → push toward it.
				if (err > 0f) p.controlRight = true; else p.controlLeft = true;
			}
		}

		public static string StatusJson()
		{
			var p = Main.LocalPlayer;
			var sb = new StringBuilder();
			sb.Append("{\"outcome\":\"").Append(Outcome).Append('"')
			  .Append(",\"running\":").Append(_running ? "true" : "false")
			  .Append(",\"col\":").Append(_col)
			  .Append(",\"reason\":\"").Append(Reason).Append('"');
			if (p != null)
				sb.Append(",\"origin\":[").Append(ActExecutor.OriginCx(p)).Append(',').Append(ActExecutor.OriginCy(p)).Append(']')
				  .Append(",\"on_ground\":").Append(p.velocity.Y == 0f ? "true" : "false")
				  .Append(",\"vel_x\":").Append(p.velocity.X.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
			sb.Append('}');
			return sb.ToString();
		}
	}
}
