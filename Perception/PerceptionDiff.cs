using Terraria;

namespace TerraBlind
{
	// Eye, B-path: turn continuous reality into DISCRETE events that are worth waking the brain (LLM). Runs every
	// frame off PostUpdateEverything, compares a few salient world quantities against last frame, and only PushEvents
	// when something a human would actually notice AND that changes what to do. Stays silent otherwise, no polling,
	// no per-frame stream to the LLM. This is the salience filter the design calls for: HP drops, a threat entering
	// the alert ring, world-event transitions, stepping into lava. Normal HP regen, an enemy drifting away, the slow
	// day/night creep, not reported.
	public static class PerceptionDiff
	{
		// alert ring: enemies within this many tiles are "in view" and worth flagging when they first appear.
		private const float AlertRingPx = 60 * 16f;
		// a single-frame HP loss at least this big is a hit worth reporting (filters tiny chip/regen noise).
		private const int HitThreshold = 8;

		private static bool _init;
		private static int _lastHp;
		private static bool _lastBloodMoon, _lastEclipse, _lastDay, _lastInLava;
		private static int _lastInvasion;
		private static int _lastNearbyThreats;

		public static void Reset() { _init = false; }

		public static void Tick()
		{
			var p = Main.LocalPlayer;
			if (p == null || !p.active) { _init = false; return; }

			if (!_init)
			{
				_init = true;
				_lastHp = p.statLife;
				_lastBloodMoon = Main.bloodMoon;
				_lastEclipse = Main.eclipse;
				_lastDay = Main.dayTime;
				_lastInvasion = Main.invasionType;
				_lastInLava = p.lavaWet;
				_lastNearbyThreats = CountNearbyThreats(p);
				return;
			}

			// HP drop → hit
			int hp = p.statLife;
			if (_lastHp - hp >= HitThreshold)
				HttpServerSystem.PushEvent("hurt",
					"{\"hp\":" + hp + ",\"max_hp\":" + p.statLifeMax2 + ",\"lost\":" + (_lastHp - hp) + "}");
			_lastHp = hp;

			// stepped into lava
			bool inLava = p.lavaWet;
			if (inLava && !_lastInLava)
				HttpServerSystem.PushEvent("hazard", "{\"kind\":\"lava\"}");
			_lastInLava = inLava;

			// a new threat entered the alert ring (count went up = something appeared, not just moved)
			int threats = CountNearbyThreats(p);
			if (threats > _lastNearbyThreats)
			{
				var (name, dist) = NearestThreat(p);
				HttpServerSystem.PushEvent("threat",
					"{\"count\":" + threats + ",\"nearest\":\"" + HttpServerSystem.JsonEscPublic(name)
					+ "\",\"dist_tiles\":" + (int)(dist / 16f) + "}");
			}
			_lastNearbyThreats = threats;

			// world-event transitions (start/end), each changes the threat landscape
			if (Main.bloodMoon != _lastBloodMoon)
				HttpServerSystem.PushEvent("world_event",
					"{\"event\":\"blood_moon\",\"active\":" + (Main.bloodMoon ? "true" : "false") + "}");
			_lastBloodMoon = Main.bloodMoon;

			if (Main.eclipse != _lastEclipse)
				HttpServerSystem.PushEvent("world_event",
					"{\"event\":\"eclipse\",\"active\":" + (Main.eclipse ? "true" : "false") + "}");
			_lastEclipse = Main.eclipse;

			if (Main.invasionType != _lastInvasion)
				HttpServerSystem.PushEvent("world_event",
					"{\"event\":\"invasion\",\"type\":" + Main.invasionType + "}");
			_lastInvasion = Main.invasionType;

			// day↔night flip (dawn/dusk), night means spawns, a human notices it turning dark
			if (Main.dayTime != _lastDay)
				HttpServerSystem.PushEvent("world_event",
					"{\"event\":\"" + (Main.dayTime ? "dawn" : "dusk") + "\"}");
			_lastDay = Main.dayTime;
		}

		private static int CountNearbyThreats(Player p)
		{
			int n = 0;
			for (int i = 0; i < Main.maxNPCs; i++)
			{
				var npc = Main.npc[i];
				if (!npc.active || npc.friendly || npc.townNPC || npc.lifeMax <= 5) continue;
				if (Vector2Dist(npc.Center, p.Center) <= AlertRingPx) n++;
			}
			return n;
		}

		private static (string, float) NearestThreat(Player p)
		{
			string name = ""; float best = float.MaxValue;
			for (int i = 0; i < Main.maxNPCs; i++)
			{
				var npc = Main.npc[i];
				if (!npc.active || npc.friendly || npc.townNPC || npc.lifeMax <= 5) continue;
				float d = Vector2Dist(npc.Center, p.Center);
				if (d < best) { best = d; name = npc.FullName; }
			}
			return (name, best);
		}

		private static float Vector2Dist(Microsoft.Xna.Framework.Vector2 a, Microsoft.Xna.Framework.Vector2 b)
		{
			float dx = a.X - b.X, dy = a.Y - b.Y;
			return (float)System.Math.Sqrt(dx * dx + dy * dy);
		}
	}
}
