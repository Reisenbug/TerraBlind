using System;
using System.Linq;
using System.Text.Json.Nodes;
using Terraria;
using Terraria.ID;
using Terraria.UI;

namespace TerraBlind;

public static class Equipment
{
    internal const int GrappleSlot = 10;
    const int GrappleIndex = 4;
    static JsonObject ItemState(Item item) => new()
    { ["type"] = item.type, ["prefix"] = item.prefix, ["stack"] = item.stack, ["name"] = item.Name };

    public static JsonArray Snapshot() => new(Enumerable.Range(0, GrappleSlot + 1).Select(slot =>
    {
        bool grapple = slot == GrappleSlot;
        var item = ItemState(grapple ? Main.LocalPlayer.miscEquips[GrappleIndex] : Main.LocalPlayer.armor[slot]);
        item["slot"] = slot; item["unlocked"] = grapple || Main.LocalPlayer.IsItemSlotUnlockedAndUsable(slot);
        item["kind"] = slot switch { 0 => "head", 1 => "body", 2 => "legs", GrappleSlot => "grapple", _ => "accessory" };
        return (JsonNode)item;
    }).ToArray());

    public static JsonObject Swap(JsonObject args)
    {
        var player = Main.LocalPlayer;
        int source = args["inventory_slot"]?.GetValue<int>() ?? -1, target = args["equipment_slot"]?.GetValue<int>() ?? -1;
        if (source is < 0 or >= 50 || target is < 0 or > GrappleSlot) return new JsonObject { ["error"] = "invalid_equipment_slot" };
        bool grapple = target == GrappleSlot;
        Item[] equipment = grapple ? player.miscEquips : player.armor;
        int nativeSlot = grapple ? GrappleIndex : target;
        if (Main.gameMenu || !player.active || player.dead || player.itemAnimation != 0 || player.itemTime != 0 || !Main.mouseItem.IsAir
            || ItemUseCoordinator.IsActive || PlaceAnywhere.IsRunning)
            return new JsonObject { ["error"] = "equipment_busy", ["busy"] = new JsonObject
            {
                ["menu"] = Main.gameMenu, ["active"] = player.active, ["dead"] = player.dead,
                ["item_animation"] = player.itemAnimation, ["item_time"] = player.itemTime,
                ["mouse_item"] = ItemState(Main.mouseItem), ["item_use_active"] = ItemUseCoordinator.IsActive,
                ["placement_active"] = PlaceAnywhere.IsRunning
            } };
        bool Matches(Item item, JsonNode expected) => expected is JsonObject && expected["type"]?.GetValue<int>() == item.type
            && expected["prefix"]?.GetValue<int>() == item.prefix && expected["stack"]?.GetValue<int>() == item.stack;
        if (!Matches(player.inventory[source], args["expected_inventory"]) || !Matches(equipment[nativeSlot], args["expected_equipment"]))
            return new JsonObject { ["error"] = "stale_equipment", ["inventory"] = ItemState(player.inventory[source]),
                ["equipment"] = ItemState(equipment[nativeSlot]) };
        if ((!grapple && !player.IsItemSlotUnlockedAndUsable(target)) || ItemSlot.isEquipLocked(equipment[nativeSlot].type))
            return new JsonObject { ["error"] = "equipment_slot_locked" };
        int context = grapple ? ItemSlot.Context.EquipGrapple : target < 3 ? ItemSlot.Context.EquipArmor : ItemSlot.Context.EquipAccessory;
        if (ItemSlot.PickItemMovementAction(equipment, context, nativeSlot, player.inventory[source]) != 1)
            return new JsonObject { ["error"] = "incompatible_equipment" };
        var before = new JsonObject { ["inventory"] = ItemState(player.inventory[source]), ["equipment"] = ItemState(equipment[nativeSlot]) };
        bool left = Main.mouseLeft, released = Main.mouseLeftRelease;
        try
        {
            Utils.Swap(ref Main.mouseItem, ref player.inventory[source]);
            Main.mouseLeft = Main.mouseLeftRelease = true;
            ItemSlot.LeftClick(equipment, context, nativeSlot);
        }
        finally
        {
            if (player.inventory[source].IsAir) Utils.Swap(ref Main.mouseItem, ref player.inventory[source]);
            Main.mouseLeft = left; Main.mouseLeftRelease = released;
        }
        bool changed = before["equipment"]["type"].GetValue<int>() != equipment[nativeSlot].type
            || before["equipment"]["prefix"].GetValue<int>() != equipment[nativeSlot].prefix;
        if (Main.netMode == NetmodeID.MultiplayerClient)
        {
            NetMessage.SendData(MessageID.SyncEquipment, number: player.whoAmI, number2: source);
            NetMessage.SendData(MessageID.SyncEquipment, number: player.whoAmI,
                number2: (grapple ? PlayerItemSlotID.Misc0 : PlayerItemSlotID.Armor0) + nativeSlot);
        }
        return new JsonObject { ["ok"] = changed, ["outcome"] = !changed ? "unchanged" : equipment[nativeSlot].IsAir ? "unequipped" : "equipped",
            ["inventory_slot"] = source, ["equipment_slot"] = target, ["before"] = before,
            ["after"] = new JsonObject { ["inventory"] = ItemState(player.inventory[source]), ["equipment"] = ItemState(equipment[nativeSlot]) },
            ["cursor_empty"] = Main.mouseItem.IsAir, ["effects"] = "updated_by_next_player_tick" };
    }
}
