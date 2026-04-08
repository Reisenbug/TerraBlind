namespace TerraBlind
{
	public sealed class HotbarSlot
	{
		public int Id;
		public string Name = "";
		public int Stack;
	}

	public sealed class BuffEntry
	{
		public int Id;
		public string Name = "";
		public float TimeLeft;
	}

	public sealed class PlayerSnapshot
	{
		public int Hp;
		public int MaxHp;
		public int Mana;
		public int MaxMana;
		public float PosX;
		public float PosY;
		public float VelX;
		public float VelY;
		public string Direction = "right";
		public bool OnGround;
		public bool InLiquid;
	}

	public sealed class EquipmentSnapshot
	{
		public int SelectedSlot;
		public HotbarSlot HeldItem = new HotbarSlot();
		public HotbarSlot[] Hotbar = new HotbarSlot[10];
	}

	public sealed class Snapshot
	{
		public long Tick;
		public PlayerSnapshot Player = new PlayerSnapshot();
		public EquipmentSnapshot Equipment = new EquipmentSnapshot();
		public BuffEntry[] Buffs = new BuffEntry[0];
	}
}
