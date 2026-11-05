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
    bool _dag;
    public int Cursor => _cursor;
    public int Progress => _progress;
    public bool RequiresNodeExecution => _dag && Incomplete;
    public bool Incomplete => _steps.Any(s => s["status"]?.ToString() != "completed");

    public void Reset() { _steps.Clear(); _goal = null; _cursor = _progress = 0; _dag = false; _revision++; }

    public JsonObject Create(JsonObject args, Func<JsonObject, int> measure)
    {
        if (Incomplete && args["replace"]?.GetValue<bool>() != true)
            throw new InvalidOperationException("已有未完成计划；继续当前步骤，或明确 replace=true。旧计划替换后不可恢复。");
        string goal = args["goal"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(goal) || args["steps"] is not JsonArray steps || steps.Count == 0 || steps.Count > 20)
            throw new InvalidOperationException("计划需要 goal 和 1..20 个步骤。");
        bool dag = args["mode"]?.ToString() == "dag";
        var parsed = ParseSteps(steps, dag, measure);
        ValidateDependencies(parsed);
        _steps.Clear(); _steps.AddRange(parsed); _goal = goal; _cursor = _progress = 0;
        _dag = dag;
        SelectReady();
        _revision++;
        return Snapshot();
    }

    static List<JsonObject> ParseSteps(JsonArray steps, bool dag, Func<JsonObject, int> measure)
    {
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
            if (dag && (step["depends_on"] is not JsonArray || string.IsNullOrWhiteSpace(step["script"]?.GetValue<string>())))
                throw new InvalidOperationException("DAG节点需要depends_on数组和可执行script；根节点使用空依赖数组。");
            copy["depends_on"] = dag ? step["depends_on"].DeepClone()
                : new JsonArray(parsed.Count == 0 ? Array.Empty<JsonNode>() : new[] { JsonValue.Create(parsed[^1]["id"].ToString()) });
            if (dag)
            {
                copy["script"] = step["script"].DeepClone();
                copy["workflow"] = "dag-" + Guid.NewGuid().ToString("N");
            }
            foreach (var condition in conditions)
            {
                var check = condition as JsonObject ?? throw new InvalidOperationException("条件必须是对象。");
                string kind = check["kind"]?.GetValue<string>();
                if (kind is not ("permanent_health" or "position" or "item" or "equipped" or "npc_kills" or "npc_alive" or "chests_looted") || check["count"]?.GetValue<int>() is not > 0
                    || kind is not ("permanent_health" or "position" or "chests_looted") && check["type"]?.GetValue<int>() is not > 0)
                    throw new InvalidOperationException("条件仅支持 permanent_health、position、item、equipped、npc_kills、npc_alive、chests_looted；count及需要的type必须为正数。");
                var verified = new JsonObject { ["kind"] = kind, ["count"] = check["count"].DeepClone() };
                if (kind == "position")
                {
                    int radius = check["radius"]?.GetValue<int>() ?? 2;
                    if (check["count"].GetValue<int>() != 1 || check["x"]?.GetValue<int>() is not >= 0
                        || check["y"]?.GetValue<int>() is not >= 0 || radius is < 1 or > 60)
                        throw new InvalidOperationException("position需要count=1、非负格坐标x/y、radius为1..60格（默认2）。");
                    verified["x"] = check["x"].DeepClone();
                    verified["y"] = check["y"].DeepClone();
                    verified["radius"] = radius;
                }
                else if (kind is not ("permanent_health" or "chests_looted")) verified["type"] = check["type"].DeepClone();
                int value = measure(verified);
                if (kind is "npc_kills" or "chests_looted") verified["baseline"] = value;
                copy["conditions"].AsArray().Add(verified);
                copy["best_actual"].AsArray().Add(Math.Min(kind is "npc_kills" or "chests_looted" ? 0 : value, check["count"].GetValue<int>()));
            }
            parsed.Add(copy);
        }
        return parsed;
    }

    static void ValidateDependencies(List<JsonObject> steps)
    {
        var byId = steps.ToDictionary(s => s["id"].GetValue<string>());
        var visiting = new HashSet<string>();
        var visited = new HashSet<string>();
        void Visit(string id)
        {
            if (visited.Contains(id)) return;
            if (!visiting.Add(id)) throw new InvalidOperationException("任务依赖存在环。");
            var seen = new HashSet<string>();
            foreach (var dependency in byId[id]["depends_on"].AsArray())
            {
                string parent = dependency?.GetValue<string>();
                if (parent == null || !byId.ContainsKey(parent) || !seen.Add(parent))
                    throw new InvalidOperationException("依赖必须引用存在且不重复的节点。");
                Visit(parent);
            }
            visiting.Remove(id); visited.Add(id);
        }
        foreach (string id in byId.Keys) Visit(id);
    }

    bool Ready(JsonObject step) => step["status"]?.ToString() != "completed"
        && step["depends_on"].AsArray().All(id => _steps.Any(s => s["id"]?.ToString() == id.ToString()
            && s["status"]?.ToString() == "completed"));

    void SelectReady()
    {
        _cursor = _steps.FindIndex(Ready);
        if (_cursor < 0) _cursor = _steps.Count;
        else if (_steps[_cursor]["status"]?.ToString() == "pending") _steps[_cursor]["status"] = "active";
    }

    public JsonObject Select(string id)
    {
        int index = _steps.FindIndex(s => s["id"]?.ToString() == id);
        if (index < 0 || !Ready(_steps[index])) throw new InvalidOperationException("节点不存在、已完成或前置尚未验收。");
        if (Incomplete && _steps[_cursor]["status"]?.ToString() == "active") _steps[_cursor]["status"] = "pending";
        _cursor = index;
        if (_steps[index]["status"]?.ToString() == "pending") _steps[index]["status"] = "active";
        _revision++;
        return Snapshot();
    }

    public JsonObject Script(string id)
    {
        var step = _steps.FirstOrDefault(s => s["id"]?.ToString() == id);
        if (!_dag || step == null || !Ready(step)) throw new InvalidOperationException("需要前置已验收的DAG节点。");
        return new JsonObject { ["code"] = step["script"].DeepClone(), ["workflow"] = step["workflow"].DeepClone() };
    }

    public JsonObject Revise(JsonObject args, Func<JsonObject, int> measure)
    {
        if (!_dag || !Incomplete || args["expected_revision"]?.GetValue<int>() != _revision)
            throw new InvalidOperationException("需要当前未完成DAG的revision。");
        string reason = args["reason"]?.GetValue<string>();
        var changes = args["nodes"] as JsonArray ?? new JsonArray();
        var additions = args["add_nodes"] as JsonArray ?? new JsonArray();
        if (string.IsNullOrWhiteSpace(reason) || changes.Count + additions.Count == 0 || additions.Count + _steps.Count > 20)
            throw new ArgumentException("需要reason及nodes/add_nodes；计划总节点数不能超过20。");
        var revised = _steps.Select(s => (JsonObject)s.DeepClone()).ToList();
        var added = ParseSteps(additions, true, measure);
        if (added.Any(a => revised.Any(s => s["id"]?.ToString() == a["id"]?.ToString())))
            throw new InvalidOperationException("新增节点id已存在；修改已有节点请用nodes。");
        foreach (var step in added)
        {
            step["added_reason"] = reason;
            step["added_revision"] = _revision;
        }
        var seen = new HashSet<string>();
        foreach (var change in changes)
        {
            string id = change["id"]?.GetValue<string>();
            var step = revised.FirstOrDefault(s => s["id"]?.ToString() == id);
            if (step == null || !seen.Add(id) || step["status"]?.ToString() == "completed")
                throw new InvalidOperationException("只能修改已有未完成节点，不得重复修改。");
            if (change["script"] == null && change["depends_on"] == null)
                throw new ArgumentException("需要script或depends_on修改。");
            if (change["script"] != null)
            {
                if (string.IsNullOrWhiteSpace(change["script"].GetValue<string>())) throw new ArgumentException("script不能为空。");
                step["script"] = change["script"].DeepClone();
            }
            if (change["depends_on"] != null)
                step["depends_on"] = (change["depends_on"] as JsonArray ?? throw new ArgumentException("depends_on需要数组。")).DeepClone();
            var history = step["revisions"] as JsonArray;
            if (history == null) step["revisions"] = history = new JsonArray();
            history.Add(new JsonObject { ["reason"] = reason, ["revision"] = _revision,
                ["before_script"] = _steps.First(s => s["id"]?.ToString() == id)["script"].DeepClone(),
                ["before_dependencies"] = _steps.First(s => s["id"]?.ToString() == id)["depends_on"].DeepClone() });
        }
        revised.AddRange(added);
        ValidateDependencies(revised);
        _steps.Clear(); _steps.AddRange(revised);
        if (!Ready(_steps[_cursor]))
        {
            if (_steps[_cursor]["status"]?.ToString() == "active") _steps[_cursor]["status"] = "pending";
            SelectReady();
        }
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
        foreach (var step in _steps.Where(Ready))
        {
            var conditions = step["conditions"].AsArray();
            var best = step["best_actual"].AsArray();
            for (int i = 0; i < conditions.Count; i++)
            {
                var condition = conditions[i].AsObject();
                int actual = measure(condition) - (condition["kind"]?.ToString() is "npc_kills" or "chests_looted" ? condition["baseline"].GetValue<int>() : 0);
                int capped = Math.Min(actual, condition["count"].GetValue<int>());
                if (capped <= best[i].GetValue<int>()) continue;
                best[i] = capped;
                _progress++;
            }
        }
    }

    public JsonObject Advance(string id, Func<JsonObject, int> measure)
    {
        var step = _steps.FirstOrDefault(s => s["id"]?.ToString() == id);
        if (step == null || !Ready(step))
            throw new InvalidOperationException("只能验证前置已完成的未完成节点。");
        Observe(measure);
        var evidence = new JsonArray();
        bool passed = true;
        foreach (var node in step["conditions"].AsArray())
        {
            var condition = node.AsObject();
            int actual = measure(condition) - (condition["kind"]?.GetValue<string>() is "npc_kills" or "chests_looted"
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
            if (_steps[_cursor] == step) SelectReady();
            _revision++;
        }
        var result = Snapshot();
        result["verified"] = passed;
        return result;
    }

    public void Record(string tool, string output, bool action = false, string nodeId = null)
    {
        if (!Incomplete) return;
        var step = nodeId == null ? (_dag ? null : _steps[_cursor])
            : _steps.FirstOrDefault(s => s["id"]?.ToString() == nodeId && s["status"]?.ToString() != "completed");
        if (step == null) return;
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
        step["last_action"] = new JsonObject
        {
            ["tool"] = tool, ["result"] = output.Length <= 6000 ? output : output[..6000],
            ["utc"] = DateTime.UtcNow.ToString("O")
        };
        bool failed = facts != null && (facts["empty"]?.ToString() == "true" || action &&
            (facts["error"] != null || facts["ok"]?.ToString() == "false" || facts["accepted"]?.ToString() == "false"
             || facts["done"]?.ToString() == "false" || facts["status"]?.ToString() is "failed" or "stopped_short"
             || facts["outcome"]?.ToString() is "timeout" or "no_progress" or "stalled" or "unbreakable" or "out_of_reach"
                 or "no_tiles" or "stuck" or "partial" or "not_placed" or "interrupted_by_death"));
        if (failed) step["blocking_evidence"] = step["last_action"].DeepClone();
        else if (facts != null && facts["error"] == null && (action || step["blocking_evidence"]?["tool"]?.ToString() == tool))
        {
            if (step["status"]?.ToString() == "blocked")
            {
                step["previous_failure"] = new JsonObject { ["reason"] = step["failure"]?.DeepClone(),
                    ["evidence"] = step["blocking_evidence"]?.DeepClone() };
                step.Remove("failure");
                step["status"] = _steps[_cursor] == step ? "active" : "pending";
            }
            step.Remove("blocking_evidence");
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
        foreach (var terminal in _steps.Where(s => !_steps.Any(other => other["depends_on"].AsArray()
            .Any(id => id.ToString() == s["id"].ToString()))))
            foreach (var node in terminal["conditions"].AsArray())
            {
                var condition = node.AsObject();
                int actual = measure(condition) - (condition["kind"]?.ToString() is "npc_kills" or "chests_looted" ? condition["baseline"].GetValue<int>() : 0);
                bool met = actual >= condition["count"].GetValue<int>();
                passed &= met;
                evidence.Add(new JsonObject { ["step"] = terminal["id"].DeepClone(),
                    ["condition"] = condition.DeepClone(), ["actual"] = actual, ["met"] = met });
            }
        return new JsonObject { ["verified"] = passed, ["plan"] = Snapshot(), ["final_evidence"] = evidence };
    }

    public JsonObject Summary()
    {
        var result = Snapshot();
        foreach (var step in result["steps"].AsArray().OfType<JsonObject>())
        {
            step.Remove("last_action"); step.Remove("previous_failure");
            if (step["blocking_evidence"] is JsonObject evidence)
            {
                string text = evidence["result"]?.ToString() ?? "";
                step["blocking_evidence"] = new JsonObject { ["tool"] = evidence["tool"]?.DeepClone(),
                    ["utc"] = evidence["utc"]?.DeepClone(), ["result_excerpt"] = text[..Math.Min(512, text.Length)] };
            }
        }
        return result;
    }

    public JsonObject Snapshot() => new()
    {
        ["goal"] = _goal, ["status"] = _steps.Count == 0 ? "none" : Incomplete ? _steps[_cursor]["status"]?.DeepClone() : JsonValue.Create("completed"),
        ["cursor"] = _cursor, ["progress_revision"] = _progress, ["revision"] = _revision,
        ["mode"] = _dag ? "dag" : "sequential",
        ["ready_steps"] = new JsonArray(_steps.Where(Ready).Select(s => s["id"].DeepClone()).ToArray()),
        ["current_step"] = Incomplete ? _steps[_cursor]["id"]?.DeepClone() : null,
        ["steps"] = new JsonArray(_steps.Select(step => step.DeepClone()).ToArray())
    };
}
