using System;
using System.Linq;
using System.Text.Json.Nodes;
using Terraria;
using Terraria.ID;
using Terraria.UI;

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

    public static JsonObject Slot(JsonObject args, bool click)
    {
        var player = Main.LocalPlayer;
        int index = args["slot"]?.GetValue<int>() ?? -1;
        string section = args["section"]?.GetValue<string>();
        Item[] slots = player.inventory;
        int context;
        switch (section)
        {
            case "inventory" when index is >= 0 and < 50: context = ItemSlot.Context.InventoryItem; break;
            case "coin" when index is >= 0 and < 4: index += 50; context = ItemSlot.Context.InventoryCoin; break;
            case "ammo" when index is >= 0 and < 4: index += 54; context = ItemSlot.Context.InventoryAmmo; break;
            case "equipment" or "vanity" when index is >= 0 and < 10:
                context = section == "equipment"
                    ? index < 3 ? ItemSlot.Context.EquipArmor : ItemSlot.Context.EquipAccessory
                    : index < 3 ? ItemSlot.Context.EquipArmorVanity : ItemSlot.Context.EquipAccessoryVanity;
                if (section == "vanity") index += 10;
                slots = player.armor;
                if (click && !player.IsItemSlotUnlockedAndUsable(index >= 10 ? index - 10 : index))
                    return new JsonObject { ["error"] = "equipment_slot_locked" };
                break;
            case "container" when index >= 0:
                if (player.chest < -5 || player.chest == -1
                    || player.chest >= 0 && (player.chest >= Main.chest.Length || Main.chest[player.chest] == null))
                    return new JsonObject { ["error"] = "container_not_open" };
                if (click && args["expected_container"]?.GetValue<int>() != player.chest)
                    return new JsonObject { ["error"] = "container_changed", ["container"] = player.chest };
                ChestUI.GetContainerUsageInfo(out _, out slots);
                context = player.chest >= 0 ? ItemSlot.Context.ChestItem : ItemSlot.Context.BankItem;
                break;
            case "trash" when index == 0: slots = new[] { player.trashItem }; context = ItemSlot.Context.TrashItem; break;
            default: return new JsonObject { ["error"] = "invalid_slot_address" };
        }
        if (index >= slots.Length) return new JsonObject { ["error"] = "invalid_slot_address" };
        JsonObject State() => new()
        {
            ["item"] = ItemState(slots[index]), ["cursor"] = ItemState(Main.mouseItem),
            ["container"] = section == "container" ? player.chest : null
        };
        if (!click) return State();
        if (!Main.playerInventory) return new JsonObject { ["error"] = "inventory_closed" };
        if (Main.gameMenu || !player.active || player.dead || player.itemAnimation != 0 || player.itemTime != 0
            || ItemUseCoordinator.IsActive || PlaceAnywhere.IsRunning)
            return new JsonObject { ["error"] = "inventory_busy" };
        bool Matches(Item item, JsonNode expected) => expected is JsonObject
            && expected["type"]?.GetValue<int>() == item.type && expected["prefix"]?.GetValue<int>() == item.prefix
            && expected["stack"]?.GetValue<int>() == item.stack;
        var before = State();
        if (!Matches(slots[index], args["expected_item"]) || !Matches(Main.mouseItem, args["expected_cursor"]))
            return new JsonObject { ["error"] = "stale_slot", ["actual"] = before };
        string button = args["button"]?.GetValue<string>();
        if (button is not ("left" or "right")) return new JsonObject { ["error"] = "invalid_slot_button" };
        var mouse = (Main.mouseLeft, Main.mouseLeftRelease, Main.mouseRight, Main.mouseRightRelease);
        try
        {
            Main.mouseLeft = Main.mouseLeftRelease = button == "left";
            Main.mouseRight = Main.mouseRightRelease = button == "right";
            if (button == "left") ItemSlot.LeftClick(slots, context, index);
            else ItemSlot.RightClick(slots, context, index);
            if (section == "trash") player.trashItem = slots[index];
        }
        finally { (Main.mouseLeft, Main.mouseLeftRelease, Main.mouseRight, Main.mouseRightRelease) = mouse; }
        var after = State();
        bool changed = !JsonNode.DeepEquals(before, after);
        if (changed) PvpDuel.InventoryChanged();
        return new JsonObject { ["ok"] = true, ["outcome"] = changed ? "changed" : "unchanged",
            ["before"] = before, ["after"] = after };
    }

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
            fact["can_open"] = Terraria.ModLoader.ItemLoader.CanRightClick(item);
            fact["heal_life"] = item.healLife; fact["use_ammo"] = item.useAmmo;
            return fact;
        }
        return new JsonObject
        {
            ["trash"] = ItemState(player.trashItem),
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

    public static JsonObject Drop(JsonObject args) => Discard(args, false);

    public static JsonObject Trash(JsonObject args) => Discard(args, true);

    public static JsonObject Open(JsonObject args)
    {
        var player = Main.LocalPlayer;
        int slot = args["slot"]?.GetValue<int>() ?? -1;
        if (slot is < 0 or >= 50) return new JsonObject { ["error"] = "invalid_inventory_slot" };
        if (Main.gameMenu || !player.active || player.dead || player.itemAnimation != 0 || player.itemTime != 0
            || !Main.mouseItem.IsAir || ItemUseCoordinator.IsActive || PlaceAnywhere.IsRunning)
            return new JsonObject { ["error"] = "inventory_busy" };
        var item = player.inventory[slot]; var expected = args["expected"];
        if (expected is not JsonObject || expected["type"]?.GetValue<int>() != item.type
            || expected["prefix"]?.GetValue<int>() != item.prefix || expected["stack"]?.GetValue<int>() != item.stack)
            return new JsonObject { ["error"] = "stale_inventory", ["actual"] = ItemState(item) };
        if (!Terraria.ModLoader.ItemLoader.CanRightClick(item)) return new JsonObject { ["error"] = "item_not_openable" };
        var before = ItemState(item);
        var mouse = (Main.mouseRight, Main.mouseRightRelease);
        try
        {
            Main.mouseRight = Main.mouseRightRelease = true;
            Terraria.UI.ItemSlot.RightClick(player.inventory, 0, slot);
        }
        finally { (Main.mouseRight, Main.mouseRightRelease) = mouse; }
        var after = player.inventory[slot];
        int consumed = before["stack"].GetValue<int>() - (after.type == before["type"].GetValue<int>() ? after.stack : 0);
        return new JsonObject { ["ok"] = consumed > 0, ["outcome"] = consumed > 0 ? "opened" : "not_consumed",
            ["consumed"] = consumed, ["item_before"] = before, ["item_after"] = ItemState(after) };
    }

    static JsonObject Discard(JsonObject args, bool trash)
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
        if (trash)
        {
            var expectedTrash = args["expected_trash"];
            if (expectedTrash is not JsonObject || expectedTrash["type"]?.GetValue<int>() != player.trashItem.type
                || expectedTrash["prefix"]?.GetValue<int>() != player.trashItem.prefix
                || expectedTrash["stack"]?.GetValue<int>() != player.trashItem.stack)
                return new JsonObject { ["error"] = "stale_trash", ["actual"] = ItemState(player.trashItem) };
            var replaced = ItemState(player.trashItem);
            player.trashItem = item;
            player.inventory[slot] = new Item();
            if (Main.netMode == NetmodeID.MultiplayerClient)
                NetMessage.SendData(MessageID.SyncEquipment, number: player.whoAmI, number2: slot);
            PvpDuel.InventoryChanged();
            return new JsonObject { ["ok"] = true, ["outcome"] = "trashed", ["slot"] = slot,
                ["after"] = ItemState(player.inventory[slot]), ["trash"] = ItemState(player.trashItem),
                ["replaced"] = replaced, ["detail"] = "整叠移入垃圾桶，不生成地面掉落；旧垃圾被覆盖。当前垃圾桶物品仍可在游戏界面取回。" };
        }
        if (!Main.item.Take(Main.maxItems).Any(i => !i.active)) return new JsonObject { ["error"] = "world_item_capacity" };
        var before = ItemState(item);
        player.DropSelectedItem(slot, ref player.inventory[slot]);
        int dropped = Array.FindIndex(Main.item, i => ReferenceEquals(i, item) && i.active);
        bool verified = player.inventory[slot].IsAir && dropped >= 0;
        JsonObject spawn = dropped < 0 ? null : new JsonObject
        {
            ["x"] = Main.item[dropped].position.X, ["y"] = Main.item[dropped].position.Y,
            ["vx"] = Main.item[dropped].velocity.X, ["vy"] = Main.item[dropped].velocity.Y
        };
        if (Main.netMode == NetmodeID.MultiplayerClient)
            NetMessage.SendData(MessageID.SyncEquipment, number: player.whoAmI, number2: slot);
        PvpDuel.InventoryChanged();
        return new JsonObject { ["ok"] = verified, ["slot"] = slot, ["before"] = before,
            ["after"] = ItemState(player.inventory[slot]), ["world_item"] = dropped >= 0 ? dropped : null,
            ["spawn_pixels"] = spawn,
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
