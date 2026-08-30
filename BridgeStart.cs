using Terraria;

namespace TerraBlind
{
	// 站上桥起点。这一段【整个重写过】--- 老的 ReachCell 用"先降到目标行再横过去"
	// (platdown 降 / pillar 升 / bridge 横,一次消一个方向),实测一路铺平台梯、
	// 回头拆自己刚铺的平台、身子飘走了 _col 还锁在原列、每卡一次等 300 帧。
	//
	// 现在的做法:走路交给寻路(它有跳放/pillar/bridge/逃逸步,H 场会定价),
	// 这里只管三件寻路不管的事 ---
	//   1. 目标格是空气,站不上去 -> 先放一块,那格才存在
	//   2. 放完站上去
	//   3. 【站住 60 帧才算数】-- 看着到了其实又滑下去,是这一段最常见的假成功
	public static class BridgeStart
	{
		enum Ph { Idle, Goto, Place, Stand, Verify, Done }
		static Ph _ph = Ph.Idle;

		static string _item = "";
		static int _tx, _ty;
		static int _phaseFrames, _held, _tries;

		// 站住这么多帧才认。1 秒 -- 短于此的"到了"都可能是路过
		const int HoldFrames = 60;
		const int MaxPhaseFrames = 60 * 60;
		const int MaxTries = 3;

		public static bool IsRunning => _ph != Ph.Idle && _ph != Ph.Done;
		public static string Outcome = "idle";
		public static string Reason = "";

		public static bool Start(string item, int tx, int ty, out string why)
		{
			why = "";
			var p = Main.LocalPlayer;
			if (p == null) { why = "no_player"; return false; }
			_item = item; _tx = tx; _ty = ty;
			_phaseFrames = 0; _held = 0; _tries = 0;
			Outcome = "running"; Reason = "";
			DiagLog.Write($"[bstart] START 人({ActExecutor.OriginCx(p)},{ActExecutor.OriginCy(p)}) → 桥起点({tx},{ty})");
			// 【Goto 这一步只要够得着 —— 站上去是 Ph.Stand 的事,而且要等方块放好之后】。
			// 桥起点此刻【还是空气】(整个原语就是为了"先放一块再站上去"存在的),
			// 这时要求 Mode.Stand 等于让 A* 去站一个不存在的落脚点:它只能垫平台去够,
			// 搜不出来就报 unreachable(现场:人已到(784,1050),目标(788,1050)同行差4列,
			// 却连报 3 次 no plan)。方块放好之后 Ph.Stand 再用 Mode.Stand,那时它是实的。
			//
			// 之前改成 Stand 是为了躲另一个坑:Reach 停得远 → PlaceAnywhere 接 9 格锚点链 →
			// 把人砌在墙里。那个坑已在 PlaceAnywhere 单独堵了(SealsPlayer:链绝不砌进头顶 3 行)。
			RecedingNav.Start(tx, ty, RecedingNav.Mode.Reach);
			_ph = Ph.Goto;
			return true;
		}

		public static void Stop()
		{
			if (Outcome == "running") Outcome = "stopped";
			RecedingNav.Stop(); PlaceAnywhere.Stop(); SettleAt.Stop();
			_ph = Ph.Idle;
		}

		static void Fail(string why)
		{
			Outcome = "stuck"; Reason = why; _ph = Ph.Done;
			DiagLog.Write($"[bstart] STUCK {why}");
		}

		public static void Tick()
		{
			if (!IsRunning) return;
			var p = Main.LocalPlayer;
			if (p == null) return;
			if (++_phaseFrames > MaxPhaseFrames) { Fail($"相位{_ph}超时"); return; }

			switch (_ph)
			{
				// 走过去。够得着就行 -- Mode.Reach 的到达判据就是原版的交互距离,
				// 和"放得出方块"同一把尺子
				case Ph.Goto:
					if (RecedingNav.Active) return;
					if (RecedingNav.LastStop != "done")
					{ Fail($"寻路没到:{RecedingNav.LastStop}"); return; }
					_phaseFrames = 0; _ph = Ph.Place;
					return;

				// 目标格是空气,先放一块。放完它才是"能站的一格"
				case Ph.Place:
					if (PlaceAnywhere.IsRunning) return;
					if (Predicates.IsGround(_tx, _ty))
					{ _phaseFrames = 0; _ph = Ph.Stand; return; }
					if (PlaceAnywhere.Outcome == "stuck")
					{ Fail($"桥起点({_tx},{_ty})放不上:{PlaceAnywhere.Reason}"); return; }
					if (!PlaceAnywhere.Start(_item, _tx, _ty, out string pw))
					{ Fail($"放不了({_tx},{_ty}):{pw}"); return; }
					return;

				// 站上去。这一格现在是实的,【走过去的活交给寻路】
				case Ph.Stand:
					if (RecedingNav.Active || SettleAt.IsRunning || HopUp.IsRunning || DropDown.IsRunning) return;
					{
						int cx = Predicates.PillarCol(p), cy = ActExecutor.OriginCy(p);
						// 踩在目标头顶那一行 = 到位,进验收
						if (cx == _tx && cy == _ty - 1 && p.velocity.Y == 0f)
						{ _phaseFrames = 0; _held = 0; _ph = Ph.Verify; return; }
						if (++_tries > MaxTries) { Fail($"站不上桥起点({_tx},{_ty}),现在({cx},{cy})"); return; }
						// 【差一格才自己挪,差得多交寻路】。原来一律"先对列再对高度",
						// 而 SettleAt 只会横移 —— 人在(2068,1057)、目标(2067,1054)差3行时
						// 横移一辈子也上不去,三次重试全打在同一个位置然后 STUCK。
						// 寻路会跳会搭会挖,这种一跳的事它自己就办了
						int dx = System.Math.Abs(cx - _tx), dy = System.Math.Abs(cy - (_ty - 1));
						if (dx <= 1 && dy == 0) { SettleAt.Start(_tx, out _); return; }
						DiagLog.Write($"[bstart] 站位差({dx},{dy}) → 交寻路 ({_tx},{_ty - 1}) 第{_tries}次");
						// 上一轮寻路怎么结束的要报出来,不然三次失败之后只知道"站不上",
						// 不知道是走不到还是走到了没站稳
						if (_tries > 1 && RecedingNav.LastStop != null && RecedingNav.LastStop != "done")
							DiagLog.Write($"[bstart] 上一轮寻路结果={RecedingNav.LastStop}");
						RecedingNav.Start(_tx, _ty - 1, RecedingNav.Mode.Stand);
					}
					return;

				// 【站住 60 帧才算数】。这一段最常见的假成功就是"到了"然后滑下去 --
				// 残余横速把人冲出那一格,或者脚下那块被后面的动作碰掉
				case Ph.Verify:
					{
						int cx = Predicates.PillarCol(p), cy = ActExecutor.OriginCy(p);
						bool onSpot = cx == _tx && cy == _ty - 1 && p.velocity.Y == 0f;
						if (!onSpot)
						{
							DiagLog.Write($"[bstart] 站了{_held}帧就掉了:现在({cx},{cy}) vy={p.velocity.Y:0.##} → 重站");
							// 【滑落不算"站不上去"】,重试配额得还回去 —— 不然掉两次就把
							// Ph.Stand 的三次机会吃光,而它一次都还没真正试过
							_held = 0; _phaseFrames = 0; _tries = 0; _ph = Ph.Place;   // 那一格可能也没了,回去重放
							return;
						}
						if (++_held < HoldFrames) return;
						DiagLog.Write($"[bstart] DONE 站稳{_held}帧 ({_tx},{_ty - 1})");
						Outcome = "done"; _ph = Ph.Done;
					}
					return;
			}
		}
	}
}
