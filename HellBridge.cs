using Terraria;

namespace TerraBlind
{
	// HELL BRIDGE — 从人现在站的地方,把 170 格桥建出来。
	//
	//   1 算线   HellLine 定桥面高度和方向
	//   2 竖降   PlatformDown 降到桥面那一行
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
			// 已经在桥面高度就不用降
			if (ActExecutor.OriginCy(p) >= _deckY - 1) return BeginLay(out why);
			if (!PlatformDown.Start(itemName, _deckY - 1, out why)) { Outcome = "stuck"; Reason = why; return false; }
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
			PlatformDown.Stop(); BridgeBuilder.Stop();
			_ph = Ph.Idle;
		}

		public static void Tick()
		{
			if (!IsRunning) return;
			if (++_frames > 60 * 600) { Fail("timeout"); return; }

			switch (_ph)
			{
				case Ph.Down:
					if (PlatformDown.IsRunning) return;
					if (PlatformDown.Outcome != "done")
					{
						// 降到岩浆就算到底了,那本来就是桥该在的高度,不是失败
						if (PlatformDown.Reason.StartsWith("下面是岩浆"))
							DiagLog.Write($"[hellbridge] 降到岩浆面,就地开铺");
						else { Fail($"降不下去:{PlatformDown.Outcome} {PlatformDown.Reason}"); return; }
					}
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
