using Terraria;

namespace TerraBlind
{
    public static class CraftCoordinator
    {
        public static int Craft(int targetItemId, int amount)
        {
            int totalCrafted = 0;
            int remaining = amount;
            while (remaining > 0)
            {
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
                if (recipeIdx < 0) break;

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

                var result = new Item();
                result.SetDefaults(targetItemId);
                result.stack = stackPerCraft;
                Main.LocalPlayer.GetItem(Main.myPlayer, result, GetItemSettings.GetItemInDropItemCheck);

                totalCrafted += stackPerCraft;
                remaining -= stackPerCraft;
            }
            return totalCrafted;
        }
    }
}
