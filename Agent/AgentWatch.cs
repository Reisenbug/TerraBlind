using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace TerraBlind;

public sealed class AgentWatch
{
    readonly HashSet<string> events = new(), seen = new();
    readonly Queue<JsonObject> pending = new();
    readonly List<JsonObject> history = new();
    int issued;
    int samples;
    bool configured;
    JsonObject lastObservation;
    public int Radius { get; private set; } = 25;
    public int HealthBelow { get; private set; } = 80;
    public bool HasPending => pending.Count > 0;
    public bool Armed => events.Count > 0;
    public int Samples => samples;
    public JsonObject LastObservation => lastObservation?.DeepClone() as JsonObject;
    public void IgnorePriorActions(JsonArray actions)
    { foreach (var action in actions) seen.Add("action_finished:" + action["handle"] + ":" + action["attempt"]); }
    public void EnsureDefaults()
    {
        events.UnionWith(new[] { "action_finished", "low_health", "player_died", "inventory_full" });
        if (!configured) events.Add("nearby_chest");
    }

    public JsonObject Configure(JsonObject args)
    {
        var requested = args["events"]?.AsArray().Select(x => x.GetValue<string>()).ToArray()
            ?? throw new ArgumentException("需要events数组；空数组取消观察。");
        if (requested.Any(x => x is not ("nearby_chest" or "action_finished" or "low_health" or "player_died" or "inventory_full")))
            throw new ArgumentException("不支持的观察事件。");
        int radius = args["radius"]?.GetValue<int>() ?? 25, health = args["health_below"]?.GetValue<int>() ?? 80;
        if (radius is < 1 or > 60 || health is < 1 or > 500) throw new ArgumentException("radius须1..60，health_below须1..500。");
        events.Clear(); events.UnionWith(requested); Radius = radius; HealthBelow = health;
        configured = true;
        return Snapshot();
    }

    public void Observe(JsonObject state)
    {
        if (!Armed) return;
        samples++;
        lastObservation = new JsonObject { ["hp"] = state["hp"]?.DeepClone(), ["dead"] = state["dead"]?.DeepClone(),
            ["pos"] = state["pos"]?.DeepClone(), ["sample"] = samples, ["utc"] = DateTime.UtcNow.ToString("O") };
        void Emit(string kind, string key, JsonNode evidence)
        {
            if (!events.Contains(kind) || issued >= 6 && kind is not ("action_finished" or "player_died") || !seen.Add(kind + ":" + key)) return;
            var value = new JsonObject { ["id"] = ++issued, ["kind"] = kind, ["key"] = key,
                ["evidence"] = evidence?.DeepClone() };
            pending.Enqueue(value); history.Add((JsonObject)value.DeepClone());
        }
        if (state["dead"]?.GetValue<bool>() == true) Emit("player_died", "task", state);
        else if (state["hp"]?.GetValue<int>() < HealthBelow) Emit("low_health", "task", state);
        if (state["inventory_full"]?.GetValue<bool>() == true) Emit("inventory_full", "task", state);
        foreach (var chest in state["chests"]?.AsArray() ?? new JsonArray())
            Emit("nearby_chest", chest["x"] + "," + chest["y"], chest);
        foreach (var action in state["actions"]?.AsArray() ?? new JsonArray())
            if (action["status"]?.ToString() == "failed" || action["status"]?.ToString() == "completed" && action["inline"]?.GetValue<bool>() != true)
                Emit("action_finished", action["handle"] + ":" + action["attempt"], action);
    }

    public JsonArray Take()
    {
        var result = new JsonArray();
        while (pending.TryDequeue(out var item)) result.Add(item);
        return result;
    }
    public JsonObject Pending() => new() { ["events"] = new JsonArray(pending.Select(x => x.DeepClone()).ToArray()) };

    public JsonObject Request(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("决策点需要原因。");
        if (issued >= 6) throw new InvalidOperationException("本指令唤醒预算已用完，请报告阻塞或完成。");
        var value = new JsonObject { ["id"] = ++issued, ["kind"] = "decision_point", ["reason"] = reason };
        pending.Enqueue(value); history.Add((JsonObject)value.DeepClone());
        return (JsonObject)value.DeepClone();
    }

    public JsonObject Snapshot() => new()
    {
        ["events"] = new JsonArray(events.Select(x => (JsonNode)JsonValue.Create(x)).ToArray()),
        ["radius"] = Radius, ["health_below"] = HealthBelow, ["wake_count"] = issued, ["wake_limit"] = 6,
        ["pending"] = new JsonArray(pending.Select(x => x.DeepClone()).ToArray()),
        ["history"] = new JsonArray(history.Select(x => x.DeepClone()).ToArray()), ["scope"] = "current_instruction",
        ["samples"] = samples, ["last_observation"] = lastObservation?.DeepClone()
    };

    public void Reset()
    { events.Clear(); seen.Clear(); pending.Clear(); history.Clear(); issued = samples = 0; configured = false; lastObservation = null; }
    public void End() { events.Clear(); pending.Clear(); }
}
