using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TerraBlind;

public sealed class ScriptProgress
{
    readonly object gate = new();
    readonly Dictionary<string, JsonObject> steps = new();
    readonly Queue<JsonObject> calls = new();
    int revision, segment;
    bool running;
    public bool Running { get { lock (gate) return running; } }
    JsonObject last;
    JsonObject watchPolicy;
    string executedSource;
    JsonObject intentMemory;
    internal JsonObject IntentMemory { get { lock (gate) return intentMemory?.DeepClone().AsObject(); } }
    internal void RecordIntents(JsonObject state)
    {
        lock (gate) intentMemory = new JsonObject { ["completed"] = state["completed"]?.DeepClone(),
            ["blocked"] = state["blocked"]?.DeepClone(), ["verified_actions"] = state["verified_actions"]?.DeepClone() };
    }
    int stepCalls;
    JsonObject navigationFailure, pendingNavigation;
    static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public int Completed { get { lock (gate) return steps.Values.Count(s => s["status"].ToString() == "completed"); } }
    public int Segment { get { lock (gate) return segment; } }
    public bool Yielded { get { lock (gate) return !running && last?["value"] is JsonObject value && value["status"]?.ToString() is "yield" or "needs_review"; } }
    public bool ReviewRequested { get { lock (gate) return !running && last?["value"] is JsonObject value && value["wake"] is JsonObject; } }

    internal static bool Failed(JsonObject result) => result["error"] != null || result["ok"]?.ToString() == "false"
        || result["outcome"]?.ToString() is "interrupted" or "interrupted_by_death" or "no_progress";

    internal bool IntentContinuationReady(string source)
    {
        lock (gate) return !running && executedSource == source && intentMemory != null && last?["error"] == null
            && last?["value"] is JsonObject value && value["status"]?.ToString() == "continue"
            && value["reason"]?.ToString() == "intent_segment_complete" && value["wake"] == null;
    }

    internal bool HasPreparedContinuation(string source, JsonObject action = null)
    {
        lock (gate)
        {
            if (IntentContinuationReady(source)) return true;
            if (running || executedSource == null || last?["error"] != null
                || last?["value"] is not JsonObject value || value["status"]?.ToString() != "yield" || value["wake"] != null) return false;
            if (source != executedSource) return last["failure"] == null;
            if (action?["handle"]?.ToString() is not string handle || action["status"]?.ToString() != "completed"
                || action["previous_attempt"]?["status"]?.ToString() != "failed"
                || steps.Values.Any(step => step["status"]?.ToString() == "uncertain")) return false;
            if (value["continue_after"] != null)
                return value["continue_after"] is JsonValue expected && expected.TryGetValue<string>(out string expectedHandle)
                    && expectedHandle == handle;

            return value["handle"]?.ToString() == handle && value["reason"]?.ToString() == "navigation_no_progress"
                && value["collection"] is JsonObject collection && collection["managed_handle"]?.ToString() == handle
                && collection["outcome"]?.ToString() == "navigation_no_progress"
                && collection["targets"] is JsonArray { Count: > 0 } targets
                && targets.All(target => target?["inventory_delta"]?.ToString() == "0")
                && action["tool"]?.ToString() == "gather" && action["result"]?["outcome"]?.ToString() == "enough"
                && action["previous_attempt"]?["result"]?["outcome"]?.ToString() == "navigation_no_progress"
                && last["tool_counts"] is JsonObject calls && calls["gather"]?.ToString() == "1"
                && calls.All(call => call.Key is "gather" or "find_tiles" or "get_state");
        }
    }

    public void Begin(string source = null)
    {
        lock (gate)
        {
            if (running) throw new InvalidOperationException("workflow正在执行，不能并发续接。");
            if (executedSource != source) intentMemory = null;
            executedSource = source;
            pendingNavigation = null;
            running = true; segment++; revision++;
        }
    }

    public JsonObject Enter(string id)
    {
        lock (gate)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 80) throw new ArgumentException("step id需要1..80字符。");
            if (steps.TryGetValue(id, out var saved))
            {
                if (saved["status"].ToString() == "uncertain" && saved["repeatable_navigation"]?.GetValue<bool>() == true)
                {
                    saved["previous_failure"] = saved["failure"]?.DeepClone();
                    saved.Remove("repeatable_navigation"); saved.Remove("failure");
                    saved["status"] = "running"; saved["segment"] = segment;
                    stepCalls = 0; navigationFailure = null; revision++;
                    return new JsonObject { ["execute"] = true };
                }
                if (saved["status"].ToString() != "completed")
                    throw new InvalidOperationException("step_requires_reconciliation: " + id);
                return new JsonObject { ["execute"] = false, ["value"] = saved["value"]?.DeepClone() };
            }
            if (steps.Count >= 128) throw new InvalidOperationException("workflow最多128个步骤。");
            if (steps.Values.Any(s => s["status"].ToString() == "running"))
                throw new InvalidOperationException("step不支持嵌套。");
            steps[id] = new JsonObject { ["id"] = id, ["status"] = "running", ["segment"] = segment };
            stepCalls = 0; navigationFailure = null;
            revision++;
            return new JsonObject { ["execute"] = true };
        }
    }

    public void Complete(string id, JsonNode value)
    {
        lock (gate)
        {
            if (!steps.TryGetValue(id, out var step) || step["status"].ToString() != "running")
                throw new InvalidOperationException("step不是当前运行步骤。");
            if ((value?.ToJsonString(JsonOptions).Length ?? 0) > 8000) throw new ArgumentException("step返回值超过8000字符。");
            if (value is JsonObject result && Failed(result))
                throw new InvalidOperationException("step返回失败；保留证据，不能标为完成。");
            step["status"] = "completed"; step["value"] = value?.DeepClone(); revision++;
        }
    }

    internal void StartCall(string name, JsonObject args)
    {
        lock (gate) pendingNavigation = name == "nav_to" && stepCalls == 0
            && steps.Values.Any(s => s["status"]?.ToString() == "running") ? args.DeepClone().AsObject() : null;
    }

    public void Record(string name, JsonObject args, string result)
    {
        lock (gate)
        {
            pendingNavigation = null;
            if (steps.Values.Any(s => s["status"]?.ToString() == "running"))
            {
                navigationFailure = null;
                if (++stepCalls == 1 && name == "nav_to" && JsonNode.Parse(result) is JsonObject navigation
                    && navigation["outcome"]?.ToString() == "no_progress")
                    navigationFailure = navigation;
            }
            if (name == "watch" && JsonNode.Parse(result) is JsonObject observed
                && observed["error"] == null && observed["events"] is JsonArray)
                watchPolicy = (JsonObject)args.DeepClone();
            static string Clip(string text) => text.Length > 1000 ? text[..1000] + "…" : text;
            calls.Enqueue(new JsonObject { ["tool"] = name, ["args"] = Clip(args.ToJsonString()),
                ["result"] = Clip(result), ["segment"] = segment });
            if (calls.Count > 12) calls.Dequeue();
        }
    }

    public void End(JsonObject result)
    {
        lock (gate)
        {
            foreach (var step in steps.Values.Where(s => s["status"].ToString() == "running"))
            {
                step["status"] = "uncertain";
                if (navigationFailure != null && result["error"]?.ToString() == "script_failed")
                {
                    step["repeatable_navigation"] = true;
                    step["failure"] = navigationFailure.DeepClone();
                }
                else if (pendingNavigation != null && result["error"]?.ToString() == "cancelled")
                {
                    step["repeatable_navigation"] = true;
                    step["failure"] = new JsonObject { ["outcome"] = "cancelled", ["tool"] = "nav_to",
                        ["args"] = pendingNavigation.DeepClone() };
                }
            }
            pendingNavigation = null;
            last = new JsonObject { ["error"] = result["error"]?.DeepClone(), ["detail"] = result["detail"]?.DeepClone(),
                ["value"] = result["value"]?.DeepClone(), ["tool_counts"] = result["tool_counts"]?.DeepClone(),
                ["tool_calls"] = result["tool_calls"]?.DeepClone(), ["elapsed_ms"] = result["elapsed_ms"]?.DeepClone(),
                ["skill_calls"] = result["skill_calls"]?.DeepClone(),
                ["failure"] = result["failure"]?.DeepClone(), ["output_limit"] = result["output_limit"]?.DeepClone() };
            running = false; revision++;
        }
    }

    public JsonObject Resolve(string id, int expectedRevision, bool completed, JsonNode value, JsonObject observed)
    {
        lock (gate)
        {
            if (steps.Values.Any(s => s["status"].ToString() == "running") || revision != expectedRevision)
                throw new InvalidOperationException("workflow状态已改变，请重新读取。");
            if (!steps.TryGetValue(id, out var step) || step["status"].ToString() != "uncertain")
                throw new InvalidOperationException("只能核对已有uncertain步骤，complete不能创建记录。保存新的决定或理由，请在run_script指定同一workflow，使用step('稳定步骤id',()=>({decision:'skip',reason:'实际依据'}))；step是JS函数，不是tool。已完成步骤无需complete。");
            if ((value?.ToJsonString(JsonOptions).Length ?? 0) > 8000) throw new ArgumentException("核对结果超过8000字符。");
            if (completed)
            {
                step["status"] = "completed"; step["value"] = value?.DeepClone();
                step["observed"] = observed.DeepClone();
            }
            else steps.Remove(id);
            revision++;
            return Snapshot();
        }
    }

    public JsonObject Summary()
    {
        lock (gate)
        {
            var saved = new JsonArray();
            foreach (var step in steps.Values)
            {
                var brief = (JsonObject)step.DeepClone();
                if (brief["value"] is JsonObject value && value["handle"] is JsonValue handle
                    && value["tool"] is JsonValue && value["status"] is JsonValue)
                {
                    brief["value"] = new JsonObject { ["handle"] = handle.DeepClone(), ["tool"] = value["tool"].DeepClone() };
                    brief["value_omitted"] = true;
                    brief["current_state_tool"] = "action_query";
                }
                else if ((brief["value"]?.ToJsonString(JsonOptions).Length ?? 0) > 512)
                { brief.Remove("value"); brief["value_omitted"] = true; }
                saved.Add(brief);
            }
            JsonObject execution = null;
            if (last != null)
            {
                execution = new JsonObject();
                foreach (string field in new[] { "error", "tool_counts", "tool_calls", "elapsed_ms", "failure", "output_limit", "skill_calls" })
                    execution[field] = last[field]?.DeepClone();
                if (last["detail"] is JsonValue detail)
                {
                    string text = detail.ToString();
                    execution["detail"] = text.Length > 1000 ? text[..1000] + "…" : text;
                }
            }
            return new JsonObject { ["revision"] = revision, ["segment"] = segment, ["running"] = running,
                ["steps"] = saved, ["last_error"] = last?["error"]?.DeepClone(),
                ["last_execution"] = execution,
                ["watch_policy"] = watchPolicy?.DeepClone(),
                ["detail_tool"] = "script_progress" };
        }
    }

    public JsonObject Snapshot()
    {
        lock (gate) return new JsonObject { ["revision"] = revision, ["segment"] = segment, ["running"] = running,
            ["watch_policy"] = watchPolicy?.DeepClone(),
            ["steps"] = new JsonArray(steps.Values.Select(s => s.DeepClone()).ToArray()),
            ["last_segment"] = last?.DeepClone(), ["recent_calls"] = new JsonArray(calls.Select(c => c.DeepClone()).ToArray()) };
    }
}
