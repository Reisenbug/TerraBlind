using Terraria;

namespace TerraBlind
{
	// PLATFORM DOWN — 踩着平台一格一格往下降。
	//
	//   1 站位:人站的格子里有一格是平台,记下它的列和行
	//   2 放置:往那块平台的【下面一格】放平台
	//   3 下移:按【一下】S,y+1
	//   重复 2、3
	//
	// 水平位置全程不变,所以对齐只在第 1 步做一次,循环里不再动身体。
	public static class PlatformDown
	{
		private enum Ph { Idle, Stand, Place, Tap, Settle, Done }
		private static Ph _ph = Ph.Idle;

		private static string _item = "";
		private static int _slot = -1;
		private static int _targetWy;
		private static int _col;              // 站住的那一列,开工定死
		private static int _platY;            // 人现在踩着的那块平台在哪一行
		private static int _placed;
		private static int _frames, _phaseFrames;
		private static bool _tapped;          // S 已经按过一下了,松开才能再按

		private const int MaxPhaseFrames = 300;

		public static bool IsRunning => _ph != Ph.Idle && _ph != Ph.Done;
		public static string Outcome = "idle";
		public static string Reason = "";
		public static int Placed => _placed;

		public static bool Start(string itemName, int targetWy, out string why)
		{
			why = "";
			var p = Main.LocalPlayer;
			if (p == null) { why = "no_player"; return false; }
			_slot = PlaceAction.HomeInHotbar(itemName);
			if (_slot < 0) { why = "no_item"; Outcome = "no_item"; Reason = itemName; return false; }
			_item = itemName;
			_targetWy = targetWy;
			_placed = 0; _frames = 0; _phaseFrames = 0; _tapped = false;
			_col = int.MinValue; _platY = int.MinValue;
			Outcome = "running"; Reason = "";
			var (l0, r0) = Predicates.BodyCols(p);
			DiagLog.Write($"[platdown] START feet={ActExecutor.OriginCy(p)} cols={l0}..{r0} → 目标{_targetWy}");
			_ph = Ph.Stand;
			return true;
		}

		public static void Stop()
		{
			if (Outcome == "running") Outcome = "stopped";
			_ph = Ph.Idle;
		}

		static bool IsPlat(int x, int y)
		{
			if (!Predicates.InBounds(x, y)) return false;
			var t = Main.tile[x, y];
			return t.HasTile && Main.tileSolidTop[t.TileType] && !Main.tileSolid[t.TileType];
		}

		public static void Tick()
		{
			if (!IsRunning) return;
			var p = Main.LocalPlayer;
			if (p == null || !p.active) { Done("stuck", "no_player"); return; }
			if (++_frames > 60 * 180) { Done("stuck", "timeout"); return; }

			int feetY = ActExecutor.OriginCy(p);
			var (bl, br) = Predicates.BodyCols(p);

			switch (_ph)
			{
				// 站位:人压住的列里找一列脚下是平台。找到就把列和行都钉死,之后不再动身体。
				case Ph.Stand:
					if (p.velocity.Y != 0f) return;
					for (int c = bl; c <= br; c++)
						if (IsPlat(c, feetY + 1))
						{
							_col = c; _platY = feetY + 1;
							DiagLog.Write($"[platdown] 站位 col={_col} 平台在({_col},{_platY}) 身子{bl}..{br}");
							_phaseFrames = 0; _ph = Ph.Place;
							return;
						}
					if (++_phaseFrames > MaxPhaseFrames)
					{ Done("stuck", $"站不到平台上 身子{bl}..{br} 脚下{feetY + 1}"); return; }
					return;

				// 放置:往那块平台的下面一格放
				case Ph.Place:
					if (++_phaseFrames > MaxPhaseFrames)
					{ Done("stuck", $"放不出来 ({_col},{_platY + 1})"); return; }
					if (IsPlat(_col, _platY + 1))
					{
						DiagLog.Write($"[platdown] 放好 ({_col},{_platY + 1})");
						_phaseFrames = 0; _tapped = false; _ph = Ph.Tap;
						return;
					}
					if (!PlaceAction.IsRunning)
						PlaceAction.Start(_item, _col, _platY + 1, 1, 0, 0, true, out _);
					return;

				// 下移:按【一下】S。按住的话人会一路穿到底 —— 之前掉 12 格就是这么来的。
				case Ph.Tap:
					if (++_phaseFrames > MaxPhaseFrames)
					{ Done("stuck", $"穿不下去 站在({_col},{_platY})"); return; }
					if (!_tapped) { p.controlDown = true; _tapped = true; return; }
					// 落稳了再记账:下落途中 feetY 也在变,那时候记等于把没踩住的位置当成了新起点
					if (p.velocity.Y == 0f && feetY + 1 > _platY)
					{
						_platY = feetY + 1;
						_placed++;
						DiagLog.Write($"[platdown] 降1格 → 现在踩({_col},{_platY}) 第{_placed}格 vy={p.velocity.Y:0.##}");
						if (_platY - 1 >= _targetWy) { Done("done", ""); return; }
						_phaseFrames = 0; _ph = Ph.Place;
						return;
					}
					return;
			}
		}

		static void Done(string outcome, string reason)
		{
			Outcome = outcome; Reason = reason;
			DiagLog.Write($"[platdown] {outcome.ToUpperInvariant()} {reason} placed={_placed} 踩({_col},{_platY})");
			_ph = outcome == "done" ? Ph.Done : Ph.Idle;
		}
	}
}
