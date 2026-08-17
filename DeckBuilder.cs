using System.Collections.Generic;
using Terraria;

namespace TerraBlind
{
	// 沿着 HellLine 一格一格铺桥面。和 BridgeBuilder 的区别只有一个:那个锁死一行,
	// 这个跟着 Line 的起伏走 —— 地狱的线本来就不是平的。
	//
	// 线自己保证了坡度:每列最多变 1 行,而且不会连着两列都变(HellLine 的 maxStep/backToBack)。
	// 所以人站在已铺好的桥面上,下一格永远在伸手范围内,不用另算怎么爬。
	public static class DeckBuilder
	{
		private enum Ph { Idle, Place, Done }
		private static Ph _ph = Ph.Idle;

		private static List<(int x, int y)> _line = new();
		private static int _idx;
		private static string _item = "";
		private static int _frames, _cellFrames;
		private static bool _tried;   // 这一格我们动过手,用来分清 Placed / Already

		private const int MaxFrames = 60 * 600;
		private const int MaxCellFrames = 180;

		public static bool IsRunning => _ph != Ph.Idle && _ph != Ph.Done;
		public static string Outcome = "idle";
		public static string Reason = "";
		public static int Placed, Already;

		public static bool Start(string itemName, List<(int x, int y)> line, int from, out string why)
		{
			why = "";
			if (line == null || line.Count == 0) { why = "空线"; return false; }
			_item = itemName;
			_line = line; _idx = System.Math.Max(0, from);
			_frames = 0; _cellFrames = 0; Placed = 0; Already = 0; _tried = false;
			Outcome = "running"; Reason = "";
			_ph = Ph.Place;
			DiagLog.Write($"[deck] start {itemName} 共{line.Count}格 从i={_idx} ({line[_idx].x},{line[_idx].y})");
			return true;
		}

		public static void Stop()
		{
			if (Outcome == "running") Outcome = "stopped";
			_ph = Ph.Idle;
			PlaceAnywhere.Stop();
		}

		public static void Tick()
		{
			if (!IsRunning) return;
			var p = Main.LocalPlayer;
			if (p == null || !p.active) { Fail("no_player"); return; }
			if (++_frames > MaxFrames) { Fail($"超时 铺到第{_idx}/{_line.Count}格"); return; }

			// 铺完了
			if (_idx >= _line.Count)
			{
				Outcome = "done"; _ph = Ph.Done;
				DiagLog.Write($"[deck] DONE 放了{Placed}格 本来就有{Already}格");
				return;
			}

			var (x, y) = _line[_idx];

			// 有东西就算这一格过了。_tried 分得清是我们放的还是本来就有的
			if (Predicates.IsSolid(x, y))
			{
				if (_tried) Placed++; else Already++;
				_idx++; _cellFrames = 0; _tried = false;
				return;
			}

			if (PlaceAnywhere.IsRunning) return;
			if (PlaceAnywhere.Outcome == "stuck")
			{ Fail($"第{_idx}格({x},{y})放不上:{PlaceAnywhere.Reason}"); return; }
			if (++_cellFrames > MaxCellFrames) { Fail($"({x},{y})卡了{_cellFrames}帧"); return; }
			// 放置的全部麻烦(够不着/人挡着/没锚)都在 PlaceAnywhere 里,这里只管要结果。
			// 别在这儿 _idx++:它是异步的,这一格要等上面 IsSolid 判真才算完
			if (!PlaceAnywhere.Start(_item, x, y, out string pw))
			{ Fail($"第{_idx}格({x},{y}):{pw}"); return; }
			_tried = true;
		}

		static void Fail(string reason)
		{
			Outcome = "stuck"; Reason = reason; _ph = Ph.Idle;
			DiagLog.Write($"[deck] STUCK {reason}");
		}
	}
}
