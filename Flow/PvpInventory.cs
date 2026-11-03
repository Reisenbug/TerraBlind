using System;
using System.Linq;
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

    public static JsonObject Review()
    {
        var player = Main.LocalPlayer;
        JsonObject Describe(Item item, int slot)
        {
            var fact = ItemState(item);
            fact["slot"] = slot; fact["favorited"] = item.favorited;
            fact["defense"] = item.defense; fact["accessory"] = item.accessory;
            fact["armor_slot"] = item.headSlot >= 0 ? 0 : item.bodySlot >= 0 ? 1 : item.legSlot >= 0 ? 2 : null;
            fact["damage"] = item.damage; fact["pick"] = item.pick; fact["axe"] = item.axe; fact["hammer"] = item.hammer;
            fact["material"] = item.material; fact["consumable"] = item.consumable;
            fact["heal_life"] = item.healLife; fact["use_ammo"] = item.useAmmo;
            return fact;
        }
        return new JsonObject
        {
            ["inventory"] = new JsonArray(Enumerable.Range(0, 50).Where(i => !player.inventory[i].IsAir)
                .Select(i => (JsonNode)Describe(player.inventory[i], i)).ToArray()),
            ["empty_slots"] = new JsonArray(Enumerable.Range(0, 50).Where(i => player.inventory[i].IsAir)
                .Select(i => (JsonNode)JsonValue.Create(i)).ToArray()),
            ["equipment"] = new JsonArray(Enumerable.Range(0, 10).Select(i =>
            {
                var fact = Describe(player.armor[i], i);
                fact["unlocked"] = player.IsItemSlotUnlockedAndUsable(i);
                fact["kind"] = i < 3 ? new[] { "head", "body", "legs" }[i] : "accessory";
                return (JsonNode)fact;
            }).ToArray()),
            ["detail"] = "属性是物品基础值，不是装备收益评分。特殊效果用item_info查询；按当前目标决定保留、换装或丢弃。"
        };
    }

    public static JsonObject Drop(JsonObject args)
    {
        var player = Main.LocalPlayer;
        int slot = args["slot"]?.GetValue<int>() ?? -1;
        if (slot is < 0 or >= 50) return new JsonObject { ["error"] = "invalid_inventory_slot" };
        if (Main.gameMenu || !player.active || player.dead || player.itemAnimation != 0 || player.itemTime != 0
            || !Main.mouseItem.IsAir || ItemUseCoordinator.IsActive || PlaceAnywhere.IsRunning)
            return new JsonObject { ["error"] = "inventory_busy" };
        var item = player.inventory[slot];
        var expected = args["expected"];
        if (expected is not JsonObject || expected["type"]?.GetValue<int>() != item.type
            || expected["prefix"]?.GetValue<int>() != item.prefix || expected["stack"]?.GetValue<int>() != item.stack)
            return new JsonObject { ["error"] = "stale_inventory", ["actual"] = ItemState(item) };
        if (item.IsAir || item.favorited) return new JsonObject { ["error"] = item.IsAir ? "empty_slot" : "favorited_item" };
        if (!Main.item.Take(Main.maxItems).Any(i => !i.active)) return new JsonObject { ["error"] = "world_item_capacity" };
        var before = ItemState(item);
        player.DropSelectedItem(slot, ref player.inventory[slot]);
        int dropped = Array.FindIndex(Main.item, i => ReferenceEquals(i, item) && i.active);
        bool verified = player.inventory[slot].IsAir && dropped >= 0;
        if (Main.netMode == NetmodeID.MultiplayerClient)
            NetMessage.SendData(MessageID.SyncEquipment, number: player.whoAmI, number2: slot);
        PvpDuel.InventoryChanged();
        return new JsonObject { ["ok"] = verified, ["slot"] = slot, ["before"] = before,
            ["after"] = ItemState(player.inventory[slot]), ["world_item"] = dropped >= 0 ? dropped : null,
            ["outcome"] = verified ? "dropped" : "drop_unverified",
            ["detail"] = "整叠按原版规则丢到地上，不是销毁；之后可能被重新拾取。" };
    }

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
