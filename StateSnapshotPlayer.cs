using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraBlind
{
	public class StateSnapshotPlayer : ModPlayer
	{
		// 每间房要一个火把,而火把合不出来(配方要凝胶,这世界不刷怪),只能开箱砸罐。
		// 沿下丛林的路收会一路走到丛林深处,房子反而没盖成 —— 直接发,别让它成为流程的坎。
		public override System.Collections.Generic.IEnumerable<Item> AddStartingItems(bool mediumCoreDeath)
		{
			yield return new Item(ItemID.Torch, 4);
		}

		private const int ControlTimeoutTicks = 60;
		private const int JumpHoldFrames = 15;
		private const int AutoJumpCooldownFrames = 10;
		private int _jumpFramesLeft;
		private int _autoJumpCooldown;
		private float _prevVy;
		private static int _bridgeStartTick;   // 测铺路用时:开工那一帧,铺完报一次
		public static bool JumpPlaceEnabled = false;
		static bool _uiBlocking;
		static uint _uiBlockFrom;
		public static bool WalkTraceEnabled = false;
		private bool _jumpPlaceFired;
		// 后台扫房址的结果。画图和 nav 都只能在主线程碰,所以后台只放结论,下一帧再消费。
		private class SiteResult { public bool Got; public int Bx, By, Scanned, Fx, Fy; }
		private static volatile SiteResult _site;
		// H 选好的房址:nav 走完就在这儿开工。站位由 HouseBuilder.Ph.Lift 自己对齐,不要求按键时站对。
		private static (int x, int y)? _pendingHouse;
		private static int _pillarTestFrom, _pillarTestTarget;
		private static int _houseNavTries;
		// [ 测试:导航到桥起点之后,把那一格弄成放得出方块的(四周全空就先造个锚)
		// 放完第一格之后要站上去的那一格(桥面),站位是它上面一行
		private static bool _hellHouseStarted;
		private static (int x, int y)? _pendingStand;
		private static System.Collections.Generic.List<(int x, int y)> _pendingDeck;
		private static int _deckFrom;   // 房子沿线挪过之后,桥要从房子右端接着铺
		private static bool _wofAfterDeck;   // 桥铺完接着走肉山那一套
		private static int _deckStarve;      // 铺桥期间 DeckBuilder.Tick 连着几帧没轮到

		public override void ProcessTriggers(Terraria.GameInput.TriggersSet triggersSet)
		{
			if (_pendingHouse.HasValue && !RecedingNav.Active && !HouseBuilder.IsRunning)
			{
				var (hx, hy) = _pendingHouse.Value;
				var php2 = Main.LocalPlayer;
				int scol = hx;   // 梯子就在房子那一列
				int gapX = System.Math.Abs(ActExecutor.OriginCx(php2) - scol);
				// nav 会在半路停下还报 done,所以自己验位置。差得远就重走 —— SettleAt 只会走平地,
				// 跨不了沟翻不了墙,那是寻路的活。差几格才交给 Ph.Lift 精调。
				if (gapX > 6 && ++_houseNavTries <= 3)
				{
					Chatter.Say($"[TerraBlind] 离开工位还差 {gapX} 格,再走一次({_houseNavTries}/3)", 255, 220, 120);
					RecedingNav.Start(scol, HouseBuilder.LadderFootRow(hx, hy));
				}
				else
				{
					_pendingHouse = null; _houseNavTries = 0;
					if (gapX > 6)
						Chatter.Say($"[TerraBlind] 走不到开工位({scol},{hy + 1}),还差 {gapX} 格", 255, 120, 120);
					else if (HouseBuilder.Start(4, 1, hx, hy, out string whyH))
						Chatter.Say($"[TerraBlind] 到了,开工盖房 ({hx},{hy})", 120, 255, 120);
					else
						Chatter.Say($"[TerraBlind] 开工失败:{whyH}", 255, 120, 120);
				}
			}
			// 第一格放好了 → 直接开工盖房子。HouseBuilder 的 Ph.Lift 本来就是"站到那个悬空的
			// 左下角上":对齐列 → 爬过头就 DropDown → 挑头顶干净的半边 → pillar 上去。
			// 通用寻路不知道这些约束,扔给 Mode.Stand 会在目标附近来回弹(1036↔1037↔1039)。
			if (_pendingStand.HasValue && !BridgeStart.IsRunning && !PlaceAnywhere.IsRunning
			    && !RecedingNav.Active && !HouseBuilder.IsRunning)
			{
				var (sx2, sy2) = _pendingStand.Value;
				_pendingStand = null;
				// BridgeStart 已经把"放第一格 + 站上去 + 站稳60帧"全办了,这里只看它的结论
				if (BridgeStart.Outcome != "done")
				{
					// 【必须写日志】。原来只 Chatter.Say,而录视频时 Diag=false ---
					// 房子没开工、屏幕和日志都一个字没有,只能靠猜
					DiagLog.Write($"[reach-test] 没站上桥起点({sx2},{sy2}):{BridgeStart.Outcome}/{BridgeStart.Reason},房子起不了");
					Chatter.Say($"[TerraBlind] 没站上桥起点:{BridgeStart.Reason}", 255, 120, 120);
				}
				else
				{
					int hdir2 = ActExecutor.OriginCx(Main.LocalPlayer) < Main.maxTilesX / 2 ? 1 : -1;
					DiagLog.Write($"[reach-test] 第一格好了({sx2},{sy2}),盖房子 dir={hdir2}");
					if (HouseBuilder.Start(1, hdir2, sx2, sy2, out string hw2))
					{ _hellHouseStarted = true; Chatter.Say($"[TerraBlind] 爬上桥起点并盖房 ({sx2},{sy2})", 120, 255, 120); }
					else
					{
						DiagLog.Write($"[reach-test] 开不了工:{hw2}");
						Chatter.Say($"[TerraBlind] 开不了工:{hw2}", 255, 120, 120);
					}
				}
			}
			// 房子盖完 → 沿着线把桥铺出去。桥面从房子那一头往外接,所以跳过房子占的那几列。
			// 【还得等下降结束】。人在桥面上方 39 行时开工,DeckBuilder 每格都够不着,
			// 而它够不着只会横着走 -- 列号一格格往右爬(702->726),桥一格没铺。
			if (_pendingDeck != null && !HouseBuilder.IsRunning && !PlaceAnywhere.IsRunning
			    && !RecedingNav.Active && !DeckBuilder.IsRunning
			    && !BridgeStart.IsRunning && !PlatformDown.IsRunning)
			{
				var line = _pendingDeck;
				_pendingDeck = null;
				// 【Outcome 是上一栋房子留下的】。这一趟房子压根没开工时它还是 "done",
				// 于是照样铺桥 --- 得看这一趟到底起没起
				if (!_hellHouseStarted)
					DiagLog.Write("[reach-test] 这一趟房子没开工,不铺桥");
				else if (HouseBuilder.Outcome == "done")
				{
					int from = _deckFrom;
					DiagLog.Write($"[reach-test] 房子好了,开始铺桥 从i={from}/{line.Count}");
					if (DeckBuilder.Start("", line, from, out string dw))
					{
						Chatter.Say($"[TerraBlind] 铺桥 {line.Count - from}格", 120, 255, 120);
						_wofAfterDeck = true;
					}
					else
						Chatter.Say($"[TerraBlind] 铺不了:{dw}", 255, 120, 120);
				}
			}
			var site = _site;
			if (site != null)
			{
				_site = null;
				const int HW = 21, HH = 10;
				if (site.Got)
				{
					int d = System.Math.Abs(site.Bx - site.Fx) + System.Math.Abs(site.By - site.Fy);
					Predicates.VisualizeBox(site.Bx, site.By, HW, HH, $"house {HW}x{HH}");
					Chatter.Say($"[TerraBlind] 房址 左下角({site.Bx},{site.By}) 右上角({site.Bx + HW - 1},{site.By - HH + 1}) 离你{d}格", 120, 255, 120);
					// 走过去,到了自己开工。nav 直接送到【开工站位】而不是房址本身:
					// 房址那格是要放出来的,还不存在;站位是隔两列、下面一行的实地。
					_pendingHouse = (site.Bx, site.By);
					RecedingNav.Start(site.Bx, HouseBuilder.LadderFootRow(site.Bx, site.By));
				}
				else
				{
					int blocked = Predicates.VisualizeBox(site.Fx, site.Fy, HW, HH, "NO SITE (from here)");
					Chatter.Say($"[TerraBlind] 附近没有 {HW}x{HH} 的空位(扫了{site.Scanned}格)。画的是你脚下这个框,红的{blocked}格挡着。", 255, 120, 120);
				}
			}
			if (TerraBlind.ToggleMazeNav != null && TerraBlind.ToggleMazeNav.JustPressed)
				MazeWand.ToggleNav();
			if (TerraBlind.ToggleRecedingNav != null && TerraBlind.ToggleRecedingNav.JustPressed)
				RecedingNav.Toggle();
			// M 画出【贪心一定卡死的地方】:红=局部极小,橙=会被吸进去的盆地。按住 Shift 清图层
			if (TerraBlind.ShowMinima != null && TerraBlind.ShowMinima.JustPressed)
			{
				if (Main.keyState.PressingShift()) { Trap.Reset(); Chatter.Say("[TerraBlind] 卡点记录已清"); }
				else Trap.Report();
			}
			// H 找一次房址并画出来:绿=空,红=被占。找不到就画脚下那个框,直接看出被什么挡的。
			if (TerraBlind.ShowHouseSite != null && TerraBlind.ShowHouseSite.JustPressed)
			{
				var hp = Main.LocalPlayer;
				int fx = ActExecutor.OriginCx(hp), fy = ActExecutor.OriginCy(hp);
				const int HW = 21, HH = 10;
				// 扫最坏是 200×60×4 个候选,每个再验 210 格 —— 放主线程上就是一次可见的卡顿。
				// 纯读 tile,丢后台;画和走留到结果回来那一帧(SiteReady 在下面消费)。
				System.Threading.Tasks.Task.Run(() =>
				{
					try
					{
						bool g = Predicates.ScanHouse(fx, fy, HW, HH, 200, out int bx, out int by, out int sc);
						_site = new SiteResult { Got = g, Bx = bx, By = by, Scanned = sc, Fx = fx, Fy = fy };
					}
					catch (System.Exception e) { DiagLog.Write($"[house-scan] EXC {e.Message}"); }
				});
			}
			// B 测试铺路:朝面朝的方向铺 30 格。再按一次停。
			if (TerraBlind.TestBridge != null && TerraBlind.TestBridge.JustPressed)
			{
				if (BridgeBuilder.IsRunning)
				{
					BridgeBuilder.Stop();
					Chatter.Say($"[TerraBlind] 铺路停止,已铺 {BridgeBuilder.Placed}", 255, 200, 120);
				}
				else
				{
					var bp = Main.LocalPlayer;
					string bdir = bp.direction >= 0 ? "right" : "left";
					_bridgeStartTick = (int)Main.GameUpdateCount;
					if (BridgeBuilder.Start(BridgeTestItem(bp), bdir, 30, out string bwhy))
						Chatter.Say($"[TerraBlind] 铺路 {bdir} 30 格…", 120, 255, 120);
					else
					{ _bridgeStartTick = 0; Chatter.Say($"[TerraBlind] 铺不了: {bwhy}", 255, 120, 120); }
				}
			}
			// 铺完报一次用时 —— "边走边放"到底快多少,就看这个数。
			if (_bridgeStartTick > 0 && !BridgeBuilder.IsRunning)
			{
				int el = (int)Main.GameUpdateCount - _bridgeStartTick;
				_bridgeStartTick = 0;
				Chatter.Say($"[TerraBlind] 铺了 {BridgeBuilder.Placed} 格,{el} 帧 ({el / 60f:0.0}s, {BridgeBuilder.Placed * 60f / System.Math.Max(1, el):0.00} 格/秒) {BridgeBuilder.Outcome}", 200, 220, 255);
				DiagLog.Write($"[bridge-test] placed={BridgeBuilder.Placed} frames={el} rate={BridgeBuilder.Placed * 60f / System.Math.Max(1, el):0.00}/s outcome={BridgeBuilder.Outcome}");
			}

			// N 测试单间:在脚下朝面朝方向盖 6 宽的单间。
			if (TerraBlind.TestRoom != null && TerraBlind.TestRoom.JustPressed)
			{
				if (HouseBuilder.IsRunning)
				{
					HouseBuilder.Stop();
					Chatter.Say("[TerraBlind] 盖房已停", 255, 200, 120);
				}
				else if (HouseBuilder.StartHere(1, Main.LocalPlayer.direction, out string rwhy))
					Chatter.Say("[TerraBlind] 盖单间…", 120, 255, 120);
				else
					Chatter.Say($"[TerraBlind] 盖不了: {rwhy}", 255, 120, 120);
			}
			// P 单测 pillar:原地往上搭 10 格,人跟着爬上去。再按一次停。
			if (TerraBlind.TestPillar != null && TerraBlind.TestPillar.JustPressed)
			{
				if (SkillExecutor.IsActive) { SkillExecutor.Stop(); Chatter.Say("[TerraBlind] pillar 停", 255, 200, 120); }
				else
				{
					var pp = Main.LocalPlayer;
					int feet = (int)((pp.position.Y + pp.height) / 16f);
					int tgt = feet - 10;
					_pillarTestFrom = feet; _pillarTestTarget = tgt;
					SkillExecutor.StartPillarJump(pp.direction >= 0, tgt);
					Chatter.Say($"[TerraBlind] pillar: 脚 {feet} → {tgt}(10格)", 120, 255, 120);
				}
			}
			if (_pillarTestFrom != 0 && !SkillExecutor.IsActive)
			{
				var pp = Main.LocalPlayer;
				int feet = (int)((pp.position.Y + pp.height) / 16f);
				int got = _pillarTestFrom - feet;
				bool ok = feet <= _pillarTestTarget;
				Chatter.Say($"[TerraBlind] pillar 结束:升了 {got}/10 格,脚在 {feet}(要 {_pillarTestTarget}) {(ok ? "OK" : "没到")}",
					ok ? (byte)120 : (byte)255, ok ? (byte)255 : (byte)120, 120);
				DiagLog.Write($"[pillar-test] rose={got}/10 feet={feet} target={_pillarTestTarget} ok={ok}");
				_pillarTestFrom = 0;
			}
			// L 一键建桥:算线 → 竖降到桥面 → 横铺 170 格
			if (TerraBlind.BuildHellBridge != null && TerraBlind.BuildHellBridge.JustPressed)
			{
				if (HellBridge.IsRunning) { HellBridge.Stop(); Chatter.Say("[TerraBlind] 建桥停止"); }
				else if (HellBridge.Start("94", out string hbwhy))
					Chatter.Say("[TerraBlind] 开始建地狱桥", 120, 255, 120);
				else Chatter.Say($"[TerraBlind] 建不了:{hbwhy}", 255, 120, 120);
			}
			// O 测试:从脚下往下降 12 格,一路铺平台
			if (TerraBlind.TestPlatDown != null && TerraBlind.TestPlatDown.JustPressed)
			{
				if (PlatformDown.IsRunning) { PlatformDown.Stop(); Chatter.Say("[TerraBlind] 下降停止"); }
				else
				{
					int tgt = ActExecutor.OriginCy(Main.LocalPlayer) + 12;
					if (PlatformDown.Start("94", tgt, out string dwhy))
						Chatter.Say($"[TerraBlind] 往下铺平台 → {tgt}", 120, 255, 120);
					else Chatter.Say($"[TerraBlind] 下不去:{dwhy}", 255, 120, 120);
				}
			}
			// [ 单测"够得着就算到":算地狱线,导航去白点(开工点)。它悬空、站不上去,正是要试的情形。
			if (TerraBlind.TestReachWork != null && TerraBlind.TestReachWork.JustPressed)
			{
				if (RecedingNav.Active || BridgeStart.IsRunning) { StopHellRun(); Chatter.Say("[TerraBlind] 停止", 255, 200, 120); }
				else if (!StartHellRun(out string hrw)) Chatter.Say($"[TerraBlind] {hrw}", 255, 120, 120);
			}

			// I 预览全程:主道→地狱的线 + 桥 + 房子,一次画完。人不动,纯看位置对不对。
			if (TerraBlind.PreviewDescent != null && TerraBlind.PreviewDescent.JustPressed)
			{
				bool pok = HttpServerSystem.PreviewDescentAndBridge("jungle", out string pmsg);
				Chatter.Say("[TerraBlind] " + pmsg, pok ? (byte)120 : (byte)255, pok ? (byte)255 : (byte)120, 120);
			}
			// U 画地狱桥线:从人所在列往地图中心方向算 170 格,青线=桥,金色=房子那 6 格。只算不搭。
			if (TerraBlind.ShowHellLine != null && TerraBlind.ShowHellLine.JustPressed)
			{
				var hp = Main.LocalPlayer;
				int hbx = ActExecutor.OriginCx(hp);
				int hdir = hbx < Main.maxTilesX / 2 ? 1 : -1;
				var hres = HellLine.Compute(hbx, hdir);
				if (!hres.Found)
					Chatter.Say($"[TerraBlind] 地狱线算不出来：{hres.Why}", 255, 120, 120);
				else
				{
					var hvis = new System.Collections.Generic.List<(int, int, Microsoft.Xna.Framework.Color)>();
					var hlc = new Microsoft.Xna.Framework.Color(0, 200, 255, 140);
					var hhc = new Microsoft.Xna.Framework.Color(255, 180, 0, 230);
					foreach (var (hlx, hly) in hres.Line) hvis.Add((hlx, hly, hlc));
					for (int hk = 0; hk < HouseBuilder.RoomWidth + 1; hk++)
						hvis.Add((hres.HouseX + hdir * hk, hres.HouseY, hhc));
					// 白色=开工点。它未必是桥头,只是最好放出第一格的地方
					hvis.Add((hres.WorkX, hres.WorkY, new Microsoft.Xna.Framework.Color(255, 255, 255, 240)));
					PathVisSystem.SetTiles(hvis, 7200);
					string lavaTag = hres.HouseOnLava ? "岩浆上" : $"只有{hres.HouseLavaCols}/6列在岩浆上";
					Chatter.Say($"[TerraBlind] 桥线 房子({hres.HouseX},{hres.HouseY}) 开工点({hres.WorkX},{hres.WorkY})锚{hres.WorkAnchor} {lavaTag} 要挖{hres.DigCells}格 代价{hres.Cost}", 120, 255, 120);
					DiagLog.Write($"[hell-line] key start=({hres.StartX},{hres.StartY}) dir={hdir} house=({hres.HouseX},{hres.HouseY}) 岩浆列={hres.HouseLavaCols}/6 dig={hres.DigCells} cost={hres.Cost}");
				}
			}
		}

		// 地狱里一个站得住的落脚点。【只测地狱那一段】时传送用 —— 不用每次都从地表
		// 跑一遍丛林和下降。
		//
		// 挑法和 HellLine 一致:从人所在半边往中间扫,找【天花板到岩浆面之间空腔够高】
		// 且脚下是实地的列。找不到返回 (-1,-1),调用方自己报。
		// 选址:算线 → 找块干净的(底下要有岩浆)→ 从新房址重算线。
		// 人【已经在地狱】之后才跑这一步 —— tb 1 是走下来的,tb 2 是传到 A 点的,
		// 两条路到这儿汇合,后面完全一样
		public static HellLine.Result PickHellSite(int bx, int dir)
		{
			var rr = HellLine.Compute(bx, dir);
			if (!rr.Found) return rr;
			// 底下必须是岩浆:杀向导召肉山靠的就是把他从房里捅进岩浆。
			// 实在找不到岩浆上的干净地才退而求其次 —— 那时候后面那套做不了,但房子还能盖
			int hw1 = HouseBuilder.RoomWidth + 1;
			bool got = Predicates.ScanHouse(rr.HouseX, rr.HouseY, hw1, 10, 24,
				out int cx0, out int cy0, out int sc0, false, true);
			if (!got)
			{
				DiagLog.Write($"[reach-test] 附近没有【岩浆上】的干净房址(扫了{sc0}),退回不要求岩浆");
				got = Predicates.ScanHouse(rr.HouseX, rr.HouseY, hw1, 10, 24,
					out cx0, out cy0, out sc0, false);
			}
			if (got && (cx0 != rr.HouseX || cy0 != rr.HouseY))
			{
				DiagLog.Write($"[reach-test] 房址({rr.HouseX},{rr.HouseY})被占(树/旧平台),挪到({cx0},{cy0}) 扫了{sc0}");
				var rr2 = HellLine.Compute(cx0, dir, cy0);
				if (rr2.Found) rr = rr2;
				else DiagLog.Write($"[reach-test] 新房址算不出线({rr2.Why}),用原来那条");
			}
			return rr;
		}

		// tb 2 的传送落点。【必须和 tb 1 选的房址是同一处】—— 传到别处等于测的不是那条流程。
		// 落在桥起点上:那本来就是 BridgeStart 要站的第一格,人一到就能接着往下跑
		// tb 2 的落点 = 【A 点】= tb 1 下丛林走完、刚到地狱时人站的那一格。
		// 就是下降路线的终点,和 /descent_route 描的是同一条线。
		//
		// 【别在这儿算房址/桥线】。写过一版是"算出桥起点再传过去",那是流程更后面好几步的位置,
		// 传到那儿测的就不是"从地狱开始"这一段了。到了 A 点之后的事全归 StartHellRun。
		const int LandScan = 30;   // A 点被埋住时往上找几行

		public static (int x, int y) HellLanding()
		{
			var (ax, ay) = HttpServerSystem.DescentEnd("jungle", out string why);
			if (ax <= 0)
			{
				DiagLog.Write($"[teleport] 算不出下降终点:{why}");
				return (-1, -1);
			}
			// 【必须落在人放得下的地方】。A 点是 H 场描出来的线上一格,线是允许穿实心的
			// (下降路上本来就要挖),直接把人塞进方块 = vanilla 的挤出算炸,一帧飞几千格。
			// 从 A 点往上找第一处身子那 3 行都空、且不沾岩浆的落脚行
			for (int y = ay; y > ay - LandScan && y > 1; y--)
			{
				bool clear = true;
				for (int r = 0; r < 3 && clear; r++)
					if (Predicates.IsWall(ax, y - r) || Predicates.IsLava(ax, y - r)) clear = false;
				if (!clear) continue;
				DiagLog.Write($"[teleport] A点(下降终点)=({ax},{ay}) 落脚({ax},{y - 1}) 往上让了{ay - y}行");
				return (ax, y - 1);
			}
			DiagLog.Write($"[teleport] A点({ax},{ay})往上{LandScan}行都塞不下人");
			return (-1, -1);
		}

		// 地狱那一整套的唯一入口:算线 → 选址 → 去桥起点 → (盖房 → 铺桥 → 肉山 → 开打)。
		// 后面几步由 _pendingStand/_pendingDeck/_wofAfterDeck 接力,不在这儿等。
		// 键盘([ 键)和 HTTP(/hell_run)都走这里 —— 两套入口各写一遍是老毛病了
		public static bool StartHellRun(out string why)
		{
			why = "";
			var rp = Main.LocalPlayer;
			if (rp == null || !rp.active) { why = "no_player"; return false; }
			// 【人得先站稳】。整条线是按"人现在在哪"算的,而人在空中时那个坐标是半路的:
			// 落地后位置早变了,算出来的线和房址全对不上。
			// 实测(2559,1005)正在执行一条下落边,70 帧后这里切进来取到(2552,1012) —— 半空中,
			// 然后 ReachCell→PlatformDown 一路等落地,人却掉穿目标行 40 格进了岩浆。
			if (rp.velocity.Y != 0f) { why = $"人还在空中(vy={rp.velocity.Y:0.##}),等落地再开工"; return false; }
			int rbx = ActExecutor.OriginCx(rp);
			int rdir = rbx < Main.maxTilesX / 2 ? 1 : -1;
			// 树和旧平台都占着格子(Vacant 认 HasTile),直接开工必然撞上。
			// 选址那一套单独拆成 PickHellSite,读起来清楚些。
			// 【传送落点不走这儿】:那是 A 点(下降终点),在这一步【之前】
			var rr = PickHellSite(rbx, rdir);
			if (!rr.Found) { why = $"算不出线:{rr.Why}"; return false; }
			_deckFrom = HouseBuilder.RoomWidth + 1;
			var (rsx, rsy) = rr.Line[0];
			// 画线必须在重算【之后】:画早了显示的是旧线,和实际铺的对不上。
			// 蓝=要铺 绿=现成地形能用上 白=起点。时长按 176 格铺完估,别中途消失
			var rv = new System.Collections.Generic.List<(int, int, Microsoft.Xna.Framework.Color)>();
			for (int li = 0; li < rr.Line.Count; li++)
			{
				var (rlx, rly) = rr.Line[li];
				bool have = Predicates.IsGround(rlx, rly);
				rv.Add((rlx, rly, have
					? new Microsoft.Xna.Framework.Color(60, 230, 90, 110)
					: new Microsoft.Xna.Framework.Color(0, 200, 255, 100)));
			}
			rv.Add((rsx, rsy, new Microsoft.Xna.Framework.Color(255, 255, 255, 240)));
			PathVisSystem.SetDeck(rv, 60 * 60 * 20);
			// 【去桥起点这一段整个重写过】。老的 ReachCell 是"先降到目标行再横过去",
			// 实测一路铺平台梯、回头拆自己刚铺的平台、身子飘了 _col 还锁在原列。
			// 现在走路交给寻路,BridgeStart 只管三件寻路不管的事:放出那一格、站上去、
			// 【站住 60 帧才算到】。放第一格也归它了,所以不再走 _pendingAnchor。
			// 【桥起点一律用平台】。方块的锚点只认【四邻】,平台认 3x3【含斜角】——
			// 桥起点常常只有斜下方那一格有东西,方块判"没锚"直接 STUCK,平台放得上去
			// (现场:(857,1032)左下角明明有方块,却报"接不到任何有锚的地方")。
			// 原来借的是 HellBridge.FindBlockSlot,那函数专门 continue 掉平台(桥面要方块),
			// 借过来就把平台的锚点能力一起丢了
			int rplat = Unstick.PlatformItem(rp);
			if (rplat <= 0) { why = "背包里没有平台,放不出桥起点"; return false; }
			string rblk = rplat.ToString();
			if (!BridgeStart.Start(rblk, rsx, rsy, out string rcw))
			{ why = $"去不了桥起点:{rcw}"; return false; }
			_hellHouseStarted = false;
			_pendingStand = (rsx, rsy);
			_pendingDeck = rr.Line;
			DiagLog.Write($"[reach-test] 人({rbx},{ActExecutor.OriginCy(rp)}) → 桥起点({rsx},{rsy}) dir={rdir}");
			Chatter.Say($"[TerraBlind] 去桥起点({rsx},{rsy})", 120, 255, 120);
			return true;
		}

		public static void StopHellRun()
		{
			BridgeStart.Stop(); RecedingNav.Stop();
			HouseBuilder.Stop(); DeckBuilder.Stop(); WofPrep.Stop();
			if (WofFight.On) WofFight.Toggle();
			_pendingStand = null; _pendingDeck = null; _wofAfterDeck = false; _hellHouseStarted = false;
			DiagLog.Write("[reach-test] 地狱流程停止");
		}

		// 跑到哪一步了。给 /hell_run_status 用 —— Python 靠它判断该不该继续等
		public static string HellRunPhase()
		{
			if (WofFight.On) return "fight";
			if (WofPrep.IsRunning) return "wof:" + WofPrep.Phase;
			if (DeckBuilder.IsRunning) return "deck";
			if (HouseBuilder.IsRunning) return "house";
			if (PlaceAnywhere.IsRunning) return "anchor";
			if (BridgeStart.IsRunning || RecedingNav.Active) return "goto";
			if (_pendingStand.HasValue || _pendingDeck != null || _wofAfterDeck) return "handoff";
			return "idle";
		}

		// 我们自己的动作在不在跑。判据只有这一份,别在各处各写一套
		static bool TbAutoActive()
			=> ItemUseCoordinator.IsActive || PlaceAction.IsRunning || BridgeBuilder.IsRunning
			   || PillarUp.IsRunning || DeckBuilder.IsRunning || HouseBuilder.IsRunning
			   || PlaceAnywhere.IsRunning || WofPrep.IsRunning || RecedingNav.Active
			   || PlaceWalls.IsRunning || WalkPlace.IsRunning || WofFight.On;

		public override void SetControls()
		{
			if (Player != Main.LocalPlayer) return;

			// 光标压在任何 UI 上(背包、别的模组的界面)时,原版把 mouseInterface 置真,
			// ItemCheck 里就 delayUseItem=true 把这一帧的使用吞掉(Player.cs:24410) ——
			// 我们的动作全靠 controlUseItem,于是"用物品偶尔失效"。自动化在跑时清掉它
			// delayUseItem 一旦被置真就【自锁】:原版只在 !controlUseItem 时才清它(Player.cs:23969),
			// 而我们每帧都按着 controlUseItem —— 所以它永远不清,不是偶发失效是永久失效。
			// 上一版把清除挂在 mouseInterface 上,而那个标志此刻还没被置真(24410 在 23956 之后),
			// 于是一次都没触发。这里只认"我们自己在挥",玩家手动时不动
			// 【所有】鼠标动作都被这两个标志拦,不只是用物品:
			//   delayUseItem — 吃掉 controlUseItem(Player.cs:23969),而且会自锁
			//   mouseInterface — 拦 controlUseTile(29679,开箱/对话/开门)、拦挥动(46962/45495)、
			//                    拦智能光标(16188)
			// mouseInterface 每帧在 Main.Update 里重置、UI 绘制时再置真,而 SetControls 在两者之间,
			// 所以这里清掉的正是那些门本帧要读的值
			if (TbAutoActive())
			{
				// 只在【开始拦】和【拦完了】各记一行。原来每帧一行,光标往背包上一放就是几百行,
				// 895 行日志里 670 行是它,真正的事件全被冲掉了
				bool blocking = Player.delayUseItem || Player.mouseInterface;
				if (blocking != _uiBlocking)
				{
					_uiBlocking = blocking;
					if (blocking) { _uiBlockFrom = Main.GameUpdateCount; DiagLog.Write($"[ui-block] 开始拦 delayUse={Player.delayUseItem} mouseIface={Player.mouseInterface}"); }
					else DiagLog.Write($"[ui-block] 拦完了 共{Main.GameUpdateCount - _uiBlockFrom}帧");
				}
				Player.delayUseItem = false;
				Player.mouseInterface = false;
				Main.HoveringOverAnNPC = false;         // 也拦 controlUseTile(29679)
				Main.SmartInteractShowingGenuine = false;
			}

			// 打肉山:方向键和 controlUseItem 都要在 SetControls 这条线上发才算数。
			// 【必须在清拦截之后】—— delayUseItem 会自锁,先发 controlUseItem 就再也清不掉了
			WofFight.Tick();

			if (JumpPlaceEnabled)
			{
				bool atPeak = _prevVy < 0f && Player.velocity.Y >= 0f;
				if (atPeak && !_jumpPlaceFired)
				{
					_jumpPlaceFired = true;
					int slot = -1;
					for (int i = 0; i < 10; i++)
					{
						var it = Player.inventory[i];
						if (it != null && !it.IsAir && it.createTile >= 0 && Terraria.ID.TileID.Sets.Platforms[it.createTile])
						{ slot = i; break; }
					}
					if (slot >= 0)
					{
						Player.selectedItem = slot;
						Player.controlUseItem = true;
						// place at feet+1 tile so player lands on it
						int tileX = (int)((Player.position.X + Player.width / 2f) / 16f);
						int tileY = (int)((Player.position.Y + Player.height) / 16f) + 1;
						Cursor.AimTile(tileX, tileY);
						PathVisSystem.SetTiles(new System.Collections.Generic.List<(int, int, Microsoft.Xna.Framework.Color)>
						{
							(tileX, tileY, new Microsoft.Xna.Framework.Color(255, 220, 0, 200))
						}, ttlFrames: 180);
						Main.SmartCursorWanted_Mouse = false;
						DiagLog.Write($"[jump_place] fired at peak vy_prev={_prevVy:0.##} vy={Player.velocity.Y:0.##} slot={slot} tile=({tileX},{tileY}) itemTime={Player.itemTime} itemAnimation={Player.itemAnimation}");
					}
				}
				if (Player.velocity.Y == 0f && _jumpPlaceFired)
				{
					JumpPlaceEnabled = false;
					_jumpPlaceFired = false;
				}
			}
			_prevVy = Player.velocity.Y;

			// 锁的清算点:所有 Tick 都从这条链上走,所以在链头收回死持有者的锁。
			// 锁本身【跨帧】,这里不清活着的那些
			AxisLock.Sweep();

			// 【铺桥时这一帧被谁吃掉了】。下面那一串原语只要有一个在跑就 return,
			// DeckBuilder.Tick 一帧都轮不到 —— 那时它自己的心跳也不响,整段全黑,
			// 人站着不动而日志几百帧一片空白。必须埋在链头(所有 return 之前),
			// 真跑到 DeckBuilder.Tick 时清零;连着不清零就是被上游截了
			if (DeckBuilder.IsRunning && ++_deckStarve % 120 == 0)
				DiagLog.Write($"[deck] Tick连{_deckStarve}帧没轮到 占着的:place={PlaceAnywhere.IsRunning} " +
					$"settle={SettleAt.IsRunning} hop={HopUp.IsRunning} drop={DropDown.IsRunning} " +
					$"walk={WalkPlace.IsRunning} rope={RopeLadder.IsRunning} pillar={PillarUp.IsRunning} " +
					$"platdown={PlatformDown.IsRunning} walls={PlaceWalls.IsRunning} " +
					$"helldeck={HellDeck.IsRunning} nav={RecedingNav.Active} mine={MineCoordinator.IsActive}");

			// semantic place: drives its cell queue through ItemUseCoordinator. Ticked before the coordinator block
			// below so a cell it starts this frame gets swung at immediately.
			PlaceAction.Tick();

			// rope ladder: alternates placing (via ItemUseCoordinator) and climbing (writes controlUp itself). Its
			// climb phase owns the controls, so it returns before anything else can fight it for them.
			if (RopeLadder.IsRunning)
			{
				RopeLadder.Tick();
				if (ItemUseCoordinator.IsActive) ItemUseCoordinator.ApplyControls();
				RecordSystem.CaptureFrame(Player);
				return;
			}

			// pillar: builds a platform column to the player's right, driving jump + useItem + cursor itself.
			if (PillarUp.IsRunning)
			{
				PillarUp.Tick();
				RecordSystem.CaptureFrame(Player);
				return;
			}

			// hellbridge/reach: 只做编排,真正干活的是下面那些原语,所以它们先跑、不 return
			if (HellBridge.IsRunning) HellBridge.Tick();
			if (BridgeStart.IsRunning) BridgeStart.Tick();

			// 放第一格:走 PlaceAction,控制要跟着发;让位用 SettleAt,它的 Tick 在下面,
			// 所以这里必须替它跑一次 —— 直接 return 的话让位永远走不完。
			// 【寻路也一样】。StepAside 换行时起的是 RecedingNav,它的 Tick 也在下面:
			// 不替它跑,Active 永远为真,让位那一步死等(日志:2460帧"让位中")
			if (PlaceAnywhere.IsRunning)
			{
				PlaceAnywhere.Tick();
				if (SettleAt.IsRunning) SettleAt.Tick();
				// 寻路光 Tick 只是派发,真正走路的是 TickBlocks + ApplyControls --- 一起跑才动得了
				if (RecedingNav.Active)
				{
					RecedingNav.Tick();
					StateSpacePlanner.TickBlocks();
					StateSpacePlanner.BlockNavTick();
					if (StateSpacePlanner.IsActive) StateSpacePlanner.ApplyControls();
					else if (MineCoordinator.IsActive) MineCoordinator.ApplyControls();
				}
				if (ItemUseCoordinator.IsActive) ItemUseCoordinator.ApplyControls();
				RecordSystem.CaptureFrame(Player);
				return;
			}

			// helldeck: 照着线逐格铺桥面,放置走 PlaceAction,所以控制要跟着发
			if (HellDeck.IsRunning)
			{
				HellDeck.Tick();
				if (ItemUseCoordinator.IsActive) ItemUseCoordinator.ApplyControls();
				RecordSystem.CaptureFrame(Player);
				return;
			}

			// platdown: 踩着平台一格一格往下降。放平台要用 PlaceAction,所以它的控制也得跟着发
			if (PlatformDown.IsRunning)
			{
				PlatformDown.Tick();
				if (ItemUseCoordinator.IsActive) ItemUseCoordinator.ApplyControls();
				RecordSystem.CaptureFrame(Player);
				return;
			}

			// place-walls: places background walls at an ordered cell list, jumping for the ones too high.
			if (PlaceWalls.IsRunning)
			{
				PlaceWalls.Tick();
				RecordSystem.CaptureFrame(Player);
				return;
			}

			// walk-place: walks to a column, dropping furniture at in-reach targets along the way.
			if (WalkPlace.IsRunning)
			{
				WalkPlace.Tick();
				RecordSystem.CaptureFrame(Player);
				return;
			}

			// drop: falls through the platform underfoot to the solid ground below (roof → base).
			if (DropDown.IsRunning)
			{
				DropDown.Tick();
				RecordSystem.CaptureFrame(Player);
				return;
			}

			// settle: brakes to a stop on a target column, writing movement keys itself.
			if (SettleAt.IsRunning)
			{
				SettleAt.Tick();
				RecordSystem.CaptureFrame(Player);
				return;
			}

			// hop-up: writes controlJump itself until the player is standing on the target row.
			if (HopUp.IsRunning)
			{
				HopUp.Tick();
				// hop 卡住时会挖头顶那几格。挖走 ItemUseCoordinator,而这里直接 return,
				// 不替它发控制帧的话镐永远挥不动 -- 指令发了、人不动、还是跳满 300 帧
				if (ItemUseCoordinator.IsActive) ItemUseCoordinator.ApplyControls();
				RecordSystem.CaptureFrame(Player);
				return;
			}

			// house: pure orchestration over the other primitives. Ticked BEFORE them so a step it starts
			// this frame is driven immediately; it writes no controls itself.
			if (HouseBuilder.IsRunning) HouseBuilder.Tick();
			if (DeckBuilder.IsRunning) { DeckBuilder.Tick(); _deckStarve = 0; }
			// 桥铺完 → 肉山那一套(等天黑→买雷管→换向导→捅进岩浆)
			if (_wofAfterDeck && !DeckBuilder.IsRunning && !WofPrep.IsRunning)
			{
				_wofAfterDeck = false;
				if (DeckBuilder.Outcome == "done")
				{
					int wdir = ActExecutor.OriginCx(Main.LocalPlayer) < Main.maxTilesX / 2 ? 1 : -1;
					if (WofPrep.Start(HouseBuilder.TorchWx, HouseBuilder.TorchWy, wdir, out string ww))
						Chatter.Say("[TerraBlind] 桥好了,开始肉山流程", 120, 255, 120);
					else Chatter.Say($"[TerraBlind] 起不了:{ww}", 255, 120, 120);
				}
			}
			if (WofPrep.IsRunning) WofPrep.Tick();

			// bridge: same deal — its walk phase writes the movement keys itself, so it owns the frame while running.
			if (BridgeBuilder.IsRunning)
			{
				BridgeBuilder.Tick();
				if (ItemUseCoordinator.IsActive) ItemUseCoordinator.ApplyControls();
				RecordSystem.CaptureFrame(Player);
				return;
			}

			// /act takes the wheel outright: it is the raw action primitive the LLM drives by hand, so nothing else may
			// write controls underneath it. First in, and it returns — nav/mine/place all stand down while it runs.
			if (ActExecutor.IsActive)
			{
				ActExecutor.ApplyControls();
				RecordSystem.CaptureFrame(Player);
				BuildRecorder.Tick(Player);
				return;
			}

			// direction-explore drives StateSpacePlanner leg-by-leg; run it before TickBlocks so a freshly dispatched
			// leg gets stepped this frame.
			ExploreCoordinator.ApplyControls();

			// 必须在 RecedingNav.Tick 之前:它这一帧起的 nav 要靠同帧的 Tick 驱动
			BuildReplayer.Tick();

			TreasureGrab.Tick();  // 必须在 RecedingNav 之前:它这一帧起的 nav 要靠同帧的 Tick 驱动
			RecedingNav.Tick();   // receding-horizon (K): plan next short window from real pos, dispatch; below drives it
			StateSpacePlanner.TickBlocks();
			StateSpacePlanner.BlockNavTick();   // block-nav driver: advance to next chunk when current single-point leg finishes

			if (StateSpacePlanner.IsActive)
			{
				StateSpacePlanner.ApplyControls();
				return;
			}
			if (NavCoordinator.IsActive)
			{
				NavCoordinator.ApplyControls();
				ReplaySystem.ApplyControls();
				PlaceCoordinator.ApplyControls();
				JumpCoordinator.ApplyControls();
				SkillExecutor.ApplyControls();
				return;
			}
			if (MineCoordinator.IsActive)
			{
				MineCoordinator.ApplyControls();
				return;
			}
			if (ItemUseCoordinator.IsActive)
			{
				ItemUseCoordinator.ApplyControls();
				return;
			}
			if (SkillExecutor.IsActive)
			{
				SkillExecutor.ApplyControls();
				ReplaySystem.ApplyControls();
				PlaceCoordinator.ApplyControls();
				return;
			}
			if (ReplaySystem.IsActive)
			{
				ReplaySystem.ApplyControls();
				return;
			}
			if (JumpCoordinator.IsActive)
			{
				JumpCoordinator.ApplyControls();
				return;
			}
			if (WalkCoordinator.IsActive)
			{
				WalkCoordinator.ApplyControls();
				RecordSystem.CaptureFrame(Player);
				BuildRecorder.Tick(Player);
				return;
			}
			bool placeActive = PlaceCoordinator.IsActive;
			PlaceCoordinator.ApplyControls();
			var ci = HttpServerSystem.PendingControl;

			int jflBefore = _jumpFramesLeft;
			bool ciJumpIn = ci != null && ci.Jump;
			bool ciLeftIn = ci != null && ci.Left;
			bool ciRightIn = ci != null && ci.Right;
			bool ciDownIn = ci != null && ci.Down;
			long ciAge = ci != null ? (long)Main.GameUpdateCount - ci.Tick : -1;

			if (_jumpFramesLeft > 0)
			{
				Player.controlJump = true;
				_jumpFramesLeft--;
				if (_jumpFramesLeft == 0) _jumpFramesLeft = -1;
			}
			else if (_jumpFramesLeft == -1)
			{
				_jumpFramesLeft = 0;
			}

			bool jumpFromAuto = false, jumpFromCi = false;
			bool walking = false, blocked = false;

			FightCoordinator.Tick(Player);
			if (WalkTraceEnabled && (System.Math.Abs(Player.velocity.X) > 0.02f || System.Math.Abs(Player.velocity.Y) > 0.02f))
				DiagLog.Write($"[jump-trace] py={Player.position.Y:0.##} vy={Player.velocity.Y:0.###} jump={Player.jump} feetCy={(int)((Player.position.Y + Player.height) / 16f)} wet={Player.wet} jumpH={Player.jumpHeight}");
			if (ci == null)
			{
				if (placeActive || jflBefore != 0)
					DiagLog.JumpTrace($"jfl={jflBefore}->{_jumpFramesLeft} ci=null place={placeActive} cJ={Player.controlJump}");
				bool jumpActive = jflBefore > 0 || Player.controlJump;
				RecordSystem.CaptureFrame(Player, jumpActive);
				BuildRecorder.Tick(Player);
				return;
			}
			if (ciAge > ControlTimeoutTicks)
			{
				DiagLog.JumpTrace($"jfl={jflBefore}->{_jumpFramesLeft} ci=EXPIRED age={ciAge} place={placeActive}");
				HttpServerSystem.PendingControl = null;
				RecordSystem.CaptureFrame(Player, jflBefore > 0);
				BuildRecorder.Tick(Player);
				return;
			}
			if (ci.Left) Player.controlLeft = true;
			if (ci.Right) Player.controlRight = true;
			if (ci.Up) Player.controlUp = true;
			if (ci.Down) Player.controlDown = true;
			walking = (ci.Left || ci.Right) && !ci.Down;
			bool onGround = Player.velocity.Y == 0f;
			blocked = onGround && System.Math.Abs(Player.velocity.X) < 0.1f;
			if (_autoJumpCooldown > 0) _autoJumpCooldown--;
			if (walking && blocked && _jumpFramesLeft == 0 && _autoJumpCooldown == 0)
			{
				_jumpFramesLeft = JumpHoldFrames;
				Player.controlJump = true;
				jumpFromAuto = true;
			}
			if (ci.Jump && _jumpFramesLeft == 0)
			{
				_jumpFramesLeft = JumpHoldFrames;
				Player.controlJump = true;
				ci.Jump = false;
				jumpFromCi = true;
			}
			if (ci.UseItem) Player.controlUseItem = true;
			if (ci.UseTile) Player.controlUseTile = true;
			if (ci.SmartCursor >= 0) Main.SmartCursorWanted_Mouse = ci.SmartCursor == 1;
			if (ci.SelectedSlot >= 0 && ci.SelectedSlot <= 9)
				Player.selectedItem = ci.SelectedSlot;
			if (!float.IsNaN(ci.Mx) && !float.IsNaN(ci.My))
			{
				Cursor.AimOffset(Player, ci.Mx, ci.My);
			}

			if (placeActive || ciJumpIn || jumpFromAuto || jumpFromCi || jflBefore != 0)
			{
				DiagLog.JumpTrace(
					$"jfl={jflBefore}->{_jumpFramesLeft} ciJ={ciJumpIn} ciL={ciLeftIn} ciR={ciRightIn} ciD={ciDownIn} " +
					$"age={ciAge} walk={walking} blk={blocked} vy={Player.velocity.Y:F2} vx={Player.velocity.X:F2} " +
					$"place={placeActive} autoJ={jumpFromAuto} ciJfire={jumpFromCi} cJ={Player.controlJump} cUI={Player.controlUseItem}"
				);
			}
			RecordSystem.CaptureFrame(Player, jflBefore > 0 || jumpFromCi);
			BuildRecorder.Tick(Player);
		}

		public override void PostUpdate()
		{
			if (Player != Main.LocalPlayer) return;
			// speed fields are baked (×moveSpeed) LATE in Player.Update — only here are they trustworthy for planning
			PhysicsSimulator.CaptureBaked(Player);
			PlatformStock.Tick();
			// 手动开的观测工具,和自动化无关 —— 所以挂在这条无条件的更新上
			DynamiteMeter.Tick();

			var snap = new Snapshot
			{
				Tick = (long)Main.GameUpdateCount,
				Player = new PlayerSnapshot
				{
					Hp = Player.statLife,
					MaxHp = Player.statLifeMax2,
					Mana = Player.statMana,
					MaxMana = Player.statManaMax2,
					PosX = Player.position.X,
					PosY = Player.position.Y,
					VelX = Player.velocity.X,
					VelY = Player.velocity.Y,
					Width = Player.width,
					Height = Player.height,
					Direction = Player.direction >= 0 ? "right" : "left",
					OnGround = Player.velocity.Y == 0f,
					InLiquid = Player.wet,
					Biome = DetectBiome(),
					Defense = Player.statDefense,
					MinionSlots = (int)Player.slotsMinions,
					MaxMinionSlots = Player.maxMinions,
					Coins = (int)System.Math.Min(int.MaxValue, Terraria.Utils.CoinsCount(out _, Player.inventory)),
				},
				World = BuildWorld(),
				Equipment = BuildEquipment(),
				Camera = new CameraSnapshot
				{
					ScreenPosX = Main.screenPosition.X,
					ScreenPosY = Main.screenPosition.Y,
					ScreenWidth = Main.screenWidth,
					ScreenHeight = Main.screenHeight,
					Zoom = Main.GameZoomTarget,
				},
				WalkToEdgeDone = WalkCoordinator.Done,
				Movement = BuildMovement(),
				Buffs = BuildBuffs(),
				Enemies = BuildEnemies(),
				TownNpcs = BuildTownNpcs(),
				Tiles = BuildTiles(),
				Objects = BuildObjects(),
				DroppedItems = BuildDroppedItems(),
				NearbyStations = BuildNearbyStations(),
				AvailableRecipes = BuildAvailableRecipes(),
				DetectedTiles = BuildDetectedTiles(),
			};

			HttpServerSystem.LatestSnapshot = snap;
			PhysicsRecorder.Capture(Player, Player.controlJump);
		}

		private WorldSnapshot BuildWorld()
		{
			// Terraria clock: Main.time counts ticks into the current segment. Day starts at 4:30am and lasts 54000
			// ticks; night starts at 7:30pm and lasts 32400. Convert to a 24h wall clock the way the in-game clock does.
			double t = Main.time;
			double hours;
			if (Main.dayTime) hours = t / 3600.0 + 4.5;          // day segment → 4:30 .. 19:30
			else hours = t / 3600.0 + 19.5;                      // night segment → 19:30 .. 4:30(+24)
			hours %= 24.0;
			int hh = (int)hours;
			int mm = (int)((hours - hh) * 60);

			string evt = "";
			if (Main.invasionType > 0) evt = Main.invasionType switch {
				1 => "goblin_army", 2 => "frost_legion", 3 => "pirates", 4 => "martians", _ => "invasion" };
			else if (Main.eclipse) evt = "eclipse";
			else if (Main.bloodMoon) evt = "blood_moon";

			return new WorldSnapshot
			{
				DayTime = Main.dayTime,
				Time = Main.time,
				Clock = $"{hh:D2}:{mm:D2}",
				Raining = Main.raining,
				RainIntensity = Main.raining ? Main.maxRaining : 0f,
				Sandstorm = Player.ZoneSandstorm,
				Hardmode = Main.hardMode,
				BloodMoon = Main.bloodMoon,
				Eclipse = Main.eclipse,
				DownedEyeOfCthulhu = NPC.downedBoss1,
				DownedEvilBoss = NPC.downedBoss2,
				DownedSkeletron = NPC.downedBoss3,
				DownedWallOfFlesh = Main.hardMode,   // hardmode is entered exactly by killing WoF
				ActiveEvent = evt,
			};
		}

		private string DetectBiome()
		{
			if (Player.ZoneJungle) return "jungle";
			if (Player.ZoneDungeon) return "dungeon";
			if (Player.ZoneCorrupt) return "corruption";
			if (Player.ZoneCrimson) return "crimson";
			if (Player.ZoneHallow) return "hallow";
			if (Player.ZoneSnow) return "snow";
			if (Player.ZoneDesert) return "desert";
			if (Player.ZoneBeach) return "ocean";
			if (Player.ZoneUnderworldHeight) return "underworld";
			if (Player.ZoneRockLayerHeight) return "cavern";
			if (Player.ZoneDirtLayerHeight) return "underground";
			if (Player.ZoneSkyHeight) return "sky";
			return "forest";
		}

		private EquipmentSnapshot BuildEquipment()
		{
			var eq = new EquipmentSnapshot
			{
				SelectedSlot = Player.selectedItem,
				HeldItem = ItemToSlot(Player.HeldItem),
				InventoryOpen = Main.playerInventory,
				ChestOpen = Player.chest != -1,
				SmartCursor = Main.SmartCursorWanted,
			};
			for (int i = 0; i < 10; i++)
			{
				eq.Hotbar[i] = ItemToSlot(Player.inventory[i]);
			}
			for (int i = 0; i < 40; i++)
			{
				eq.Inventory[i] = ItemToSlot(Player.inventory[i + 10]);
			}
			for (int i = 0; i < 4; i++)
			{
				eq.Coins[i] = ItemToSlot(Player.inventory[i + 50]);
			}
			for (int i = 0; i < 4; i++)
			{
				eq.Ammo[i] = ItemToSlot(Player.inventory[i + 54]);
			}
			return eq;
		}

		// 测试用:背包里挑个能铺的 —— 平台优先,没有就拿存量最多的方块。
		private static string BridgeTestItem(Player p)
		{
			var td = Terraria.ID.TileID.Sets.Platforms;
			for (int i = 0; i < p.inventory.Length; i++)
			{
				var it = p.inventory[i];
				if (it == null || it.IsAir || it.createTile < 0) continue;
				if (td != null && it.createTile < td.Length && td[it.createTile]) return it.Name;
			}
			string best = "木平台"; int bestStack = 0;
			for (int i = 0; i < p.inventory.Length; i++)
			{
				var it = p.inventory[i];
				if (it == null || it.IsAir || it.createTile < 0) continue;
				if (it.stack > bestStack) { bestStack = it.stack; best = it.Name; }
			}
			return best;
		}

		private static HotbarSlot ItemToSlot(Item item)
		{
			if (item == null || item.IsAir)
			{
				return new HotbarSlot { Id = 0, Name = "", Stack = 0 };
			}
			return new HotbarSlot
			{
				Id = item.type,
				Name = item.Name ?? "",
				Stack = item.stack,
				Damage = item.damage,
				Pick = item.pick,
				Axe = item.axe,
				Hammer = item.hammer,
				CreateTile = item.createTile,
				Consumable = item.consumable,
				Category = ClassifyItem(item),
			};
		}

		private static string ClassifyItem(Item item)
		{
			if (item.pick > 0) return "pickaxe";
			if (item.axe > 0) return "axe";
			if (item.hammer > 0) return "hammer";
			if (item.createTile >= 0)
			{
				if (TileID.Sets.Platforms[item.createTile]) return "platform";
				if (TileID.Sets.Torch[item.createTile]) return "torch";
				return "block";
			}
			if (item.createWall >= 0) return "wall";
			if (item.ammo != AmmoID.None) return "ammo";
			if (item.damage > 0) return "weapon";
			if (item.consumable) return "consumable";
			return "misc";
		}

		private const float EnemyHalfWidthTiles = 60f;
		private const float EnemyHalfHeightTiles = 36f;
		private const float TileSize = 16f;

		private EnemyEntry[] BuildEnemies()
		{
			var list = new System.Collections.Generic.List<EnemyEntry>();
			float pcx = Player.position.X + Player.width / 2f;
			float pcy = Player.position.Y + Player.height / 2f;
			float halfW = EnemyHalfWidthTiles * TileSize;
			float halfH = EnemyHalfHeightTiles * TileSize;
			for (int i = 0; i < Main.maxNPCs; i++)
			{
				NPC npc = Main.npc[i];
				if (npc == null || !npc.active) continue;
				if (npc.townNPC || npc.friendly) continue;
				if (npc.lifeMax <= 5 && npc.damage == 0) continue;
				float ncx = npc.position.X + npc.width / 2f;
				float ncy = npc.position.Y + npc.height / 2f;
				if (System.Math.Abs(ncx - pcx) > halfW) continue;
				if (System.Math.Abs(ncy - pcy) > halfH) continue;
				list.Add(new EnemyEntry
				{
					WhoAmI = npc.whoAmI,
					Type = npc.type,
					Name = npc.TypeName ?? "",
					PosX = npc.position.X,
					PosY = npc.position.Y,
					VelX = npc.velocity.X,
					VelY = npc.velocity.Y,
					Width = npc.width,
					Height = npc.height,
					Hp = npc.life,
					MaxHp = npc.lifeMax,
					Boss = npc.boss,
					ScreenX = (ncx - Main.screenPosition.X) * Main.GameZoomTarget,
					ScreenY = (ncy - Main.screenPosition.Y) * Main.GameZoomTarget,
				});
			}
			return list.ToArray();
		}

		private TownNpcEntry[] BuildTownNpcs()
		{
			var list = new System.Collections.Generic.List<TownNpcEntry>();
			for (int i = 0; i < Main.maxNPCs; i++)
			{
				NPC npc = Main.npc[i];
				if (npc == null || !npc.active) continue;
				if (!npc.townNPC) continue;
				list.Add(new TownNpcEntry
				{
					WhoAmI = npc.whoAmI,
					Type = npc.type,
					Name = npc.GivenOrTypeName ?? "",
					DisplayName = npc.TypeName ?? "",
					PosX = npc.position.X,
					PosY = npc.position.Y,
					Homeless = npc.homeless,
				});
			}
			return list.ToArray();
		}

		private const int TileWindowWidth = 120;
		private const int TileWindowHeight = 70;

		private TileWindowSnapshot BuildTiles()
		{
			int pcx = (int)((Player.position.X + Player.width / 2f) / 16f);
			int pcy = (int)((Player.position.Y + Player.height / 2f) / 16f);
			int ox = pcx - TileWindowWidth / 2;
			int oy = pcy - TileWindowHeight / 2;

			var rows = new TileRun[TileWindowHeight][];
			var runBuf = new System.Collections.Generic.List<TileRun>(32);

			for (int ry = 0; ry < TileWindowHeight; ry++)
			{
				runBuf.Clear();
				int wy = oy + ry;
				TileRun cur = default;
				bool has = false;
				for (int rx = 0; rx < TileWindowWidth; rx++)
				{
					int wx = ox + rx;
					ushort type = 0;
					byte sflags = 0;
					if (wx >= 0 && wx < Main.maxTilesX && wy >= 0 && wy < Main.maxTilesY)
					{
						Tile t = Main.tile[wx, wy];
						if (t.HasTile)
						{
							type = t.TileType;
							sflags |= 1;
							if (Main.tileSolid[type]) sflags |= 2;
								if (Main.tileSolidTop[type]) sflags |= 64;
							// 128 = slope or half-brick (non-full collision shape); not visible otherwise
							if ((int)t.Slope != 0 || t.IsHalfBlock) sflags |= 128;
						}
						if (t.LiquidAmount > 0)
						{
							if (t.LiquidType == LiquidID.Water) sflags |= 4;
							else if (t.LiquidType == LiquidID.Lava) sflags |= 8;
							else if (t.LiquidType == LiquidID.Honey) sflags |= 16;
							else if (t.LiquidType == LiquidID.Shimmer) sflags |= 32;
						}
					}
					if (!has)
					{
						cur = new TileRun { Type = type, SFlags = sflags, Count = 1 };
						has = true;
					}
					else if (cur.Type == type && cur.SFlags == sflags && cur.Count < ushort.MaxValue)
					{
						cur.Count++;
					}
					else
					{
						runBuf.Add(cur);
						cur = new TileRun { Type = type, SFlags = sflags, Count = 1 };
					}
				}
				if (has) runBuf.Add(cur);
				rows[ry] = runBuf.ToArray();
			}

			return new TileWindowSnapshot
			{
				OriginTileX = ox,
				OriginTileY = oy,
				Width = TileWindowWidth,
				Height = TileWindowHeight,
				Rows = rows,
			};
		}

		private WorldObjectEntry[] BuildObjects()
		{
			var list = new System.Collections.Generic.List<WorldObjectEntry>();
			int pcx = (int)((Player.position.X + Player.width / 2f) / 16f);
			int pcy = (int)((Player.position.Y + Player.height / 2f) / 16f);
			int ox = pcx - TileWindowWidth / 2;
			int oy = pcy - TileWindowHeight / 2;
			int ex = ox + TileWindowWidth;
			int ey = oy + TileWindowHeight;

			var addedTreeX = new System.Collections.Generic.HashSet<int>();
			for (int wy = oy; wy < ey; wy++)
			{
				if (wy < 0 || wy >= Main.maxTilesY) continue;
				for (int wx = ox; wx < ex; wx++)
				{
					if (wx < 0 || wx >= Main.maxTilesX) continue;
					Tile t = Main.tile[wx, wy];
					if (!t.HasTile) continue;
					ushort type = t.TileType;
					if (TileID.Sets.IsATreeTrunk[type])
					{
						if (addedTreeX.Contains(wx)) continue;
						bool isTop = wy - 1 < 0;
						if (!isTop) { Tile above = Main.tile[wx, wy - 1]; isTop = !above.HasTile || !TileID.Sets.IsATreeTrunk[above.TileType]; }
						if (!isTop) continue;
						addedTreeX.Add(wx);
						int objHeight = 0;
						for (int dy = 0; dy < 60; dy++)
						{
							int sy = wy + dy;
							if (sy < 0 || sy >= Main.maxTilesY) break;
							Tile st = Main.tile[wx, sy];
							if (st.HasTile && TileID.Sets.IsATreeTrunk[st.TileType]) objHeight++;
							else break;
						}
						list.Add(new WorldObjectEntry
						{
							TileX = wx,
							TileY = wy,
							Type = type,
							Name = "tree",
							PosX = wx * 16f,
							PosY = wy * 16f,
							Height = objHeight,
						});
						continue;
					}
					if (t.TileFrameX != 0 || t.TileFrameY != 0) continue;
					string cat = ClassifyTile(type);
					if (cat == null) continue;
					list.Add(new WorldObjectEntry
					{
						TileX = wx,
						TileY = wy,
						Type = type,
						Name = cat,
						PosX = wx * 16f,
						PosY = wy * 16f,
					});
				}
			}
			return list.ToArray();
		}

		private static string ClassifyTile(ushort type)
		{
			if (TileID.Sets.BasicChest[type]) return "chest";
			if (TileID.Sets.BasicDresser[type]) return "dresser";
			if (TileID.Sets.IsATreeTrunk[type]) return "tree";
			if (TileID.Sets.Torch[type]) return "torch";
			if (TileID.Sets.Platforms[type]) return null;
			switch (type)
			{
				case TileID.Containers2: return "chest";
				case TileID.WorkBenches: return "workbench";
				case TileID.Anvils: return "anvil";
				case TileID.MythrilAnvil: return "anvil";
				case TileID.Furnaces: return "furnace";
				case TileID.Hellforge: return "furnace";
				case TileID.AdamantiteForge: return "furnace";
				case TileID.Pots: return "pot";
				case TileID.Signs: return "sign";
				case TileID.Beds: return "bed";
				case TileID.Bottles: return "alchemy";
				case TileID.AlchemyTable: return "alchemy";
				case TileID.CookingPots: return "cooking_pot";
				case TileID.Sawmill: return "sawmill";
				case TileID.TinkerersWorkbench: return "tinkerer";
				case TileID.DemonAltar: return "altar";
				case TileID.Loom: return "loom";
				case TileID.Solidifier: return "solidifier";
				case TileID.HeavyWorkBench: return "workbench";
			}
			return null;
		}

		private DroppedItemEntry[] BuildDroppedItems()
		{
			var list = new System.Collections.Generic.List<DroppedItemEntry>();
			float pcx = Player.position.X + Player.width / 2f;
			float pcy = Player.position.Y + Player.height / 2f;
			float halfW = TileWindowWidth / 2f * 16f;
			float halfH = TileWindowHeight / 2f * 16f;
			for (int i = 0; i < Main.maxItems; i++)
			{
				Item item = Main.item[i];
				if (item == null || !item.active || item.IsAir) continue;
				float ix = item.position.X + item.width / 2f;
				float iy = item.position.Y + item.height / 2f;
				if (System.Math.Abs(ix - pcx) > halfW) continue;
				if (System.Math.Abs(iy - pcy) > halfH) continue;
				list.Add(new DroppedItemEntry
				{
					WhoAmI = i,
					Type = item.type,
					Name = item.Name ?? "",
					Stack = item.stack,
					PosX = item.position.X,
					PosY = item.position.Y,
				});
			}
			return list.ToArray();
		}

		private MovementSnapshot BuildMovement()
		{
			int extraJumps = 0;
			foreach (var jh in Player.ExtraJumps)
			{
				if (jh.Enabled) extraJumps++;
			}
			return new MovementSnapshot
			{
				JumpSpeed = Player.jumpSpeed,
				JumpHeight = Player.jumpHeight,
				Gravity = Player.gravity,
				MaxRunSpeed = Player.maxRunSpeed,
				AccRunSpeed = Player.accRunSpeed,
				WingTimeMax = Player.wingTimeMax,
				NoFallDmg = Player.noFallDmg,
				LavaImmune = Player.lavaImmune,
				LavaTime = Player.lavaMax,
				ExtraJumps = extraJumps,
			};
		}

		private static readonly System.Collections.Generic.Dictionary<int, string> _watchTiles = new System.Collections.Generic.Dictionary<int, string>
		{
			{ 396, "sandstone" },
			{ 397, "hardened_sand" },
		};
		private const int _watchWall = 220;

		private DetectedTileEntry[] BuildDetectedTiles()
		{
			int pcx = (int)((Player.position.X + Player.width / 2f) / 16f);
			int pcy = (int)((Player.position.Y + Player.height / 2f) / 16f);
			int ox = pcx - TileWindowWidth / 2;
			int oy = pcy - TileWindowHeight / 2;
			var found = new System.Collections.Generic.Dictionary<string, DetectedTileEntry>();
			for (int ry = 0; ry < TileWindowHeight; ry++)
			{
				int wy = oy + ry;
				if (wy < 0 || wy >= Main.maxTilesY) continue;
				for (int rx = 0; rx < TileWindowWidth; rx++)
				{
					int wx = ox + rx;
					if (wx < 0 || wx >= Main.maxTilesX) continue;
					Tile t = Main.tile[wx, wy];
					string name = null;
					if (t.HasTile && _watchTiles.TryGetValue(t.TileType, out string tname))
						name = tname;
					else if (t.WallType == _watchWall)
						name = "sandstone_wall";
					if (name == null) continue;
					if (found.ContainsKey(name)) continue;
					found[name] = new DetectedTileEntry
					{
						Name = name,
						TileX = wx,
						TileY = wy,
						RelX = wx - pcx,
						RelY = wy - pcy,
					};
				}
			}
			var arr = new DetectedTileEntry[found.Count];
			found.Values.CopyTo(arr, 0);
			return arr;
		}

		private AvailableRecipeEntry[] BuildAvailableRecipes()
		{
			var list = new System.Collections.Generic.List<AvailableRecipeEntry>();
			for (int ri = 0; ri < Main.numAvailableRecipes; ri++)
			{
				var r = Main.recipe[Main.availableRecipe[ri]];
				var ings = new System.Collections.Generic.List<IngredientEntry>();
				foreach (var req in r.requiredItem)
				{
					if (req.IsAir) continue;
					ings.Add(new IngredientEntry { Name = req.Name ?? "", Count = req.stack });
				}
				list.Add(new AvailableRecipeEntry
				{
					ItemId = r.createItem.type,
					ItemName = r.createItem.Name ?? "",
					ResultStack = r.createItem.stack,
					Ingredients = ings.ToArray(),
				});
			}
			return list.ToArray();
		}

		private string[] BuildNearbyStations()
		{
			var found = new System.Collections.Generic.HashSet<string>();
			int pcx = (int)((Player.position.X + Player.width / 2f) / 16f);
			int pcy = (int)((Player.position.Y + Player.height / 2f) / 16f);
			int radius = 35;
			for (int wy = pcy - radius; wy <= pcy + radius; wy++)
			{
				if (wy < 0 || wy >= Main.maxTilesY) continue;
				for (int wx = pcx - radius; wx <= pcx + radius; wx++)
				{
					if (wx < 0 || wx >= Main.maxTilesX) continue;
					Tile t = Main.tile[wx, wy];
					if (!t.HasTile) continue;
					string cat = ClassifyTile(t.TileType);
					if (cat != null && cat != "chest" && cat != "pot" && cat != "sign" && cat != "bed" && cat != "torch" && cat != "dresser")
						found.Add(cat);
				}
			}
			var arr = new string[found.Count];
			found.CopyTo(arr);
			return arr;
		}

		private BuffEntry[] BuildBuffs()
		{
			var list = new System.Collections.Generic.List<BuffEntry>();
			for (int i = 0; i < Player.buffType.Length; i++)
			{
				int type = Player.buffType[i];
				if (type <= 0) continue;
				int frames = Player.buffTime[i];
				string name;
				try
				{
					name = Lang.GetBuffName(type) ?? "";
					if (string.IsNullOrEmpty(name) || name.StartsWith("Mods.") || name.Contains("BuffName."))
					{
						name = BuffID.Search.GetName(type) ?? ("Buff" + type);
					}
				}
				catch
				{
					name = "Buff" + type;
				}
				list.Add(new BuffEntry
				{
					Id = type,
					Name = name,
					TimeLeft = frames / 60f,
				});
			}
			return list.ToArray();
		}
	}
}
