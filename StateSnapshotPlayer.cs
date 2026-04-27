using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraBlind
{
	public class StateSnapshotPlayer : ModPlayer
	{
		private const int ControlTimeoutTicks = 60;
		private const int JumpHoldFrames = 15;
		private int _jumpFramesLeft;

		public override void SetControls()
		{
			if (Player != Main.LocalPlayer) return;
			if (SkillExecutor.IsActive)
			{
				SkillExecutor.ApplyControls();
				PlaceCoordinator.ApplyControls();
				return;
			}
			if (ReplaySystem.IsActive)
			{
				ReplaySystem.ApplyControls();
				return;
			}
			if (WalkCoordinator.IsActive)
			{
				WalkCoordinator.ApplyControls();
				RecordSystem.CaptureFrame(Player);
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
			if (ci == null)
			{
				if (placeActive || jflBefore != 0)
					DiagLog.JumpTrace($"jfl={jflBefore}->{_jumpFramesLeft} ci=null place={placeActive} cJ={Player.controlJump}");
				bool jumpActive = jflBefore > 0 || Player.controlJump;
				RecordSystem.CaptureFrame(Player, jumpActive);
				return;
			}
			if (ciAge > ControlTimeoutTicks)
			{
				DiagLog.JumpTrace($"jfl={jflBefore}->{_jumpFramesLeft} ci=EXPIRED age={ciAge} place={placeActive}");
				HttpServerSystem.PendingControl = null;
				RecordSystem.CaptureFrame(Player, jflBefore > 0);
				return;
			}
			if (ci.Left) Player.controlLeft = true;
			if (ci.Right) Player.controlRight = true;
			if (ci.Up) Player.controlUp = true;
			if (ci.Down) Player.controlDown = true;
			walking = (ci.Left || ci.Right) && !ci.Down;
			bool onGround = Player.velocity.Y == 0f;
			blocked = onGround && System.Math.Abs(Player.velocity.X) < 0.1f;
			if (walking && blocked && _jumpFramesLeft == 0)
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
		}

		public override void PostUpdate()
		{
			if (Player != Main.LocalPlayer) return;

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
				},
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
			};

			HttpServerSystem.LatestSnapshot = snap;
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
		private const int TileWindowHeight = 80;

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

			for (int wy = oy; wy < ey; wy++)
			{
				if (wy < 0 || wy >= Main.maxTilesY) continue;
				for (int wx = ox; wx < ex; wx++)
				{
					if (wx < 0 || wx >= Main.maxTilesX) continue;
					Tile t = Main.tile[wx, wy];
					if (!t.HasTile) continue;
					if (t.TileFrameX != 0 || t.TileFrameY != 0) continue;
					ushort type = t.TileType;
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
