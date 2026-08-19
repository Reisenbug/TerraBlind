using Terraria;

namespace TerraBlind
{
	// Hand, survival reflex: a horizontal, always-on frame-level loop that keeps the bot ALIVE while any main action
	// (mine/chop/etc.) is running — the brain (LLM) is seconds too slow to react to lava or a sudden hit. It does
	// "stay alive", NOT "tactics": jump out of lava, quick-heal when hurt. It does not chase or fight — that's the
	// brain's call once woken. Fires an "interrupted" event so the brain takes over for the actual decision.
	//
	// Runs every frame from PostUpdateEverything, independent of any single action (reflex is cross-cutting, not
	// baked into each action). Sets controls directly, same as the coordinators.
	public static class SurvivalReflex
	{
		// heal when HP falls below this fraction of max — low enough not to waste potions on chip damage.
		private const float HealFraction = 0.5f;
		// melee-on-contact reflex: DISABLED pending a proper combat design — the v1 swing was unreliable and
		// enemy handling deserves better than a slapdash reflex.
		private const bool MeleeReflexEnabled = false;
		private static bool _firedThisEmergency;   // one interrupt per danger episode, not per frame

		public static void Tick()
		{
			var p = Main.LocalPlayer;
			if (p == null || !p.active || p.dead) { _firedThisEmergency = false; return; }

			bool inLava = p.lavaWet;
			bool lowHp = p.statLife < p.statLifeMax2 * HealFraction;
			bool emergency = inLava || lowHp;

			// LAVA: jump to climb out. controlJump each frame while submerged pushes the player upward.
			if (inLava)
				p.controlJump = true;

			// HEAL: quick-heal respects potionDelay internally, so calling it every frame is safe (no-op on cooldown).
			if (inLava || lowHp)
				p.QuickHeal();

			// MELEE REFLEX: an enemy INSIDE arm's reach gets a swing — at that range whacking it for a few frames
			// beats continuing the dig/walk. Pure reflex, no chasing, no tactics: the moment it leaves the ring we
			// stop touching the controls, and the encounter-level decision (clear it properly / flee) stays upstream.
			var foe = MeleeReflexEnabled ? ClosestFoe(p, 3.5f * 16f) : null;
			if (foe != null)
			{
				int ws = BestWeaponSlot(p);
				if (ws >= 0)
				{
					p.selectedItem = ws;
					Cursor.AimPx(foe.Center.X, foe.Center.Y);
					if (p.itemTime == 0)
						p.controlUseItem = true;
				}
			}

			// Wake the brain ONCE when an emergency begins — it decides the real response (flee where, fight, retreat).
			// The reflex only bought time. Reset when danger clears so the next episode fires again.
			if (emergency && !_firedThisEmergency)
			{
				_firedThisEmergency = true;
				string kind = inLava ? "lava" : "low_hp";
				HttpServerSystem.PushEvent("survival",
					"{\"kind\":\"" + kind + "\",\"hp\":" + p.statLife + ",\"max_hp\":" + p.statLifeMax2 + "}");
			}
			else if (!emergency)
			{
				_firedThisEmergency = false;
			}
		}

		static NPC ClosestFoe(Player p, float rangePx)
		{
			NPC best = null; float bd = rangePx;
			for (int i = 0; i < Main.maxNPCs; i++)
			{
				var n = Main.npc[i];
				if (n == null || !n.active || n.friendly || n.damage <= 0 || n.life <= 0) continue;
				float d = Microsoft.Xna.Framework.Vector2.Distance(n.Center, p.Center);
				if (d < bd) { bd = d; best = n; }
			}
			return best;
		}

		// best hotbar weapon: prefer a pure weapon (no tool power), fall back to the hardest-hitting tool
		static int BestWeaponSlot(Player p)
		{
			int best = -1, bestDmg = 0; bool bestPure = false;
			for (int i = 0; i < 10; i++)
			{
				var it = p.inventory[i];
				if (it == null || it.IsAir || it.damage <= 0 || it.useStyle == 0) continue;
				bool pure = it.pick == 0 && it.axe == 0 && it.hammer == 0;
				if ((pure && !bestPure) || (pure == bestPure && it.damage > bestDmg))
				{ best = i; bestDmg = it.damage; bestPure = pure; }
			}
			return best;
		}
	}
}
