using Terraria;

namespace TerraBlind
{
	// 卡住怎么办 —— 只有这一份。
	//
	// 以前每个原语在 Finish("stuck") 之前各自想办法(或者干脆不想),于是同一种阻挡
	// 在 Ph.Lift 里会挖开、在 BridgeBuilder 里就直接失败。类型只有四种,解法就该只有四套。
	//
	// 用法:原语调 Stuck(b) 而不是直接 Finish。这里能解就解,解完让原语重试;
	// 解不了才让它真失败。同一个格子连着卡 HopelessAt 次就升级成无解 —— 挖了还挡着,
	// 再挖一遍也是一样。
	public static class Unstick
	{
		public const int HopelessAt = 3;

		static (int wx, int wy, BlockKind kind) _last;
		static int _repeats;

		public static string LastAction = "";

		public static void Reset() { _last = (int.MinValue, int.MinValue, BlockKind.Hopeless); _repeats = 0; }

		// 能不能解决。true = 已经动手了,调用方这一轮别再往下走,下一轮重试
		public static bool Handle(string who, Blocker b)
		{
			var key = (b.Wx, b.Wy, b.Kind);
			if (key == _last) _repeats++;
			else { _last = key; _repeats = 1; }

			if (b.Kind == BlockKind.Hopeless)
			{
				DiagLog.Write($"[unstick] {who} {b} 真无解,不救");
				return false;
			}
			if (_repeats > HopelessAt)
			{
				DiagLog.Write($"[unstick] {who} {b} 救了{HopelessAt}次还卡着 → 当无解");
				return false;
			}

			var p = Main.LocalPlayer;
			if (p == null) return false;

			bool ok = b.Kind switch
			{
				BlockKind.Terrain => Dig(p, b),
				BlockKind.SelfInWay => StepAside(p, b),
				BlockKind.OutOfReach => Approach(p, b),
				_ => false
			};
			DiagLog.Write($"[unstick] {who} {b} 第{_repeats}次 → {(ok ? LastAction : "没招了")}");
			return ok;
		}

		// 地形挡着:挖掉。ClearWay.Dig 自己会判够不够得着、有没有镐
		static bool Dig(Player p, Blocker b)
		{
			if (ClearWay.Dig(p, b.Wx, b.Wy, "unstick")) { LastAction = $"挖({b.Wx},{b.Wy})"; return true; }
			// 够不着就先靠过去,下一轮再挖
			if (!p.IsInTileInteractionRange(b.Wx, b.Wy, Terraria.DataStructures.TileReachCheckSettings.Simple))
				return Approach(p, b);
			// 平台不归 ClearWay 管(平时穿过去就行),但挡在身体里时就得拆
			if (Predicates.IsPlatform(b.Wx, b.Wy) && !ItemUseCoordinator.IsActive)
			{
				int pk = ClearWay.PickSlot(p);
				if (pk < 0) return false;
				ItemUseCoordinator.Start(new ItemUseRequest { TargetWx = b.Wx, TargetWy = b.Wy, Slot = pk, Strict = true });
				LastAction = $"拆平台({b.Wx},{b.Wy})";
				return true;
			}
			return false;
		}

		// 人自己占着要动的格子:挪到旁边。往离目标格远的那边让
		static bool StepAside(Player p, Blocker b)
		{
			if (SettleAt.IsRunning) { LastAction = "让位中"; return true; }
			int cx = ActExecutor.OriginCx(p);
			int away = cx <= b.Wx ? -1 : 1;
			var (bl, br) = Predicates.BodyCols(p);
			int span = br - bl + 1;
			if (!SettleAt.Start(cx + away * span, out string why)) return false;
			LastAction = $"让到{cx + away * span}列";
			return true;
		}

		// 够不着:先走过去。走不过去(悬空)就给自己造个落脚点 —— 地狱里这是常态
		static bool Approach(Player p, Blocker b)
		{
			if (SettleAt.IsRunning || PillarUp.IsRunning || PlatformDown.IsRunning) { LastAction = "靠近中"; return true; }
			int cx = ActExecutor.OriginCx(p), cy = ActExecutor.OriginCy(p);
			// 同高就横着靠过去:停在够得着的那一列,不是踩到目标头上
			if (System.Math.Abs(b.Wy - cy) <= 1)
			{
				int want = b.Wx + (cx <= b.Wx ? -2 : 2);
				if (SettleAt.Start(want, out _)) { LastAction = $"靠到{want}列"; return true; }
			}
			// 目标在上面:pillar 上去。在下面:平台梯下去。两个都是自造落脚点,不要求那儿有地
			if (b.Wy < cy - 1)
			{
				if (PillarUp.Start("94", cy - b.Wy, cx, out _)) { LastAction = $"pillar升{cy - b.Wy}"; return true; }
			}
			else if (b.Wy > cy + 1)
			{
				if (PlatformDown.Start("94", b.Wy, out _)) { LastAction = $"平台梯降到{b.Wy}"; return true; }
			}
			return false;
		}
	}
}
