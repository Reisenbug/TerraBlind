using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace TerraBlind;

internal sealed class NavigationCycleGuard
{
    readonly record struct Edge((int, int) From, (int, int) Target);
    readonly record struct Traversal(Edge Edge, (int, int) Landing, int Context);
    readonly System.Collections.Generic.List<Traversal> path = new();
    readonly System.Collections.Generic.Dictionary<Edge, int> blocked = new();

    internal void Reset() { path.Clear(); blocked.Clear(); }

    internal bool TryGetBlockedContext((int, int) from, (int, int) target, out int context)
        => blocked.TryGetValue(new Edge(from, target), out context);

    internal bool Landed((int, int) from, (int, int) target, (int, int) landing, int context)
    {
        if (path.Count > 0 && path[^1].Landing != from) path.Clear();
        var edge = new Edge(from, target);
        if (landing == from) return false;
        int repeated = path.FindLastIndex(step => step.Edge.From == landing);
        if (repeated >= 0)
        {
            for (int i = repeated; i < path.Count; i++) blocked[path[i].Edge] = path[i].Context;
            blocked[edge] = context;
            path.RemoveRange(repeated, path.Count - repeated);
            return true;
        }
        path.Add(new Traversal(edge, landing, context));
        if (path.Count > 16) path.RemoveAt(0);
        return false;
    }
}

public sealed class NavigationProgress
{
    readonly Dictionary<int, long> tiles = new();
    readonly bool requireApproach;
    bool initialized;
    double anchorX, anchorY, localX, localY, bestDistance;
    int? bestRouteCost;
    double lastMovement, lastLocalProgress, lastProgress;

    public NavigationProgress(bool requireApproach = false) => this.requireApproach = requireApproach;

    public JsonObject Observe(double seconds, double x, double y, double distance, IReadOnlyDictionary<int, long> terrain,
        bool planning = false, int? routeCost = null)
    {
        if (planning) { initialized = false; return null; }
        bool edited = false;
        foreach (var cell in terrain)
        {
            if (tiles.TryGetValue(cell.Key, out long before) && before != cell.Value) edited = true;
            tiles[cell.Key] = cell.Value;
        }
        if (!initialized)
        {
            initialized = true; anchorX = localX = x; anchorY = localY = y; bestDistance = distance;
            bestRouteCost = routeCost;
            lastMovement = lastLocalProgress = lastProgress = seconds;
            return null;
        }
        if (Math.Abs(x - anchorX) >= 16 || Math.Abs(y - anchorY) >= 16)
        { anchorX = x; anchorY = y; lastMovement = seconds; }
        if (Math.Abs(x - localX) >= 48 || Math.Abs(y - localY) >= 48)
        { localX = x; localY = y; lastLocalProgress = seconds; }
        if (distance <= bestDistance - 16)
        { bestDistance = distance; lastLocalProgress = lastProgress = seconds; }
        if (routeCost is int cost && (bestRouteCost == null || cost < bestRouteCost))
        {
            if (bestRouteCost != null) lastLocalProgress = lastProgress = seconds;
            bestRouteCost = cost;
        }
        if (edited)
        {
            lastMovement = lastLocalProgress = seconds;
            if (!requireApproach) lastProgress = seconds;
        }
        string reason = null;
        if (seconds - lastMovement >= 8) reason = "stationary";
        else if (seconds - lastLocalProgress >= 8) reason = "local_oscillation";
        else if (seconds >= 30 && seconds - lastProgress >= 30) reason = "moving_without_route_progress";
        return reason == null ? null : new JsonObject { ["outcome"] = "no_progress", ["reason"] = reason,
            ["elapsed_seconds"] = seconds, ["stationary_seconds"] = seconds - lastMovement,
            ["progress_idle_seconds"] = seconds - lastProgress, ["distance_pixels"] = distance,
            ["route_cost"] = routeCost, ["best_route_cost"] = bestRouteCost,
            ["best_distance_pixels"] = bestDistance, ["pos"] = new JsonObject { ["x"] = x, ["y"] = y } };
    }
}
