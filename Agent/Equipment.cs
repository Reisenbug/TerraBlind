using System;
using System.Linq;
using System.Text.Json.Nodes;
using Terraria;
using Terraria.ID;
using Terraria.UI;

namespace TerraBlind;

public static class Equipment
{
    static JsonObject ItemState(Item item) => new()
    { ["type"] = item.type, ["prefix"] = item.prefix, ["stack"] = item.stack, ["name"] = item.Name };

    public static JsonArray Snapshot() => new(Enumerable.Range(0, 10).Select(slot =>
    {
        var item = ItemState(Main.LocalPlayer.armor[slot]);
        item["slot"] = slot; item["unlocked"] = Main.LocalPlayer.IsItemSlotUnlockedAndUsable(slot);
        item["kind"] = slot switch { 0 => "head", 1 => "body", 2 => "legs", _ => "accessory" };
        return (JsonNode)item;
    }).ToArray());

    public static JsonObject Swap(JsonObject args)
    {
        var player = Main.LocalPlayer;
        int source = args["inventory_slot"]?.GetValue<int>() ?? -1, target = args["equipment_slot"]?.GetValue<int>() ?? -1;
        if (source is < 0 or >= 50 || target is < 0 or >= 10) return new JsonObject { ["error"] = "invalid_equipment_slot" };
        if (Main.gameMenu || !player.active || player.dead || player.itemAnimation != 0 || player.itemTime != 0 || !Main.mouseItem.IsAir
            || ItemUseCoordinator.IsActive || PlaceAnywhere.IsRunning) return new JsonObject { ["error"] = "equipment_busy" };
        bool Matches(Item item, JsonNode expected) => expected is JsonObject && expected["type"]?.GetValue<int>() == item.type
            && expected["prefix"]?.GetValue<int>() == item.prefix && expected["stack"]?.GetValue<int>() == item.stack;
        if (!Matches(player.inventory[source], args["expected_inventory"]) || !Matches(player.armor[target], args["expected_equipment"]))
            return new JsonObject { ["error"] = "stale_equipment", ["inventory"] = ItemState(player.inventory[source]),
                ["equipment"] = ItemState(player.armor[target]) };
        if (!player.IsItemSlotUnlockedAndUsable(target) || ItemSlot.isEquipLocked(player.armor[target].type))
            return new JsonObject { ["error"] = "equipment_slot_locked" };
        int context = target < 3 ? ItemSlot.Context.EquipArmor : ItemSlot.Context.EquipAccessory;
        if (ItemSlot.PickItemMovementAction(player.armor, context, target, player.inventory[source]) != 1)
            return new JsonObject { ["error"] = "incompatible_equipment" };
        var before = new JsonObject { ["inventory"] = ItemState(player.inventory[source]), ["equipment"] = ItemState(player.armor[target]) };
        bool left = Main.mouseLeft, released = Main.mouseLeftRelease;
        try
        {
            Utils.Swap(ref Main.mouseItem, ref player.inventory[source]);
            Main.mouseLeft = Main.mouseLeftRelease = true;
            ItemSlot.LeftClick(player.armor, context, target);
        }
        finally
        {
            if (player.inventory[source].IsAir) Utils.Swap(ref Main.mouseItem, ref player.inventory[source]);
            Main.mouseLeft = left; Main.mouseLeftRelease = released;
        }
        bool changed = before["equipment"]["type"].GetValue<int>() != player.armor[target].type
            || before["equipment"]["prefix"].GetValue<int>() != player.armor[target].prefix;
        if (Main.netMode == NetmodeID.MultiplayerClient)
        {
            NetMessage.SendData(MessageID.SyncEquipment, number: player.whoAmI, number2: source);
            NetMessage.SendData(MessageID.SyncEquipment, number: player.whoAmI, number2: PlayerItemSlotID.Armor0 + target);
        }
        return new JsonObject { ["ok"] = changed, ["outcome"] = !changed ? "unchanged" : player.armor[target].IsAir ? "unequipped" : "equipped",
            ["inventory_slot"] = source, ["equipment_slot"] = target, ["before"] = before,
            ["after"] = new JsonObject { ["inventory"] = ItemState(player.inventory[source]), ["equipment"] = ItemState(player.armor[target]) },
            ["cursor_empty"] = Main.mouseItem.IsAir, ["effects"] = "updated_by_next_player_tick" };
    }
}
