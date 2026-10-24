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
    readonly Dictionary<long, int> history = new();
    readonly Queue<int> responses = new();
    Task<JsonObject> pending;
    long handoff;
    long requestedAt;
    bool completionObserved;
    public int PlanningLead { get; private set; } = LeadFrames;
    public int ResponseFrames { get; private set; }
    int requestRevision;
    int currentRevision = -1;
    JsonObject context;
    JsonObject lastRequest;
    JsonArray observations = new();
    public int Accepted { get; private set; }
    public int Rejected { get; private set; }
    public int LateAccepted { get; private set; }
    public int Rebased { get; private set; }
    public string Status { get; private set; } = "idle";
    public bool Busy => pending != null;
    public Vector2[] AcceptedRoute { get; private set; } = Array.Empty<Vector2>();
    public JsonObject LastResult { get; private set; }
    public string LastMismatch { get; private set; }
    public string LastRejection { get; private set; }
    public JsonObject LastDivergence { get; private set; }
    public string ActivePlan { get; private set; } = "none";
    public Vector2 ActiveGoal { get; private set; }
    public Vector2 ActiveAnchor { get; private set; }
    long activeStart;
    int activeOffset;

    public VanillaAsyncMovement(Func<JsonObject, Task<JsonObject>> transport) => send = transport;

    public int InputAt(long tick) => inputs.TryGetValue(tick, out int key) ? key : 0;
    public bool HasInputAt(long tick) => inputs.ContainsKey(tick);
    public int PlanFrameAt(long tick) => HasInputAt(tick) ? checked((int)(tick - activeStart)) + activeOffset : -1;

    public void Poll(long tick, Player player, int revision)
    {
        int applied = InputAt(tick);
        history[tick] = (player.controlLeft ? 1 : 0) | (player.controlRight ? 2 : 0) | (player.controlJump ? 4 : 0)
            | (player.controlUp ? 8 : 0) | (player.controlDown ? 16 : 0);
        foreach (long old in history.Keys.Where(key => key < tick - 120).ToArray()) history.Remove(old);
        foreach (long old in inputs.Keys.Where(key => key <= tick).ToArray()) inputs.Remove(old);
        if (revision != currentRevision) { inputs.Clear(); currentRevision = revision; }
        if (pending != null)
        {
            var sample = VanillaPrediction.Observe(player, false);
            sample["tick"] = tick; sample["scheduled_keys"] = applied;
            observations.Add(sample);
            if (observations.Count > 120) observations.RemoveAt(0);
        }
        if (pending == null || !pending.IsCompleted) return;
        if (!completionObserved)
        {
            ResponseFrames = checked((int)(tick - requestedAt)); completionObserved = true;
            responses.Enqueue(ResponseFrames);
            if (responses.Count > 4) responses.Dequeue();
            int slowest = responses.Max();
            int lead = Math.Clamp(slowest + 6, 6, 90);
            if (slowest > PlanningLead || lead < PlanningLead) PlanningLead = lead;
            if (pending.IsCompletedSuccessfully) LastResult = pending.Result;
        }
        if (pending.IsFaulted || pending.IsCanceled) { _ = pending.Exception; Reject("worker_error"); return; }
        if (revision != requestRevision) { Reject("revision_changed"); return; }
        if (tick < handoff && inputs.Count > 0) return;
        var result = pending.GetAwaiter().GetResult();
        bool navigation = context["navigation"]?.GetValue<bool>() == true;
        bool planned = result["status"]?.GetValue<string>() == "planned";
        int offset = 0;
        var controls = result["controls"]?.AsArray();
        var forecast = result["forecast"]?["frames"]?.AsArray();
        if (navigation && planned && tick > handoff && tick - handoff < controls.Count)
        {
            int elapsed = checked((int)(tick - handoff));
            if (Enumerable.Range(0, elapsed).All(i => history.TryGetValue(handoff + i + 1, out int key) && key == controls[i].GetValue<int>()))
                offset = elapsed;
        }
        string reason = null, mismatch = null;
        var anchor = offset > 0 ? forecast[offset - 1].AsObject() : result["anchor"]?.AsObject();
        bool matches = planned && StateMatches(player, anchor, navigation, out reason, out mismatch);
        bool rebase = navigation && inputs.Count == 0 && matches;
        bool catchUp = offset > 0 && matches;
        if (tick > handoff && !rebase && !catchUp)
        {
            Reject("late_result", false); return;
        }
        if (tick < handoff && !rebase) return;
        if (!VanillaFrame.ContextMatches(context, player, out string difference, result["forecast"]?["frames"]?.AsArray()))
        { LastMismatch = difference; Reject("context_changed"); return; }
        if (!planned) { Reject("no_safe_plan"); return; }
        if (!matches)
        {
            LastMismatch = mismatch;
            LastDivergence = new JsonObject { ["reason"] = reason, ["tick"] = tick, ["handoff"] = handoff,
                ["request"] = lastRequest.DeepClone(), ["anchor"] = result["anchor"].DeepClone(),
                ["actual"] = VanillaPrediction.Observe(player), ["observed"] = observations.DeepClone() };
            Reject(reason); return;
        }
        AcceptedRoute = forecast.Skip(offset).Select(frame => new Vector2(frame["x"].GetValue<float>(), frame["y"].GetValue<float>())).ToArray();
        inputs.Clear();
        long start = rebase || catchUp ? tick : handoff;
        activeStart = start + 1; activeOffset = offset;
        ActivePlan = result["selected"]?.GetValue<string>() ?? "unknown";
        ActiveGoal = new Vector2(lastRequest["x"].GetValue<float>(), lastRequest["y"].GetValue<float>());
        ActiveAnchor = new Vector2(result["anchor"]["x"].GetValue<float>(), result["anchor"]["y"].GetValue<float>());
        for (int i = offset; i < controls.Count; i++) inputs[start + i - offset + 1] = controls[i].GetValue<int>();
        if (catchUp) LateAccepted++;
        else if (rebase && tick != handoff) Rebased++;
        Accepted++; Status = "accepted"; LastRejection = null; LastMismatch = null; pending = null;
    }

    static bool StateMatches(Player player, JsonObject anchor, bool navigation, out string reason, out string mismatch)
    {
        reason = null; mismatch = null;
        var actual = VanillaPrediction.Observe(player, !navigation);
        foreach (string key in new[] { "x", "y", "vx", "vy", "wing_time" })
            if (Math.Abs(actual[key].GetValue<float>() - anchor[key].GetValue<float>()) > .01f)
            { mismatch = $"{key}: actual={actual[key]} predicted={anchor[key]}"; reason = "state_changed:" + key; return false; }
        foreach (string key in new[] { "hp", "dead", "grav_dir", "jump", "fall_start", "available_jumps", "frozen", "webbed", "buff_types", "buff_times", "extra_jumps" })
        {
            if (navigation && key is "hp" or "buff_types" or "buff_times" or "extra_jumps") continue;
            if (!JsonNode.DeepEquals(actual[key], anchor[key]))
            { mismatch = $"{key}: actual={actual[key]} predicted={anchor[key]}"; reason = "state_changed:" + key; return false; }
        }
        if (navigation)
        {
            foreach (string key in new[] { "run_speed", "run_accel", "run_slowdown", "move_speed", "gravity", "fall_speed", "jump_boost" })
                if (Math.Abs(actual[key].GetValue<float>() - anchor[key].GetValue<float>()) > .0001f)
                { mismatch = $"{key}: actual={actual[key]} predicted={anchor[key]}"; reason = "physics_changed:" + key; return false; }
            foreach (string key in new[] { "wet", "honey_wet", "no_fall_damage", "merman", "jump_state", "release_jump" })
                if (!JsonNode.DeepEquals(actual[key], anchor[key])) { reason = "physics_changed:" + key; return false; }
        }
        return true;
    }

    void Reject(string reason, bool clear = true) { if (clear) inputs.Clear(); Rejected++; Status = reason; LastRejection = reason; pending = null; }

    public bool Request(JsonObject snapshot, long tick, Vector2 goal, int revision, bool takeOwnership = false)
    {
        if (pending != null) return false;
        if (revision != currentRevision) { inputs.Clear(); currentRevision = revision; }
        requestedAt = tick; completionObserved = false;
        handoff = tick + PlanningLead; requestRevision = revision;
        var prefix = new JsonArray(Enumerable.Range(1, PlanningLead).Select(i => JsonValue.Create(InputAt(tick + i))).ToArray());
        var request = new JsonObject { ["command"] = "plan", ["snapshot"] = takeOwnership ? snapshot : snapshot.DeepClone(),
            ["prefix"] = prefix, ["x"] = goal.X, ["y"] = goal.Y,
            ["continuation"] = new JsonArray(Enumerable.Range(1, VanillaLocalPlanner.Horizon)
                .TakeWhile(i => inputs.ContainsKey(handoff + i)).Select(i => JsonValue.Create(InputAt(handoff + i))).ToArray()) };
        context = request["snapshot"].AsObject();
        lastRequest = request; observations = new JsonArray(); LastDivergence = null;
        pending = send(request); Status = "planning"; return true;
    }
}
