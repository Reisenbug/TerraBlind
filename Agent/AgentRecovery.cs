using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace TerraBlind;

public sealed class AgentRecovery
{
    readonly Dictionary<string, int> attempts = new();
    JsonObject latest;
    int revision, offered, rounds;
    public int Revision => revision;
    public bool Denied(string tool, JsonObject args) => attempts.GetValueOrDefault(tool + ":" + args.ToJsonString()) >= 3;
    public bool Exhausted => latest != null && (latest["attempts"].GetValue<int>() >= 3 || rounds >= 3);

    public void Record(string tool, JsonObject args, string output)
    {
        JsonObject result;
        try { result = JsonNode.Parse(output) as JsonObject; }
        catch (System.Text.Json.JsonException) { return; }
        if (result == null) return;
        bool failed = result["error"] != null || result["ok"]?.ToString() == "false" || result["accepted"]?.ToString() == "false"
            || result["status"]?.ToString() is "failed" or "stopped_short"
            || result["outcome"]?.ToString() is "source_unavailable" or "no_progress" or "stalled" or "stuck" or "timeout";
        if (!failed)
        {
            if (latest?["tool"]?.ToString() == tool) latest = null;
            return;
        }
        string key = tool + ":" + args.ToJsonString();
        int count = attempts.GetValueOrDefault(key) + 1;
        attempts[key] = count;
        var evidence = new JsonObject();
        foreach (string field in new[] { "error", "detail", "outcome", "status", "reason", "requested_name", "name_candidates", "targets", "state", "dist" })
            if (result[field] != null) evidence[field] = result[field].DeepClone();
        latest = new JsonObject { ["tool"] = tool, ["args"] = args.DeepClone(), ["evidence"] = evidence,
            ["attempts"] = count, ["revision"] = ++revision };
    }

    public bool Offer()
    {
        if (latest == null || offered == revision || Exhausted) return false;
        offered = revision; rounds++; return true;
    }

    public JsonObject Snapshot() => new() { ["latest"] = latest?.DeepClone(), ["revision"] = revision,
        ["repair_rounds"] = rounds, ["exhausted"] = Exhausted };
    public void Reset() { attempts.Clear(); latest = null; revision = offered = rounds = 0; }
}
