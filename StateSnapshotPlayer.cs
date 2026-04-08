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
					Direction = Player.direction >= 0 ? "right" : "left",
					OnGround = Player.velocity.Y == 0f,
					InLiquid = Player.wet,
				},
				Equipment = BuildEquipment(),
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
			};
			for (int i = 0; i < 10; i++)
			{
				eq.Hotbar[i] = ItemToSlot(Player.inventory[i]);
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
				}
				catch
				{
					name = "";
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
