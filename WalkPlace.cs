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
		public struct Target { public int Wx, Wy, Slot, TileType; public bool Done; }

		private static bool _running;
		private static int _destCx, _dir;
		private static readonly List<Target> _targets = new();
		private static int _frames, _pendingFrames;

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

			// 每种物品开局占一个固定热键位:以前每次放置临时换槽,几个目标抢同一个槽会互相踢掉
			_targets.Clear();
			var homeOf = new Dictionary<int, int>();   // item.type -> hotbar slot it now lives in
			int nextHb = 0;
			foreach (var (wx, wy, item) in targets)
			{
				int slot = PlaceAction.ResolveSlot(item);
				if (slot < 0) { why = "no_item:" + item; Outcome = "no_item"; Reason = item; return false; }
				var it = p.inventory[slot];
				int type = it.type;

				if (!homeOf.TryGetValue(type, out int home))
				{
					if (slot <= 9) { home = slot; }              // already in the hotbar — leave it
					else
					{
						// find a free hotbar slot not already claimed by another item this run; swap the item down into it.
						home = -1;
						for (; nextHb < 10; nextHb++)
						{
							bool claimed = false;
							foreach (var v in homeOf.Values) if (v == nextHb) { claimed = true; break; }
							if (claimed) continue;
							var hbItem = p.inventory[nextHb];
							if (hbItem == null || hbItem.IsAir) { home = nextHb; break; }
						}
						if (home < 0) home = 0;                  // no empty slot — displace slot 0
						var tmp = p.inventory[home]; p.inventory[home] = p.inventory[slot]; p.inventory[slot] = tmp;
					}
					homeOf[type] = home;
				}
				_targets.Add(new Target { Wx = wx, Wy = wy, Slot = home, TileType = it.createTile, Done = false });
			}
			_destCx = destCx;
			_dir = destCx >= ActExecutor.OriginCx(p) ? 1 : -1;
			_frames = 0; PlacedCount = 0;
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
					// t.Slot is this item's permanent hotbar home (assigned at Start) — just select it, never swap.
					p.selectedItem = t.Slot;
					Main.mouseX = (int)(t.Wx * 16f + 8f - Main.screenPosition.X);
					Main.mouseY = (int)(t.Wy * 16f + 8f - Main.screenPosition.Y);
					Main.SmartCursorWanted_Mouse = false;
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
			if (arrived && !allDone)
			{
				// reached the end but something didn't get placed (missed its reach window). Report it rather than
				// walking past forever.
				Outcome = "incomplete"; _running = false; Dump("incomplete");
				return;
			}
			// 够得着但手还在冷却 → 站着等手,不然走过去就出了射程,这一格永远补不上。
			// 但只等 PendingLimit 帧:那格要是根本放不上(被占/地面不对),死等会拖到超时。
			if (pending && ++_pendingFrames <= PendingLimit) return;
			if (!pending) _pendingFrames = 0;
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
