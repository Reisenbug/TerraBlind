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
    JsonObject last;
    static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public int Completed { get { lock (gate) return steps.Values.Count(s => s["status"].ToString() == "completed"); } }
    public int Segment { get { lock (gate) return segment; } }
    public bool Yielded { get { lock (gate) return !running && last?["value"] is JsonObject value && value["status"]?.ToString() == "yield"; } }

    public void Begin()
    {
        lock (gate)
        {
            if (running) throw new InvalidOperationException("workflow正在执行，不能并发续接。");
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
                if (saved["status"].ToString() != "completed")
                    throw new InvalidOperationException("step_requires_reconciliation: " + id);
                return new JsonObject { ["execute"] = false, ["value"] = saved["value"]?.DeepClone() };
            }
            if (steps.Count >= 128) throw new InvalidOperationException("workflow最多128个步骤。");
            if (steps.Values.Any(s => s["status"].ToString() == "running"))
                throw new InvalidOperationException("step不支持嵌套。");
            steps[id] = new JsonObject { ["id"] = id, ["status"] = "running", ["segment"] = segment };
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
            if (value is JsonObject result && (result["error"] != null || result["ok"]?.ToString() == "false"
                || result["outcome"]?.ToString() is "interrupted" or "interrupted_by_death" or "no_progress"))
                throw new InvalidOperationException("step返回失败；保留证据，不能标为完成。");
            step["status"] = "completed"; step["value"] = value?.DeepClone(); revision++;
        }
    }

    public void Record(string name, JsonObject args, string result)
    {
        lock (gate)
        {
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
            foreach (var step in steps.Values.Where(s => s["status"].ToString() == "running")) step["status"] = "uncertain";
            last = new JsonObject { ["error"] = result["error"]?.DeepClone(), ["detail"] = result["detail"]?.DeepClone(),
                ["value"] = result["value"]?.DeepClone(), ["tool_counts"] = result["tool_counts"]?.DeepClone(),
                ["tool_calls"] = result["tool_calls"]?.DeepClone(), ["elapsed_ms"] = result["elapsed_ms"]?.DeepClone(),
                ["failure"] = result["failure"]?.DeepClone() };
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
                throw new InvalidOperationException("只能核对uncertain步骤。");
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
                if ((brief["value"]?.ToJsonString(JsonOptions).Length ?? 0) > 512)
                { brief.Remove("value"); brief["value_omitted"] = true; }
                saved.Add(brief);
            }
            JsonObject execution = null;
            if (last != null)
            {
                execution = new JsonObject();
                foreach (string field in new[] { "error", "tool_counts", "tool_calls", "elapsed_ms", "failure" })
                    execution[field] = last[field]?.DeepClone();
            }
            return new JsonObject { ["revision"] = revision, ["segment"] = segment, ["running"] = running,
                ["steps"] = saved, ["last_error"] = last?["error"]?.DeepClone(),
                ["last_execution"] = execution,
                ["detail_tool"] = "script_progress" };
        }
    }

    public JsonObject Snapshot()
    {
        lock (gate) return new JsonObject { ["revision"] = revision, ["segment"] = segment, ["running"] = running,
            ["steps"] = new JsonArray(steps.Values.Select(s => s.DeepClone()).ToArray()),
            ["last_segment"] = last?.DeepClone(), ["recent_calls"] = new JsonArray(calls.Select(c => c.DeepClone()).ToArray()) };
    }
}
