using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Terraria;

namespace TerraBlind;

public sealed class GameGather : IGatherWorld
{
    readonly GatherTarget[] _targets;
    readonly GatherArea _area;
    readonly Dictionary<int, (NPC Entity, int Generation)> observedNpcs = new();
    readonly Dictionary<int, (Item Entity, int Generation)> observedDrops = new();
    int dropGeneration;
    int npcGeneration;
    public GameGather(GatherTarget[] targets, GatherArea area = null) { _targets = targets; _area = area; }

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
                    {
                        if (!observedDrops.TryGetValue(i, out var identity) || !ReferenceEquals(identity.Entity, item))
                            observedDrops[i] = identity = (item, ++dropGeneration);
                        var candidate = Candidate(target.ItemType, "drop", i, item.Center.X / 16, item.Center.Y / 16, p);
                        candidate["drop_generation"] = identity.Generation;
                        candidate["id"] = $"{target.ItemType}:drop:{i}:{identity.Generation}";
                        candidates.Add(candidate);
                    }
                }
                for (int i = 0; i < Main.maxNPCs; i++)
                {
                    var npc = Main.npc[i];
                    if (npc.active && !npc.friendly && !npc.townNPC && target.Npcs.Contains(npc.type)
                        && Microsoft.Xna.Framework.Vector2.Distance(npc.Center, p.Center) < 1280)
                    {
                        var candidate = Candidate(target.ItemType, "npc", i, npc.Center.X / 16, npc.Center.Y / 16, p);
                        if (!observedNpcs.TryGetValue(i, out var identity) || !ReferenceEquals(identity.Entity, npc))
                            observedNpcs[i] = identity = (npc, ++npcGeneration);
                        candidate["npc_generation"] = identity.Generation;
                        candidate["id"] = $"{target.ItemType}:npc:{i}:{identity.Generation}";
                        candidate["npc_type"] = npc.type;
                        candidate["hp"] = npc.life;
                        candidate["clear_line"] = Collision.CanHitLine(p.Center, 4, 4, npc.Center, 4, 4);
                        candidates.Add(candidate);
                    }
                }
            }
            return new JsonObject { ["counts"] = counts, ["candidates"] = candidates,
                ["source_rules"] = Gather.DescribeSources(_targets),
                ["error"] = p.dead ? "interrupted_by_death" : null };
        }, token);
        if (scene["error"] != null) return scene;
        foreach (var target in _targets.Where(t => scene["counts"][t.ItemType.ToString()].GetValue<int>() < t.Count))
        foreach (string tile in target.Tiles)
        {
            var found = await AgentTools.FindTiles(new JsonObject { ["name"] = tile, ["n"] = 12, ["max_dist"] = 2000,
                ["source_area"] = _area?.Snapshot(), ["frame_x"] = target.TileFrameX, ["frame_y"] = target.TileFrameY }, token);
            if (found["error"] != null)
            { scene["error"] = found["error"].DeepClone(); scene["source_error"] = found.DeepClone(); return scene; }
            foreach (var cell in found["tiles"]?.AsArray() ?? new JsonArray())
                scene["candidates"].AsArray().Add(new JsonObject { ["item_type"] = target.ItemType, ["kind"] = "tile",
                    ["id"] = $"{target.ItemType}:tile:{cell["x"]}:{cell["y"]}", ["x"] = cell["x"].DeepClone(),
                    ["y"] = cell["y"].DeepClone(), ["distance"] = cell["dist"]?.DeepClone() ?? JsonValue.Create(0),
                    ["tile_type"] = cell["tile_type"]?.DeepClone(), ["frame_x"] = cell["frame_x"]?.DeepClone(), ["frame_y"] = cell["frame_y"]?.DeepClone() });
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
        if (kind == "tile")
        {
            bool unchanged = await NativeAgent.OnMain(() =>
            {
                var tile = Main.tile[x, y];
                return tile.HasTile && (candidate["tile_type"] == null || tile.TileType == candidate["tile_type"].GetValue<int>())
                    && (candidate["frame_x"] == null || tile.TileFrameX == candidate["frame_x"].GetValue<int>())
                    && (candidate["frame_y"] == null || tile.TileFrameY == candidate["frame_y"].GetValue<int>());
            }, token);
            if (!unchanged) return new JsonObject { ["error"] = "target_changed" };
            return await AgentTools.CollectTarget(x, y, token, false) ?? new JsonObject { ["error"] = "target_changed" };
        }
        int who = candidate["who"].GetValue<int>();
        if (kind == "drop")
        {
            bool ValidDrop() => observedDrops.TryGetValue(who, out var identity)
                && identity.Generation == candidate["drop_generation"]?.GetValue<int>()
                && ReferenceEquals(identity.Entity, Main.item[who]) && Main.item[who].active
                && Main.item[who].type == candidate["item_type"].GetValue<int>();
            int stable = 0;
            Microsoft.Xna.Framework.Vector2? previousPosition = null;
            for (int i = 0; i < 20; i++)
            {
                var sample = await NativeAgent.OnMain(() =>
                {
                    var item = Main.item[who];
                    return (Valid: ValidDrop(), Position: item.Center);
                }, token);
                if (!sample.Valid) return new JsonObject { ["outcome"] = "target_changed" };
                stable = previousPosition.HasValue && (sample.Position - previousPosition.Value).LengthSquared() <= .25f ? stable + 1 : 0;
                previousPosition = sample.Position;
                if (stable >= 2) break;
                await Task.Delay(150, token);
            }
            var live = await NativeAgent.OnMain(() =>
            {
                var item = Main.item[who];
                DiagLog.Write($"[gather] drop who={who} pos={item.Center} velocity={item.velocity} player={Main.LocalPlayer.Center}");
                if (!ValidDrop()) return null;
                if (stable < 2 || (item.Center - previousPosition.Value).LengthSquared() > .25f)
                    return new JsonObject { ["moving"] = true };
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
            && !Main.npc[who].townNPC && Main.npc[who].type == candidate["npc_type"].GetValue<int>()
            && observedNpcs.TryGetValue(who, out var identity) && ReferenceEquals(identity.Entity, Main.npc[who])
            && candidate["npc_generation"]?.GetValue<int>() == identity.Generation;
        if (!await NativeAgent.OnMain(Valid, token)) return new JsonObject { ["error"] = "target_changed" };
        var engagement = await NativeAgent.OnMain(() =>
        {
            if (!Valid()) return new JsonObject { ["error"] = "target_changed" };
            var player = Main.LocalPlayer;
            var npc = Main.npc[who];
            float distance = Microsoft.Xna.Framework.Vector2.Distance(player.Center, npc.Center) / 16;
            return distance <= 40 ? null : new JsonObject { ["outcome"] = "combat_out_of_range",
                ["npc"] = who, ["npc_type"] = npc.type, ["distance_tiles"] = distance, ["combat_range_tiles"] = 40,
                ["target_x"] = npc.Center.X / 16, ["target_y"] = npc.Center.Y / 16,
                ["player_x"] = player.Center.X / 16, ["player_y"] = player.Center.Y / 16,
                ["hp"] = player.statLife, ["target_hp"] = npc.life,
                ["clear_line"] = Collision.CanHitLine(player.Center, 4, 4, npc.Center, 4, 4),
                ["detail"] = "敌怪超出当前战斗范围，未向敌怪坐标导航；选择并验证人物站位后恢复原采集句柄。" };
        }, token);
        if (engagement != null) return engagement;
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
        int blockedSamples = 0, previousHp = hpBefore;
        JsonObject combat = null;
        try
        {
            for (int i = 0; i < 32; i++)
            {
                await Task.Delay(250, token);
                combat = await NativeAgent.OnMain(() =>
                {
                    var player = Main.LocalPlayer;
                    bool present = Valid();
                    return new JsonObject { ["target_present"] = present, ["hp_before"] = hpBefore,
                        ["hp_after"] = present ? Main.npc[who].life : 0, ["self_dead"] = player.dead,
                        ["clear_line"] = present && Collision.CanHitLine(player.Center, 4, 4, Main.npc[who].Center, 4, 4),
                        ["requires_clear_line"] = FightCoordinator.NeedsClearLine(player.HeldItem),
                        ["weapon_type"] = player.HeldItem.type,
                        ["target_x"] = present ? Main.npc[who].Center.X / 16f : null,
                        ["target_y"] = present ? Main.npc[who].Center.Y / 16f : null };
                }, token);
                if (!combat["target_present"].GetValue<bool>() || combat["self_dead"].GetValue<bool>()) break;
                int hp = combat["hp_after"].GetValue<int>();
                blockedSamples = !combat["clear_line"].GetValue<bool>() && combat["requires_clear_line"].GetValue<bool>()
                    && hp >= previousHp ? blockedSamples + 1 : 0;
                previousHp = hp;
                if (blockedSamples >= 8)
                {
                    combat["outcome"] = "combat_obstructed";
                    combat["blocked_samples"] = blockedSamples;
                    combat["sample_interval_ms"] = 250;
                    return combat;
                }
            }
            return combat;
        }
        finally { if (!token.IsCancellationRequested) await NativeAgent.OnMain(() => { FightCoordinator.Stop(); return true; }, token); }
    }

    public Task Stop(CancellationToken token) => NativeAgent.OnMain(() =>
    { RecedingNav.Stop(); ItemUseCoordinator.Stop(); FightCoordinator.Stop(); return true; }, token);
}
