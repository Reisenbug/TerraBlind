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
        public bool Outside;
        public (int X, int Y, int Width, int Height)? Region;
    }

    readonly Dictionary<string, Rule> rules = new();
    volatile Rule[] spatialRules = Array.Empty<Rule>();
    public long Revision { get; private set; }
    public bool HasClaims => rules.Values.Any(rule => rule.Channels != ControlChannels.None);
    public bool Contains(string id) => rules.ContainsKey(id);
    public bool HasSpatial(ControlBehavior behavior) => spatialRules.Any(rule => rule.Behaviors.Contains(behavior));

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

    public string Deny(IEnumerable<ControlBehavior> behaviors, ControlLifetime lifetime, string action, string reason,
        (int X, int Y, int Width, int Height)? region = null, bool outside = false)
    {
        Validate(lifetime, action, reason);
        var values = behaviors.Distinct().ToArray();
        if (values.Length == 0 || values.Any(value => !Enum.IsDefined(value)))
            throw new ArgumentException("需要有效的行为名称。");
        if (region is { } area && (area.X < 0 || area.Y < 0 || area.Width <= 0 || area.Height <= 0
            || (long)area.X + area.Width > int.MaxValue || (long)area.Y + area.Height > int.MaxValue))
            throw new ArgumentException("区域需要非负世界格坐标和正数宽高。");
        if (outside && region == null) throw new ArgumentException("confine需要明确的世界格矩形region。");
        if (region != null && values.Any(value => value is not (ControlBehavior.Mining or ControlBehavior.Placement or ControlBehavior.Navigation)
            && !(outside && value == ControlBehavior.Dodge)))
            throw new ArgumentException("区域禁令只支持navigation/mining/placement。");
        return Add(new Rule { Behaviors = values, Lifetime = lifetime, Action = action, Reason = reason, Region = region, Outside = outside });
    }

    string Add(Rule rule)
    {
        rule.Id = Guid.NewGuid().ToString("N");
        rules.Add(rule.Id, rule); Revision++;
        spatialRules = rules.Values.Where(value => value.Region != null).ToArray();
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
            if (rule.Region == null && rule.Behaviors?.Contains(behavior) == true)
                return Denied(rule, "behavior_forbidden", behavior);
            if ((rule.Channels & channels) != 0 && (rule.Id != claim || !rule.Ready))
                return Denied(rule, rule.Ready ? "control_owned_by_agent" : "control_handoff_pending", behavior);
        }
        if (granted != null && (granted.Channels & channels) != channels)
            return Denied(granted, "control_claim_missing_channel", behavior);
        return null;
    }

    public JsonObject CheckAt(ControlBehavior behavior, int x, int y)
    {
        foreach (var rule in spatialRules)
            if (rule.Region is { } area && rule.Behaviors?.Contains(behavior) == true
                && rule.Outside != (x >= area.X && y >= area.Y && x < (long)area.X + area.Width && y < (long)area.Y + area.Height))
            {
                var denied = Denied(rule, "region_forbidden", behavior);
                denied["x"] = x; denied["y"] = y;
                return denied;
            }
        return null;
    }

    public bool ForbiddenAt(ControlBehavior behavior, int x, int y)
    {
        foreach (var rule in spatialRules)
            if (rule.Region is { } area && rule.Behaviors.Contains(behavior)
                && rule.Outside != (x >= area.X && y >= area.Y && x < (long)area.X + area.Width && y < (long)area.Y + area.Height))
                return true;
        return false;
    }

    public bool BoundsAllowed(ControlBehavior behavior, float x, float y, float width, float height)
    {
        foreach (var rule in spatialRules)
        {
            if (!rule.Behaviors.Contains(behavior) || rule.Region is not { } area) continue;
            if (rule.Outside ? x < area.X || y < area.Y || x + width > (long)area.X + area.Width || y + height > (long)area.Y + area.Height
                : x < (long)area.X + area.Width && y < (long)area.Y + area.Height && x + width > area.X && y + height > area.Y)
                return false;
        }
        return true;
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
        spatialRules = rules.Values.Where(value => value.Region != null).ToArray();
    }

    public void EndAction(string action) => Remove(rule => rule.Lifetime == ControlLifetime.Action && rule.Action == action);
    public void BeginTask() => Remove(rule => rule.Lifetime != ControlLifetime.Persistent);
    public void Reset() => Remove(_ => true);

    void Remove(Func<Rule, bool> predicate)
    {
        var ids = rules.Values.Where(predicate).Select(rule => rule.Id).ToArray();
        foreach (string id in ids) rules.Remove(id);
        if (ids.Length > 0)
        {
            Revision++;
            spatialRules = rules.Values.Where(value => value.Region != null).ToArray();
        }
    }

    public JsonObject Snapshot() => new()
    {
        ["revision"] = Revision,
        ["rules"] = new JsonArray(rules.Values.Select(rule => (JsonNode)new JsonObject
        {
            ["id"] = rule.Id, ["kind"] = rule.Behaviors == null ? "claim" : rule.Outside ? "confine" : "deny",
            ["scope"] = rule.Lifetime.ToString().ToLowerInvariant(), ["action"] = rule.Action,
            ["reason"] = rule.Reason,
            ["region"] = rule.Region is { } area ? new JsonObject { ["x"] = area.X, ["y"] = area.Y,
                ["width"] = area.Width, ["height"] = area.Height } : null,
            ["status"] = rule.Behaviors != null ? "active" : rule.Ready ? "ready" : "acquiring",
            ["channels"] = new JsonArray(new[] { ControlChannels.Movement, ControlChannels.ItemUse }
                .Where(channel => (rule.Channels & channel) != 0)
                .Select(channel => JsonValue.Create(channel == ControlChannels.Movement ? "movement" : "item_use")).ToArray()),
            ["behaviors"] = new JsonArray((rule.Behaviors ?? Array.Empty<ControlBehavior>())
                .Select(behavior => JsonValue.Create(behavior.ToString().ToLowerInvariant())).ToArray())
        }).ToArray())
    };
}
