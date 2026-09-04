using Terraria;

namespace TerraBlind
{
    public static class CraftCoordinator
    {
        // 背包满时 GetItem 收不下,多出来的会掉在地上 —— 而材料已经扣掉了,等于白烧。
        // 所以每一轮先看背包收不收得下:收不下就停,不再扣下一份材料。
        // Crafted 只数真进了背包的,Overflow 数掉地上的,Stopped 说明为什么停 —— 以前一律报
        // "crafted:96" 然后下一步查库存是 0,谎报在别处才炸。
        public static int LastCrafted, LastOverflow;
        public static string LastStop = "";

        // 合成数和背包实际数对不上时,要看的是每一轮各发生了什么,不是最后一个总数
        public static bool Trace = false;

        static int Count(int id)
        {
            var p = Main.LocalPlayer;
            if (p == null) return -1;
            int n = 0;
            for (int i = 0; i < 58 && i < p.inventory.Length; i++)
            {
                var it = p.inventory[i];
                if (it != null && !it.IsAir && it.type == id) n += it.stack;
            }
            return n;
        }

        public static int Craft(int targetItemId, int amount)
        {
            LastCrafted = 0; LastOverflow = 0; LastStop = "";
            int remaining = amount;
            // 满包时游戏连配方都不算 available,合成必失败。先扔两格出来,合完由调用方捡回。
            if (ThrowItems.FreeSlots() < 2) ThrowItems.MakeRoom(2);
            if (Trace) DiagLog.Write($"[craft] 合成前 当前数={Count(targetItemId)} 要合={amount}");
            int round = 0;
            while (remaining > 0)
            {
                round++;
                int recipeIdx = -1;
                for (int ri = 0; ri < Main.numAvailableRecipes; ri++)
                {
                    var r = Main.recipe[Main.availableRecipe[ri]];
                    if (r.createItem.type == targetItemId)
                    {
                        recipeIdx = Main.availableRecipe[ri];
                        break;
                    }
                }
                // 配方找不到有三种可能,别一律报 no_recipe:背包满的时候游戏根本不把配方算作
                // available,材料再多也查不到 —— 那次房子就是这样,报 not_available 像是缺材料。
                if (recipeIdx < 0)
                {
                    if (Trace) DiagLog.Write($"[craft] 第{round}轮 找不到配方 当前数={Count(targetItemId)} 还要={remaining}");
                    if (LastStop == "")
                    {
                        int free = 0;
                        var pf = Main.LocalPlayer;
                        if (pf != null)
                            for (int i = 0; i < 50; i++)
                                if (pf.inventory[i] == null || pf.inventory[i].IsAir) free++;
                        LastStop = free == 0 ? "inventory_full" : (LastCrafted > 0 ? "no_more_materials" : "no_recipe");
                    }
                    break;
                }

                var recipe = Main.recipe[recipeIdx];
                int stackPerCraft = recipe.createItem.stack;

                foreach (var req in recipe.requiredItem)
                {
                    if (req.IsAir) continue;
                    int needed = req.stack;
                    for (int i = 0; i < 58 && needed > 0; i++)
                    {
                        var slot = Main.LocalPlayer.inventory[i];
                        if (slot == null || slot.IsAir || slot.type != req.type) continue;
                        int take = System.Math.Min(needed, slot.stack);
                        slot.stack -= take;
                        needed -= take;
                        if (slot.stack <= 0) slot.TurnToAir();
                    }
                }

                int before = Trace ? Count(targetItemId) : 0;

                var result = new Item();
                result.SetDefaults(targetItemId);
                result.stack = stackPerCraft;
                // 返回的是"没收下的部分"(空/stack=0 = 全收了)。原来这个返回值被丢掉,
                // 于是背包满时东西掉地上,计数照样加。
                var left = Main.LocalPlayer.GetItem(Main.myPlayer, result, GetItemSettings.GetItemInDropItemCheck);
                int refused = (left == null || left.IsAir) ? 0 : left.stack;
                LastCrafted += stackPerCraft - refused;
                remaining -= stackPerCraft;
                if (Trace)
                    DiagLog.Write($"[craft] 第{round}轮 产出{stackPerCraft} 拒收{refused} 数量 {before}→{Count(targetItemId)} 还要={remaining}");
                if (refused > 0)
                {
                    LastOverflow += refused;
                    LastStop = "inventory_full";
                    break;   // 背包已经收不下,再合下去只会继续白烧材料
                }
            }
            if (Trace) DiagLog.Write($"[craft] 合成后 当前数={Count(targetItemId)} 记账crafted={LastCrafted} overflow={LastOverflow} stop={LastStop}");
            return LastCrafted;
        }
    }
}
