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
		private enum Ph { Idle, Down, House, Lay, Done }
		private static Ph _ph = Ph.Idle;

		private static string _item = "";
		private static int _dir = 1;
		private static int _deckY, _startX;
		private static int _houseX, _houseY;
		private static int _frames;
		private static int _laid;        // 累计铺了多少格(跨换料)
		private static string _lastBlock = "";
		private static System.Collections.Generic.List<(int x, int y)> _line;
		private static int _visTick;

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
			_houseX = hl.HouseX; _houseY = hl.HouseY;
			_frames = 0; _laid = 0; _lastBlock = ""; _line = hl.Line; _visTick = 0;
			Repaint();
			Outcome = "running"; Reason = "";
			DiagLog.Write($"[hellbridge] START 人({bx},{ActExecutor.OriginCy(p)}) 房子=({_houseX},{_houseY}) 岩浆列{hl.HouseLavaCols}/{HellLine.HouseW} " +
				$"桥面行={_deckY} 桥起点列={_startX} dir={_dir} 挖{hl.DigCells}");
			// 先去房子的左下角。它是【放出来的】不是走上去的,所以人只要站旁边够得着。
			int bslot0 = FindBlockSlot(p, out _);
			string blk = bslot0 >= 0 ? p.inventory[bslot0].type.ToString() : itemName;
			if (!ReachCell.Start(itemName, blk, _houseX, _houseY, out why))
			{ Outcome = "stuck"; Reason = why; return false; }
			_ph = Ph.Down;
			return true;
		}

		static bool BeginLay(out string why)
		{
			var p = Main.LocalPlayer;
			// 只要够得着桥头就能开工 —— 人站在它旁边,不是站在它上面(那儿是岩浆)。
			// 不能再拿"人脚下那行"当桥面:桥面由 HellLine 定死,人站哪儿不改变它。
			if (!p.IsInTileInteractionRange(_startX, _deckY, Terraria.DataStructures.TileReachCheckSettings.Simple))
			{ Outcome = "stuck"; Reason = $"够不着桥起点({_startX},{_deckY}) 人在({ActExecutor.OriginCx(p)},{ActExecutor.OriginCy(p)})";
			  why = Reason; _ph = Ph.Idle; DiagLog.Write($"[hellbridge] STUCK {Reason}"); return false; }
			int bslot = FindBlockSlot(p, out int bcount);
			if (bslot < 0)
			{ Outcome = "stuck"; Reason = "背包里没有能铺桥的方块"; why = Reason; _ph = Ph.Idle;
			  DiagLog.Write($"[hellbridge] STUCK {Reason}"); return false; }
			string block = p.inventory[bslot].type.ToString();
			_lastBlock = block;
			// 照着线铺,不是沿一行平推 —— 平推的话图上有坡有挖,实际是一条直线,两边永远对不上
			if (!HellDeck.Start(block, BridgePart(), out why))
			{ Outcome = "stuck"; Reason = why; _ph = Ph.Idle; return false; }
			DiagLog.Write($"[hellbridge] 开铺 料={p.inventory[bslot].Name}({block}) 存量{bcount}");
			_ph = Ph.Lay;
			return true;
		}

		// 线的头 HouseW 列是房子地板,归 HouseBuilder 铺;桥只管剩下那 170 格。
		static System.Collections.Generic.List<(int x, int y)> BridgePart()
			=> _line.GetRange(HellLine.HouseW, _line.Count - HellLine.HouseW);

		// 绿=已铺、青=待铺、黄=拐角锚点、金=房子那几列。看真实地块不看计数 —— 卡在哪一格要一眼看见。
		static void Repaint()
		{
			if (_line == null) return;
			var vis = new System.Collections.Generic.List<(int, int, Microsoft.Xna.Framework.Color)>();
			var done = new Microsoft.Xna.Framework.Color(80, 255, 120, 190);
			var todo = new Microsoft.Xna.Framework.Color(0, 200, 255, 120);
			var anchor = new Microsoft.Xna.Framework.Color(255, 210, 0, 200);
			var house = new Microsoft.Xna.Framework.Color(255, 180, 0, 230);
			for (int i = 0; i < HellLine.HouseW && i < _line.Count; i++)
				vis.Add((_line[i].x, _line[i].y, house));
			foreach (var c in HellDeck.Expand(BridgePart()))
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
			if (!HellDeck.Start(it, BridgePart(), out _)) return false;
			DiagLog.Write($"[hellbridge] 换料 {p.inventory[slot].Name} 存量{cnt},已铺{_laid}");
			return true;
		}

		public static void Stop()
		{
			if (Outcome == "running") Outcome = "stopped";
			ReachCell.Stop(); HouseBuilder.Stop(); HellDeck.Stop();
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
					if (ReachCell.IsRunning) return;
					if (ReachCell.Outcome != "done")
					{ Fail($"够不到房址({_houseX},{_houseY}):{ReachCell.Reason}"); return; }
					// 房子先盖:桥是从房子边上往外接的,房子的地板行就是桥面行
					if (!HouseBuilder.Start(1, _dir, _houseX, _houseY, out string hw))
					{ Fail($"房子起不来:{hw}"); return; }
					DiagLog.Write($"[hellbridge] 盖房子 角=({_houseX},{_houseY}) dir={_dir}");
					_ph = Ph.House;
					return;

				case Ph.House:
					if (HouseBuilder.IsRunning) return;
					if (HouseBuilder.Outcome != "done")
					{ Fail($"房子没盖成:{HouseBuilder.Outcome}/{HouseBuilder.Reason}"); return; }
					DiagLog.Write($"[hellbridge] 房子好了,开始铺桥 {_startX} 起 {HellLine.Bridge} 格");
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
