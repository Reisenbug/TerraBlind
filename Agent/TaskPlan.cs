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
    int _progress;
    public int Cursor => _cursor;
    public int Progress => _progress;
    public bool Incomplete => _steps.Count > _cursor;

    public void Reset() { _steps.Clear(); _goal = null; _cursor = _progress = 0; }

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
            var copy = new JsonObject { ["id"] = id, ["description"] = step["description"].DeepClone(),
                ["status"] = "pending", ["conditions"] = new JsonArray(), ["best_actual"] = new JsonArray() };
            foreach (var condition in conditions)
            {
                var check = condition as JsonObject ?? throw new InvalidOperationException("条件必须是对象。");
                string kind = check["kind"]?.GetValue<string>();
                if (kind is not ("permanent_health" or "item" or "equipped" or "npc_kills" or "npc_alive") || check["count"]?.GetValue<int>() is not > 0
                    || kind != "permanent_health" && check["type"]?.GetValue<int>() is not > 0)
                    throw new InvalidOperationException("条件仅支持 permanent_health、item、equipped、npc_kills、npc_alive，count/type 必须为正数。");
                var verified = new JsonObject { ["kind"] = kind, ["count"] = check["count"].DeepClone() };
                if (kind != "permanent_health") verified["type"] = check["type"].DeepClone();
                int value = measure(verified);
                if (kind == "npc_kills") verified["baseline"] = value;
                copy["conditions"].AsArray().Add(verified);
                copy["best_actual"].AsArray().Add(Math.Min(kind == "npc_kills" ? 0 : value, check["count"].GetValue<int>()));
            }
            parsed.Add(copy);
        }
        _steps.Clear(); _steps.AddRange(parsed); _goal = goal; _cursor = _progress = 0;
        _steps[0]["status"] = "active";
        return Snapshot();
    }

    public void Observe(Func<JsonObject, int> measure)
    {
        if (!Incomplete) return;
        var conditions = _steps[_cursor]["conditions"].AsArray();
        var best = _steps[_cursor]["best_actual"].AsArray();
        for (int i = 0; i < conditions.Count; i++)
        {
            var condition = conditions[i].AsObject();
            int actual = measure(condition) - (condition["kind"]?.ToString() == "npc_kills" ? condition["baseline"].GetValue<int>() : 0);
            int capped = Math.Min(actual, condition["count"].GetValue<int>());
            if (capped <= best[i].GetValue<int>()) continue;
            best[i] = capped;
            _progress++;
        }
    }

    public JsonObject Advance(string id, Func<JsonObject, int> measure)
    {
        if (!Incomplete || _steps[_cursor]["id"]?.GetValue<string>() != id)
            throw new InvalidOperationException("只能验证当前步骤，不能跳过步骤。");
        Observe(measure);
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
            _progress++;
            if (++_cursor < _steps.Count) _steps[_cursor]["status"] = "active";
        }
        var result = Snapshot();
        result["verified"] = passed;
        return result;
    }

    public void Record(string tool, string output, bool action = false)
    {
        if (!Incomplete) return;
        JsonObject facts = null;
        try
        {
            if (JsonNode.Parse(output) is JsonObject result)
            {
                facts = result;
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
        bool failed = facts != null && (facts["empty"]?.ToString() == "true" || action &&
            (facts["error"] != null || facts["ok"]?.ToString() == "false" || facts["accepted"]?.ToString() == "false"
             || facts["done"]?.ToString() == "false" || facts["status"]?.ToString() is "failed" or "stopped_short"
             || facts["outcome"]?.ToString() is "timeout" or "no_progress" or "stalled" or "unbreakable" or "out_of_reach"
                 or "no_tiles" or "stuck" or "partial" or "not_placed" or "interrupted_by_death"));
        if (failed) _steps[_cursor]["blocking_evidence"] = _steps[_cursor]["last_action"].DeepClone();
        else if (facts != null && facts["error"] == null && (action || _steps[_cursor]["blocking_evidence"]?["tool"]?.ToString() == tool))
        {
            if (_steps[_cursor]["status"]?.ToString() == "blocked")
            {
                _steps[_cursor]["previous_failure"] = new JsonObject { ["reason"] = _steps[_cursor]["failure"]?.DeepClone(),
                    ["evidence"] = _steps[_cursor]["blocking_evidence"]?.DeepClone() };
                _steps[_cursor].Remove("failure");
                _steps[_cursor]["status"] = "active";
            }
            _steps[_cursor].Remove("blocking_evidence");
        }
    }

    public JsonObject Block(string id, string reason)
    {
        if (!Incomplete || _steps[_cursor]["id"]?.GetValue<string>() != id || string.IsNullOrWhiteSpace(reason))
            throw new InvalidOperationException("阻塞报告需要当前步骤 id 和实际原因。");
        if (_steps[_cursor]["blocking_evidence"] == null)
            throw new InvalidOperationException("尚无实际阻塞证据。成功查询到候选或查询名称拼错不等于无法执行；先尝试可用动作、修正参数或向玩家询问缺失决策。");
        _steps[_cursor]["status"] = "blocked";
        _steps[_cursor]["failure"] = reason;
        return Snapshot();
    }

    public JsonObject Snapshot() => new()
    {
        ["goal"] = _goal, ["status"] = _steps.Count == 0 ? "none" : Incomplete ? _steps[_cursor]["status"]?.DeepClone() : JsonValue.Create("completed"),
        ["cursor"] = _cursor, ["progress_revision"] = _progress, ["current_step"] = Incomplete ? _steps[_cursor]["id"]?.DeepClone() : null,
        ["steps"] = new JsonArray(_steps.Select(step => step.DeepClone()).ToArray())
    };
}
