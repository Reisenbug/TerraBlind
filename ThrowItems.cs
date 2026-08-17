using System.Collections.Generic;
using Terraria;

namespace TerraBlind
{
	// 背包满了就合不出东西 —— 游戏连配方都不算 available。腾位置的办法是把东西【暂时】
	// 扔到地上,合成/放置完再捡回来。不是 trash,扔出去的还要。
	//
	// 不随机挑:随机会扔掉镐子、平台、或者这次合成正要用的材料。挑"最不要紧"的那格。
	public static class ThrowItems
	{
		// 扔出去的东西记在这儿,捡回来之前别再扔第二遍
		public static readonly List<int> Thrown = new();
		public static string LastNote = "";

		// 原版手动丢弃给 noGrabDelay=100(≈1.7秒)。不改它:刚扔就被吸回来等于没扔,
		// 而合成只要一帧,这个窗口足够。
		const int InvEnd = 50;      // 50..57 是钱币/弹药格,不动

		// 这些扔了就回不来:工具没了挖不动,平台/木材是正在盖的房子的料
		static bool Protected(Item it)
		{
			if (it == null || it.IsAir) return true;
			if (it.favorited) return true;
			if (it.pick > 0 || it.axe > 0 || it.hammer > 0) return true;
			// 能放置的一律留着:桥面用任何方块,扔掉的可能正是下一格要铺的
			if (it.createTile >= 0 || it.createWall >= 0) return true;
			return false;
		}

		// 越"不要紧"分越低。能放置的进不来(Protected 挡了),这里只排剩下的杂物
		static int Junk(Item it)
		{
			int s = it.stack;
			if (it.damage > 0 || it.healLife > 0 || it.potion) s += 500;
			return s;
		}

		// 掉落物会弹、会滑,所以要的不是"脚下这一格有地",是【一整段】连续的地。
		// 少了这段,东西滚下悬崖或者掉进岩浆就再也捡不回来了。
		const int SafeRun = 7;

		// 往 dir 方向数:每一列脚下都得是实处,而且那一列本身不能是岩浆
		static int SafeLen(int fx, int fy, int dir)
		{
			for (int n = 0; n < SafeRun; n++)
			{
				int x = fx + dir * n;
				if (!Predicates.InBounds(x, fy)) return n;
				if (Predicates.IsLava(x, fy) || Predicates.IsLava(x, fy + 1)) return n;
				if (!Predicates.IsSolid(x, fy + 1)) return n;
			}
			return SafeRun;
		}

		// 挑一边扔。两边都不安全就挑长的那边 —— 宁可扔出去也不能卡住合成。
		static int PickSide(Player p, out bool safe)
		{
			int fx = ActExecutor.OriginCx(p), fy = ActExecutor.OriginCy(p);
			int r = SafeLen(fx, fy, 1), l = SafeLen(fx, fy, -1);
			safe = r >= SafeRun || l >= SafeRun;
			if (r >= SafeRun) return 1;
			if (l >= SafeRun) return -1;
			return r >= l ? 1 : -1;
		}

		public static int FreeSlots()
		{
			var p = Main.LocalPlayer;
			if (p == null) return 0;
			int n = 0;
			for (int i = 0; i < InvEnd && i < p.inventory.Length; i++)
				if (p.inventory[i] == null || p.inventory[i].IsAir) n++;
			return n;
		}

		// 腾出 want 个空格。返回真正腾出来的数量。扔的东西记进 Thrown,等着 PickBack。
		public static int MakeRoom(int want)
		{
			var p = Main.LocalPlayer;
			LastNote = "";
			if (p == null) return 0;
			int free = FreeSlots();
			if (free >= want) return free;

			var cands = new List<(int junk, int slot)>();
			for (int i = 10; i < InvEnd && i < p.inventory.Length; i++)
			{
				var it = p.inventory[i];
				if (Protected(it)) continue;
				cands.Add((Junk(it), i));
			}
			cands.Sort((a, b) => a.junk.CompareTo(b.junk));

			int dir = PickSide(p, out bool safe);
			if (!safe) DiagLog.Write($"[throw] 两边都没有{SafeRun}格实地,照扔不误(可能捡不回来)");

			int thrown = 0;
			foreach (var (_, slot) in cands)
			{
				if (free + thrown >= want) break;
				var it = p.inventory[slot];
				if (it == null || it.IsAir) continue;
				var drop = p.QuickSpawnItemDirect(p.GetSource_Misc("terrablind_throw"), it, it.stack);
				if (drop == null) continue;
				// 落在挑好的那一边、贴着人,速度清零 —— 不清会继承人的速度飞出去
				drop.position.X = p.position.X + dir * 16f;
				drop.position.Y = p.position.Y;
				drop.velocity = Microsoft.Xna.Framework.Vector2.Zero;
				drop.noGrabDelay = 100;
				Thrown.Add(drop.whoAmI);
				DiagLog.Write($"[throw] 扔出 {it.Name}x{it.stack} (槽{slot}) 往{(dir > 0 ? "右" : "左")} 安全={safe}");
				p.inventory[slot] = new Item();
				thrown++;
			}
			Recipe.FindRecipes();
			int now = FreeSlots();
			LastNote = thrown == 0
				? $"背包满但没有可扔的(全是工具/收藏/建材) 空格={now}"
				: $"扔了{thrown}件,空格 {free}→{now}";
			DiagLog.Write($"[throw] {LastNote}");
			return now;
		}

		// 还在地上、且是我们扔的那些。捡回来靠走过去:原版吸取范围约 2.6 格。
		public static bool AnyOnGround(out int wx, out int wy)
		{
			wx = wy = 0;
			foreach (int who in Thrown)
			{
				if (who < 0 || who >= Main.maxItems) continue;
				var it = Main.item[who];
				if (it == null || !it.active || it.IsAir) continue;
				int x = (int)(it.position.X / 16f), y = (int)(it.position.Y / 16f);
				// 掉进岩浆的别去捡 —— 那是把人也送进去,而人掉岩浆这把就完了
				if (Predicates.IsLava(x, y)) { DiagLog.Write($"[throw] ({x},{y})那件掉岩浆里了,不去捡"); continue; }
				wx = x; wy = y;
				return true;
			}
			return false;
		}

		public static void Forget() { Thrown.Clear(); }
	}
}
