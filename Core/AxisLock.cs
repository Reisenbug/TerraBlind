using System.Collections.Generic;

namespace TerraBlind
{
	// 控制权的锁,按【方面】分,不是按整个玩家分。
	//
	// 起因(日志 29503~29625):岩浆堤选中一格开始放,寻路同时把人挪走,122 帧后
	// 放置报 out_of_reach 作废;堤又选一格,又被挪走 --- 列号 1171<->1175 来回跳,
	// 一块都没放成。再往前 pillar 砌到一半被堤填的方块挡住(veto=occupied)。
	// 三方每帧各写各的控制键,谁也不知道别人在干什么。
	//
	// 粒度按【物理上能不能同时做】划分:走路和放置互不干扰,可以并行;
	// 而左右键必须一个动作说了算,否则原地抖。
	//
	// 锁【跨帧】持有 --- 这是关键。冲突本来就是跨帧的(一次放置 90 帧,中间被挪走),
	// 每帧清锁根本挡不住。所以谁开工谁一直拿着,干完自己 Release。
	// 忘了放的兜底见 Sweep():持有者不再 IsRunning 就自动收回。
	public enum Ax
	{
		None = 0,
		Move = 1,        // controlLeft / controlRight
		Jump = 2,        // controlJump
		Vertical = 4,    // controlDown / controlUp(穿平台、爬绳)
		// selectedItem + Cursor.Aim + controlUseItem 是一整套:
		// 换手、瞄准、按键必须同一个动作说了算,拆开就是"A 瞄准 B 按键"那种 bug
		Use = 8,
	}

	public static class AxisLock
	{
		// 方面 -> 持有者名字。名字就是身份,用字符串是为了日志直接可读
		static readonly Dictionary<Ax, string> _held = new();
		// 持有者 -> 它还活着吗。Sweep 用这个收回忘记释放的锁
		static readonly Dictionary<string, System.Func<bool>> _alive = new();

		static readonly Ax[] All = { Ax.Move, Ax.Jump, Ax.Vertical, Ax.Use };

		// 抢。要什么一次性说全 --- 分两次抢会半途卡住(拿到 Move 没拿到 Use,
		// 人走过去了却放不了东西,比一开始就不动更糟)
		public static bool Take(string owner, Ax want, System.Func<bool> alive = null)
		{
			foreach (var a in All)
			{
				if ((want & a) == 0) continue;
				if (_held.TryGetValue(a, out string h) && h != owner) return false;
			}
			foreach (var a in All)
				if ((want & a) != 0) _held[a] = owner;
			if (alive != null) _alive[owner] = alive;
			return true;
		}

		// 放掉自己持有的全部。干完就放,别等 Sweep
		public static void Release(string owner)
		{
			foreach (var a in All)
				if (_held.TryGetValue(a, out string h) && h == owner) _held.Remove(a);
			_alive.Remove(owner);
		}

		// 谁拿着这个方面。没人拿着返回空串
		public static string Held(Ax a) => _held.TryGetValue(a, out string h) ? h : "";

		// 自己拿着吗
		public static bool Has(string owner, Ax a) => Held(a) == owner;

		// 兜底:持有者已经不跑了就收回它的锁。忘记 Release 不该让整个系统死锁,
		// 而 Start/Stop 分散在几十个文件里,总有漏的
		public static void Sweep()
		{
			List<string> dead = null;
			foreach (var kv in _alive)
				if (!kv.Value()) (dead ??= new List<string>()).Add(kv.Key);
			if (dead == null) return;
			foreach (var d in dead)
			{
				DiagLog.Write($"[axis] {d} 已停但没放锁,收回");
				Release(d);
			}
		}

		// 全放。换关卡/重开时用 --- 不清的话上一局的持有者永远占着
		public static void Reset()
		{
			_held.Clear(); _alive.Clear();
		}

		public static string Dump()
		{
			var sb = new System.Text.StringBuilder();
			foreach (var a in All)
				sb.Append(a).Append('=').Append(Held(a).Length == 0 ? "-" : Held(a)).Append(' ');
			return sb.ToString();
		}
	}
}
