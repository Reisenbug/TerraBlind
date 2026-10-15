using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Terraria;

namespace TerraBlind;

public static class AgentTools
{
    static readonly Dictionary<string, int> Tracking = new();
    static readonly HttpClient Wiki = new() { Timeout = TimeSpan.FromSeconds(20) };
    static readonly HttpClient HarvestHttp = new() { Timeout = TimeSpan.FromSeconds(8) };
    public static void NewTask() => Tracking.Clear();
    public static void Stop()
    {
        Halt.All();
        StateSnapshotPlayer.StopRemoteTask();
        FightCoordinator.Stop(); ReplaySystem.Stop(); KeyPress.Clear();
        PlayerCombat.Stop();
        StateSpacePlanner.PreserveTerrain = false;
        HttpServerSystem.PendingControl = null;
    }

    static int Int(JsonObject args, string key, int fallback = 0) => args[key]?.GetValue<int>() ?? fallback;
    static string Text(JsonObject args, string key) => args[key]?.GetValue<string>() ?? "";
    static bool Bool(JsonObject args, string key) => args[key]?.GetValue<bool>() ?? false;
    static bool Rejected(JsonObject result) => result["error"] != null ||
        result["ok"]?.ToString() == "false" || result["accepted"]?.ToString() == "false";

    static Task<JsonObject> Call(string path, JsonObject args, CancellationToken token) => NativeAgent.OnMain(() =>
    {
        token.ThrowIfCancellationRequested();
        var response = HttpServerSystem.Dispatch(path, args?.ToJsonString(new System.Text.Json.JsonSerializerOptions
        { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? "{}");
        var result = JsonNode.Parse(response.Body) as JsonObject
            ?? throw new InvalidOperationException($"{path} 未返回 JSON 对象。");
        if (response.Status >= 400 && result["error"] == null) result["error"] = $"status {response.Status}";
        return result;
    }, token);

    public static Task<string> State(CancellationToken token) => NativeAgent.OnMain(() =>
    {
        var state = JsonNode.Parse(StateSerializer.ToJson(HttpServerSystem.LatestSnapshot)) as JsonObject ?? new();
        state["agent"] = new JsonObject
        {
            ["status"] = NativeAgent.Status, ["auto_dodge"] = AgentPermissions.Dodge,
            ["auto_attack"] = AgentPermissions.Attack, ["player_id"] = Main.LocalPlayer.whoAmI,
            ["pvp"] = Main.LocalPlayer.hostile, ["team"] = Main.LocalPlayer.team,
            ["instruction_player_id"] = AgentNetwork.OwnerPlayer,
            ["pvp_target"] = PlayerCombat.Target
        };
        var players = new JsonArray();
        for (int id = 0; id < Main.maxPlayers; id++)
        {
            var other = Main.player[id];
            if (other == null || !other.active || id == Main.LocalPlayer.whoAmI) continue;
            players.Add(new JsonObject
            {
                ["id"] = id, ["name"] = other.name, ["hp"] = other.statLife, ["dead"] = other.dead,
                ["pvp"] = other.hostile, ["team"] = other.team,
                ["pos"] = new JsonObject { ["x"] = other.position.X, ["y"] = other.position.Y },
                ["pvp_block_reason"] = PlayerCombat.TargetReason(Main.LocalPlayer, id)
            });
        }
        state["other_players"] = players;
        return state.ToJsonString();
    }, token);

    static Dictionary<string, int> Inventory()
    {
        var items = new Dictionary<string, int>();
        foreach (var item in Main.LocalPlayer.inventory)
            if (item != null && !item.IsAir) items[item.Name] = items.GetValueOrDefault(item.Name) + item.stack;
        return items;
    }

    static Task<string> Result(JsonObject result, Dictionary<string, int> before, CancellationToken token) => NativeAgent.OnMain(() =>
    {
        var player = Main.LocalPlayer;
        var now = Inventory();
        var got = new JsonObject(); var lost = new JsonObject(); var progress = new JsonObject();
        foreach (var item in now)
            if (item.Value > before.GetValueOrDefault(item.Key)) got[item.Key] = item.Value - before.GetValueOrDefault(item.Key);
        foreach (var item in before)
            if (item.Value > now.GetValueOrDefault(item.Key)) lost[item.Key] = item.Value - now.GetValueOrDefault(item.Key);
        foreach (var item in Tracking) progress[item.Key] = $"{now.GetValueOrDefault(item.Key)}/{item.Value}";
        result["state"] = new JsonObject
        {
            ["pos"] = new JsonObject { ["x"] = ActExecutor.OriginCx(player), ["y"] = ActExecutor.OriginCy(player) },
            ["hp"] = player.statLife, ["on_ground"] = player.velocity.Y == 0f
        };
        result["got"] = got; result["lost"] = lost;
        if (progress.Count > 0) result["进度"] = progress;
        return result.ToJsonString();
    }, token);

    internal static async Task<JsonObject> Wait(string path, string active, string stop, int seconds, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed.TotalSeconds < seconds)
        {
            await Task.Delay(150, token);
            var status = await Call(path, null, token);
            if (active == "nav" ? Bool(status, "done") || Text(status, "status") == "failed" : !Bool(status, active))
                return status;
        }
        await Call(stop, stop == "/fight" ? new JsonObject { ["active"] = false } : null, token);
        var final = await Call(path, null, token);
        final["outcome"] = "timeout";
        return final;
    }

    public static async Task<string> Execute(string name, JsonObject args, CancellationToken token)
    {
        await NativeAgent.OnMain(() => { DiagLog.Write($"[agent] tool {name} {args}"); return true; }, token);
        if (name == "get_state") return await State(token);
        if (name == "say")
        {
            await NativeAgent.OnMain(() => { AgentChat.Say(AgentConversation.VisibleText(Text(args, "text"))); return true; }, token);
            return "ok";
        }
        if (name == "ask") return new JsonObject { ["player_answer"] = await NativeAgent.Ask(AgentConversation.VisibleText(Text(args, "question")), token) }.ToJsonString();
        if (name is "wiki_search" or "wiki_page") return await ReadWiki(name, args, token);
        var before = await NativeAgent.OnMain(Inventory, token);
        JsonObject result;
        switch (name)
        {
            case "set_combat":
                result = await NativeAgent.OnMain(() =>
                {
                    if (args["auto_dodge"] != null) AgentPermissions.Dodge = Bool(args, "auto_dodge");
                    if (args["auto_attack"] != null) AgentPermissions.Attack = Bool(args, "auto_attack");
                    if (!AgentPermissions.Attack) { Combat.Stop(); FightCoordinator.Stop(); PlayerCombat.Stop(); }
                    return new JsonObject { ["auto_dodge"] = AgentPermissions.Dodge, ["auto_attack"] = AgentPermissions.Attack };
                }, token);
                break;
            case "track":
                result = await NativeAgent.OnMain(() =>
                {
                    Tracking.Clear();
                    foreach (var item in args["items"].AsObject()) Tracking[item.Key] = item.Value.GetValue<int>();
                    return new JsonObject { ["outcome"] = "tracking" };
                }, token);
                break;
            case "item_info": case "find_biome": case "find_descent": case "descent_route":
            case "tile_names": case "find_tiles": case "recipe":
                result = await Call("/" + name, args, token);
                break;
            case "nav_to":
                result = await Navigate(args, token);
                break;
            case "mine_vein":
                result = await MineVein(args, token);
                break;
            case "chop_wood":
                int wantedWood = Int(args, "count");
                if (wantedWood < 1 || wantedWood > 9999) return "{\"error\":\"count 必须在 1 到 9999 之间。\"}";
                string key = await NativeAgent.OnMain(() => Config.I.TypeSafeKey?.Trim(), token);
                if (string.IsNullOrEmpty(key)) return "{\"error\":\"请先配置 Jev 的 TypeSafe Key。\"}";
                await NativeAgent.OnMain(() => { StateSpacePlanner.PreserveTerrain = true; return true; }, token);
                try
                {
                    var decision = new WoodHarvestDecision(HarvestHttp, key);
                    result = await WoodHarvest.Run(wantedWood, Text(args, "constraints"), new GameWoodHarvest(), async (scene, ct) =>
                    {
                        var clock = Stopwatch.StartNew();
                        string choice = await decision.Choose(scene, ct);
                        await NativeAgent.OnMain(() => { DiagLog.Write($"[harvest] jev choice={choice} ms={clock.ElapsedMilliseconds} scene={scene}"); return true; }, ct);
                        return choice;
                    }, token);
                }
                finally
                {
                    await NativeAgent.OnMain(() => { StateSpacePlanner.PreserveTerrain = false; return true; }, token);
                }
                break;
            case "mine":
                result = await Call("/mine", new JsonObject { ["dir"] = Text(args, "dir"),
                    ["target_wx"] = Int(args, "target_x"), ["target_wy"] = Int(args, "target_y") }, token);
                if (!Rejected(result)) result = await Wait("/mine_status", "running", "/mine_stop", 120, token);
                break;
            case "use_item":
                result = await Call("/item_use", new JsonObject
                {
                    ["target_wx"] = Int(args, "x"), ["target_wy"] = Int(args, "y"), ["slot"] = Int(args, "slot", -1),
                    ["duration_ticks"] = Int(args, "duration_ticks", 30), ["strict"] = Bool(args, "strict")
                }, token);
                if (!Rejected(result)) result = await Wait("/item_use_status", "active", "/item_use_stop", 60, token);
                break;
            case "place_at":
                result = await Place(args, token);
                break;
            case "interact":
                result = await Call("/interact", new JsonObject { ["tile_x"] = Int(args, "x"), ["tile_y"] = Int(args, "y") }, token);
                if (!Rejected(result))
                {
                    await Task.Delay(200, token);
                    result = await NativeAgent.OnMain(() => new JsonObject { ["outcome"] = HttpServerSystem.LastInteract }, token);
                }
                break;
            case "loot_all":
                result = await Call("/loot_all", args, token);
                await Task.Delay(300, token);
                break;
            case "craft":
                result = await Call("/craft", new JsonObject { ["item_name"] = Text(args, "name"), ["amount"] = Int(args, "amount", 1) }, token);
                break;
            case "act":
                result = await Call("/act", args, token);
                if (!Rejected(result)) result = await Wait("/act_status", "active", "/act_stop", 120, token);
                break;
            case "fight":
                if (!await NativeAgent.OnMain(() => AgentPermissions.Attack, token))
                    return "{\"error\":\"玩家禁止自动攻击，请先确认是否修改许可。\"}";
                result = await Call("/fight", args, token);
                if (!Rejected(result))
                {
                    result = await Wait("/fight_active", "active", "/fight", Math.Clamp((int)Math.Ceiling(args["seconds"]?.GetValue<double>() ?? 10), 1, 120), token);
                    await Call("/fight", new JsonObject { ["active"] = false }, token);
                }
                break;
            case "fight_player":
                int target = Int(args, "player_id", -1);
                int seconds = Math.Clamp((int)Math.Ceiling(args["seconds"]?.GetValue<double>() ?? 10), 1, 120);
                float range = Math.Clamp(Int(args, "max_dist", 80), 1, 200);
                result = await NativeAgent.OnMain(() =>
                {
                    if (!PlayerCombat.Start(target, seconds, range, out string reason))
                        return new JsonObject { ["error"] = reason };
                    return new JsonObject { ["target_player"] = target, ["target_hp_before"] = Main.player[target].statLife,
                        ["self_pvp_enabled"] = Main.LocalPlayer.hostile };
                }, token);
                if (!Rejected(result))
                {
                    var timer = Stopwatch.StartNew();
                    while (await NativeAgent.OnMain(() => PlayerCombat.Active, token) && timer.Elapsed.TotalSeconds < seconds + 3)
                        await Task.Delay(150, token);
                    await NativeAgent.OnMain(() =>
                    {
                        if (PlayerCombat.Active) PlayerCombat.Stop("timeout");
                        result["outcome"] = PlayerCombat.Outcome;
                        result["target_hp_after"] = Main.player[target].statLife;
                        return true;
                    }, token);
                }
                break;
            default: throw new InvalidOperationException($"未知工具: {name}");
        }
        string output = await Result(result, before, token);
        await NativeAgent.OnMain(() => { DiagLog.Write($"[agent] result {name} {output}"); return true; }, token);
        return output;
    }

    internal static async Task<JsonObject> Navigate(JsonObject args, CancellationToken token, int seconds = 180)
    {
        var start = await Call("/nav_recede", new JsonObject
        {
            ["gx"] = Int(args, "x"), ["gy"] = Int(args, "y"), ["exact"] = Bool(args, "exact"), ["reach"] = Bool(args, "reach")
        }, token);
        if (Rejected(start)) return start;
        var result = await Wait("/nav_recede_done", "nav", "/nav_recede_stop", seconds, token);
        return await NativeAgent.OnMain(() =>
        {
            var p = Main.LocalPlayer;
            int distance = Math.Max(Math.Abs(ActExecutor.OriginCx(p) - Int(args, "x")), Math.Abs(ActExecutor.OriginCy(p) - Int(args, "y")));
            bool arrived = Bool(args, "reach") ? Reach.CanInteract(p, Int(args, "x"), Int(args, "y")) : distance <= 6;
            if (Bool(result, "done") && !arrived)
            { result["done"] = false; result["status"] = "stopped_short"; result["dist"] = distance; }
            return result;
        }, token);
    }

    static async Task<JsonObject> Place(JsonObject args, CancellationToken token)
    {
        int count = Math.Clamp(Int(args, "n", 1), 1, 200);
        int placed = 0;
        var cells = new JsonArray();
        for (int i = 0; i < count; i++)
        {
            var payload = new JsonObject { ["item"] = Text(args, "item") };
            if (args["x"] != null && args["y"] != null)
                payload["world"] = new JsonArray(Int(args, "x") + i * Int(args, "step_x"), Int(args, "y") + i * Int(args, "step_y"));
            var result = await Call("/place_anywhere", payload, token);
            if (!Rejected(result)) result = await Wait("/place_anywhere_status", "running", "/place_anywhere_stop", 120, token);
            cells.Add(result);
            if (Text(result, "outcome") != "done") break;
            placed++;
        }
        return new JsonObject { ["outcome"] = placed == count ? "done" : placed > 0 ? "partial" : "stuck", ["placed"] = placed, ["wanted"] = count, ["cells"] = cells };
    }

    static async Task<JsonObject> MineVein(JsonObject args, CancellationToken token)
    {
        string name = Text(args, "name");
        int wanted = Math.Clamp(Int(args, "count", 1), 1, 1000), removed = 0;
        var skipped = new HashSet<(int, int)>();
        string outcome = "exhausted";
        for (int attempt = 0; attempt < wanted + 8 && removed < wanted; attempt++)
        {
            var found = await Call("/find_tiles", new JsonObject { ["name"] = name, ["n"] = 30, ["max_dist"] = 2000 }, token);
            var target = found["tiles"]?.AsArray().OfType<JsonObject>().FirstOrDefault(t => !skipped.Contains((Int(t, "x"), Int(t, "y"))));
            if (target == null) break;
            int x = Int(target, "x"), y = Int(target, "y");
            skipped.Add((x, y));
            await Navigate(new JsonObject { ["x"] = x, ["y"] = y, ["exact"] = true }, token);
            var reach = await Call("/mine_reach", null, token);
            if (Rejected(reach)) continue;
            int slot = await NativeAgent.OnMain(() => Enumerable.Range(0, 10)
                .OrderByDescending(i => Main.LocalPlayer.inventory[i].pick)
                .FirstOrDefault(i => Main.LocalPlayer.inventory[i].pick > 0, -1), token);
            if (slot < 0) return new JsonObject { ["outcome"] = "no_pickaxe", ["tiles_removed"] = removed };
            var nearby = await Call("/find_tiles", new JsonObject { ["name"] = name, ["n"] = 40, ["max_dist"] = 60 }, token);
            foreach (var tile in nearby["tiles"]?.AsArray().OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            {
                if (removed >= wanted) break;
                int tx = Int(tile, "x"), ty = Int(tile, "y");
                if (tx < Int(reach, "lx") || tx > Int(reach, "hx") || ty < Int(reach, "ly") || ty > Int(reach, "hy")) continue;
                var started = await Call("/item_use", new JsonObject { ["target_wx"] = tx, ["target_wy"] = ty, ["slot"] = slot, ["strict"] = true, ["duration_ticks"] = 0 }, token);
                if (Rejected(started)) continue;
                var result = await Wait("/item_use_status", "active", "/item_use_stop", 60, token);
                if (Text(result, "outcome") == "removed") removed++;
                if (Text(result, "reason") == "tool_weak") return new JsonObject { ["outcome"] = "tool_weak", ["tiles_removed"] = removed };
            }
        }
        if (removed >= wanted) outcome = "enough";
        return new JsonObject { ["outcome"] = outcome, ["tiles_removed"] = removed, ["wanted"] = wanted,
            ["note"] = "清除方块数不代表获得物品数，以 got 和背包实际数量为准。" };
    }

    static async Task<string> ReadWiki(string name, JsonObject args, CancellationToken token)
    {
        string query = name == "wiki_search"
            ? "action=query&list=search&srsearch=" + Uri.EscapeDataString(Text(args, "query")) + "&srlimit=5"
            : "action=parse&prop=text&disablelimitreport=1&redirects=1&page=" + Uri.EscapeDataString(Text(args, "title"));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://terraria.wiki.gg/api.php?format=json&" + query);
        request.Headers.UserAgent.ParseAdd("TerraBlind/1.0");
        using var response = await Wiki.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        string text = await response.Content.ReadAsStringAsync(token);
        var data = JsonNode.Parse(text);
        if (name == "wiki_search") return (data?["query"]?["search"] ?? data).ToJsonString();
        string html = data?["parse"]?["text"]?["*"]?.GetValue<string>();
        if (html == null) return new JsonObject { ["error"] = "wiki_not_found", ["title"] = Text(args, "title") }.ToJsonString();
        string plain = System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " "));
        return new JsonObject { ["title"] = Text(args, "title"), ["text"] = plain[..Math.Min(plain.Length, 6000)] }.ToJsonString();
    }
}
