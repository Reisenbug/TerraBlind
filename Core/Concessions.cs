using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraBlind
{
	// 对项目的让步:把游戏调简单一点,好让"能不能跑通全程"这件事不被物料和手臂长度淹没。
	//
	// 每一条都是【环境】的让步,不是给规划器开后门 -- 规划器照旧要自己找路、自己判够不够得着,
	// 只是够得着的范围大一点、料不会突然没了。这样失败还是真失败,只是少一批噪音。
	public class Concessions : ModPlayer
	{
		// 木头永远够。房子的地板/平台/家具/墙全从木头合出来,断在半路时留下的是
		// "椅子从4变3、walkplace 卡在物品34没了"这种查不动的现场
		public const int WoodKeep = 9999;

		// 伸手范围加成。【已归零】。全流程跑通之后回到原版距离,只剩捅向导那一段
		// 单独开 LongArm。留着这个常量是因为好几处判据的注释拿它举例(隔墙够得着但脚过不去),
		// 归零之后那些分支自然不再触发,但代码路径还在
		public const int ReachBoost = 0;

		// 捅向导那一段专用的【超长手臂】。ReachBoost 只加 blockRange,只有放置吃得到;
		// 挖和对话都以 static tileRangeX 为底,所以要够到向导脚下的每一列,只能动这个 static。
		// 【动了就必须还回去】。它是全局的,规划器的判据也读它,留着不还会让整个寻路以为
		// 手能伸 30 格。开关成对出现在 WofPrep:BackToGuide 开,Patch 收尾关
		public const int LongArm = 30;
		static int _savedRangeX = -1, _savedRangeY = -1;
		public static bool LongArmOn => _savedRangeX >= 0;
		public static void LongArmBegin()
		{
			if (_savedRangeX >= 0) return;   // 已经开着,别把加长后的值当原值存下来
			_savedRangeX = Player.tileRangeX; _savedRangeY = Player.tileRangeY;
			Player.tileRangeX = LongArm; Player.tileRangeY = LongArm;
			DiagLog.Write($"[reach] 手臂加长到{LongArm}格(原{_savedRangeX}/{_savedRangeY})");
		}
		// vanilla 每帧 ResetEffects 把 tileRangeX/Y 打回 5/4,所以加长期间要每帧重写
		public static void LongArmKeep()
		{
			if (_savedRangeX < 0) return;
			Player.tileRangeX = LongArm; Player.tileRangeY = LongArm;
		}

		public static void LongArmEnd()
		{
			if (_savedRangeX < 0) return;
			Player.tileRangeX = _savedRangeX; Player.tileRangeY = _savedRangeY;
			DiagLog.Write($"[reach] 手臂还原到{_savedRangeX}/{_savedRangeY}");
			_savedRangeX = _savedRangeY = -1;
		}

		// 放置/挖掘用时倍率。【已还原成 1】。全流程跑通了,回到原版速度。
		// 提速当初是为了绕开"空中放置来不来得及""放置和飞行的时序竞争"这一类时序问题,
		// 还原之后这些会重新露头:一次放置回到十几帧,ItemUseCoordinator 要等更久
		public const float UseTimeMul = 1f;

		public static bool Enabled = true;


		public override void PostUpdateEquips()
		{
			if (!Enabled) return;
			Player.blockRange += ReachBoost;
		}

		public override void PostUpdate()
		{
			if (!AgentNetwork.CanAutomate) return;
			if (Player.whoAmI != Main.myPlayer) return;
			// 【只有 start 全流程才扔】。名单上有矿石,而每帧无条件扫会让挖来的矿进包即毁:
			// mine_vein 挖满 45 格铅矿,got 里只剩土块石块,到 craft 那步才发现手里是空的
			if (StartRun.IsRunning) KeepList.Sweep();
			if (!Enabled) return;
			TopUpWood();
			GiveWeapon();
		}

		// 开局给一把武器。【只给一次】:像木头那样每帧补的话,扔掉/换掉它又会自己回来
		public const int StartWeapon = 65;
		static bool _gaveWeapon;
		public static void ResetWeaponGrant() => _gaveWeapon = false;

		static void GiveWeapon()
		{
			if (_gaveWeapon) return;
			var p = Main.LocalPlayer;
			if (p == null || !p.active) return;
			for (int i = 0; i < 58 && i < p.inventory.Length; i++)
			{
				var it = p.inventory[i];
				if (it != null && !it.IsAir && it.type == StartWeapon) { _gaveWeapon = true; return; }
			}
			for (int i = 0; i < 50 && i < p.inventory.Length; i++)
			{
				var it = p.inventory[i];
				if (it != null && !it.IsAir) continue;
				p.inventory[i] = new Item();
				p.inventory[i].SetDefaults(StartWeapon);
				_gaveWeapon = true;
				DiagLog.Write($"[concession] 开局武器 {p.inventory[i].Name}(id{StartWeapon}) 放在第{i}格");
				return;
			}
		}

		// 补到 WoodKeep。找第一摞木头往上加;一摞都没有就塞进空格
		static void TopUpWood()
		{
			var p = Main.LocalPlayer;
			if (p == null || !p.active) return;
			int have = 0, slot = -1;
			for (int i = 0; i < 58 && i < p.inventory.Length; i++)
			{
				var it = p.inventory[i];
				if (it == null || it.IsAir || it.type != ItemID.Wood) continue;
				have += it.stack;
				if (slot < 0) slot = i;
			}
			if (have >= WoodKeep) return;
			if (slot >= 0) { p.inventory[slot].stack += WoodKeep - have; return; }
			for (int i = 0; i < 50 && i < p.inventory.Length; i++)
			{
				var it = p.inventory[i];
				if (it != null && !it.IsAir) continue;
				p.inventory[i] = new Item();
				p.inventory[i].SetDefaults(ItemID.Wood);
				p.inventory[i].stack = WoodKeep;
				return;
			}
		}
	}

	// 用时倍率走 GlobalItem:ModPlayer 的 UseTimeMultiplier 只管手上那件,
	// 这里一次覆盖所有物品,挖和放都在内
	public class ConcessionSpeed : GlobalItem
	{
		// 【雷管不加速】。原版 useTime=useAnimation=40,把投掷节奏卡死在 40 帧
		// 这是打肉山那套距离表的基准。加速到 1/8 会让雷管连珠炮一样出去,
		// 距离/血量全对不上,打起来会出大问题
		static bool NoSpeedup(Item it) => it != null && it.type == ItemID.Dynamite;

		public override float UseTimeMultiplier(Item item, Player player)
			=> Concessions.Enabled && !NoSpeedup(item) ? Concessions.UseTimeMul : 1f;

		public override float UseAnimationMultiplier(Item item, Player player)
			=> Concessions.Enabled && !NoSpeedup(item) ? Concessions.UseTimeMul : 1f;
	}
}
