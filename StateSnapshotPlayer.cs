using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraBlind
{
	public class StateSnapshotPlayer : ModPlayer
	{
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
				Buffs = BuildBuffs(),
				Enemies = BuildEnemies(),
				TownNpcs = BuildTownNpcs(),
			};

			HttpServerSystem.LatestSnapshot = snap;
		}

		private EquipmentSnapshot BuildEquipment()
		{
			var eq = new EquipmentSnapshot
			{
				SelectedSlot = Player.selectedItem,
				HeldItem = ItemToSlot(Player.HeldItem),
				InventoryOpen = Main.playerInventory,
				ChestOpen = Player.chest != -1,
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
			};
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
