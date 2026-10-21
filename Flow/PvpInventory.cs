using System.Text.Json.Nodes;
using Terraria;
using Terraria.ID;

namespace TerraBlind;

public static class PvpInventory
{
    public static JsonArray Snapshot()
    {
        var slots = new JsonArray();
        for (int slot = 0; slot < 50; slot++)
        {
            var item = ItemState(Main.LocalPlayer.inventory[slot]); item["slot"] = slot; slots.Add(item);
        }
        return slots;
    }

    static JsonObject ItemState(Item item) => new()
    { ["type"] = item.type, ["prefix"] = item.prefix, ["stack"] = item.stack, ["name"] = item.Name };

    public static JsonObject Swap(JsonObject args)
    {
        int src = args["src"]?.GetValue<int>() ?? -1, dst = args["dst"]?.GetValue<int>() ?? -1;
        var self = Main.LocalPlayer;
        if (src < 0 || src >= 50 || dst < 0 || dst >= 50 || src == dst) return new JsonObject { ["error"] = "invalid_inventory_slots" };
        if (self.dead || self.itemAnimation != 0 || self.itemTime != 0 || ItemUseCoordinator.IsActive || PlaceAnywhere.IsRunning)
            return new JsonObject { ["error"] = "inventory_busy" };
        bool Matches(Item item, JsonNode expected) => expected is JsonObject
            && expected["type"]?.GetValue<int>() == item.type && expected["prefix"]?.GetValue<int>() == item.prefix
            && expected["stack"]?.GetValue<int>() == item.stack;
        if (!Matches(self.inventory[src], args["expected_src"]) || !Matches(self.inventory[dst], args["expected_dst"]))
            return new JsonObject { ["error"] = "stale_inventory", ["src_item"] = ItemState(self.inventory[src]), ["dst_item"] = ItemState(self.inventory[dst]) };
        return SwapSlots(src, dst);
    }

    public static JsonObject SwapSlots(int src, int dst)
    {
        var self = Main.LocalPlayer;
        if (src < 0 || src >= self.inventory.Length || dst < 0 || dst >= self.inventory.Length || src == dst)
            return new JsonObject { ["error"] = "invalid_inventory_slots" };
        var before = new JsonObject { ["src"] = ItemState(self.inventory[src]), ["dst"] = ItemState(self.inventory[dst]) };
        (self.inventory[src], self.inventory[dst]) = (self.inventory[dst], self.inventory[src]);
        if (Main.netMode == NetmodeID.MultiplayerClient)
        {
            NetMessage.SendData(MessageID.SyncEquipment, number: self.whoAmI, number2: src);
            NetMessage.SendData(MessageID.SyncEquipment, number: self.whoAmI, number2: dst);
        }
        PvpDuel.InventoryChanged();
        return new JsonObject { ["ok"] = true, ["src"] = src, ["dst"] = dst, ["before"] = before,
            ["after"] = new JsonObject { ["src"] = ItemState(self.inventory[src]), ["dst"] = ItemState(self.inventory[dst]) } };
    }
}
