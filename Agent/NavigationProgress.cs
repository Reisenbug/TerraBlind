using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace TerraBlind;

public sealed class NavigationProgress
{
    readonly Dictionary<int, long> tiles = new();
    bool initialized;
    double anchorX, anchorY, localX, localY, bestDistance;
    double lastMovement, lastLocalProgress, lastProgress;

    public JsonObject Observe(double seconds, double x, double y, double distance, IReadOnlyDictionary<int, long> terrain, bool planning = false)
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
            lastMovement = lastLocalProgress = lastProgress = seconds;
            return null;
        }
        if (Math.Abs(x - anchorX) >= 16 || Math.Abs(y - anchorY) >= 16)
        { anchorX = x; anchorY = y; lastMovement = seconds; }
        if (Math.Abs(x - localX) >= 48 || Math.Abs(y - localY) >= 48)
        { localX = x; localY = y; lastLocalProgress = seconds; }
        if (distance <= bestDistance - 16)
        { bestDistance = distance; lastLocalProgress = lastProgress = seconds; }
        if (edited) lastMovement = lastLocalProgress = lastProgress = seconds;
        string reason = null;
        if (seconds - lastMovement >= 8) reason = "stationary";
        else if (seconds - lastLocalProgress >= 8) reason = "local_oscillation";
        else if (seconds >= 30 && seconds - lastProgress >= 30) reason = "moving_without_route_progress";
        return reason == null ? null : new JsonObject { ["outcome"] = "no_progress", ["reason"] = reason,
            ["elapsed_seconds"] = seconds, ["stationary_seconds"] = seconds - lastMovement,
            ["progress_idle_seconds"] = seconds - lastProgress, ["distance_pixels"] = distance,
            ["best_distance_pixels"] = bestDistance, ["pos"] = new JsonObject { ["x"] = x, ["y"] = y } };
    }
}
