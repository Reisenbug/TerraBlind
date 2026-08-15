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
			return t.HasTile && Main.tileSolidTop[t.TileType];
		}

		public static void Tick()
		{
			if (!IsRunning) return;
			var p = Main.LocalPlayer;
			if (p == null || !p.active) { Done("stuck", "no_player"); return; }
			if (++_frames > 60 * 180) { Done("stuck", "timeout"); return; }

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
					{ Done("stuck", $"站位超时 vy={p.velocity.Y:0.##} 身子{bl}..{br} 脚下行{feetY + 1}"); return; }
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
					{ Done("stuck", $"放不出来 ({_col},{_platY + 1})"); return; }
					// 人占的【每一列】都要处理:只管 _col 的话,隔壁列那块砖会把人顶住,S 按不下去。
					// 第一格是 Stand 逐列办的所以能过,第二格开始只办一列 —— 人就停在第二格。
					int need = int.MinValue;
					for (int c = bl; c <= br; c++)
						if (!IsPlat(c, _platY + 1)) { need = c; break; }
					if (need == int.MinValue)
					{
						DiagLog.Write($"[platdown] 放好 {bl}..{br} 行{_platY + 1}");
						_phaseFrames = 0; _tapped = false; _ph = Ph.Tap;
						return;
					}
					// 空格子里有岩浆时放不进去(vanilla PlaceThing_Tiles_IsBlockedByLava,只管空格子;
					// 砖照样能替换)。在这儿死等没意义 —— 到岩浆面就是该停的地方。
					if (!Predicates.IsSolid(need, _platY + 1) && Predicates.IsLava(need, _platY + 1))
					{ Done("stuck", $"下面是岩浆 ({need},{_platY + 1})"); return; }
					if (!PlaceAction.IsRunning)
						PlaceAction.Start(_item, need, _platY + 1, 1, 0, 0, true, out _);
					return;

				// 下移:按【一下】S。按住的话人会一路穿到底 —— 之前掉 12 格就是这么来的。
				case Ph.Tap:
					// 沉的过程中身子可能挪列,新压住的那列没铺就又被顶住 —— 回 Place 补上,别在这儿干等
					if (++_phaseFrames > MaxPhaseFrames)
					{
						for (int c = bl; c <= br; c++)
							if (!IsPlat(c, _platY + 1))
							{
								DiagLog.Write($"[platdown] 穿不下去,列{c}行{_platY + 1}还没铺 → 回去补");
								_phaseFrames = 0; _ph = Ph.Place;
								return;
							}
						Done("stuck", $"穿不下去 站在({_col},{_platY}) 身子{bl}..{br}");
						return;
					}
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
