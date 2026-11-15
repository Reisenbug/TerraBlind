using System;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;

namespace TerraBlind;

public static class AgentCatalog
{
    public static JsonObject ResolvePlanTypes(JsonObject args)
    {
        var resolved = (JsonObject)args.DeepClone();
        foreach (string field in new[] { "steps", "add_nodes" })
        foreach (var step in resolved[field] as JsonArray ?? new JsonArray())
        foreach (var condition in step["conditions"] as JsonArray ?? new JsonArray())
        {
            if (condition["type"] is not JsonValue value || !value.TryGetValue<string>(out string name)) continue;
            string kind = condition["kind"]?.ToString();
            Type constants = kind switch
            {
                "item" or "equipped" => typeof(Terraria.ID.ItemID),
                "npc_alive" or "npc_kills" => typeof(Terraria.ID.NPCID),
                "tiles" => typeof(Terraria.ID.TileID),
                _ => throw new ArgumentException("名称型type只用于物品、装备和NPC条件。")
            };
            var matches = Query(constants, name)["matches"].AsArray();
            var ids = matches.Where(m => m["exact"]?.GetValue<bool>() == true)
                .Select(m => m["type"].GetValue<int>()).Distinct().ToArray();
            if (ids.Length != 1) throw new ArgumentException("条件type需要唯一的原版内部名称：" + name);
            condition["type"] = ids[0];
        }
        return resolved;
    }

    public static JsonObject NpcDropSources(int itemType, int limit = 40)
    {
        if (itemType <= 0 || itemType >= Terraria.ModLoader.ItemLoader.ItemCount || limit is < 1 or > 100)
            throw new ArgumentException("item_type需要已加载的物品编号，limit须1..100。");
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var sources = new JsonArray();
        int total = 0;
        for (int type = 1; type < Terraria.ModLoader.NPCLoader.NPCCount; type++)
        {
            var drops = new System.Collections.Generic.List<Terraria.GameContent.ItemDropRules.DropRateInfo>();
            foreach (var rule in Terraria.Main.ItemDropsDB.GetRulesForNPCID(type, true))
                rule.ReportDroprates(drops, new Terraria.GameContent.ItemDropRules.DropRateInfoChainFeed(1f));
            foreach (var drop in drops.Where(d => d.itemId == itemType))
            {
                total++;
                if (sources.Count >= limit) continue;
                sources.Add(new JsonObject { ["npc_type"] = type, ["name"] = Terraria.Lang.GetNPCNameValue(type),
                    ["reported_drop_rate"] = drop.dropRate, ["stack_min"] = drop.stackMin, ["stack_max"] = drop.stackMax,
                    ["conditions"] = new JsonArray((drop.conditions ?? new()).Select(c => (JsonNode)new JsonObject
                    { ["type"] = c.GetType().FullName, ["description"] = c.GetConditionDescription() }).ToArray()) });
            }
        }
        return new JsonObject { ["item_type"] = itemType, ["sources"] = sources, ["total_rules"] = total,
            ["truncated"] = total > limit, ["elapsed_ms"] = timer.Elapsed.TotalMilliseconds,
            ["source"] = "loaded_npc_drop_rules_including_global_rules",
            ["limitations"] = "枚举已加载的正数NPC类型；条件未经现场求值，报告概率不保证当前可掉落或已生成。不是击杀、拾取或库存证据。未包含负数netID变体、方块掉落、开袋及未登记到掉落规则的特殊逻辑；空结果不证明物品没有来源。" };
    }

    static string Key(string name) => name.Replace(" ", "").Replace("_", "").ToLowerInvariant();

    public static JsonObject Query(Type constants, string query, Func<int, string[]> displayNames = null)
    {
        string key = Key(query ?? "");
        if (key.Length == 0) throw new ArgumentException("请给出原版英文内部名称或名称片段。");
        bool numeric = int.TryParse(key, out int id);
        var matches = constants.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.Name != "Count" &&
                (field.FieldType == typeof(int) || field.FieldType == typeof(short) || field.FieldType == typeof(ushort) || field.FieldType == typeof(byte)))
            .Select(field => new { Name = field.Name, Id = Convert.ToInt32(field.GetRawConstantValue()),
                Labels = displayNames?.Invoke(Convert.ToInt32(field.GetRawConstantValue())) ?? Array.Empty<string>() })
            .Where(entry => numeric ? entry.Id == id : Key(entry.Name).Contains(key) || entry.Labels.Any(label => Key(label).Contains(key)))
            .OrderByDescending(entry => Key(entry.Name) == key || entry.Labels.Any(label => Key(label) == key))
            .ThenBy(entry => entry.Name, StringComparer.Ordinal)
            .ToArray();
        var exact = matches.Where(entry => numeric || Key(entry.Name) == key || entry.Labels.Any(label => Key(label) == key)).ToArray();
        if (exact.Length > 0) matches = exact;
        return new JsonObject
        {
            ["found"] = matches.Length > 0, ["total"] = matches.Length, ["truncated"] = matches.Length > 12,
            ["matches"] = new JsonArray(matches.Take(12).Select(entry => (JsonNode)new JsonObject
            {
                ["internal"] = entry.Name, ["type"] = entry.Id,
                ["exact"] = numeric || Key(entry.Name) == key || entry.Labels.Any(label => Key(label) == key),
                ["display_names"] = new JsonArray(entry.Labels.Select(label => (JsonNode)JsonValue.Create(label)).ToArray())
            }).ToArray()),
            ["source"] = "Loaded vanilla ID constants and any supplied current-language map labels. Lookup only; not inventory, drop, spawn or kill evidence. Mod-registered IDs are not included."
        };
    }
}
