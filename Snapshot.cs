namespace TerraBlind
{
	public sealed class HotbarSlot
	{
		public int Id;
		public string Name = "";
		public int Stack;
		public int Damage;
		public int Pick;
		public int Axe;
		public int Hammer;
		public int CreateTile = -1;
		public bool Consumable;
	}

	public sealed class SlotPosition
	{
		public int X;
		public int Y;
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
		public float Width;
		public float Height;
		public string Direction = "right";
		public bool OnGround;
		public bool InLiquid;
	}

	public sealed class CameraSnapshot
	{
		public float ScreenPosX;
		public float ScreenPosY;
		public int ScreenWidth;
		public int ScreenHeight;
		public float Zoom;
	}

	public sealed class EquipmentSnapshot
	{
		public int SelectedSlot;
		public HotbarSlot HeldItem = new HotbarSlot();
		public HotbarSlot[] Hotbar = new HotbarSlot[10];
		public HotbarSlot[] Inventory = new HotbarSlot[40];
		public HotbarSlot[] Coins = new HotbarSlot[4];
		public HotbarSlot[] Ammo = new HotbarSlot[4];
		public SlotPosition[] SlotPositions = new SlotPosition[58];
		public bool InventoryOpen;
		public bool ChestOpen;
	}

	public sealed class EnemyEntry
	{
		public int WhoAmI;
		public int Type;
		public string Name = "";
		public float PosX;
		public float PosY;
		public float VelX;
		public float VelY;
		public float Width;
		public float Height;
		public int Hp;
		public int MaxHp;
		public bool Boss;
	}

	public sealed class TownNpcEntry
	{
		public int WhoAmI;
		public int Type;
		public string Name = "";
		public string DisplayName = "";
		public float PosX;
		public float PosY;
		public bool Homeless;
	}

	public sealed class Snapshot
	{
		public long Tick;
		public PlayerSnapshot Player = new PlayerSnapshot();
		public EquipmentSnapshot Equipment = new EquipmentSnapshot();
		public CameraSnapshot Camera = new CameraSnapshot();
		public BuffEntry[] Buffs = new BuffEntry[0];
		public EnemyEntry[] Enemies = new EnemyEntry[0];
		public TownNpcEntry[] TownNpcs = new TownNpcEntry[0];
	}
}
