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
			// 【手在场就先打手】。血量那项让 4400 血的头碾压 600 血的手,
			// 而手不打掉,头一直无敌 -- 排序反了就是全程在打一个打不动的目标
			float part = npc.type == Terraria.ID.NPCID.SkeletronHand ? 100f : 0f;
			return hpFrac * 100f * near + speed * 2f + npc.life * 0.01f + part;
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
				if (d > RangeCells) continue;
				bool walled = Blocked(atCx, atCy, ncx, ncy);
				bool toward = (ncx < atCx && npc.velocity.X > 0.1f) || (ncx > atCx && npc.velocity.X < -0.1f);
				if (n++ > 0) sb.Append(',');
				sb.Append("{\"name\":\"").Append(npc.TypeName ?? "?").Append('"')
				  .Append(",\"cell\":[").Append(ncx).Append(',').Append(ncy).Append(']')
				  .Append(",\"damage\":").Append(npc.damage)
				  .Append(",\"damage_pct_of_my_hp\":").Append((int)(npc.damage * 100f / System.Math.Max(1, p.statLife)))
				  .Append(",\"hp\":").Append(npc.life)
				  .Append(",\"speed\":").Append((System.Math.Abs(npc.velocity.X) + System.Math.Abs(npc.velocity.Y)).ToString("0.0"))
				  .Append(",\"threat\":").Append(Score(p, npc, d).ToString("0.0"))
				  .Append(",\"distance_cells\":").Append(d)
				  .Append(",\"flies\":").Append(npc.noGravity ? "true" : "false")
				  .Append(",\"behind_blocks\":").Append(walled ? "true" : "false")
				  .Append(",\"moving_toward_player\":").Append(toward ? "true" : "false")
				  .Append(",\"boss\":").Append(npc.boss ? "true" : "false")
				  .Append('}');
			}
			return sb.Append(']').ToString();
		}

		// 敌方弹幕。【眼睛里原本没有这一类东西】:只扫 NPC 的话,会放弹幕的 boss
		// 等于躲无可躲 -- 打得到你的东西有一半不在视野里
		public static string ProjJson(Player p, int atCx, int atCy)
		{
			var sb = new StringBuilder("[");
			int n = 0;
			for (int i = 0; i < Main.maxProjectiles && n < 12; i++)
			{
				var pr = Main.projectile[i];
				if (pr == null || !pr.active || !pr.hostile || pr.damage <= 0) continue;
				int pcx = (int)(pr.Center.X / 16f), pcy = (int)(pr.Center.Y / 16f);
				int d = System.Math.Abs(pcx - atCx) + System.Math.Abs(pcy - atCy);
				if (d > RangeCells) continue;
				// 朝我来才值得报。飞走的弹幕不该占名额,更不该让它以为处处是危险
				bool toward = (pcx < atCx && pr.velocity.X > 0.1f) || (pcx > atCx && pr.velocity.X < -0.1f)
						   || (pcy < atCy && pr.velocity.Y > 0.1f) || (pcy > atCy && pr.velocity.Y < -0.1f);
				if (!toward) continue;
				if (n++ > 0) sb.Append(',');
				sb.Append("{\"name\":\"").Append(pr.Name ?? "?").Append('"')
				  .Append(",\"cells_right\":").Append(pcx - atCx)
				  .Append(",\"cells_below\":").Append(pcy - atCy)
				  .Append(",\"distance_cells\":").Append(d)
				  .Append(",\"damage_pct_of_my_hp\":").Append((int)(pr.damage * 100f / System.Math.Max(1, p.statLife)))
				  .Append(",\"vx\":").Append(pr.velocity.X.ToString("0.0"))
				  .Append(",\"vy\":").Append(pr.velocity.Y.ToString("0.0"))
				  .Append('}');
			}
			return sb.Append(']').ToString();
		}

		// 弹幕的【汇总】。逐发列表看不出该往哪走 -- 那要把十几发加起来看,
		// 是确定的算术,代码算比让模型心算可靠
		public static string PressureJson(Player p)
		{
			int left = 0, right = 0, above = 0, below = 0;
			int soonest = -1;
			for (int i = 0; i < Main.maxProjectiles; i++)
			{
				var pr = Main.projectile[i];
				if (pr == null || !pr.active || !pr.hostile || pr.damage <= 0) continue;
				float dx = pr.Center.X - p.Center.X, dy = pr.Center.Y - p.Center.Y;
				if (System.Math.Abs(dx) / 16f > RangeCells || System.Math.Abs(dy) / 16f > RangeCells) continue;
				int f = FramesToReach(p, pr);
				if (f < 0) continue;
				if (dx < 0) left++; else right++;
				if (dy < 0) above++; else below++;
				if (soonest < 0 || f < soonest) soonest = f;
			}
			return "{\"from_left\":" + left + ",\"from_right\":" + right
				 + ",\"from_above\":" + above + ",\"from_below\":" + below
				 + ",\"frames_until_the_closest_one_reaches_me\":"
				 + (soonest < 0 ? "\"没有朝我来的\"" : soonest.ToString()) + "}";
		}

		// 这发弹幕按当前速度还有几帧碰到我。不朝我来就 -1。和 Dodge.FramesToHit 同一套算法
		public static int FramesToReach(Player p, Projectile pr)
		{
			float gapX = System.Math.Abs(pr.Center.X - p.Center.X) - (pr.width + p.width) * 0.5f;
			float gapY = System.Math.Abs(pr.Center.Y - p.Center.Y) - (pr.height + p.height) * 0.5f;
			float closeX = (pr.Center.X > p.Center.X) == (pr.velocity.X < 0) ? System.Math.Abs(pr.velocity.X) : 0f;
			float closeY = (pr.Center.Y > p.Center.Y) == (pr.velocity.Y < 0) ? System.Math.Abs(pr.velocity.Y) : 0f;
			if (closeX < 0.1f && closeY < 0.1f) return -1;
			float fx = closeX > 0.1f ? gapX / closeX : 9999f;
			float fy = closeY > 0.1f ? gapY / closeY : 9999f;
			float f = System.Math.Max(fx <= 0f ? 0f : fx, fy <= 0f ? 0f : fy);
			return f > 600f ? -1 : (int)f;
		}

		// 最近的一发还有几帧到。反射层用它 -- 原来 incoming 只看 boss 本体,弹幕再近也不算数
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
