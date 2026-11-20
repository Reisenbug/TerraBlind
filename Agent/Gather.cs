using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TerraBlind;

public sealed record GatherTarget(int ItemType, int Count, string[] Tiles, int[] Npcs, int? TileFrameX = null, int? TileFrameY = null);

public sealed record GatherArea(int X, int Y, int Width, int Height)
{
    public static GatherArea Parse(JsonObject value)
    {
        if (value == null) return null;
        var area = new GatherArea(value["x"].GetValue<int>(), value["y"].GetValue<int>(),
            value["width"].GetValue<int>(), value["height"].GetValue<int>());
        if (area.X < 0 || area.Y < 0 || area.Width is < 1 or > 120 || area.Height is < 1 or > 120)
            throw new ArgumentException("source_area需要非负坐标及1..120格的width/height。");
        return area;
    }
    public bool Contains(JsonObject candidate) => candidate["x"] != null && candidate["y"] != null
        && candidate["x"].GetValue<int>() >= X && candidate["x"].GetValue<int>() < (long)X + Width
        && candidate["y"].GetValue<int>() >= Y && candidate["y"].GetValue<int>() < (long)Y + Height;
    public JsonObject Snapshot() => new() { ["x"] = X, ["y"] = Y, ["width"] = Width, ["height"] = Height };
}

public interface IGatherWorld
{
    Task<JsonObject> Observe(CancellationToken token);
    Task<JsonObject> Act(JsonObject candidate, CancellationToken token);
    Task Stop(CancellationToken token);
}

public static class Gather
{
    public static JsonArray DescribeSources(GatherTarget[] targets) => new(targets.Select(t => (JsonNode)new JsonObject
    {
        ["item_type"] = t.ItemType, ["tile_names"] = new JsonArray(t.Tiles.Select(v => (JsonNode)JsonValue.Create(v)).ToArray()),
        ["npc_types"] = new JsonArray(t.Npcs.Select(v => (JsonNode)JsonValue.Create(v)).ToArray()),
        ["drops_only"] = t.Tiles.Length == 0 && t.Npcs.Length == 0,
        ["detail"] = t.Tiles.Length == 0 && t.Npcs.Length == 0
            ? "仅搜索已有掉落物；未配置方块或敌怪来源，不会从物品编号推断来源。要主动获取，先查证并提供tile_names或npc_types；npc_drop_sources可查询登记的敌怪掉落规则，方块来源需另查。"
            : "来源过滤来自调用者配置，未验证这些方块或敌怪会掉目标物品。候选item_type是请求目标，不是掉落结论；生长基底不等于采集对象，同类型方块的frame变体也可能掉不同物品。需查证来源并以实际库存增量验证。"
    }).ToArray());

    public static void Validate(GatherTarget[] targets, GatherArea area)
    {
        if (targets.Length is < 1 or > 8 || targets.Select(t => t.ItemType).Distinct().Count() != targets.Length
            || targets.Any(t => t.ItemType <= 0 || t.Count is < 1 or > 9999 || t.Npcs.Any(id => id <= 0)
                || t.TileFrameX is < 0 or > short.MaxValue || t.TileFrameY is < 0 or > short.MaxValue))
            throw new ArgumentException("需要1..8种不重复的物品，正数物品/NPC编号，目标数量1..9999。");
    }

    public static async Task<JsonObject> Run(GatherTarget[] targets, int seconds, IGatherWorld world, CancellationToken token, GatherArea area = null)
    {
        Validate(targets, area);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        var timer = Stopwatch.StartNew();
        var attempts = new Dictionary<string, int>();
        var unreachable = new Dictionary<string, string>();
        var combatBlocked = new Dictionary<string, string>();
        var inventoryBefore = new Dictionary<int, int>();
        var unproductiveTiles = new Dictionary<int, JsonArray>();
        JsonObject scene = null, last = null;
        var blockedSources = new JsonArray();
        JsonNode sourceError = null;
        string outcome = "attempt_limit";
        int actions = 0, stalledActions = 0;
        int previousCount = 0;
        int Count(GatherTarget target) => scene?["counts"]?[target.ItemType.ToString()]?.GetValue<int>() ?? 0;
        try
        {
            for (int step = 0; ; step++)
            {
                scene = await world.Observe(budget.Token);
                if (step == 0)
                    foreach (var target in targets) inventoryBefore[target.ItemType] = Count(target);
                if (targets.All(t => Count(t) >= t.Count)) { outcome = "enough"; break; }
                if (scene["error"] != null)
                { outcome = scene["error"].ToString(); sourceError = scene["source_error"]?.DeepClone(); break; }
                var available = scene["candidates"].AsArray().OfType<JsonObject>()
                    .Where(c => area == null || area.Contains(c))
                    .Where(c => targets.Any(t => t.ItemType == c["item_type"].GetValue<int>() && Count(t) < t.Count))
                    .ToArray();
                if (last != null)
                {
                    int item = last["candidate"]["item_type"].GetValue<int>();
                    int actual = scene["counts"]?[item.ToString()]?.GetValue<int>() ?? 0;
                    stalledActions = actual > previousCount ? 0 : stalledActions + 1;
                    if (actual > previousCount) unproductiveTiles.Remove(item);
                    else if (last["candidate"]["kind"].ToString() == "tile"
                        && last["result"]?["outcome"]?.ToString() == "removed")
                    {
                        if (!unproductiveTiles.TryGetValue(item, out var evidence))
                            unproductiveTiles[item] = evidence = new JsonArray();
                        evidence.Add(new JsonObject { ["candidate"] = last["candidate"].DeepClone(),
                            ["count_before"] = previousCount, ["count_after"] = actual });
                        if (evidence.Count >= 3 && !available.Any(c => c["kind"].ToString() == "drop"
                            && c["item_type"].GetValue<int>() == item))
                        {
                            outcome = "source_needs_review";
                            sourceError = new JsonObject { ["item_type"] = item, ["observations"] = evidence.DeepClone(),
                                ["detail"] = "连续移除方块但目标库存未增长，现场也没有目标掉落物。先核查来源、变体和拾取条件；这不是该来源永远无效的证明，不要原样重复整批采集。",
                                ["recovery"] = SourceReview(targets.Single(t => t.ItemType == item)) };
                            break;
                        }
                    }
                }
                if (stalledActions >= 32) break;
                bool CanPickup(JsonObject c) => c["can_pickup"]?.GetValue<bool>()
                    ?? scene["item_space"]?[c["item_type"].ToString()]?.GetValue<bool>() ?? true;
                var candidate = available
                    .Where(CanPickup)
                    .Where(c => c["blocked_reason"] == null)
                    .Where(c => !unreachable.ContainsKey(c["id"].ToString()))
                    .Where(c => !combatBlocked.ContainsKey(c["id"].ToString()))
                    .Where(c => attempts.GetValueOrDefault(c["id"].ToString()) < 2)
                    .OrderBy(c => c["distance"].GetValue<float>() + (c["kind"].ToString() == "drop" ? -1000 : c["kind"].ToString() == "npc" ? -20 : 0))
                    .FirstOrDefault();
                if (candidate == null)
                {
                    outcome = available.Any(c => !CanPickup(c)) || targets.Any(t => Count(t) < t.Count
                        && scene["item_space"]?[t.ItemType.ToString()]?.GetValue<bool>() == false)
                        ? "inventory_full" : available.Length == 0 ? "source_unavailable"
                        : available.All(c => unreachable.GetValueOrDefault(c["id"].ToString()) == "early_underground_desert_destination")
                            ? "early_underground_desert_destination"
                        : available.All(c => unreachable.ContainsKey(c["id"].ToString())) ? "navigation_no_progress"
                        : available.All(c => c["blocked_reason"] != null)
                            ? available[0]["blocked_reason"]?.ToString() ?? "source_blocked"
                            : available.Select(c => combatBlocked.GetValueOrDefault(c["id"].ToString())).FirstOrDefault(reason => reason != null) ?? "attempt_limit";
                    foreach (var source in available.Where(c => !unreachable.ContainsKey(c["id"].ToString()) && !combatBlocked.ContainsKey(c["id"].ToString())))
                        blockedSources.Add(new JsonObject { ["candidate"] = source.DeepClone(),
                            ["reason"] = !CanPickup(source) ? "inventory_full"
                                : source["blocked_reason"] != null
                                    ? source["blocked_reason"]?.ToString() ?? "source_blocked" : "attempt_limit",
                            ["attempts"] = attempts.GetValueOrDefault(source["id"].ToString()) });
                    break;
                }
                string id = candidate["id"].ToString();
                attempts[id] = attempts.GetValueOrDefault(id) + 1;
                actions++;
                previousCount = scene["counts"]?[candidate["item_type"].ToString()]?.GetValue<int>() ?? 0;
                last = new JsonObject { ["candidate"] = candidate.DeepClone() };
                last["result"] = await world.Act(candidate, budget.Token);
                if (last["result"]?["outcome"]?.ToString() == "inventory_full")
                { outcome = "inventory_full"; break; }
                var navigation = last["result"]?["navigation"];
                string blocked = navigation?["error"]?.ToString() == "early_underground_desert_destination"
                    ? "early_underground_desert_destination"
                    : navigation?["outcome"]?.ToString() == "no_route" ? "navigation_no_route" : null;
                if (blocked != null)
                {
                    unreachable[id] = blocked;
                    blockedSources.Add(new JsonObject { ["candidate"] = candidate.DeepClone(),
                        ["reason"] = blocked, ["attempts"] = attempts[id], ["result"] = last["result"].DeepClone() });
                    continue;
                }
                if (last["result"]?["navigation"]?["outcome"]?.ToString() == "no_progress"
                    || candidate["kind"].ToString() == "drop" && last["result"]?["outcome"]?.ToString() == "no_progress")
                {
                    foreach (var source in available.Where(c => c["id"].ToString() == id
                        || candidate["kind"].ToString() == "tile" && c["kind"].ToString() == "tile"
                            && c["item_type"].ToString() == candidate["item_type"].ToString()
                            && Math.Abs(c["x"].GetValue<int>() - candidate["x"].GetValue<int>()) <= 1
                            && Math.Abs(c["y"].GetValue<int>() - candidate["y"].GetValue<int>()) <= 1))
                        unreachable[source["id"].ToString()] = "navigation_no_progress";
                    blockedSources.Add(new JsonObject { ["candidate"] = candidate.DeepClone(),
                        ["reason"] = "navigation_no_progress", ["attempts"] = attempts[id],
                        ["result"] = last["result"].DeepClone() });
                    continue;
                }
                if (last["result"]?["outcome"]?.ToString() == "no_progress")
                {
                    if (candidate["kind"].ToString() == "tile" && attempts[id] < 2
                        && last["result"]?["reason"]?.ToString() == "out_of_reach"
                        && last["result"]?["target"]?["has_tile"]?.GetValue<bool>() == true) continue;
                    outcome = "collection_no_progress"; break;
                }
                if (last["result"]?["error"]?.ToString() == "collect_target_out_of_mining_range")
                { outcome = "collection_out_of_range"; break; }
                if (candidate["kind"]?.ToString() == "npc" && last["result"] is JsonObject combat
                    && combat["error"] == null && combat["outcome"] == null
                    && combat["target_present"]?.GetValue<bool>() == true && combat["self_dead"]?.GetValue<bool>() == false
                    && combat["hp_before"] is JsonValue beforeHp && beforeHp.TryGetValue<int>(out int before)
                    && combat["hp_after"] is JsonValue afterHp && afterHp.TryGetValue<int>(out int after)
                    && after > 0 && after < before)
                    attempts[id] = 0;
                if (last["result"]?["outcome"]?.ToString() is "combat_obstructed" or "combat_out_of_range" or "combat_no_progress")
                {
                    combatBlocked[id] = last["result"]["outcome"].ToString();
                    blockedSources.Add(new JsonObject { ["candidate"] = candidate.DeepClone(),
                        ["reason"] = combatBlocked[id], ["attempts"] = attempts[id], ["result"] = last["result"].DeepClone() });
                    continue;
                }
                if (last["result"]?["outcome"]?.ToString() == "unsafe"
                    || last["result"]?["navigation"]?["outcome"]?.ToString() == "unsafe")
                { outcome = "unsafe"; break; }
            }
        }
        finally
        {
            if (!token.IsCancellationRequested) await world.Stop(token);
        }
        scene = await world.Observe(token);
        bool enough = targets.All(t => Count(t) >= t.Count);
        var combatCandidate = blockedSources.LastOrDefault(source => source["reason"]?.ToString() == outcome)?["candidate"]
            ?? (last?["candidate"]?["kind"]?.ToString() == "npc" ? last["candidate"] : scene?["candidates"]?.AsArray()
                .FirstOrDefault(candidate => candidate["kind"]?.ToString() == "npc"));
        if (!enough && outcome == "source_unavailable" && area != null
            && targets.Any(t => Count(t) > inventoryBefore[t.ItemType])) outcome = "source_area_exhausted";
        return new JsonObject { ["ok"] = enough, ["outcome"] = enough ? "enough" : outcome,
            ["detail"] = outcome == "source_area_exhausted"
                ? "本轮已获得部分目标物品，指定source_area内当前已无符合筛选条件的候选。保留实际库存进度，选择下一处来源或调整source_area后继续剩余目标。"
                : outcome == "inventory_full" ? "目标物品当前无法装入背包。使用inventory_review核对物品，整理出空间后续接剩余目标；实际库存进度已保留。" : null,
            ["item_space"] = scene["item_space"]?.DeepClone(),
            ["source_rules"] = DescribeSources(targets),
            ["source_area"] = area?.Snapshot(),
            ["actions"] = actions, ["elapsed_ms"] = timer.ElapsedMilliseconds, ["last_result"] = last,
            ["source_error"] = sourceError,
            ["recovery_skill"] = outcome is "source_unavailable" or "source_area_exhausted"
                ? new JsonObject { ["tool"] = "script_program", ["operation"] = "example", ["name"] = "gather_route" }
                : outcome is "combat_obstructed" or "combat_out_of_range" or "combat_no_progress"
                    ? new JsonObject { ["tool"] = "script_program", ["operation"] = "get", ["name"] = "combat_approach",
                        ["input"] = new JsonObject { ["npc"] = combatCandidate?["who"]?.DeepClone(),
                            ["npc_type"] = combatCandidate?["npc_type"]?.DeepClone() } } : null,
            ["blocked_sources"] = blockedSources,
            ["targets"] = new JsonArray(targets.Select(t => (JsonNode)new JsonObject
                { ["item_type"] = t.ItemType, ["wanted"] = t.Count, ["actual"] = Count(t),
                    ["inventory_before"] = inventoryBefore[t.ItemType], ["inventory_delta"] = Count(t) - inventoryBefore[t.ItemType],
                    ["remaining"] = Math.Max(0, t.Count - Count(t)), ["met"] = Count(t) >= t.Count }).ToArray()) };
    }

    static JsonObject SourceReview(GatherTarget target)
    {
        return new JsonObject
        {
            ["identify_item"] = new JsonObject { ["tool"] = "game_catalog", ["args"] = new JsonObject
                { ["kind"] = "item", ["name"] = target.ItemType.ToString() } },
            ["example"] = new JsonObject { ["tool"] = "script_program",
                ["args"] = new JsonObject { ["operation"] = "example", ["name"] = "sample_tile_variants" } },
            ["item_type"] = target.ItemType, ["wanted"] = target.Count,
            ["detail"] = "旧tile_names是调用者假设，不是已知来源。先查目标物品的Wiki页面及nearby_listed_tiles，区分采集对象与生长基底，再按现场变体和emission选择。可修改试采示例，用原action_query句柄恢复；是否试采、跳过或换区由你决定。没有库存增量的样本不是永久排除规则。"
        };
    }
}
