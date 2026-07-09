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
	}
}
