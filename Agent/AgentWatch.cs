using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace TerraBlind;

public sealed class AgentWatch
{
    readonly Func<JsonObject, JsonObject> assessOpportunity;
    readonly Func<long> clock;
    long nextOpportunityReview;
    JsonObject navigation;
    public AgentWatch(Func<JsonObject, JsonObject> assessOpportunity = null, Func<long> clock = null)
    {
        this.assessOpportunity = assessOpportunity;
        this.clock = clock ?? (() => Environment.TickCount64);
    }
    readonly HashSet<string> events = new(), seen = new();
    readonly HashSet<string> visibleDrops = new();
    readonly HashSet<string> visibleBosses = new();
    readonly HashSet<int> tileTypes = new();
    readonly HashSet<string> policyEvents = new();
    readonly HashSet<int> policyTiles = new();
    int radius = 25, policyRadius;
    bool lootChests;
    bool? policyLootChests;
    internal void SetPolicyWatch(JsonObject args)
    {
        var validated = new AgentWatch();
        if (args != null) validated.Configure(args);
        policyEvents.Clear(); policyEvents.UnionWith(validated.events);
        policyTiles.Clear(); policyTiles.UnionWith(validated.tileTypes);
        policyRadius = args == null ? 0 : validated.Radius;
        policyLootChests = args?["loot_chests_in_reach"] == null ? null : validated.LootChestsInReach;
    }
    readonly HashSet<string> passive = new();
    readonly HashSet<string> background = new();
    readonly HashSet<int> advised = new();
    readonly HashSet<string> verifiedCompletions = new();
    readonly HashSet<string> continuedAttempts = new();
    readonly Queue<JsonObject> pending = new();
    readonly List<JsonObject> history = new();
    int issued, wakeCount;
    bool localDelivery;
    readonly HashSet<Guid> scriptReaders = new();
    readonly Dictionary<Guid, HashSet<string>> scriptHandlers = new();
    internal bool HasScriptHandlers => scriptHandlers.Count > 0;
    internal void RegisterHandler(Guid reader, string kind)
    {
        if (!scriptHandlers.TryGetValue(reader, out var kinds)) scriptHandlers[reader] = kinds = new();
        kinds.Add(kind);
    }
    internal void EndHandlers(Guid reader) => scriptHandlers.Remove(reader);
    bool HandledByScript(JsonObject notice) => scriptHandlers.Values.Any(kinds => kinds.Contains(notice["kind"]?.ToString()));
    internal JsonArray TakeForScript(string[] kinds) => TakeMatching(1, notice => kinds.Contains(notice["kind"]?.ToString()));
    internal void ReturnToAgent(JsonObject notice)
    {
        string kind = notice["kind"]?.ToString();
        foreach (var kinds in scriptHandlers.Values) kinds.Remove(kind);
        if (kind is "nearby_tile" or "nearby_chest" or "nearby_drop")
        {
            passive.Add(kind); background.Add(kind);
            notice["wake_deferred"] = false;
            nextOpportunityReview = clock();
        }
        pending.Enqueue((JsonObject)notice.DeepClone());
    }
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
    long? honeySince;
    bool configured;
    string currentChestTarget;
    JsonObject lastObservation;
    JsonObject itemCounts;
    public int Radius { get => Math.Max(radius, policyRadius); private set => radius = value; }
    internal bool DefaultRouteLoot { get; set; }
    public bool LootChestsInReach { get => configured ? lootChests : policyLootChests ?? DefaultRouteLoot; private set => lootChests = value; }
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
    public bool HasInterrupting => pending.Any(Interrupting);
    internal bool HasModelInterrupting => pending.Any(ModelInterrupting);
    bool ModelInterrupting(JsonObject e) => !HandledByScript(e) && Interrupting(e)
        && (!LocalDelivery || e["kind"]?.ToString() is not ("nearby_tile" or "nearby_chest" or "nearby_drop"));
    internal bool HasRecoveryBlocker => pending.Any(RecoveryBlocker);
    bool RecoveryBlocker(JsonObject e) => !HandledByScript(e) && e["kind"]?.ToString() != "execution_gap" && Interrupting(e);
    bool Interrupting(JsonObject e) => e["wake_deferred"]?.GetValue<bool>() != true
        && e["kind"]?.ToString() != "low_health"
        && e["kind"]?.ToString() != "honey_slowdown"
        && e["kind"]?.ToString() != "inventory_full"
        && e["kind"]?.ToString() != "tactical_review"
        && e["kind"]?.ToString() != "boss_warning"
        && e["kind"]?.ToString() is not ("action_finished" or "target_acquired")
        && !(e["kind"]?.ToString() == "nearby_chest" && e["key"]?.ToString() == currentChestTarget)
        && !passive.Contains(e["kind"]?.ToString());
    public bool Armed => events.Count > 0 || policyEvents.Count > 0;
    public bool Subscribed(string kind) => events.Contains(kind) || policyEvents.Contains(kind);
    public bool WatchesTile(int type) => tileTypes.Contains(type) || policyTiles.Contains(type);
    public int Samples => samples;
    public JsonObject LastObservation => lastObservation?.DeepClone() as JsonObject;
    public void IgnorePriorActions(JsonArray actions)
    { foreach (var action in actions) seen.Add("action_finished:" + action["handle"] + ":" + action["attempt"]); }
    public void EnsureDefaults()
    {
        if (!events.Contains("target_acquired")) passive.Add("target_acquired");
        passive.Add("honey_slowdown");
        passive.Add("invasion_started");
        passive.Add("boss_warning");
        passive.Add("lava_protection_expiring");
        events.UnionWith(new[] { "action_finished", "target_acquired", "low_health", "suffocating", "player_died", "boss_appeared", "inventory_full", "honey_slowdown" });
        events.Add("invasion_started");
        events.Add("boss_warning");
        events.Add("lava_protection_expiring");
        if (!configured)
        {
            events.Add("nearby_chest");
            passive.Add("nearby_chest");
            background.Add("nearby_chest");
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

    internal void AcknowledgeResult(JsonObject action)
    {
        if (action["status"]?.ToString() != "failed") { AcknowledgeCompletion(action); return; }
        string key = action["handle"] + ":" + action["attempt"];
        continuedAttempts.Add(key);
        foreach (var notice in pending)
            if (notice["kind"]?.ToString() == "action_finished" && notice["key"]?.ToString() == key)
            {
                notice["wake_deferred"] = true;
                notice["deferred_reason"] = "failure_returned_to_script";
            }
    }

    internal void AcknowledgeExecutionStart(string node, bool dead, bool suffocating, JsonArray actions = null)
    {
        foreach (var action in actions ?? new JsonArray())
            if (action["task_node"]?.ToString() == node && action["tool"]?.ToString() != "fight"
                && action["status"]?.ToString() is "completed" or "failed")
                continuedAttempts.Add(action["handle"] + ":" + action["attempt"]);
        foreach (var notice in pending)
        {
            if (notice["kind"]?.ToString() == "action_finished"
                && notice["evidence"]?["task_node"]?.ToString() == node
                && notice["evidence"]?["tool"]?.ToString() != "fight")
            {
                continuedAttempts.Add(notice["key"].ToString());
                notice["wake_deferred"] = true;
                notice["deferred_reason"] = "node_execution_continued";
            }
            else if (notice["kind"]?.ToString() == "execution_gap"
                && (notice["reported_evidence"]?["task_node"]?.ToString() == node
                    || notice["reported_evidence"]?["current_node"]?.ToString() == node))
            {
                notice["wake_deferred"] = true;
                notice["deferred_reason"] = "node_execution_restarted";
            }
            else if (!dead && (notice["kind"]?.ToString() == "player_died"
                || notice["kind"]?.ToString() == "suffocating" && !suffocating))
            {
                notice["wake_deferred"] = true;
                notice["deferred_reason"] = "danger_resolved_before_execution";
            }
        }
    }

    public JsonObject Configure(JsonObject args)
    {
        var requested = args["events"]?.AsArray().Select(x => x.GetValue<string>()).ToArray()
            ?? throw new ArgumentException("需要events数组；空数组取消观察。");
        if (requested.Any(x => x is not ("nearby_tile" or "nearby_chest" or "nearby_drop" or "nearby_npc_killed" or "action_finished" or "target_acquired" or "low_health" or "suffocating" or "player_died" or "boss_appeared" or "boss_warning" or "inventory_full" or "honey_slowdown")))
            throw new ArgumentException("不支持的观察事件。");
        int radius = args["radius"]?.GetValue<int>() ?? 25;
        int? health = args["health_below"]?.GetValue<int>();
        if (radius is < 1 or > 60 || health is < 1 or > 500) throw new ArgumentException("radius须1..60，health_below须1..500。");
        var types = args["tile_types"]?.AsArray().Select(x => x.GetValue<int>()).Distinct().ToArray() ?? Array.Empty<int>();
        if (types.Length > 64 || types.Any(x => x < 0 || x > ushort.MaxValue)
            || requested.Contains("nearby_tile") && types.Length == 0)
            throw new ArgumentException("nearby_tile需要1..64个tile_types方块编号，请通过game_catalog查询。");
        var passiveEvents = args["passive_events"]?.AsArray().Select(x => x.GetValue<string>()).ToArray() ?? Array.Empty<string>();
        if (passiveEvents.Any(x => !requested.Contains(x) || x is not ("nearby_tile" or "nearby_chest" or "nearby_drop" or "target_acquired" or "low_health" or "honey_slowdown")))
            throw new ArgumentException("passive_events仅支持已订阅的nearby_tile/nearby_chest/nearby_drop/target_acquired/low_health/honey_slowdown；low_health与honey_slowdown始终只通知，不暂停动作。");
        var backgroundEvents = args["background_events"]?.AsArray().Select(x => x.GetValue<string>()).ToArray() ?? Array.Empty<string>();
        if (backgroundEvents.Any(x => !requested.Contains(x) || x is not ("nearby_tile" or "nearby_chest" or "nearby_drop" or "target_acquired")))
            throw new ArgumentException("background_events仅支持已订阅的nearby_tile/nearby_chest/nearby_drop/target_acquired。");
        passive.Clear(); passive.UnionWith(passiveEvents); passive.UnionWith(backgroundEvents);
        background.Clear(); background.UnionWith(backgroundEvents);
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
        if (state["honey_slowed"]?.GetValue<bool>() == true && state["dead"]?.GetValue<bool>() != true
            && state["tick"] is JsonNode tick)
        {
            honeySince ??= tick.GetValue<long>();
            if (tick.GetValue<long>() - honeySince.Value >= 120)
                Emit("honey_slowdown", honeySince.Value.ToString(), state);
        }
        else if (state["honey_slowed"]?.GetValue<bool>() == false || state["dead"]?.GetValue<bool>() == true)
            honeySince = null;
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
            if (state["dead"]?.GetValue<bool>() == false)
                seen.RemoveWhere(key => key.StartsWith("player_died:", StringComparison.Ordinal));
            if (state["hp"]?.GetValue<int>() < HealthBelow) Emit("low_health", dangerKey, state);
            else if (state["hp"]?.GetValue<int>() >= HealthBelow)
                seen.RemoveWhere(key => key.StartsWith("low_health:", StringComparison.Ordinal));
            if (state["suffocating"]?.GetValue<bool>() == true) Emit("suffocating", dangerKey, state);
            else if (state["suffocating"]?.GetValue<bool>() == false)
                seen.RemoveWhere(key => key.StartsWith("suffocating:", StringComparison.Ordinal));
        }
        if (state["inventory_full"]?.GetValue<bool>() == true) Emit("inventory_full", "task", state);
        else if (state["inventory_full"]?.GetValue<bool>() == false) seen.Remove("inventory_full:task");
        if (state["lava_protection_warning"] is JsonObject lava) Emit("lava_protection_expiring", "obsidian_skin", lava);
        else if (state.ContainsKey("lava_protection_warning") && seen.Remove("lava_protection_expiring:obsidian_skin"))
        {
            int count = pending.Count;
            for (int i = 0; i < count; i++)
            {
                var item = pending.Dequeue();
                if (item["kind"]?.ToString() != "lava_protection_expiring") pending.Enqueue(item);
            }
        }
        if (state["upcoming_boss"] is JsonObject warning) Emit("boss_warning", warning["type"].ToString(), warning);
        else if (state.ContainsKey("upcoming_boss") && seen.RemoveWhere(key => key.StartsWith("boss_warning:", StringComparison.Ordinal)) > 0)
        {
            int count = pending.Count;
            for (int i = 0; i < count; i++)
            {
                var item = pending.Dequeue();
                if (item["kind"]?.ToString() != "boss_warning") pending.Enqueue(item);
            }
        }
        if (state["invasion"] is JsonObject invasion)
        {
            if (invasion["active"]?.GetValue<bool>() == true) Emit("invasion_started", invasion["type"].ToString(), invasion);
            else seen.RemoveWhere(key => key.StartsWith("invasion_started:", StringComparison.Ordinal));
        }
        if (state["bosses"] is JsonArray bosses)
        {
            string BossKey(JsonNode boss) => boss["who"] + ":" + boss["type"];
            var currentBosses = bosses.Select(BossKey).ToHashSet();
            foreach (string vanished in visibleBosses.Except(currentBosses)) seen.Remove("boss_appeared:" + vanished);
            visibleBosses.Clear(); visibleBosses.UnionWith(currentBosses);
            foreach (var boss in bosses) Emit("boss_appeared", BossKey(boss), boss);
        }
        foreach (var chest in state["chests"]?.AsArray() ?? new JsonArray())
            Emit("nearby_chest", chest["x"] + "," + chest["y"], chest);
        foreach (var tile in state["tiles"]?.AsArray() ?? new JsonArray())
            if (tile["tile_type"] is JsonValue type && WatchesTile(type.GetValue<int>()))
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
        if (!Subscribed(kind) || !seen.Add(kind + ":" + key)) return;
        bool attackFinished = kind == "action_finished" && evidence?["tool"]?.ToString() == "fight";
        bool verified = kind == "action_finished" && verifiedCompletions.Contains(key);
        bool continued = kind == "action_finished" && continuedAttempts.Contains(key);
        bool deferred = verified || continued || attackFinished || !LocalDelivery && !passive.Contains(kind) && wakeCount >= 6
            && kind is not ("action_finished" or "target_acquired" or "low_health" or "suffocating" or "player_died" or "boss_appeared");
        if (!LocalDelivery && !passive.Contains(kind) && !deferred && kind is not ("low_health" or "boss_appeared" or "honey_slowdown" or "inventory_full" or "action_finished" or "target_acquired")
            && !(kind == "nearby_chest" && key == currentChestTarget)) wakeCount++;
        var value = new JsonObject { ["id"] = ++issued, ["kind"] = kind, ["key"] = key, ["evidence"] = evidence?.DeepClone() };
        if (kind is "nearby_chest" or "nearby_tile" or "nearby_drop") value["navigation"] = navigation?.DeepClone();
        if (deferred) value["wake_deferred"] = true;
        if (verified) value["deferred_reason"] = "completion_observed";
        if (continued) value["deferred_reason"] = "node_execution_continued";
        pending.Enqueue(value); history.Add((JsonObject)value.DeepClone());
    }

    public JsonArray Take() => Take(int.MaxValue);
    public JsonArray Take(int maxEvents, bool recoveryOnly = false)
        => TakeMatching(maxEvents, recoveryOnly ? RecoveryBlocker : null);
    internal JsonArray TakeModelInterrupts(int maxEvents) => TakeMatching(maxEvents, ModelInterrupting);
    JsonArray TakeMatching(int maxEvents, Func<JsonObject, bool> filter)
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
        int remaining = pending.Count;
        while (result.Count < maxEvents && remaining-- > 0 && pending.TryDequeue(out var item))
        {
            if (filter != null && !filter(item)) { pending.Enqueue(item); continue; }
            if (assessOpportunity != null && item["kind"]?.ToString() is "nearby_chest" or "nearby_tile" or "nearby_drop")
            {
                if (assessed++ < 6) item["assessment"] = assessOpportunity(item);
                else item["assessment_deferred"] = true;
            }
            result.Add(item);
        }
        return result;
    }
    static int ReviewPriority(JsonObject item) => item["kind"]?.ToString() is "low_health" or "suffocating" or "player_died" or "boss_appeared" or "boss_warning" or "decision_point" or "execution_gap" or "honey_slowdown" or "lava_protection_expiring"
        || item["kind"]?.ToString() == "action_finished" && item["evidence"]?["status"]?.ToString() == "failed" ? 0 : 1;

    public JsonObject Pending(bool recoveryOnly = false)
    {
        var selected = pending.Where(e => !recoveryOnly || RecoveryBlocker(e)).OrderBy(ReviewPriority).ToArray();
        return new JsonObject { ["events"] = new JsonArray(selected.Take(8).Select(x => x.DeepClone()).ToArray()),
            ["remaining_events"] = Math.Max(0, selected.Length - 8) };
    }

    internal JsonArray Advisories()
    {
        var result = new JsonArray();
        foreach (var item in pending.Where(IsAdvisory).OrderBy(ReviewPriority)
            .ThenBy(e => e["kind"]?.ToString() == "nearby_chest" ? 0 : 1).Take(4))
        {
            advised.Add(item["id"].GetValue<int>());
            result.Add(new JsonObject { ["id"] = item["id"]?.DeepClone(), ["kind"] = item["kind"]?.DeepClone(),
                ["key"] = item["key"]?.DeepClone(), ["evidence"] = item["evidence"]?.DeepClone(),
                ["navigation"] = item["navigation"]?.DeepClone() });
        }
        if (result.Any(item => item["kind"]?.ToString() is "nearby_chest" or "nearby_tile" or "nearby_drop" && WakesModel(item)))
            nextOpportunityReview = clock() + 15000;
        return result;
    }

    bool IsAdvisory(JsonObject item)
    {
        if (HandledByScript(item)) return false;
        string kind = item["kind"]?.ToString();
        if (kind == "tactical_review" && item["wake_deferred"]?.GetValue<bool>() == true) return false;
        if (kind is "nearby_chest" or "nearby_tile" or "nearby_drop"
            && (clock() < nextOpportunityReview || item["wake_deferred"]?.GetValue<bool>() == true)) return false;
        if (kind is "nearby_chest" or "nearby_tile" or "nearby_drop"
            && item["evidence"]?["x"] != null && item["evidence"]["y"] != null
            && lastObservation?["pos"]?["x"] != null && lastObservation["pos"]["y"] != null)
        {
            double Number(JsonNode value) => double.Parse(value.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);
            double dx = Number(item["evidence"]["x"]) - Number(lastObservation["pos"]["x"]) / 16;
            double dy = Number(item["evidence"]["y"]) - Number(lastObservation["pos"]["y"]) / 16;
            if (dx * dx + dy * dy > Radius * Radius) return false;
        }
        return !advised.Contains(item["id"].GetValue<int>())
            && (kind is "nearby_chest" or "nearby_tile" or "nearby_drop" or "honey_slowdown" or "inventory_full" or "tactical_review" or "boss_warning" or "lava_protection_expiring"
                || item["wake_deferred"]?.GetValue<bool>() != true
                    && (kind == "target_acquired" && (!passive.Contains(kind) || background.Contains(kind))
                        || kind == "action_finished" && item["evidence"]?["status"]?.ToString() == "failed"));
    }

    bool WakesModel(JsonNode item) => item["kind"]?.ToString() is not ("nearby_chest" or "nearby_tile" or "nearby_drop")
        || !passive.Contains(item["kind"].ToString()) || background.Contains(item["kind"].ToString());
    internal bool HasAdvisories => !LocalDelivery && pending.Any(item => IsAdvisory(item) && WakesModel(item));

    public JsonObject Reconsider(string kind, string key, string reason)
    {
        if (kind is not ("nearby_tile" or "nearby_chest" or "nearby_drop") || string.IsNullOrWhiteSpace(key)
            || string.IsNullOrWhiteSpace(reason) || reason.Length > 500)
            throw new ArgumentException("需要机会事件kind/key及1..500字符的重新考虑原因。");
        if (!Subscribed(kind)) throw new ArgumentException("该事件尚未订阅。");
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
        wakeCount++;
        var value = new JsonObject { ["id"] = ++issued, ["kind"] = "decision_point", ["reason"] = reason };
        if (evidence != null) value["reported_evidence"] = evidence.DeepClone();
        pending.Enqueue(value); history.Add((JsonObject)value.DeepClone());
        return (JsonObject)value.DeepClone();
    }

    internal JsonObject RequestTacticalReview(string key, string reason, JsonObject evidence)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 500 || string.IsNullOrWhiteSpace(reason)
            || reason.Length > 500 || evidence?.ToJsonString(AgentTools.ModelJson).Length > 8000)
            throw new ArgumentException("战术审阅需要稳定key、原因及简短现场证据。");
        if (!seen.Add("tactical_review:" + key)) return new JsonObject { ["status"] = "already_requested" };
        bool deferred = wakeCount >= 6;
        if (!deferred) wakeCount++;
        var value = new JsonObject { ["id"] = ++issued, ["kind"] = "tactical_review", ["key"] = key,
            ["evidence"] = new JsonObject { ["reason"] = reason, ["observed"] = evidence?.DeepClone() },
            ["wake_deferred"] = deferred };
        pending.Enqueue(value); history.Add((JsonObject)value.DeepClone());
        return new JsonObject { ["status"] = deferred ? "deferred" : "queued", ["event_id"] = issued };
    }

    internal JsonArray TacticalAdvisories(bool acknowledge = true)
    {
        var result = new JsonArray();
        foreach (var value in pending.Where(e => e["kind"]?.ToString() is "tactical_review" or "boss_warning" && IsAdvisory(e)))
        {
            if (acknowledge) advised.Add(value["id"].GetValue<int>());
            result.Add(value.DeepClone());
        }
        return result;
    }

    internal void AcknowledgeTacticalAdvisories(JsonArray values)
    {
        foreach (var value in values) advised.Add(value["id"].GetValue<int>());
    }

    internal void ExecutionFault(JsonObject evidence)
    {
        if (pending.Any(e => e["kind"]?.ToString() == "execution_gap" && e["wake_deferred"]?.GetValue<bool>() != true)) return;
        var value = new JsonObject { ["id"] = ++issued, ["kind"] = "execution_gap",
            ["reported_evidence"] = evidence.DeepClone() };
        pending.Enqueue(value);
        history.Add((JsonObject)value.DeepClone());
    }

    internal void ResolveExecutionGap(string reason)
    {
        foreach (var notice in pending)
            if (notice["kind"]?.ToString() == "execution_gap" && notice["wake_deferred"]?.GetValue<bool>() != true)
            {
                notice["wake_deferred"] = true;
                notice["deferred_reason"] = reason;
            }
    }

    public JsonObject Summary() => new()
    {
        ["loot_chests_in_reach"] = LootChestsInReach,
        ["passive_events"] = new JsonArray(passive.Select(x => (JsonNode)JsonValue.Create(x)).ToArray()),
        ["background_events"] = new JsonArray(background.Select(x => (JsonNode)JsonValue.Create(x)).ToArray()),
        ["tile_types"] = new JsonArray(tileTypes.Union(policyTiles).Select(x => (JsonNode)JsonValue.Create(x)).ToArray()),
        ["events"] = new JsonArray(events.Union(policyEvents).Select(x => (JsonNode)JsonValue.Create(x)).ToArray()),
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
        ["background_events"] = new JsonArray(background.Select(x => (JsonNode)JsonValue.Create(x)).ToArray()),
        ["tile_types"] = new JsonArray(tileTypes.Union(policyTiles).Select(x => (JsonNode)JsonValue.Create(x)).ToArray()),
        ["events"] = new JsonArray(events.Union(policyEvents).Select(x => (JsonNode)JsonValue.Create(x)).ToArray()),
        ["radius"] = Radius, ["health_below"] = HealthBelow, ["wake_count"] = wakeCount, ["wake_limit"] = 6, ["event_count"] = issued,
        ["pending"] = new JsonArray(pending.Select(x => x.DeepClone()).ToArray()),
        ["history"] = new JsonArray(history.Select(x => x.DeepClone()).ToArray()), ["scope"] = "current_instruction",
        ["samples"] = samples, ["last_observation"] = lastObservation?.DeepClone()
    };

    public void Reset()
    { SetPolicyWatch(null); events.Clear(); seen.Clear(); visibleDrops.Clear(); visibleBosses.Clear(); tileTypes.Clear(); passive.Clear(); background.Clear(); advised.Clear(); verifiedCompletions.Clear(); continuedAttempts.Clear(); pending.Clear(); history.Clear(); scriptReaders.Clear(); scriptHandlers.Clear(); issued = wakeCount = samples = 0; nextOpportunityReview = 0; configured = LocalDelivery = LootChestsInReach = DefaultRouteLoot = false; lastObservation = itemCounts = null; healthOverride = null; maxHealth = 100; currentChestTarget = null; navigation = null; honeySince = null; }
    public void End() { events.Clear(); pending.Clear(); scriptHandlers.Clear(); SetPolicyWatch(null); LootChestsInReach = false; }
}
