using System.Collections.Generic;

namespace TerraBlind
{
	// Jev 判断的环形缓冲。【游戏线程写、HTTP 线程读】,所以全程锁住
	public static class JevLog
	{
		public struct Entry
		{
			public long Ms;          // 开机以来的毫秒,给页面排序用
			public string Site;      // 哪个调用点,例如 triage
			public string State;     // 喂进去的 state JSON
			public string Pick;      // 选了哪个
			public float Confidence;
			public string Probs;     // "A:0.72,B:0.21" 这样一行,页面直接显示
			public string Why;
			public int LatencyMs;    // -1 = baseline,没真发请求
		}

		const int Cap = 200;
		static readonly List<Entry> _ring = new();
		static readonly object _lock = new();

		public static void Add(Entry e)
		{
			lock (_lock)
			{
				_ring.Add(e);
				if (_ring.Count > Cap) _ring.RemoveAt(0);
			}
		}

		public static List<Entry> Snapshot()
		{
			lock (_lock) return new List<Entry>(_ring);
		}

		public static void Clear()
		{
			lock (_lock) _ring.Clear();
		}
	}
}
