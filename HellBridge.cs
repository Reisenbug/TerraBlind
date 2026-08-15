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
		private static int _laid;        // 累计铺了多少格(跨换料)
		private static string _lastBlock = "";
		private static System.Collections.Generic.List<(int x, int y)> _line;
		private static int _visTick;
		private const int DeckTol = 3;   // 落点和桥面差几行还能接受

		public static bool IsRunning => _ph != Ph.Idle && _ph != Ph.Done;
		public static string Outcome = "idle";
		public static string Reason = "";

		// 桥面要用【方块】不是平台:平台踩得住但雷管停不住。会掉的沙子类不行(TileID.Sets.Falling),
		// 剩下的实心方块都行,背包里挑数量最多的那摞。
		public static int FindBlockSlot(Player p, out int count)
		{
			count = 0;
			int best = -1;
			var falling = Terraria.ID.TileID.Sets.Falling;
			for (int i = 0; i < p.inventory.Length; i++)
			{
				var it = p.inventory[i];
				if (it == null || it.IsAir || it.createTile < 0) continue;
				int t = it.createTile;
				if (t >= Main.tileSolid.Length || !Main.tileSolid[t]) continue;
				if (Terraria.ID.TileID.Sets.Platforms[t]) continue;
				if (falling != null && t < falling.Length && falling[t]) continue;
				if (it.stack > count) { count = it.stack; best = i; }
			}
			return best;
		}

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
			_frames = 0; _laid = 0; _lastBlock = ""; _line = hl.Line; _visTick = 0;
			Repaint();
			Outcome = "running"; Reason = "";
			DiagLog.Write($"[hellbridge] START 人({bx},{ActExecutor.OriginCy(p)}) 桥面行={_deckY} 桥头列={_startX} dir={_dir} 挖{hl.DigCells}");
			// 桥头就在半空 —— 那正是 stand 模式的活:A* 会自己搭平台梯、pillar、挖过去。
			// 之前退回 Snap 是因为它总 fail,但那是 goalSnapCap 的实现 bug,不是它做不到。
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
			// 差太远就别铺:上一版拿人脚下那行当桥面,寻路没到位时就在 1014 铺了 17 格才 walk_blocked,
			// 而桥面本该在 1050。错的位置铺出来的桥比铺不出来更难收拾。
			if (System.Math.Abs(fy - _deckY) > DeckTol)
			{ Outcome = "stuck"; Reason = $"没到桥面:人在{fy},桥面{_deckY}"; why = Reason; _ph = Ph.Idle;
			  DiagLog.Write($"[hellbridge] STUCK {Reason}"); return false; }
			int bslot = FindBlockSlot(p, out int bcount);
			if (bslot < 0)
			{ Outcome = "stuck"; Reason = "背包里没有能铺桥的方块"; why = Reason; _ph = Ph.Idle;
			  DiagLog.Write($"[hellbridge] STUCK {Reason}"); return false; }
			string block = p.inventory[bslot].type.ToString();
			_lastBlock = block;
			// 照着线铺,不是沿一行平推 —— 平推的话图上有坡有挖,实际是一条直线,两边永远对不上
			if (!HellDeck.Start(block, _line, out why))
			{ Outcome = "stuck"; Reason = why; _ph = Ph.Idle; return false; }
			DiagLog.Write($"[hellbridge] 开铺 料={p.inventory[bslot].Name}({block}) 存量{bcount}");
			_ph = Ph.Lay;
			return true;
		}

		// 绿=已铺、青=待铺、黄=拐角锚点。看真实地块不看计数 —— 卡在哪一格要一眼看见。
		static void Repaint()
		{
			if (_line == null) return;
			var vis = new System.Collections.Generic.List<(int, int, Microsoft.Xna.Framework.Color)>();
			var done = new Microsoft.Xna.Framework.Color(80, 255, 120, 190);
			var todo = new Microsoft.Xna.Framework.Color(0, 200, 255, 120);
			var anchor = new Microsoft.Xna.Framework.Color(255, 210, 0, 200);
			foreach (var c in HellDeck.Expand(_line))
				vis.Add((c.X, c.Y, Predicates.IsSolid(c.X, c.Y) ? done : c.Anchor ? anchor : todo));
			PathVisSystem.SetTiles(vis, 240);
		}

		// 换一摞料继续铺。挑不出料/起不来就返回 false,让调用方去报错。
		static bool Relay()
		{
			var p = Main.LocalPlayer;
			int slot = FindBlockSlot(p, out int cnt);
			if (slot < 0) return false;
			string it = p.inventory[slot].type.ToString();
			// 挑出来还是刚才那摞就别重来了 —— 那说明停下另有原因(够不着、被挡),换料解决不了,重启只会死循环
			if (it == _lastBlock) return false;
			_lastBlock = it;
			if (!HellDeck.Start(it, _line, out _)) return false;
			DiagLog.Write($"[hellbridge] 换料 {p.inventory[slot].Name} 存量{cnt},已铺{_laid}");
			return true;
		}

		public static void Stop()
		{
			if (Outcome == "running") Outcome = "stopped";
			RecedingNav.Stop(); HellDeck.Stop();
			_ph = Ph.Idle;
		}

		public static void Tick()
		{
			if (!IsRunning) return;
			if (++_frames > 60 * 600) { Fail("timeout"); return; }
			// 半秒刷一次:ttl 给 240 帧,所以停下之后那张图还留 4 秒,失败现场不会立刻消失
			if (++_visTick % 30 == 0) Repaint();

			switch (_ph)
			{
				case Ph.Down:
					if (RecedingNav.Active) return;
					if (RecedingNav.LastStop != "done")
					{ Fail($"到不了桥头({_startX},{_deckY - 1}):{RecedingNav.LastStop}"); return; }
					BeginLay(out _);
					return;

				case Ph.Lay:
					if (HellDeck.IsRunning) return;
					_laid += HellDeck.Placed;
					// 这摞用光了就换第二多的接着铺,从它停的那一格续 —— 2000 木材铺完还差的那截,
					// 该由 150 泥块顶上,而不是在这儿报"铺不完"。
					if (HellDeck.Outcome != "done" && Relay()) return;
					if (HellDeck.Outcome != "done")
					{ Fail($"铺不完:{HellDeck.Outcome} {HellDeck.Reason} 已铺{_laid}"); return; }
					Outcome = "done"; _ph = Ph.Done;
					DiagLog.Write($"[hellbridge] DONE 铺了{_laid}格");
					return;
			}
		}

		static void Fail(string reason)
		{
			Outcome = "stuck"; Reason = reason;
			DiagLog.Write($"[hellbridge] STUCK {reason}");
			Repaint();   // 停在哪儿断的,图上留着
			_ph = Ph.Idle;
		}
	}
}
