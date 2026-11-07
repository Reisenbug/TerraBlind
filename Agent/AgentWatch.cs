using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace TerraBlind;

public sealed class AgentWatch
{
    readonly Func<JsonObject, JsonObject> assessOpportunity;
    JsonObject navigation;
    public AgentWatch(Func<JsonObject, JsonObject> assessOpportunity = null) => this.assessOpportunity = assessOpportunity;
    readonly HashSet<string> events = new(), seen = new();
    readonly HashSet<string> visibleDrops = new();
    readonly HashSet<int> tileTypes = new();
    readonly HashSet<string> passive = new();
    readonly HashSet<int> advised = new();
    readonly HashSet<string> verifiedCompletions = new();
    readonly Queue<JsonObject> pending = new();
    readonly List<JsonObject> history = new();
    int issued, wakeCount;
    bool localDelivery;
    readonly HashSet<Guid> scriptReaders = new();
    public Guid BeginScriptDelivery()
    {
        var reader = Guid.NewGuid();
        scriptReaders.Add(reader);
        return reader;
    }
    public void EndScriptDelivery(Guid reader)
    {
        if (scriptReaders.Remove(reader) && !LocalDelivery && passive.Contains("low_health"))
            seen.RemoveWhere(key => key.StartsWith("low_health:", StringComparison.Ordinal));
    }
    public bool LocalDelivery
    {
        get => localDelivery || scriptReaders.Count > 0;
        set
        {
            if (localDelivery && !value && passive.Contains("low_health"))
                seen.RemoveWhere(key => key.StartsWith("low_health:", StringComparison.Ordinal));
            localDelivery = value;
        }
    }
    int samples;
    bool configured;
    string currentChestTarget;
    JsonObject lastObservation;
    JsonObject itemCounts;
    public int Radius { get; private set; } = 25;
    public bool LootChestsInReach { get; private set; }
    int? healthOverride;
    int maxHealth = 100;
    public int HealthBelow => healthOverride ?? (int)Math.Ceiling(maxHealth * 0.3);
    public bool HasPending => pending.Count > 0;
    public bool HasHealthNotice => pending.Any(e => e["kind"]?.ToString() == "low_health");
    public JsonArray TakeHealthNotices()
    {
        var result = new JsonArray();
        int count = pending.Count;
        for (int i = 0; i < count; i++)
        {
            var item = pending.Dequeue();
            if (item["kind"]?.ToString() == "low_health") result.Add(item);
            else pending.Enqueue(item);
        }
        return result;
    }
    public int PendingCount => pending.Count;
    public bool HasInterrupting => pending.Any(e => e["wake_deferred"]?.GetValue<bool>() != true
        && e["kind"]?.ToString() != "low_health"
        && !(e["kind"]?.ToString() == "nearby_chest" && e["key"]?.ToString() == currentChestTarget)
        && !passive.Contains(e["kind"]?.ToString()));
    public bool Armed => events.Count > 0;
    public bool Subscribed(string kind) => events.Contains(kind);
    public bool WatchesTile(int type) => tileTypes.Contains(type);
    public int Samples => samples;
    public JsonObject LastObservation => lastObservation?.DeepClone() as JsonObject;
    public void IgnorePriorActions(JsonArray actions)
    { foreach (var action in actions) seen.Add("action_finished:" + action["handle"] + ":" + action["attempt"]); }
    public void EnsureDefaults()
    {
        if (!events.Contains("target_acquired")) passive.Add("target_acquired");
        events.UnionWith(new[] { "action_finished", "target_acquired", "low_health", "suffocating", "player_died", "inventory_full" });
        if (!configured)
        {
            events.Add("nearby_chest");
            passive.Add("nearby_chest");
        }
    }

    public void Resumed(string handle, int attempt)
    {
        foreach (var notice in pending)
            if (notice["kind"]?.ToString() is "action_finished" or "target_acquired"
                && notice["evidence"]?["handle"]?.ToString() == handle
                && notice["evidence"]?["attempt"]?.GetValue<int>() < attempt)
            {
                notice["wake_deferred"] = true;
                notice["deferred_reason"] = "superseded_action_attempt";
            }
    }

    public void AcknowledgeCompletion(JsonObject action, bool nodeVerified = false)
    {
        if (action["status"]?.ToString() != "completed" && !nodeVerified) return;
        string key = action["handle"] + ":" + action["attempt"];
        verifiedCompletions.Add(key);
        foreach (var notice in pending)
            if (notice["kind"]?.ToString() == "action_finished" && notice["key"]?.ToString() == key)
            {
                notice["wake_deferred"] = true;
                notice["deferred_reason"] = nodeVerified ? "node_completion_verified" : "completion_observed";
            }
    }

    public JsonObject Configure(JsonObject args)
    {
        var requested = args["events"]?.AsArray().Select(x => x.GetValue<string>()).ToArray()
            ?? throw new ArgumentException("需要events数组；空数组取消观察。");
        if (requested.Any(x => x is not ("nearby_tile" or "nearby_chest" or "nearby_drop" or "nearby_npc_killed" or "action_finished" or "target_acquired" or "low_health" or "suffocating" or "player_died" or "inventory_full")))
            throw new ArgumentException("不支持的观察事件。");
        int radius = args["radius"]?.GetValue<int>() ?? 25;
        int? health = args["health_below"]?.GetValue<int>();
        if (radius is < 1 or > 60 || health is < 1 or > 500) throw new ArgumentException("radius须1..60，health_below须1..500。");
        var types = args["tile_types"]?.AsArray().Select(x => x.GetValue<int>()).Distinct().ToArray() ?? Array.Empty<int>();
        if (types.Length > 64 || types.Any(x => x < 0 || x > ushort.MaxValue)
            || requested.Contains("nearby_tile") && types.Length == 0)
            throw new ArgumentException("nearby_tile需要1..64个tile_types方块编号，请通过game_catalog查询。");
        var passiveEvents = args["passive_events"]?.AsArray().Select(x => x.GetValue<string>()).ToArray() ?? Array.Empty<string>();
        if (passiveEvents.Any(x => !requested.Contains(x) || x is not ("nearby_tile" or "nearby_chest" or "nearby_drop" or "target_acquired" or "low_health")))
            throw new ArgumentException("passive_events仅支持已订阅的nearby_tile/nearby_chest/nearby_drop/target_acquired/low_health；low_health始终只通知，不暂停动作。");
        passive.Clear(); passive.UnionWith(passiveEvents);
        LootChestsInReach = requested.Contains("nearby_chest")
            && (args["loot_chests_in_reach"]?.GetValue<bool>() ?? LootChestsInReach);
        currentChestTarget = null;
        tileTypes.Clear(); tileTypes.UnionWith(types);
        events.Clear(); events.UnionWith(requested); Radius = radius; healthOverride = health;
        configured = true;
        return Snapshot();
    }

    public void Observe(JsonObject state)
    {
        if (!Armed) return;
        samples++;
        if (state["max_hp"]?.GetValue<int>() is > 0 and var maximum) maxHealth = maximum;
        lastObservation = new JsonObject { ["hp"] = state["hp"]?.DeepClone(), ["dead"] = state["dead"]?.DeepClone(),
            ["max_hp"] = maxHealth,
            ["suffocating"] = state["suffocating"]?.DeepClone(),
            ["pos"] = state["pos"]?.DeepClone(), ["sample"] = samples, ["utc"] = DateTime.UtcNow.ToString("O") };
        var running = state["actions"]?.AsArray().LastOrDefault(action => action["status"]?.ToString() == "running");
        navigation = running?["tool"]?.ToString() == "nav_to" && running["args"] is JsonObject destination
            ? new JsonObject { ["handle"] = running["handle"]?.DeepClone(),
                ["destination"] = new JsonObject { ["x"] = destination["x"]?.DeepClone(), ["y"] = destination["y"]?.DeepClone() } } : null;
        currentChestTarget = !configured && running?["tool"]?.ToString() == "collect_treasure"
            && running["args"]?["x"] != null && running["args"]?["y"] != null
            ? running["args"]["x"] + "," + running["args"]["y"] : null;
        string dangerKey = running == null ? "task" : running["handle"] + ":" + running["attempt"];
        if (state["item_counts"] is JsonObject current)
        {
            if (itemCounts != null && running?["tool"]?.ToString() == "gather")
                foreach (var target in running["args"]?["targets"]?.AsArray() ?? new JsonArray())
                {
                    string type = target["item_type"].ToString();
                    int before = itemCounts[type]?.GetValue<int>() ?? 0, after = current[type]?.GetValue<int>() ?? 0;
                    if (after > before) Emit("target_acquired", type + ":" + samples, new JsonObject
                    { ["handle"] = running["handle"]?.DeepClone(), ["item_type"] = target["item_type"].DeepClone(),
                        ["attempt"] = running["attempt"]?.DeepClone(),
                        ["before"] = before, ["after"] = after, ["gained"] = after - before });
                }
            itemCounts = (JsonObject)current.DeepClone();
        }
        if (state["dead"]?.GetValue<bool>() == true) Emit("player_died", dangerKey, state);
        else
        {
            if (state["hp"]?.GetValue<int>() < HealthBelow) Emit("low_health", dangerKey, state);
            else if (state["hp"]?.GetValue<int>() >= HealthBelow)
                seen.RemoveWhere(key => key.StartsWith("low_health:", StringComparison.Ordinal));
            if (state["suffocating"]?.GetValue<bool>() == true) Emit("suffocating", dangerKey, state);
            else if (state["suffocating"]?.GetValue<bool>() == false)
                seen.RemoveWhere(key => key.StartsWith("suffocating:", StringComparison.Ordinal));
        }
        if (state["inventory_full"]?.GetValue<bool>() == true) Emit("inventory_full", "task", state);
        foreach (var chest in state["chests"]?.AsArray() ?? new JsonArray())
            Emit("nearby_chest", chest["x"] + "," + chest["y"], chest);
        foreach (var tile in state["tiles"]?.AsArray() ?? new JsonArray())
            if (tile["tile_type"] is JsonValue type && tileTypes.Contains(type.GetValue<int>()))
                Emit("nearby_tile", tile["tile_type"] + ":" + tile["x"] + "," + tile["y"], tile);
        if (state["drops"] is JsonArray drops)
        {
            string DropKey(JsonNode drop) => drop["who"] + ":" + drop["item_type"]
                + (drop["generation"] == null ? "" : ":" + drop["generation"]);
            var currentDrops = drops.Select(DropKey).ToHashSet();
            foreach (string vanished in visibleDrops.Except(currentDrops)) seen.Remove("nearby_drop:" + vanished);
            visibleDrops.Clear(); visibleDrops.UnionWith(currentDrops);
            foreach (var drop in drops) Emit("nearby_drop", DropKey(drop), drop);
        }
        var actions = state["actions"]?.AsArray() ?? new JsonArray();
        foreach (var action in actions)
            if (action["status"]?.ToString() == "failed" || action["status"]?.ToString() == "completed" && action["inline"]?.GetValue<bool>() != true)
            {
                if (action["status"]?.ToString() == "completed" && actions.Any(next =>
                    next["after_handle"]?.ToString() == action["handle"]?.ToString()
                    && next["status"]?.ToString() is "running" or "completed"))
                    AcknowledgeCompletion(action.AsObject());
                Emit("action_finished", action["handle"] + ":" + action["attempt"], action);
            }
    }

    public void Emit(string kind, string key, JsonNode evidence)
    {
        if (!events.Contains(kind) || !seen.Add(kind + ":" + key)) return;
        bool attackFinished = kind == "action_finished" && evidence?["tool"]?.ToString() == "fight";
        bool verified = kind == "action_finished" && verifiedCompletions.Contains(key);
        bool deferred = verified || attackFinished || !LocalDelivery && !passive.Contains(kind) && wakeCount >= 6
            && kind is not ("action_finished" or "target_acquired" or "low_health" or "suffocating" or "player_died");
        if (!LocalDelivery && !passive.Contains(kind) && !deferred && kind != "low_health"
            && !(kind == "nearby_chest" && key == currentChestTarget)) wakeCount++;
        var value = new JsonObject { ["id"] = ++issued, ["kind"] = kind, ["key"] = key, ["evidence"] = evidence?.DeepClone() };
        if (kind is "nearby_chest" or "nearby_tile" or "nearby_drop") value["navigation"] = navigation?.DeepClone();
        if (deferred) value["wake_deferred"] = true;
        if (verified) value["deferred_reason"] = "completion_observed";
        pending.Enqueue(value); history.Add((JsonObject)value.DeepClone());
    }

    public JsonArray Take() => Take(int.MaxValue);
    public JsonArray Take(int maxEvents)
    {
        var result = new JsonArray();
        if (maxEvents < pending.Count)
        {
            double Distance(JsonObject item)
            {
                if (lastObservation?["pos"]?["x"] == null || lastObservation["pos"]["y"] == null
                    || item["kind"]?.ToString() is not ("nearby_chest" or "nearby_tile" or "nearby_drop")
                    || item["evidence"]?["x"] == null || item["evidence"]["y"] == null) return 0;
                double Number(JsonNode value) => double.Parse(value.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);
                double dx = Number(item["evidence"]["x"]) - Number(lastObservation["pos"]["x"]) / 16;
                double dy = Number(item["evidence"]["y"]) - Number(lastObservation["pos"]["y"]) / 16;
                return dx * dx + dy * dy;
            }
            var ordered = pending.OrderBy(ReviewPriority)
                .ThenBy(Distance).ToArray();
            pending.Clear();
            foreach (var item in ordered) pending.Enqueue(item);
        }
        int assessed = 0;
        while (result.Count < maxEvents && pending.TryDequeue(out var item))
        {
            if (assessOpportunity != null && item["kind"]?.ToString() is "nearby_chest" or "nearby_tile" or "nearby_drop")
            {
                if (assessed++ < 6) item["assessment"] = assessOpportunity(item);
                else item["assessment_deferred"] = true;
            }
            result.Add(item);
        }
        return result;
    }
    static int ReviewPriority(JsonObject item) => item["kind"]?.ToString() is "low_health" or "suffocating" or "player_died" or "decision_point" or "execution_gap"
        || item["kind"]?.ToString() == "action_finished" && item["evidence"]?["status"]?.ToString() == "failed" ? 0 : 1;

    public JsonObject Pending() => new() { ["events"] = new JsonArray(pending.OrderBy(ReviewPriority).Take(8).Select(x => x.DeepClone()).ToArray()),
        ["remaining_events"] = Math.Max(0, pending.Count - 8) };

    internal JsonArray Advisories()
    {
        var result = new JsonArray();
        foreach (var item in pending.Where(e => e["kind"]?.ToString() is "nearby_chest" or "nearby_tile" or "nearby_drop"
            && !advised.Contains(e["id"].GetValue<int>())).OrderBy(e => e["kind"]?.ToString() == "nearby_chest" ? 0 : 1).Take(4))
        {
            advised.Add(item["id"].GetValue<int>());
            result.Add(new JsonObject { ["id"] = item["id"]?.DeepClone(), ["kind"] = item["kind"]?.DeepClone(),
                ["key"] = item["key"]?.DeepClone(), ["evidence"] = item["evidence"]?.DeepClone(),
                ["navigation"] = item["navigation"]?.DeepClone() });
        }
        return result;
    }

    public JsonObject Reconsider(string kind, string key, string reason)
    {
        if (kind is not ("nearby_tile" or "nearby_chest" or "nearby_drop") || string.IsNullOrWhiteSpace(key)
            || string.IsNullOrWhiteSpace(reason) || reason.Length > 500)
            throw new ArgumentException("需要机会事件kind/key及1..500字符的重新考虑原因。");
        if (!events.Contains(kind)) throw new ArgumentException("该事件尚未订阅。");
        if (pending.Any(e => e["kind"]?.ToString() == kind && e["key"]?.ToString() == key))
            return new JsonObject { ["status"] = "already_pending", ["kind"] = kind, ["key"] = key };
        if (!seen.Remove(kind + ":" + key)) throw new ArgumentException("没有已处理的对应机会事件。");
        return new JsonObject { ["status"] = "rearmed", ["kind"] = kind, ["key"] = key, ["reason"] = reason,
            ["detail"] = "下次实际观察仍发现该机会时重新通知；不复用旧现场，不重置模型唤醒额度。" };
    }

    public JsonObject Request(string reason, JsonObject evidence = null)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("决策点需要原因。");
        if (evidence?.ToJsonString(AgentTools.ModelJson).Length > 32000)
            throw new ArgumentException("决策证据超过工具参数的32000字符上限，请保留关键实测字段和句柄。");
        if (wakeCount >= 6) throw new InvalidOperationException("本指令唤醒预算已用完，请报告阻塞或完成。");
        wakeCount++;
        var value = new JsonObject { ["id"] = ++issued, ["kind"] = "decision_point", ["reason"] = reason };
        if (evidence != null) value["reported_evidence"] = evidence.DeepClone();
        pending.Enqueue(value); history.Add((JsonObject)value.DeepClone());
        return (JsonObject)value.DeepClone();
    }

    internal void ExecutionFault(JsonObject evidence)
    {
        if (pending.Any(e => e["kind"]?.ToString() == "execution_gap")) return;
        var value = new JsonObject { ["id"] = ++issued, ["kind"] = "execution_gap",
            ["reported_evidence"] = evidence.DeepClone() };
        pending.Enqueue(value);
        history.Add((JsonObject)value.DeepClone());
    }

    public JsonObject Summary() => new()
    {
        ["loot_chests_in_reach"] = LootChestsInReach,
        ["passive_events"] = new JsonArray(passive.Select(x => (JsonNode)JsonValue.Create(x)).ToArray()),
        ["tile_types"] = new JsonArray(tileTypes.Select(x => (JsonNode)JsonValue.Create(x)).ToArray()),
        ["events"] = new JsonArray(events.Select(x => (JsonNode)JsonValue.Create(x)).ToArray()),
        ["radius"] = Radius, ["health_below"] = HealthBelow, ["wake_count"] = wakeCount, ["wake_limit"] = 6, ["event_count"] = issued,
        ["pending"] = new JsonArray(pending.Take(12).Select(value => value.DeepClone()).ToArray()),
        ["pending_count"] = pending.Count, ["pending_truncated"] = pending.Count > 12,
        ["pending_detail_request"] = new JsonObject { ["tool"] = "script_events",
            ["args"] = new JsonObject { ["wait_ms"] = 0, ["max_events"] = 16 } },
        ["samples"] = samples, ["last_observation"] = LastObservation,
        ["history_count"] = history.Count, ["history_truncated"] = history.Count > 12,
        ["history_detail_request"] = new JsonObject { ["tool"] = "get_state", ["args"] = new JsonObject { ["include_watch_history"] = true } },
        ["history"] = new JsonArray(history.TakeLast(12).Select(value => (JsonNode)new JsonObject
            { ["id"] = value["id"]?.DeepClone(), ["kind"] = value["kind"]?.DeepClone(), ["key"] = value["key"]?.DeepClone() }).ToArray())
    };

    public JsonObject Snapshot() => new()
    {
        ["loot_chests_in_reach"] = LootChestsInReach,
        ["passive_events"] = new JsonArray(passive.Select(x => (JsonNode)JsonValue.Create(x)).ToArray()),
        ["tile_types"] = new JsonArray(tileTypes.Select(x => (JsonNode)JsonValue.Create(x)).ToArray()),
        ["events"] = new JsonArray(events.Select(x => (JsonNode)JsonValue.Create(x)).ToArray()),
        ["radius"] = Radius, ["health_below"] = HealthBelow, ["wake_count"] = wakeCount, ["wake_limit"] = 6, ["event_count"] = issued,
        ["pending"] = new JsonArray(pending.Select(x => x.DeepClone()).ToArray()),
        ["history"] = new JsonArray(history.Select(x => x.DeepClone()).ToArray()), ["scope"] = "current_instruction",
        ["samples"] = samples, ["last_observation"] = lastObservation?.DeepClone()
    };

    public void Reset()
    { events.Clear(); seen.Clear(); visibleDrops.Clear(); tileTypes.Clear(); passive.Clear(); advised.Clear(); verifiedCompletions.Clear(); pending.Clear(); history.Clear(); scriptReaders.Clear(); issued = wakeCount = samples = 0; configured = LocalDelivery = LootChestsInReach = false; lastObservation = itemCounts = null; healthOverride = null; maxHealth = 100; currentChestTarget = null; navigation = null; }
    public void End() { events.Clear(); pending.Clear(); LootChestsInReach = false; }
}
