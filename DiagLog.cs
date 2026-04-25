using System;
using System.IO;
using Terraria;
using Terraria.ModLoader;

namespace TerraBlind
{
	public static class DiagLog
	{
		private static readonly object _lock = new object();
		private static string _path;

		private static string Path
		{
			get
			{
				if (_path != null) return _path;
				try
				{
					string dir = System.IO.Path.Combine(Main.SavePath, "TerraBlindLogs");
					Directory.CreateDirectory(dir);
					_path = System.IO.Path.Combine(dir, "jump_trace.log");
				}
				catch
				{
					_path = "";
				}
				return _path;
			}
		}

		public static void JumpTrace(string msg)
		{
			string p = Path;
			if (string.IsNullOrEmpty(p)) return;
			try
			{
				lock (_lock)
				{
					File.AppendAllText(p, $"{Main.GameUpdateCount} {msg}\n");
				}
			}
			catch { }
		}
	}
}
