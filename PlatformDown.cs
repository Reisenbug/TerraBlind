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
		private const int MaxStandFrames = 900;

		// 掉过目标这么多行就认账。给几行余量:落地那一下本来就会过冲一点
		const int PastTargetSlack = 3;
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

		// 平台(19)在 vanilla 里 tileSolid 和 tileSolidTop 【都是 true】(Main.cs:7752)。
		// 原来加了 !tileSolid,于是每一块平台都判成不是平台,站位阶段永远找不到东西 → 超时。
		static bool IsPlat(int x, int y)
		{
			if (!Predicates.InBounds(x, y)) return false;
			var t = Main.tile[x, y];
			return Predicates.IsPlatform(x, y);
		}

		public static void Tick()
		{
			if (!IsRunning) return;
			var p = Main.LocalPlayer;
			if (p == null || !p.active) { Done("stuck", "no_player"); return; }
			if (++_frames > 60 * 180)
			{ Stuck(new Blocker(BlockKind.OutOfReach, _col, _platY, "总超时"), "timeout"); return; }

			int feetY = ActExecutor.OriginCy(p);
			var (bl, br) = Predicates.BodyCols(p);
			// 物块替换必须开着:关着的话对准砖用平台是"放不下",开着才是"换掉"
			if (!p.TileReplacementEnabled) p.builderAccStatus[10] = 0;

			switch (_ph)
			{
				// 站位:人压住的列里找一列脚下是平台。找到就把列和行都钉死,之后不再动身体。
				case Ph.Stand:
					// 每 30 帧报一次现场,不然"什么都没发生"就只能靠猜
					if ((_frames % 30) == 1)
					{
						// 逐列打全:上一版只打首尾两列,人跨三列时中间那列(真正踩着的)恰好没打出来
						var sb = new System.Text.StringBuilder();
						for (int c = bl; c <= br; c++)
							sb.Append(c).Append('=').Append(IsPlat(c, feetY + 1) ? "平台"
								: Predicates.IsSolid(c, feetY + 1) ? "砖" : "空").Append(' ');
						DiagLog.Write($"[platdown] 找平台中 f={_frames} feet={feetY} vy={p.velocity.Y:0.##} 脚下 {sb}");
					}
					// 站位可能要先造落脚点再走过去,比其他相位慢得多,给它单独的预算
					if (++_phaseFrames > MaxStandFrames)
					{
						// 脚下有砖就是地形,没砖就是够不着(悬空)
						var sb2 = Predicates.IsWall(bl, feetY + 1) ? new Blocker(BlockKind.Terrain, bl, feetY + 1, "脚下被占")
							: new Blocker(BlockKind.OutOfReach, bl, feetY + 1, "站不住");
						Stuck(sb2, $"站位超时 vy={p.velocity.Y:0.##} 身子{bl}..{br} 脚下行{feetY + 1}"); return;
					}
					// 【掉过目标行就别再等了】。这一相位只服务"站着往下铺梯子",人悬空时原样 return
					// 等落地 —— 而等的过程中人一直在掉:(2552,1012)那次 61 帧掉到目标行 1044、
					// 241 帧掉到 1084,穿过去 40 行进了岩浆,全程一句话没报。
					// 掉过了就当场报出来,让上层重算(人落地后位置本来就变了,原来那条线也不对了)
					if (feetY > _targetWy + PastTargetSlack)
					{
						Stuck(new Blocker(BlockKind.OutOfReach, _col == int.MinValue ? bl : _col, _targetWy, "掉过头了"),
							$"自由落体掉过目标行:现在{feetY} 目标{_targetWy} vy={p.velocity.Y:0.##}");
						return;
					}
					if (p.velocity.Y != 0f) return;
					// 砖挡着就沉不下去,所以【每一列】都不能是砖(平台不算砖),不是"找到一列平台就开工"
					int plat = int.MinValue, brick = int.MinValue;
					for (int c = bl; c <= br; c++)
					{
						if (IsPlat(c, feetY + 1)) { if (plat == int.MinValue) plat = c; }
						else if (Predicates.IsSolid(c, feetY + 1)) brick = c;
					}
					if (plat != int.MinValue && brick == int.MinValue)
					{
						_col = plat; _platY = feetY + 1;
						DiagLog.Write($"[platdown] 站位 col={_col} 平台在({_col},{_platY}) 身子{bl}..{br}");
						_phaseFrames = 0; _ph = Ph.Place;
						return;
					}
					// 脚下全是砖(从主道下来就是这样)→ 直接【物块替换】成平台:对着那格用平台就换掉了。
					// 挖掉再放会有一帧没落脚点,替换没有这个空窗,人原地就站到平台上。
					if (plat == int.MinValue)
					{
						if (!PlaceAction.IsRunning)
						{
							PlaceAction.Start(_item, bl, feetY + 1, 1, 0, 0, true, out _);
							DiagLog.Write($"[platdown] 脚下全砖 → 替换({bl},{feetY + 1})成平台");
						}
						return;
					}
					// 旁边那列有砖挡着沉不下去 —— 一样替换掉,不用绕路走位
					if (!PlaceAction.IsRunning)
					{
						PlaceAction.Start(_item, brick, feetY + 1, 1, 0, 0, true, out _);
						DiagLog.Write($"[platdown] 砖{brick}挡着 → 替换成平台");
					}
					return;

				// 放置:往那块平台的下面一格放。下面是砖就靠物块替换换掉 —— 厚砖层这样一格一格啃,
				// 每一步人都站在平台上,不用先挖穿再铺。
				case Ph.Place:
					if (++_phaseFrames > MaxPhaseFrames)
					{ Stuck(new Blocker(BlockKind.Terrain, _col, _platY + 1, "放不出来"), $"放不出来 ({_col},{_platY + 1})"); return; }
					// 【挡路的才办,空的不铺】。以前人占几列就铺几列,理由是"隔壁那块砖会顶住人,
					// S 按不下去" —— 那个理由只在隔壁【真是砖】时成立。隔壁是空气时多铺的那一格
					// 纯属浪费,而且它自己会变成下一步卡住身子的东西(日志:铺完 704..705,
					// 转头就 unstick 挖(705,1012))。
					// 所以:实心的必须换成平台(不然沉不下去),空的只办站着的那一列。
					int need = int.MinValue;
					for (int c = bl; c <= br; c++)
						if (!IsPlat(c, _platY + 1) && Predicates.IsSolid(c, _platY + 1)) { need = c; break; }
					// 没有砖挡着 → 只要脚下这一列有平台就能往下沉
					if (need == int.MinValue && !IsPlat(_col, _platY + 1)) need = _col;
					if (need == int.MinValue)
					{
						DiagLog.Write($"[platdown] 放好 列{_col} 行{_platY + 1}(身子{bl}..{br})");
						_phaseFrames = 0; _tapped = false; _ph = Ph.Tap;
						return;
					}
					// 到岩浆面就是该停的地方。放【得】进去(按下去那帧会抹掉液体),
					// 但平台放进岩浆会立即烧毁,而这套下降靠踩平台按 S 穿 -- 平台一没人就直接掉下去。
					// 所以这里停住往上报,让上层改用方块或者绕开,别在这儿死等。
					if (!Predicates.IsSolid(need, _platY + 1) && Predicates.IsLava(need, _platY + 1))
					{ Stuck(new Blocker(BlockKind.Hopeless, need, _platY + 1, "岩浆"), $"下面是岩浆 ({need},{_platY + 1})"); return; }
					if (!PlaceAction.IsRunning)
						PlaceAction.Start(_item, need, _platY + 1, 1, 0, 0, true, out _);
					return;

				// 下移:按【一下】S。按住的话人会一路穿到底 —— 之前掉 12 格就是这么来的。
				case Ph.Tap:
					// 沉的过程中身子可能挪列,新压住的那列没铺就又被顶住 —— 回 Place 补上,别在这儿干等
					if (++_phaseFrames > MaxPhaseFrames)
					{
						// 判据必须和 Ph.Place 【一模一样】,否则两个相位互相踢皮球:
						// Tap 说"这列没铺,回去补",Place 说"这列是空的不用铺",来回不停
						for (int c = bl; c <= br; c++)
							if (!IsPlat(c, _platY + 1) && Predicates.IsSolid(c, _platY + 1))
							{
								DiagLog.Write($"[platdown] 穿不下去,列{c}行{_platY + 1}还有砖 → 回去换");
								_phaseFrames = 0; _ph = Ph.Place;
								return;
							}
						// 穿不下去时把周围摊开:身子那 3 行 + 脚下两行,逐列报,别再猜是哪一格顶着
						var dbg = new System.Text.StringBuilder();
						for (int c = bl; c <= br; c++)
							for (int y2 = feetY - 2; y2 <= _platY + 1; y2++)
								dbg.Append($"({c},{y2})=").Append(IsPlat(c, y2) ? "平台"
									: Predicates.IsSolid(c, y2) ? "砖" : Predicates.IsLava(c, y2) ? "岩浆" : "空").Append(' ');
						// 身体自己那 3 行里卡着平台/砖时 S 是穿不下去的(vanilla 只让人穿完全在脚底下的)。
						// 找出是哪一格,交给 Unstick 拆
						var hit = new Blocker(BlockKind.OutOfReach, _col, _platY, "穿不下去");
						for (int c2 = bl; c2 <= br && hit.Kind == BlockKind.OutOfReach; c2++)
							for (int r2 = 0; r2 < 3; r2++)
								if (IsPlat(c2, feetY - r2) || Predicates.IsWall(c2, feetY - r2))
								{ hit = new Blocker(BlockKind.Terrain, c2, feetY - r2, "卡在身体里"); break; }
						Stuck(hit, $"穿不下去 站({_col},{_platY}) 身子{bl}..{br} vy={p.velocity.Y:0.##} | {dbg}");
						return;
					}
					// 一次按不动就隔 20 帧再按一次。按住会一路穿到底,所以是"重按"不是"按住";
					// 只按一次的话,那一帧要是被 vanilla 忽略,就白等满 300 帧。
					if (!_tapped || (_phaseFrames % 20) == 0) { p.controlDown = true; _tapped = true; return; }
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

		// stuck 的唯一出口。救得动就救,救完清计时重来
		static void Stuck(Blocker b, string reason)
		{
			Reason = reason;
			if (Unstick.Handle("platdown", b)) { _phaseFrames = 0; _frames = 0; return; }
			Done("stuck", reason);
		}

		static void Done(string outcome, string reason)
		{
			Outcome = outcome; Reason = reason;
			DiagLog.Write($"[platdown] {outcome.ToUpperInvariant()} {reason} placed={_placed} 踩({_col},{_platY})");
			_ph = outcome == "done" ? Ph.Done : Ph.Idle;
		}
	}
}
