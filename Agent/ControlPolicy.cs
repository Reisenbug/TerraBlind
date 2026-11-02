using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace TerraBlind;

[Flags]
public enum ControlChannels { None = 0, Movement = 1, ItemUse = 2, All = Movement | ItemUse }
public enum ControlBehavior { Navigation, Attack, Dodge, Survival, Mining, Placement, Workflow, Direct }
public enum ControlLifetime { Action, Task, Persistent }

public sealed class AgentControlPolicy
{
    sealed class Rule
    {
        public string Id, Action, Reason;
        public ControlLifetime Lifetime;
        public ControlChannels Channels;
        public ControlBehavior[] Behaviors;
        public bool Ready;
    }

    readonly Dictionary<string, Rule> rules = new();
    public long Revision { get; private set; }

    static void Validate(ControlLifetime lifetime, string action, string reason)
    {
        if (!Enum.IsDefined(lifetime)) throw new ArgumentException("未知覆盖期限。");
        if (lifetime == ControlLifetime.Action && string.IsNullOrWhiteSpace(action))
            throw new ArgumentException("动作级覆盖必须绑定动作句柄。");
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("必须说明接管或禁止的原因。");
    }

    public string Claim(ControlChannels channels, ControlLifetime lifetime, string action, string reason)
    {
        Validate(lifetime, action, reason);
        if (channels == ControlChannels.None || (channels & ~ControlChannels.All) != 0)
            throw new ArgumentException("需要 movement 或 item_use 控制通道。");
        if (rules.Values.Any(rule => (rule.Channels & channels) != 0))
            throw new InvalidOperationException("控制通道已被接管；先观察并明确释放原接管。");
        return Add(new Rule { Channels = channels, Lifetime = lifetime, Action = action, Reason = reason });
    }

    public string Deny(IEnumerable<ControlBehavior> behaviors, ControlLifetime lifetime, string action, string reason)
    {
        Validate(lifetime, action, reason);
        var values = behaviors.Distinct().ToArray();
        if (values.Length == 0 || values.Any(value => !Enum.IsDefined(value)))
            throw new ArgumentException("需要有效的行为名称。");
        return Add(new Rule { Behaviors = values, Lifetime = lifetime, Action = action, Reason = reason });
    }

    string Add(Rule rule)
    {
        rule.Id = Guid.NewGuid().ToString("N");
        rules.Add(rule.Id, rule); Revision++;
        return rule.Id;
    }

    public void ConfirmReleased(string id)
    {
        if (!rules.TryGetValue(id, out var rule) || rule.Channels == ControlChannels.None)
            throw new ArgumentException("未知接管句柄。");
        if (!rule.Ready) { rule.Ready = true; Revision++; }
    }

    public JsonObject Check(ControlBehavior behavior, ControlChannels channels, string claim = null)
    {
        Rule granted = null;
        if (claim != null && (!rules.TryGetValue(claim, out granted) || granted.Channels == ControlChannels.None))
            return new JsonObject { ["error"] = "control_claim_expired", ["claim"] = claim, ["revision"] = Revision };
        foreach (var rule in rules.Values)
        {
            if (rule.Behaviors?.Contains(behavior) == true)
                return Denied(rule, "behavior_forbidden", behavior);
            if ((rule.Channels & channels) != 0 && (rule.Id != claim || !rule.Ready))
                return Denied(rule, rule.Ready ? "control_owned_by_agent" : "control_handoff_pending", behavior);
        }
        if (granted != null && (granted.Channels & channels) != channels)
            return Denied(granted, "control_claim_missing_channel", behavior);
        return null;
    }

    JsonObject Denied(Rule rule, string error, ControlBehavior behavior) => new()
    {
        ["error"] = error, ["behavior"] = behavior.ToString().ToLowerInvariant(),
        ["rule"] = rule.Id, ["reason"] = rule.Reason, ["revision"] = Revision
    };

    public void Release(string id)
    {
        if (!rules.Remove(id)) throw new ArgumentException("未知或已经释放的覆盖句柄。");
        Revision++;
    }

    public void EndAction(string action) => Remove(rule => rule.Lifetime == ControlLifetime.Action && rule.Action == action);
    public void BeginTask() => Remove(rule => rule.Lifetime != ControlLifetime.Persistent);
    public void Reset() => Remove(_ => true);

    void Remove(Func<Rule, bool> predicate)
    {
        var ids = rules.Values.Where(predicate).Select(rule => rule.Id).ToArray();
        foreach (string id in ids) rules.Remove(id);
        if (ids.Length > 0) Revision++;
    }

    public JsonObject Snapshot() => new()
    {
        ["revision"] = Revision,
        ["rules"] = new JsonArray(rules.Values.Select(rule => (JsonNode)new JsonObject
        {
            ["id"] = rule.Id, ["kind"] = rule.Behaviors == null ? "claim" : "deny",
            ["scope"] = rule.Lifetime.ToString().ToLowerInvariant(), ["action"] = rule.Action,
            ["reason"] = rule.Reason,
            ["status"] = rule.Behaviors != null ? "active" : rule.Ready ? "ready" : "acquiring",
            ["channels"] = new JsonArray(new[] { ControlChannels.Movement, ControlChannels.ItemUse }
                .Where(channel => (rule.Channels & channel) != 0)
                .Select(channel => JsonValue.Create(channel == ControlChannels.Movement ? "movement" : "item_use")).ToArray()),
            ["behaviors"] = new JsonArray((rule.Behaviors ?? Array.Empty<ControlBehavior>())
                .Select(behavior => JsonValue.Create(behavior.ToString().ToLowerInvariant())).ToArray())
        }).ToArray())
    };
}
