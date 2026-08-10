using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraBlind
{
	public class StateSnapshotPlayer : ModPlayer
	{
		private const int ControlTimeoutTicks = 60;
		private const int JumpHoldFrames = 15;
		private const int AutoJumpCooldownFrames = 10;
		private int _jumpFramesLeft;
		private int _autoJumpCooldown;
		private float _prevVy;
		private static int _bridgeStartTick;   // 测铺路用时:开工那一帧,铺完报一次
		public static bool JumpPlaceEnabled = false;
		public static bool WalkTraceEnabled = false;
		private bool _jumpPlaceFired;
		// 后台扫房址的结果。画图和 nav 都只能在主线程碰,所以后台只放结论,下一帧再消费。
		private class SiteResult { public bool Got; public int Bx, By, Scanned, Fx, Fy; }
		private static volatile SiteResult _site;
		// H 选好的房址:nav 走完就在这儿开工。站位由 HouseBuilder.Ph.Lift 自己对齐,不要求按键时站对。
		private static (int x, int y)? _pendingHouse;
		private static int _pillarTestFrom, _pillarTestTarget;
		private static int _houseNavTries;

		public override void ProcessTriggers(Terraria.GameInput.TriggersSet triggersSet)
		{
			if (_pendingHouse.HasValue && !RecedingNav.Active && !HouseBuilder.IsRunning)
			{
				var (hx, hy) = _pendingHouse.Value;
				var php2 = Main.LocalPlayer;
				int scol = hx;   // 梯子就在房子那一列
				int gapX = System.Math.Abs(ActExecutor.OriginCx(php2) - scol);
				// nav 会在半路停下还报 done,所以自己验位置。差得远就重走 —— SettleAt 只会走平地,
				// 跨不了沟翻不了墙,那是寻路的活。差几格才交给 Ph.Lift 精调。
				if (gapX > 6 && ++_houseNavTries <= 3)
				{
					Main.NewText($"[TerraBlind] 离开工位还差 {gapX} 格,再走一次({_houseNavTries}/3)", 255, 220, 120);
					RecedingNav.Start(scol, HouseBuilder.LadderFootRow(hx, hy));
				}
				else
				{
					_pendingHouse = null; _houseNavTries = 0;
					if (gapX > 6)
						Main.NewText($"[TerraBlind] 走不到开工位({scol},{hy + 1}),还差 {gapX} 格", 255, 120, 120);
					else if (HouseBuilder.Start(4, 1, hx, hy, out string whyH))
						Main.NewText($"[TerraBlind] 到了,开工盖房 ({hx},{hy})", 120, 255, 120);
					else
						Main.NewText($"[TerraBlind] 开工失败:{whyH}", 255, 120, 120);
				}
			}
			var site = _site;
			if (site != null)
			{
				_site = null;
				const int HW = 21, HH = 10;
				if (site.Got)
				{
					int d = System.Math.Abs(site.Bx - site.Fx) + System.Math.Abs(site.By - site.Fy);
					Predicates.VisualizeBox(site.Bx, site.By, HW, HH, $"house {HW}x{HH}");
					Main.NewText($"[TerraBlind] 房址 左下角({site.Bx},{site.By}) 右上角({site.Bx + HW - 1},{site.By - HH + 1}) 离你{d}格", 120, 255, 120);
					// 走过去,到了自己开工。nav 直接送到【开工站位】而不是房址本身:
					// 房址那格是要放出来的,还不存在;站位是隔两列、下面一行的实地。
					_pendingHouse = (site.Bx, site.By);
					RecedingNav.Start(site.Bx, HouseBuilder.LadderFootRow(site.Bx, site.By));
				}
				else
				{
					int blocked = Predicates.VisualizeBox(site.Fx, site.Fy, HW, HH, "NO SITE (from here)");
					Main.NewText($"[TerraBlind] 附近没有 {HW}x{HH} 的空位(扫了{site.Scanned}格)。画的是你脚下这个框,红的{blocked}格挡着。", 255, 120, 120);
				}
			}
			if (TerraBlind.ToggleMazeNav != null && TerraBlind.ToggleMazeNav.JustPressed)
				MazeWand.ToggleNav();
			if (TerraBlind.ToggleRecedingNav != null && TerraBlind.ToggleRecedingNav.JustPressed)
				RecedingNav.Toggle();
			// H 找一次房址并画出来:绿=空,红=被占。找不到就画脚下那个框,直接看出被什么挡的。
			if (TerraBlind.ShowHouseSite != null && TerraBlind.ShowHouseSite.JustPressed)
			{
				var hp = Main.LocalPlayer;
				int fx = ActExecutor.OriginCx(hp), fy = ActExecutor.OriginCy(hp);
				const int HW = 21, HH = 10;
				// 扫最坏是 200×60×4 个候选,每个再验 210 格 —— 放主线程上就是一次可见的卡顿。
				// 纯读 tile,丢后台;画和走留到结果回来那一帧(SiteReady 在下面消费)。
				System.Threading.Tasks.Task.Run(() =>
				{
					try
					{
						bool g = Predicates.ScanHouse(fx, fy, HW, HH, 200, out int bx, out int by, out int sc);
						_site = new SiteResult { Got = g, Bx = bx, By = by, Scanned = sc, Fx = fx, Fy = fy };
					}
					catch (System.Exception e) { DiagLog.Write($"[house-scan] EXC {e.Message}"); }
				});
			}
			// B 测试铺路:朝面朝的方向铺 30 格。再按一次停。
			if (TerraBlind.TestBridge != null && TerraBlind.TestBridge.JustPressed)
			{
				if (BridgeBuilder.IsRunning)
				{
					BridgeBuilder.Stop();
					Main.NewText($"[TerraBlind] 铺路停止,已铺 {BridgeBuilder.Placed}", 255, 200, 120);
				}
				else
				{
					var bp = Main.LocalPlayer;
					string bdir = bp.direction >= 0 ? "right" : "left";
					_bridgeStartTick = (int)Main.GameUpdateCount;
					if (BridgeBuilder.Start(BridgeTestItem(bp), bdir, 30, out string bwhy))
						Main.NewText($"[TerraBlind] 铺路 {bdir} 30 格…", 120, 255, 120);
					else
					{ _bridgeStartTick = 0; Main.NewText($"[TerraBlind] 铺不了: {bwhy}", 255, 120, 120); }
				}
			}
			// 铺完报一次用时 —— "边走边放"到底快多少,就看这个数。
			if (_bridgeStartTick > 0 && !BridgeBuilder.IsRunning)
			{
				int el = (int)Main.GameUpdateCount - _bridgeStartTick;
				_bridgeStartTick = 0;
				Main.NewText($"[TerraBlind] 铺了 {BridgeBuilder.Placed} 格,{el} 帧 ({el / 60f:0.0}s, {BridgeBuilder.Placed * 60f / System.Math.Max(1, el):0.00} 格/秒) {BridgeBuilder.Outcome}", 200, 220, 255);
				DiagLog.Write($"[bridge-test] placed={BridgeBuilder.Placed} frames={el} rate={BridgeBuilder.Placed * 60f / System.Math.Max(1, el):0.00}/s outcome={BridgeBuilder.Outcome}");
			}

			// N 测试单间:在脚下朝面朝方向盖 6 宽的单间。
			if (TerraBlind.TestRoom != null && TerraBlind.TestRoom.JustPressed)
			{
				if (HouseBuilder.IsRunning)
				{
					HouseBuilder.Stop();
					Main.NewText("[TerraBlind] 盖房已停", 255, 200, 120);
				}
				else if (HouseBuilder.StartHere(1, Main.LocalPlayer.direction, out string rwhy))
					Main.NewText("[TerraBlind] 盖单间…", 120, 255, 120);
				else
					Main.NewText($"[TerraBlind] 盖不了: {rwhy}", 255, 120, 120);
			}
			// 场把往上那一列算成多少钱:H 说该挖上去、实际要挖 8 格,得看每格的账
			if (TerraBlind.TestPillar != null && TerraBlind.TestPillar.JustPressed
				&& Main.keyState.PressingShift())
			{
				var cp = Main.LocalPlayer;
				int ccx = (int)((cp.position.X + cp.width / 2f) / 16f);
				int ccy = (int)((cp.position.Y + cp.height - 2f) / 16f);
				var sb = new System.Text.StringBuilder($"[costdump] 从 ({ccx},{ccy}) 往上\n");
				for (int r = 1; r <= 10; r++)
				{
					int from = ccy - r + 1, to = ccy - r;
					sb.Append($"  ({ccx},{from})→({ccx},{to}) cost={MazeWand.StepCostPublic(ccx, to, ccx, from)}"
						+ $" solid={Predicates.IsSolid(ccx, to)}\n");
				}
				sb.Append($"  横向 ({ccx},{ccy})→西 cost={MazeWand.StepCostPublic(ccx - 1, ccy, ccx, ccy)}"
					+ $" 东 cost={MazeWand.StepCostPublic(ccx + 1, ccy, ccx, ccy)}");
				DiagLog.Write(sb.ToString());
				Main.NewText("[TerraBlind] 代价已打进日志", 120, 255, 120);
			}
			// P 单测 pillar:原地往上搭 10 格,人跟着爬上去。再按一次停。
			else if (TerraBlind.TestPillar != null && TerraBlind.TestPillar.JustPressed)
			{
				if (SkillExecutor.IsActive) { SkillExecutor.Stop(); Main.NewText("[TerraBlind] pillar 停", 255, 200, 120); }
				else
				{
					var pp = Main.LocalPlayer;
					int feet = (int)((pp.position.Y + pp.height) / 16f);
					int tgt = feet - 10;
					_pillarTestFrom = feet; _pillarTestTarget = tgt;
					SkillExecutor.StartPillarJump(pp.direction >= 0, tgt);
					Main.NewText($"[TerraBlind] pillar: 脚 {feet} → {tgt}(10格)", 120, 255, 120);
				}
			}
			if (_pillarTestFrom != 0 && !SkillExecutor.IsActive)
			{
				var pp = Main.LocalPlayer;
				int feet = (int)((pp.position.Y + pp.height) / 16f);
				int got = _pillarTestFrom - feet;
				bool ok = feet <= _pillarTestTarget;
				Main.NewText($"[TerraBlind] pillar 结束:升了 {got}/10 格,脚在 {feet}(要 {_pillarTestTarget}) {(ok ? "OK" : "没到")}",
					ok ? (byte)120 : (byte)255, ok ? (byte)255 : (byte)120, 120);
				DiagLog.Write($"[pillar-test] rose={got}/10 feet={feet} target={_pillarTestTarget} ok={ok}");
				_pillarTestFrom = 0;
			}
			// U toggles build RECORDING: capture place/mine intents (build_rec.json) while you build by hand.
			if (TerraBlind.ToggleBuildRecord != null && TerraBlind.ToggleBuildRecord.JustPressed)
			{
				if (BuildRecorder.IsRecording)
				{
					BuildRecorder.Stop();
					Main.NewText($"[TerraBlind] 停止建造录制（{BuildRecorder.LastEventCount} 事件）→ build_rec.json", 255, 120, 120);
				}
				else
				{
					BuildRecorder.Start();
					Main.NewText("[TerraBlind] 开始建造录制（放置/挖掘意图）", 120, 255, 120);
				}
			}
			// I toggles build REPLAY: start at the player's feet (anchor -1,-1) if idle, stop if running.
			if (TerraBlind.ToggleBuildReplay != null && TerraBlind.ToggleBuildReplay.JustPressed)
			{
				if (BuildReplayer.Running)
				{
					BuildReplayer.Stop();
					Main.NewText("[TerraBlind] 建造回放已停止");
				}
				else if (BuildReplayer.Start(-1, -1, out string why))
					Main.NewText("[TerraBlind] 开始回放建造（锚点=脚下）");
				else
					Main.NewText($"[TerraBlind] 无法回放：{why}");
			}
		}

		public override void SetControls()
		{
			if (Player != Main.LocalPlayer) return;

			if (JumpPlaceEnabled)
			{
				bool atPeak = _prevVy < 0f && Player.velocity.Y >= 0f;
				if (atPeak && !_jumpPlaceFired)
				{
					_jumpPlaceFired = true;
					int slot = -1;
					for (int i = 0; i < 10; i++)
					{
						var it = Player.inventory[i];
						if (it != null && !it.IsAir && it.createTile >= 0 && Terraria.ID.TileID.Sets.Platforms[it.createTile])
						{ slot = i; break; }
					}
					if (slot >= 0)
					{
						Player.selectedItem = slot;
						Player.controlUseItem = true;
						// place at feet+1 tile so player lands on it
						int tileX = (int)((Player.position.X + Player.width / 2f) / 16f);
						int tileY = (int)((Player.position.Y + Player.height) / 16f) + 1;
						Main.mouseX = (int)(tileX * 16f + 8f - Main.screenPosition.X);
						Main.mouseY = (int)(tileY * 16f + 8f - Main.screenPosition.Y);
						PathVisSystem.SetTiles(new System.Collections.Generic.List<(int, int, Microsoft.Xna.Framework.Color)>
						{
							(tileX, tileY, new Microsoft.Xna.Framework.Color(255, 220, 0, 200))
						}, ttlFrames: 180);
						Main.SmartCursorWanted_Mouse = false;
						DiagLog.Write($"[jump_place] fired at peak vy_prev={_prevVy:0.##} vy={Player.velocity.Y:0.##} slot={slot} tile=({tileX},{tileY}) itemTime={Player.itemTime} itemAnimation={Player.itemAnimation}");
					}
				}
				if (Player.velocity.Y == 0f && _jumpPlaceFired)
				{
					JumpPlaceEnabled = false;
					_jumpPlaceFired = false;
				}
			}
			_prevVy = Player.velocity.Y;

			// semantic place: drives its cell queue through ItemUseCoordinator. Ticked before the coordinator block
			// below so a cell it starts this frame gets swung at immediately.
			PlaceAction.Tick();

			// rope ladder: alternates placing (via ItemUseCoordinator) and climbing (writes controlUp itself). Its
			// climb phase owns the controls, so it returns before anything else can fight it for them.
			if (RopeLadder.IsRunning)
			{
				RopeLadder.Tick();
				if (ItemUseCoordinator.IsActive) ItemUseCoordinator.ApplyControls();
				RecordSystem.CaptureFrame(Player);
				return;
			}

			// pillar: builds a platform column to the player's right, driving jump + useItem + cursor itself.
			if (PillarUp.IsRunning)
			{
				PillarUp.Tick();
				RecordSystem.CaptureFrame(Player);
				return;
			}

			// place-walls: places background walls at an ordered cell list, jumping for the ones too high.
			if (PlaceWalls.IsRunning)
			{
				PlaceWalls.Tick();
				RecordSystem.CaptureFrame(Player);
				return;
			}

			// walk-place: walks to a column, dropping furniture at in-reach targets along the way.
			if (WalkPlace.IsRunning)
			{
				WalkPlace.Tick();
				RecordSystem.CaptureFrame(Player);
				return;
			}

			// drop: falls through the platform underfoot to the solid ground below (roof → base).
			if (DropDown.IsRunning)
			{
				DropDown.Tick();
				RecordSystem.CaptureFrame(Player);
				return;
			}

			// settle: brakes to a stop on a target column, writing movement keys itself.
			if (SettleAt.IsRunning)
			{
				SettleAt.Tick();
				RecordSystem.CaptureFrame(Player);
				return;
			}

			// hop-up: writes controlJump itself until the player is standing on the target row.
			if (HopUp.IsRunning)
			{
				HopUp.Tick();
				RecordSystem.CaptureFrame(Player);
				return;
			}

			// house: pure orchestration over the other primitives. Ticked BEFORE them so a step it starts
			// this frame is driven immediately; it writes no controls itself.
			if (HouseBuilder.IsRunning) HouseBuilder.Tick();

			// bridge: same deal — its walk phase writes the movement keys itself, so it owns the frame while running.
			if (BridgeBuilder.IsRunning)
			{
				BridgeBuilder.Tick();
				if (ItemUseCoordinator.IsActive) ItemUseCoordinator.ApplyControls();
				RecordSystem.CaptureFrame(Player);
				return;
			}

			// /act takes the wheel outright: it is the raw action primitive the LLM drives by hand, so nothing else may
			// write controls underneath it. First in, and it returns — nav/mine/place all stand down while it runs.
			if (ActExecutor.IsActive)
			{
				ActExecutor.ApplyControls();
				RecordSystem.CaptureFrame(Player);
				BuildRecorder.Tick(Player);
				return;
			}

			// direction-explore drives StateSpacePlanner leg-by-leg; run it before TickBlocks so a freshly dispatched
			// leg gets stepped this frame.
			ExploreCoordinator.ApplyControls();

			// 必须在 RecedingNav.Tick 之前:它这一帧起的 nav 要靠同帧的 Tick 驱动
			BuildReplayer.Tick();

			RecedingNav.Tick();   // receding-horizon (K): plan next short window from real pos, dispatch; below drives it
			StateSpacePlanner.TickBlocks();
			StateSpacePlanner.BlockNavTick();   // block-nav driver: advance to next chunk when current single-point leg finishes

			if (StateSpacePlanner.IsActive)
			{
				StateSpacePlanner.ApplyControls();
				return;
			}
			if (NavCoordinator.IsActive)
			{
				NavCoordinator.ApplyControls();
				ReplaySystem.ApplyControls();
				PlaceCoordinator.ApplyControls();
				JumpCoordinator.ApplyControls();
				SkillExecutor.ApplyControls();
				return;
			}
			if (MineCoordinator.IsActive)
			{
				MineCoordinator.ApplyControls();
				return;
			}
			if (ItemUseCoordinator.IsActive)
			{
				ItemUseCoordinator.ApplyControls();
				return;
			}
			if (SkillExecutor.IsActive)
			{
				SkillExecutor.ApplyControls();
				ReplaySystem.ApplyControls();
				PlaceCoordinator.ApplyControls();
				return;
			}
			if (ReplaySystem.IsActive)
			{
				ReplaySystem.ApplyControls();
				return;
			}
			if (JumpCoordinator.IsActive)
			{
				JumpCoordinator.ApplyControls();
				return;
			}
			if (WalkCoordinator.IsActive)
			{
				WalkCoordinator.ApplyControls();
				RecordSystem.CaptureFrame(Player);
				BuildRecorder.Tick(Player);
				return;
			}
			bool placeActive = PlaceCoordinator.IsActive;
			PlaceCoordinator.ApplyControls();
			var ci = HttpServerSystem.PendingControl;

			int jflBefore = _jumpFramesLeft;
			bool ciJumpIn = ci != null && ci.Jump;
			bool ciLeftIn = ci != null && ci.Left;
			bool ciRightIn = ci != null && ci.Right;
			bool ciDownIn = ci != null && ci.Down;
			long ciAge = ci != null ? (long)Main.GameUpdateCount - ci.Tick : -1;

			if (_jumpFramesLeft > 0)
			{
				Player.controlJump = true;
				_jumpFramesLeft--;
				if (_jumpFramesLeft == 0) _jumpFramesLeft = -1;
			}
			else if (_jumpFramesLeft == -1)
			{
				_jumpFramesLeft = 0;
			}

			bool jumpFromAuto = false, jumpFromCi = false;
			bool walking = false, blocked = false;

			FightCoordinator.Tick(Player);
			if (WalkTraceEnabled && (System.Math.Abs(Player.velocity.X) > 0.02f || System.Math.Abs(Player.velocity.Y) > 0.02f))
				DiagLog.Write($"[jump-trace] py={Player.position.Y:0.##} vy={Player.velocity.Y:0.###} jump={Player.jump} feetCy={(int)((Player.position.Y + Player.height) / 16f)} wet={Player.wet} jumpH={Player.jumpHeight}");
			if (ci == null)
			{
				if (placeActive || jflBefore != 0)
					DiagLog.JumpTrace($"jfl={jflBefore}->{_jumpFramesLeft} ci=null place={placeActive} cJ={Player.controlJump}");
				bool jumpActive = jflBefore > 0 || Player.controlJump;
				RecordSystem.CaptureFrame(Player, jumpActive);
				BuildRecorder.Tick(Player);
				return;
			}
			if (ciAge > ControlTimeoutTicks)
			{
				DiagLog.JumpTrace($"jfl={jflBefore}->{_jumpFramesLeft} ci=EXPIRED age={ciAge} place={placeActive}");
				HttpServerSystem.PendingControl = null;
				RecordSystem.CaptureFrame(Player, jflBefore > 0);
				BuildRecorder.Tick(Player);
				return;
			}
			if (ci.Left) Player.controlLeft = true;
			if (ci.Right) Player.controlRight = true;
			if (ci.Up) Player.controlUp = true;
			if (ci.Down) Player.controlDown = true;
			walking = (ci.Left || ci.Right) && !ci.Down;
			bool onGround = Player.velocity.Y == 0f;
			blocked = onGround && System.Math.Abs(Player.velocity.X) < 0.1f;
			if (_autoJumpCooldown > 0) _autoJumpCooldown--;
			if (walking && blocked && _jumpFramesLeft == 0 && _autoJumpCooldown == 0)
			{
				_jumpFramesLeft = JumpHoldFrames;
				Player.controlJump = true;
				jumpFromAuto = true;
			}
			if (ci.Jump && _jumpFramesLeft == 0)
			{
				_jumpFramesLeft = JumpHoldFrames;
				Player.controlJump = true;
				ci.Jump = false;
				jumpFromCi = true;
			}
			if (ci.UseItem) Player.controlUseItem = true;
			if (ci.UseTile) Player.controlUseTile = true;
			if (ci.SmartCursor >= 0) Main.SmartCursorWanted_Mouse = ci.SmartCursor == 1;
			if (ci.SelectedSlot >= 0 && ci.SelectedSlot <= 9)
				Player.selectedItem = ci.SelectedSlot;
			if (!float.IsNaN(ci.Mx) && !float.IsNaN(ci.My))
			{
				Main.mouseX = (int)(Player.position.X + Player.width / 2f + ci.Mx * 16f - Main.screenPosition.X);
				Main.mouseY = (int)(Player.position.Y + Player.height / 2f + ci.My * 16f - Main.screenPosition.Y);
			}

			if (placeActive || ciJumpIn || jumpFromAuto || jumpFromCi || jflBefore != 0)
			{
				DiagLog.JumpTrace(
					$"jfl={jflBefore}->{_jumpFramesLeft} ciJ={ciJumpIn} ciL={ciLeftIn} ciR={ciRightIn} ciD={ciDownIn} " +
					$"age={ciAge} walk={walking} blk={blocked} vy={Player.velocity.Y:F2} vx={Player.velocity.X:F2} " +
					$"place={placeActive} autoJ={jumpFromAuto} ciJfire={jumpFromCi} cJ={Player.controlJump} cUI={Player.controlUseItem}"
				);
			}
			RecordSystem.CaptureFrame(Player, jflBefore > 0 || jumpFromCi);
			BuildRecorder.Tick(Player);
		}

		public override void PostUpdate()
		{
			if (Player != Main.LocalPlayer) return;
			// speed fields are baked (×moveSpeed) LATE in Player.Update — only here are they trustworthy for planning
			PhysicsSimulator.CaptureBaked(Player);
			PlatformStock.Tick();

			var snap = new Snapshot
			{
				Tick = (long)Main.GameUpdateCount,
				Player = new PlayerSnapshot
				{
					Hp = Player.statLife,
					MaxHp = Player.statLifeMax2,
					Mana = Player.statMana,
					MaxMana = Player.statManaMax2,
					PosX = Player.position.X,
					PosY = Player.position.Y,
					VelX = Player.velocity.X,
					VelY = Player.velocity.Y,
					Width = Player.width,
					Height = Player.height,
					Direction = Player.direction >= 0 ? "right" : "left",
					OnGround = Player.velocity.Y == 0f,
					InLiquid = Player.wet,
					Biome = DetectBiome(),
					Defense = Player.statDefense,
					MinionSlots = (int)Player.slotsMinions,
					MaxMinionSlots = Player.maxMinions,
					Coins = (int)System.Math.Min(int.MaxValue, Terraria.Utils.CoinsCount(out _, Player.inventory)),
				},
				World = BuildWorld(),
				Equipment = BuildEquipment(),
				Camera = new CameraSnapshot
				{
					ScreenPosX = Main.screenPosition.X,
					ScreenPosY = Main.screenPosition.Y,
					ScreenWidth = Main.screenWidth,
					ScreenHeight = Main.screenHeight,
					Zoom = Main.GameZoomTarget,
				},
				WalkToEdgeDone = WalkCoordinator.Done,
				Movement = BuildMovement(),
				Buffs = BuildBuffs(),
				Enemies = BuildEnemies(),
				TownNpcs = BuildTownNpcs(),
				Tiles = BuildTiles(),
				Objects = BuildObjects(),
				DroppedItems = BuildDroppedItems(),
				NearbyStations = BuildNearbyStations(),
				AvailableRecipes = BuildAvailableRecipes(),
				DetectedTiles = BuildDetectedTiles(),
			};

			HttpServerSystem.LatestSnapshot = snap;
			PhysicsRecorder.Capture(Player, Player.controlJump);
		}

		private WorldSnapshot BuildWorld()
		{
			// Terraria clock: Main.time counts ticks into the current segment. Day starts at 4:30am and lasts 54000
			// ticks; night starts at 7:30pm and lasts 32400. Convert to a 24h wall clock the way the in-game clock does.
			double t = Main.time;
			double hours;
			if (Main.dayTime) hours = t / 3600.0 + 4.5;          // day segment → 4:30 .. 19:30
			else hours = t / 3600.0 + 19.5;                      // night segment → 19:30 .. 4:30(+24)
			hours %= 24.0;
			int hh = (int)hours;
			int mm = (int)((hours - hh) * 60);

			string evt = "";
			if (Main.invasionType > 0) evt = Main.invasionType switch {
				1 => "goblin_army", 2 => "frost_legion", 3 => "pirates", 4 => "martians", _ => "invasion" };
			else if (Main.eclipse) evt = "eclipse";
			else if (Main.bloodMoon) evt = "blood_moon";

			return new WorldSnapshot
			{
				DayTime = Main.dayTime,
				Time = Main.time,
				Clock = $"{hh:D2}:{mm:D2}",
				Raining = Main.raining,
				RainIntensity = Main.raining ? Main.maxRaining : 0f,
				Sandstorm = Player.ZoneSandstorm,
				Hardmode = Main.hardMode,
				BloodMoon = Main.bloodMoon,
				Eclipse = Main.eclipse,
				DownedEyeOfCthulhu = NPC.downedBoss1,
				DownedEvilBoss = NPC.downedBoss2,
				DownedSkeletron = NPC.downedBoss3,
				DownedWallOfFlesh = Main.hardMode,   // hardmode is entered exactly by killing WoF
				ActiveEvent = evt,
			};
		}

		private string DetectBiome()
		{
			if (Player.ZoneJungle) return "jungle";
			if (Player.ZoneDungeon) return "dungeon";
			if (Player.ZoneCorrupt) return "corruption";
			if (Player.ZoneCrimson) return "crimson";
			if (Player.ZoneHallow) return "hallow";
			if (Player.ZoneSnow) return "snow";
			if (Player.ZoneDesert) return "desert";
			if (Player.ZoneBeach) return "ocean";
			if (Player.ZoneUnderworldHeight) return "underworld";
			if (Player.ZoneRockLayerHeight) return "cavern";
			if (Player.ZoneDirtLayerHeight) return "underground";
			if (Player.ZoneSkyHeight) return "sky";
			return "forest";
		}

		private EquipmentSnapshot BuildEquipment()
		{
			var eq = new EquipmentSnapshot
			{
				SelectedSlot = Player.selectedItem,
				HeldItem = ItemToSlot(Player.HeldItem),
				InventoryOpen = Main.playerInventory,
				ChestOpen = Player.chest != -1,
				SmartCursor = Main.SmartCursorWanted,
			};
			for (int i = 0; i < 10; i++)
			{
				eq.Hotbar[i] = ItemToSlot(Player.inventory[i]);
			}
			for (int i = 0; i < 40; i++)
			{
				eq.Inventory[i] = ItemToSlot(Player.inventory[i + 10]);
			}
			for (int i = 0; i < 4; i++)
			{
				eq.Coins[i] = ItemToSlot(Player.inventory[i + 50]);
			}
			for (int i = 0; i < 4; i++)
			{
				eq.Ammo[i] = ItemToSlot(Player.inventory[i + 54]);
			}
			return eq;
		}

		// 测试用:背包里挑个能铺的 —— 平台优先,没有就拿存量最多的方块。
		private static string BridgeTestItem(Player p)
		{
			var td = Terraria.ID.TileID.Sets.Platforms;
			for (int i = 0; i < p.inventory.Length; i++)
			{
				var it = p.inventory[i];
				if (it == null || it.IsAir || it.createTile < 0) continue;
				if (td != null && it.createTile < td.Length && td[it.createTile]) return it.Name;
			}
			string best = "木平台"; int bestStack = 0;
			for (int i = 0; i < p.inventory.Length; i++)
			{
				var it = p.inventory[i];
				if (it == null || it.IsAir || it.createTile < 0) continue;
				if (it.stack > bestStack) { bestStack = it.stack; best = it.Name; }
			}
			return best;
		}

		private static HotbarSlot ItemToSlot(Item item)
		{
			if (item == null || item.IsAir)
			{
				return new HotbarSlot { Id = 0, Name = "", Stack = 0 };
			}
			return new HotbarSlot
			{
				Id = item.type,
				Name = item.Name ?? "",
				Stack = item.stack,
				Damage = item.damage,
				Pick = item.pick,
				Axe = item.axe,
				Hammer = item.hammer,
				CreateTile = item.createTile,
				Consumable = item.consumable,
				Category = ClassifyItem(item),
			};
		}

		private static string ClassifyItem(Item item)
		{
			if (item.pick > 0) return "pickaxe";
			if (item.axe > 0) return "axe";
			if (item.hammer > 0) return "hammer";
			if (item.createTile >= 0)
			{
				if (TileID.Sets.Platforms[item.createTile]) return "platform";
				if (TileID.Sets.Torch[item.createTile]) return "torch";
				return "block";
			}
			if (item.createWall >= 0) return "wall";
			if (item.ammo != AmmoID.None) return "ammo";
			if (item.damage > 0) return "weapon";
			if (item.consumable) return "consumable";
			return "misc";
		}

		private const float EnemyHalfWidthTiles = 60f;
		private const float EnemyHalfHeightTiles = 36f;
		private const float TileSize = 16f;

		private EnemyEntry[] BuildEnemies()
		{
			var list = new System.Collections.Generic.List<EnemyEntry>();
			float pcx = Player.position.X + Player.width / 2f;
			float pcy = Player.position.Y + Player.height / 2f;
			float halfW = EnemyHalfWidthTiles * TileSize;
			float halfH = EnemyHalfHeightTiles * TileSize;
			for (int i = 0; i < Main.maxNPCs; i++)
			{
				NPC npc = Main.npc[i];
				if (npc == null || !npc.active) continue;
				if (npc.townNPC || npc.friendly) continue;
				if (npc.lifeMax <= 5 && npc.damage == 0) continue;
				float ncx = npc.position.X + npc.width / 2f;
				float ncy = npc.position.Y + npc.height / 2f;
				if (System.Math.Abs(ncx - pcx) > halfW) continue;
				if (System.Math.Abs(ncy - pcy) > halfH) continue;
				list.Add(new EnemyEntry
				{
					WhoAmI = npc.whoAmI,
					Type = npc.type,
					Name = npc.TypeName ?? "",
					PosX = npc.position.X,
					PosY = npc.position.Y,
					VelX = npc.velocity.X,
					VelY = npc.velocity.Y,
					Width = npc.width,
					Height = npc.height,
					Hp = npc.life,
					MaxHp = npc.lifeMax,
					Boss = npc.boss,
					ScreenX = (ncx - Main.screenPosition.X) * Main.GameZoomTarget,
					ScreenY = (ncy - Main.screenPosition.Y) * Main.GameZoomTarget,
				});
			}
			return list.ToArray();
		}

		private TownNpcEntry[] BuildTownNpcs()
		{
			var list = new System.Collections.Generic.List<TownNpcEntry>();
			for (int i = 0; i < Main.maxNPCs; i++)
			{
				NPC npc = Main.npc[i];
				if (npc == null || !npc.active) continue;
				if (!npc.townNPC) continue;
				list.Add(new TownNpcEntry
				{
					WhoAmI = npc.whoAmI,
					Type = npc.type,
					Name = npc.GivenOrTypeName ?? "",
					DisplayName = npc.TypeName ?? "",
					PosX = npc.position.X,
					PosY = npc.position.Y,
					Homeless = npc.homeless,
				});
			}
			return list.ToArray();
		}

		private const int TileWindowWidth = 120;
		private const int TileWindowHeight = 70;

		private TileWindowSnapshot BuildTiles()
		{
			int pcx = (int)((Player.position.X + Player.width / 2f) / 16f);
			int pcy = (int)((Player.position.Y + Player.height / 2f) / 16f);
			int ox = pcx - TileWindowWidth / 2;
			int oy = pcy - TileWindowHeight / 2;

			var rows = new TileRun[TileWindowHeight][];
			var runBuf = new System.Collections.Generic.List<TileRun>(32);

			for (int ry = 0; ry < TileWindowHeight; ry++)
			{
				runBuf.Clear();
				int wy = oy + ry;
				TileRun cur = default;
				bool has = false;
				for (int rx = 0; rx < TileWindowWidth; rx++)
				{
					int wx = ox + rx;
					ushort type = 0;
					byte sflags = 0;
					if (wx >= 0 && wx < Main.maxTilesX && wy >= 0 && wy < Main.maxTilesY)
					{
						Tile t = Main.tile[wx, wy];
						if (t.HasTile)
						{
							type = t.TileType;
							sflags |= 1;
							if (Main.tileSolid[type]) sflags |= 2;
								if (Main.tileSolidTop[type]) sflags |= 64;
							// 128 = slope or half-brick (non-full collision shape); not visible otherwise
							if ((int)t.Slope != 0 || t.IsHalfBlock) sflags |= 128;
						}
						if (t.LiquidAmount > 0)
						{
							if (t.LiquidType == LiquidID.Water) sflags |= 4;
							else if (t.LiquidType == LiquidID.Lava) sflags |= 8;
							else if (t.LiquidType == LiquidID.Honey) sflags |= 16;
							else if (t.LiquidType == LiquidID.Shimmer) sflags |= 32;
						}
					}
					if (!has)
					{
						cur = new TileRun { Type = type, SFlags = sflags, Count = 1 };
						has = true;
					}
					else if (cur.Type == type && cur.SFlags == sflags && cur.Count < ushort.MaxValue)
					{
						cur.Count++;
					}
					else
					{
						runBuf.Add(cur);
						cur = new TileRun { Type = type, SFlags = sflags, Count = 1 };
					}
				}
				if (has) runBuf.Add(cur);
				rows[ry] = runBuf.ToArray();
			}

			return new TileWindowSnapshot
			{
				OriginTileX = ox,
				OriginTileY = oy,
				Width = TileWindowWidth,
				Height = TileWindowHeight,
				Rows = rows,
			};
		}

		private WorldObjectEntry[] BuildObjects()
		{
			var list = new System.Collections.Generic.List<WorldObjectEntry>();
			int pcx = (int)((Player.position.X + Player.width / 2f) / 16f);
			int pcy = (int)((Player.position.Y + Player.height / 2f) / 16f);
			int ox = pcx - TileWindowWidth / 2;
			int oy = pcy - TileWindowHeight / 2;
			int ex = ox + TileWindowWidth;
			int ey = oy + TileWindowHeight;

			var addedTreeX = new System.Collections.Generic.HashSet<int>();
			for (int wy = oy; wy < ey; wy++)
			{
				if (wy < 0 || wy >= Main.maxTilesY) continue;
				for (int wx = ox; wx < ex; wx++)
				{
					if (wx < 0 || wx >= Main.maxTilesX) continue;
					Tile t = Main.tile[wx, wy];
					if (!t.HasTile) continue;
					ushort type = t.TileType;
					if (TileID.Sets.IsATreeTrunk[type])
					{
						if (addedTreeX.Contains(wx)) continue;
						bool isTop = wy - 1 < 0;
						if (!isTop) { Tile above = Main.tile[wx, wy - 1]; isTop = !above.HasTile || !TileID.Sets.IsATreeTrunk[above.TileType]; }
						if (!isTop) continue;
						addedTreeX.Add(wx);
						int objHeight = 0;
						for (int dy = 0; dy < 60; dy++)
						{
							int sy = wy + dy;
							if (sy < 0 || sy >= Main.maxTilesY) break;
							Tile st = Main.tile[wx, sy];
							if (st.HasTile && TileID.Sets.IsATreeTrunk[st.TileType]) objHeight++;
							else break;
						}
						list.Add(new WorldObjectEntry
						{
							TileX = wx,
							TileY = wy,
							Type = type,
							Name = "tree",
							PosX = wx * 16f,
							PosY = wy * 16f,
							Height = objHeight,
						});
						continue;
					}
					if (t.TileFrameX != 0 || t.TileFrameY != 0) continue;
					string cat = ClassifyTile(type);
					if (cat == null) continue;
					list.Add(new WorldObjectEntry
					{
						TileX = wx,
						TileY = wy,
						Type = type,
						Name = cat,
						PosX = wx * 16f,
						PosY = wy * 16f,
					});
				}
			}
			return list.ToArray();
		}

		private static string ClassifyTile(ushort type)
		{
			if (TileID.Sets.BasicChest[type]) return "chest";
			if (TileID.Sets.BasicDresser[type]) return "dresser";
			if (TileID.Sets.IsATreeTrunk[type]) return "tree";
			if (TileID.Sets.Torch[type]) return "torch";
			if (TileID.Sets.Platforms[type]) return null;
			switch (type)
			{
				case TileID.Containers2: return "chest";
				case TileID.WorkBenches: return "workbench";
				case TileID.Anvils: return "anvil";
				case TileID.MythrilAnvil: return "anvil";
				case TileID.Furnaces: return "furnace";
				case TileID.Hellforge: return "furnace";
				case TileID.AdamantiteForge: return "furnace";
				case TileID.Pots: return "pot";
				case TileID.Signs: return "sign";
				case TileID.Beds: return "bed";
				case TileID.Bottles: return "alchemy";
				case TileID.AlchemyTable: return "alchemy";
				case TileID.CookingPots: return "cooking_pot";
				case TileID.Sawmill: return "sawmill";
				case TileID.TinkerersWorkbench: return "tinkerer";
				case TileID.DemonAltar: return "altar";
				case TileID.Loom: return "loom";
				case TileID.Solidifier: return "solidifier";
				case TileID.HeavyWorkBench: return "workbench";
			}
			return null;
		}

		private DroppedItemEntry[] BuildDroppedItems()
		{
			var list = new System.Collections.Generic.List<DroppedItemEntry>();
			float pcx = Player.position.X + Player.width / 2f;
			float pcy = Player.position.Y + Player.height / 2f;
			float halfW = TileWindowWidth / 2f * 16f;
			float halfH = TileWindowHeight / 2f * 16f;
			for (int i = 0; i < Main.maxItems; i++)
			{
				Item item = Main.item[i];
				if (item == null || !item.active || item.IsAir) continue;
				float ix = item.position.X + item.width / 2f;
				float iy = item.position.Y + item.height / 2f;
				if (System.Math.Abs(ix - pcx) > halfW) continue;
				if (System.Math.Abs(iy - pcy) > halfH) continue;
				list.Add(new DroppedItemEntry
				{
					WhoAmI = i,
					Type = item.type,
					Name = item.Name ?? "",
					Stack = item.stack,
					PosX = item.position.X,
					PosY = item.position.Y,
				});
			}
			return list.ToArray();
		}

		private MovementSnapshot BuildMovement()
		{
			int extraJumps = 0;
			foreach (var jh in Player.ExtraJumps)
			{
				if (jh.Enabled) extraJumps++;
			}
			return new MovementSnapshot
			{
				JumpSpeed = Player.jumpSpeed,
				JumpHeight = Player.jumpHeight,
				Gravity = Player.gravity,
				MaxRunSpeed = Player.maxRunSpeed,
				AccRunSpeed = Player.accRunSpeed,
				WingTimeMax = Player.wingTimeMax,
				NoFallDmg = Player.noFallDmg,
				LavaImmune = Player.lavaImmune,
				LavaTime = Player.lavaMax,
				ExtraJumps = extraJumps,
			};
		}

		private static readonly System.Collections.Generic.Dictionary<int, string> _watchTiles = new System.Collections.Generic.Dictionary<int, string>
		{
			{ 396, "sandstone" },
			{ 397, "hardened_sand" },
		};
		private const int _watchWall = 220;

		private DetectedTileEntry[] BuildDetectedTiles()
		{
			int pcx = (int)((Player.position.X + Player.width / 2f) / 16f);
			int pcy = (int)((Player.position.Y + Player.height / 2f) / 16f);
			int ox = pcx - TileWindowWidth / 2;
			int oy = pcy - TileWindowHeight / 2;
			var found = new System.Collections.Generic.Dictionary<string, DetectedTileEntry>();
			for (int ry = 0; ry < TileWindowHeight; ry++)
			{
				int wy = oy + ry;
				if (wy < 0 || wy >= Main.maxTilesY) continue;
				for (int rx = 0; rx < TileWindowWidth; rx++)
				{
					int wx = ox + rx;
					if (wx < 0 || wx >= Main.maxTilesX) continue;
					Tile t = Main.tile[wx, wy];
					string name = null;
					if (t.HasTile && _watchTiles.TryGetValue(t.TileType, out string tname))
						name = tname;
					else if (t.WallType == _watchWall)
						name = "sandstone_wall";
					if (name == null) continue;
					if (found.ContainsKey(name)) continue;
					found[name] = new DetectedTileEntry
					{
						Name = name,
						TileX = wx,
						TileY = wy,
						RelX = wx - pcx,
						RelY = wy - pcy,
					};
				}
			}
			var arr = new DetectedTileEntry[found.Count];
			found.Values.CopyTo(arr, 0);
			return arr;
		}

		private AvailableRecipeEntry[] BuildAvailableRecipes()
		{
			var list = new System.Collections.Generic.List<AvailableRecipeEntry>();
			for (int ri = 0; ri < Main.numAvailableRecipes; ri++)
			{
				var r = Main.recipe[Main.availableRecipe[ri]];
				var ings = new System.Collections.Generic.List<IngredientEntry>();
				foreach (var req in r.requiredItem)
				{
					if (req.IsAir) continue;
					ings.Add(new IngredientEntry { Name = req.Name ?? "", Count = req.stack });
				}
				list.Add(new AvailableRecipeEntry
				{
					ItemId = r.createItem.type,
					ItemName = r.createItem.Name ?? "",
					ResultStack = r.createItem.stack,
					Ingredients = ings.ToArray(),
				});
			}
			return list.ToArray();
		}

		private string[] BuildNearbyStations()
		{
			var found = new System.Collections.Generic.HashSet<string>();
			int pcx = (int)((Player.position.X + Player.width / 2f) / 16f);
			int pcy = (int)((Player.position.Y + Player.height / 2f) / 16f);
			int radius = 35;
			for (int wy = pcy - radius; wy <= pcy + radius; wy++)
			{
				if (wy < 0 || wy >= Main.maxTilesY) continue;
				for (int wx = pcx - radius; wx <= pcx + radius; wx++)
				{
					if (wx < 0 || wx >= Main.maxTilesX) continue;
					Tile t = Main.tile[wx, wy];
					if (!t.HasTile) continue;
					string cat = ClassifyTile(t.TileType);
					if (cat != null && cat != "chest" && cat != "pot" && cat != "sign" && cat != "bed" && cat != "torch" && cat != "dresser")
						found.Add(cat);
				}
			}
			var arr = new string[found.Count];
			found.CopyTo(arr);
			return arr;
		}

		private BuffEntry[] BuildBuffs()
		{
			var list = new System.Collections.Generic.List<BuffEntry>();
			for (int i = 0; i < Player.buffType.Length; i++)
			{
				int type = Player.buffType[i];
				if (type <= 0) continue;
				int frames = Player.buffTime[i];
				string name;
				try
				{
					name = Lang.GetBuffName(type) ?? "";
					if (string.IsNullOrEmpty(name) || name.StartsWith("Mods.") || name.Contains("BuffName."))
					{
						name = BuffID.Search.GetName(type) ?? ("Buff" + type);
					}
				}
				catch
				{
					name = "Buff" + type;
				}
				list.Add(new BuffEntry
				{
					Id = type,
					Name = name,
					TimeLeft = frames / 60f,
				});
			}
			return list.ToArray();
		}
	}
}
