using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace TerraBlind;

public sealed class AgentWatch
{
    readonly HashSet<string> events = new(), seen = new();
    readonly HashSet<string> visibleDrops = new();
    readonly HashSet<int> tileTypes = new();
    readonly HashSet<string> passive = new();
    readonly Queue<JsonObject> pending = new();
    readonly List<JsonObject> history = new();
    int issued, wakeCount;
    bool localDelivery;
    public bool LocalDelivery
    {
        get => localDelivery;
        set
        {
            if (localDelivery && !value && passive.Contains("low_health"))
                seen.RemoveWhere(key => key.StartsWith("low_health:", StringComparison.Ordinal));
            localDelivery = value;
        }
    }
    int samples;
    bool configured;
    JsonObject lastObservation;
    JsonObject itemCounts;
    public int Radius { get; private set; } = 25;
    public int HealthBelow { get; private set; } = 80;
    public bool HasPending => pending.Count > 0;
    public bool HasInterrupting => pending.Any(e => !passive.Contains(e["kind"]?.ToString())
        || e["kind"]?.ToString() == "low_health" && !LocalDelivery);
    public bool Armed => events.Count > 0;
    public bool Subscribed(string kind) => events.Contains(kind);
    public bool WatchesTile(int type) => tileTypes.Contains(type);
    public int Samples => samples;
    public JsonObject LastObservation => lastObservation?.DeepClone() as JsonObject;
    public void IgnorePriorActions(JsonArray actions)
    { foreach (var action in actions) seen.Add("action_finished:" + action["handle"] + ":" + action["attempt"]); }
    public void EnsureDefaults()
    {
        events.UnionWith(new[] { "action_finished", "target_acquired", "low_health", "suffocating", "player_died", "inventory_full" });
        if (!configured)
        {
            events.Add("nearby_chest");
            passive.Add("nearby_chest");
        }
    }

    public JsonObject Configure(JsonObject args)
    {
        var requested = args["events"]?.AsArray().Select(x => x.GetValue<string>()).ToArray()
            ?? throw new ArgumentException("需要events数组；空数组取消观察。");
        if (requested.Any(x => x is not ("nearby_tile" or "nearby_chest" or "nearby_drop" or "nearby_npc_killed" or "action_finished" or "target_acquired" or "low_health" or "suffocating" or "player_died" or "inventory_full")))
            throw new ArgumentException("不支持的观察事件。");
        int radius = args["radius"]?.GetValue<int>() ?? 25, health = args["health_below"]?.GetValue<int>() ?? 80;
        if (radius is < 1 or > 60 || health is < 1 or > 500) throw new ArgumentException("radius须1..60，health_below须1..500。");
        var types = args["tile_types"]?.AsArray().Select(x => x.GetValue<int>()).Distinct().ToArray() ?? Array.Empty<int>();
        if (types.Length > 64 || types.Any(x => x < 0 || x > ushort.MaxValue)
            || requested.Contains("nearby_tile") && types.Length == 0)
            throw new ArgumentException("nearby_tile需要1..64个tile_types方块编号，请通过game_catalog查询。");
        var passiveEvents = args["passive_events"]?.AsArray().Select(x => x.GetValue<string>()).ToArray() ?? Array.Empty<string>();
        if (passiveEvents.Any(x => !requested.Contains(x) || x is not ("nearby_tile" or "nearby_chest" or "nearby_drop" or "low_health")))
            throw new ArgumentException("passive_events仅支持已订阅的nearby_tile/nearby_chest/nearby_drop/low_health；low_health仅在本地JS执行期间不强制中断。");
        passive.Clear(); passive.UnionWith(passiveEvents);
        tileTypes.Clear(); tileTypes.UnionWith(types);
        events.Clear(); events.UnionWith(requested); Radius = radius; HealthBelow = health;
        configured = true;
        return Snapshot();
    }

    public void Observe(JsonObject state)
    {
        if (!Armed) return;
        samples++;
        lastObservation = new JsonObject { ["hp"] = state["hp"]?.DeepClone(), ["dead"] = state["dead"]?.DeepClone(),
            ["suffocating"] = state["suffocating"]?.DeepClone(),
            ["pos"] = state["pos"]?.DeepClone(), ["sample"] = samples, ["utc"] = DateTime.UtcNow.ToString("O") };
        var running = state["actions"]?.AsArray().LastOrDefault(action => action["status"]?.ToString() == "running");
        if (!configured)
        {
            if (running == null) passive.Add("nearby_chest");
            else passive.Remove("nearby_chest");
        }
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
            var currentDrops = drops.Select(drop => drop["who"] + ":" + drop["item_type"]).ToHashSet();
            foreach (string vanished in visibleDrops.Except(currentDrops)) seen.Remove("nearby_drop:" + vanished);
            visibleDrops.Clear(); visibleDrops.UnionWith(currentDrops);
            foreach (var drop in drops) Emit("nearby_drop", drop["who"] + ":" + drop["item_type"], drop);
        }
        foreach (var action in state["actions"]?.AsArray() ?? new JsonArray())
            if (action["status"]?.ToString() == "failed" || action["status"]?.ToString() == "completed" && action["inline"]?.GetValue<bool>() != true)
                Emit("action_finished", action["handle"] + ":" + action["attempt"], action);
    }

    public void Emit(string kind, string key, JsonNode evidence)
    {
        if (!events.Contains(kind) || !LocalDelivery && !passive.Contains(kind) && wakeCount >= 6 && kind is not ("action_finished" or "target_acquired" or "low_health" or "suffocating" or "player_died") || !seen.Add(kind + ":" + key)) return;
        if (!LocalDelivery && !passive.Contains(kind)) wakeCount++;
        var value = new JsonObject { ["id"] = ++issued, ["kind"] = kind, ["key"] = key, ["evidence"] = evidence?.DeepClone() };
        pending.Enqueue(value); history.Add((JsonObject)value.DeepClone());
    }

    public JsonArray Take()
    {
        var result = new JsonArray();
        while (pending.TryDequeue(out var item)) result.Add(item);
        return result;
    }
    public JsonObject Pending() => new() { ["events"] = new JsonArray(pending.Select(x => x.DeepClone()).ToArray()) };

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

    public JsonObject Summary() => new()
    {
        ["passive_events"] = new JsonArray(passive.Select(x => (JsonNode)JsonValue.Create(x)).ToArray()),
        ["tile_types"] = new JsonArray(tileTypes.Select(x => (JsonNode)JsonValue.Create(x)).ToArray()),
        ["events"] = new JsonArray(events.Select(x => (JsonNode)JsonValue.Create(x)).ToArray()),
        ["radius"] = Radius, ["health_below"] = HealthBelow, ["wake_count"] = wakeCount, ["wake_limit"] = 6, ["event_count"] = issued,
        ["pending"] = Pending()["events"].DeepClone(), ["samples"] = samples, ["last_observation"] = LastObservation,
        ["history_count"] = history.Count, ["history_truncated"] = history.Count > 12,
        ["history_detail_request"] = new JsonObject { ["tool"] = "get_state", ["args"] = new JsonObject { ["include_watch_history"] = true } },
        ["history"] = new JsonArray(history.TakeLast(12).Select(value => (JsonNode)new JsonObject
            { ["id"] = value["id"]?.DeepClone(), ["kind"] = value["kind"]?.DeepClone(), ["key"] = value["key"]?.DeepClone() }).ToArray())
    };

    public JsonObject Snapshot() => new()
    {
        ["passive_events"] = new JsonArray(passive.Select(x => (JsonNode)JsonValue.Create(x)).ToArray()),
        ["tile_types"] = new JsonArray(tileTypes.Select(x => (JsonNode)JsonValue.Create(x)).ToArray()),
        ["events"] = new JsonArray(events.Select(x => (JsonNode)JsonValue.Create(x)).ToArray()),
        ["radius"] = Radius, ["health_below"] = HealthBelow, ["wake_count"] = wakeCount, ["wake_limit"] = 6, ["event_count"] = issued,
        ["pending"] = new JsonArray(pending.Select(x => x.DeepClone()).ToArray()),
        ["history"] = new JsonArray(history.Select(x => x.DeepClone()).ToArray()), ["scope"] = "current_instruction",
        ["samples"] = samples, ["last_observation"] = lastObservation?.DeepClone()
    };

    public void Reset()
    { events.Clear(); seen.Clear(); visibleDrops.Clear(); tileTypes.Clear(); passive.Clear(); pending.Clear(); history.Clear(); issued = wakeCount = samples = 0; configured = LocalDelivery = false; lastObservation = itemCounts = null; }
    public void End() { events.Clear(); pending.Clear(); }
}
