using System;
using System.Linq;
using System.Text.Json.Nodes;

namespace TerraBlind;

public sealed class AgentControlSession
{
    readonly AgentControlPolicy policy;
    long sequence, current;
    string tool, claim;
    ControlBehavior[] delegates = Array.Empty<ControlBehavior>();

    public AgentControlSession(AgentControlPolicy policy) { this.policy = policy; }
    public bool Active => current != 0;
    public string Tool => tool;
    public static bool ActionTool(string name) => name is "nav_to" or "mine" or "mine_vein" or "chop_wood"
        or "use_item" or "place_at" or "act" or "interact" or "loot_all" or "craft" or "inventory_swap"
        or "combat_reposition" or "gather" or "collect_treasure" or "equip_item" or "npc_chat" or "fight" or "fight_player";

    public static void DescribeClaims(JsonArray schemas)
    {
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
        "gather" or "collect_treasure" => ControlBehavior.Workflow,
        _ => ControlBehavior.Direct
    };

    public JsonObject Begin(string name, string controlClaim, out long ticket, ControlChannels channels = ControlChannels.All)
    {
        ticket = 0;
        if (Active) return new JsonObject { ["error"] = "control_action_busy", ["owner"] = tool };
        var denied = policy.Check(Behavior(name), channels, controlClaim);
        if (denied != null) return denied;
        tool = name; claim = controlClaim; current = ticket = ++sequence;
        delegates = name switch
        {
            "nav_to" => new[] { ControlBehavior.Navigation, ControlBehavior.Mining, ControlBehavior.Placement, ControlBehavior.Direct },
            "mine" or "mine_vein" or "chop_wood" => new[] { ControlBehavior.Navigation, ControlBehavior.Mining, ControlBehavior.Direct },
            "place_at" => new[] { ControlBehavior.Navigation, ControlBehavior.Mining, ControlBehavior.Placement, ControlBehavior.Direct },
            "gather" or "collect_treasure" => new[] { ControlBehavior.Navigation, ControlBehavior.Mining, ControlBehavior.Placement,
                ControlBehavior.Direct, ControlBehavior.Workflow, ControlBehavior.Attack, ControlBehavior.Dodge },
            "fight" or "fight_player" => new[] { ControlBehavior.Attack, ControlBehavior.Dodge },
            "act" or "use_item" => new[] { ControlBehavior.Direct, ControlBehavior.Attack, ControlBehavior.Mining, ControlBehavior.Placement },
            _ => new[] { ControlBehavior.Direct }
        };
        return null;
    }

    public JsonObject Check(ControlBehavior behavior, ControlChannels channels, bool delegated = false)
        => policy.Check(behavior, channels, delegated && Active && delegates.Contains(behavior) ? claim : null);

    public JsonObject CheckWorkflow(string ownerTool)
    {
        if (Active && tool != ownerTool)
            return new JsonObject { ["error"] = "workflow_owned_by_another_action", ["owner"] = tool, ["requested"] = ownerTool };
        return Check(ControlBehavior.Workflow, ControlChannels.All, delegated: true);
    }

    public static ControlChannels RequiredChannels(string name, JsonObject args)
    {
        if (name != "act") return ControlChannels.All;
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
        if (ticket != current) return;
        current = 0; tool = claim = null; delegates = Array.Empty<ControlBehavior>();
    }

    public void Reset() { End(current); sequence++; }
    public JsonObject Snapshot() => new()
    {
        ["tool"] = tool, ["claim"] = claim, ["execution"] = current,
        ["delegated_behaviors"] = new JsonArray(delegates.Select(value => JsonValue.Create(value.ToString().ToLowerInvariant())).ToArray())
    };
}
