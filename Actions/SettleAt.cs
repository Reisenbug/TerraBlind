using System.Text;
using Terraria;
using Terraria.ModLoader;

namespace TerraBlind
{
	// SETTLE, come to a full stop with the player CENTERED on a target column, without overshooting off it.
	//
	// The stopping DISTANCE is computed from vanilla's own friction, so it holds under any mobility. Release with no
	// input, on the ground, decays velocity.X by `runSlowdown` per frame (Player.cs ~19591), a linear ramp, so the
	// slide distance is the arithmetic series sum ≈ v² / (2·runSlowdown). runSlowdown is a LIVE player field that
	// already bakes in boots/wings/honey/etc., so predicting with it never goes stale the way a hard-coded distance
	// would.
	//
	// Each frame compare the remaining distance to that predicted slide:
	//   remaining > slide  → not far enough; keep pushing toward the target
	//   remaining ≤ slide  → release (or brake), momentum will carry us the rest of the way onto the cell
	// Done when centered within half a tile, ~stopped, on the ground, for a few frames.
	public static class SettleAt
	{
		private static bool _running;
		private static int _col;
		private static float _targetPx;          // world-x of the target column's CENTER
		private static float _tol = HalfTile;
		private static int _frames, _stableFrames;

		private const int MaxFrames = 300;
		private const int StableNeeded = 5;      // frames centered & ~stopped to call it settled
		private const float HalfTile = 8f;       // half a tile, the target tolerance (geometry, not mobility)
		public const float VxDead = 0.05f;       // treat |vx| below this as stopped(到达判定共用这一份)

		public static bool IsRunning => _running;
		public static string Outcome = "idle";   // idle running done timeout
		public static string Reason = "";

		public static bool Start(int col, out string why) => StartPx(col, col * 16f + 8f, HalfTile, out why);

		// 精确到【占哪几列】。像素 tol=0 不可能:落点靠摩擦滑行,是亚像素值,永远等不到相等。
		// 列是整数条件。取合法像素窗口的中点、容差=半窗宽,于是列上零容差。
		public static bool StartSpan(int leftCol, int rightCol, out string why)
		{
			why = "";
			var p = Main.LocalPlayer;
			if (p == null) { why = "no_player"; return false; }
			float w = p.width;
			// 占 l..r ⟺ px∈[l*16, l*16+15] 且 px+w-1∈[r*16, r*16+15],两个区间取交
			float loPx = System.MathF.Max(leftCol * 16f, rightCol * 16f - w + 1f);
			float hiPx = System.MathF.Min(leftCol * 16f + 15f, rightCol * 16f + 16f - w);
			if (loPx > hiPx) { why = $"span_impossible {leftCol}..{rightCol} w={w}"; return false; }
			float mid = (loPx + hiPx) / 2f;
			return StartPx(leftCol, mid + w / 2f, (hiPx - loPx) / 2f + 0.4f, out why);
		}

		// 精确停位:身体中心停到 centerPx(容差 tol)。柱子放哪一列看身体中心,
		// 半格容差下人可能跨 {col-1,col} 也可能跨 {col,col+1}。头顶哪一列有方块就撞哪列。
		public static bool StartPx(int col, float centerPx, float tol, out string why)
		{
			why = "";
			var p = Main.LocalPlayer;
			if (p == null) { why = "no_player"; return false; }
			_col = col;
			_targetPx = centerPx;
			_tol = tol;
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

			// SETTLED, centered within half a tile, ~stopped, on the ground, held a few frames.
			if (System.MathF.Abs(err) <= _tol && System.MathF.Abs(vx) <= VxDead && p.velocity.Y == 0f)
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

			// PREDICTED SLIDE from vanilla's live friction, how far a release now would carry us, in the direction we
			// are actually moving. runSlowdown is read every frame, so this tracks whatever mobility is in effect.
			float slow = System.MathF.Max(p.runSlowdown, 0.01f);
			float slide = vx * vx / (2f * slow);               // magnitude
			float slideSigned = System.MathF.Sign(vx) * slide; // along the current motion

			// If letting go now would carry us PAST the target, stop feeding speed, release, or brake if we are
			// overshooting fast. Otherwise we still need more distance, so push toward the target.
			bool wouldReachOrPass = System.MathF.Abs(slideSigned) >= System.MathF.Abs(err)
									 && System.MathF.Sign(slideSigned) == System.MathF.Sign(err);

			if (wouldReachOrPass)
			{
				// coasting will land us on (or past) the cell. If we are clearly going to overshoot, brake against
				// the motion; otherwise release and let friction do the rest.
				float overshoot = System.MathF.Abs(slideSigned) - System.MathF.Abs(err);
				if (overshoot > _tol)
				{
					if (vx > 0f) p.controlLeft = true; else if (vx < 0f) p.controlRight = true;
				}
				// else release, the coast lands within tolerance.
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
