using System.Collections.Generic;
using System.Text;
using Terraria;

namespace TerraBlind
{
	// STUCK SNAPSHOT, the whole decision situation, written to disk the moment a loop is detected.
	//
	// Debugging a loop from jump_trace.log means reconstructing a two-dimensional field from one-dimensional text,
	// with the numbers that decide everything (H over the neighbourhood, the accumulated edge penalties) absent
	// they only ever appeared for cells already stood on. Worse, the situation is unreproducible: it depends on how
	// much penalty had piled up and when the field was built, so the same bug cannot be run twice.
	//
	// So capture it whole: terrain, H at every cell around, every candidate with its score breakdown, and the
	// penalty table. One JSON per loop, inspectable offline with nobody playing the game.
	public static class StuckSnapshot
	{
		const int Radius = 24;          // how far around the player to capture
		static int _seq;

		public static string Capture(int curCx, int curCy, int curH, int goalWx, int goalWy,
			List<StateSpacePlanner.Cand> cands, string why, string trail)
		{
			try
			{
				var field = MazeWand.PeekField();
				var sb = new StringBuilder();
				sb.Append("{\"why\":\"").Append(Esc(why)).Append('"');
				sb.Append(",\"at\":[").Append(curCx).Append(',').Append(curCy).Append("],\"h\":").Append(curH);
				sb.Append(",\"goal\":[").Append(goalWx).Append(',').Append(goalWy).Append(']');
				int x0 = curCx - Radius, x1 = curCx + Radius, y0 = curCy - Radius, y1 = curCy + Radius;
				sb.Append(",\"region\":[").Append(x0).Append(',').Append(y0).Append(',').Append(x1).Append(',').Append(y1).Append(']');

				// terrain + H + standability, one row per line: this is the picture the log could never show
				sb.Append(",\"cells\":[");
				bool first = true;
				for (int y = y0; y <= y1; y++)
					for (int x = x0; x <= x1; x++)
					{
						if (x < 1 || y < 1 || x >= Main.maxTilesX - 1 || y >= Main.maxTilesY - 1) continue;
						var t = Main.tile[x, y];
						bool hasH = field != null && field.TryGetValue((x, y), out int hv);
						int h = hasH && field.TryGetValue((x, y), out int hv2) ? hv2 : -1;
						bool stand = Predicates.CanStand(x, y);
						// skip cells that carry no information at all: empty, no H, not standable
						if (!t.HasTile && !hasH && !stand) continue;
						if (!first) sb.Append(',');
						first = false;
						sb.Append("{\"x\":").Append(x).Append(",\"y\":").Append(y)
						  .Append(",\"t\":").Append(t.HasTile ? t.TileType.ToString() : "null")
						  .Append(",\"h\":").Append(hasH ? h.ToString() : "null")
						  .Append(",\"s\":").Append(stand ? "1" : "0").Append('}');
					}
				sb.Append(']');

				// every candidate the planner had, so "why did it not take the exit" is answerable offline
				sb.Append(",\"cands\":[");
				for (int i = 0; cands != null && i < cands.Count; i++)
				{
					var c = cands[i];
					if (i > 0) sb.Append(',');
					sb.Append("{\"x\":").Append(c.Cx).Append(",\"y\":").Append(c.Cy)
					  .Append(",\"h\":").Append(c.H).Append(",\"g\":").Append(c.Cost)
					  .Append(",\"kind\":\"").Append(c.Kind).Append("\",\"down\":").Append(c.Descends ? "1" : "0").Append('}');
				}
				sb.Append(']');

				// the penalty table, the hidden state that makes a loop unreproducible
				sb.Append(",\"pen\":[").Append(StateSpacePlanner.PenaltyJson(x0, y0, x1, y1)).Append(']');
				sb.Append(",\"trail\":\"").Append(Esc(trail)).Append("\"}");

				string dir = System.IO.Path.Combine(LogRoot.Dir, "stuck");
				System.IO.Directory.CreateDirectory(dir);
				string path = System.IO.Path.Combine(dir, $"stuck_{System.DateTime.Now:HHmmss}_{_seq++}.json");
				System.IO.File.WriteAllText(path, sb.ToString());
				DiagLog.Write($"[stuck-snap] wrote {path}");
				return path;
			}
			catch (System.Exception e)
			{
				DiagLog.Write($"[stuck-snap] FAILED {e.Message}");
				return null;
			}
		}

		static string Esc(string s) => string.IsNullOrEmpty(s) ? "" : s.Replace("\\", "\\\\").Replace("\"", "\\\"");
	}
}
