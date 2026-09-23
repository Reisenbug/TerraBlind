using System.Collections.Generic;
using System.IO;
using Terraria;

namespace TerraBlind
{
	// Jev 每道题每个选项选了几次,按 boss 分开记。对照组按这张表抽
	public static class JevPrior
	{
		const int SaveEvery = 20;

		class Prior
		{
			public Dictionary<string, int[]> Counts = new();
			public int Total;
		}

		static Dictionary<int, Prior> _book;
		static int _unsaved;
		static string FilePath => Path.Combine(LogRoot.Root, "jev_prior.txt");

		static Dictionary<int, Prior> Book
		{
			get
			{
				if (_book == null) Load();
				return _book;
			}
		}

		// 一次回答记一条:每道题选了第几个选项,一共几个选项
		public static void Record(int bossType, params (string Q, int Pick, int Options)[] picks)
		{
			int key = BossBook.Canonical(bossType);
			if (!Book.TryGetValue(key, out var p)) Book[key] = p = new Prior();
			foreach (var (q, pick, n) in picks)
			{
				if (!p.Counts.TryGetValue(q, out var c) || c.Length != n) p.Counts[q] = c = new int[n];
				c[pick]++;
			}
			p.Total++;
			if (++_unsaved >= SaveEvery) Save();
		}

		public static int Samples(int bossType)
			=> Book.TryGetValue(BossBook.Canonical(bossType), out var p) ? p.Total : 0;

		// 按 Jev 的比例抽一道题的选项,没有记录就均匀抽
		public static int Sample(int bossType, string q, int options)
		{
			if (Book.TryGetValue(BossBook.Canonical(bossType), out var p)
			 && p.Counts.TryGetValue(q, out var c) && c.Length == options)
			{
				int sum = 0;
				foreach (int x in c) sum += x;
				if (sum > 0)
				{
					int r = Main.rand.Next(sum);
					for (int i = 0; i < c.Length; i++)
					{
						r -= c[i];
						if (r < 0) return i;
					}
				}
			}
			return Main.rand.Next(options);
		}

		// 一行一个 boss:key|total|题=次数,次数;题=...
		static void Load()
		{
			_book = new Dictionary<int, Prior>();
			try
			{
				if (!File.Exists(FilePath)) return;
				foreach (var line in File.ReadAllLines(FilePath))
				{
					var f = line.Split('|');
					if (f.Length != 3 || !int.TryParse(f[0], out int key) || !int.TryParse(f[1], out int total)) continue;
					var p = new Prior { Total = total };
					foreach (var part in f[2].Split(';'))
					{
						int eq = part.IndexOf('=');
						if (eq <= 0) continue;
						var nums = part.Substring(eq + 1).Split(',');
						var c = new int[nums.Length];
						for (int i = 0; i < nums.Length; i++) int.TryParse(nums[i], out c[i]);
						p.Counts[part.Substring(0, eq)] = c;
					}
					_book[key] = p;
				}
				DiagLog.Write($"[prior] 读到 {_book.Count} 个 boss 的 Jev 记录");
			}
			catch (System.Exception e) { DiagLog.Write($"[prior] 读取失败 {e.Message},从空表开始"); }
		}

		public static void Save()
		{
			if (_book == null) return;
			_unsaved = 0;
			try
			{
				var lines = new List<string>();
				foreach (var kv in _book)
				{
					var parts = new List<string>();
					foreach (var c in kv.Value.Counts) parts.Add(c.Key + "=" + string.Join(",", c.Value));
					lines.Add($"{kv.Key}|{kv.Value.Total}|{string.Join(";", parts)}");
				}
				Directory.CreateDirectory(LogRoot.Root);
				File.WriteAllLines(FilePath, lines);
			}
			catch (System.Exception e) { DiagLog.Write($"[prior] 保存失败 {e.Message}"); }
		}
	}

	// 每 20 次才落盘,退出世界时补存最后那几次
	public class JevPriorSaver : Terraria.ModLoader.ModSystem
	{
		public override void OnWorldUnload() => JevPrior.Save();
	}
}
