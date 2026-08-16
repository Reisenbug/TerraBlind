using Terraria;

namespace TerraBlind
{
	// 让某一格【放得出方块】。放置要正交邻居,四周全空就只能挥空手。
	// 起点悬在半空是常态(地狱那条线大半都悬空),所以不是换个起点,是自己把锚造出来。
	public static class EnsureAnchor
	{
		private enum Ph { Idle, Place, Verify, Done }
		private static Ph _ph = Ph.Idle;

		private static string _item = "";
		private static int _tx, _ty;
		private static int _ax, _ay;      // 正在放的那一格(锚)
		private static int _frames, _tries;
		// 试过放不上的格子记下来,不然 Pick 每次都挑回同一格,原地挥到超时
		private static readonly System.Collections.Generic.HashSet<(int, int)> _tried = new();

		private const int MaxTries = 4;
		private const int MaxFrames = 60 * 30;

		public static bool IsRunning => _ph != Ph.Idle && _ph != Ph.Done;
		public static string Outcome = "idle";
		public static string Reason = "";

		// 四邻有实心就贴得住。和 HellLine.AnchorScore 同一个判据,别再编第二套。
		public static bool HasAnchor(int x, int y)
			=> Predicates.IsSolid(x - 1, y) || Predicates.IsSolid(x + 1, y)
			|| Predicates.IsSolid(x, y - 1) || Predicates.IsSolid(x, y + 1);

		// 【不预测】能不能放 —— 放置眼会观测目标格有没有长出东西(PlaceAction 的 blocked/placed)。
		// 这里只排除物理上不可能的:界外、已经有东西、熔岩(放不进去)。挡不挡得住交给眼去报。
		static bool Worth(int x, int y)
		{
			if (!Predicates.InBounds(x, y)) return false;
			if (Predicates.IsSolid(x, y)) return false;
			return !Predicates.IsLava(x, y);
		}

		public static bool Start(string itemName, int tx, int ty, out string why)
		{
			why = "";
			var p = Main.LocalPlayer;
			if (p == null) { why = "no_player"; return false; }
			_item = itemName; _tx = tx; _ty = ty;
			_frames = 0; _tries = 0; _tried.Clear();
			Outcome = "running"; Reason = "";
			if (HasAnchor(tx, ty))
			{
				Outcome = "done"; _ph = Ph.Done;
				DiagLog.Write($"[anchor] ({tx},{ty}) 本来就贴得住,不用造");
				return true;
			}
			if (!Pick(out _ax, out _ay))
			{ why = $"({tx},{ty})四邻都放不了锚"; Outcome = "stuck"; Reason = why;
			  DiagLog.Write($"[anchor] STUCK {why} " + Dump()); return false; }
			DiagLog.Write($"[anchor] ({tx},{ty})四周全空 → 先放({_ax},{_ay}) " + Dump());
			_ph = Ph.Place;
			return true;
		}

		// 桥面【下面】优先:它既当锚又当人的落脚点,而且不挡桥上的净空。
		// 左右次之(桥自己要往那边长)。正上方最后 —— 那是人要走的 3 格净空,放了还得挖。
		static bool Pick(out int ax, out int ay)
		{
			foreach (var (dx, dy) in new[] { (0, 1), (-1, 0), (1, 0), (0, -1) })
			{
				int x = _tx + dx, y = _ty + dy;
				if (Worth(x, y) && !_tried.Contains((x, y))) { ax = x; ay = y; return true; }
			}
			ax = ay = 0;
			return false;
		}

		static string Dump()
		{
			string S(int x, int y) => Predicates.IsLava(x, y) ? "浆" : Predicates.IsSolid(x, y) ? "实" : "空";
			return $"上{S(_tx, _ty - 1)}下{S(_tx, _ty + 1)}左{S(_tx - 1, _ty)}右{S(_tx + 1, _ty)}";
		}

		public static void Stop()
		{
			if (Outcome == "running") Outcome = "stopped";
			_ph = Ph.Idle;
		}

		public static void Tick()
		{
			if (!IsRunning) return;
			var p = Main.LocalPlayer;
			if (p == null || !p.active) { Fail("no_player"); return; }
			if (++_frames > MaxFrames) { Fail($"超时 停在({_ax},{_ay})"); return; }

			switch (_ph)
			{
				case Ph.Place:
					if (HasAnchor(_tx, _ty))
					{
						Outcome = "done"; _ph = Ph.Done;
						DiagLog.Write($"[anchor] DONE ({_tx},{_ty}) 现在贴得住了 " + Dump());
						return;
					}
					if (!p.IsInTileInteractionRange(_ax, _ay, Terraria.DataStructures.TileReachCheckSettings.Simple))
					{
						if (_frames % 60 == 1) DiagLog.Write($"[anchor] 够不着({_ax},{_ay}) 人在({ActExecutor.OriginCx(p)},{ActExecutor.OriginCy(p)})");
						int cx = ActExecutor.OriginCx(p);
						if (cx < _ax) p.controlRight = true; else if (cx > _ax) p.controlLeft = true;
						return;
					}
					if (PlaceAction.IsRunning) return;
					// 眼说话:blocked = 这一格放不上,别再挥了,记下来换一格
					if (PlaceAction.Outcome == "blocked")
					{
						_tried.Add((_ax, _ay));
						DiagLog.Write($"[anchor] ({_ax},{_ay})放不上:{PlaceAction.Reason}");
						_ph = Ph.Verify; _frames = 0; return;
					}
					if (Predicates.IsSolid(_ax, _ay)) { _ph = Ph.Verify; _frames = 0; return; }
					PlaceAction.Start(_item, _ax, _ay, 1, 0, 0, true, out _);
					return;

				case Ph.Verify:
					// 放上了但目标还是贴不住 = 挑错了格子,换下一个再来。挑不出来才算真失败。
					if (HasAnchor(_tx, _ty))
					{
						Outcome = "done"; _ph = Ph.Done;
						DiagLog.Write($"[anchor] DONE ({_tx},{_ty}) 靠({_ax},{_ay}) " + Dump());
						return;
					}
					if (++_tries >= MaxTries || !Pick(out _ax, out _ay))
					{ Fail($"放了{_tries}格还是贴不住 " + Dump()); return; }
					DiagLog.Write($"[anchor] 换一格试:({_ax},{_ay}) 第{_tries}次");
					_ph = Ph.Place; _frames = 0;
					return;
			}
		}

		static void Fail(string reason)
		{
			Outcome = "stuck"; Reason = reason; _ph = Ph.Idle;
			DiagLog.Write($"[anchor] STUCK {reason}");
		}
	}
}
