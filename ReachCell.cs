using Terraria;

namespace TerraBlind
{
	// REACH — 挪到【够得着目标格】的地方。目标本身常常站不住(悬在岩浆上),
	// 所以到达 = 站在它旁边一格、脚下有实处,伸手能放到它。
	//
	// 三个原语按方向分工,互不干扰:bridge 只改横向(铺人脚下那行)、
	// PlatformDown 只改纵向往下(在人这一列)、PillarUp 只改纵向往上。
	// 一次消一个方向,不交替,所以不会来回震荡。
	public static class ReachCell
	{
		private enum Ph { Idle, Walk, Bridge, Down, Up, Done }
		private static Ph _ph = Ph.Idle;

		private static string _plat = "", _block = "";
		private static int _tx, _ty;      // 要够到的那一格
		private static int _standX;       // 最终要站的列(目标旁边)
		private static int _frames, _phaseFrames, _rounds;
		private static int _lastX, _lastY;

		private const int MaxRounds = 40;
		private const int MaxPhaseFrames = 60 * 40;
		private const int ArmReach = 5;   // 站在旁边一格,伸手够得着

		public static bool IsRunning => _ph != Ph.Idle && _ph != Ph.Done;
		public static string Outcome = "idle";
		public static string Reason = "";

		public static bool Start(string platItem, string blockItem, int tx, int ty, out string why)
		{
			why = "";
			var p = Main.LocalPlayer;
			if (p == null) { why = "no_player"; return false; }
			_plat = platItem; _block = blockItem;
			_tx = tx; _ty = ty;
			// 站目标【靠人这一侧】的那一列:桥从人这边接过去,站过头就等于白铺一段
			_standX = ActExecutor.OriginCx(p) <= tx ? tx - 1 : tx + 1;
			_frames = 0; _phaseFrames = 0; _rounds = 0;
			_lastX = int.MinValue; _lastY = int.MinValue;
			Outcome = "running"; Reason = "";
			DiagLog.Write($"[reach] START 人({ActExecutor.OriginCx(p)},{ActExecutor.OriginCy(p)}) → 够到({tx},{ty}),站位列{_standX}");
			_ph = Ph.Walk;
			return true;
		}

		public static void Stop()
		{
			if (Outcome == "running") Outcome = "stopped";
			BridgeBuilder.Stop(); PlatformDown.Stop(); PillarUp.Stop(); RecedingNav.Stop();
			_ph = Ph.Idle;
		}

		// 够得着 = 站的位置和目标同高、只差一列,而且脚下踩得住
		static bool Arrived(Player p)
		{
			int cx = ActExecutor.OriginCx(p), cy = ActExecutor.OriginCy(p);
			return System.Math.Abs(cx - _tx) <= ArmReach && System.Math.Abs(cy - _ty) <= ArmReach
				&& p.IsInTileInteractionRange(_tx, _ty, Terraria.DataStructures.TileReachCheckSettings.Simple);
		}

		public static void Tick()
		{
			if (!IsRunning) return;
			var p = Main.LocalPlayer;
			if (p == null || !p.active) { Fail("no_player"); return; }
			if (++_frames > 60 * 900) { Fail("timeout"); return; }

			// 子动作在跑就等它,但别无限等
			if (BridgeBuilder.IsRunning || PlatformDown.IsRunning || PillarUp.IsRunning || RecedingNav.Active)
			{
				if (++_phaseFrames > MaxPhaseFrames)
				{ BridgeBuilder.Stop(); PlatformDown.Stop(); PillarUp.Stop(); RecedingNav.Stop(); _phaseFrames = 0; }
				return;
			}

			int cx = ActExecutor.OriginCx(p), cy = ActExecutor.OriginCy(p);
			if (Arrived(p))
			{
				Outcome = "done"; _ph = Ph.Done;
				DiagLog.Write($"[reach] DONE 站({cx},{cy}) 够到({_tx},{_ty}) 用了{_rounds}轮");
				return;
			}

			// 一轮没挪动就是这个方向走不通,换一轮再判 —— 原地重复同一个动作是最没用的死法
			bool moved = cx != _lastX || cy != _lastY;
			if (!moved && _rounds > 0)
				DiagLog.Write($"[reach] 第{_rounds}轮没挪动 站({cx},{cy}) 目标({_tx},{_ty})");
			_lastX = cx; _lastY = cy;
			if (++_rounds > MaxRounds) { Fail($"{MaxRounds}轮还没够到,停在({cx},{cy})"); return; }

			int dx = _standX - cx, dy = _ty - cy;
			_phaseFrames = 0;

			// 先消纵向:横向铺的是【人脚下那行】,高度不对的话铺出来的桥整条都在错的行上
			if (dy > 1)
			{
				DiagLog.Write($"[reach] 轮{_rounds} 降 {cy}→{_ty} (差{dy})");
				if (PlatformDown.Start(_plat, _ty, out string dw)) { _ph = Ph.Down; return; }
				Fail($"降不了:{dw}"); return;
			}
			if (dy < -1)
			{
				DiagLog.Write($"[reach] 轮{_rounds} 升 {cy}→{_ty} (差{-dy})");
				if (PillarUp.Start(_plat, -dy, cx, out string uw)) { _ph = Ph.Up; return; }
				Fail($"升不了:{uw}"); return;
			}
			if (dx != 0)
			{
				// 脚下这行往目标方向铺过去。桥自己就是落脚点,所以不需要那边先有地
				int n = System.Math.Abs(dx);
				DiagLog.Write($"[reach] 轮{_rounds} 横向铺{n}格 {cx}→{_standX}");
				if (BridgeBuilder.Start(_block, dx > 0 ? "right" : "left", n, out string bw))
				{ _ph = Ph.Bridge; return; }
				Fail($"铺不了:{bw}"); return;
			}
			Fail($"站到位了却够不着({_tx},{_ty}) 人在({cx},{cy})");
		}

		static void Fail(string reason)
		{
			Outcome = "stuck"; Reason = reason; _ph = Ph.Idle;
			DiagLog.Write($"[reach] STUCK {reason}");
		}
	}
}
