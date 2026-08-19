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
		// 实测 34 根刚好打死(日志:第34根时剩35血),30 根必然打不完。
		// 45 根留出余量 —— 单价 1780 铜,合 8 金 1 银
		const int WantDynamite = 45;
		const int WalkAwayTiles = 80;
		const int DigDepth = 6;   // 往下最多挖这么深;再深人就够不着,也补不回来

		// 夜里 19:30~4:30 才回家。原版时间:白天 0~54000(4:30~19:30),夜里 0~32400
		public static bool IsNight() => !Main.dayTime;

		static int _houseWx, _houseWy;   // 房间内一格(火把那格)
		static int _bridgeDir;           // 桥往哪边延伸
		static int _frames;
		// 挖掉的格子,存【完整坐标】。原来只存行号、列共用一个 _dugCol,
		// 而向导每挖一格就移位,三格挖在三列上(日志:3931/3930/3929),补回来的只有一格
		static readonly System.Collections.Generic.List<(int x, int y)> _dug = new();
		// 站定挖洞时脚下那一行。向导掉下去之后【绝不能跟着他往下走】——
		// 日志:向导落到 1062,寻路算出 jump→(3470,1059) H=0 代价-4.6,人就跟着跳进坑里上不来了
		static int _deckRow = -1;

		public static bool Start(int houseWx, int houseWy, int bridgeDir, out string why)
		{
			why = "";
			if (Main.LocalPlayer == null) { why = "no_player"; return false; }
			_houseWx = houseWx; _houseWy = houseWy; _bridgeDir = bridgeDir >= 0 ? 1 : -1;
			_frames = 0; _dug.Clear(); _deckRow = -1;
			Outcome = "running"; Reason = "";
			Phase = Ph.WaitNight;
			DiagLog.Write($"[wof] start 房间({houseWx},{houseWy}) 桥方向={_bridgeDir}");
			return true;
		}

		public static void Stop() { if (Outcome == "running") Outcome = "stopped"; Phase = Ph.Idle; }

		// 桥有起伏,没有固定的一行 —— 从人当前高度往下找第一块站得住的地。
		// 找不到就是那一列没铺到(桥断了),交给上层报,别默默停在空中。
		const int DeckScan = 12;
		static int DeckRow(int cx, int fromCy)
		{
			for (int y = fromCy; y < fromCy + DeckScan; y++)
			{
				if (Predicates.IsLava(cx, y)) return -1;
				if (Predicates.IsGround(cx, y)) return y - 1;
			}
			return -1;
		}

		static void Fail(string r) { Outcome = "stuck"; Reason = r; Phase = Ph.Idle; DiagLog.Write($"[wof] STUCK {r}"); }
		static void Go(Ph next) { Phase = next; _frames = 0; DiagLog.Write($"[wof] → {next}"); }

		// 能不能跟他说话:照抄原版每帧那套(Player.cs:26297)——玩家中心±tileRange 的矩形
		// 和 NPC 碰撞箱相交。够不着的话 SetTalkNPC 当帧就被原版清掉,商店根本开不起来
		public static bool CanTalkTo(Player p, NPC npc)
		{
			var box = new Microsoft.Xna.Framework.Rectangle(
				(int)(p.position.X + p.width / 2 - Player.tileRangeX * 16),
				(int)(p.position.Y + p.height / 2 - Player.tileRangeY * 16),
				Player.tileRangeX * 16 * 2, Player.tileRangeY * 16 * 2);
			var nb = new Microsoft.Xna.Framework.Rectangle(
				(int)npc.position.X, (int)npc.position.Y, npc.width, npc.height);
			return box.Intersects(nb);
		}

		// Main.OpenShop 是 private 实例方法,照它做一遍(Main.cs:52557):
		// 爆破专家(type 38)对应 shopIndex 4(Main.cs:52218 那串分发)
		const int DemoShopIndex = 4;
		static void OpenShop(Player p, int npcIndex)
		{
			p.SetTalkNPC(npcIndex);
			Main.playerInventory = true;
			Main.npcChatText = "";
			Main.SetNPCShopIndex(1);
			Main.instance.shop[Main.npcShop].SetupShop(DemoShopIndex);
			DiagLog.Write($"[wof] 开爆破专家的商店 shopIndex={DemoShopIndex}");
		}

		// 买到的东西在鼠标上,得塞回背包才腾得出手继续买
		static bool StashMouse(Player p)
		{
			if (Main.mouseItem.IsAir) return true;
			Main.mouseItem = p.GetItem(Main.myPlayer, Main.mouseItem, Terraria.GetItemSettings.InventoryEntityToPlayerInventorySettings);
			return Main.mouseItem.IsAir;
		}

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

				// 走到【NPC 跟前】,不是走到火把那格 —— 火把在房顶,NPC 站地板上,
				// 够得着火把不等于够得着他,于是商店开不起来、Reach 又反复重启刷屏
				case Ph.GoToNpc:
				{
					int dn0 = NPC.FindFirstNPC(NPCID.Demolitionist);
					if (dn0 < 0) { Fail("爆破专家不见了"); return; }
					if (CanTalkTo(p, Main.npc[dn0])) { RecedingNav.Stop(); Go(Ph.Buy); return; }
					if (RecedingNav.Active) return;
					if (_frames > 60 * 300) { Fail("走不到爆破专家跟前"); return; }
					int tx = (int)(Main.npc[dn0].Center.X / 16f);
					int ty = (int)((Main.npc[dn0].position.Y + Main.npc[dn0].height - 2f) / 16f);
					DiagLog.Write($"[wof] 去找爆破专家({tx},{ty}) 人在({ActExecutor.OriginCx(p)},{ActExecutor.OriginCy(p)})");
					RecedingNav.Start(tx, ty, RecedingNav.Mode.Reach);
					return;
				}

				// 严格走商店:跟 NPC 对话 → 开他的货架 → 从货架上一件件买。
				// 价格问游戏要(GetItemExpectedPrice),不自己写死 —— 写死的数迟早和版本对不上
				case Ph.Buy:
				{
					int have = Predicates.Have(DynamiteId);
					if (have >= WantDynamite)
					{ if (!Main.mouseItem.IsAir) return; Main.playerInventory = false; p.SetTalkNPC(-1); Go(Ph.SwapGuide); return; }
					if (_frames > 60 * 120) { Fail($"买不到雷管,现有{have}/{WantDynamite}"); return; }

					int dn = NPC.FindFirstNPC(NPCID.Demolitionist);
					if (dn < 0) { Fail("爆破专家不见了"); return; }
					// 走远了就回去 —— 原版每帧会把够不着的 talkNPC 清掉
					if (!CanTalkTo(p, Main.npc[dn])) { DiagLog.Write("[wof] 离商人太远,回去"); Go(Ph.GoToNpc); return; }
					if (p.talkNPC != dn) { OpenShop(p, dn); return; }

					var shop = Main.instance.shop[Main.npcShop];
					if (shop == null || shop.item == null) { OpenShop(p, dn); return; }
					int slot = -1;
					for (int i = 0; i < shop.item.Length; i++)
						if (shop.item[i] != null && shop.item[i].type == DynamiteId && shop.item[i].stack > 0) { slot = i; break; }
					if (slot < 0) { Fail("爆破专家货架上没有雷管"); return; }

					// 鼠标上攒着的先塞进背包,不然买到 maxStack 就卡住
					if (!Main.mouseItem.IsAir)
					{
						if (ThrowItems.FreeSlots() < 1 && !StashMouse(p)) { Fail("背包满了,放不下买到的雷管"); return; }
						if (!StashMouse(p)) return;
						return;
					}
					p.GetItemExpectedPrice(shop.item[slot], out _, out long buyPrice);
					if (!p.CanAfford(buyPrice, shop.item[slot].shopSpecialCurrency))
					{ Fail($"钱不够(还差买第{have + 1}根雷管的钱),卖东西那套还没做"); return; }
					if (!p.BuyItem(buyPrice, shop.item[slot].shopSpecialCurrency)) { Fail("扣钱失败"); return; }

					// 照抄 ItemSlot.HandleShopSlot:复制一件、清掉商店标记、走 OnCreated
					var bought = shop.item[slot].Clone();
					bought.buyOnce = false; bought.isAShopItem = false;
					if (bought.shopSpecialCurrency != -1) { bought.shopSpecialCurrency = -1; bought.shopCustomPrice = null; }
					bought.stack = 1;
					bought.OnCreated(new Terraria.DataStructures.BuyItemCreationContext(Main.mouseItem, p.TalkNPC));
					if (Main.mouseItem.IsAir) Main.mouseItem = bought;
					else Main.mouseItem.stack++;
					if ((have + 1) % 10 == 0) DiagLog.Write($"[wof] 买到第{have + 1}根雷管 单价={buyPrice}");
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
						if (p.velocity.Y != 0f) return;               // 落地了才记桥面,半空记的是错的
						_deckRow = ActExecutor.OriginCy(p);
						DiagLog.Write($"[wof] 桥面记作{_deckRow}行,再往下不去");
						Go(Ph.BackToGuide);
						return;
					}
					if (_frames > 60 * 300) { Fail("走不开"); return; }
					// 【必须真站上桥面】。Reach 只要伸手够得着就算到,人常停在桥面上方三四行的
					// 空中(日志:goal=(3466,1049) 人=(3463,1046)),接下来挖向导脚下就够不着了。
					int dstCx = _houseWx + _bridgeDir * WalkAwayTiles;
					int deck = DeckRow(dstCx, ActExecutor.OriginCy(p));
					if (deck < 0) { Fail($"列{dstCx}底下找不到桥面"); return; }
					RecedingNav.Start(dstCx, deck, RecedingNav.Mode.Stand);
					return;
				}

				// 同样朝【向导本人】走:等下要挖他脚下,得先够得着那一格
				case Ph.BackToGuide:
				{
					int g0 = NPC.FindFirstNPC(NPCID.Guide);
					if (g0 < 0) { Go(Ph.WaitWof); return; }
					var gn0 = Main.npc[g0];
					int fx = (int)(gn0.Center.X / 16f);
					int fy0 = (int)((gn0.position.Y + gn0.height + 2f) / 16f);
					// 向导已经掉到桥面以下 = 活儿干完了,他正在往岩浆里落。
					// 【绝不跟下去】:下面没有回得来的路,人一跳就出不来
					if (_deckRow >= 0 && fy0 > _deckRow + 1)
					{ DiagLog.Write($"[wof] 向导掉到{fy0}行(桥面{_deckRow}),不追了,去补洞"); Go(Ph.Patch); return; }
					// 够得着【而且脚踏实地】才开工。只判够得着的话人会停在半空,一飘就出range,
					// 于是 DigUnder↔BackToGuide 来回跳(日志:8243/8332/8601)
					if (p.velocity.Y == 0f
						&& p.IsInTileInteractionRange(fx, fy0, Terraria.DataStructures.TileReachCheckSettings.Simple))
					{ RecedingNav.Stop(); Go(Ph.DigUnder); return; }
					if (RecedingNav.Active) return;
					if (_frames > 60 * 300) { Fail("回不到向导跟前"); return; }
					// 站到向导【旁边】那一列的桥面上:站他自己那列的话挖脚下会把自己也带下去
					int side = fx - _bridgeDir * 2;
					int sdeck = DeckRow(side, ActExecutor.OriginCy(p));
					if (sdeck < 0) { RecedingNav.Start(fx, fy0 - 1, RecedingNav.Mode.Reach); return; }
					RecedingNav.Start(side, sdeck, RecedingNav.Mode.Stand);
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
					if (_deckRow >= 0 && gy > _deckRow + 1)
					{ DiagLog.Write($"[wof] 向导已在{gy}行往下落,不挖了"); Go(Ph.Patch); return; }
					if (_frames > 60 * 300) { Fail($"挖不动向导脚下({gx},{gy})"); return; }
					// 他会走动,走出伸手范围就先追上去,别对着够不着的格子空挥
					if (!p.IsInTileInteractionRange(gx, gy, Terraria.DataStructures.TileReachCheckSettings.Simple))
					{ Go(Ph.BackToGuide); return; }
					// 向导走到人自己脚底下那一列了:挖下去等于拆自己站的地,人跟着一起掉。
					// 先挪开一格再挖 —— 挪位由 SettleAt 落定,别在这儿硬挖
					var (mbl, mbr) = Predicates.BodyCols(p);
					if (gx >= mbl && gx <= mbr)
					{
						if (SettleAt.IsRunning) return;
						int away = gx - _bridgeDir * 2;
						if (_frames % 60 == 1) DiagLog.Write($"[wof] 向导({gx})在人脚下(身{mbl}..{mbr}),先让到{away}");
						SettleAt.Start(away, out _);
						return;
					}
					if (ItemUseCoordinator.IsActive) return;
					// 一路往下挖到岩浆:脚下常有寻路自己铺的平台,只挖 3 格的话向导落在平台上就卡住了。
					// 但【只挖够得着的】—— 够不着的挖不动,而且补的时候也回不去,那洞就永远留着
					for (int k = 0; k < DigDepth; k++)
					{
						int dy = gy + k;
						if (Predicates.IsLava(gx, dy)) break;          // 到岩浆了,下面不用管
						if (!Main.tile[gx, dy].HasTile) continue;      // 空的跳过,继续往下找
						if (!p.IsInTileInteractionRange(gx, dy, Terraria.DataStructures.TileReachCheckSettings.Simple))
						{
							if (_frames % 120 == 1) DiagLog.Write($"[wof] ({gx},{dy})够不着,不挖了 —— 再深就补不回来");
							break;
						}
						// 平台也要挖:ClearWay.Dig 认为平台不挡路,但它挡得住掉下去的向导
						if (Predicates.IsPlatform(gx, dy))
						{
							int pk2 = ClearWay.PickSlot(p);
							if (pk2 < 0) { Fail("要挖平台但没镐"); return; }
							ItemUseCoordinator.Start(new ItemUseRequest { TargetWx = gx, TargetWy = dy, Slot = pk2, Strict = true });
							DiagLog.Write($"[wof] 挖({gx},{dy}) 平台挡着向导");
						}
						else if (!ClearWay.Dig(p, gx, dy, "捅向导")) return;
						if (!_dug.Contains((gx, dy))) _dug.Add((gx, dy));
						return;
					}
					return;
				}

				// 补回缺口:桥面不能留洞,不然回头走这儿会掉进去
				case Ph.Patch:
				{
					if (PlaceAnywhere.IsRunning) return;
					if (_dug.Count == 0) { Go(Ph.WaitWof); return; }
					// 先补【离人最近】的那格:从最远的补起,人得走过去,而脚下的洞还没补,容易掉下去
					int pick = 0, pbest = int.MaxValue;
					int pcx = ActExecutor.OriginCx(p), pcy = ActExecutor.OriginCy(p);
					for (int i = 0; i < _dug.Count; i++)
					{
						int d2 = System.Math.Abs(_dug[i].x - pcx) + System.Math.Abs(_dug[i].y - pcy);
						if (d2 < pbest) { pbest = d2; pick = i; }
					}
					var (px2, py2) = _dug[pick];
					if (Predicates.IsSolid(px2, py2)) { _dug.RemoveAt(pick); return; }
					if (_frames > 60 * 300) { Fail($"补不回({px2},{py2}),还剩{_dug.Count}格"); return; }
					if (_dug.Count > 0 && _frames % 120 == 1) DiagLog.Write($"[wof] 补洞({px2},{py2}) 还剩{_dug.Count}格");
					int bid = DeckBuilder.PickBlock();
					if (bid < 0) { DiagLog.Write("[wof] 没方块补洞了,先放着"); Go(Ph.WaitWof); return; }
					// PlaceAnywhere 够不着会自己走过去,但行差太大它会认输 —— 那种洞补不回来,
					// 与其卡在这儿不如放掉,桥面已经通了(挖的是向导脚下那一列)
					if (System.Math.Abs(ActExecutor.OriginCy(p) - py2) > 4)
					{
						DiagLog.Write($"[wof] ({px2},{py2})太深补不回来,放掉");
						_dug.RemoveAt(pick);
						return;
					}
					PlaceAnywhere.Start(bid.ToString(), px2, py2, out _);
					return;
				}

				case Ph.WaitWof:
					if (NPC.AnyNPCs(NPCID.WallofFlesh))
					{
						// 肉山一出来就交给 WofFight:它自己会先等肉山进射程再开扔
						if (!WofFight.On) WofFight.Toggle();
						Outcome = "done"; Phase = Ph.Done;
						DiagLog.Write("[wof] 肉山出来了,交给 WofFight");
						Main.NewText("[TerraBlind] 肉山出来了,开打", 120, 255, 120);
						return;
					}
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
