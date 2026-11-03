using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TerraBlind;

public sealed record GatherTarget(int ItemType, int Count, string[] Tiles, int[] Npcs, int? TileFrameX = null, int? TileFrameY = null);

public sealed record GatherArea(int X, int Y, int Width, int Height)
{
    public static GatherArea Parse(JsonObject value)
    {
        if (value == null) return null;
        var area = new GatherArea(value["x"].GetValue<int>(), value["y"].GetValue<int>(),
            value["width"].GetValue<int>(), value["height"].GetValue<int>());
        if (area.X < 0 || area.Y < 0 || area.Width is < 1 or > 120 || area.Height is < 1 or > 120)
            throw new ArgumentException("source_area需要非负坐标及1..120格的width/height。");
        return area;
    }
    public bool Contains(JsonObject candidate) => candidate["x"] != null && candidate["y"] != null
        && candidate["x"].GetValue<int>() >= X && candidate["x"].GetValue<int>() < (long)X + Width
        && candidate["y"].GetValue<int>() >= Y && candidate["y"].GetValue<int>() < (long)Y + Height;
    public JsonObject Snapshot() => new() { ["x"] = X, ["y"] = Y, ["width"] = Width, ["height"] = Height };
}

public interface IGatherWorld
{
    Task<JsonObject> Observe(CancellationToken token);
    Task<JsonObject> Act(JsonObject candidate, CancellationToken token);
    Task Stop(CancellationToken token);
}

public static class Gather
{
    public static void Validate(GatherTarget[] targets, GatherArea area)
    {
        if (targets.Length is < 1 or > 8 || targets.Select(t => t.ItemType).Distinct().Count() != targets.Length
            || targets.Any(t => t.ItemType <= 0 || t.Count is < 1 or > 9999 || t.Npcs.Any(id => id <= 0)
                || t.TileFrameX is < 0 or > short.MaxValue || t.TileFrameY is < 0 or > short.MaxValue))
            throw new ArgumentException("需要1..8种不重复的物品，正数物品/NPC编号，目标数量1..9999。");
        if (area == null && targets.Any(t => t.TileFrameX != null || t.TileFrameY != null))
            throw new ArgumentException("按方块frame筛选来源需要source_area，先观察实际变体及坐标。");
    }

    public static async Task<JsonObject> Run(GatherTarget[] targets, int seconds, IGatherWorld world, CancellationToken token, GatherArea area = null)
    {
        Validate(targets, area);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        var timer = Stopwatch.StartNew();
        var attempts = new Dictionary<string, int>();
        JsonObject scene = null, last = null;
        var blockedSources = new JsonArray();
        JsonNode sourceError = null;
        string outcome = "attempt_limit";
        int actions = 0;
        int Count(GatherTarget target) => scene?["counts"]?[target.ItemType.ToString()]?.GetValue<int>() ?? 0;
        try
        {
            for (int step = 0; step < 32; step++)
            {
                scene = await world.Observe(budget.Token);
                if (targets.All(t => Count(t) >= t.Count)) { outcome = "enough"; break; }
                if (scene["error"] != null)
                { outcome = scene["error"].ToString(); sourceError = scene["source_error"]?.DeepClone(); break; }
                var available = scene["candidates"].AsArray().OfType<JsonObject>()
                    .Where(c => area == null || area.Contains(c))
                    .Where(c => targets.Any(t => t.ItemType == c["item_type"].GetValue<int>() && Count(t) < t.Count))
                    .ToArray();
                var candidate = available
                    .Where(c => attempts.GetValueOrDefault(c["id"].ToString()) < 2)
                    .OrderBy(c => c["distance"].GetValue<float>() + (c["kind"].ToString() == "drop" ? -1000 : c["kind"].ToString() == "npc" ? -20 : 0))
                    .FirstOrDefault();
                if (candidate == null)
                {
                    outcome = available.Length == 0 ? "source_unavailable" : "attempt_limit";
                    foreach (var source in available)
                        blockedSources.Add(new JsonObject { ["candidate"] = source.DeepClone(),
                            ["attempts"] = attempts.GetValueOrDefault(source["id"].ToString()) });
                    break;
                }
                string id = candidate["id"].ToString();
                attempts[id] = attempts.GetValueOrDefault(id) + 1;
                actions++;
                last = new JsonObject { ["candidate"] = candidate.DeepClone() };
                last["result"] = await world.Act(candidate, budget.Token);
                if (last["result"]?["outcome"]?.ToString() == "combat_obstructed")
                { outcome = "combat_obstructed"; break; }
                if (last["result"]?["outcome"]?.ToString() == "unsafe"
                    || last["result"]?["navigation"]?["outcome"]?.ToString() == "unsafe")
                { outcome = "unsafe"; break; }
            }
        }
        finally
        {
            if (!token.IsCancellationRequested) await world.Stop(token);
        }
        scene = await world.Observe(token);
        bool enough = targets.All(t => Count(t) >= t.Count);
        return new JsonObject { ["ok"] = enough, ["outcome"] = enough ? "enough" : outcome,
            ["source_area"] = area?.Snapshot(),
            ["actions"] = actions, ["elapsed_ms"] = timer.ElapsedMilliseconds, ["last_result"] = last,
            ["source_error"] = sourceError,
            ["blocked_sources"] = blockedSources,
            ["targets"] = new JsonArray(targets.Select(t => (JsonNode)new JsonObject
                { ["item_type"] = t.ItemType, ["wanted"] = t.Count, ["actual"] = Count(t), ["met"] = Count(t) >= t.Count }).ToArray()) };
    }
}
