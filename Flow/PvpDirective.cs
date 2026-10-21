using System;
using System.Linq;
using System.Text.Json.Nodes;

namespace TerraBlind;

public sealed class PvpDirective
{
    static readonly string[] Sections = { "general", "movement", "weapon", "aim", "terrain", "inventory" };
    JsonObject player = new(), agent = new(), instructions = new();
    public long Revision { get; private set; }
    public string Goal { get; }
    public PvpDirective(string goal) => Goal = goal ?? "";

    static bool Allows(JsonObject rules, int type)
        => (rules["allowed_types"] is not JsonArray allowed || allowed.Any(value => value.GetValue<int>() == type))
            && (rules["denied_types"] is not JsonArray denied || !denied.Any(value => value.GetValue<int>() == type));

    public bool AllowsWeapon(int type) => Allows(player, type) && Allows(agent, type);
    public bool AllowsTerrain(bool placing, int x, int y)
    {
        string key = placing ? "allow_placing" : "allow_mining";
        if (player[key]?.GetValue<bool>() != true || agent[key]?.GetValue<bool>() == false) return false;
        return !new[] { player, agent }.Any(rules => rules["protected_rects"] is JsonArray areas && areas.Any(node =>
            x >= node["x"].GetValue<int>() && y >= node["y"].GetValue<int>()
            && x < (long)node["x"].GetValue<int>() + node["w"].GetValue<int>()
            && y < (long)node["y"].GetValue<int>() + node["h"].GetValue<int>()));
    }

    public JsonObject Update(JsonObject patch, bool fromPlayer = false)
    {
        if (!fromPlayer && (!long.TryParse(patch["expected_revision"]?.ToString(), out long expected) || expected != Revision))
            return new JsonObject { ["error"] = "revision_conflict", ["revision"] = Revision };
        try
        {
            var text = (JsonObject)instructions.DeepClone();
            var rules = (JsonObject)(fromPlayer ? player : agent).DeepClone();
            if (patch["instructions"] is JsonObject changes)
                foreach (var change in changes)
                {
                    if (!Sections.Contains(change.Key)) throw new ArgumentException("unknown_instruction_section");
                    string value = change.Value?.GetValue<string>();
                    if (value?.Length > 2000) throw new ArgumentException("instruction_too_long");
                    text[change.Key] = value;
                }
            if (text.Sum(entry => entry.Value?.GetValue<string>()?.Length ?? 0) > 6000)
                throw new ArgumentException("instructions_too_long");
            if (patch["constraints"] is JsonObject constraints)
                foreach (var field in constraints)
                {
                    switch (field.Key)
                    {
                        case "allowed_types": case "denied_types":
                            if (field.Value != null && (field.Value is not JsonArray values || values.Any(value => value == null || value.GetValue<int>() <= 0)))
                                throw new ArgumentException("invalid_item_types");
                            break;
                        case "allow_mining": case "allow_placing":
                            if (field.Value != null) _ = field.Value.GetValue<bool>();
                            break;
                        case "protected_rects":
                            if (field.Value is not JsonArray areas || areas.Any(area => area is not JsonObject
                                || area["x"]?.GetValue<int>() < 0 || area["y"]?.GetValue<int>() < 0
                                || area["x"] == null || area["y"] == null
                                || area["w"]?.GetValue<int>() is not > 0 || area["h"]?.GetValue<int>() is not > 0))
                                throw new ArgumentException("invalid_protected_rects");
                            break;
                        default: throw new ArgumentException("unknown_constraint");
                    }
                    rules[field.Key] = field.Value?.DeepClone();
                }
            instructions = text;
            if (fromPlayer) player = rules; else agent = rules;
            Revision++;
            return new JsonObject { ["ok"] = true, ["directive"] = ToJson() };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException or OverflowException)
        { return new JsonObject { ["error"] = ex.Message, ["revision"] = Revision }; }
    }

    public JsonObject ToJson() => new()
    {
        ["revision"] = Revision, ["player_goal"] = Goal, ["instructions"] = instructions.DeepClone(),
        ["player_constraints"] = player.DeepClone(), ["agent_constraints"] = agent.DeepClone()
    };
}
