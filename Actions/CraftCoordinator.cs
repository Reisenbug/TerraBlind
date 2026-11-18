using System;
using System.Linq;
using Terraria;
using Terraria.ModLoader;

namespace TerraBlind;

public static class CraftCoordinator
{
    public static int LastCrafted, LastOverflow;
    public static string LastStop = "";
    public static bool Trace;

    public static int Craft(int targetItemId, int amount)
    {
        LastCrafted = LastOverflow = 0;
        LastStop = "";
        var player = Main.LocalPlayer;
        if (!Main.mouseItem.IsAir)
        {
            LastStop = "craft_cursor_occupied";
            return 0;
        }
        player.AdjTiles();
        while (LastCrafted < amount)
        {
            Recipe.FindRecipes();
            var recipe = Main.availableRecipe.Take(Main.numAvailableRecipes).Select(id => Main.recipe[id])
                .FirstOrDefault(candidate => candidate.createItem.type == targetItemId);
            if (recipe == null)
            {
                LastStop = LastCrafted == 0 ? "no_recipe" : "no_more_materials";
                break;
            }
            var output = recipe.createItem;
            int space = 0;
            for (int i = 0; i < 50 && i < player.inventory.Length && space < output.stack; i++)
            {
                var item = player.inventory[i];
                if (item.IsAir) space += output.maxStack;
                else if (item.type == output.type && item.prefix == output.prefix && ItemLoader.CanStack(item, output))
                    space += Math.Max(0, item.maxStack - item.stack);
            }
            if (space < output.stack)
            {
                LastStop = "inventory_full";
                break;
            }
            Main.CraftItem(recipe);
            var made = Main.mouseItem;
            if (made.IsAir) { LastStop = "craft_not_observed"; break; }
            if (made.type != targetItemId)
            {
                Main.playerInventory = true;
                LastOverflow = made.stack;
                LastStop = "craft_output_changed";
                break;
            }
            int count = made.stack;
            Main.mouseItem = player.GetItem(Main.myPlayer, made, GetItemSettings.GetItemInDropItemCheck);
            LastOverflow = Main.mouseItem.IsAir ? 0 : Main.mouseItem.stack;
            LastCrafted += count - LastOverflow;
            if (LastOverflow > 0)
            {
                Main.playerInventory = true;
                LastStop = "crafted_remainder_on_cursor";
                break;
            }
        }
        if (Trace) DiagLog.Write($"[craft-native] item={targetItemId} crafted={LastCrafted} cursor={LastOverflow} stop={LastStop}");
        return LastCrafted;
    }
}
