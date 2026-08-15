using Terraria;

namespace TerraBlind
{
	// PLATFORM DOWN — 踩着平台一格一格往下降,直到脚下到达 targetWy。
	//
	// 一格的循环:
	//   1 站位   玩家压住的【每一列】下方都不能有方块(平台不算方块)。不满足就往左或往右对齐
	//   2 放置   在脚下那格的下面一格放平台
	//   3 下沉   按下键穿过去
	//   4 计数   脚比出发那行低了就算一格,回 1
	//
	// 没有失败出口:对不齐就换个方向对,穿不下去就再对一次。唯一的终止是到达 targetWy。
	public static class PlatformDown
	{
		private enum Ph { Idle, Align, Stand, Place, Sink, Done }
		private static Ph _ph = Ph.Idle;

		private static string _item = "";
		private static int _slot = -1;
		private static int _targetWy;
		private static int _placed;
		private static int _frames, _phaseFrames;
		private static int _sinkFrom;
		private static int _alignDir;         // 上一次往哪边让的,不满足就换一边

		private const int MaxPhaseFrames = 240;

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
			_placed = 0; _frames = 0; _phaseFrames = 0; _alignDir = 0;
			Outcome = "running"; Reason = "";
			DiagLog.Write($"[platdown] start feet={ActExecutor.OriginCy(p)} → {_targetWy}");
			_ph = Ph.Stand;
			return true;
		}

		public static void Stop()
		{
			if (Outcome == "running") Outcome = "stopped";
			_ph = Ph.Idle;
		}

		// 站位合格 = 玩家压住的每一列,脚下那格都不是方块。平台不是方块(IsSolid 对平台为 false)。
		static bool Standable(Player p, out int l, out int r)
		{
			(l, r) = Predicates.BodyCols(p);
			int fy = ActExecutor.OriginCy(p) + 1;
			for (int c = l; c <= r; c++)
				if (Predicates.IsSolid(c, fy)) return false;
			return true;
		}

		// 踩着的是不是平台:每一列都不是方块之后,至少得有一列脚下有平台,否则人是悬空的
		static bool OnPlatform(Player p)
		{
			var (l, r) = Predicates.BodyCols(p);
			int fy = ActExecutor.OriginCy(p) + 1;
			for (int c = l; c <= r; c++)
				if (Predicates.IsGround(c, fy)) return true;
			return false;
		}

		public static void Tick()
		{
			if (!IsRunning) return;
			var p = Main.LocalPlayer;
			if (p == null || !p.active) { Outcome = "stuck"; Reason = "no_player"; _ph = Ph.Idle; return; }
			if (++_frames > 60 * 180) { Outcome = "stuck"; Reason = "timeout"; _ph = Ph.Idle; return; }

			int feetY = ActExecutor.OriginCy(p);
			if (feetY >= _targetWy)
			{
				Outcome = "done"; _ph = Ph.Done;
				DiagLog.Write($"[platdown] done feet={feetY} placed={_placed}");
				return;
			}

			switch (_ph)
			{
				// 让开方块:20px 宽在 16px 格上必然跨 2~3 列,停列心时边上那列常是砖。
				// 所以按边缘对齐,不按列心;这次不行就换一边,不报错。
				case Ph.Align:
					if (SettleAt.IsRunning) { SettleAt.Tick(); return; }
					_phaseFrames = 0; _ph = Ph.Stand;
					return;

				case Ph.Stand:
					if (p.velocity.Y != 0f) return;
					if (Standable(p, out int sl, out int sr) && OnPlatform(p))
					{ _phaseFrames = 0; _ph = Ph.Place; return; }
					if (++_phaseFrames > MaxPhaseFrames) { _phaseFrames = 0; _alignDir = _alignDir >= 0 ? -1 : 1; }
					StartAlign(p, sl, sr);
					return;

				case Ph.Place:
					if (++_phaseFrames > MaxPhaseFrames) { _phaseFrames = 0; _ph = Ph.Stand; return; }
					int pc = PlaceCol(p);
					if (Predicates.IsGround(pc, feetY + 2))
					{ _sinkFrom = feetY; _phaseFrames = 0; _ph = Ph.Sink; return; }
					if (!PlaceAction.IsRunning)
						PlaceAction.Start(_item, pc, feetY + 2, 1, 0, 0, true, out _);
					return;

				case Ph.Sink:
					if (++_phaseFrames > MaxPhaseFrames) { _phaseFrames = 0; _ph = Ph.Stand; return; }
					p.controlDown = true;
					if (feetY > _sinkFrom)
					{
						_placed++;
						DiagLog.Write($"[platdown] 降 {_sinkFrom}→{feetY} 第{_placed}格");
						_phaseFrames = 0; _ph = Ph.Stand;
					}
					return;
			}
		}

		// 往没有方块的那一侧让。左右都有就先试一边,下一轮 _alignDir 翻面再试另一边。
		static void StartAlign(Player p, int l, int r)
		{
			if (SettleAt.IsRunning) { SettleAt.Tick(); return; }
			int fy = ActExecutor.OriginCy(p) + 1;
			int keep = l;
			for (int c = l; c <= r; c++)
				if (!Predicates.IsSolid(c, fy) && Predicates.IsGround(c, fy)) { keep = c; break; }
			bool blockRight = Predicates.IsSolid(keep + 1, fy);
			int dir = _alignDir != 0 ? _alignDir : (blockRight ? -1 : 1);
			float w = PhysicsSimulator.PlayerW;
			float tpx = dir < 0 ? keep * 16f + 15f - (w - 1f) / 2f : keep * 16f + w / 2f;
			SettleAt.StartPx(keep, tpx, 4f, out _);
			_ph = Ph.Align;
		}

		static int PlaceCol(Player p)
		{
			var (l, r) = Predicates.BodyCols(p);
			int fy = ActExecutor.OriginCy(p) + 1;
			for (int c = l; c <= r; c++)
				if (Predicates.IsGround(c, fy)) return c;
			return l;
		}
	}
}
