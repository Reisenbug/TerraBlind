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
