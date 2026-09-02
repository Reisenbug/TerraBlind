using System.Collections.Generic;
using Terraria;
using Terraria.ID;

namespace TerraBlind
{
	// 【/tb 1 的全流程,搬进 mod】。原来这一整套编排在 python 的 _run_from_zero 里,
	// 而它每一步都要 HTTP 往返 --- 发布时想把 python 切掉就得连流程一起丢。
	//
	// 相位链和 python 那份一一对应:砍树 → 收火把 → 选址 → 走过去 → 盖房 → 下地狱 → 地狱全套。
	// 失败即停,原因写进 Reason,和 python 的 say(...) + return 一个意思。
	public static class StartRun
	{
		public enum Ph { Idle, Chop, Torch, Site, GotoSite, House, Descend, Hell, Done }

		public static Ph Phase = Ph.Idle;
		public static bool IsRunning => Phase != Ph.Idle && Phase != Ph.Done;
		public static string Outcome = "idle";
		public static string Reason = "";

		// 和 python 那份保持一致 (RUN1_BUILD_WOOD / RUN1_ROAD_WOOD / RUN1_NEED)
		const int BuildWood = 125;
		const int RoadWood = 75;
		const int NeedWood = BuildWood + RoadWood;
		const int NeedTorch = 4;
		// 挑树:cost = 砍的固定耗时 + 走过去的时间,bonus = 树高折成的木头
		const int MinTrunkH = 6;
		const float ChopFrames = 60f;
		const float WalkFramesPerTile = 4f;
		const float WoodPerTrunk = 1.6f;
		const float TowardBonus = 25f;
		const int ChopScanDist = 400;
		const int HouseW = 21, HouseH = 10;

		static int _frames;
		static int _jungleDir;                     // 丛林在东(+1)还是西(-1),砍树往那边偏
		static int _siteX, _siteY;
		static (int x, int y) _target;             // 这一轮要砍的树 / 要开的箱
		static bool _haveTarget;
		static readonly HashSet<(int, int)> _skip = new();
		static List<(int x, int y, string kind)> _route = new();
		static int _routeIdx;

		public static bool Start(out string why)
		{
			why = "";
			var p = Main.LocalPlayer;
			if (p == null || !p.active) { why = "no_player"; return false; }
			if (IsRunning) { why = "已经在跑了"; return false; }
			_skip.Clear(); _route.Clear(); _routeIdx = 0; _haveTarget = false; _routeReady = null;
			Outcome = "running"; Reason = "";
			DiagLog.Write($"[start] 开工 人({ActExecutor.OriginCx(p)},{ActExecutor.OriginCy(p)}) 木材{Have(ItemID.Wood)} 火把{Have(ItemID.Torch)}");
			Chatter.Say("[TerraBlind] 开工:砍木头 → 收火把 → 盖房子 → 下地狱", 120, 255, 120);
			Go(Ph.Chop);
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

		// 砍树用的斧子。照 ClearWay.PickSlot 的样子,热键栏没有就从背包搬一把上来
		static int AxeSlot(Player p)
		{
			int slot = -1, best = 0;
			for (int i = 0; i < 10 && i < p.inventory.Length; i++)
			{
				var it = p.inventory[i];
				if (it != null && !it.IsAir && it.axe > best) { best = it.axe; slot = i; }
			}
			if (slot >= 0) return slot;
			int bagSlot = -1, bagBest = 0;
			for (int i = 10; i < 54 && i < p.inventory.Length; i++)
			{
				var it = p.inventory[i];
				if (it != null && !it.IsAir && it.axe > bagBest) { bagBest = it.axe; bagSlot = i; }
			}
			if (bagSlot < 0) return -1;
			return PlaceAction.HomeSlot(bagSlot);
		}

		// 【挑哪棵树】。照抄 python 的 _tallest_trunks:按 cost-bonus 排,
		// cost = 砍的固定耗时 + 走过去的时间,bonus = 树高折成的木头。
		// 只按高度排会为了远处一棵大树跑穿半张图;只按距离排又会一直啃小树苗
		static (int x, int y)? BestTrunk()
		{
			var p = Main.LocalPlayer;
			if (p == null) return null;
			var tiles = TileScan.Nearest(TileID.Trees, 400, ChopScanDist);
			var byCol = new Dictionary<int, List<int>>();
			foreach (var (x, y) in tiles)
			{
				if (!byCol.TryGetValue(x, out var ys)) { ys = new List<int>(); byCol[x] = ys; }
				ys.Add(y);
			}
			int px = ActExecutor.OriginCx(p);
			float bestScore = float.MaxValue;
			(int x, int y)? best = null;
			foreach (var kv in byCol)
			{
				var ys = kv.Value;
				ys.Sort();
				int st = 0;
				for (int i = 1; i <= ys.Count; i++)
				{
					// 一段连续的 y 就是一根树干
					if (i != ys.Count && ys[i] == ys[i - 1] + 1) continue;
					int h = i - st, baseY = ys[i - 1];
					st = i;
					if (h < MinTrunkH) continue;
					if (_skip.Contains((kv.Key, baseY))) continue;
					int dist = TileScan.Dist(kv.Key, baseY);
					float score = ChopFrames + dist * WalkFramesPerTile - h * WoodPerTrunk * ChopFrames / 10f;
					if (_jungleDir != 0 && (kv.Key - px) * _jungleDir > 0) score -= TowardBonus;
					if (score < bestScore) { bestScore = score; best = (kv.Key, baseY); }
				}
			}
			return best;
		}
		public static void Tick()
		{
			if (!IsRunning) return;
			var p = Main.LocalPlayer;
			if (p == null || !p.active) { Fail("no_player"); return; }
			_frames++;

			switch (Phase)
			{
				// 砍树凑木材。顺着丛林那头砍:等量的木头,砍完顺带也走近了
				case Ph.Chop:
				{
					if (Have(ItemID.Wood) >= NeedWood)
					{ DiagLog.Write($"[start] 木材够了({Have(ItemID.Wood)}/{NeedWood})"); Go(Ph.Torch); return; }
					if (_frames > 60 * 600) { Fail($"砍了10分钟木材还是只有{Have(ItemID.Wood)}/{NeedWood}"); return; }
					if (RecedingNav.Active || ItemUseCoordinator.IsActive) return;
					if (_haveTarget)
					{
						// 上一趟的结果:没砍掉就拉黑,免得对着同一棵砍到天荒地老
						if (RecedingNav.LastStop != null && RecedingNav.LastStop != "done")
							_skip.Add(_target);
						else if (Main.tile[_target.x, _target.y].HasTile)
							_skip.Add(_target);
						_haveTarget = false;
						return;   // 空一帧让掉落物落地被捡
					}
					var tr = BestTrunk();
					if (tr == null) { Fail($"附近{ChopScanDist}格内没有 h>={MinTrunkH} 的树,木材{Have(ItemID.Wood)}/{NeedWood}"); return; }
					int ax = AxeSlot(p);
					if (ax < 0) { Fail("背包里没有斧子"); return; }
					_target = tr.Value; _haveTarget = true;
					DiagLog.Write($"[start] 砍树({_target.x},{_target.y}) 木材{Have(ItemID.Wood)}/{NeedWood}");
					RecedingNav.Start(_target.x, _target.y, RecedingNav.Mode.Reach);
					return;
				}

				// 火把合不出来(要凝胶,这世界不刷怪),只能开箱;顺着下丛林的路收,够了就停
				case Ph.Torch:
				{
					if (Have(ItemID.Torch) >= NeedTorch)
					{ DiagLog.Write($"[start] 火把够了({Have(ItemID.Torch)}/{NeedTorch})"); Go(Ph.Site); return; }
					if (_frames > 60 * 900) { Fail($"收火把超时,只有{Have(ItemID.Torch)}/{NeedTorch}"); return; }
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
					if (stop.kind == "heart") return;         // 这趟只为补货,血量另说
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
						DiagLog.Write($"[start] 下降[{_routeIdx}/{_route.Count}] {stop.kind}({stop.x},{stop.y})");
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

		public static string StatusJson()
			=> $"{{\"running\":{(IsRunning ? "true" : "false")},\"phase\":\"{Phase}\",\"outcome\":\"{Outcome}\",\"reason\":\"{HttpServerSystem.JsonEscPublic(Reason)}\"}}";
	}
}
