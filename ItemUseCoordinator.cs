using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraBlind
{
	public class ItemUseRequest
	{
		public int TargetWx;
		public int TargetWy;
		public int Slot;          // -1 = keep current selection
		public int DurationTicks; // 0 = run until Stop(). IGNORED for mining and placing — both end on a world fact,
		                          // not a swing budget; only bounds uses with nothing observable (potion/bomb/summon).
		public bool Strict;       // exact-coord caller: never snap to a different tile; target gone = report, don't hunt
	}

	public class ItemUseCoordinator : ModSystem
	{
		private static volatile ItemUseRequest _active;
		private static int _ticksLeft;
		private static bool _snapped;    // has this request already snapped its target this session?
		private static int _watchType = -1;  // TileType of the collect target we're watching; -1 = not watching
		// PLACEMENT has its own eye, mirroring the collect one. Collect watches a tile DISAPPEAR; placement watches
		// the target cell GAIN the tile this item creates (item.createTile — the placement counterpart of pick/axe/
		// hammer). Without this a place action had nothing observable at all and always ended "n/a", which upper
		// layers read as success: 20 ropes could fail silently and every single call reported fine.
		private static int _placeType = -1;  // TileType this item will create; -1 = not a placing item
		private static int _swings;          // COMPLETED swings (itemAnimation falling edge), not frames pressed
		private static int _prevAnim;        // last frame's itemAnimation, for that falling edge
		private static bool _preHadTile;     // something (not ours) occupied the target before we swung
		// How many full swings a placement gets before we call it refused. One is enough when it works — the extra
		// two absorb a swing eaten by a stance change or an item swap.
		private const int PlaceSwingGrace = 3;
		// hard ceiling in frames for one placement attempt, so the attempt ends even if no swing ever completes.
		private const int PlaceFrameCeiling = 90;
		private static int _elapsed;         // ticks since this action started (for the no-progress grace window)
		// 挥这么多帧还是零伤害 = 工具啃不动,早停别耗到超时("两下没动静,换工具")
		private const int ProgressGrace = 45;
		private static int _outOfReachFrames;
		private const int ReachLostGrace = 30;

		// 镐/斧/锤挥出去是为了让某一格消失,那格找不到就没有终点可等 —— 药水炸弹不算
		private static bool IsCollectTool(Item it) => it.pick > 0 || it.axe > 0 || it.hammer > 0;

		// the tile the target snapped to (for HTTP reporting); -1,-1 if no snap happened.
		public static int SnappedWx = -1;
		public static int SnappedWy = -1;
		// 采集:removed/no_progress/timeout。放置:placed/already_there/not_placed/no_swing。
		// n/a 只留给药水炸弹这种既不采也不放的 —— 没有可观测的目标格
		public static string Outcome = "idle";
		// 采集:blocked/tool_weak/out_of_reach。放置:occupied/no_anchor/out_of_reach/wrong_item/out_of_stock
		public static string Reason = "";

		public static bool IsActive => _active != null;

		public static void Start(ItemUseRequest r)
		{
			_active = r;
			_ticksLeft = r.DurationTicks > 0 ? r.DurationTicks : int.MaxValue;
			_snapped = false;
			_watchType = -1;
			_placeType = -1;
			_swings = 0;
			_prevAnim = 0;
			_preHadTile = false;
			_elapsed = 0;
			_outOfReachFrames = 0;
			SnappedWx = -1; SnappedWy = -1;
			Outcome = "running";
			Reason = "";
		}

		public static void Stop()
		{
			if (Outcome == "running") Outcome = "stopped";
			_active = null;
		}

		public static void ApplyControls()
		{
			var req = _active;
			if (req == null) return;
			var p = Main.LocalPlayer;
			if (p == null || !p.active) { _active = null; return; }

			_elapsed++;

			// 采集三分:removed(目标没了=成)/no_progress(零伤害=工具不行,早停)/timeout(还在啃但超预算)
			if (_watchType >= 0)
			{
				var wt = Main.tile[SnappedWx, SnappedWy];
				if (!wt.HasTile || wt.TileType != _watchType)
				{ Outcome = "removed"; _active = null; return; }

				if (_elapsed >= ProgressGrace && TileMineDamage(p, SnappedWx, SnappedWy) <= 0)
				{
					// 零伤害要分清原因:上面压着树/箱子是结构问题(换镐没用),否则才是镐不够硬
					Reason = WorldGen.CanKillTile(SnappedWx, SnappedWy) ? "tool_weak" : "blocked";
					Outcome = "no_progress"; _active = null; return;
				}
			}

			// PLACE completion — the mirror of the collect check above: the target cell now holds the tile this item
			// creates, so the placement landed. Checked before the budget runs out so a successful place ends at once.
			if (_placeType >= 0)
			{
				var pt = Main.tile[req.TargetWx, req.TargetWy];
				if (pt.HasTile && pt.TileType == _placeType)
				{ Outcome = "placed"; _active = null; return; }
			}

			// 放置只按【挥完的次数】判,不按帧预算:一次放置要 useTime 帧,调用方不可能知道,
			// 按预算判会把"动画还没完"误报成"游戏拒绝了" 
			if (_placeType >= 0)
			{
			// 动画永不归零的物品(autoReuse)数不到下降沿,所以再加个帧数上限兜底
				if (_swings >= PlaceSwingGrace || _elapsed >= PlaceFrameCeiling)
				{
					Outcome = "not_placed"; Reason = DiagnosePlace(p, req);
					DiagLog.Write($"[item_use] not_placed at ({req.TargetWx},{req.TargetWy}) swings={_swings} elapsed={_elapsed} reason={Reason}");
					_active = null; return;
				}
			}
			// Uses with NOTHING observable (potion, bomb, summon) are the only ones still bounded by the budget —
			// there is no world fact to wait for, so the swing count is all they have.
			else if (_watchType < 0 && _ticksLeft <= 0)
			{
				if (Outcome == "running") Outcome = "n/a";
				_active = null;
				return;
			}
			_ticksLeft--;

			int slot = req.Slot;
			if (slot < 0)
			{
				slot = FindAxeSlot(p);
				if (slot < 0)
				{
					Terraria.Main.NewText("[item_use] no axe in hotbar, stopping");
					_active = null;
					return;
				}
			}
			// selectedItem only holds items in the hotbar (0-9). A backpack slot (10-49) can't be held — swap it
			// into a hotbar slot first (prefer an empty one, else slot 0), then use from there.
			if (slot >= 10 && slot < p.inventory.Length)
			{
				int hb = -1;
				for (int i = 0; i < 10; i++)
					if (p.inventory[i] == null || p.inventory[i].IsAir) { hb = i; break; }
				if (hb < 0) hb = 0;   // no empty hotbar slot → displace slot 0 (its item goes to the backpack slot)
				var tmp = p.inventory[hb];
				p.inventory[hb] = p.inventory[slot];
				p.inventory[slot] = tmp;
				slot = hb;
			}

			// LLM 给的是"树大概在这儿",常落在树叶或空气上 —— 像原版 SmartCursor 那样吸附到最近可作用的格
			if (!_snapped)
			{
				_snapped = true;
				var it = p.inventory[slot];
				if (it != null && !it.IsAir)
				{
					if (req.Strict)
					{
						// exact-coord caller (batch mine): never re-aim to a different tile. Target gone = report it,
						// not silently snap onto whatever solid rock happens to be nearby.
						var tt = Main.tile[req.TargetWx, req.TargetWy];
						if (!tt.HasTile) { Outcome = "target_gone"; _active = null; return; }
						SnappedWx = req.TargetWx; SnappedWy = req.TargetWy;
						_watchType = tt.TileType;
					}
					else if (TrySnap(it, ref req.TargetWx, ref req.TargetWy))
					{
						SnappedWx = req.TargetWx; SnappedWy = req.TargetWy;
						var st = Main.tile[SnappedWx, SnappedWy];
						if (st.HasTile) _watchType = st.TileType;   // watch this tile for removal (chop/mine done)
					}
					// 放置不吸附:给的坐标就是要填的那格。登记期望出现的 tile,放置才有可观测结果
					if (_watchType < 0 && it.createTile >= 0)
					{
						_placeType = it.createTile;
						SnappedWx = req.TargetWx; SnappedWy = req.TargetWy;
						// 只有【同类型】才算已经放过了:草和藤蔓那格照样放得进去,不挥等于白白少放一次
						var pre = Main.tile[req.TargetWx, req.TargetWy];
						if (pre.HasTile && pre.TileType == _placeType)
						{
							Outcome = "already_there"; Reason = pre.TileType.ToString();
							_active = null; return;
						}
						_preHadTile = pre.HasTile;
					}

					// 采集工具吸附不到任何目标(够不着时半径内一格可作用的都没有)= 没有可观测的终点,
					// 而 DurationTicks=0 的预算是无限的 —— 不在这儿报,就会对着空气永远挥下去。
					if (_watchType < 0 && _placeType < 0 && IsCollectTool(it))
					{
						Outcome = "no_progress"; Reason = "out_of_reach";
						DiagLog.Write($"[item_use] 吸附不到 ({req.TargetWx},{req.TargetWy}),够不着,不挥了");
						_active = null; return;
					}

					// 够不着就是挥空(原版会把目标钳回来),挖脚下还会把人挪走让批量作废 —— 直接报,别耗宽限窗口
					if ((_watchType >= 0 || _placeType >= 0)
						&& !p.IsInTileInteractionRange(SnappedWx, SnappedWy, Terraria.DataStructures.TileReachCheckSettings.Simple))
					{
						Outcome = _placeType >= 0 ? "no_swing" : "no_progress";
						Reason = "out_of_reach"; _active = null; return;
					}
				}
			}

			// 开工时够得着不代表一直够得着:被怪击退、脚下塌了,目标就出了射程,再挥全是空的。
			// 每帧复查,但给宽限 —— 挖矿本来就会小幅位移,一出界就放弃太脆。
			if ((_watchType >= 0 || _placeType >= 0) && SnappedWx >= 0)
			{
				if (p.IsInTileInteractionRange(SnappedWx, SnappedWy, Terraria.DataStructures.TileReachCheckSettings.Simple))
					_outOfReachFrames = 0;
				else if (++_outOfReachFrames >= ReachLostGrace)
				{
					Outcome = _placeType >= 0 ? "no_swing" : "no_progress";
					Reason = "out_of_reach";
					DiagLog.Write($"[item_use] out_of_reach at ({SnappedWx},{SnappedWy}) after {_elapsed}f — 被挪开了");
					_active = null; return;
				}
			}

			float worldX = req.TargetWx * 16f + 8f;
			float worldY = req.TargetWy * 16f + 8f;
			Cursor.AimPx(worldX, worldY);
			p.selectedItem = slot;

			// 数 itemAnimation 的下降沿:数"按了几帧"会把一次挥舞重复计数(动画启动前有好几帧是 0)
			if (_prevAnim > 0 && p.itemAnimation == 0) _swings++;
			_prevAnim = p.itemAnimation;

			if (p.itemTime == 0)
			{
				// 按下去的【同一帧】抹掉目标格的岩浆。早抹邻格会流回来,门重新关上;
				// 只对放置的东西做(_placeType>=0),挖掘不碰液体
				if (_placeType >= 0)
				{
					Concessions.ClearLavaForPlacement(req.TargetWx, req.TargetWy);
					// 目标格和人自己重叠时 vanilla 会拒(Collision.EmptyTile)。
					// 这里【只挂旗】,真正缩碰撞箱在 PreItemCheck -- 我们跑在 PostUpdateEverything,
					// 这一帧的 ItemCheck 早跑完了,旗子给的是下一帧那次
					Concessions.ShrinkHitboxThisFrame = true;
				}
				p.controlUseItem = true;
			}
		}

		// 只报【观测到的事实】,绝不预判原版会不会接受 —— 眼睛报发生了什么,不判断能不能发生。
		// 所以 no_anchor 只作为提示附上,永远不当拦截条件(往空中放绳子是合法的,只是没意义)
		private static string DiagnosePlace(Player p, ItemUseRequest req)
		{
			int x = req.TargetWx, y = req.TargetWy;
			if (!InBounds(x, y)) return "out_of_bounds";
			var it = p.inventory[p.selectedItem];
			if (it == null || it.IsAir) return "empty_hand";
			if (it.createTile != _placeType) return "wrong_item";
			if (it.stack <= 0) return "out_of_stock";
			if (!p.IsInTileInteractionRange(x, y, Terraria.DataStructures.TileReachCheckSettings.Simple)) return "out_of_reach";
			var t = Main.tile[x, y];
			if (t.HasTile) return "occupied";
			// 挥了、格子空、够得着、东西对、有货,却没出现 —— 原版自己拒的。如实报,附个不具约束力的提示
			return HasAnchor(x, y) ? "rejected" : "rejected_no_anchor_hint";
		}

		// 绳子只能从已有绳子或天花板往下接,对着半空放是静默失败 —— 以前这种情况会误报成功
		public static bool HasAnchor(int x, int y)
		{
			(int, int)[] n = { (0, -1), (0, 1), (-1, 0), (1, 0) };
			foreach (var (dx, dy) in n)
			{
				int a = x + dx, b = y + dy;
				if (!InBounds(a, b)) continue;
				var t = Main.tile[a, b];
				if (t.HasTile && (Main.tileSolid[t.TileType] || Main.tileSolidTop[t.TileType] || Main.tileRope[t.TileType]))
					return true;
			}
			return false;
		}

		// 把粗略坐标吸附到最近的可作用格。半径要给得宽:LLM 的坐标常偏出树干十几格
		private const int SnapRadius = 12;
		private static bool TrySnap(Item it, ref int wx, ref int wy)
		{
			if (it.axe > 0) return TrySnapTree(ref wx, ref wy);

			System.Func<int, int, bool> ok;
			if (it.hammer > 0) ok = (x, y) => Main.tile[x, y].HasTile && Main.tileHammer[Main.tile[x, y].TileType];
			else if (it.pick > 0) ok = (x, y) => Main.tile[x, y].HasTile && Main.tileSolid[Main.tile[x, y].TileType] && !Main.tileHammer[Main.tile[x, y].TileType];
			else return false;   // not a collecting tool — don't snap

			if (InBounds(wx, wy) && ok(wx, wy)) return false;   // already on a valid tile, no snap needed

			int bestX = -1, bestY = -1, bestD = int.MaxValue;
			for (int dx = -SnapRadius; dx <= SnapRadius; dx++)
				for (int dy = -SnapRadius; dy <= SnapRadius; dy++)
				{
					int x = wx + dx, y = wy + dy;
					if (!InBounds(x, y) || !ok(x, y)) continue;
					int d = dx * dx + dy * dy;
					if (d < bestD) { bestD = d; bestX = x; bestY = y; }
				}
			if (bestX < 0) return false;
			wx = bestX; wy = bestY;
			return true;
		}

		// 只有砍中主干最底下那格整棵树才倒,砍树根/枝杈只掉那点装饰。
		// 树是同一个 TileType,靠 frameX 区分部位(22=主干 44=左 66=右 88=枝),先找主干再滑到根部
		private static bool TrySnapTree(ref int wx, ref int wy)
		{
			int bestX = -1, bestY = -1, bestD = int.MaxValue;
			for (int dx = -SnapRadius; dx <= SnapRadius; dx++)
				for (int dy = -SnapRadius; dy <= SnapRadius; dy++)
				{
					int x = wx + dx, y = wy + dy;
					if (!InBounds(x, y)) continue;
					var t = Main.tile[x, y];
					if (!t.HasTile || !TileID.Sets.IsATreeTrunk[t.TileType]) continue;
					int d = dx * dx + dy * dy;
					if (d < bestD) { bestD = d; bestX = x; bestY = y; }
				}
			if (bestX < 0) return false;

			int type = Main.tile[bestX, bestY].TileType;
			bool IsTree(int x, int y) => InBounds(x, y) && Main.tile[x, y].HasTile && Main.tile[x, y].TileType == type;
			// 主干是唯一贯穿整棵树高的那一列,枝杈只占部分高度所以总更短 —— 取最长的那列就是主干
			int trunkX = bestX, trunkLen = -1;
			for (int sx = -3; sx <= 3; sx++)
			{
				int cx = bestX + sx;
				if (!IsTree(cx, bestY)) continue;
				int up = 0, dn = 0;
				while (IsTree(cx, bestY - up - 1)) up++;
				while (IsTree(cx, bestY + dn + 1)) dn++;
				int len = up + dn + 1;
				if (len > trunkLen) { trunkLen = len; trunkX = cx; }
			}
			bestX = trunkX;
			while (IsTree(bestX, bestY + 1)) bestY++;   // walk down the trunk column to its ground-contact cell

			wx = bestX; wy = bestY;
			return true;
		}

		// Live view of the watched target for /item_use_status: is there still a tile, can the HELD tool act on it,
		// and how much mining damage has accumulated — rising damage = the swings are landing; flat 0 = flailing.
		public static string TargetJson()
		{
			if (SnappedWx < 0 || !InBounds(SnappedWx, SnappedWy)) return "null";
			var t = Main.tile[SnappedWx, SnappedWy];
			bool has = t.HasTile;
			int type = has ? t.TileType : -1;
			var p = Main.LocalPlayer;
			bool toolOk = false;
			int dmg = 0;
			if (p != null && p.active)
			{
				if (has)
				{
					var it = p.inventory[p.selectedItem];
					if (it != null && !it.IsAir)
						toolOk = it.axe > 0 ? Main.tileAxe[type]
							: it.hammer > 0 ? Main.tileHammer[type]
							: it.pick > 0 && Main.tileSolid[type] && !Main.tileAxe[type] && !Main.tileHammer[type];
					dmg = TileMineDamage(p, SnappedWx, SnappedWy);
				}
			}
			return "{\"has_tile\":" + (has ? "true" : "false") + ",\"type\":" + type
				+ ",\"tool_ok\":" + (toolOk ? "true" : "false") + ",\"damage\":" + dmg + "}";
		}

		// Accumulated mining damage on a tile from this player's swings (hitTile buffers it, decaying after 60 ticks
		// of no hits). >0 means the tool is actually chipping the tile; 0 after the grace window means it isn't.
		private static int TileMineDamage(Player p, int x, int y)
		{
			int id = p.hitTile.TryFinding(x, y, 1);   // hitType 1 = TILE
			if (id < 0) return 0;
			return p.hitTile.data[id].damage;
		}

		private static bool InBounds(int x, int y) =>
			x >= 0 && y >= 0 && x < Main.maxTilesX && y < Main.maxTilesY;

		private static int FindAxeSlot(Player p)
		{
			for (int i = 0; i < 10; i++)
			{
				var item = p.inventory[i];
				if (item != null && !item.IsAir && item.axe > 0)
					return i;
			}
			return -1;
		}
	}
}
