using System.Globalization;
using System.Text;

namespace TerraBlind
{
	public static class StateSerializer
	{
		public static string ToJson(Snapshot s)
		{
			var sb = new StringBuilder(1024);
			if (s == null)
			{
				sb.Append("{\"tick\":0,\"player\":null,\"equipment\":null,\"buffs\":[]}");
				return sb.ToString();
			}

			sb.Append('{');
			sb.Append("\"tick\":").Append(s.Tick).Append(',');

			sb.Append("\"player\":{");
			sb.Append("\"hp\":").Append(s.Player.Hp).Append(',');
			sb.Append("\"max_hp\":").Append(s.Player.MaxHp).Append(',');
			sb.Append("\"mana\":").Append(s.Player.Mana).Append(',');
			sb.Append("\"max_mana\":").Append(s.Player.MaxMana).Append(',');
			sb.Append("\"pos\":{\"x\":").Append(F(s.Player.PosX)).Append(",\"y\":").Append(F(s.Player.PosY)).Append("},");
			sb.Append("\"vel\":{\"x\":").Append(F(s.Player.VelX)).Append(",\"y\":").Append(F(s.Player.VelY)).Append("},");
			sb.Append("\"width\":").Append(F(s.Player.Width)).Append(',');
			sb.Append("\"height\":").Append(F(s.Player.Height)).Append(',');
			sb.Append("\"direction\":\"").Append(EscapeStr(s.Player.Direction)).Append("\",");
			sb.Append("\"on_ground\":").Append(B(s.Player.OnGround)).Append(',');
			sb.Append("\"in_liquid\":").Append(B(s.Player.InLiquid));
			sb.Append("},");

			sb.Append("\"equipment\":{");
			sb.Append("\"selected_slot\":").Append(s.Equipment.SelectedSlot).Append(',');
			sb.Append("\"inventory_open\":").Append(B(s.Equipment.InventoryOpen)).Append(',');
			sb.Append("\"chest_open\":").Append(B(s.Equipment.ChestOpen)).Append(',');
			sb.Append("\"smart_cursor\":").Append(B(s.Equipment.SmartCursor)).Append(',');
			sb.Append("\"held_item\":");
			AppendSlot(sb, s.Equipment.HeldItem);
			sb.Append(',');
			sb.Append("\"hotbar\":[");
			for (int i = 0; i < s.Equipment.Hotbar.Length; i++)
			{
				if (i > 0) sb.Append(',');
				AppendSlot(sb, s.Equipment.Hotbar[i]);
			}
			sb.Append("],");
			sb.Append("\"inventory\":[");
			for (int i = 0; i < s.Equipment.Inventory.Length; i++)
			{
				if (i > 0) sb.Append(',');
				AppendSlot(sb, s.Equipment.Inventory[i]);
			}
			sb.Append("],");
			sb.Append("\"coins\":[");
			for (int i = 0; i < s.Equipment.Coins.Length; i++)
			{
				if (i > 0) sb.Append(',');
				AppendSlot(sb, s.Equipment.Coins[i]);
			}
			sb.Append("],");
			sb.Append("\"ammo\":[");
			for (int i = 0; i < s.Equipment.Ammo.Length; i++)
			{
				if (i > 0) sb.Append(',');
				AppendSlot(sb, s.Equipment.Ammo[i]);
			}
			sb.Append(']');
			sb.Append("},");

			sb.Append("\"camera\":{");
			sb.Append("\"screen_pos\":{\"x\":").Append(F(s.Camera.ScreenPosX)).Append(",\"y\":").Append(F(s.Camera.ScreenPosY)).Append("},");
			sb.Append("\"screen_size\":{\"w\":").Append(s.Camera.ScreenWidth).Append(",\"h\":").Append(s.Camera.ScreenHeight).Append("},");
			sb.Append("\"zoom\":").Append(F(s.Camera.Zoom));
			sb.Append("},");

			sb.Append("\"buffs\":[");
			for (int i = 0; i < s.Buffs.Length; i++)
			{
				if (i > 0) sb.Append(',');
				var b = s.Buffs[i];
				sb.Append("{\"id\":").Append(b.Id);
				sb.Append(",\"name\":\"").Append(EscapeStr(b.Name)).Append("\"");
				sb.Append(",\"time_left\":").Append(F(b.TimeLeft));
				sb.Append('}');
			}
			sb.Append("],");

			sb.Append("\"enemies\":[");
			for (int i = 0; i < s.Enemies.Length; i++)
			{
				if (i > 0) sb.Append(',');
				var e = s.Enemies[i];
				sb.Append("{\"who\":").Append(e.WhoAmI);
				sb.Append(",\"type\":").Append(e.Type);
				sb.Append(",\"name\":\"").Append(EscapeStr(e.Name)).Append("\"");
				sb.Append(",\"pos\":{\"x\":").Append(F(e.PosX)).Append(",\"y\":").Append(F(e.PosY)).Append("}");
				sb.Append(",\"vel\":{\"x\":").Append(F(e.VelX)).Append(",\"y\":").Append(F(e.VelY)).Append("}");
				sb.Append(",\"w\":").Append(F(e.Width));
				sb.Append(",\"h\":").Append(F(e.Height));
				sb.Append(",\"hp\":").Append(e.Hp);
				sb.Append(",\"max_hp\":").Append(e.MaxHp);
				sb.Append(",\"boss\":").Append(B(e.Boss));
				sb.Append('}');
			}
			sb.Append("],");

			sb.Append("\"town_npcs\":[");
			for (int i = 0; i < s.TownNpcs.Length; i++)
			{
				if (i > 0) sb.Append(',');
				var n = s.TownNpcs[i];
				sb.Append("{\"who\":").Append(n.WhoAmI);
				sb.Append(",\"type\":").Append(n.Type);
				sb.Append(",\"name\":\"").Append(EscapeStr(n.Name)).Append("\"");
				sb.Append(",\"display_name\":\"").Append(EscapeStr(n.DisplayName)).Append("\"");
				sb.Append(",\"pos\":{\"x\":").Append(F(n.PosX)).Append(",\"y\":").Append(F(n.PosY)).Append("}");
				sb.Append(",\"homeless\":").Append(B(n.Homeless));
				sb.Append('}');
			}
			sb.Append("],");

			if (s.Tiles != null)
			{
				var tw = s.Tiles;
				sb.Append("\"tiles\":{");
				sb.Append("\"origin\":{\"x\":").Append(tw.OriginTileX).Append(",\"y\":").Append(tw.OriginTileY).Append("},");
				sb.Append("\"w\":").Append(tw.Width).Append(",\"h\":").Append(tw.Height).Append(',');
				sb.Append("\"rows\":[");
				for (int r = 0; r < tw.Rows.Length; r++)
				{
					if (r > 0) sb.Append(',');
					sb.Append('[');
					var row = tw.Rows[r];
					for (int c = 0; c < row.Length; c++)
					{
						if (c > 0) sb.Append(',');
						sb.Append('[').Append(row[c].Type).Append(',').Append(row[c].SFlags).Append(',').Append(row[c].Count).Append(']');
					}
					sb.Append(']');
				}
				sb.Append("]},");
			}

			sb.Append("\"objects\":[");
			for (int i = 0; i < s.Objects.Length; i++)
			{
				if (i > 0) sb.Append(',');
				var o = s.Objects[i];
				sb.Append("{\"tx\":").Append(o.TileX);
				sb.Append(",\"ty\":").Append(o.TileY);
				sb.Append(",\"type\":").Append(o.Type);
				sb.Append(",\"name\":\"").Append(EscapeStr(o.Name)).Append("\"");
				sb.Append(",\"pos\":{\"x\":").Append(F(o.PosX)).Append(",\"y\":").Append(F(o.PosY)).Append("}");
				sb.Append('}');
			}
			sb.Append("],");

			sb.Append("\"dropped_items\":[");
			for (int i = 0; i < s.DroppedItems.Length; i++)
			{
				if (i > 0) sb.Append(',');
				var d = s.DroppedItems[i];
				sb.Append("{\"who\":").Append(d.WhoAmI);
				sb.Append(",\"type\":").Append(d.Type);
				sb.Append(",\"name\":\"").Append(EscapeStr(d.Name)).Append("\"");
				sb.Append(",\"stack\":").Append(d.Stack);
				sb.Append(",\"pos\":{\"x\":").Append(F(d.PosX)).Append(",\"y\":").Append(F(d.PosY)).Append("}");
				sb.Append('}');
			}
			sb.Append(']');

			sb.Append('}');
			return sb.ToString();
		}

		private static void AppendSlot(StringBuilder sb, HotbarSlot slot)
		{
			if (slot == null)
			{
				sb.Append("{\"id\":0,\"name\":\"\",\"stack\":0,\"damage\":0,\"pick\":0,\"axe\":0,\"hammer\":0,\"create_tile\":-1,\"consumable\":false}");
				return;
			}
			sb.Append("{\"id\":").Append(slot.Id);
			sb.Append(",\"name\":\"").Append(EscapeStr(slot.Name)).Append("\"");
			sb.Append(",\"stack\":").Append(slot.Stack);
			sb.Append(",\"damage\":").Append(slot.Damage);
			sb.Append(",\"pick\":").Append(slot.Pick);
			sb.Append(",\"axe\":").Append(slot.Axe);
			sb.Append(",\"hammer\":").Append(slot.Hammer);
			sb.Append(",\"create_tile\":").Append(slot.CreateTile);
			sb.Append(",\"consumable\":").Append(B(slot.Consumable));
			sb.Append('}');
		}

		private static string F(float v)
		{
			return v.ToString("0.###", CultureInfo.InvariantCulture);
		}

		private static string B(bool v)
		{
			return v ? "true" : "false";
		}

		private static string EscapeStr(string s)
		{
			if (string.IsNullOrEmpty(s)) return "";
			var sb = new StringBuilder(s.Length);
			foreach (char c in s)
			{
				switch (c)
				{
					case '"': sb.Append("\\\""); break;
					case '\\': sb.Append("\\\\"); break;
					case '\n': sb.Append("\\n"); break;
					case '\r': sb.Append("\\r"); break;
					case '\t': sb.Append("\\t"); break;
					default:
						if (c < 0x20)
							sb.AppendFormat("\\u{0:x4}", (int)c);
						else
							sb.Append(c);
						break;
				}
			}
			return sb.ToString();
		}
	}
}
