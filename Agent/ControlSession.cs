using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace TerraBlind;

public sealed class AgentControlSession
{
    readonly AgentControlPolicy policy;
    long sequence, current;
    ControlChannels primaryChannels;
    int resetGeneration;
    string tool, claim;
    long attackTicket;
    string attackClaim;
    long scriptInvocation, releasedScript, releasedAt;
    string releasedTool;
    ControlBehavior[] delegates = Array.Empty<ControlBehavior>();
    readonly Dictionary<long, int[]> gatheredItems = new();

    public AgentControlSession(AgentControlPolicy policy) { this.policy = policy; }
    public bool PrimaryActive => current != 0;
    internal long NavigationExecution => delegates.Contains(ControlBehavior.Navigation) ? current : 0;
    internal bool Gathering(int itemType) => gatheredItems.Values.Any(items => items.Contains(itemType));
    internal void BindGatherTargets(GatherTarget[] targets)
    {
        if (current != 0 && tool == "gather") gatheredItems[current] = targets.Select(t => t.ItemType).ToArray();
    }
    internal bool OwnsItemUse => current != 0 && (primaryChannels & ControlChannels.ItemUse) != 0;
    internal bool OwnsDirectMovement => current != 0 && tool == "act" && (primaryChannels & ControlChannels.Movement) != 0;
    public bool Active => current != 0 || attackTicket != 0;
    public string Tool => tool ?? (attackTicket != 0 ? "fight" : null);
    public static bool ActionTool(string name) => name is "nav_to" or "mine" or "mine_vein" or "chop_wood"
        or "use_item" or "place_at" or "act" or "interact" or "loot_all" or "craft_into_inventory" or "inventory_swap"
        or "combat_reposition" or "gather" or "collect_treasure" or "build_house" or "hell_run" or "equip_item" or "open_item" or "inventory_drop" or "inventory_trash" or "npc_chat" or "fight" or "fight_player"
        or "slot_click" or "container_action" or "game_input" or "craft_recipe" or "housing" or "mod_input"
        or "pylon_use" or "reforge" or "fish" or "container_transfer" or "craft_to_slot";

    public static void DescribeClaims(JsonArray schemas)
    {
        var control = schemas.First(s => s["function"]["name"].ToString() == "control")["function"];
        control["parameters"]["properties"]["scope"]["default"] = "task";
        control["parameters"]["properties"]["operation"]["enum"].AsArray().Add("confine");
        control["description"] = control["description"].ToString()
            + "confine将behaviors限定在region矩形内，默认navigation/dodge；边界为世界格坐标，人物整个碰撞箱应在区域内。"
            + "deny配合region可保护具体区域的mining/placement/navigation；scope默认task，控制action/task/persistent有效期，release按id撤销。"
            + "equip_item、inventory_swap、craft_recipe等常规工具已完成原子输入交接，可直接调用；claim用于自行持续接管输入。";
        foreach (var schema in schemas)
            if (ActionTool(schema["function"]?["name"]?.ToString()))
                schema["function"]["parameters"]["properties"]["control_claim"] = new JsonObject
                { ["type"] = "string", ["description"] = "control claim返回的id；使用已接管的输入通道时必须提供。" };
    }

    public static ControlBehavior Behavior(string name) => name switch
    {
        "nav_to" => ControlBehavior.Navigation,
        "mine" or "mine_vein" or "chop_wood" => ControlBehavior.Mining,
        "place_at" => ControlBehavior.Placement,
        "fight" or "fight_player" or "combat_start" => ControlBehavior.Attack,
        "gather" or "collect_treasure" or "build_house" or "hell_run" => ControlBehavior.Workflow,
        _ => ControlBehavior.Direct
    };

    public JsonObject Begin(string name, string controlClaim, out long ticket, ControlChannels channels = ControlChannels.All, long script = 0)
    {
        ticket = 0;
        if (name == "fight")
        {
            if (attackTicket != 0) return new JsonObject { ["error"] = "control_action_busy", ["owner"] = "fight" };
            var attackDenied = policy.Check(ControlBehavior.Attack, ControlChannels.ItemUse, controlClaim);
            if (attackDenied != null) return attackDenied;
            attackClaim = controlClaim; attackTicket = ticket = ++sequence;
            return null;
        }
        if (current != 0) return new JsonObject { ["error"] = "control_action_busy", ["owner"] = tool };
        var denied = policy.Check(Behavior(name), channels, controlClaim);
        if (denied != null) return denied;
        if (releasedAt != 0)
            DiagLog.Write("[agent_input_handoff] " + new JsonObject { ["from"] = releasedTool, ["to"] = name,
                ["elapsed_ms"] = System.Diagnostics.Stopwatch.GetElapsedTime(releasedAt).TotalMilliseconds,
                ["prepared_in_same_script"] = script != 0 && script == releasedScript }.ToJsonString());
        scriptInvocation = script;
        tool = name; claim = controlClaim; current = ticket = ++sequence;
        primaryChannels = channels;
        delegates = name switch
        {
            "nav_to" => new[] { ControlBehavior.Navigation, ControlBehavior.Mining, ControlBehavior.Placement, ControlBehavior.Direct, ControlBehavior.Attack },
            "mine" or "mine_vein" or "chop_wood" => new[] { ControlBehavior.Navigation, ControlBehavior.Mining, ControlBehavior.Direct, ControlBehavior.Attack },
            "place_at" => new[] { ControlBehavior.Navigation, ControlBehavior.Mining, ControlBehavior.Placement, ControlBehavior.Direct },
            "gather" or "collect_treasure" or "build_house" or "hell_run" => new[] { ControlBehavior.Navigation, ControlBehavior.Mining, ControlBehavior.Placement,
                ControlBehavior.Direct, ControlBehavior.Workflow, ControlBehavior.Attack, ControlBehavior.Dodge },
            "fight" or "fight_player" => new[] { ControlBehavior.Attack, ControlBehavior.Dodge },
            "act" or "use_item" => new[] { ControlBehavior.Direct, ControlBehavior.Attack, ControlBehavior.Mining, ControlBehavior.Placement },
            _ => new[] { ControlBehavior.Direct }
        };
        return null;
    }

    public JsonObject Check(ControlBehavior behavior, ControlChannels channels, bool delegated = false)
        => policy.Check(behavior, channels, delegated && behavior == ControlBehavior.Attack && attackTicket != 0
            ? attackClaim : delegated && current != 0 && delegates.Contains(behavior) ? claim : null);

    public JsonObject CheckWorkflow(string ownerTool)
    {
        bool child = tool == "hell_run" && ownerTool is "build_house" or "wof_prep" or "wof_fight";
        if (current != 0 && tool != ownerTool && !child)
            return new JsonObject { ["error"] = "workflow_owned_by_another_action", ["owner"] = tool, ["requested"] = ownerTool };
        return Check(ControlBehavior.Workflow, ControlChannels.All, delegated: true);
    }

    public static ControlChannels RequiredChannels(string name, JsonObject args)
    {
        if (name == "fight") return ControlChannels.ItemUse;
        if (name == "game_input" && args["action"]?.ToString() == "quick_mount") return ControlChannels.Movement;
        if (name is "slot_click" or "container_action" or "game_input" or "craft_recipe" or "housing" or "reforge"
            or "container_transfer" or "craft_to_slot") return ControlChannels.ItemUse;
        if (name != "act") return ControlChannels.All;
        if (args["combat_candidate"] != null) return ControlChannels.Movement;
        var channels = ControlChannels.None;
        foreach (var step in args["steps"]?.AsArray() ?? new JsonArray())
        {
            foreach (var key in step["keys"]?.AsArray() ?? new JsonArray())
                channels |= key?.ToString() switch
                {
                    "left" or "right" or "up" or "down" or "jump" or "mount" => ControlChannels.Movement,
                    "hook" => ControlChannels.All,
                    _ => ControlChannels.ItemUse
                };
            if (step["slot"] != null || step["rel"] != null || step["at"] != null || step["use"]?.ToString() == "true")
                channels |= ControlChannels.ItemUse;
        }
        return channels;
    }

    public void End(long ticket)
    {
        gatheredItems.Remove(ticket);
        if (ticket != 0 && ticket == attackTicket) { attackTicket = 0; attackClaim = null; return; }
        if (ticket == 0 || ticket != current) return;
        releasedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        releasedScript = scriptInvocation; releasedTool = tool;
        current = 0; tool = claim = null; delegates = Array.Empty<ControlBehavior>();
        primaryChannels = ControlChannels.None;
    }

    internal Action SuspendPrimary()
    {
        long saved = current, savedScript = scriptInvocation;
        int generation = resetGeneration;
        string savedTool = tool, savedClaim = claim;
        var savedChannels = primaryChannels;
        var savedDelegates = delegates;
        current = 0; tool = claim = null; delegates = Array.Empty<ControlBehavior>();
        return () =>
        {
            if (generation != resetGeneration) return;
            if (current != 0) throw new InvalidOperationException("事件动作尚未释放控制权。");
            current = saved; scriptInvocation = savedScript;
            primaryChannels = savedChannels;
            tool = savedTool; claim = savedClaim; delegates = savedDelegates;
        };
    }

    public void Reset() { End(current); End(attackTicket); gatheredItems.Clear(); sequence++; resetGeneration++; releasedAt = releasedScript = scriptInvocation = 0; releasedTool = null; }
    public JsonObject Snapshot() => new()
    {
        ["tool"] = tool, ["claim"] = claim, ["execution"] = current,
        ["attack_execution"] = attackTicket, ["attack_claim"] = attackClaim,
        ["delegated_behaviors"] = new JsonArray(delegates.Select(value => JsonValue.Create(value.ToString().ToLowerInvariant())).ToArray())
    };
}
