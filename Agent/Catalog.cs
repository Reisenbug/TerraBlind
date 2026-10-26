using System;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;

namespace TerraBlind;

public static class AgentCatalog
{
    static string Key(string name) => name.Replace(" ", "").Replace("_", "").ToLowerInvariant();

    public static JsonObject Query(Type constants, string query)
    {
        string key = Key(query ?? "");
        if (key.Length == 0) throw new ArgumentException("请给出原版英文内部名称或名称片段。");
        bool numeric = int.TryParse(key, out int id);
        var matches = constants.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.Name != "Count" &&
                (field.FieldType == typeof(int) || field.FieldType == typeof(short) || field.FieldType == typeof(ushort) || field.FieldType == typeof(byte)))
            .Where(field => numeric ? Convert.ToInt32(field.GetRawConstantValue()) == id : Key(field.Name).Contains(key))
            .OrderByDescending(field => Key(field.Name) == key).ThenBy(field => field.Name, StringComparer.Ordinal)
            .ToArray();
        return new JsonObject
        {
            ["found"] = matches.Length > 0, ["total"] = matches.Length, ["truncated"] = matches.Length > 12,
            ["matches"] = new JsonArray(matches.Take(12).Select(field => (JsonNode)new JsonObject
            {
                ["internal"] = field.Name, ["type"] = Convert.ToInt32(field.GetRawConstantValue()), ["exact"] = numeric || Key(field.Name) == key
            }).ToArray()),
            ["source"] = "Loaded vanilla ID constants. Identifier lookup only; not inventory, drop, spawn or kill evidence. Mod-registered IDs and translated names are not included."
        };
    }
}
