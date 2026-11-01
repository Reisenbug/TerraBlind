using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TerraBlind;

public sealed record GatherTarget(int ItemType, int Count, string[] Tiles, int[] Npcs);

public interface IGatherWorld
{
    Task<JsonObject> Observe(CancellationToken token);
    Task<JsonObject> Act(JsonObject candidate, CancellationToken token);
    Task Stop(CancellationToken token);
}

public static class Gather
{
    public static async Task<JsonObject> Run(GatherTarget[] targets, int seconds, IGatherWorld world, CancellationToken token)
    {
        if (targets.Length is < 1 or > 8 || targets.Select(t => t.ItemType).Distinct().Count() != targets.Length
            || targets.Any(t => t.ItemType <= 0 || t.Count is < 1 or > 9999 || t.Npcs.Any(id => id <= 0)))
            throw new ArgumentException("需要1..8种不重复的物品，正数物品/NPC编号，目标数量1..9999。");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        var timer = Stopwatch.StartNew();
        var attempts = new Dictionary<string, int>();
        JsonObject scene = null, last = null;
        string outcome = "attempt_limit";
        int actions = 0;
        int Count(GatherTarget target) => scene?["counts"]?[target.ItemType.ToString()]?.GetValue<int>() ?? 0;
        try
        {
            for (int step = 0; step < 32; step++)
            {
                scene = await world.Observe(budget.Token);
                if (targets.All(t => Count(t) >= t.Count)) { outcome = "enough"; break; }
                if (scene["error"] != null) { outcome = scene["error"].ToString(); break; }
                var candidate = scene["candidates"].AsArray().OfType<JsonObject>()
                    .Where(c => targets.Any(t => t.ItemType == c["item_type"].GetValue<int>() && Count(t) < t.Count))
                    .Where(c => attempts.GetValueOrDefault(c["id"].ToString()) < 2)
                    .OrderBy(c => c["distance"].GetValue<float>() + (c["kind"].ToString() == "drop" ? -1000 : c["kind"].ToString() == "npc" ? -20 : 0))
                    .FirstOrDefault();
                if (candidate == null) { outcome = "source_unavailable"; break; }
                string id = candidate["id"].ToString();
                attempts[id] = attempts.GetValueOrDefault(id) + 1;
                actions++;
                last = new JsonObject { ["candidate"] = candidate.DeepClone() };
                last["result"] = await world.Act(candidate, budget.Token);
            }
        }
        finally
        {
            if (!token.IsCancellationRequested) await world.Stop(token);
        }
        scene = await world.Observe(token);
        bool enough = targets.All(t => Count(t) >= t.Count);
        return new JsonObject { ["ok"] = enough, ["outcome"] = enough ? "enough" : outcome,
            ["actions"] = actions, ["elapsed_ms"] = timer.ElapsedMilliseconds, ["last_result"] = last,
            ["targets"] = new JsonArray(targets.Select(t => (JsonNode)new JsonObject
                { ["item_type"] = t.ItemType, ["wanted"] = t.Count, ["actual"] = Count(t), ["met"] = Count(t) >= t.Count }).ToArray()) };
    }
}
