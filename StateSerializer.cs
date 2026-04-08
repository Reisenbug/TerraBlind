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
			sb.Append("\"direction\":\"").Append(EscapeStr(s.Player.Direction)).Append("\",");
			sb.Append("\"on_ground\":").Append(B(s.Player.OnGround)).Append(',');
			sb.Append("\"in_liquid\":").Append(B(s.Player.InLiquid));
			sb.Append("},");

			sb.Append("\"equipment\":{");
			sb.Append("\"selected_slot\":").Append(s.Equipment.SelectedSlot).Append(',');
			sb.Append("\"held_item\":");
			AppendSlot(sb, s.Equipment.HeldItem);
			sb.Append(',');
			sb.Append("\"hotbar\":[");
			for (int i = 0; i < s.Equipment.Hotbar.Length; i++)
			{
				if (i > 0) sb.Append(',');
				AppendSlot(sb, s.Equipment.Hotbar[i]);
			}
			sb.Append(']');
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
			sb.Append(']');

			sb.Append('}');
			return sb.ToString();
		}

		private static void AppendSlot(StringBuilder sb, HotbarSlot slot)
		{
			if (slot == null)
			{
				sb.Append("{\"id\":0,\"name\":\"\",\"stack\":0}");
				return;
			}
			sb.Append("{\"id\":").Append(slot.Id);
			sb.Append(",\"name\":\"").Append(EscapeStr(slot.Name)).Append("\"");
			sb.Append(",\"stack\":").Append(slot.Stack);
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
