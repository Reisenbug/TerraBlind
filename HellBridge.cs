using Terraria;

namespace TerraBlind
{
	// HELL BRIDGE — 从人现在站的地方,把 170 格桥建出来。
	//
	//   1 算线   HellLine 定桥面高度和方向
	//   2 下去   寻路(stand 模式)走到桥头 —— 它自己绕岩浆、搭梯子、挖穿
	//   3 横铺   BridgeBuilder 往 dir 铺满 170 格
	//
	// 先竖后横:横向那段并进桥里,不用在半空单独解决走位。
	public static class HellBridge
	{
		private enum Ph { Idle, Down, Lay, Done }
		private static Ph _ph = Ph.Idle;

		private static string _item = "";
		private static int _dir = 1;
		private static int _deckY, _startX;
		private static int _frames;

		public static bool IsRunning => _ph != Ph.Idle && _ph != Ph.Done;
		public static string Outcome = "idle";
		public static string Reason = "";

		public static bool Start(string itemName, out string why)
		{
			why = "";
			var p = Main.LocalPlayer;
			if (p == null) { why = "no_player"; return false; }
			int bx = ActExecutor.OriginCx(p);
			_dir = bx < Main.maxTilesX / 2 ? 1 : -1;
			var hl = HellLine.Compute(bx, _dir);
			if (!hl.Found) { why = hl.Why; Outcome = "stuck"; Reason = hl.Why; return false; }
			_item = itemName;
			_deckY = hl.StartY; _startX = hl.StartX;
			_frames = 0;
			Outcome = "running"; Reason = "";
			DiagLog.Write($"[hellbridge] START 人({bx},{ActExecutor.OriginCy(p)}) 桥面行={_deckY} 桥头列={_startX} dir={_dir} 挖{hl.DigCells}");
			// 交给寻路下去,不用 PlatformDown:岩浆在场里是 LavaCost=100000,小池子它自己就绕开了;
			// 桥头悬在半空 → stand 模式(A* 会自己搭平台梯/挖穿),这正是当初为房子左下角写的那个。
			if (ActExecutor.OriginCy(p) == _deckY - 1 && ActExecutor.OriginCx(p) == _startX)
				return BeginLay(out why);
			RecedingNav.Start(_startX, _deckY - 1, RecedingNav.Mode.Stand);
			_ph = Ph.Down;
			return true;
		}

		static bool BeginLay(out string why)
		{
			var p = Main.LocalPlayer;
			int fy = ActExecutor.OriginCy(p) + 1;
			// 从人脚下那一行铺,不是从规划的 deckY —— 降下来差一两格是常态,按真实落点铺才接得上
			if (!BridgeBuilder.Start(_item, _dir > 0 ? "right" : "left", HellLine.Length,
				ActExecutor.OriginCx(p), fy, out why))
			{ Outcome = "stuck"; Reason = why; _ph = Ph.Idle; return false; }
			DiagLog.Write($"[hellbridge] 开铺 从({ActExecutor.OriginCx(p)},{fy}) 往{(_dir > 0 ? "右" : "左")} {HellLine.Length}格");
			_ph = Ph.Lay;
			return true;
		}

		public static void Stop()
		{
			if (Outcome == "running") Outcome = "stopped";
			RecedingNav.Stop(); BridgeBuilder.Stop();
			_ph = Ph.Idle;
		}

		public static void Tick()
		{
			if (!IsRunning) return;
			if (++_frames > 60 * 600) { Fail("timeout"); return; }

			switch (_ph)
			{
				case Ph.Down:
					if (RecedingNav.Active) return;
					if (RecedingNav.LastStop != "done")
					{ Fail($"到不了桥头({_startX},{_deckY - 1}):{RecedingNav.LastStop}"); return; }
					BeginLay(out _);
					return;

				case Ph.Lay:
					if (BridgeBuilder.IsRunning) return;
					if (BridgeBuilder.Outcome != "done")
					{ Fail($"铺不完:{BridgeBuilder.Outcome} {BridgeBuilder.Reason} 已铺{BridgeBuilder.Placed}"); return; }
					Outcome = "done"; _ph = Ph.Done;
					DiagLog.Write($"[hellbridge] DONE 铺了{BridgeBuilder.Placed}格");
					return;
			}
		}

		static void Fail(string reason)
		{
			Outcome = "stuck"; Reason = reason;
			DiagLog.Write($"[hellbridge] STUCK {reason}");
			_ph = Ph.Idle;
		}
	}
}
