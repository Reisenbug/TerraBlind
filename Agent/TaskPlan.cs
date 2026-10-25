using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace TerraBlind;

public sealed class AgentTaskPlan
{
    readonly List<JsonObject> _steps = new();
    string _goal;
    int _cursor;
    public int Cursor => _cursor;
    public bool Incomplete => _steps.Count > _cursor;

    public void Reset() { _steps.Clear(); _goal = null; _cursor = 0; }

    public JsonObject Create(JsonObject args, Func<JsonObject, int> measure)
    {
        if (Incomplete && args["replace"]?.GetValue<bool>() != true)
            throw new InvalidOperationException("已有未完成计划；继续当前步骤，或明确 replace=true。旧计划替换后不可恢复。");
        string goal = args["goal"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(goal) || args["steps"] is not JsonArray steps || steps.Count == 0 || steps.Count > 20)
            throw new InvalidOperationException("计划需要 goal 和 1..20 个步骤。");
        var parsed = new List<JsonObject>();
        var ids = new HashSet<string>();
        foreach (var node in steps)
        {
            var step = node as JsonObject ?? throw new InvalidOperationException("步骤必须是对象。");
            string id = step["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(id) || !ids.Add(id) || string.IsNullOrWhiteSpace(step["description"]?.GetValue<string>())
                || step["conditions"] is not JsonArray conditions || conditions.Count == 0)
                throw new InvalidOperationException("步骤需要唯一 id、description 和非空 conditions。");
            var copy = (JsonObject)step.DeepClone();
            copy["status"] = "pending";
            foreach (var condition in copy["conditions"].AsArray())
            {
                var check = condition as JsonObject ?? throw new InvalidOperationException("条件必须是对象。");
                string kind = check["kind"]?.GetValue<string>();
                if (kind is not ("permanent_health" or "item" or "npc_kills") || check["count"]?.GetValue<int>() is not > 0
                    || kind != "permanent_health" && check["type"]?.GetValue<int>() is not > 0)
                    throw new InvalidOperationException("条件仅支持 permanent_health、item、npc_kills，count/type 必须为正数。");
                int value = measure(check);
                if (kind == "npc_kills") check["baseline"] = value;
            }
            parsed.Add(copy);
        }
        _steps.Clear(); _steps.AddRange(parsed); _goal = goal; _cursor = 0;
        _steps[0]["status"] = "active";
        return Snapshot();
    }

    public JsonObject Advance(string id, Func<JsonObject, int> measure)
    {
        if (!Incomplete || _steps[_cursor]["id"]?.GetValue<string>() != id)
            throw new InvalidOperationException("只能验证当前步骤，不能跳过步骤。");
        var step = _steps[_cursor];
        var evidence = new JsonArray();
        bool passed = true;
        foreach (var node in step["conditions"].AsArray())
        {
            var condition = node.AsObject();
            int actual = measure(condition) - (condition["kind"]?.GetValue<string>() == "npc_kills"
                ? condition["baseline"].GetValue<int>() : 0);
            bool met = actual >= condition["count"].GetValue<int>();
            passed &= met;
            evidence.Add(new JsonObject { ["condition"] = condition.DeepClone(), ["actual"] = actual, ["met"] = met });
        }
        step["evidence"] = evidence;
        if (passed)
        {
            step["status"] = "completed";
            if (++_cursor < _steps.Count) _steps[_cursor]["status"] = "active";
        }
        var result = Snapshot();
        result["verified"] = passed;
        return result;
    }

    public void Record(string tool, string output)
    {
        if (!Incomplete) return;
        try
        {
            if (JsonNode.Parse(output) is JsonObject result)
            {
                result.Remove("task_plan");
                output = result.ToJsonString();
            }
        }
        catch (System.Text.Json.JsonException) { }
        _steps[_cursor]["last_action"] = new JsonObject
        {
            ["tool"] = tool, ["result"] = output.Length <= 6000 ? output : output[..6000],
            ["utc"] = DateTime.UtcNow.ToString("O")
        };
    }

    public JsonObject Block(string id, string reason)
    {
        if (!Incomplete || _steps[_cursor]["id"]?.GetValue<string>() != id || string.IsNullOrWhiteSpace(reason))
            throw new InvalidOperationException("阻塞报告需要当前步骤 id 和实际原因。");
        _steps[_cursor]["status"] = "blocked";
        _steps[_cursor]["failure"] = reason;
        return Snapshot();
    }

    public JsonObject Snapshot() => new()
    {
        ["goal"] = _goal, ["status"] = _steps.Count == 0 ? "none" : Incomplete ? _steps[_cursor]["status"]?.DeepClone() : JsonValue.Create("completed"),
        ["cursor"] = _cursor, ["current_step"] = Incomplete ? _steps[_cursor]["id"]?.DeepClone() : null,
        ["steps"] = new JsonArray(_steps.Select(step => step.DeepClone()).ToArray())
    };
}
