using Terraria;
using Terraria.ID;

namespace TerraBlind
{
	// 桥搭完之后这一整套:等爆破专家自己传送回房 → 买雷管 → 换向导住进来 → 走远一个屏幕 →
	// 把向导捅进岩浆 → 等肉山。
	//
	// 每一相位的完成判据都是【世界里的事实】(NPC 在不在家、背包里有几根雷管、人走了多远),
	// 不是等帧数 —— 帧数在别人机器上就不对。
	public static class WofPrep
	{
		public enum Ph
		{
			Idle,
			WaitNight,     // 等入夜 + 人离开,让爆破专家自己传送回家(原版逻辑,我们不搬他)
			GoToNpc,       // 走到房子那头
			Buy,           // 买雷管
			SwapGuide,     // 踢掉爆破专家,把向导指进来
			WalkAway,      // 沿桥走开一个屏幕,让向导传送进房
			BackToGuide,   // 回到房子那头
			DigUnder,      // 挖向导脚下,让他掉岩浆
			Patch,         // 补回缺口
			WaitWof,       // 等肉山
			Done
		}
		public static Ph Phase = Ph.Idle;
		public static string Outcome = "idle", Reason = "";
		public static bool IsRunning => Phase != Ph.Idle && Phase != Ph.Done;

		const int DynamiteId = ItemID.Dynamite;
		const int WantDynamite = 30;
		// 5金34银 = 5*100*100 + 34*100(铜)
		const long DynamitePrice = 5L * 10000 + 34L * 100;
		const int WalkAwayTiles = 80;

		// 夜里 19:30~4:30 才回家。原版时间:白天 0~54000(4:30~19:30),夜里 0~32400
		public static bool IsNight() => !Main.dayTime;

		static int _houseWx, _houseWy;   // 房间内一格(火把那格)
		static int _bridgeDir;           // 桥往哪边延伸
		static int _frames;
		static int[] _dug = new int[3];  // 挖掉的格子(补回来用)
		static int _dugN, _dugCol;

		public static bool Start(int houseWx, int houseWy, int bridgeDir, out string why)
		{
			why = "";
			if (Main.LocalPlayer == null) { why = "no_player"; return false; }
			_houseWx = houseWx; _houseWy = houseWy; _bridgeDir = bridgeDir >= 0 ? 1 : -1;
			_frames = 0; _dugN = 0;
			Outcome = "running"; Reason = "";
			Phase = Ph.WaitNight;
			DiagLog.Write($"[wof] start 房间({houseWx},{houseWy}) 桥方向={_bridgeDir}");
			return true;
		}

		public static void Stop() { if (Outcome == "running") Outcome = "stopped"; Phase = Ph.Idle; }

		static void Fail(string r) { Outcome = "stuck"; Reason = r; Phase = Ph.Idle; DiagLog.Write($"[wof] STUCK {r}"); }
		static void Go(Ph next) { Phase = next; _frames = 0; DiagLog.Write($"[wof] → {next}"); }

		// 那只 NPC 在不在家里(判"传送回来了没有")
		static bool AtHome(int type)
		{
			int n = NPC.FindFirstNPC(type);
			if (n < 0) return false;
			var npc = Main.npc[n];
			if (npc.homeless) return false;
			return System.Math.Abs((int)(npc.Center.X / 16f) - npc.homeTileX) <= 8
			    && System.Math.Abs((int)(npc.Center.Y / 16f) - npc.homeTileY) <= 8;
		}

		public static void Tick()
		{
			if (!IsRunning) return;
			var p = Main.LocalPlayer;
			if (p == null || !p.active) { Fail("no_player"); return; }
			_frames++;

			switch (Phase)
			{
				// 不自己搬 NPC:原版每帧判"不在好休息点 + 玩家看不见"就把他传送回家。
				// 我们只要满足条件然后等 —— 人此刻在桥的远端,本来就离得远
				case Ph.WaitNight:
					if (!IsNight()) { if (_frames % 300 == 1) DiagLog.Write("[wof] 等天黑"); return; }
					if (!AtHome(NPCID.Demolitionist))
					{ if (_frames % 300 == 1) DiagLog.Write("[wof] 天黑了,等爆破专家自己回家"); return; }
					Go(Ph.GoToNpc);
					return;

				case Ph.GoToNpc:
				{
					if (RecedingNav.Active) return;
					int px = ActExecutor.OriginCx(p);
					if (System.Math.Abs(px - _houseWx) <= 3) { Go(Ph.Buy); return; }
					if (_frames > 60 * 300) { Fail("走不到房子那头"); return; }
					RecedingNav.Start(_houseWx, _houseWy + 1, RecedingNav.Mode.Reach);
					return;
				}

				case Ph.Buy:
				{
					int have = Predicates.Have(DynamiteId);
					if (have >= WantDynamite) { Go(Ph.SwapGuide); return; }
					if (ThrowItems.FreeSlots() < 1) ThrowItems.MakeRoom(1);
					if (!p.CanAfford(DynamitePrice))
					{ Fail($"钱不够买{WantDynamite}雷管(要 5金34银),卖东西那套还没做"); return; }
					if (!p.BuyItem(DynamitePrice)) { Fail("扣钱失败"); return; }
					var it = new Item();
					it.SetDefaults(DynamiteId);
					it.stack = WantDynamite;
					p.QuickSpawnClonedItem(p.GetSource_Misc("terrablind_buy"), it, WantDynamite);
					DiagLog.Write($"[wof] 买了{WantDynamite}雷管,原有{have}");
					return;
				}

				case Ph.SwapGuide:
				{
					int d = NPC.FindFirstNPC(NPCID.Demolitionist);
					if (d >= 0 && !Main.npc[d].homeless) { WorldGen.kickOut(d); DiagLog.Write("[wof] 踢掉爆破专家"); return; }
					if (!AssignHome.Try(NPCID.Guide, _houseWx, _houseWy, out string gw)) { Fail($"向导住不进来:{gw}"); return; }
					Go(Ph.WalkAway);
					return;
				}

				// 走开一个屏幕,原版才肯把向导传送回家 —— 传送要求 NPC 和家都不在玩家视野内
				case Ph.WalkAway:
				{
					if (RecedingNav.Active) return;
					int px = ActExecutor.OriginCx(p);
					if (System.Math.Abs(px - _houseWx) >= WalkAwayTiles)
					{
						if (!AtHome(NPCID.Guide))
						{ if (_frames % 300 == 1) DiagLog.Write("[wof] 已走远,等向导传送回家"); return; }
						Go(Ph.BackToGuide);
						return;
					}
					if (_frames > 60 * 300) { Fail("走不开"); return; }
					RecedingNav.Start(_houseWx + _bridgeDir * WalkAwayTiles, _houseWy, RecedingNav.Mode.Reach);
					return;
				}

				case Ph.BackToGuide:
				{
					if (RecedingNav.Active) return;
					if (System.Math.Abs(ActExecutor.OriginCx(p) - _houseWx) <= 3) { Go(Ph.DigUnder); return; }
					if (_frames > 60 * 300) { Fail("回不到房子"); return; }
					RecedingNav.Start(_houseWx, _houseWy + 1, RecedingNav.Mode.Reach);
					return;
				}

				// 向导站哪是他自己走出来的,不能预先算死 —— 每帧读他真实位置,挖【他脚下】那一列
				case Ph.DigUnder:
				{
					int g = NPC.FindFirstNPC(NPCID.Guide);
					if (g < 0) { Go(Ph.WaitWof); return; }          // 已经没了 = 掉下去了
					var gn = Main.npc[g];
					int gx = (int)(gn.Center.X / 16f);
					int gy = (int)((gn.position.Y + gn.height + 2f) / 16f);
					if (Predicates.IsLava(gx, gy) || gn.life <= 0) { Go(Ph.Patch); return; }
					if (_frames > 60 * 300) { Fail($"挖不动向导脚下({gx},{gy})"); return; }
					if (ItemUseCoordinator.IsActive) return;
                    // 记下挖了哪些,等下要原样补回去
					if (_dugN == 0) _dugCol = gx;
					for (int k = 0; k < 3; k++)
					{
						int dy = gy + k;
						if (!Predicates.IsSolid(gx, dy)) continue;
						if (!ClearWay.Dig(p, gx, dy, "捅向导")) return;
						if (_dugN < 3) _dug[_dugN++] = dy;
						return;
					}
					return;
				}

				// 补回缺口:桥面不能留洞,不然回头走这儿会掉进去
				case Ph.Patch:
				{
					if (PlaceAnywhere.IsRunning) return;
					if (_dugN == 0) { Go(Ph.WaitWof); return; }
					int dy2 = _dug[_dugN - 1];
					if (Predicates.IsSolid(_dugCol, dy2)) { _dugN--; return; }
					if (_frames > 60 * 300) { Fail($"补不回({_dugCol},{dy2})"); return; }
					int bid = DeckBuilder.PickBlock();
					if (bid < 0) { DiagLog.Write("[wof] 没方块补洞了,先放着"); Go(Ph.WaitWof); return; }
					PlaceAnywhere.Start(bid.ToString(), _dugCol, dy2, out _);
					return;
				}

				case Ph.WaitWof:
					if (NPC.AnyNPCs(NPCID.WallofFlesh))
					{ Outcome = "done"; Phase = Ph.Done; DiagLog.Write("[wof] 肉山出来了"); Main.NewText("[TerraBlind] 肉山出来了", 120, 255, 120); return; }
					if (_frames % 300 == 1) DiagLog.Write("[wof] 等肉山");
					if (_frames > 60 * 120) { Fail("等了2分钟没出肉山 —— 向导可能没死在岩浆里"); return; }
					return;

				default:
					Fail($"相位没实现:{Phase}");
					return;
			}
		}
	}
}
