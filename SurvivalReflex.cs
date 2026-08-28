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

			// 【不能只看 lavaWet】。零点几格的岩浆照样把人困住(行为和满格岩浆没区别),
			// 而那种深度下 lavaWet 可能已经是假的 -- 堤会提前停手,人还泡在里面出不来。
			// 判据改成:碰撞箱盖到的格子里【一滴岩浆都不许有】。
			bool touchingLava = TouchesLava(p);
			bool inLava = p.lavaWet || touchingLava;
			bool lowHp = p.statLife < p.statLifeMax2 * HealFraction;
			bool emergency = inLava || lowHp;

			// 泡着就按跳:岩浆里跳能抵消下沉,给放置争取时间。
			// 【只在自己拿着 Jump 时按】-- 不然会和寻路派发的跳跃边打架
			if (inLava && AxisLock.Take(Owner, Ax.Jump, () => TouchesLava(Main.LocalPlayer)))
				p.controlJump = true;
			// 垫脚下一格,别再往下沉。脱离岩浆就立刻放锁 —— 拿着不放寻路一步都走不了
			if (touchingLava) LavaLevee(p);
			else AxisLock.Release(Owner);

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

		// 岩浆里往脚下堤方块。一格一格垫,直到 lavaWet 消失。
		//
		// 放置本身走 PlaceAction(它管选槽/搬到热键栏/瞄准),按下去那一帧
		// Concessions.ClearLavaForPlacement 会抹掉目标格的液体 -- 没有它 vanilla 的
		// CheckLavaBlocking 会一路拒到底(方块是 tileSolid,连 tileLavaDeath 都问不到)。
		static int _leveeWait;

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

		const string Owner = "lava-levee";

		// 泡在岩浆里时【只做一件事:别再往下沉】--- 往脚下那一格放一块方块,踩住。
		//
		// 【不再填一片】。上一版扫碰撞箱盖到的所有岩浆格挨个填,人升一格、碰撞箱又盖到新的一格、
		// 接着填 --- 填出一个 5 列宽 6 行高的实心坨把自己封在里面(日志 57696~57735 连填 14 块)。
		// 封住之后寻路的边全被自己的砖挡死,泛洪只剩 1 格,A* 无目标,Commitment 在 1057<->1065
		// 之间无限往返。
		//
		// 现在只垫脚下一列:人踩住就不沉了,四周和头顶保持空的,剩下的交给寻路 ---
		// 规划器现在能在岩浆里放方块(EmitPlace 按格选料),岩浆和普通地形没区别了。
		static void LavaLevee(Player p)
		{
			if (PlaceAction.IsRunning) { _leveeWait = 0; return; }
			// 要 Use 也要 Move:放一格要 90 帧,期间寻路把人挪走就 out_of_reach 作废
			// (日志 29503 开填、29625 报"被挪开了",一块没放成)
			if (!AxisLock.Take(Owner, Ax.Use | Ax.Move, () => TouchesLava(Main.LocalPlayer)))
			{
				if (_leveeBlocked != AxisLock.Held(Ax.Move))
				{
					_leveeBlocked = AxisLock.Held(Ax.Move);
					DiagLog.Write($"[lava-levee] 等 {_leveeBlocked} 放开控制权 ({AxisLock.Dump()})");
				}
				return;
			}
			_leveeBlocked = "";
			if (_leveeWait > 0) { _leveeWait--; return; }

			int cx = ActExecutor.OriginCx(p), fy = ActExecutor.OriginCy(p) + 1;
			// 脚下已经踩实了就不用垫 --- 人不沉了,接下来是寻路的事
			if (!Predicates.InBounds(cx, fy)) return;
			if (Main.tile[cx, fy].HasTile) { AxisLock.Release(Owner); return; }
			// 方块的锚点比平台严(只认四邻实心)。没锚就放不上,与其对着空气挥手,
			// 不如放开控制权让寻路去找有锚的地方
			if (!MazeWand.BlockAnchor(cx, fy)) { AxisLock.Release(Owner); return; }

			int block = Unstick.BlockItem(p);
			if (block < 0)
			{
				if (!_leveeNoItem)
				{
					_leveeNoItem = true;
					DiagLog.Write("[lava-levee] 泡在岩浆里但身上一块方块都没有");
				}
				return;
			}
			_leveeNoItem = false;
			if (PlaceAction.Start(block.ToString(), cx, fy, 1, 0, 0, true, out string why))
			{
				_leveeWait = LeveeCooldown;
				DiagLog.Write($"[lava-levee] 垫脚下({cx},{fy}) item={block}");
			}
			else
				DiagLog.Write($"[lava-levee] 垫({cx},{fy})开不了工: {why}");
		}
		const int LeveeCooldown = 6;
		static bool _leveeNoItem;
		static string _leveeBlocked = "";

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
