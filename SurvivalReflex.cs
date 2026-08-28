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

			// LAVA: jump to climb out. controlJump each frame while submerged pushes the player upward.
			if (inLava)
				p.controlJump = true;
			// 光跳出不来:竖井里四面都是岩浆,跳多高都落回原处。要【往脚下堤方块】一格格垫上去。
			// 只能用方块 -- 平台放进岩浆当场烧没,人以为踩上了其实还在往下沉。
			// 脱离岩浆就立刻放锁 —— 拿着不放的话寻路一步都走不了。
			// alive 回调(TouchesLava)是兜底,正常路径靠这一行
			if (touchingLava) LavaLevee(p);
			else { _leveeCol = int.MinValue; AxisLock.Release(Owner); }

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

		// 碰着人的岩浆格里【最低的那一格】。填它而不是填脚下那一列:
		// 碰上人的岩浆可能在隔壁列,只顾自己那列永远清不掉
		static (int x, int y)? NearestLavaCell(Player p)
		{
			int x0 = (int)(p.position.X / 16f);
			int x1 = (int)((p.position.X + p.width - 1) / 16f);
			int y0 = (int)(p.position.Y / 16f);
			int y1 = (int)((p.position.Y + p.height - 1) / 16f);
			float ccx = (p.position.X + p.width / 2f) / 16f;
			(int, int)? best = null; float bd = float.MinValue;
			for (int x = x0; x <= x1; x++)
				for (int y = y0; y <= y1; y++)
				{
					if (!Predicates.InBounds(x, y)) continue;
					var t = Main.tile[x, y];
					if (t.LiquidAmount == 0 || t.LiquidType != Terraria.ID.LiquidID.Lava) continue;
					if (t.HasTile) continue;   // 已经有东西还带液体的格填不进去,跳过
					// 【从下往上填】。把身子四周的岩浆格全填实等于把自己砌进砖里,
					// 而目标是"人不碰岩浆"不是"周围没岩浆" -- 填低处,人踩上去自然抬高脱离。
					// 所以排序先比行(越低越优先),同一行再比横向距离
					float d = (y * 1000f) - System.Math.Abs(x - ccx);
					if (best == null || d > bd) { bd = d; best = (x, y); }
				}
			return best;
		}

		const string Owner = "lava-levee";

		static void LavaLevee(Player p)
		{
			// 别和正在跑的放置抢:PlaceAction 一次只服务一个目标,抢了两边都放不成
			if (PlaceAction.IsRunning) { _leveeWait = 0; return; }
			// 【要 Use 也要 Move】。放一格要 90 帧,期间寻路把人挪走就 out_of_reach 作废 --
			// 日志 29503 开填、29625 报"被挪开了",列号 1171<->1175 来回跳一块没放成。
			// 所以连人的位置一起锁住,锁不到就这一帧不堤(下一帧再看,反正人无敌不怕泡着)
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
			// 放完到方块真正出现之间有几帧,不等就会对着同一格连开好几枪
			if (_leveeWait > 0) { _leveeWait--; return; }

			// 【填有岩浆的那一格,不是脚下那一格】。碰着人的岩浆可能在隔壁列,
			// 只顾自己这列会一直填不到点子上;而一格填满(方块占位)那格就再也没有液体了。
			// 从人身上往外挑最近的一格有岩浆的,由近及远
			var target = NearestLavaCell(p);
			if (target == null) return;
			int tx = target.Value.x, fy = target.Value.y;
			_leveeCol = tx;
			// 只在够得着的范围里堤。够不着说明人已经浮上来了,交给跳
			if (!p.IsInTileInteractionRange(tx, fy, Terraria.DataStructures.TileReachCheckSettings.Simple)) return;

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
			if (PlaceAction.Start(block.ToString(), tx, fy, 1, 0, 0, true, out string why))
			{
				_leveeWait = LeveeCooldown;
				DiagLog.Write($"[lava-levee] 填岩浆格({tx},{fy}) item={block}");
			}
			else
				DiagLog.Write($"[lava-levee] 填({tx},{fy})开不了工: {why}");
		}
		const int LeveeCooldown = 6;    // 放下到方块出现的间隔,比一次挥舞略长
		static bool _leveeNoItem;       // 没料只报一次,别每帧刷屏
		static string _leveeBlocked = "";   // 上一次是被谁挡的,变了才打日志

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
