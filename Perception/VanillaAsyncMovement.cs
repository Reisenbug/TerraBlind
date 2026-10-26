using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Terraria;

namespace TerraBlind;

public sealed class VanillaAsyncMovement
{
    public const int LeadFrames = 30;
    readonly Func<JsonObject, CancellationToken, Task<JsonObject>> send;
    readonly Dictionary<long, int> inputs = new();
    readonly Dictionary<long, int> history = new();
    readonly Queue<int> responses = new();
    Task<JsonObject> pending;
    CancellationTokenSource pendingCancellation;
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
    public int ContactContinuations { get; private set; }
    public int ContactRetainedFrames { get; private set; }
    public float ContactOffset { get; private set; }
    public string Status { get; private set; } = "idle";
    public bool Busy => pending != null;
    public Vector2[] AcceptedRoute { get; private set; } = Array.Empty<Vector2>();
    public JsonObject LastResult { get; private set; }
    public JsonObject ActiveResult { get; private set; }
    internal JsonObject LastRequest => lastRequest;
    public string LastMismatch { get; private set; }
    public string LastRejection { get; private set; }
    public JsonObject LastDivergence { get; private set; }
    public string ActivePlan { get; private set; } = "none";
    public Vector2 ActiveGoal { get; private set; }
    public Vector2 ActiveAnchor { get; private set; }
    long activeStart;
    int activeOffset;
    JsonArray activeForecast;
    JsonObject activeContext;
    int[] activeTiles;
    int activeUsefulEnd;
    int[] recovery;

    public VanillaAsyncMovement(Func<JsonObject, Task<JsonObject>> transport) : this((request, _) => transport(request)) { }
    public VanillaAsyncMovement(Func<JsonObject, CancellationToken, Task<JsonObject>> transport, int planningLead = LeadFrames)
    {
        send = transport;
        PlanningLead = Math.Clamp(planningLead, 6, 90);
    }

    public void CancelPending()
    {
        var task = pending; var cancellation = pendingCancellation;
        pending = null; pendingCancellation = null;
        cancellation?.Cancel();
        if (task == null) { cancellation?.Dispose(); return; }
        _ = task.ContinueWith(completed => { _ = completed.Exception; cancellation?.Dispose(); }, TaskScheduler.Default);
    }

    public int InputAt(long tick) => inputs.TryGetValue(tick, out int key) ? key : 0;
    public bool HasInputAt(long tick) => inputs.ContainsKey(tick);
    internal bool ProtectFall(Player player, long tick)
    {
        if (HasInputAt(tick) || player.gravDir != 1 || player.velocity.Y <= 0
            || !player.releaseJump || !player.AnyExtraJumpUsable()) return false;
        var step = new Vector2(player.velocity.X, Math.Max(player.velocity.Y, player.maxFallSpeed));
        bool dangerousFall = !player.noFallDmg && player.position.Y / 16 - player.fallStart > 25 + player.extraFall;
        bool danger = false;
        var position = player.position;
        for (int i = 0; i < 8 && !danger; i++)
        {
            var allowed = Collision.TileCollision(position, step, player.width, player.height);
            position += allowed;
            danger = dangerousFall && allowed.Y < step.Y || Collision.LavaCollision(position, player.width, player.height);
        }
        if (!danger) return false;
        Reject("emergency_jump");
        for (int i = 0; i < 12; i++) inputs[tick + i] = 4;
        return true;
    }
    internal void DiscardRoute(Player player, long tick)
    {
        RecordAirborneDivergence(player, tick, "terrain_approach_changed");
        RecoverRoute(tick, "terrain_approach_changed");
    }

    void RecordAirborneDivergence(Player player, long tick, string reason)
    {
        if (player.velocity.Y == 0 || activeContext == null
            || !Terraria.Program.LaunchParameters.ContainsKey("-vnavbaseline")) return;
        var area = activeContext["area"];
        LastDivergence = new JsonObject { ["reason"] = reason, ["mismatch"] = LastMismatch, ["tick"] = tick,
            ["actual"] = VanillaPrediction.Observe(player), ["active_plan"] = ActiveResult?.DeepClone(),
            ["active_context"] = activeContext.DeepClone(),
            ["snapshot"] = VanillaFrame.Capture(player, new Rectangle(area[0].GetValue<int>(), area[1].GetValue<int>(),
                area[2].GetValue<int>(), area[3].GetValue<int>()), true),
            ["x"] = ActiveGoal.X, ["y"] = ActiveGoal.Y,
            ["remaining_controls"] = new JsonArray(Enumerable.Range(1, VanillaLocalPlanner.Horizon)
                .TakeWhile(i => inputs.ContainsKey(tick + i)).Select(i => JsonValue.Create(InputAt(tick + i))).ToArray()) };
    }

    void RecoverRoute(long tick, string reason)
    {
        var remaining = Enumerable.Range(1, VanillaLocalPlanner.Horizon)
            .TakeWhile(i => inputs.ContainsKey(tick + i)).Select(i => InputAt(tick + i)).ToArray();
        Reject(reason);
        recovery = remaining;
    }
    public bool TryArrival(long tick, out int[] controls, out JsonObject anchor)
    {
        controls = null; anchor = null;
        if (ActiveResult == null || activeForecast == null || activeUsefulEnd < 0) return false;
        int end = activeUsefulEnd + 3;
        int start = checked((int)(tick - activeStart)) + activeOffset + 1;
        int count = end - start + 1;
        if (end >= activeForecast.Count || count < 12 || count > 90) return false;
        var arrival = activeForecast[end].AsObject();
        if (arrival["vx"].GetValue<float>() != 0 || arrival["vy"].GetValue<float>() != 0) return false;
        if (!Enumerable.Range(1, count).All(i => inputs.ContainsKey(tick + i))) return false;
        controls = Enumerable.Range(1, count).Select(i => InputAt(tick + i)).ToArray();
        anchor = arrival;
        return true;
    }

    public bool CurrentMatches(long tick, Player player)
    {
        if (ActiveResult == null || activeContext == null) return false;
        int index = checked((int)(tick - activeStart)) + activeOffset;
        var expected = index < 0 ? ActiveResult["anchor"]?.AsObject()
            : index < activeForecast.Count ? activeForecast[index].AsObject() : null;
        if (expected == null) { LastMismatch = "missing_expected_frame"; return false; }
        if (!StateMatches(player, expected, true, out string reason, out string mismatch))
        { LastMismatch = mismatch ?? reason; return false; }
        bool matches = VanillaFrame.ContextMatches(activeContext, player, out string difference, activeForecast,
            activeTiles, Math.Max(0, index + 1), 12);
        LastMismatch = matches ? null : difference;
        return matches;
    }
    public bool NeedsPlan(long tick, Player player, bool terminal)
    {
        if (ActiveResult == null || activeContext == null) return true;
        int index = checked((int)(tick - activeStart)) + activeOffset;
        var expected = index < 0 ? ActiveResult["anchor"]?.AsObject()
            : index < activeForecast.Count ? activeForecast[index].AsObject() : null;
        if (expected == null) return true;
        if (!StateMatches(player, expected, true, out string reason, out string mismatch))
        {
            if (reason == "state_changed:x" && TryContactContinuation(tick, index, player, expected)) return true;
            LastMismatch = mismatch ?? reason; RecordAirborneDivergence(player, tick, reason); RecoverRoute(tick, reason);
            return true;
        }
        if (!VanillaFrame.ContextMatches(activeContext, player, out string difference, activeForecast,
            activeTiles, Math.Max(0, index + 1), 12))
        {
            LastMismatch = difference; RecordAirborneDivergence(player, tick, "context_changed");
            Reject("context_changed"); return true;
        }
        return terminal || ActiveResult["route_progress"]?.GetValue<double>() <= 0
            || inputs.Count <= PlanningLead + 12 || activeUsefulEnd - index <= PlanningLead + 12;
    }

    static int LastMovingFrame(JsonArray frames)
    {
        for (int i = frames.Count - 1; i >= 0; i--)
            if (Math.Abs(frames[i]["vx"].GetValue<float>()) + Math.Abs(frames[i]["vy"].GetValue<float>()) > .01f) return i;
        return -1;
    }

    bool TryContactContinuation(long tick, int index, Player player, JsonObject expected)
    {
        if (player.gravDir != 1 || player.velocity.Y != 0 || index < 0
            || expected["grounded"]?.GetValue<bool>() != true
            || !StateMatches(player, expected, true, out _, out _, ignoreHorizontalPosition: true)) return false;
        var original = new Vector2(expected["x"].GetValue<float>(), expected["y"].GetValue<float>());
        if (!VanillaContactRegion.ConnectedSupport(original, player.position, player.width, player.height)) return false;
        float shift = player.position.X - original.X;
        var previous = player.position;
        int end = index;
        for (int i = index + 1; i < activeForecast.Count; i++)
        {
            long at = activeStart + i - activeOffset;
            if (!inputs.TryGetValue(at, out int keys) || (keys & ~3) != 0) break;
            var frame = activeForecast[i];
            if (frame["grounded"]?.GetValue<bool>() != true || frame["vy"].GetValue<float>() != 0) break;
            var before = activeForecast[i - 1];
            float speed = Math.Max(Math.Abs(before["vx"].GetValue<float>()), player.accRunSpeed) + player.runAcceleration;
            int direction = keys == 1 ? -1 : keys == 2 ? 1 : Math.Sign(before["vx"].GetValue<float>());
            var originalBefore = new Vector2(before["x"].GetValue<float>(), before["y"].GetValue<float>());
            var clearance = new Vector2(direction * speed, 0);
            if (!VanillaContactRegion.ConnectedSupport(originalBefore, originalBefore + clearance, player.width, player.height)
                || !VanillaContactRegion.ConnectedSupport(previous, previous + clearance, player.width, player.height)) break;
            var position = new Vector2(frame["x"].GetValue<float>() + shift, frame["y"].GetValue<float>());
            if (!VanillaContactRegion.ConnectedSupport(previous, position, player.width, player.height)) break;
            previous = position; end = i;
        }
        if (end == index) return false;
        CancelPending();
        var translated = new JsonArray();
        foreach (var sample in activeForecast.Take(end + 1))
        {
            var frame = sample.DeepClone(); frame["x"] = frame["x"].GetValue<float>() + shift; translated.Add(frame);
        }
        foreach (long key in inputs.Keys.Where(key => key > activeStart + end - activeOffset).ToArray()) inputs.Remove(key);
        activeForecast = translated;
        ActiveResult = ActiveResult.DeepClone().AsObject();
        ActiveResult["forecast"]["frames"] = translated.DeepClone();
        activeUsefulEnd = LastMovingFrame(translated);
        AcceptedRoute = translated.Skip(index + 1).Select(frame => new Vector2(frame["x"].GetValue<float>(), frame["y"].GetValue<float>())).ToArray();
        ContactContinuations++; ContactRetainedFrames = end - index; ContactOffset = shift;
        LastMismatch = LastRejection = null;
        return true;
    }
    public void TerrainChanged(int x, int y, Player player, long tick)
    {
        CancelPending();
        if (activeForecast == null) return;
        var changed = new Rectangle(x * 16 - 16, y * 16 - 16, 48, 48);
        for (int i = activeOffset; i < activeForecast.Count; i++)
        {
            long at = activeStart + i - activeOffset;
            if (at <= tick) continue;
            var frame = activeForecast[i];
            var body = new Rectangle((int)frame["x"].GetValue<float>(), (int)frame["y"].GetValue<float>(), player.width, player.height);
            if (!body.Intersects(changed)) continue;
            foreach (long key in inputs.Keys.Where(key => key >= at).ToArray()) inputs.Remove(key);
            ActiveResult = null;
            break;
        }
    }
    public bool InstallTerrain(JsonObject result, Player player, long tick, Vector2 goal)
    {
        var frames = result["forecast"]["frames"].AsArray();
        int offset = -1;
        for (int i = result["wait_frames"].GetValue<int>(); i >= 0; i--)
        {
            var anchor = i == 0 ? result["anchor"].AsObject() : frames[i - 1].AsObject();
            if (StateMatches(player, anchor, true, out var reason, out var mismatch)) { offset = i; break; }
            LastMismatch = mismatch ?? reason;
        }
        if (offset < 0) return false;
        if (!VanillaFrame.ContextMatches(result["context"].AsObject(), player, out var difference, frames))
        { LastMismatch = difference; return false; }
        LastMismatch = null;
        CancelPending();
        inputs.Clear(); history.Clear(); currentRevision = 0;
        context = result["context"].AsObject(); LastResult = result; ActiveResult = result;
        lastRequest = new JsonObject { ["snapshot"] = context.DeepClone(), ["x"] = goal.X, ["y"] = goal.Y };
        var controls = result["controls"].AsArray();
        for (int i = offset; i < controls.Count; i++) inputs[tick + i - offset + 1] = controls[i].GetValue<int>();
        activeStart = tick + 1; activeOffset = offset; ActivePlan = result["selected"].GetValue<string>();
        activeForecast = frames;
        activeContext = context; activeTiles = VanillaFrame.ReadTiles(context);
        activeUsefulEnd = LastMovingFrame(frames);
        ActiveGoal = goal; ActiveAnchor = player.position;
        AcceptedRoute = frames.Skip(offset).Select(frame => new Vector2(frame["x"].GetValue<float>(), frame["y"].GetValue<float>())).ToArray();
        Accepted++; Status = "accepted";
        return true;
    }
    public int PlanFrameAt(long tick) => HasInputAt(tick) ? checked((int)(tick - activeStart)) + activeOffset : -1;

    public void Poll(long tick, Player player, int revision)
    {
        int applied = InputAt(tick);
        history[tick] = (player.controlLeft ? 1 : 0) | (player.controlRight ? 2 : 0) | (player.controlJump ? 4 : 0)
            | (player.controlUp ? 8 : 0) | (player.controlDown ? 16 : 0);
        foreach (long old in history.Keys.Where(key => key < tick - 120).ToArray()) history.Remove(old);
        foreach (long old in inputs.Keys.Where(key => key <= tick).ToArray()) inputs.Remove(old);
        if (revision != currentRevision)
        {
            if (pending != null) Reject("revision_changed");
            inputs.Clear(); ActiveResult = null; currentRevision = revision;
        }
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
        if (!planned) { Reject("no_safe_plan", false); return; }
        if (!VanillaFrame.ContextMatches(context, player, out string difference, result["forecast"]?["frames"]?.AsArray()))
        { LastMismatch = difference; RejectResponse("context_changed", player, tick, navigation); return; }
        if (!matches)
        {
            LastMismatch = mismatch;
            LastDivergence = new JsonObject { ["reason"] = reason, ["tick"] = tick, ["handoff"] = handoff,
                ["request"] = lastRequest.DeepClone(), ["anchor"] = result["anchor"].DeepClone(),
                ["actual"] = VanillaPrediction.Observe(player), ["observed"] = observations.DeepClone() };
            RejectResponse(reason, player, tick, navigation); return;
        }
        AcceptedRoute = forecast.Skip(offset).Select(frame => new Vector2(frame["x"].GetValue<float>(), frame["y"].GetValue<float>())).ToArray();
        inputs.Clear();
        long start = rebase || catchUp ? tick : handoff;
        activeStart = start + 1; activeOffset = offset;
        activeForecast = forecast;
        activeContext = context; activeTiles = VanillaFrame.ReadTiles(context);
        activeUsefulEnd = LastMovingFrame(forecast);
        ActiveResult = result;
        ActivePlan = result["selected"]?.GetValue<string>() ?? "unknown";
        ActiveGoal = new Vector2(lastRequest["x"].GetValue<float>(), lastRequest["y"].GetValue<float>());
        ActiveAnchor = new Vector2(result["anchor"]["x"].GetValue<float>(), result["anchor"]["y"].GetValue<float>());
        for (int i = offset; i < controls.Count; i++) inputs[start + i - offset + 1] = controls[i].GetValue<int>();
        if (catchUp) LateAccepted++;
        else if (rebase && tick != handoff) Rebased++;
        Accepted++; Status = "accepted"; LastRejection = null; LastMismatch = null; pending = null;
        pendingCancellation?.Dispose(); pendingCancellation = null;
    }

    internal static bool StateMatches(Player player, JsonObject anchor, bool navigation, out string reason, out string mismatch,
        bool ignoreHorizontalPosition = false)
    {
        reason = null; mismatch = null;
        var actual = VanillaPrediction.Observe(player, !navigation);
        foreach (string key in new[] { "x", "y", "vx", "vy", "wing_time" })
        {
            if (ignoreHorizontalPosition && key == "x") continue;
            if (Math.Abs(actual[key].GetValue<float>() - anchor[key].GetValue<float>()) > .01f)
            { mismatch = $"{key}: actual={actual[key]} predicted={anchor[key]}"; reason = "state_changed:" + key; return false; }
        }
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

    void Reject(string reason, bool clear = true) { recovery = null; if (clear) { inputs.Clear(); ActiveResult = null; } Rejected++; Status = reason; LastRejection = reason; CancelPending(); }

    void RejectResponse(string reason, Player player, long tick, bool navigation)
    {
        string mismatch = LastMismatch;
        bool valid = navigation && CurrentMatches(tick, player);
        LastMismatch = mismatch;
        Reject(reason, !valid);
    }

    public bool Request(JsonObject snapshot, long tick, Vector2 goal, int revision, bool takeOwnership = false)
    {
        if (pending != null) return false;
        if (revision != currentRevision) { inputs.Clear(); ActiveResult = null; recovery = null; currentRevision = revision; }
        requestedAt = tick; completionObserved = false;
        handoff = tick + PlanningLead; requestRevision = revision;
        var prefix = new JsonArray(Enumerable.Range(1, PlanningLead).Select(i => JsonValue.Create(InputAt(tick + i))).ToArray());
        var request = new JsonObject { ["command"] = "plan", ["snapshot"] = takeOwnership ? snapshot : snapshot.DeepClone(),
            ["prefix"] = prefix, ["x"] = goal.X, ["y"] = goal.Y,
            ["continuation"] = new JsonArray(Enumerable.Range(1, VanillaLocalPlanner.Horizon)
                .TakeWhile(i => inputs.ContainsKey(handoff + i)).Select(i => JsonValue.Create(InputAt(handoff + i))).ToArray()) };
        if (recovery?.Length > 0)
        {
            request["recover"] = true;
            request["continuation"] = new JsonArray(recovery.Select(key => JsonValue.Create(key)).ToArray());
            recovery = null;
        }
        context = request["snapshot"].AsObject();
        lastRequest = request; observations = new JsonArray(); LastDivergence = null;
        pendingCancellation = new CancellationTokenSource();
        pending = send(request, pendingCancellation.Token); Status = "planning"; return true;
    }
}
