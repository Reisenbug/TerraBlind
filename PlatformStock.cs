using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraBlind
{
	// 平台是脚下的路。任何时候低于 Low 就用木材补到 High —— 不看谁在用、也不等流程走到补货点。
	// 手里 198 木材却因为平台见底停在半空,是最没道理的失败。
	public class PlatformStock : ModSystem
	{
		public const int ItemId = ItemID.WoodPlatform;
		private const int Low = 20;
		private const int High = 100;
		private const int WoodReserve = 30;    // 留给房子/家具的木材,不许平台吃光
		private const int CheckEvery = 30;
		private const int RetryCooldown = 600; // 合失败了别每半秒重试一次
		private const int ShortRetry = 60;     // 配方表还没建好那种,过一秒就能成

		private static int _nextTry;

		public static void Tick()
		{
			var p = Main.LocalPlayer;
			if (p == null || !p.active) return;
			if (Main.GameUpdateCount % CheckEvery != 0) return;
			if (Main.GameUpdateCount < _nextTry) return;

			int have = Predicates.Have(ItemId);
			if (have >= Low) return;

			int wood = Predicates.Have(ItemID.Wood);
			int spare = wood - WoodReserve;
			if (spare <= 0) { _nextTry = (int)Main.GameUpdateCount + RetryCooldown; return; }

			int want = High - have;
			int times = System.Math.Min((want + 1) / 2, spare);
			if (times <= 0) { _nextTry = (int)Main.GameUpdateCount + RetryCooldown; return; }

			CraftCoordinator.Craft(ItemId, times * 2);
			int now = Predicates.Have(ItemId);
			DiagLog.Write($"[platstock] 平台{have}<{Low},木材{wood} → 合到 {now} stop={CraftCoordinator.LastStop}");
			// 【no_recipe 是暂时的】。刚进世界配方表还没建好,这时判它 600 帧冷却,
			// 等再次重试人已经开工了 --- 现场:0帧 no_recipe,1200帧才补上,而房子1040帧就要用
			if (now <= have)
				_nextTry = (int)Main.GameUpdateCount
					+ (CraftCoordinator.LastStop == "no_recipe" ? ShortRetry : RetryCooldown);
		}
	}
}
