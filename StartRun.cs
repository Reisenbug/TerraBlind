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
			_route.Clear(); _routeIdx = 0; _haveTarget = false; _routeReady = null; _sideTrip = false;
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
					if (!RecedingNav.Active && !TreasureGrab.IsRunning && GreedSideTrip()) return;
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
					if (!TreasureGrab.Start(stop.x, stop.y, out string tw))
						DiagLog.Write($"[start] 这个开不了:{tw},跳过");
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
					DiagLog.Write("[start] 房子盖好了");
					Chatter.Say("[TerraBlind] 房子好了,下地狱", 120, 255, 120);
					Go(Ph.Descend);
					return;
				}

				// 下地狱。走 itinerary 链,逐站导航 + 顺手收
				case Ph.Descend:
				{
					// 【顺路采集】。itinerary 只列了几个大目标,路上贴着走过去的箱子靠这一层。
					// 【只在主链空档拐】:主链正朝某个目标走时抢过方向盘,那个目标就丢了 ---
					// _routeIdx 已经推进过,回来不会重走。空档拐,回来接着取下一站,不丢东西
					if (!RecedingNav.Active && !TreasureGrab.IsRunning && GreedSideTrip()) return;
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
						// 【生命水晶不挖】
						if (stop.kind == "heart")
						{ DiagLog.Write($"[start] 跳过[{_routeIdx}/{_route.Count}] 生命水晶({stop.x},{stop.y})"); return; }
						// 【H 比我大 = 离地狱更远 = 在身后】。拐一趟出来人就偏了,近的会被误判,
						// 所以只有【又远又在上游】才算走过头 --- 近的一律去拿,折回也就几秒
						if (d > SkipNearCells && ph2 >= 0 && th >= 0 && th > ph2 + 30)
						{ DiagLog.Write($"[start] 跳过[{_routeIdx}/{_route.Count}] ({stop.x},{stop.y}) H{th}>我的H{ph2}+30 且距{d}格,在身后了"); return; }
						// 顺路已经捡掉的东西计划里还留着,轮到它时人会跑回去捡空气
						if (!Main.tile[stop.x, stop.y].HasTile)
						{ DiagLog.Write($"[start] 跳过[{_routeIdx}/{_route.Count}] ({stop.x},{stop.y}) 那儿已经空了"); return; }
						DiagLog.Write($"[start] 下降[{_routeIdx}/{_route.Count}] {stop.kind}({stop.x},{stop.y}) 距{d} H{th}(我{ph2})");
						GreedPickup.MarkDone(stop.x, stop.y);   // 主链要去的,顺路那层别再算一遍
						if (!TreasureGrab.Start(stop.x, stop.y, out _))
							RecedingNav.Start(stop.x, stop.y, RecedingNav.Mode.Reach);
						return;
					}
					// 链走完了 = 到地狱了,交给地狱那一套
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
		static bool _sideTrip;
		static bool GreedSideTrip()
		{
			if (_sideTrip)
			{
				_sideTrip = false;
				DiagLog.Write($"[greed] 顺路那趟完了:{TreasureGrab.Outcome}/{TreasureGrab.Reason}");
				return false;
			}
			var hit = GreedPickup.Poll();
			if (hit == null) return false;
			var (gx, gy) = hit.Value;
			GreedPickup.MarkDone(gx, gy);
			if (!TreasureGrab.Start(gx, gy, out string gw))
			{ DiagLog.Write($"[greed] ({gx},{gy})开不了:{gw}"); return false; }
			_sideTrip = true;
			return true;
		}

		public static string StatusJson()
			=> $"{{\"running\":{(IsRunning ? "true" : "false")},\"phase\":\"{Phase}\",\"outcome\":\"{Outcome}\",\"reason\":\"{HttpServerSystem.JsonEscPublic(Reason)}\"}}";
	}
}
