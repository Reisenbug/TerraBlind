using Terraria;

namespace TerraBlind
{
	// Hand, survival reflex: a horizontal, always-on frame-level loop that keeps the bot ALIVE while any main action
	// (mine/chop/etc.) is running, the brain (LLM) is seconds too slow to react to lava or a sudden hit. It does
	// "stay alive", NOT "tactics": jump out of lava, quick-heal when hurt. It does not chase or fight, that's the
	// brain's call once woken. Fires an "interrupted" event so the brain takes over for the actual decision.
	//
	// Runs every frame from PostUpdateEverything, independent of any single action (reflex is cross-cutting, not
	// baked into each action). Sets controls directly, same as the coordinators.
	public static class SurvivalReflex
	{
		// heal when HP falls below this fraction of max, low enough not to waste potions on chip damage.
		private const float HealFraction = 0.5f;
		// melee-on-contact reflex: DISABLED pending a proper combat design, the v1 swing was unreliable and
		// enemy handling deserves better than a slapdash reflex.
		private const bool MeleeReflexEnabled = false;
		private static bool _firedThisEmergency;   // one interrupt per danger episode, not per frame
		static float _prevPx = float.NaN, _prevPy;   // 上一帧位置,用来抓"一帧动了几百格"

		public static void Tick()
		{
			if (!AgentTools.ControlAllows(ControlBehavior.Survival, ControlChannels.All)) { AxisLock.Release(Owner); return; }
			var p = Main.LocalPlayer;
			if (p == null || !p.active || p.dead) { _firedThisEmergency = false; _prevPx = float.NaN; return; }

			// 一帧动了几百格 = 传送或碰撞崩了。把那一帧的真实数字打出来,猜不出来就量
			if (!float.IsNaN(_prevPx))
			{
				float dpx = p.position.X - _prevPx, dpy = p.position.Y - _prevPy;
				if (System.MathF.Abs(dpx) > 160f || System.MathF.Abs(dpy) > 160f)
					DiagLog.Write($"[warp] 一帧位移 dx={dpx:0.#}px({dpx / 16f:0.#}格) dy={dpy:0.#}px "
						+ $"vx={p.velocity.X:0.##} vy={p.velocity.Y:0.##} "
						+ $"w={p.width} h={p.height} "
						+ $"→({(int)(p.position.X / 16f)},{(int)(p.position.Y / 16f)})");
			}
			_prevPx = p.position.X; _prevPy = p.position.Y;

			bool touchingLava = TouchesLava(p);
			bool inLava = p.lavaWet || touchingLava;
			bool lowHp = p.statLife < p.statLifeMax2 * HealFraction;
			bool emergency = inLava || lowHp;

			if (inLava && RecedingNav.Active)
			{
				RecedingNav.LastStop = "lava_emergency";
				RecedingNav.Stop();
				MineCoordinator.Stop();
				p.controlUseItem = p.controlDown = false;
			}
			// 泡着就按跳,并把危险交给上层处理。
			// 【只在自己拿着 Jump 时按】-- 不然会和寻路派发的跳跃边打架
			if (inLava && AxisLock.Take(Owner, Ax.Jump, () => TouchesLava(Main.LocalPlayer)))
				p.controlJump = true;
			if (!touchingLava) AxisLock.Release(Owner);

			// HEAL: quick-heal respects potionDelay internally, so calling it every frame is safe (no-op on cooldown).
			if (inLava || lowHp)
				p.QuickHeal();

			// 够得着的敌人就挥两下。纯反射,不追不打策略,出了圈立刻松手
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

			// Wake the brain ONCE when an emergency begins, it decides the real response (flee where, fight, retreat).
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

		// 碰撞箱碰到岩浆没有。【一滴都算】 -- 零点几格的岩浆和满格一样能困住人,
		// 而 lavaWet 在那种深度下会是假的。扫碰撞箱盖到的每一格,LiquidAmount>0 就算碰上。
		static bool TouchesLava(Player p)
		{
			int x0 = (int)(p.position.X / 16f);
			int x1 = (int)((p.position.X + p.width - 1) / 16f);
			int y0 = (int)(p.position.Y / 16f);
			int y1 = (int)((p.position.Y + p.height - 1) / 16f);
			for (int x = x0; x <= x1; x++)
				for (int y = y0; y <= y1; y++)
				{
					if (!Predicates.InBounds(x, y)) continue;
					var t = Main.tile[x, y];
					if (t.LiquidAmount > 0 && t.LiquidType == Terraria.ID.LiquidID.Lava) return true;
				}
			return false;
		}

		const string Owner = "survival-reflex";



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
