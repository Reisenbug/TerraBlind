using System.Collections.Generic;
using System.Text;
using Terraria;
using Terraria.ModLoader;

namespace TerraBlind
{
	// WALK-PLACE — walk in one direction to a target column, and along the way, whenever a placement target comes
	// within reach, place its item there. Does not stop at each target — reaches, places, keeps walking. Used to
	// drop furniture at specific columns while crossing a floor.
	//
	// Each target is an absolute cell + an item name (resolved to a slot). A target is placed once; placement is
	// judged by the MAP (the cell now holds that item's tile). Walking ends at the destination column.
	public static class WalkPlace
	{
		public struct Target { public int Wx, Wy, Slot, TileType, ItemType; public bool Done; }

		private static bool _running;
		private static int _destCx, _dir;
		private static readonly List<Target> _targets = new();
		private static int _frames, _pendingFrames;
		private static bool _armed;

		private const int MaxFrames = 1200;
		private const int PendingLimit = 90;   // 等手够久了还没放上,就是放不上,别死等

		public static bool IsRunning => _running;
		public static string Outcome = "idle";   // idle running done timeout no_item
		public static string Reason = "";
		public static int PlacedCount { get; private set; }

		// targets: list of (wx, wy, itemName). destCx: the column to stop at. dir is inferred from start vs dest.
		public static bool Start(int destCx, List<(int wx, int wy, string item)> targets, out string why)
		{
			why = "";
			var p = Main.LocalPlayer;
			if (p == null) { why = "no_player"; return false; }

			_targets.Clear();
			foreach (var (wx, wy, item) in targets)
			{
				int slot = PlaceAction.ResolveSlot(item);
				if (slot < 0) { why = "no_item:" + item; Outcome = "no_item"; Reason = item; return false; }
				var it = p.inventory[slot];
				_targets.Add(new Target { Wx = wx, Wy = wy, Slot = slot, TileType = it.createTile, ItemType = it.type, Done = false });
			}
			_armed = false;
			_destCx = destCx;
			_dir = destCx >= ActExecutor.OriginCx(p) ? 1 : -1;
			_frames = 0; PlacedCount = 0; _pendingFrames = 0;
			_running = true;
			Outcome = "running"; Reason = "";
			var tl = new StringBuilder();
			foreach (var t in _targets) tl.Append($"({t.Wx},{t.Wy}) ");
			DiagLog.Write($"[walkplace] start dest={destCx} dir={_dir} 人在={ActExecutor.OriginCx(p)} 目标={tl.ToString().Trim()}");
			return true;
		}

		public static void Stop()
		{
			if (Outcome == "running") Outcome = "stopped";
			_running = false;
		}

		// 桌子 3 格宽 2 格高,锚点那格空不代表放得下 —— 连脚下支撑一起看
		static string Foot(int wx, int wy)
		{
			var sb = new StringBuilder();
			for (int y = wy - 1; y <= wy + 1; y++)
			{
				for (int x = wx - 1; x <= wx + 1; x++)
					sb.Append(!InBounds(x, y) ? '?' : (Main.tile[x, y].HasTile ? '#' : '.'));
				sb.Append('/');
			}
			return sb.ToString();
		}

		static void Dump(string tag)
		{
			var p = Main.LocalPlayer;
			var miss = new StringBuilder();
			foreach (var t in _targets)
			{
				if (t.Done) continue;
				bool reach = p != null && p.IsInTileInteractionRange(t.Wx, t.Wy, Terraria.DataStructures.TileReachCheckSettings.Simple);
				string occ = "oob";
				if (InBounds(t.Wx, t.Wy))
				{
					var tile = Main.tile[t.Wx, t.Wy];
					occ = tile.HasTile ? "占用t" + tile.TileType : "空";
				}
				miss.Append($"({t.Wx},{t.Wy})slot{t.Slot} 够得着={reach} 那格={occ} 占地={Foot(t.Wx, t.Wy)} ");
			}
			DiagLog.Write($"[walkplace] {tag} placed={PlacedCount}/{_targets.Count} 没放上={miss.ToString().Trim()} 人在={(p != null ? ActExecutor.OriginCx(p) : -1)} 手上={(p != null ? p.selectedItem : -1)} 帧={_frames}");
		}

		// 挤掉哪个热键位:本轮用不到的那种物品优先。挤掉正在用的会让它的 t.Slot 失效。
		static int FreeHome(Player p, Dictionary<int, int> homeOf)
		{
			for (int i = 0; i < 10; i++)
			{
				bool claimed = false;
				foreach (var v in homeOf.Values) if (v == i) { claimed = true; break; }
				if (claimed) continue;
				var it = p.inventory[i];
				bool needed = false;
				if (it != null && !it.IsAir)
					foreach (var t in _targets) if (!t.Done && t.ItemType == it.type) { needed = true; break; }
				if (!needed) return i;
			}
			return 0;
		}

		// 每种物品占一个固定热键位:以前每次放置临时换槽,几个目标抢同一个槽会互相踢掉。
		// 只在手空闲时调用 —— 冷却里换槽会被原版补一次消耗。
		static void Arm(Player p)
		{
			var homeOf = new Dictionary<int, int>();
			int nextHb = 0;
			for (int i = 0; i < _targets.Count; i++)
			{
				var t = _targets[i];
				if (!homeOf.TryGetValue(t.ItemType, out int home))
				{
					int slot = t.Slot;
					if (slot <= 9) home = slot;
					else
					{
						home = -1;
						for (; nextHb < 10; nextHb++)
						{
							bool claimed = false;
							foreach (var v in homeOf.Values) if (v == nextHb) { claimed = true; break; }
							if (claimed) continue;
							var hbItem = p.inventory[nextHb];
							if (hbItem == null || hbItem.IsAir) { home = nextHb; break; }
						}
						// 热键位全满时旧代码 home=0 硬挤:把 0 号原来的东西换到 slot 去。
						// 换出去的要是【本轮还要用的另一种料】,它的 t.Slot 就指向了别人的格子。
						if (home < 0) home = FreeHome(p, homeOf);
						var tmp = p.inventory[home]; p.inventory[home] = p.inventory[slot]; p.inventory[slot] = tmp;
						DiagLog.Write($"[walkplace] arm 物品{t.ItemType} 槽{slot}→热键{home} 换出={(tmp == null || tmp.IsAir ? "空" : tmp.type + "x" + tmp.stack)}");
					}
					homeOf[t.ItemType] = home;
				}
				t.Slot = home; _targets[i] = t;
			}
		}

		private static bool Filled(Target t)
		{
			if (!InBounds(t.Wx, t.Wy)) return false;
			var tile = Main.tile[t.Wx, t.Wy];
			return tile.HasTile && tile.TileType == t.TileType;
		}

		public static void Tick()
		{
			if (!_running) return;
			var p = Main.LocalPlayer;
			if (p == null || !p.active) { Reason = "no_player"; Outcome = "timeout"; _running = false; return; }

			_frames++;
			if (_frames > MaxFrames) { Outcome = "timeout"; _running = false; Dump("timeout"); return; }

			// 上一个动作的手还没收:这时候换槽,冷却归零那一帧原版会拿刚换进来的东西补一次
			// 消耗,物品扣了却没落地(桌子每次蒸发一张就是这个)。等手真空了再开工。
			if (!_armed)
			{
				if (p.itemTime > 0 || p.itemAnimation > 0) return;
				Arm(p);
				_armed = true;
			}

			// 放成没放成看地图说了算,不看挥舞结果
			bool swungThisFrame = false;
			bool pending = false;   // 够得着但还没放上的目标
			for (int i = 0; i < _targets.Count; i++)
			{
				var t = _targets[i];
				if (t.Done) continue;
				if (Filled(t)) { t.Done = true; _targets[i] = t; PlacedCount++; continue; }
				if (!p.IsInTileInteractionRange(t.Wx, t.Wy, Terraria.DataStructures.TileReachCheckSettings.Simple)) continue;
				pending = true;

				if (!swungThisFrame && p.itemTime == 0)
				{
					// t.Slot 是 Arm 分的热键位,但中途别的动作可能把料挪走了 —— 按【物品类型】认,
					// 不认下标。认错下标 = 拿着别的东西挥一下,料没少但也没放上。
					var held = p.inventory[t.Slot];
					if (held == null || held.IsAir || held.type != t.ItemType)
					{
						int re = PlaceAction.FindSlotById(t.ItemType);
						if (re < 0) { DiagLog.Write($"[walkplace] 物品{t.ItemType} 没了,({t.Wx},{t.Wy}) 放不了"); continue; }
						if (re > 9) { var sw = p.inventory[t.Slot]; p.inventory[t.Slot] = p.inventory[re]; p.inventory[re] = sw; }
						else { t.Slot = re; _targets[i] = t; }
						DiagLog.Write($"[walkplace] 物品{t.ItemType} 槽位漂了,重新定位到 {t.Slot}");
					}
					p.selectedItem = t.Slot;
					Cursor.AimTile(t.Wx, t.Wy);
					p.controlUseItem = true;
					swungThisFrame = true;
				}
			}

			// walk toward the destination. Done when we've reached it AND every target is placed.
			int cx = ActExecutor.OriginCx(p);
			bool arrived = _dir > 0 ? cx >= _destCx : cx <= _destCx;
			bool allDone = true;
			foreach (var t in _targets) if (!t.Done) { allDone = false; break; }

			if (arrived && allDone) { Outcome = "done"; _running = false; DiagLog.Write($"[walkplace] done placed={PlacedCount}"); return; }

			// 终点也要等手:最后一张桌子的射程窗口和终点重叠,一到就判失败等于从不给它机会
			if (pending && ++_pendingFrames <= PendingLimit) return;
			if (!pending) _pendingFrames = 0;

			if (arrived)
			{
				Outcome = "incomplete"; _running = false; Dump("incomplete");
				return;
			}
			if (_dir > 0) p.controlRight = true; else p.controlLeft = true;
		}

		public static string StatusJson()
		{
			var p = Main.LocalPlayer;
			var sb = new StringBuilder();
			sb.Append("{\"outcome\":\"").Append(Outcome).Append('"')
			  .Append(",\"running\":").Append(_running ? "true" : "false")
			  .Append(",\"placed\":").Append(PlacedCount).Append(",\"targets\":").Append(_targets.Count)
			  .Append(",\"reason\":\"").Append(Reason).Append('"').Append(",\"cells\":[");
			for (int i = 0; i < _targets.Count; i++)
			{
				if (i > 0) sb.Append(',');
				sb.Append("{\"at\":[").Append(_targets[i].Wx).Append(',').Append(_targets[i].Wy)
				  .Append("],\"done\":").Append(_targets[i].Done ? "true" : "false").Append('}');
			}
			sb.Append(']');
			if (p != null) sb.Append(",\"origin\":[").Append(ActExecutor.OriginCx(p)).Append(',').Append(ActExecutor.OriginCy(p)).Append(']');
			sb.Append('}');
			return sb.ToString();
		}

		private static bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Main.maxTilesX && y < Main.maxTilesY;
	}
}
