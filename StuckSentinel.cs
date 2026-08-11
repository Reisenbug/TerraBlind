using Terraria;

namespace TerraBlind
{
	// THE FAST EYE ON PROGRESS. Stuck is an expectation violation, and every signal needed to see it is readable
	// every frame: body position, the field's H, mining damage, the tiles around the body. A human notices "I'm
	// not actually moving" in a fraction of a second — this sentinel matches that: when for FlatWindow ticks
	// NOTHING changes (no displacement, no H drop, no dig damage accruing, no nearby tile placed/removed), that
	// is an anomaly. The response ladder starts at a one-cell safe step within half a second and abandons the
	// leg in single-digit seconds — abandoning is cheap, the receding loop re-selects instantly off the cached
	// field. Standing still while the pick chews a tile is NOT stuck: the dig-damage signal keeps it legal.
	public static class StuckSentinel
	{
		const int FlatWindow = 30;      // ~0.5s of everything-flat → anomaly (also tolerates the pause between pick hits)
		const int NudgeTicks = 18;      // one safe step: ~0.3s of manual control
		const int CalmTicks = 300;      // 5s without a new anomaly clears the count
		const int GiveUpAnomalies = 4;  // 4th anomaly in one episode → abandon the leg (~6-8s worst case)
		const float MovedPx = 24f;      // net displacement over the window that counts as motion (1.5 cells)

		public static bool Nudging => _nudgeLeft > 0;

		static int _sampleAge;
		static int _anomalies;
		static int _calm;               // ticks since the last anomaly
		static int _hAtEpisode = int.MaxValue;   // H when the current stuck episode began — dropping below = real recovery
		static int _nudgeLeft;
		static int _nudgeDir;
		static float _basePx, _basePy;
		static int _baseH = int.MaxValue;
		static int _baseDig;
		static int _baseSolids;

		public static void Reset()
		{
			_sampleAge = 0; _anomalies = 0; _calm = 0; _nudgeLeft = 0; _nudgeDir = 0;
			_baseH = int.MaxValue; _hAtEpisode = int.MaxValue;
		}

		// Per-frame. Returns true when the leg should be abandoned (caller reports "stuck" and stops).
		public static bool Tick(Player p, int goalWx, int goalWy)
		{
			if (_nudgeLeft > 0) { ApplyNudge(p); return false; }

			_calm++;
			if (_anomalies > 0 && _calm >= CalmTicks) { _anomalies = 0; _hAtEpisode = int.MaxValue; }

			_sampleAge++;
			if (_sampleAge < FlatWindow) return false;
			_sampleAge = 0;

			var field = MazeWand.GetField(goalWx, goalWy);
			var (cx, cy) = StateSpacePlanner.StandCell(p.position.X, p.position.Y);
			int h = field != null && field.TryGetValue((cx, cy), out int hv) ? hv : int.MaxValue;
			int dig = DigSum(p);
			int solids = SolidsNear(p);
			float moved = System.Math.Abs(p.position.X - _basePx) + System.Math.Abs(p.position.Y - _basePy);

			bool first = _baseH == int.MaxValue;
			bool progress = moved > MovedPx || (h < _baseH) || (dig > _baseDig) || (solids != _baseSolids);
			_basePx = p.position.X; _basePy = p.position.Y;
			_baseH = h; _baseDig = dig; _baseSolids = solids;
			if (first) return false;                      // no baseline yet — judge from the next window on

			// real recovery: H fell below where this episode started → the safe steps worked, close the episode
			if (_anomalies > 0 && h < _hAtEpisode) { _anomalies = 0; _hAtEpisode = int.MaxValue; }
			if (progress) return false;

			// ANOMALY — everything flat for a full window
			_calm = 0;
			_anomalies++;
			if (_anomalies == 1) _hAtEpisode = h;
			EventLog.W(Ev.Sentinel, $"anomaly {_anomalies}/{GiveUpAnomalies} at ({cx},{cy}) H={h} moved={moved:0}px dig={dig} solids={solids}");
			if (_anomalies >= GiveUpAnomalies) return true;

			// SAFE STEP: abort whatever the executor was doing and take one manual sideways hop; alternate the
			// direction each try so two nudges probe both sides. Never a ban, never a backtrack rule — one step.
			StateSpacePlanner.StopNav();
			_nudgeDir = _nudgeDir == 0 ? OpenDir(p, cx, cy) : -_nudgeDir;
			_nudgeLeft = NudgeTicks;
			Main.NewText($"[TerraBlind] sentinel: flat {FlatWindow} ticks — safe step {(_nudgeDir > 0 ? "right" : "left")} ({_anomalies}/{GiveUpAnomalies})");
			return false;
		}

		static void ApplyNudge(Player p)
		{
			_nudgeLeft--;
			if (_nudgeDir > 0) p.controlRight = true; else p.controlLeft = true;
			if (_nudgeLeft > NudgeTicks - 10) p.controlJump = true;   // a small hop clears lips/half-bricks
		}

		// which side has more open body-height cells two columns out — step toward space, not into the wall
		static int OpenDir(Player p, int cx, int cy)
		{
			int OpenCount(int c)
			{
				int n = 0;
				for (int r = 0; r < 3; r++)
				{
					int x = c, y = cy - r;
					if (x < 0 || y < 0 || x >= Main.maxTilesX || y >= Main.maxTilesY) continue;
					var t = Main.tile[x, y];
					if (!t.HasTile || !Main.tileSolid[t.TileType]) n++;
				}
				return n;
			}
			return OpenCount(cx + 2) >= OpenCount(cx - 2) ? 1 : -1;
		}

		// total mining damage buffered across the player's recent swings — rising = the tool is landing hits
		static int DigSum(Player p)
		{
			int s = 0;
			var data = p.hitTile?.data;
			if (data == null) return 0;
			for (int i = 0; i < data.Length; i++)
				if (data[i] != null) s += data[i].damage;
			return s;
		}

		// solid tiles in a small ring around the body — placing a pillar block or finishing a dig changes it
		static int SolidsNear(Player p)
		{
			int cx = (int)((p.position.X + p.width / 2f) / 16f);
			int cy = (int)((p.position.Y + p.height / 2f) / 16f);
			int n = 0;
			for (int dx = -4; dx <= 4; dx++)
				for (int dy = -4; dy <= 4; dy++)
				{
					int x = cx + dx, y = cy + dy;
					if (x < 0 || y < 0 || x >= Main.maxTilesX || y >= Main.maxTilesY) continue;
					var t = Main.tile[x, y];
					if (t.HasTile && Main.tileSolid[t.TileType]) n++;
				}
			return n;
		}
	}
}
