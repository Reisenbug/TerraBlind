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
		// 【买不够就有几根用几根】,但少于这个数一定打不死 —— 那就别往下走了,
		// 换向导捅岩浆那一整套白干,还得重来。20 以上是有概率的,值得试
		const int MinDynamite = 20;
		const int WalkAwayTiles = 80;
		// 往下最多挖这么深。【6 是 4 格手臂时代的数】:这一段开着 30 格手臂,
		// 够不着由循环里的 Reach.CanMine 逐格挡,深度上限不该再当第二道闸
		const int DigDepth = 24;
		// 向导要掉过洞口这么多行才开始补。补早了等于给他垫块地,他就再也下不去了
		const int PatchClear = 8;
		const int PatchWait = 60 * 10;   // 最多等他掉 10 秒。再不掉就照补,别把窟窿永远留着

		// 夜里 19:30~4:30 才回家。原版时间:白天 0~54000(4:30~19:30),夜里 0~32400
		public static bool IsNight() => !Main.dayTime;

		static int _houseWx, _houseWy;   // 房间内一格(火把那格)
		static int _bridgeDir;           // 桥往哪边延伸
		static int _frames;
		static int _nightAt;   // 天黑那一帧。等天黑不计时,天黑之后等 NPC 才计时
		static int _lastHave = -1, _buyIdle;   // 买雷管的进展:根数或钱变了就算有进展
		static long _lastMoney = -1;
		// 原版 NPC 传送回家要"玩家看不见 + 不在好休息点",几秒一次判定;给足一分钟
		const int NightWaitFrames = 60 * 60;
		// 挖掉的格子,存【完整坐标】。原来只存行号、列共用一个 _dugCol,
		// 而向导每挖一格就移位,三格挖在三列上(日志:3931/3930/3929),补回来的只有一格
		static readonly System.Collections.Generic.List<(int x, int y)> _dug = new();
		// 站定挖洞时脚下那一行。向导掉下去之后【绝不能跟着他往下走】——
		// 日志:向导落到 1062,寻路算出 jump→(3470,1059) H=0 代价-4.6,人就跟着跳进坑里上不来了

		public static bool Start(int houseWx, int houseWy, int bridgeDir, out string why)
		{
			why = "";
			if (Main.LocalPlayer == null) { why = "no_player"; return false; }
			_houseWx = houseWx; _houseWy = houseWy; _bridgeDir = bridgeDir >= 0 ? 1 : -1;
			_frames = 0; _nightAt = 0; _dug.Clear();
			_lastHave = -1; _lastMoney = -1; _buyIdle = 0;
			Outcome = "running"; Reason = "";
			Phase = Ph.WaitNight;
			DiagLog.Write($"[wof] start 房间({houseWx},{houseWy}) 桥方向={_bridgeDir}");
			return true;
		}

		// 外部叫停也要还手臂 —— 不然停在 DigUnder 那一刻,30 格就永远留在全局里了
		public static void Stop() { Concessions.LongArmEnd(); if (Outcome == "running") Outcome = "stopped"; Phase = Ph.Idle; }

		// 桥有起伏,没有固定的一行 —— 从人当前高度往下找第一块站得住的地。
		// 找不到就是那一列没铺到(桥断了),交给上层报,别默默停在空中。
		const int DeckScan = 12;
		// 向导是不是【真的掉到桥面以下】了。
		//
		// 【必须按他自己那一列的桥面判】。原来用 _deckRow —— 那是人走远 80 格之后
		// 在【那边】记的行号,而桥是有坡的:房子在 1042、远处的桥面在 1040,差 2 行。
		// 于是向导好端端站在自家地板上(1042)就被当成"掉下去了",DigUnder 整个跳过,
		// 人干等肉山永远不出(现场:5787 向导掉到1042行(桥面1040),不追了)。
		// 向导是不是【真的在往下掉】。
		//
		// 【必须看他本人在不在动,不能只看地板还在不在】。挖开的那一帧他那一列往下全空,
		// DeckRow 返回 -1 —— 当帧就判"掉了"转去补洞,而向导还稳稳站在原地(vanilla 要
		// 下一帧才给他重力),洞一补上他就再也掉不下去了,人干等一个永远不来的肉山。
		//
		// 判据:他在下落(velocity.Y > 0) 或者 已经落到桥面以下。两个都不成立就是还没走。
		static bool GuideFell(NPC gn)
		{
			int gx = (int)(gn.Center.X / 16f);
			int gy = (int)((gn.position.Y + gn.height + 2f) / 16f);
			// 【有向下速度不算掉下去】。挖开那一帧他必然有向下的速度,而脚下常常还有第二层平台:
			// 现场他从1052掉到1053就踩住了(1053那格是平台19),代码却判"成了"收手去补洞,
			// 肉山永远不来。成功只有一种:他到岩浆了
			return Predicates.IsLava(gx, gy);
		}

		// 上下各扫 DeckWide 行,取离 nearCy 最近的那个桥面
		static int DeckRowNear(int cx, int nearCy)
		{
			for (int d = 0; d <= DeckWide; d++)
			{
				if (Predicates.IsGround(cx, nearCy + d) && !Predicates.IsLava(cx, nearCy + d)) return nearCy + d - 1;
				if (d > 0 && Predicates.IsGround(cx, nearCy - d) && !Predicates.IsLava(cx, nearCy - d)) return nearCy - d - 1;
			}
			return -1;
		}
		const int DeckWide = 40;

		static int DeckRow(int cx, int fromCy)
		{
			for (int y = fromCy; y < fromCy + DeckScan; y++)
			{
				if (Predicates.IsLava(cx, y)) return -1;
				if (Predicates.IsGround(cx, y)) return y - 1;
			}
			return -1;
		}

		// 向导碰撞箱压着的列。他跨 2~3 列,挖的时候【每一列都要挖】,
		// 所以站位和"够不够得着"的判据也必须按这几列算,不能只看中心列
		static (int l, int r) GuideCols(NPC gn) => Predicates.TouchCols(gn.position.X, gn.width);

		// 挖向导脚下时的心跳:每帧无声 return 的话,日志里只剩一片空白,连卡在哪都看不出来
		static int _overlapFrames;
		const int OverlapWait = 60;   // 重合满 1 秒才让位
		static string _digWhere = "";
		static int _digBeat;
		static void DigBeat(Player p, int gx, int gy, string where)
		{
			if (where != _digWhere) { _digWhere = where; _digBeat = 0; return; }
			if (++_digBeat % 60 != 0) return;
			// 【三列都要报】。只报中心那列的话,"中心全空了他还不掉"看着像见鬼,
			// 其实是左右两列还撑着他
			var sb = new System.Text.StringBuilder();
			int bg = NPC.FindFirstNPC(NPCID.Guide);
			int cl = gx, cr = gx;
			if (bg >= 0) { var bc = Predicates.TouchCols(Main.npc[bg].position.X, Main.npc[bg].width); cl = bc.left; cr = bc.right; }
			for (int dx = cl; dx <= cr; dx++)
			{
				sb.Append($" 列{dx}:");
				for (int k = 0; k < DigDepth; k++)
				{
					int dy = gy + k;
					var t = Main.tile[dx, dy];
					sb.Append($"{(t.HasTile ? t.TileType.ToString() : "空")}");
					if (t.HasTile && !Reach.CanMine(p, dx, dy)) sb.Append("!");
					sb.Append(',');
				}
			}
			DiagLog.Write($"[wof] 挖洞心跳 {_digBeat}帧都在\"{where}\" 向导({gx},{gy}) 人({ActExecutor.OriginCx(p)},{ActExecutor.OriginCy(p)}) " +
				$"已挖{_dug.Count}格 talk={p.talkNPC}{sb}");
		}

		static void Fail(string r) { Concessions.LongArmEnd(); Outcome = "stuck"; Reason = r; Phase = Ph.Idle; DiagLog.Write($"[wof] STUCK {r}"); }

		// 买完的收尾:鼠标上还攥着东西就等一帧(不然那件会掉地上),关背包、结束对话、进下一相位。
		// 【买够了】和【钱花光了】都走这一条 —— 各写一遍必然漂移
		static bool DoneBuying(Player p)
		{
			// 【最后一根还在鼠标上就把它收进背包】。Predicates.Have 是算鼠标那份的,
			// 所以买到第 45 根(44 在背包 + 1 在手上)当帧就判"够了"进这里 —— 而上面
			// 那段 StashMouse 在 have>=Want 时根本走不到。原来这里只 return false 不干活,
			// 于是每帧进来一次、每帧退回去,买也不买、走也不走,要人手动把那根拖回背包才继续
			if (!Main.mouseItem.IsAir)
			{
				if (ThrowItems.FreeSlots() < 1) KeepList.MakeRoom(2);
				if (!StashMouse(p))
				{
					if (_frames % 120 == 1)
						DiagLog.Write($"[wof] 最后一根雷管收不进背包(空格{ThrowItems.FreeSlots()}),等腾位置");
					return false;
				}
			}
			Main.playerInventory = false;
			p.SetTalkNPC(-1);
			DiagLog.Write($"[wof] 买完了,共{Predicates.Have(DynamiteId)}根雷管");
			Go(Ph.SwapGuide);
			return true;
		}
		// 【超长手臂只属于 DigUnder+Patch】。它改的是 static tileRangeX,全局都读得到,
		// 漏还一次整个寻路就以为手能伸 30 格。所以在这里统一还 —— 出口有六个,
		// 一个个加迟早漏,以后再添分支也不用记得这回事
		static void Go(Ph next)
		{
			// 【等向导死了再收手臂】。原来只留 DigUnder/Patch,而 BackToGuide 是"回去站好接着挖"
			// 的中间站 --- 一转过去手臂就收了,人站 1180、剩下那格 (1185,1051) 差 5 列,
			// 5 格手臂正好够不着,那格永远挖不掉。捅向导整段都该开着
			if (next == Ph.WaitWof || next == Ph.Done || next == Ph.Idle) Concessions.LongArmEnd();
			Phase = next; _frames = 0; _nightAt = 0; _digWhere = ""; _digBeat = 0; _overlapFrames = 0;
			DiagLog.Write($"[wof] → {next}");
		}

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
					// 【等天黑不设上限】。NPC 只在夜里传送回家,白天等多久都是白等 ——
					// 这是流程本身要等的时间,不是卡住。计时从天黑那一刻才起算
					if (!IsNight())
					{
						_nightAt = 0;
						if (_frames % 300 == 1) DiagLog.Write("[wof] 等天黑");
						return;
					}
					if (_nightAt == 0) { _nightAt = _frames; DiagLog.Write("[wof] 天黑了,开始等爆破专家回家"); }
					if (!AtHome(NPCID.Demolitionist))
					{
						// 【天黑之后才计时】。等不到有两种,原因完全不同,别混成一句"超时":
						//   NPC 不在世界里  -> 等一辈子也不会有,当场认账
						//   有 NPC 但 homeless -> 房子不合格(家具/光源不齐),他没家可传
						int dn1 = NPC.FindFirstNPC(NPCID.Demolitionist);
						if (dn1 < 0) { Fail("世界里没有爆破专家,他不会自己出现"); return; }
						if (_frames - _nightAt > NightWaitFrames)
						{
							Fail(Main.npc[dn1].homeless
								? "爆破专家没家(房子不合格:家具或光源不齐),传不回来"
								: $"天黑{NightWaitFrames / 60}秒了爆破专家还没回家(他在{(int)(Main.npc[dn1].Center.X / 16f)},{(int)(Main.npc[dn1].Center.Y / 16f)},家在{Main.npc[dn1].homeTileX},{Main.npc[dn1].homeTileY})");
							return;
						}
						if (_frames % 300 == 1)
							DiagLog.Write($"[wof] 天黑了,等爆破专家回家 {(_frames - _nightAt) / 60}/{NightWaitFrames / 60}秒 homeless={Main.npc[dn1].homeless}");
						return;
					}
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
					// 【寻路那把尺子不是搭话那把】。Mode.Reach 按方块的交互距离判"够到了"就停,
					// 而搭话判的是人和 NPC 两个矩形相交 --- 停在刚好差一点的位置时,
					// 重发寻路会被同目标守卫挡掉,原地耗到超时。停下了就自己朝他走,判据用 CanTalkTo
					if (p.velocity.Y == 0f && RecedingNav.LastStop == "done")
					{
						float pcx = p.position.X + p.width / 2f, ncx = Main.npc[dn0].Center.X;
						if (System.MathF.Abs(pcx - ncx) > 8f)
						{
							if (ncx > pcx) p.controlRight = true; else p.controlLeft = true;
							if (_frames % 60 == 1)
								DiagLog.Write($"[wof] 寻路到了但搭不上话,朝爆破专家挪 人x={pcx:0} 他x={ncx:0}");
							return;
						}
						// 横着已经贴上了还搭不上话 = 差在竖直方向,横走救不了,交回寻路重来一趟
						if (_frames % 120 == 1)
							DiagLog.Write($"[wof] 横向贴上了仍搭不上话(差在高度) 人y={p.Center.Y:0} 他y={Main.npc[dn0].Center.Y:0}");
					}
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
					// 【超时判据要排在"买够了"前面】。收尾那步(把最后一根从鼠标收回背包)
					// 也可能卡住,排后面的话它每帧 return,超时永远轮不到,又是无声死循环
					if (have != _lastHave || Shop.Money(p) != _lastMoney)
					{ _lastHave = have; _lastMoney = Shop.Money(p); _buyIdle = 0; }
					else _buyIdle++;
					if (_buyIdle > 60 * 30)
					{ Fail($"买雷管卡住30秒,现有{have}/{WantDynamite} 钱{Shop.Coins(Shop.Money(p))} 鼠标={(Main.mouseItem.IsAir ? "空" : Main.mouseItem.Name)} 空格{ThrowItems.FreeSlots()}"); return; }
					if (have >= WantDynamite) { if (!DoneBuying(p)) return; return; }

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
						// 满了先按清单删掉没用的(草药/矿石那些)。原来直接 Fail,而背包里
						// 多半全是一路捡的杂物 —— 45 根雷管就卡在这儿买不成
						if (ThrowItems.FreeSlots() < 1) KeepList.MakeRoom(2);
						if (ThrowItems.FreeSlots() < 1 && !StashMouse(p)) { Fail("背包满了,放不下买到的雷管"); return; }
						if (!StashMouse(p)) return;
						return;
					}
					p.GetItemExpectedPrice(shop.item[slot], out _, out long buyPrice);
					if (!p.CanAfford(buyPrice, shop.item[slot].shopSpecialCurrency))
					{
						// 钱不够就【当场卖东西】—— 商店已经开着,不用另走一趟。
						// 一帧只卖一格,卖完 return 等下一帧重新算够不够
						if (shop.item[slot].shopSpecialCurrency != -1)
						{ Fail($"第{have + 1}根雷管要特殊货币,买不了"); return; }
						// 还要买 (WantDynamite-have) 根,一次把总账算够,省得卖一件买一根来回折腾
						long need = buyPrice * (WantDynamite - have);
						if (Shop.SellOneFor(p, need, out string sn, DynamiteId)) { DiagLog.Write($"[wof] {sn}"); return; }
						// 【卖不出更多了 —— 有几根用几根】。够门槛就往下走,不够就当场认账:
						// 少于 MinDynamite 一定打不死,换向导捅岩浆那一套白干还得重来
						if (have >= MinDynamite)
						{
							DiagLog.Write($"[wof] 钱凑不齐了,只买到{have}根(想要{WantDynamite}),够{MinDynamite}根门槛,接着打");
							if (!DoneBuying(p)) return;
							return;
						}
						Fail($"只买到{have}根雷管(至少要{MinDynamite}根才打得死),{sn}"); return;
					}
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
						DiagLog.Write($"[wof] 走开到{ActExecutor.OriginCy(p)}行,向导已回家,回去捅他");
						Go(Ph.BackToGuide);
						return;
					}
					if (_frames > 60 * 300) { Fail("走不开"); return; }
					// 【必须真站上桥面】。Reach 只要伸手够得着就算到,人常停在桥面上方三四行的
					// 空中(日志:goal=(3466,1049) 人=(3463,1046)),接下来挖向导脚下就够不着了。
					int dstCx = _houseWx + _bridgeDir * WalkAwayTiles;
					// 【桥不是平的】。这一趟桥从 1056 一路爬到 1043,走开 80 格那一列的桥面
					// 比人所在的行高 6 行 --- 只往下扫永远扫不到,当场判死。以人为中心上下都扫。
					int deck = DeckRowNear(dstCx, ActExecutor.OriginCy(p));
					if (deck < 0) { Fail($"列{dstCx}附近找不到桥面"); return; }
					RecedingNav.Start(dstCx, deck, RecedingNav.Mode.Stand);
					return;
				}

				// 走到【单间房靠桥那头的最里面一格】站定,然后把手臂加长到 30 格。
				// 不再算站位:30 格的手臂从这儿够得着向导脚下的每一列,
				// 原来那套"扫一圈找能覆盖三列的桥面格"是给 4 格手臂擦屁股的,已经删了
				case Ph.BackToGuide:
				{
					int g0 = NPC.FindFirstNPC(NPCID.Guide);
					if (g0 < 0) { Go(Ph.WaitWof); return; }
					var gn0 = Main.npc[g0];
					int fy0 = (int)((gn0.position.Y + gn0.height + 2f) / 16f);
					// 向导已经掉到桥面以下 = 活儿干完了,他正在往岩浆里落。
					// 【绝不跟下去】:下面没有回得来的路,人一跳就出不来
					if (GuideFell(gn0))
					{ DiagLog.Write($"[wof] 向导掉到{fy0}行(他那列的桥面在下面没了),不追了,去补洞"); Go(Ph.Patch); return; }
					int sx = HouseBuilder.FarWx, sy = HouseBuilder.FarWy;
					// 【站稳了才算到】。半空中量距离,一落地就变了
					if (p.velocity.Y == 0f && ActExecutor.OriginCx(p) == sx)
					{
						RecedingNav.Stop();
						Concessions.LongArmBegin();
						Go(Ph.DigUnder); return;
					}
					if (RecedingNav.Active) return;
					if (_frames > 60 * 300) { Fail($"走不到房子最右格({sx},{sy})"); return; }
					// 【只在没跑的时候发一次】。每帧无条件 Start 会把建场任务每帧撕掉重建,
					// 屏幕刷满 building field,人一步不动(日志 6413~6857 每帧一条)
					if (_frames % 120 == 1) DiagLog.Write($"[wof] 去房子最右格({sx},{sy}) 人在({ActExecutor.OriginCx(p)},{ActExecutor.OriginCy(p)})");
					RecedingNav.Start(sx, sy, RecedingNav.Mode.Stand);
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
					if (GuideFell(gn))
					{ DiagLog.Write($"[wof] 向导已在{gy}行往下落,不挖了"); Go(Ph.Patch); return; }
					// 【挖不动不判死】。挖不掉一般是狱岩,向导容易自己走下去 --- 报一嘴接着等
					if (_frames > 60 * 300 && _frames % 600 == 1)
						DiagLog.Write($"[wof] 挖不动向导脚下({gx},{gy}) 人({ActExecutor.OriginCx(p)},{ActExecutor.OriginCy(p)}) 手臂{Player.tileRangeX}/{Player.tileRangeY},等他自己走下去");
					// 手臂 30 格(横竖都是),站在房子最右格够得着向导脚下的每一列。
					// 真够不着说明他走出去 30 格以外了,才回去重新站位 —— 【别一够不着就退】:
					// 他跳回人身边那一帧也会瞬时判不够,一退手臂就被收,剩下的格子再也挖不到
					if (!Reach.CanMine(p, gx, gy))
					{
						if (_frames % 120 == 1)
							DiagLog.Write($"[wof] 向导({gx},{gy})在30格外,人({ActExecutor.OriginCx(p)},{ActExecutor.OriginCy(p)}),回去重新站位");
						p.SetTalkNPC(-1); Go(Ph.BackToGuide); return;
					}
					// 【挖之前先跟他说话,挖的全程别断】。对话中的 NPC 站着不动 ——
					// 不拉住他的话人一边挖他一边溜达,挖开的洞永远不在他脚下。
					// vanilla 每帧会把够不着的 talkNPC 清掉,所以要每帧重设。
					// 他掉下去之后原版自己会断对话,不用我们收尾
					if (CanTalkTo(p, gn) && p.talkNPC != g)
					{
						// 【照抄右键那条路】(Main.cs:56173 那一段)。光调 SetTalkNPC 只设了个字段,
						// 对话框根本不弹 —— 而让 NPC 站住不动的正是【弹出来的对话框】。
						// npcChatText 必须是 GetChat() 的真台词,给空串等于没开
						Main.CancelHairWindow();
						Main.SetNPCShopIndex(0);
						Main.InGuideCraftMenu = false;
						p.dropItemCheck();
						Main.npcChatCornerItem = 0;
						p.sign = -1;
						Main.editSign = false;
						p.SetTalkNPC(g);
						Main.playerInventory = false;
						p.chest = -1;
						Recipe.FindRecipes();
						Main.npcChatText = gn.GetChat();
						Terraria.Audio.SoundEngine.PlaySound(SoundID.Chat);
						DiagLog.Write($"[wof] 拉住向导说话(talk={p.talkNPC}),免得他乱走");
					}
					// 向导走到人自己脚底下那一列了:挖下去等于拆自己站的地,人跟着一起掉。
					// 先挪开一格再挖 —— 挪位由 SettleAt 落定,别在这儿硬挖
					// 【只有一方被另一方完全盖住才让位】。部分相交照挖:人跨 2~3 列,
					// 挖掉相交的那一列剩下的还撑着人,而向导少一列支撑就可能掉下去。
					// 完全包含才是死结 —— 向导⊆人挖不到他,人⊆向导挖了自己没地站
					var (mbl, mbr) = Predicates.TouchCols(p.position.X, p.width);
					var (ggl, ggr) = Predicates.TouchCols(gn.position.X, gn.width);
					bool overlap = (ggl >= mbl && ggr <= mbr) || (mbl >= ggl && mbr <= ggr);
					// 【重合满 1 秒才让】。他跳来跳去时会有瞬时的完全重合,当帧就让位等于
					// 人被他推着走,一格都挖不成
					if (!overlap) _overlapFrames = 0;
					else if (++_overlapFrames >= OverlapWait)
					{
						if (SettleAt.IsRunning) { DigBeat(p, gx, gy, "让位中"); return; }
						// 【往桥延展那头让】,不是背着桥走 --- 背着走会离桥面越来越远,
						// 最后站到桥外够不着。只要走到不完全重合就够,不用让出整个箱子
						int away = _bridgeDir > 0 ? ggr + 1 : ggl - 1;
						if (_overlapFrames % 60 == 1) DiagLog.Write($"[wof] 向导箱{ggl}..{ggr}和人{mbl}..{mbr}重合{_overlapFrames}帧,往桥那头让到{away}");
						SettleAt.Start(away, out _);
						return;
					}
					if (ItemUseCoordinator.IsActive) { DigBeat(p, gx, gy, "挥镐中"); return; }
					// 【他碰撞箱压住的每一列都要挖】。NPC 和人一样跨 2~3 列 ——
					// 只挖中心那列,两边还各有半只脚踩着地,他就站在洞上不动
					// (现场:向导(1082,1052) 那一列 1052..1057 全空了,人却 960 帧一动不动)。
					// 列的算法和判人一样,不另写一套
					// 【用 TouchCols 不是 BodyCols】。BodyCols 减 1px 会少报最右那列 ——
					// 向导实际压着 3 列却只报 2 列,挖完两列他往右一滑就被第 3 列接住
					// (现场:1081/1082 全空了,他 840 帧一动不动)
					var (gbl, gbr) = Predicates.TouchCols(gn.position.X, gn.width);
					var (pbl, pbr) = Predicates.TouchCols(p.position.X, p.width);
					// 一路往下挖到岩浆:脚下常有寻路自己铺的平台,只挖 3 格的话向导落在平台上就卡住了。
					// 但【只挖够得着的】—— 够不着的挖不动,而且补的时候也回不去,那洞就永远留着
					for (int k = 0; k < DigDepth; k++)
					{
						int dy = gy + k;
						if (Predicates.IsLava(gx, dy)) break;          // 到岩浆了,下面不用管
						for (int dx = gbl; dx <= gbr; dx++)
						{
							// 【和人相交的那一列照挖】。人跨 2~3 列,挖掉其中一列剩下的还撑着他,
							// 人不会掉;而向导少一列支撑就可能掉下去,不挖等于白等。
							// 只有【人所有的列都在要挖的范围里】才留手 —— 那才是把自己的地拆光
							if (pbl >= gbl && pbr <= gbr) continue;
							if (!Main.tile[dx, dy].HasTile) continue;   // 空的跳过
							if (!Reach.CanMine(p, dx, dy))
							{
								if (_frames % 120 == 1) DiagLog.Write($"[wof] ({dx},{dy})够不着,跳过 —— 再深就补不回来");
								continue;
							}
							// 平台也要挖:ClearWay.Dig 认为平台不挡路,但它挡得住掉下去的向导
							if (Predicates.IsPlatform(dx, dy))
							{
								int pk2 = ClearWay.PickSlot(p);
								if (pk2 < 0) { Fail("要挖平台但没镐"); return; }
								ItemUseCoordinator.Start(new ItemUseRequest { TargetWx = dx, TargetWy = dy, Slot = pk2, Strict = true });
								DiagLog.Write($"[wof] 挖({dx},{dy}) 平台挡着向导");
							}
							// 【挖不动就换下一格,别整个退出】。原来是 `if (!Dig(...)) return;` ——
							// Dig 在够不着/挖不动/被 OnLine 拦时返回 false,一 false 整个循环就没了
							else if (!ClearWay.Dig(p, dx, dy, "捅向导"))
							{
								if (_frames % 120 == 1)
									DiagLog.Write($"[wof] ({dx},{dy}) type={Main.tile[dx, dy].TileType} 挖不动,换一格");
								continue;
							}
							if (!_dug.Contains((dx, dy))) _dug.Add((dx, dy));
							return;
						}
					}
					DigBeat(p, gx, gy, "一格都挖不动");
					return;
				}

				// 补回缺口:桥面不能留洞,不然回头走这儿会掉进去
				case Ph.Patch:
				{
					if (PlaceAnywhere.IsRunning) return;
					if (_dug.Count == 0) { Go(Ph.WaitWof); return; }
					// 【等他掉远了再补】。补洞是为了把桥面还原,可他刚开始掉的时候洞就在他脚下,
					// 这时补上等于给他垫了块地,人白挖一场。掉够 PatchClear 行才算走远了
					{
						int pg = NPC.FindFirstNPC(NPCID.Guide);
						if (pg >= 0)
						{
							var pgn = Main.npc[pg];
							int pgy = (int)((pgn.position.Y + pgn.height + 2f) / 16f);
							int topDug = int.MaxValue;
							foreach (var d in _dug) if (d.y < topDug) topDug = d.y;
							// 等他掉远。但【只等 PatchWait 帧】—— 他要是卡在洞口不动,
							// 一直等下去就是把洞永远留着,桥面有个窟窿人后面还要走
							if (pgy < topDug + PatchClear && _frames < PatchWait)
							{
								if (_frames % 60 == 1)
									DiagLog.Write($"[wof] 向导还在{pgy}行(洞口{topDug}),等他掉过{topDug + PatchClear}再补 {_frames}/{PatchWait}");
								return;
							}
						}
					}
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
						Chatter.Say("[TerraBlind] 肉山出来了,开打", 120, 255, 120);
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
