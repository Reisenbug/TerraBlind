using System.Collections.Generic;
using System.IO;
using Terraria;

namespace TerraBlind
{
	// Jev 实际怎么选的,按 boss 分开记。对照组按这张表抽,
	// 【比例和 Jev 一样,差的只剩"什么时候选"】 -- 均匀随机的话连"多数时候远离"都没有,比的就不是一个东西
	public static class JevPrior
	{
		const int Buckets = 20;
		const int SaveEvery = 20;

		class Prior
		{
			public int[] Pick = new int[9];
			public int[] Dash = new int[Buckets], Hook = new int[Buckets], Jump = new int[Buckets];
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

		static int Bucket(float v) => System.Math.Clamp((int)(v * Buckets), 0, Buckets - 1);

		public static void Record(int bossType, Horiz h, Vert v, float dash, float hook, float jump)
		{
			int key = BossBook.Canonical(bossType);
			if (!Book.TryGetValue(key, out var p)) Book[key] = p = new Prior();
			p.Pick[(int)h * 3 + (int)v]++;
			p.Dash[Bucket(dash)]++;
			p.Hook[Bucket(hook)]++;
			p.Jump[Bucket(jump)]++;
			p.Total++;
			if (++_unsaved >= SaveEvery) Save();
		}

		// 这个 boss 没有 Jev 的记录就返回 false,调用方自己退回均匀
		public static bool Sample(int bossType, out Horiz h, out Vert v, out float dash, out float hook, out float jump, out int samples)
		{
			h = Horiz.Away; v = Vert.Level; dash = hook = jump = 0f; samples = 0;
			if (!Book.TryGetValue(BossBook.Canonical(bossType), out var p) || p.Total == 0) return false;
			samples = p.Total;
			int pick = Draw(p.Pick);
			h = (Horiz)(pick / 3);
			v = (Vert)(pick % 3);
			dash = DrawValue(p.Dash);
			hook = DrawValue(p.Hook);
			jump = DrawValue(p.Jump);
			return true;
		}

		static int Draw(int[] counts)
		{
			int sum = 0;
			foreach (int c in counts) sum += c;
			int r = Main.rand.Next(sum);
			for (int i = 0; i < counts.Length; i++)
			{
				r -= counts[i];
				if (r < 0) return i;
			}
			return counts.Length - 1;
		}

		// 先按比例抽桶,桶内均匀
		static float DrawValue(int[] counts) => (Draw(counts) + Main.rand.NextFloat()) / Buckets;

		static void Load()
		{
			_book = new Dictionary<int, Prior>();
			try
			{
				if (!File.Exists(FilePath)) return;
				foreach (var line in File.ReadAllLines(FilePath))
				{
					var f = line.Split('|');
					if (f.Length != 6 || !int.TryParse(f[0], out int key)) continue;
					var p = new Prior();
					if (!Fill(f[1], p.Pick) || !Fill(f[2], p.Dash) || !Fill(f[3], p.Hook)
					 || !Fill(f[4], p.Jump) || !int.TryParse(f[5], out p.Total)) continue;
					_book[key] = p;
				}
				DiagLog.Write($"[prior] 读到 {_book.Count} 个 boss 的 Jev 记录");
			}
			catch (System.Exception e) { DiagLog.Write($"[prior] 读取失败 {e.Message},从空表开始"); }
		}

		static bool Fill(string s, int[] into)
		{
			var parts = s.Split(',');
			if (parts.Length != into.Length) return false;
			for (int i = 0; i < parts.Length; i++)
				if (!int.TryParse(parts[i], out into[i])) return false;
			return true;
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
					var p = kv.Value;
					lines.Add($"{kv.Key}|{string.Join(",", p.Pick)}|{string.Join(",", p.Dash)}|{string.Join(",", p.Hook)}"
						+ $"|{string.Join(",", p.Jump)}|{p.Total}");
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
