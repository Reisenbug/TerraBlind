using System.Collections.Generic;
using Terraria;
using Terraria.ID;

namespace TerraBlind
{
	// 【/tb 1 的全流程,搬进 mod】。原来这一整套编排在 python 的 _run_from_zero 里,
	// 而它每一步都要 HTTP 往返 --- 发布时想把 python 切掉就得连流程一起丢。
	//
	// 相位链:收火把 → 选址 → 走过去 → 盖房 → 下地狱 → 地狱全套。
	// 【不砍树】:木头 9999 常驻,那一段没意义了
	// 失败即停,原因写进 Reason,和 python 的 say(...) + return 一个意思。
	public static class StartRun
	{
		public enum Ph { Idle, Torch, Site, GotoSite, House, Descend, Hell, Done }

		public static Ph Phase = Ph.Idle;
		public static bool IsRunning => Phase != Ph.Idle && Phase != Ph.Done;
		public static string Outcome = "idle";
		public static string Reason = "";

		// 和 python 那份保持一致 (RUN1_BUILD_WOOD / RUN1_ROAD_WOOD / RUN1_NEED)
		const int NeedTorch = 4;
		const int HouseW = 21, HouseH = 10;
		const int NeedPlatforms = 20;   // 开工前手上至少要有这么多平台
		const int SkipNearCells = 60;   // 这么近的宝藏不判"在身后",直接去拿

		static int _frames;
		static int _siteX, _siteY;
		static bool _haveTarget;
		static List<(int x, int y, string kind)> _route = new();
		static int _routeIdx;

		public static bool Start(out string why)
		{
			why = "";
			var p = Main.LocalPlayer;
			if (p == null || !p.active) { why = "no_player"; return false; }
			if (IsRunning) { why = "已经在跑了"; return false; }
			_route.Clear(); _routeIdx = 0; _haveTarget = false; _routeReady = null; _sideTrip = false; _sideHeart = null; _stop = null; _crystalStall = 0; _crystalSwung = false; _atHellEnd = false; _hellEndTries = 0;
			GreedPickup.Reset();
			Outcome = "running"; Reason = "";
			DiagLog.Write($"[start] 开工 人({ActExecutor.OriginCx(p)},{ActExecutor.OriginCy(p)}) 木材{Have(ItemID.Wood)} 火把{Have(ItemID.Torch)}");
			Chatter.Say("[TerraBlind] 开工:收火把 → 盖房子 → 下地狱", 120, 255, 120);
			Go(Ph.Torch);
			return true;
		}

		public static void Stop()
		{
			if (Outcome == "running") Outcome = "stopped";
			RecedingNav.Stop(); TreasureGrab.Stop(); HouseBuilder.Stop();
			Phase = Ph.Idle;
			DiagLog.Write("[start] 停止");
		}

		static void Fail(string r)
		{
			Outcome = "stuck"; Reason = r; Phase = Ph.Idle;
			DiagLog.Write($"[start] STUCK {r}");
			Chatter.Say($"[TerraBlind] {r}", 255, 120, 120);
		}

		static void Go(Ph next)
		{
			Phase = next; _frames = 0; _haveTarget = false;
			DiagLog.Write($"[start] → {next}");
		}

		static int Have(int id) => Predicates.Have(id);

		public static void Tick()
		{
			if (!IsRunning) return;
			var p = Main.LocalPlayer;
			if (p == null || !p.active) { Fail("no_player"); return; }
			_frames++;

			switch (Phase)
			{
				// 火把合不出来(要凝胶,这世界不刷怪),只能开箱;顺着下丛林的路收,够了就停
				case Ph.Torch:
				{
					if (Have(ItemID.Torch) >= NeedTorch)
					{ DiagLog.Write($"[start] 火把够了({Have(ItemID.Torch)}/{NeedTorch})"); Go(Ph.Site); return; }
					if (_frames > 60 * 900) { Fail($"收火把超时,只有{Have(ItemID.Torch)}/{NeedTorch}"); return; }
					// 到了再开箱,和下降那段同一个分工,同样要排在最前面
					if (_stop.HasValue && !_sideTrip && !RecedingNav.Active && !TreasureGrab.IsRunning)
					{
						var st2 = _stop.Value; _stop = null;
						DiagLog.Write($"[start] 到了({st2.x},{st2.y}) nav={RecedingNav.LastStop} 还在={Main.tile[st2.x, st2.y].HasTile}");
						if (Main.tile[st2.x, st2.y].HasTile
							&& !TreasureGrab.Start(st2.x, st2.y, out string tw2))
							DiagLog.Write($"[start] ({st2.x},{st2.y})开不了:{tw2}");
						return;
					}
					if (!TreasureGrab.IsRunning && GreedSideTrip()) return;
					if (RecedingNav.Active || TreasureGrab.IsRunning) return;
					if (_route.Count == 0)
					{
						if (_routeBusy) return;                    // 后台在算,等着
						if (_routeReady == null) { RequestRoute(); return; }
						_route = _routeReady; _routeReady = null; _routeIdx = 0;
						if (_route.Count == 0) { Fail("没找到下地狱的主道,收不了火把"); return; }
						DiagLog.Write($"[start] 路上有{_route.Count}个宝藏点");
					}
					// 走完了还不够:没光 NPC 不住,盖了也白盖
					if (_routeIdx >= _route.Count)
					{ Fail($"路上的箱子开完了,火把只有{Have(ItemID.Torch)}/{NeedTorch},没光 NPC 不住"); return; }
					var stop = _route[_routeIdx++];
					if (stop.kind == "heart") return;         // 生命水晶不挖
					if (!Main.tile[stop.x, stop.y].HasTile)
					{ DiagLog.Write($"[start] 跳过({stop.x},{stop.y}) 那儿已经空了"); return; }
					DiagLog.Write($"[start] 开箱[{_routeIdx}/{_route.Count}] ({stop.x},{stop.y}) 火把{Have(ItemID.Torch)}/{NeedTorch}");
					_stop = stop;
					RecedingNav.Start(stop.x, stop.y, RecedingNav.Mode.Reach);
					return;
				}

				// 房子就是一个 21x10 的矩形,at 是左下角。选址只问一件事:这个框里空不空
				case Ph.Site:
				{
					if (!Predicates.ScanHouse(ActExecutor.OriginCx(p), ActExecutor.OriginCy(p),
							HouseW, HouseH, 200, out _siteX, out _siteY, out int scanned))
					{ Fail($"附近没地方盖(要{HouseW}x{HouseH}的净空;扫了{scanned}格)"); return; }
					DiagLog.Write($"[start] 房址左下角({_siteX},{_siteY})");
					Chatter.Say($"[TerraBlind] 房址({_siteX},{_siteY}),走过去", 120, 255, 120);
					Go(Ph.GotoSite);
					return;
				}

				// 走到房址那一带就行,精准踩上左下角是 HouseBuilder 的 Ph.Lift 干的
				case Ph.GotoSite:
				{
					if (RecedingNav.Active) return;
					if (_frames > 60 * 300) { Fail($"走不到房址({_siteX},{_siteY})"); return; }
					if (!_haveTarget)
					{
						_haveTarget = true;
						RecedingNav.Start(_siteX, HouseBuilder.LadderFootRow(_siteX, _siteY));
						return;
					}
					// 【平台不够就别开工】。盖房第一步 pillar 要垫平台上去,没有就当场
					// "爬不动"判死(现场:pillar stop: no platform slot)。python 那份在这儿
					// 显式补货并等结果,mod 这边 PlatformStock 是自动的,等它补上就行
					if (Have(PlatformStock.ItemId) < NeedPlatforms)
					{
						if (_frames % 120 == 1)
							DiagLog.Write($"[start] 等平台 {Have(PlatformStock.ItemId)}/{NeedPlatforms}");
						return;
					}
					// 【盖房前先把下一段的场建起来】。建场是秒级的,而盖房要几十秒 ---
					// 让它在这期间后台跑完,房子一好人直接动身
					HttpServerSystem.WarmDescentAsync("jungle");
					if (!HouseBuilder.Start(4, 1, _siteX, _siteY, out string hw))
					{ Fail($"开不了工:{hw}"); return; }
					Chatter.Say("[TerraBlind] 开始盖房子", 120, 255, 120);
					Go(Ph.House);
					return;
				}

				case Ph.House:
				{
					if (HouseBuilder.IsRunning) return;
					if (HouseBuilder.Outcome != "done")
					{ Fail($"房子没盖成:{HouseBuilder.Outcome}/{HouseBuilder.Reason}"); return; }
					// python 那份在下地狱前也补一次平台(_top_up_platforms) --- 平台是寻路的耗材
					if (Have(PlatformStock.ItemId) < NeedPlatforms)
					{
						if (_frames % 120 == 1)
							DiagLog.Write($"[start] 下地狱前等平台 {Have(PlatformStock.ItemId)}/{NeedPlatforms}");
						return;
					}
					DiagLog.Write("[start] 房子盖好了");
					Chatter.Say("[TerraBlind] 房子好了,下地狱", 120, 255, 120);
					Go(Ph.Descend);
					return;
				}

				// 下地狱。走 itinerary 链,逐站导航 + 顺手收
				case Ph.Descend:
				{
					// 【到了再开箱,而且要排在最前面】。寻路结束那一帧,顺路采集和"取下一站"
					// 都会抢在前面执行 --- 一抢 _stop 就被覆盖,箱子永远开不了
					// _sideTrip 期间原目标还没走完,别把它当"到了"
					// 【_stop 挂着却进不来】要看得见。上一局 heart(1152,568) 设了 _stop 之后
					// 2400 帧一条日志都没有,分不出是 nav 一直在跑还是条件哪项不满足
					if (_stop.HasValue && _frames % 300 == 1)
						DiagLog.Write($"[start] 等着办({_stop.Value.x},{_stop.Value.y}){_stop.Value.kind} nav={RecedingNav.Active} grab={TreasureGrab.IsRunning} side={_sideTrip}");
					if (_stop.HasValue && !_sideTrip && !RecedingNav.Active && !TreasureGrab.IsRunning)
					{
						var st = _stop.Value;
						if (!Main.tile[st.x, st.y].HasTile)
						{ _stop = null; DiagLog.Write($"[start] ({st.x},{st.y})已经没了"); return; }
						// 【水晶要挖,不是开箱】。它占 2x2,四格都挖掉才从地图上消失 ---
						// 留在 _stop 里一帧挖一格,挖没了上面那道 HasTile 自然放行
						if (st.kind == "heart") { MineCrystal(p, st.x, st.y); return; }
						_stop = null;
						DiagLog.Write($"[start] 到了({st.x},{st.y}) 人({ActExecutor.OriginCx(p)},{ActExecutor.OriginCy(p)}) nav={RecedingNav.LastStop}");
						if (!TreasureGrab.Start(st.x, st.y, out string sw))
							DiagLog.Write($"[start] ({st.x},{st.y})开不了:{sw}");
						return;
					}
					// 【顺路采集】。itinerary 只列了几个大目标,路上贴着走过去的箱子靠这一层。
					// 【只在主链空档拐】:主链正朝某个目标走时抢过方向盘,那个目标就丢了 ---
					// _routeIdx 已经推进过,回来不会重走。空档拐,回来接着取下一站,不丢东西
					// 【nav 在跑的时候也要扫】。等它跑完人早过去了 --- 这是漏宝的主因
					if (!TreasureGrab.IsRunning && GreedSideTrip()) return;
					if (RecedingNav.Active || TreasureGrab.IsRunning) return;
					if (_frames > 60 * 60 * 30) { Fail("下地狱超时"); return; }
					if (!_haveTarget)
					{
						// 盖房前预热过一趟,这里多半直接就有了
						if (_routeBusy) return;
						if (_routeReady == null) { RequestRoute(); return; }
						_route = _routeReady; _routeReady = null; _routeIdx = 0; _haveTarget = true;
						if (_route.Count == 0) { Fail("算不出下地狱的路线"); return; }
						DiagLog.Write($"[start] 下降路线{_route.Count}站");
						return;
					}
					if (_routeIdx < _route.Count)
					{
						var stop = _route[_routeIdx++];
						int pcx = ActExecutor.OriginCx(p), pcy = ActExecutor.OriginCy(p);
						int d = System.Math.Abs(pcx - stop.x) + System.Math.Abs(pcy - stop.y);
						int ph2 = HttpServerSystem.DescentH(pcx, pcy), th = HttpServerSystem.DescentH(stop.x, stop.y);
						// 【H 比我大 = 离地狱更远 = 在身后】。拐一趟出来人就偏了,近的会被误判,
						// 所以只有【又远又在上游】才算走过头 --- 近的一律去拿,折回也就几秒
						if (d > SkipNearCells && ph2 >= 0 && th >= 0 && th > ph2 + 30)
						{ DiagLog.Write($"[start] 跳过[{_routeIdx}/{_route.Count}] ({stop.x},{stop.y}) H{th}>我的H{ph2}+30 且距{d}格,在身后了"); return; }
						// 顺路已经捡掉的东西计划里还留着,轮到它时人会跑回去捡空气
						if (!Main.tile[stop.x, stop.y].HasTile)
						{ DiagLog.Write($"[start] 跳过[{_routeIdx}/{_route.Count}] ({stop.x},{stop.y}) 那儿已经空了"); return; }
						DiagLog.Write($"[start] 下降[{_routeIdx}/{_route.Count}] {stop.kind}({stop.x},{stop.y}) 距{d} H{th}(我{ph2})");
						GreedPickup.MarkDone(stop.x, stop.y);   // 主链要去的,顺路那层别再算一遍
						// 【走过去和开箱是两件事】。TreasureGrab 的 MaxGoto 只有 30 秒,
						// 是给"走到附近的箱子"设的 --- 拿它跑 495 格的长途必然超时判死
						// (现场:FAIL 走了1801帧还没到,人还差232格)。走路交给寻路,到了再开
						_stop = stop;
						RecedingNav.Start(stop.x, stop.y, RecedingNav.Mode.Reach);
						return;
					}
					// 【链走完了不等于到地狱了】。人停在最后一站宝藏那儿(现场:(1080,888)),
					// 离 A 点还差一百多行 --- python 那份走完链还有一趟 nav_to(hell_x,hell_y),
					// 到了才算 arrived。A 点就是 tb 2 传送的落点,用同一份算法,别再推一遍
					if (!_atHellEnd)
					{
						// 【用 HellLanding 不是 DescentEnd】。A 点是 H 场描出来的线上一格,线允许
						// 穿实心(下降路上本来就要挖) --- 直接拿它当落脚点,Mode.Stand 要精确站进
						// 一个实心格,A* 搜不出来,一条日志都不留就没了。HellLanding 会往上找到
						// 第一处身子那 3 行都空的落脚行,tb 2 传送用的就是它,同一份不另推
						var (ax, ay) = StateSnapshotPlayer.HellLanding();
						if (ax <= 0) { Fail("算不出地狱落脚点"); return; }
						int mycx = ActExecutor.OriginCx(p), mycy = ActExecutor.OriginCy(p);
						if (System.Math.Abs(mycx - ax) + System.Math.Abs(mycy - ay) <= 6)
						{ _atHellEnd = true; DiagLog.Write($"[start] 到 A 点了({ax},{ay})"); return; }
						if (++_hellEndTries > 3)
						{ Fail($"走不到 A 点({ax},{ay}),人({mycx},{mycy}) 最后一次nav={RecedingNav.LastStop}"); return; }
						DiagLog.Write($"[start] 链走完了,去 A 点({ax},{ay}) 人({mycx},{mycy}) 第{_hellEndTries}次 上一趟={RecedingNav.LastStop}");
						// Mode.Reach:够得着就行。A 点只是"到地狱了"的标志,不用精确踩上去 ---
						// 精确站位是 Mode.Stand,它在这种地形上常常一条路都搜不出来
						RecedingNav.Start(ax, ay, RecedingNav.Mode.Reach);
						return;
					}
					// 到地狱了,交给地狱那一套
					if (!StateSnapshotPlayer.StartHellRun(out string hrw))
					{ Fail($"地狱流程起不来:{hrw}"); return; }
					Chatter.Say("[TerraBlind] 到地狱了", 120, 255, 120);
					Go(Ph.Hell);
					return;
				}

				// 地狱那一整套编排在 StartHellRun 里,这儿只等它跑完
				case Ph.Hell:
				{
					if (StateSnapshotPlayer.HellRunPhase() != "idle") return;
					Outcome = "done"; Phase = Ph.Done;
					DiagLog.Write("[start] 全流程跑完");
					Chatter.Say("[TerraBlind] 全流程跑完", 120, 255, 120);
					return;
				}
			}
		}

		// 【必须后台跑】。RouteJsonFor 里是全图 Dijkstra + 每个宝藏一次建场,约 4 秒 ---
		// 放主线程 Tick 里就是整局卡死 4 秒。算好了写 _route,Tick 每帧看它有没有到
		static bool _routeBusy;
		static List<(int x, int y, string kind)> _routeReady;
		static void RequestRoute()
		{
			if (_routeBusy) return;
			_routeBusy = true; _routeReady = null;
			DiagLog.Write("[start] 后台开算路线");
			System.Threading.Tasks.Task.Run(() =>
			{
				try { _routeReady = ParseRoute(HttpServerSystem.RouteJsonFor("jungle", "{}")); }
				catch (System.Exception e) { DiagLog.Write($"[start] 算路线炸了:{e.Message}"); _routeReady = new List<(int, int, string)>(); }
				finally { _routeBusy = false; }
			});
		}

		// 从那份 JSON 里取 itinerary。【绝不在这儿重算】---
		// 选址/算线只有 mod 那一份,再推一遍就是第二套判据
		static List<(int x, int y, string kind)> ParseRoute(string json)
		{
			var outp = new List<(int x, int y, string kind)>();
			int at = json.IndexOf("\"itinerary\":[");
			if (at < 0) return outp;
			var rx = new System.Text.RegularExpressions.Regex(
				"\\{\"x\":(\\d+),\"y\":(\\d+),\"kind\":\"([a-z_]+)\"");
			foreach (System.Text.RegularExpressions.Match m in rx.Matches(json.Substring(at)))
				outp.Add((int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Value));
			return outp;
		}

		// 身边有值得捡的就拐一趟。返回 true = 这一帧归顺路采集管,主链先让开。
		// 【捡完自己回来】:TreasureGrab 跑完,主链下一帧照旧从人当前位置继续
		static bool _atHellEnd;      // 走到 A 点(下降线终点)了没 --- tb 2 是直接传到这儿的
		static int _hellEndTries;
		static (int x, int y, string kind)? _stop;   // 正在赶去的那一站,到了再开箱
		static bool _sideTrip;
		static (int x, int y)? _sideHeart;   // 顺路那颗水晶,要一帧挖一格
		// 生命水晶占 2x2,四格都挖掉才消失。挖完最后一格 _stop/_sideHeart 由调用方的
		// HasTile 判据自然放行 --- 这儿只负责挥一次镐
		// 【照抄 python 那三行】。它不是一格一格挖:发一次 use_item(strict, duration=0),
		// ItemUseCoordinator 自己挥到"地图上没了"为止,然后问那一格还在不在。
		// 拉黑的判据也照抄:out_of_reach / tool_weak / blocked 三种才拉黑
		static int _crystalStall;
		const int CrystalStallMax = 60 * 60;
		static void MineCrystal(Player p, int x, int y)
		{
			if (p == null) return;
			if (ItemUseCoordinator.IsActive) return;          // 正在挥,等它
			if (!Main.tile[x, y].HasTile)
			{ CrystalDone(x, y, "挖掉了"); return; }
			// 上一趟的结论:够不着/啃不动/被挡,拉黑走人 --- 别被怪推开后来回空跑
			string r = ItemUseCoordinator.Reason;
			if (_crystalSwung && (r == "out_of_reach" || r == "tool_weak" || r == "blocked"))
			{ DiagLog.Write($"[start] 水晶({x},{y})拉黑:{r}"); CrystalDone(x, y, $"放弃({r})"); return; }
			if (++_crystalStall > CrystalStallMax)
			{ CrystalDone(x, y, $"挖不动超时,人({ActExecutor.OriginCx(p)},{ActExecutor.OriginCy(p)})"); return; }
			int pk = ClearWay.PickSlot(p);
			if (pk < 0) { CrystalDone(x, y, "没镐"); return; }
			_crystalSwung = true;
			ItemUseCoordinator.Start(new ItemUseRequest
			{ TargetWx = x, TargetWy = y, Slot = pk, DurationTicks = 0, Strict = true });
			DiagLog.Write($"[start] 挖水晶({x},{y}) 人({ActExecutor.OriginCx(p)},{ActExecutor.OriginCy(p)})");
		}

		static bool _crystalSwung;
		static void CrystalDone(int x, int y, string note)
		{
			DiagLog.Write($"[start] 水晶({x},{y}){note}");
			GreedPickup.MarkDone(x, y);
			_crystalStall = 0; _crystalSwung = false;
			_stop = null; _sideHeart = null; _sideTrip = false;
		}

		static bool GreedSideTrip()
		{
			if (_sideTrip)
			{
				// 顺路那颗水晶:寻路到了就一帧挖一格,挖没了才算完
				if (_sideHeart.HasValue)
				{
					if (RecedingNav.Active) return true;
					var h = _sideHeart.Value;
					if (Main.tile[h.x, h.y].HasTile) { MineCrystal(Main.LocalPlayer, h.x, h.y); return true; }
					// 水晶挖没了 = 拿到了(MineCrystal 里已经记过账)。这一趟没经过 TreasureGrab,
					// 别去读它的 Outcome --- 那是上一个箱子留下的
					_sideHeart = null; _sideTrip = false;
					DiagLog.Write("[greed] 顺路那颗水晶挖完了");
				}
				else
				{
				_sideTrip = false;
				// 【拿到了才记账】。选中就拉黑的话,这一趟失败就再也不回来了
				if (TreasureGrab.Outcome == "done" || TreasureGrab.Outcome == "partial")
					GreedPickup.MarkDone(TreasureGrab.At.x, TreasureGrab.At.y);
				DiagLog.Write($"[greed] 顺路那趟完了:{TreasureGrab.Outcome}/{TreasureGrab.Reason}");
				}
				// 【捡完回原路】。打断时把原目标记在 _stop 里,这儿重新起一趟 ---
				// 不重发的话人就停在岔路上,主链以为"到了"直接取下一站
				if (_stop.HasValue)
				{
					var b = _stop.Value;
					DiagLog.Write($"[greed] 回原路 ({b.x},{b.y})");
					RecedingNav.Start(b.x, b.y, RecedingNav.Mode.Reach);
					return true;
				}
				return false;
			}
			var hit = GreedPickup.Poll();
			if (hit == null) return false;
			var (gx, gy, isHeart) = hit.Value;
			// 【边走边拐】。python 那份是 nav_to 全程带 greed,随时打断去捡 ---
			// 只在主链空档扫的话,一趟长途下来路过的箱子全漏了
			RecedingNav.Stop();
			if (isHeart)
			{
				// 水晶要挖,先走到够得着的地方,挖归 MineCrystal
				_sideHeart = (gx, gy);
				_sideTrip = true;
				RecedingNav.Start(gx, gy, RecedingNav.Mode.Reach);
				return true;
			}
			if (!TreasureGrab.Start(gx, gy, out string gw))
			{ DiagLog.Write($"[greed] ({gx},{gy})开不了:{gw}"); return false; }
			_sideTrip = true;
			return true;
		}

		public static string StatusJson()
			=> $"{{\"running\":{(IsRunning ? "true" : "false")},\"phase\":\"{Phase}\",\"outcome\":\"{Outcome}\",\"reason\":\"{HttpServerSystem.JsonEscPublic(Reason)}\"}}";
	}
}
