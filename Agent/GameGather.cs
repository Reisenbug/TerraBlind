using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Terraria;

namespace TerraBlind;

public sealed class GameGather : IGatherWorld
{
    readonly GatherTarget[] _targets;
    public GameGather(GatherTarget[] targets) => _targets = targets;

    public async Task<JsonObject> Observe(CancellationToken token)
    {
        var scene = await NativeAgent.OnMain(() =>
        {
            var p = Main.LocalPlayer;
            var counts = new JsonObject(); var candidates = new JsonArray();
            foreach (var target in _targets)
            {
                counts[target.ItemType.ToString()] = p.inventory.Where(i => !i.IsAir && i.type == target.ItemType).Sum(i => i.stack);
                for (int i = 0; i < Main.maxItems; i++)
                {
                    var item = Main.item[i];
                    if (item.active && item.type == target.ItemType && Microsoft.Xna.Framework.Vector2.Distance(item.Center, p.Center) < 1600)
                        candidates.Add(Candidate(target.ItemType, "drop", i, item.Center.X / 16, item.Center.Y / 16, p));
                }
                for (int i = 0; i < Main.maxNPCs; i++)
                {
                    var npc = Main.npc[i];
                    if (npc.active && !npc.friendly && !npc.townNPC && target.Npcs.Contains(npc.type)
                        && Microsoft.Xna.Framework.Vector2.Distance(npc.Center, p.Center) < 1280)
                    {
                        var candidate = Candidate(target.ItemType, "npc", i, npc.Center.X / 16, npc.Center.Y / 16, p);
                        candidate["npc_type"] = npc.type;
                        candidates.Add(candidate);
                    }
                }
            }
            return new JsonObject { ["counts"] = counts, ["candidates"] = candidates,
                ["error"] = p.dead ? "interrupted_by_death" : null };
        }, token);
        if (scene["error"] != null) return scene;
        foreach (var target in _targets.Where(t => scene["counts"][t.ItemType.ToString()].GetValue<int>() < t.Count))
        foreach (string tile in target.Tiles)
        {
            var found = await AgentTools.Call("/find_tiles", new JsonObject { ["name"] = tile, ["n"] = 12, ["max_dist"] = 2000 }, token);
            if (found["error"] != null) { scene["error"] = found["error"].DeepClone(); return scene; }
            foreach (var cell in found["tiles"]?.AsArray() ?? new JsonArray())
                scene["candidates"].AsArray().Add(new JsonObject { ["item_type"] = target.ItemType, ["kind"] = "tile",
                    ["id"] = $"{target.ItemType}:tile:{cell["x"]}:{cell["y"]}", ["x"] = cell["x"].DeepClone(),
                    ["y"] = cell["y"].DeepClone(), ["distance"] = cell["dist"]?.DeepClone() ?? JsonValue.Create(0) });
        }
        return scene;
    }

    static JsonObject Candidate(int item, string kind, int who, float x, float y, Player p) => new()
    {
        ["item_type"] = item, ["kind"] = kind, ["who"] = who, ["id"] = $"{item}:{kind}:{who}",
        ["x"] = (int)x, ["y"] = (int)y,
        ["distance"] = Microsoft.Xna.Framework.Vector2.Distance(p.Center, new Microsoft.Xna.Framework.Vector2(x * 16, y * 16)) / 16
    };

    public async Task<JsonObject> Act(JsonObject candidate, CancellationToken token)
    {
        await NativeAgent.OnMain(() => { DiagLog.Write($"[gather] start {candidate}"); return true; }, token);
        var result = await ActCore(candidate, token);
        await NativeAgent.OnMain(() => { DiagLog.Write($"[gather] result {candidate["id"]} {result}"); return true; }, token);
        return result;
    }

    async Task<JsonObject> ActCore(JsonObject candidate, CancellationToken token)
    {
        string kind = candidate["kind"].ToString();
        int x = candidate["x"].GetValue<int>(), y = candidate["y"].GetValue<int>();
        if (kind == "tile") return await AgentTools.CollectTarget(x, y, token, false) ?? new JsonObject { ["error"] = "target_changed" };
        int who = candidate["who"].GetValue<int>();
        if (kind == "drop")
        {
            int stable = 0;
            for (int i = 0; i < 20; i++)
            {
                bool moving = await NativeAgent.OnMain(() => Main.item[who].active && Main.item[who].velocity.LengthSquared() > .01f, token);
                stable = moving ? 0 : stable + 1;
                if (stable >= 2) break;
                await Task.Delay(150, token);
            }
            var live = await NativeAgent.OnMain(() =>
            {
                var item = Main.item[who];
                DiagLog.Write($"[gather] drop who={who} pos={item.Center} velocity={item.velocity} player={Main.LocalPlayer.Center}");
                if (item.velocity.LengthSquared() > .01f) return new JsonObject { ["moving"] = true };
                return item.active && item.type == candidate["item_type"].GetValue<int>()
                    ? new JsonObject { ["gx"] = (int)(item.Center.X / 16), ["gy"] = (int)(item.Center.Y / 16), ["stand"] = true,
                        ["near"] = Microsoft.Xna.Framework.Vector2.Distance(item.Center, Main.LocalPlayer.Center) <= 48 } : null;
            }, token);
            if (live == null) return new JsonObject { ["outcome"] = "target_changed" };
            if (live["moving"] != null) return new JsonObject { ["outcome"] = "drop_still_moving" };
            if (live["near"]?.GetValue<bool>() == true)
            {
                await Task.Delay(400, token);
                return new JsonObject { ["outcome"] = "waited_for_pickup" };
            }
            live.Remove("near");
            var start = await AgentTools.Call("/nav_recede", live, token);
            if (AgentTools.Rejected(start)) return start;
            var result = await AgentTools.Wait("/nav_recede_done", "nav", "/nav_recede_stop", 12, token);
            await Task.Delay(400, token);
            return result;
        }
        bool Valid() => who >= 0 && who < Main.maxNPCs && Main.npc[who].active && !Main.npc[who].friendly
            && !Main.npc[who].townNPC && Main.npc[who].type == candidate["npc_type"].GetValue<int>();
        if (!await NativeAgent.OnMain(Valid, token)) return new JsonObject { ["error"] = "target_changed" };
        if (candidate["distance"].GetValue<float>() > 30)
        {
            var approach = await AgentTools.Navigate(new JsonObject { ["x"] = x, ["y"] = y }, token, 8);
            if (approach["done"]?.GetValue<bool>() != true) return approach;
        }
        int hpBefore = 0;
        var startFight = await NativeAgent.OnMain(() =>
        {
            if (!Valid()) return false;
            if (!AgentPermissions.Attack) throw new InvalidOperationException("玩家禁止自动攻击。");
            hpBefore = Main.npc[who].life;
            FightCoordinator.Start(40, new[] { candidate["npc_type"].GetValue<int>() });
            return true;
        }, token);
        if (!startFight) return new JsonObject { ["error"] = "target_changed" };
        try
        {
            for (int i = 0; i < 32; i++)
            {
                await Task.Delay(250, token);
                if (!await NativeAgent.OnMain(() => Valid() && !Main.LocalPlayer.dead, token)) break;
            }
            return await NativeAgent.OnMain(() => new JsonObject { ["target_present"] = Valid(),
                ["hp_before"] = hpBefore, ["hp_after"] = Valid() ? Main.npc[who].life : 0, ["self_dead"] = Main.LocalPlayer.dead }, token);
        }
        finally { if (!token.IsCancellationRequested) await NativeAgent.OnMain(() => { FightCoordinator.Stop(); return true; }, token); }
    }

    public Task Stop(CancellationToken token) => NativeAgent.OnMain(() =>
    { RecedingNav.Stop(); ItemUseCoordinator.Stop(); FightCoordinator.Stop(); return true; }, token);
}
