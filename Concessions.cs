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

		// 伸手范围加成。vanilla tileRangeX=5 tileRangeY=4,每帧 ResetEffects 清零 blockRange
		// 再由饰品累加,所以这里每帧加,和饰品是同一条路,不动 static 字段
		// (改 static 会让所有读 tileRangeX 的地方跟着变,包括规划器自己的判据)
		public const int ReachBoost = 8;

		// 放置/挖掘用时倍率。1/8 让一次放置从十几帧压到一两帧,
		// 空中放置来不来得及、放置和飞行的时序竞争,这一整类问题就消失了
		public const float UseTimeMul = 0.125f;

		public static bool Enabled = true;

		public override void PostUpdateEquips()
		{
			if (!Enabled) return;
			Player.blockRange += ReachBoost;
		}

		public override void PostUpdate()
		{
			if (!Enabled || Player.whoAmI != Main.myPlayer) return;
			TopUpWood();
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
		public override float UseTimeMultiplier(Item item, Player player)
			=> Concessions.Enabled ? Concessions.UseTimeMul : 1f;

		public override float UseAnimationMultiplier(Item item, Player player)
			=> Concessions.Enabled ? Concessions.UseTimeMul : 1f;
	}
}
