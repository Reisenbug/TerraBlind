using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TerraBlind;

public sealed class ActionTasks
{
    sealed class Entry
    {
        public string Id, Tool, Status = "running", Requested;
        public JsonObject Args, Observation, PreviousAttempt, StoppedObservation;
        public JsonNode Result;
        public int Attempt;
        public bool Inline;
        public CancellationTokenSource Cancel;
        public Task Running;
    }

    readonly object gate = new();
    readonly Dictionary<string, Entry> entries = new();
    readonly AsyncLocal<string> caller = new();
    readonly Func<string, JsonObject, CancellationToken, Task<string>> execute;
    readonly Func<CancellationToken, Task<JsonObject>> observe;
    readonly Func<Func<bool>, Task> stop;
    Entry active;

    public ActionTasks(Func<string, JsonObject, CancellationToken, Task<string>> execute,
        Func<CancellationToken, Task<JsonObject>> observe, Func<Func<bool>, Task> stop)
    { this.execute = execute; this.observe = observe; this.stop = stop; }

    public bool Conflicts { get { lock (gate) return active != null && caller.Value != active.Id; } }
    public bool HasActive { get { lock (gate) return active != null; } }
    public string ActiveHandle { get { lock (gate) return active?.Id; } }

    public JsonObject Start(string tool, JsonObject args, CancellationToken root, bool inline = false)
    {
        if (tool is not ("gather" or "nav_to" or "fight" or "mine_vein" or "chop_wood")) throw new ArgumentException("不支持的后台动作。");
        root.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (active != null) throw new InvalidOperationException("已有动作持有角色输入，请先暂停或取消。");
            if (entries.Count >= 32) throw new InvalidOperationException("本世界最多32个动作句柄。");
            var entry = new Entry { Id = Guid.NewGuid().ToString("N"), Tool = tool, Args = (JsonObject)args.DeepClone(), Inline = inline };
            entries.Add(entry.Id, entry);
            Launch(entry, root);
            return Snapshot(entry);
        }
    }

    void Launch(Entry entry, CancellationToken root)
    {
        if (entry.Attempt > 0) entry.PreviousAttempt = new JsonObject { ["attempt"] = entry.Attempt,
            ["status"] = entry.Status, ["result"] = entry.Result?.DeepClone(),
            ["observation"] = (entry.StoppedObservation ?? entry.Observation)?.DeepClone() };
        entry.Result = null;
        entry.StoppedObservation = null;
        entry.Status = "running"; entry.Requested = null; entry.Attempt++;
        entry.Cancel = CancellationTokenSource.CreateLinkedTokenSource(root);
        active = entry;
        entry.Running = Task.Run(() => Run(entry, entry.Attempt));
    }

    bool Owns(Entry entry, int attempt)
    { lock (gate) return active == entry && entry.Attempt == attempt; }

    async Task Run(Entry entry, int attempt)
    {
        caller.Value = entry.Id;
        JsonNode result = null;
        JsonObject observed = null;
        string status = "failed";
        try
        {
            observed = await observe(entry.Cancel.Token);
            string output = await execute(entry.Tool, (JsonObject)entry.Args.DeepClone(), entry.Cancel.Token);
            try { result = JsonNode.Parse(output); }
            catch (System.Text.Json.JsonException) { result = JsonValue.Create(output); }
            status = result is JsonObject value && (value["error"] != null || value["ok"]?.ToString() == "false"
                || value["outcome"]?.ToString() is "timeout" or "no_progress" or "interrupted_by_death"
                || value["status"]?.ToString() is "failed" or "stopped_short") ? "failed" : "completed";
        }
        catch (OperationCanceledException) { status = "cancelled"; }
        catch (Exception error) { result = new JsonObject { ["error"] = "action_failed", ["detail"] = error.Message }; }
        finally
        {
            if (Owns(entry, attempt))
            {
                try
                {
                    await stop(() => Owns(entry, attempt));
                    if (!entry.Cancel.IsCancellationRequested) observed = await observe(entry.Cancel.Token);
                }
                catch (Exception error)
                {
                    result = new JsonObject { ["error"] = "action_cleanup_failed", ["detail"] = error.Message,
                        ["action_result"] = result?.DeepClone() };
                    status = "failed";
                }
            }
            lock (gate)
            {
                if (Owns(entry, attempt))
                {
                    entry.Status = status == "failed" ? status : entry.Requested ?? status;
                    entry.Result = result; entry.Observation = observed;
                    active = null;
                }
                entry.Cancel.Dispose();
            }
            caller.Value = null;
        }
    }

    Entry Find(string id) => entries.TryGetValue(id, out var entry) ? entry : throw new ArgumentException("未知动作句柄。");

    public async Task<JsonObject> Query(string id, int waitMs, CancellationToken token)
    {
        Task running;
        lock (gate) running = Find(id).Running;
        if (waitMs > 0 && !running.IsCompleted)
            await Task.WhenAny(running, Task.Delay(Math.Clamp(waitMs, 0, 1000), token)).WaitAsync(token);
        var current = await observe(token);
        lock (gate)
        {
            var entry = Find(id);
            entry.Observation = current;
            return Snapshot(entry);
        }
    }

    public async Task<JsonObject> Control(string id, string command, JsonObject updatedArgs, CancellationToken root)
    {
        Task running;
        lock (gate)
        {
            var entry = Find(id);
            if (command == "resume")
            {
                root.ThrowIfCancellationRequested();
                if (active != null) throw new InvalidOperationException("输入尚未释放，不能恢复。");
                if (entry.Status == "completed") throw new InvalidOperationException("已完成任务不能恢复，请建立新任务。");
                if (updatedArgs != null) entry.Args = (JsonObject)updatedArgs.DeepClone();
                entry.Inline = false;
                Launch(entry, root);
                return Snapshot(entry);
            }
            if (command is not ("pause" or "cancel")) throw new ArgumentException("command需要pause/cancel/resume。");
            if (entry.Status != "running") return Snapshot(entry);
            entry.Requested = command == "pause" ? "paused" : "cancelled";
            entry.Cancel.Cancel(); running = entry.Running;
        }
        await running.WaitAsync(root);
        var observed = await observe(root);
        lock (gate)
        {
            var entry = Find(id); entry.Observation = observed; entry.StoppedObservation = (JsonObject)observed.DeepClone();
            return Snapshot(entry);
        }
    }

    public void CancelAll()
    {
        lock (gate)
            if (active != null) { active.Requested = "cancelled"; active.Cancel.Cancel(); }
    }

    public void Reset()
    {
        lock (gate)
        {
            if (active != null) { active.Requested = "cancelled"; active.Cancel.Cancel(); }
            active = null; entries.Clear();
        }
    }

    static JsonObject Snapshot(Entry entry) => new()
    {
        ["handle"] = entry.Id, ["tool"] = entry.Tool, ["args"] = entry.Args.DeepClone(),
        ["status"] = entry.Status, ["attempt"] = entry.Attempt,
        ["inline"] = entry.Inline,
        ["result"] = entry.Result?.DeepClone(), ["observation"] = entry.Observation?.DeepClone(),
        ["previous_attempt"] = entry.PreviousAttempt?.DeepClone(),
        ["requested"] = entry.Requested
    };

    public JsonArray Snapshot()
    { lock (gate) return new JsonArray(entries.Values.Select(e => (JsonNode)Snapshot(e)).ToArray()); }
}
