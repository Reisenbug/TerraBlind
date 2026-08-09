using System.Text;
using Terraria;
using Terraria.ModLoader;

namespace TerraBlind
{
	// DROP — fall down through the platform underfoot to the solid ground below. Standing on a platform, holding
	// controlDown makes vanilla drop the player through it (Player.cs `fallThrough = controlDown`, instant). Used to
	// come down off a roof onto the foundation.
	//
	// Ends on a world fact: standing (velocity.Y == 0) on a SOLID tile (not a platform we could keep falling through).
	// No frame count — the fall takes as long as it takes at whatever gravity is in effect.
	public static class DropDown
	{
		private static bool _running;
		private static int _frames;
		private static int _startCy;
		private static int _stopCy = int.MaxValue;

		private const int MaxFrames = 240;

		public static bool IsRunning => _running;
		public static string Outcome = "idle";   // idle running done timeout
		public static string Reason = "";

		public static bool Start(out string why) => Start(int.MaxValue, out why);

		// stopCy: 掉到这一行(身体行)就停,不再往下穿。爬过头一格时只需挪一格,
		// 不传则一路掉到实心地面。
		public static bool Start(int stopCy, out string why)
		{
			why = "";
			var p = Main.LocalPlayer;
			if (p == null) { why = "no_player"; return false; }
			_running = true; _frames = 0;
			_startCy = ActExecutor.OriginCy(p);
			_stopCy = stopCy;
			Outcome = "running"; Reason = "";
			DiagLog.Write($"[drop] start from cy={_startCy} stop={(stopCy == int.MaxValue ? "ground" : stopCy.ToString())}");
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

			int cx = ActExecutor.OriginCx(p);
			int cy = ActExecutor.OriginCy(p);

			// LANDED ON SOLID GROUND = done. Grounded, moved down from the start, and the tile just below the feet is
			// SOLID (not a platform — landing on a platform we would just keep dropping through).
			// 到了指定行就停:再往下穿就得重爬一遍
			if (cy >= _stopCy && p.velocity.Y == 0f)
			{
				Outcome = "done"; _running = false;
				DiagLog.Write($"[drop] done at cy={cy} (stop row) after {_frames}f");
				return;
			}
			if (p.velocity.Y == 0f && cy > _startCy)
			{
				var below = InBounds(cx, cy + 1) ? Main.tile[cx, cy + 1] : default;
				bool onSolid = below.HasTile && Main.tileSolid[below.TileType] && !Main.tileSolidTop[below.TileType];
				if (onSolid)
				{
					Outcome = "done"; _running = false;
					DiagLog.Write($"[drop] done at cy={cy} after {_frames}f");
					return;
				}
			}

			// hold down to fall through platforms. controlDown is the instant fall-through; keep it held so each
			// platform on the way down is passed, not landed on.
			p.controlDown = true;
		}

		public static string StatusJson()
		{
			var p = Main.LocalPlayer;
			var sb = new StringBuilder();
			sb.Append("{\"outcome\":\"").Append(Outcome).Append('"')
			  .Append(",\"running\":").Append(_running ? "true" : "false")
			  .Append(",\"reason\":\"").Append(Reason).Append('"');
			if (p != null)
				sb.Append(",\"origin\":[").Append(ActExecutor.OriginCx(p)).Append(',').Append(ActExecutor.OriginCy(p)).Append(']')
				  .Append(",\"on_ground\":").Append(p.velocity.Y == 0f ? "true" : "false")
				  .Append(",\"vel_y\":").Append(p.velocity.Y.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
			sb.Append('}');
			return sb.ToString();
		}

		private static bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Main.maxTilesX && y < Main.maxTilesY;
	}
}
