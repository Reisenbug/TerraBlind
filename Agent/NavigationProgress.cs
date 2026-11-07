using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace TerraBlind;

public sealed class NavigationProgress
{
    readonly Dictionary<int, long> tiles = new();
    bool initialized;
    double anchorX, anchorY, bestDistance;
    double lastMovement, lastProgress;

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
            initialized = true; anchorX = x; anchorY = y; bestDistance = distance;
            lastMovement = lastProgress = seconds;
            return null;
        }
        if (Math.Abs(x - anchorX) >= 16 || Math.Abs(y - anchorY) >= 16)
        { anchorX = x; anchorY = y; lastMovement = seconds; }
        if (distance <= bestDistance - 16)
        { bestDistance = distance; lastProgress = seconds; }
        if (edited) lastMovement = lastProgress = seconds;
        string reason = null;
        if (seconds - lastMovement >= 8) reason = "stationary";
        else if (seconds >= 30 && seconds - lastProgress >= 30) reason = "moving_without_route_progress";
        return reason == null ? null : new JsonObject { ["outcome"] = "no_progress", ["reason"] = reason,
            ["elapsed_seconds"] = seconds, ["stationary_seconds"] = seconds - lastMovement,
            ["progress_idle_seconds"] = seconds - lastProgress, ["distance_pixels"] = distance,
            ["best_distance_pixels"] = bestDistance, ["pos"] = new JsonObject { ["x"] = x, ["y"] = y } };
    }
}
