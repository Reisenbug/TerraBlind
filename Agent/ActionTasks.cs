using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TerraBlind;

public sealed class ActionTasks
{
    internal static readonly AsyncLocal<string> NodeContext = new();
    sealed class Entry
    {
        public string Id, Tool, Status = "running", Requested, Node, After;
        public JsonObject Args, Observation, PreviousAttempt, StoppedObservation;
        public JsonNode Result;
        public int Attempt;
        public int TaskSerial;
        public bool Inline;
        public CancellationTokenSource Cancel;
        public Task Running;
        public CancellationToken Root;
        public long ExecutionEnded, ExecutionStarted;
        public double? HandoffMs;
    }

    readonly object gate = new();
    readonly HashSet<string> arrivals = new();
    int navigationArrivals, taskSerial;
    public int NavigationArrivals { get { lock (gate) return navigationArrivals; } }
    public void BeginTask() { lock (gate) { CancelQueued(); taskSerial++; arrivals.Clear(); navigationArrivals = 0; } }
    readonly Dictionary<string, Entry> entries = new();
    readonly AsyncLocal<string> caller = new();
    readonly Func<string, JsonObject, CancellationToken, Task<string>> execute;
    readonly Func<CancellationToken, Task<JsonObject>> observe;
    readonly Func<Func<bool>, Task> stop;
    readonly Func<string, Func<bool>, Task> finished;
    readonly Func<string, Func<bool>, Task> stopTool;
    readonly Func<string, string, CancellationToken, Task> prepareQueued;
    Entry active, attack;
    bool handingOff;
    int handoffGeneration;

    public ActionTasks(Func<string, JsonObject, CancellationToken, Task<string>> execute,
        Func<CancellationToken, Task<JsonObject>> observe, Func<Func<bool>, Task> stop,
        Func<string, Func<bool>, Task> finished = null, Func<string, Func<bool>, Task> stopTool = null,
        Func<string, string, CancellationToken, Task> prepareQueued = null)
    { this.execute = execute; this.observe = observe; this.stop = stop; this.finished = finished; this.stopTool = stopTool; this.prepareQueued = prepareQueued; }

    public bool Conflicts { get { lock (gate) return handingOff || active != null && caller.Value != active.Id || attack != null && caller.Value != attack.Id; } }
    public bool ConflictsWith(string tool)
    { lock (gate) { var owner = tool == "fight" ? attack : active; return handingOff || owner != null && caller.Value != owner.Id; } }
    public bool HasActive { get { lock (gate) return active != null || attack != null; } }
    public bool HasPrimaryControl { get { lock (gate) return handingOff || active != null; } }
    public bool HasAttack { get { lock (gate) return attack != null; } }
    public string ActiveHandle { get { lock (gate) return active?.Id ?? attack?.Id; } }
    internal bool RunningScript { get { lock (gate) return active?.Tool == "run_script" && caller.Value == active.Id; } }
    internal JsonObject LatestCompletedPrimary()
    {
        lock (gate)
        {
            var entry = entries.Values.Where(e => e.TaskSerial == taskSerial && e.Tool != "fight" && e.Status == "completed")
                .OrderByDescending(e => e.ExecutionEnded).FirstOrDefault();
            return entry == null ? null : Snapshot(entry);
        }
    }

    internal JsonObject LatestPrimary()
    {
        lock (gate)
        {
            var entry = entries.Values.Where(e => e.TaskSerial == taskSerial && e.Tool != "fight")
                .OrderByDescending(e => e.ExecutionStarted).FirstOrDefault();
            return entry == null ? null : new JsonObject { ["handle"] = entry.Id, ["tool"] = entry.Tool,
                ["status"] = entry.Status, ["task_node"] = entry.Node };
        }
    }

    internal static bool Supports(string tool) => tool is "gather" or "nav_to" or "fight"
        or "mine_vein" or "chop_wood" or "collect_treasure" or "run_script";

    public JsonObject Start(string tool, JsonObject args, CancellationToken root, bool inline = false)
    {
        if (!Supports(tool)) throw new ArgumentException("不支持的后台动作。");
        root.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (handingOff || (tool == "fight" ? attack : active) != null) throw new InvalidOperationException("已有动作或控制交接持有相同输入通道，请先等待释放。");
            if (!string.IsNullOrEmpty(NodeContext.Value))
            {
                var paused = entries.Values.FirstOrDefault(e => e.Node == NodeContext.Value && e.Tool == tool
                    && e.Status == "paused" && JsonNode.DeepEquals(e.Args, args));
                if (paused != null)
                    throw new InvalidOperationException("已有相同节点的暂停动作，不能重复创建。用action_control resume恢复原句柄 "
                        + paused.Id + "；若确需放弃，请先cancel并根据新事实调整方案。");
            }
            if (entries.Count >= 32) throw new InvalidOperationException("本世界最多32个动作句柄。");
            var entry = new Entry { Id = Guid.NewGuid().ToString("N"), Tool = tool, Args = (JsonObject)args.DeepClone(), Inline = inline,
                Node = NodeContext.Value };
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
        entry.Observation = null;
        entry.StoppedObservation = null;
        entry.Status = "running"; entry.Requested = null; entry.Attempt++;
        entry.TaskSerial = taskSerial;
        entry.Root = root;
        entry.Cancel = CancellationTokenSource.CreateLinkedTokenSource(root);
        if (entry.Tool == "fight") attack = entry; else active = entry;
        entry.Running = Task.Run(() => Run(entry, entry.Attempt));
    }

    public JsonObject Enqueue(string after, string tool, JsonObject args, string node, CancellationToken root)
    {
        if (!Supports(tool) || tool == "fight") throw new ArgumentException("排队只支持主任务动作。");
        root.ThrowIfCancellationRequested();
        lock (gate)
        {
            var previous = Find(after);
            if (previous.Tool == "fight" || previous.TaskSerial != taskSerial
                || previous.Status is not ("running" or "paused" or "queued" or "completed"))
                throw new InvalidOperationException("前置动作必须属于本任务且可继续执行。");
            if (entries.Values.Any(e => e.After == after && e.Status is "queued" or "running" or "paused" or "cancelling"))
                throw new InvalidOperationException("此动作已有待执行的下一段，先取消原排队动作再替换。");
            if (entries.Count >= 32) throw new InvalidOperationException("本世界最多32个动作句柄。");
            if (previous.Status == "completed" && (active != null || handingOff))
                throw new InvalidOperationException("前置已完成但输入已被其他动作占用。");
            var entry = new Entry { Id = Guid.NewGuid().ToString("N"), Tool = tool,
                Args = (JsonObject)args.DeepClone(), Node = node, After = after, Root = root,
                Status = "queued", TaskSerial = taskSerial, Running = Task.CompletedTask };
            entries.Add(entry.Id, entry);
            if (previous.Status == "completed") Launch(entry, root);
            return Snapshot(entry);
        }
    }

    void ContinueQueued(Entry previous)
    {
        foreach (var next in entries.Values.Where(e => e.After == previous.Id && e.Status == "queued").ToArray())
        {
            if (previous.Status == "paused") continue;
            if (previous.Status == "completed" && next.TaskSerial == taskSerial && !next.Root.IsCancellationRequested
                && active == null && !handingOff)
                Launch(next, next.Root);
            else
            {
                next.Status = "blocked";
                next.Result = new JsonObject { ["error"] = "predecessor_not_completed", ["handle"] = previous.Id,
                    ["status"] = previous.Status, ["result"] = previous.Result?.DeepClone() };
                ContinueQueued(next);
            }
        }
    }

    void CancelQueued()
    {
        foreach (var entry in entries.Values.Where(e => e.Status == "queued")) entry.Status = "cancelled";
    }

    bool Owns(Entry entry, int attempt)
    { lock (gate) return (entry.Tool == "fight" ? attack : active) == entry && entry.Attempt == attempt; }

    async Task Run(Entry entry, int attempt)
    {
        caller.Value = entry.Id;
        NodeContext.Value = entry.Node;
        JsonNode result = null;
        JsonObject observed = null;
        JsonObject starting = null;
        string status = "failed";
        try
        {
            if (entry.After != null && prepareQueued != null)
            {
                string previousNode;
                lock (gate) previousNode = Find(entry.After).Node;
                await prepareQueued(previousNode, entry.Node, entry.Cancel.Token);
            }
            observed = await observe(entry.Cancel.Token);
            starting = (JsonObject)observed.DeepClone();
            lock (gate)
                if (Owns(entry, attempt)) entry.Observation = (JsonObject)observed.DeepClone();
            entry.ExecutionStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            if (entry.After != null)
            {
                lock (gate) entry.HandoffMs = System.Diagnostics.Stopwatch.GetElapsedTime(Find(entry.After).ExecutionEnded, entry.ExecutionStarted).TotalMilliseconds;
            }
            string output = await execute(entry.Tool, (JsonObject)entry.Args.DeepClone(), entry.Cancel.Token);
            entry.ExecutionEnded = System.Diagnostics.Stopwatch.GetTimestamp();
            try { result = JsonNode.Parse(output); }
            catch (System.Text.Json.JsonException) { result = JsonValue.Create(output); }
            status = result is JsonObject value && (value["error"] != null || value["ok"]?.ToString() == "false"
                || entry.Tool == "run_script" && value["value"] is JsonObject returned
                    && (returned["status"]?.ToString() is "yield" or "failed" or "stopped_short"
                        || returned["wake"] != null || returned["error"] != null || returned["ok"]?.ToString() == "false"
                        || returned["done"]?.ToString() == "false"
                        || returned["outcome"]?.ToString() is "timeout" or "no_progress" or "interrupted_by_death" or "failed" or "stopped" or "partial")
                || entry.Tool == "nav_to" && value["done"]?.ToString() == "false"
                || value["outcome"]?.ToString() is "timeout" or "no_progress" or "interrupted_by_death" or "failed" or "stopped" or "partial"
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
                    if (stopTool != null) await stopTool(entry.Tool, () => Owns(entry, attempt));
                    else await stop(() => Owns(entry, attempt));
                    if (!entry.Cancel.IsCancellationRequested) observed = await observe(entry.Cancel.Token);
                    string ending;
                    lock (gate) ending = status == "failed" ? status : entry.Requested ?? status;
                    if (ending != "paused" && finished != null)
                        await finished(entry.Id, () => Owns(entry, attempt));
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
                    if (entry.TaskSerial == taskSerial && entry.Status == "completed" && entry.Tool == "nav_to"
                        && result?["done"]?.ToString() == "true" && Moved(starting, observed)
                        && arrivals.Add(entry.Args["x"] + "," + entry.Args["y"])) navigationArrivals++;
                    if (entry.Tool == "fight") attack = null; else active = null;
                    ContinueQueued(entry);
                }
                entry.Cancel.Dispose();
            }
            caller.Value = null;
        }
    }

    static bool Moved(JsonObject before, JsonObject after)
    {
        if (before?["pos"] == null || after?["pos"] == null) return false;
        double Coordinate(JsonObject state, string axis) => double.Parse(state["pos"][axis].ToString(), System.Globalization.CultureInfo.InvariantCulture);
        return Math.Abs(Coordinate(before, "x") - Coordinate(after, "x")) >= 16
            || Math.Abs(Coordinate(before, "y") - Coordinate(after, "y")) >= 16;
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
            if (entry.Status == "queued")
            {
                if (command != "cancel") throw new InvalidOperationException("待执行动作可取消；暂停或恢复其前置动作即可保留排队。");
                entry.Status = "cancelled";
                ContinueQueued(entry);
                return Snapshot(entry);
            }
            if (command == "resume")
            {
                root.ThrowIfCancellationRequested();
                if (entry.After != null && Find(entry.After).Status != "completed")
                    throw new InvalidOperationException("前置动作尚未完成，不能恢复后续动作。");
                if (handingOff || (entry.Tool == "fight" ? attack : active) != null) throw new InvalidOperationException("输入尚未释放，不能恢复。");
                if (entry.Status == "cancelling") throw new InvalidOperationException("取消清理尚未完成，不能恢复。");
                if (entry.Status == "completed") throw new InvalidOperationException("已完成任务不能恢复，请建立新任务。");
                if (updatedArgs != null) entry.Args = (JsonObject)updatedArgs.DeepClone();
                entry.Inline = false;
                Launch(entry, root);
                return Snapshot(entry);
            }
            if (command is not ("pause" or "cancel")) throw new ArgumentException("command需要pause/cancel/resume。");
            if (entry.Status == "paused" && command == "cancel")
            {
                entry.Status = "cancelling"; entry.Requested = "cancelled";
                entry.Running = CancelPaused(entry);
            }
            else if (entry.Status == "running")
            {
                entry.Requested = command == "pause" ? "paused" : "cancelled";
                entry.Cancel.Cancel();
            }
            else if (entry.Status != "cancelling") return Snapshot(entry);
            running = entry.Running;
        }
        await running.WaitAsync(root);
        var observed = await observe(root);
        lock (gate)
        {
            var entry = Find(id); entry.Observation = observed; entry.StoppedObservation = (JsonObject)observed.DeepClone();
            return Snapshot(entry);
        }
    }

    async Task CancelPaused(Entry entry)
    {
        bool Valid()
        { lock (gate) return entries.TryGetValue(entry.Id, out var current) && current == entry && entry.Status == "cancelling"; }
        try
        {
            if (finished != null) await finished(entry.Id, Valid);
            lock (gate) if (Valid()) { entry.Status = "cancelled"; ContinueQueued(entry); }
        }
        catch (Exception error)
        {
            lock (gate) if (Valid())
            {
                entry.Status = "failed";
                entry.Result = new JsonObject { ["error"] = "action_cleanup_failed", ["detail"] = error.Message,
                    ["action_result"] = entry.Result?.DeepClone() };
                ContinueQueued(entry);
            }
        }
    }

    public void CancelAll()
    {
        lock (gate)
        {
            CancelQueued();
            if (active != null) { active.Requested = "cancelled"; active.Cancel.Cancel(); }
            if (attack != null) { attack.Requested = "cancelled"; attack.Cancel.Cancel(); }
            foreach (var entry in entries.Values.Where(entry => entry.Status == "paused").ToArray())
            {
                entry.Status = "cancelling"; entry.Requested = "cancelled";
                entry.Running = CancelPaused(entry);
            }
        }
    }

    public async Task<JsonObject> Handoff(Func<Func<bool>, Task> acquire, CancellationToken token)
    {
        Entry previous, previousAttack;
        int generation;
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            if (handingOff) throw new InvalidOperationException("已有控制交接正在进行。");
            handingOff = true; generation = ++handoffGeneration;
            previous = active;
            previousAttack = attack;
            if (previous != null) { previous.Requested = "paused"; previous.Cancel.Cancel(); }
            if (previousAttack != null) { previousAttack.Requested = "paused"; previousAttack.Cancel.Cancel(); }
        }
        bool Valid() { lock (gate) return handingOff && generation == handoffGeneration && !token.IsCancellationRequested; }
        try
        {
            if (previous != null)
            {
                await previous.Running.WaitAsync(token);
                lock (gate)
                    if (previous.Result is JsonObject result && result["error"]?.ToString() == "action_cleanup_failed")
                        return new JsonObject { ["error"] = "control_handoff_cleanup_failed", ["action"] = Snapshot(previous) };
            }
            if (previousAttack != null)
            {
                await previousAttack.Running.WaitAsync(token);
                lock (gate)
                    if (previousAttack.Result is JsonObject result && result["error"]?.ToString() == "action_cleanup_failed")
                        return new JsonObject { ["error"] = "control_handoff_cleanup_failed", ["action"] = Snapshot(previousAttack) };
            }
            if (!Valid()) throw new OperationCanceledException("控制交接已失效。");
            await acquire(Valid);
            if (!Valid()) throw new OperationCanceledException("控制交接已失效。");
            return new JsonObject { ["status"] = "ready", ["paused_action"] = previous?.Id ?? previousAttack?.Id,
                ["paused_attack"] = previousAttack?.Id };
        }
        finally
        {
            lock (gate) if (generation == handoffGeneration) handingOff = false;
        }
    }

    public void Reset()
    {
        lock (gate)
        {
            handoffGeneration++; handingOff = false;
            taskSerial++; arrivals.Clear(); navigationArrivals = 0;
            if (active != null) { active.Requested = "cancelled"; active.Cancel.Cancel(); }
            if (attack != null) { attack.Requested = "cancelled"; attack.Cancel.Cancel(); }
            active = attack = null; entries.Clear();
        }
    }

    static JsonObject Snapshot(Entry entry) => new()
    {
        ["handle"] = entry.Id, ["tool"] = entry.Tool, ["args"] = entry.Args.DeepClone(),
        ["status"] = entry.Status, ["attempt"] = entry.Attempt,
        ["inline"] = entry.Inline,
        ["task_node"] = entry.Node,
        ["after_handle"] = entry.After, ["handoff_ms"] = entry.HandoffMs,
        ["result"] = entry.Result?.DeepClone(), ["observation"] = entry.Observation?.DeepClone(),
        ["previous_attempt"] = entry.PreviousAttempt?.DeepClone(),
        ["requested"] = entry.Requested
    };

    public JsonArray Snapshot()
    { lock (gate) return new JsonArray(entries.Values.Select(e => (JsonNode)Snapshot(e)).ToArray()); }

    public JsonArray Summary()
    {
        lock (gate)
        {
            var result = new JsonArray();
            foreach (var entry in entries.Values)
            {
                var facts = new JsonObject();
                if (entry.Result is JsonObject full)
                    foreach (string key in new[] { "done", "outcome", "error", "reason", "status", "got", "lost", "state" })
                        if (full[key] != null) facts[key] = full[key].DeepClone();
                result.Add(new JsonObject { ["handle"] = entry.Id, ["tool"] = entry.Tool, ["args"] = entry.Args.DeepClone(),
                    ["status"] = entry.Status, ["attempt"] = entry.Attempt, ["inline"] = entry.Inline,
                    ["task_node"] = entry.Node,
                    ["after_handle"] = entry.After, ["handoff_ms"] = entry.HandoffMs,
                    ["result"] = facts, ["detail_tool"] = "action_query" });
            }
            return result;
        }
    }
}
