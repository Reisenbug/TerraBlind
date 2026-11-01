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
    int _revision;
    public int Cursor => _cursor;
    public int Progress => _progress;
    public bool Incomplete => _steps.Count > _cursor;

    public void Reset() { _steps.Clear(); _goal = null; _cursor = _progress = 0; _revision++; }

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
        _revision++;
        return Snapshot();
    }

    public JsonObject Patch(JsonObject args, Func<JsonObject, int> measure)
    {
        if (!Incomplete || args["expected_revision"]?.GetValue<int>() != _revision)
            throw new InvalidOperationException("计划已改变或已完成，请重新读取task_plan.revision。");
        string reason = args["reason"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(reason) || args["changes"] is not JsonArray changes || changes.Count is < 1 or > 32)
            throw new ArgumentException("修正需要reason与1..32项changes。");
        var edits = new List<(JsonObject Step, int Index, JsonObject Before, JsonObject After, int Actual)>();
        var seen = new HashSet<string>();
        foreach (var node in changes)
        {
            string id = node["step"]?.GetValue<string>();
            int index = node["condition"]?.GetValue<int>() ?? -1;
            var step = _steps.FirstOrDefault(s => s["id"]?.ToString() == id);
            if (step == null || step["status"]?.ToString() == "completed" || !seen.Add(id + ":" + index)
                || index < 0 || index >= step["conditions"].AsArray().Count)
                throw new InvalidOperationException("只能修正未完成步骤的有效且不重复条件。");
            var before = step["conditions"][index].AsObject();
            int type = node["type"]?.GetValue<int>() ?? 0;
            if (before["kind"]?.ToString() is not ("item" or "equipped" or "npc_alive") || type <= 0
                || before["type"]?.GetValue<int>() != node["expected_type"]?.GetValue<int>() || before["type"].GetValue<int>() == type)
                throw new InvalidOperationException("仅允许纠正item/equipped/npc_alive的type；不得修改数量、完成步骤或击杀基线。");
            var after = (JsonObject)before.DeepClone(); after["type"] = type;
            edits.Add((step, index, (JsonObject)before.DeepClone(), after, measure(after)));
        }
        foreach (var edit in edits)
        {
            edit.Step["conditions"][edit.Index] = edit.After;
            edit.Step["best_actual"][edit.Index] = Math.Min(edit.Actual, edit.After["count"].GetValue<int>());
            edit.Step.Remove("evidence");
            var history = edit.Step["condition_corrections"] as JsonArray;
            if (history == null) edit.Step["condition_corrections"] = history = new JsonArray();
            history.Add(new JsonObject { ["before"] = edit.Before, ["after"] = edit.After.DeepClone(), ["reason"] = reason });
        }
        _revision++;
        var result = Snapshot(); result["corrected"] = true; result["verified"] = false;
        return result;
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
            _revision++;
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

    public JsonObject VerifyFinal(Func<JsonObject, int> measure)
    {
        var evidence = new JsonArray();
        bool passed = _steps.Count > 0 && !Incomplete;
        if (_steps.Count > 0)
            foreach (var node in _steps[^1]["conditions"].AsArray())
            {
                var condition = node.AsObject();
                int actual = measure(condition) - (condition["kind"]?.ToString() == "npc_kills" ? condition["baseline"].GetValue<int>() : 0);
                bool met = actual >= condition["count"].GetValue<int>();
                passed &= met;
                evidence.Add(new JsonObject { ["condition"] = condition.DeepClone(), ["actual"] = actual, ["met"] = met });
            }
        return new JsonObject { ["verified"] = passed, ["plan"] = Snapshot(), ["final_evidence"] = evidence };
    }

    public JsonObject Snapshot() => new()
    {
        ["goal"] = _goal, ["status"] = _steps.Count == 0 ? "none" : Incomplete ? _steps[_cursor]["status"]?.DeepClone() : JsonValue.Create("completed"),
        ["cursor"] = _cursor, ["progress_revision"] = _progress, ["revision"] = _revision,
        ["current_step"] = Incomplete ? _steps[_cursor]["id"]?.DeepClone() : null,
        ["steps"] = new JsonArray(_steps.Select(step => step.DeepClone()).ToArray())
    };
}
