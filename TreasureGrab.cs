using Terraria;
using Terraria.ObjectData;

namespace TerraBlind
{
	// 拿一个宝:走过去 → 开箱 → 掏空 → 验收。整条链在 mod 里跑完,python 只发一次再轮询。
	//
	// 【为什么搬进来】。原来这一套写在 python 的 _greed_collect 里:nav_to、interact、
	// 轮询 /state 的 last_interact、loot_all、记账。跨进程轮询一个【不带坐标的全局字符串】,
	// 于是这次读到的 opened 可能是上一个箱子留下的;而"拿到了"只等于"发过 loot_all",
	// 箱子到底空没空从来没人查 —— 日志里一串 GOT 全是水分。
	public static class TreasureGrab
	{
		enum Ph { Idle, Goto, Open, Loot, Done }
		static Ph _ph = Ph.Idle;

		static int _tx, _ty;        // 调用方给的格(箱子任意一格)
		static int _ax, _ay;        // 归一后的左上角锚点
		static int _idx = -1;       // Main.chest 下标
		static int _frames, _lootFrames;

		// 【调用方已经走到跟前了】。远路那一段归 python 的 nav_to(它带顺路采集),这儿只兜住
		// "到了之后被怪挤开几格"这种小位移。原来设 60*30 当【全程】超时,而宝藏常在几百格外,
		// 443 格的目标 30 秒判失败,整趟一个都没拿到。
		const int MaxGoto = 60 * 30;
		const int LootWait = 30;       // LootAll 之后等几帧再验收 —— 掏空是当帧做的,给点余量

		public static bool IsRunning => _ph != Ph.Idle && _ph != Ph.Done;
		public static string Outcome = "idle";
		public static string Reason = "";

		public static bool Start(int tx, int ty, out string why)
		{
			why = "";
			var p = Main.LocalPlayer;
			if (p == null || !p.active) { why = "no_player"; return false; }
			_tx = tx; _ty = ty; _frames = 0; _lootFrames = 0; _idx = -1;
			Outcome = "running"; Reason = "";
			// 【先归一到左上角】。箱子占 2x2 而 Chest.FindChest 只认锚点,坐标是扫地形来的,
			// 扫到右上/左下/右下都可能 —— 不归一就报 no_chest,人走到跟前也开不了。
			var a = TileObjectData.TopLeft(tx, ty);
			_ax = a.X >= 0 ? a.X : tx;
			_ay = a.Y >= 0 ? a.Y : ty;
			DiagLog.Write($"[grab] START ({tx},{ty}) 锚点({_ax},{_ay})");
			// 够得着就行 —— 开箱不用踩在箱子那一格上
			RecedingNav.Start(_ax, _ay, RecedingNav.Mode.Reach);
			_ph = Ph.Goto;
			return true;
		}

		public static void Stop()
		{
			if (Outcome == "running") { Outcome = "stopped"; Reason = "外部叫停"; }
			RecedingNav.Stop();
			_ph = Ph.Idle;
		}

		static void Fail(string reason)
		{
			Outcome = "failed"; Reason = reason; _ph = Ph.Done;
			DiagLog.Write($"[grab] FAIL ({_tx},{_ty}) {reason}");
			CloseChest();
		}

		static void Done(string note)
		{
			Outcome = "done"; Reason = note; _ph = Ph.Done;
			DiagLog.Write($"[grab] DONE ({_tx},{_ty}) {note}");
			CloseChest();
		}

		// 开着的箱子会挡下一个:vanilla 关箱就是 chest=-1 + FindRecipes
		static void CloseChest()
		{
			var p = Main.LocalPlayer;
			if (p != null && p.chest != -1) { p.chest = -1; Recipe.FindRecipes(); }
		}

		// 【开箱只有这一份】。/interact 那条路(LLM 直接编排动作时用)也调它 —— 原来那边自己
		// 抄了一遍归一/陷阱箱/锁/占用,两份判据各改各的迟早分叉。
		// 返回 Main.chest 下标,失败返回 -1 并把原因写进 why。
		public static int OpenAt(int tx, int ty, out string why)
		{
			why = "";
			var p = Main.LocalPlayer;
			if (p == null || !p.active) { why = "no_player"; return -1; }
			// 箱子占 2x2 而 Chest.FindChest 只认锚点。坐标是扫地形来的,扫到哪一格都可能
			var a = TileObjectData.TopLeft(tx, ty);
			int ax = a.X >= 0 ? a.X : tx, ay = a.Y >= 0 ? a.Y : ty;
			// 陷阱箱(FakeContainers)在 Main.chest 里也有条目,直接写 Player.chest 就开了 ——
			// 而那条路绕过右键,引线根本不触发,是作弊。一律不开。按锚点判:它同样看 frameX
			if (HttpServerSystem.IsFakeChestPublic(ax, ay)) { why = "trapped_chest"; return -1; }
			// 上一个还开着就先关,否则这个开不了(vanilla 关箱就是 chest=-1 + FindRecipes)
			if (p.chest != -1) { p.chest = -1; Recipe.FindRecipes(); }
			int idx = Chest.FindChest(ax, ay);
			if (idx == -1)
			{
				var t = Main.tile[tx, ty];
				why = "no_chest";
				DiagLog.Write($"[grab] ({tx},{ty}) 归一到({ax},{ay}) 仍找不到箱子 tile={(t.HasTile ? t.TileType.ToString() : "空")}");
				return -1;
			}
			var ch = Main.chest[idx];
			if (ch == null) { why = "no_chest"; return -1; }
			// 锁和占用用 vanilla 自己的判据,别抄 frameX 范围。用箱子自己的锚点格判
			if (Chest.IsLocked(ch.x, ch.y)) { why = "locked"; return -1; }
			if (Chest.UsingChest(idx) != -1) { why = "in_use"; return -1; }
			p.chest = idx; p.chestX = ax; p.chestY = ay;
			Main.playerInventory = true;
			Terraria.Audio.SoundEngine.PlaySound(Terraria.ID.SoundID.MenuOpen);
			return idx;
		}

		// 箱子还剩几件。全空 = 真掏干净了,这是唯一算数的验收
		static int ItemsLeft()
		{
			if (_idx < 0 || _idx >= Main.chest.Length) return -1;
			var ch = Main.chest[_idx];
			if (ch == null || ch.item == null) return -1;
			int n = 0;
			foreach (var it in ch.item) if (it != null && !it.IsAir && it.stack > 0) n++;
			return n;
		}

		public static void Tick()
		{
			// 【心跳在 241 帧断了但人还在走】= 要么 IsRunning 变假,要么这个函数没被调用。
			// 分不出来就没法修,所以在最外层先记一笔
			if (_ph != Ph.Idle && _ph != Ph.Done && Main.GameUpdateCount % 120 == 0)
				DiagLog.Write($"[grab] tick进来了 ph={_ph} frames={_frames}");
			if (!IsRunning) return;
			var p = Main.LocalPlayer;
			if (p == null || !p.active) { Fail("no_player"); return; }
			_frames++;

			// 【无声 return 要有心跳】。原来 START 之后一路没日志 --- 到底在走、在等、还是
			// Tick 根本没被调用,从日志里分不出来
			if (_frames % 120 == 1)
				DiagLog.Write($"[grab] 心跳 {_ph} {_frames}帧 目标({_ax},{_ay}) 人({ActExecutor.OriginCx(p)},{ActExecutor.OriginCy(p)}) nav={RecedingNav.Active} last={RecedingNav.LastStop} 够得着={Reach.CanInteract(p, _ax, _ay)}");

			switch (_ph)
			{
				case Ph.Goto:
					if (_frames > MaxGoto) { Fail($"走了{_frames}帧还没到"); return; }
					if (RecedingNav.Active) return;
					if (RecedingNav.LastStop != "done" && !Reach.CanInteract(p, _ax, _ay))
					{ Fail($"走不过去:{RecedingNav.LastStop}"); return; }
					_ph = Ph.Open;
					return;

				case Ph.Open:
					{
						// 开箱查【交互】那把尺子(只有 tileRangeX),不是挖的那把 —— 用错会在够不着处开箱
						if (!Reach.CanInteract(p, _ax, _ay)) { Fail("到了却够不着(交互距离)"); return; }
						_idx = OpenAt(_tx, _ty, out string ow);
						if (_idx < 0) { Fail(ow); return; }
						int before = ItemsLeft();
						if (before == 0) { Done("本来就是空的"); return; }
						DiagLog.Write($"[grab] 开了 idx={_idx} 里面{before}件");
						// 掏之前腾格子:背包满了 LootAll 会把塞不下的原样写回箱子
						KeepList.MakeRoom(LootRoom);
						Terraria.UI.ChestUI.LootAll();
						_lootFrames = 0;
						_ph = Ph.Loot;
						return;
					}

				case Ph.Loot:
					{
						_lootFrames++;
						int left = ItemsLeft();
						if (left == 0) { Done("掏空了"); return; }
						if (_lootFrames < LootWait) return;
						// 还有剩:多半是背包塞不下。再腾一次再掏,还剩就如实报,别当成功
						if (_lootFrames == LootWait)
						{
							KeepList.MakeRoom(LootRoom);
							Terraria.UI.ChestUI.LootAll();
							return;
						}
						if (_lootFrames > LootWait * 2)
						{
							Outcome = "partial"; Reason = $"还剩{left}件,背包塞不下";
							_ph = Ph.Done;
							DiagLog.Write($"[grab] PARTIAL ({_tx},{_ty}) 还剩{left}件");
							CloseChest();
						}
						return;
					}
			}
		}

		const int LootRoom = 8;   // 掏之前腾几格。箱子 40 格但大多是零头,腾太多等于白删建材

		public static string StatusJson()
			=> "{\"running\":" + (IsRunning ? "true" : "false")
			 + ",\"outcome\":\"" + Outcome + "\""
			 + ",\"reason\":\"" + HttpServerSystem.JsonEscPublic(Reason) + "\""
			 + ",\"at\":[" + _tx + "," + _ty + "]"
			 + ",\"anchor\":[" + _ax + "," + _ay + "]}";
	}
}
