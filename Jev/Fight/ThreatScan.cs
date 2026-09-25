using System.Text;
using Terraria;

namespace TerraBlind
{
	// 附近敌人的事实。只测,不判威胁 -- 哪只危险交给 Jev,它认识怪的名字
	public static class ThreatScan
	{
		public const int RangeCells = 30;

		// 两点之间有没有实心方块挡着。全项目没有现成的,这里按格子步进自己走一条线
		public static bool Blocked(int x0, int y0, int x1, int y1)
		{
			int dx = System.Math.Abs(x1 - x0), dy = System.Math.Abs(y1 - y0);
			int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
			int err = dx - dy, x = x0, y = y0;
			for (int guard = 0; guard < 256; guard++)
			{
				if (x == x1 && y == y1) return false;
				int e2 = err * 2;
				if (e2 > -dy) { err -= dy; x += sx; }
				if (e2 < dx) { err += dx; y += sy; }
				if ((x != x0 || y != y0) && Predicates.IsSolid(x, y)) return true;
			}
			return false;
		}

		// 威胁分。伤害按【占当前血量】的比例算:同样 30 点,满血是擦伤,残血是致命
		public static float Score(Player p, NPC npc, int dist)
		{
			float hpFrac = npc.damage / (float)System.Math.Max(1, p.statLife);
			float speed = System.Math.Abs(npc.velocity.X) + System.Math.Abs(npc.velocity.Y);
			float near = 1f / (dist + 1f);
			// 【手在场就先打手】。手不打掉头一直无敌,排序反了就是全程在打一个打不动的目标
			float part = npc.type == Terraria.ID.NPCID.SkeletronHand ? 100f : 0f;
			// 【不加血量项】。毁灭者 80000 血,那一项让它在 250 格外也压过身边的两个王
			return hpFrac * 100f * near + speed * 2f + part;
		}

		public static string Json(Player p, int atCx, int atCy)
		{
			var sb = new StringBuilder("[");
			int n = 0;
			for (int i = 0; i < Main.maxNPCs; i++)
			{
				var npc = Main.npc[i];
				if (npc == null || !npc.active) continue;
				if (npc.townNPC || npc.friendly) continue;
				if (npc.lifeMax <= 5 && npc.damage == 0) continue;
				int ncx = (int)(npc.Center.X / 16f), ncy = (int)(npc.Center.Y / 16f);
				int d = System.Math.Abs(ncx - atCx) + System.Math.Abs(ncy - atCy);
				// 【boss 不受 30 格限制】。双子分开飞,另一只常常在 30 格外 --
				// 滤掉它 Jev 就只看得见一只眼睛,躲开这只正好撞上那只
				if (d > RangeCells && !npc.boss) continue;
				bool walled = Blocked(atCx, atCy, ncx, ncy);
				if (n++ > 0) sb.Append(',');
				sb.Append("{\"name\":\"").Append(npc.TypeName ?? "?").Append('"')
				  .Append(",\"damage\":").Append(npc.damage)
				  .Append(",\"damage_pct_of_my_hp\":").Append((int)(npc.damage * 100f / System.Math.Max(1, p.statLife)))
				  .Append(",\"speed\":").Append((System.Math.Abs(npc.velocity.X) + System.Math.Abs(npc.velocity.Y)).ToString("0.0"))
				  .Append(",\"distance_cells\":").Append(d)
				  .Append(",\"flies\":").Append(npc.noGravity ? "true" : "false")
				  .Append(",\"behind_blocks\":").Append(walled ? "true" : "false")
				  .Append(",\"boss\":").Append(npc.boss ? "true" : "false")
				  .Append('}');
			}
			return sb.Append(']').ToString();
		}

		// 敌方弹幕。【眼睛里原本没有这一类东西】:只扫 NPC 的话,会放弹幕的 boss
		// 等于躲无可躲 -- 打得到你的东西有一半不在视野里
		public static string ProjJson(Player p, int atCx, int atCy)
		{
			// 【名额给最快打到的那几发,不是数组里靠前的】。喷火一次几十发,
			// 按下标截断会让名额全被远处的火星占掉,真正要命的那发反而报不出来
			var pick = new System.Collections.Generic.List<Projectile>();
			for (int i = 0; i < Main.maxProjectiles; i++)
			{
				var pr = Main.projectile[i];
				if (pr == null || !pr.active || !pr.hostile || pr.damage <= 0) continue;
				int pcx0 = (int)(pr.Center.X / 16f), pcy0 = (int)(pr.Center.Y / 16f);
				int d0 = System.Math.Abs(pcx0 - atCx) + System.Math.Abs(pcy0 - atCy);
				// 【朝我来的不受 30 格限制】。人常年停在 38 格外,而弹幕是从 boss 那边飞过来的
				if (d0 > RangeCells && FramesToReach(p, pr) < 0) continue;
				// 朝我来才值得报。飞走的弹幕不该占名额,更不该让它以为处处是危险
				bool toward0 = (pcx0 < atCx && pr.velocity.X > 0.1f) || (pcx0 > atCx && pr.velocity.X < -0.1f)
							|| (pcy0 < atCy && pr.velocity.Y > 0.1f) || (pcy0 > atCy && pr.velocity.Y < -0.1f);
				// 贴在身上的不管朝哪飞都要报。火焰扫过来时不"朝我来",但人正站在里面
				if (!toward0 && d0 > 6) continue;
				pick.Add(pr);
			}
			pick.Sort((a, b) =>
			{
				int fa = FramesToReach(p, a), fb = FramesToReach(p, b);
				if (fa < 0) fa = int.MaxValue;
				if (fb < 0) fb = int.MaxValue;
				return fa.CompareTo(fb);
			});

			var sb = new StringBuilder("[");
			int n = 0;
			foreach (var pr in pick)
			{
				if (n >= 12) break;
				int pcx = (int)(pr.Center.X / 16f), pcy = (int)(pr.Center.Y / 16f);
				int f = FramesToReach(p, pr);
				// 【报实际速度】。有的弹幕一帧走好几次,报 velocity 会把它说得比实际慢
				float step = pr.extraUpdates + 1;
				if (n++ > 0) sb.Append(',');
				sb.Append("{\"name\":\"").Append((pr.Name ?? "?").Replace("\"", "")).Append('"')
				  .Append(",\"damage_percent_of_my_hp\":").Append((int)(pr.damage * 100f / System.Math.Max(1, p.statLife)))
				  .Append(",\"cells_to_my_right\":").Append(pcx - atCx)
				  .Append(",\"cells_above_me\":").Append(atCy - pcy)
				  .Append(",\"speed_to_the_right\":").Append((int)(pr.velocity.X * step * 60f / 16f))
				  .Append(",\"speed_upward\":").Append((int)(-pr.velocity.Y * step * 60f / 16f))
				  .Append(",\"frames_until_it_hits_me\":").Append(f < 0 ? "\"not heading at me\"" : f.ToString())
				  .Append('}');
			}
			return sb.Append(']').ToString();
		}

		// 弹幕的【汇总】。逐发列表看不出该往哪走 -- 那要把十几发加起来看,
		// 是确定的算术,代码算比让模型心算可靠
		public static string PressureJson(Player p)
		{
			int left = 0, right = 0, above = 0, below = 0, onMe = 0;
			int soonest = -1;
			for (int i = 0; i < Main.maxProjectiles; i++)
			{
				var pr = Main.projectile[i];
				if (pr == null || !pr.active || !pr.hostile || pr.damage <= 0) continue;
				float dx = pr.Center.X - p.Center.X, dy = pr.Center.Y - p.Center.Y;
				if (System.Math.Abs(dx) / 16f > RangeCells || System.Math.Abs(dy) / 16f > RangeCells) continue;
				int f = FramesToReach(p, pr);
				// 【围着我的也要数】。喷火是一片停在身上的火,不"朝我来",
				// 按 FramesToReach 滤就整片消失 -- 而那正是最该退开的时候
				int cells = (int)(System.Math.Abs(dx) / 16f + System.Math.Abs(dy) / 16f);
				if (cells <= 6) onMe++;
				if (f < 0) continue;
				if (dx < 0) left++; else right++;
				if (dy < 0) above++; else below++;
				if (soonest < 0 || f < soonest) soonest = f;
			}
			return "{\"from_left\":" + left + ",\"from_right\":" + right
				 + ",\"from_above\":" + above + ",\"from_below\":" + below
				 + ",\"hostile_shots_within_six_cells_of_me\":" + onMe
				 + ",\"frames_until_the_closest_one_reaches_me\":"
				 + (soonest < 0 ? "\"none heading at me\"" : soonest.ToString()) + "}";
		}

		// 这发弹幕按当前速度还有几帧碰到我。不朝我来就 -1。和 Dodge.FramesToHit 同一套算法
		public static int FramesToReach(Player p, Projectile pr)
		{
			float gapX = System.Math.Abs(pr.Center.X - p.Center.X) - (pr.width + p.width) * 0.5f;
			float gapY = System.Math.Abs(pr.Center.Y - p.Center.Y) - (pr.height + p.height) * 0.5f;
			// 【一帧走 extraUpdates+1 次】(Projectile.cs:15908 的 while)。魔焰眼的喷火是 3,
			// 也就是实际速度的 4 倍 -- 不乘就把到达时间高估四倍,预警永远来不及
			float step = pr.extraUpdates + 1;
			float vx = pr.velocity.X * step, vy = pr.velocity.Y * step;
			float closeX = (pr.Center.X > p.Center.X) == (vx < 0) ? System.Math.Abs(vx) : 0f;
			float closeY = (pr.Center.Y > p.Center.Y) == (vy < 0) ? System.Math.Abs(vy) : 0f;
			// 已经重叠就是正在碰,不管它往哪飞
			if (gapX <= 0f && gapY <= 0f) return 0;
			if (closeX < 0.1f && closeY < 0.1f) return -1;
			// 【和 Dodge.FramesToHit 同一个坑】。不靠近的轴给 9999 再取 Max,
			// 平着飞过来的弹幕就永远报"没威胁"
			float fx = gapX <= 0f ? 0f : (closeX > 0.1f ? gapX / closeX : -1f);
			float fy = gapY <= 0f ? 0f : (closeY > 0.1f ? gapY / closeY : -1f);
			if (fx < 0f || fy < 0f) return -1;
			float f = System.Math.Max(fx, fy);
			return f > 600f ? -1 : (int)f;
		}

		// 最近的一发还有几帧到。反射层用它 -- 原来 incoming 只看 boss 本体,弹幕再近也不算数
		// 最快打到我的那发叫什么。【只为查日志】:分不出是哪种弹幕就没法判断是不是漏检了
		public static string SoonestName(Player p)
		{
			int best = -1;
			string name = "无";
			for (int i = 0; i < Main.maxProjectiles; i++)
			{
				var pr = Main.projectile[i];
				if (pr == null || !pr.active || !pr.hostile || pr.damage <= 0) continue;
				int f = FramesToReach(p, pr);
				if (f < 0) continue;
				if (best < 0 || f < best) { best = f; name = $"{pr.Name}(x{pr.extraUpdates + 1})"; }
			}
			return name;
		}

		public static int SoonestHit(Player p)
		{
			int best = -1;
			for (int i = 0; i < Main.maxProjectiles; i++)
			{
				var pr = Main.projectile[i];
				if (pr == null || !pr.active || !pr.hostile || pr.damage <= 0) continue;
				int f = FramesToReach(p, pr);
				if (f < 0) continue;
				if (best < 0 || f < best) best = f;
			}
			return best;
		}

		public static int Count(Player p, int atCx, int atCy)
		{
			int n = 0;
			for (int i = 0; i < Main.maxNPCs; i++)
			{
				var npc = Main.npc[i];
				if (npc == null || !npc.active) continue;
				if (npc.townNPC || npc.friendly) continue;
				if (npc.lifeMax <= 5 && npc.damage == 0) continue;
				int ncx = (int)(npc.Center.X / 16f), ncy = (int)(npc.Center.Y / 16f);
				if (System.Math.Abs(ncx - atCx) + System.Math.Abs(ncy - atCy) <= RangeCells) n++;
			}
			return n;
		}
	}
}
