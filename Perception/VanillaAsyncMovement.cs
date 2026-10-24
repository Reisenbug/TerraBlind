using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Terraria;

namespace TerraBlind;

public sealed class VanillaAsyncMovement
{
    public const int LeadFrames = 30;
    readonly Func<JsonObject, Task<JsonObject>> send;
    readonly Dictionary<long, int> inputs = new();
    Task<JsonObject> pending;
    long handoff;
    long requestedAt;
    bool completionObserved;
    public int PlanningLead { get; private set; } = LeadFrames;
    public int ResponseFrames { get; private set; }
    int requestRevision;
    int currentRevision = -1;
    JsonObject context;
    public int Accepted { get; private set; }
    public int Rejected { get; private set; }
    public string Status { get; private set; } = "idle";
    public bool Busy => pending != null;
    public Vector2[] AcceptedRoute { get; private set; } = Array.Empty<Vector2>();
    public JsonObject LastResult { get; private set; }
    public string LastMismatch { get; private set; }
    public string LastRejection { get; private set; }

    public VanillaAsyncMovement(Func<JsonObject, Task<JsonObject>> transport) => send = transport;

    public int InputAt(long tick) => inputs.TryGetValue(tick, out int key) ? key : 0;

    public void Poll(long tick, Player player, int revision)
    {
        foreach (long old in inputs.Keys.Where(key => key <= tick).ToArray()) inputs.Remove(old);
        if (revision != currentRevision) { inputs.Clear(); currentRevision = revision; }
        if (pending == null || !pending.IsCompleted) return;
        if (!completionObserved)
        {
            ResponseFrames = checked((int)(tick - requestedAt)); completionObserved = true;
            if (pending.IsCompletedSuccessfully) LastResult = pending.Result;
        }
        if (pending.IsFaulted || pending.IsCanceled) { _ = pending.Exception; Reject("worker_error"); return; }
        if (revision != requestRevision) { Reject("revision_changed"); return; }
        if (tick > handoff)
        {
            PlanningLead = Math.Clamp(ResponseFrames + 6, PlanningLead, 90);
            Reject("late_result"); return;
        }
        if (tick < handoff) return;
        if (!VanillaFrame.ContextMatches(context, player, out string difference))
        { LastMismatch = difference; Reject("context_changed"); return; }
        var actual = VanillaPrediction.Observe(player);
        var result = pending.GetAwaiter().GetResult();
        if (result["status"]?.GetValue<string>() != "planned") { Reject("no_safe_plan"); return; }
        var anchor = result["anchor"].AsObject();
        foreach (string key in new[] { "x", "y", "vx", "vy", "wing_time" })
            if (Math.Abs(actual[key].GetValue<float>() - anchor[key].GetValue<float>()) > .01f)
            { LastMismatch = $"{key}: actual={actual[key]} predicted={anchor[key]}"; Reject("state_changed:" + key); return; }
        foreach (string key in new[] { "hp", "dead", "grav_dir", "jump", "available_jumps", "frozen", "webbed", "buff_types", "buff_times", "extra_jumps" })
            if (!JsonNode.DeepEquals(actual[key], anchor[key]))
            { LastMismatch = $"{key}: actual={actual[key]} predicted={anchor[key]}"; Reject("state_changed:" + key); return; }
        var controls = result["controls"].AsArray();
        AcceptedRoute = result["forecast"]["frames"].AsArray().Select(frame => new Vector2(frame["x"].GetValue<float>(), frame["y"].GetValue<float>())).ToArray();
        inputs.Clear();
        for (int i = 0; i < controls.Count; i++) inputs[handoff + i + 1] = controls[i].GetValue<int>();
        Accepted++; Status = "accepted"; LastRejection = null; pending = null;
    }

    void Reject(string reason) { inputs.Clear(); Rejected++; Status = reason; LastRejection = reason; pending = null; }

    public bool Request(JsonObject snapshot, long tick, Vector2 goal, int revision)
    {
        if (pending != null) return false;
        if (revision != currentRevision) { inputs.Clear(); currentRevision = revision; }
        requestedAt = tick; completionObserved = false;
        handoff = tick + PlanningLead; requestRevision = revision;
        var prefix = new JsonArray(Enumerable.Range(1, PlanningLead).Select(i => JsonValue.Create(InputAt(tick + i))).ToArray());
        var request = new JsonObject { ["command"] = "plan", ["snapshot"] = snapshot.DeepClone(),
            ["prefix"] = prefix, ["x"] = goal.X, ["y"] = goal.Y };
        context = request["snapshot"].AsObject();
        pending = send(request); Status = "planning"; return true;
    }
}
