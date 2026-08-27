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
			// 光跳出不来:竖井里四面都是岩浆,跳多高都落回原处。要【往脚下堤方块】一格格垫上去。
			// 只能用方块 -- 平台放进岩浆当场烧没,人以为踩上了其实还在往下沉。
			if (inLava) LavaLevee(p); else _leveeCol = int.MinValue;

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
		static int _leveeCol = int.MinValue;   // 认准一列往上堤;每帧重挑列会左右横跳堤不起来
		static int _leveeWait;

		static void LavaLevee(Player p)
		{
			// 别和正在跑的放置抢:PlaceAction 一次只服务一个目标,抢了两边都放不成
			if (PlaceAction.IsRunning) { _leveeWait = 0; return; }
			// 放完到方块真正出现之间有几帧,不等就会对着同一格连开好几枪
			if (_leveeWait > 0) { _leveeWait--; return; }

			int cx = ActExecutor.OriginCx(p), cy = ActExecutor.OriginCy(p);
			if (_leveeCol == int.MinValue) _leveeCol = cx;
			// 人沉下去/被冲开时跟着走,不然会对着够不着的老列一直放
			if (System.Math.Abs(cx - _leveeCol) > 1) _leveeCol = cx;

			int fy = cy + 1;
			if (!Predicates.InBounds(_leveeCol, fy)) return;
			// 脚下已经实了还在岩浆里 = 埋在液面下,得往上爬而不是继续往下堆。
			// 跳那一帧身子会抬起来,下一帧的 cy 就变了,自然堤到新的一格
			if (Main.tile[_leveeCol, fy].HasTile) return;

			int block = Unstick.BlockItem(p);
			if (block < 0)
			{
				if (!_leveeNoItem)
				{
					_leveeNoItem = true;
					DiagLog.Write("[lava-levee] 泡在岩浆里但身上一块方块都没有,堤不起来");
				}
				return;
			}
			_leveeNoItem = false;
			if (PlaceAction.Start(block.ToString(), _leveeCol, fy, 1, 0, 0, true, out string why))
			{
				_leveeWait = LeveeCooldown;
				DiagLog.Write($"[lava-levee] 往脚下({_leveeCol},{fy})堤一块 item={block}");
			}
			else
				DiagLog.Write($"[lava-levee] 堤({_leveeCol},{fy})开不了工: {why}");
		}
		const int LeveeCooldown = 6;    // 放下到方块出现的间隔,比一次挥舞略长
		static bool _leveeNoItem;       // 没料只报一次,别每帧刷屏

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
