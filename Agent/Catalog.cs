using System;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;

namespace TerraBlind;

public static class AgentCatalog
{
    static string Key(string name) => name.Replace(" ", "").Replace("_", "").ToLowerInvariant();

    public static JsonObject Query(Type constants, string query, Func<int, string[]> displayNames = null)
    {
        string key = Key(query ?? "");
        if (key.Length == 0) throw new ArgumentException("请给出原版英文内部名称或名称片段。");
        bool numeric = int.TryParse(key, out int id);
        var matches = constants.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.Name != "Count" &&
                (field.FieldType == typeof(int) || field.FieldType == typeof(short) || field.FieldType == typeof(ushort) || field.FieldType == typeof(byte)))
            .Select(field => new { Name = field.Name, Id = Convert.ToInt32(field.GetRawConstantValue()),
                Labels = displayNames?.Invoke(Convert.ToInt32(field.GetRawConstantValue())) ?? Array.Empty<string>() })
            .Where(entry => numeric ? entry.Id == id : Key(entry.Name).Contains(key) || entry.Labels.Any(label => Key(label).Contains(key)))
            .OrderByDescending(entry => Key(entry.Name) == key || entry.Labels.Any(label => Key(label) == key))
            .ThenBy(entry => entry.Name, StringComparer.Ordinal)
            .ToArray();
        var exact = matches.Where(entry => numeric || Key(entry.Name) == key || entry.Labels.Any(label => Key(label) == key)).ToArray();
        if (exact.Length > 0) matches = exact;
        return new JsonObject
        {
            ["found"] = matches.Length > 0, ["total"] = matches.Length, ["truncated"] = matches.Length > 12,
            ["matches"] = new JsonArray(matches.Take(12).Select(entry => (JsonNode)new JsonObject
            {
                ["internal"] = entry.Name, ["type"] = entry.Id,
                ["exact"] = numeric || Key(entry.Name) == key || entry.Labels.Any(label => Key(label) == key),
                ["display_names"] = new JsonArray(entry.Labels.Select(label => (JsonNode)JsonValue.Create(label)).ToArray())
            }).ToArray()),
            ["source"] = "Loaded vanilla ID constants and any supplied current-language map labels. Lookup only; not inventory, drop, spawn or kill evidence. Mod-registered IDs are not included."
        };
    }
}
