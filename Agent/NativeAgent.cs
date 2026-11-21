using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraBlind;

public class NativeAgent : ModSystem
{
    const int CleanupFreeSlots = 3;
    const int LavaProtectionNoticeTicks = 45 * 60;
    static readonly ConcurrentQueue<Action> MainQueue = new();
    static readonly ConcurrentQueue<Action> FrameQueue = new();
    static readonly ConcurrentQueue<Action> WorldFrameQueue = new();
    static NativeAgent _instance;
    HttpClient _http;
    AgentConversation _conversation;
    CodexAgent _codex;
    CancellationTokenSource _cancel;
    CancellationTokenSource _scriptWarmup;
    Task _running;
    TaskCompletionSource<string> _answer;
    string _pending;
    string _system;
    JsonObject _goalOptions;
    Task _goalStopping = Task.CompletedTask;
    Task _dangerDelivery = Task.CompletedTask;
    Task _idleReaction = Task.CompletedTask;
    readonly ParallelAgentWork _parallel = new();
    Func<string, JsonObject, CancellationToken, Task<JsonObject>> _propose;
    ulong _inventoryReviewTick, _chestReviewRevision, _potionReviewTick, _preparationTick;
    JsonNode _lastAutomaticTrash;
    Action _finishPotion;
    internal static JsonArray ParallelReceipts => _instance?._parallel.Receipts() ?? new JsonArray();
    internal static Task IdleReaction => _instance?._idleReaction ?? Task.CompletedTask;
    int _lastDangerNotice;
    internal static JsonObject GoalState => _instance?._codex?.GoalState;
    DateTime _lastFeedback;
    DateTime _lastWatch;
    JsonArray _schemas;
    readonly ConcurrentQueue<string> _combatMessages = new();
    internal static bool CombatMessagesPending => _instance?._combatMessages.IsEmpty == false;
    internal static JsonArray TakeCombatMessages()
    {
        var messages = new JsonArray();
        if (_instance != null)
            while (_instance._combatMessages.TryDequeue(out string message)) messages.Add(message);
        return messages;
    }
    static string status = "idle";
    public static string Status
    {
        get
        {
            if (status != "waiting for decision") return status;
            string tool = AgentTools.ControlSession.PrimaryActive ? AgentTools.ControlSession.Tool : AgentTools.Actions.PrimaryTool;
            return tool == null ? status : "running " + tool;
        }
        private set => status = value;
    }
    internal static bool Processing => _instance?._running?.IsCompleted == false
        && _instance._cancel?.IsCancellationRequested == false && _instance._answer == null;
    internal static bool WorkPending => _instance?._pending != null || Processing;
    internal static bool AwaitingDecision => Processing && status is "waiting for model" or "waiting for decision" or "continuing goal" or "running script_events";
    internal static Func<TimeSpan> FrozenTime = () => TimeSpan.Zero;
    internal static int WorldWaiters;
    internal static JsonArray ObserveTiles(Player player)
    {
        var result = new JsonArray();
        var watch = AgentTools.Watches;
        if (!watch.Subscribed("nearby_tile")) return result;
        int px = (int)(player.Center.X / 16), py = (int)(player.Center.Y / 16), radius = watch.Radius;
        for (int x = Math.Max(0, px - radius); x <= Math.Min(Main.maxTilesX - 1, px + radius); x++)
            for (int y = Math.Max(0, py - radius); y <= Math.Min(Main.maxTilesY - 1, py + radius); y++)
            {
                int distance = Math.Abs(x - px) + Math.Abs(y - py);
                if (distance > radius) continue;
                var tile = Main.tile[x, y];
                if (!tile.HasTile || !watch.WatchesTile(tile.TileType)) continue;
                result.Add(new JsonObject { ["tile_type"] = (int)tile.TileType, ["x"] = x, ["y"] = y,
                    ["frame_x"] = (int)tile.TileFrameX, ["frame_y"] = (int)tile.TileFrameY,
                    ["distance_tiles_manhattan"] = distance, ["liquid_amount"] = (int)tile.LiquidAmount,
                    ["liquid_type"] = tile.LiquidType, ["route_assessed"] = false,
                    ["unit"] = "tile_cell", ["collection_verified"] = false });
            }
        return result;
    }

    public override void Load()
    {
        if (Main.dedServ) return;
        _instance = this;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        _conversation = new AgentConversation(_http);
        _system = Encoding.UTF8.GetString(Mod.GetFileBytes("Agent/System.txt"));
        _schemas = JsonNode.Parse(Encoding.UTF8.GetString(Mod.GetFileBytes("Agent/Tools.json"))).AsArray();
        AgentControlSession.DescribeClaims(_schemas);
        AgentScript.DescribePrograms(_schemas);
    }

    public static void Submit(string text)
    {
        if (!AgentNetwork.CanAutomate) { AgentChat.Say("Use /ai <player> task <instruction> to contact the AI player."); return; }
        if (!Config.I.AgentEnabled) { AgentChat.Say("The built-in agent is disabled."); return; }
        var self = _instance;
        if (self == null) return;
        if (string.IsNullOrWhiteSpace(text)) { AgentChat.Say("Enter an instruction."); return; }
        if (text.Trim() is "stop" or "停止") { self.Cancel(); AgentChat.Say("Stopped."); return; }
        if (text.Trim() == "status") { AgentChat.Say(Status); return; }
        bool goalCommand = text.StartsWith("/goal ", StringComparison.Ordinal) || text.StartsWith("goal ", StringComparison.Ordinal);
        if (goalCommand)
        {
            string objective = text[(text.IndexOf(' ') + 1)..].Trim();
            if (objective == "status") { AgentChat.Say(GoalState?.ToJsonString() ?? "No Codex goal."); return; }
            if (objective is "pause" or "stop") { self.Cancel(); AgentChat.Say("Goal paused. Progress saved."); return; }
            if (!Config.I.AgentUseCodex) { AgentChat.Say("/goal requires the Codex backend."); return; }
            if (objective == "resume")
            {
                self._goalOptions ??= ReadSavedGoal();
                if (self._goalOptions == null) { AgentChat.Say("There is no goal to resume."); return; }
                if (GoalState?["native"]?["status"]?.ToString() is "budgetLimited" or "usageLimited" or "complete")
                { AgentChat.Say("The goal is complete or has reached its limit. Start a new goal with a new budget."); return; }
                self.Cancel();
                self._goalOptions["request"] = Guid.NewGuid().ToString("N");
                self._goalOptions["resume_previous"] = true;
                self._pending = self._goalOptions["objective"].ToString(); Status = "queued"; return;
            }
            long? budget = null;
            if (objective.StartsWith("--tokens ", StringComparison.Ordinal))
            {
                var parts = objective.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 3 || !long.TryParse(parts[1], out long amount) || amount <= 0)
                { AgentChat.Say("Usage: /tb /goal --tokens <positive count> <objective>"); return; }
                budget = amount; objective = parts[2];
            }
            if (string.IsNullOrWhiteSpace(objective)) { AgentChat.Say("Enter a goal."); return; }
            self.Cancel();
            self._goalOptions = new JsonObject { ["objective"] = objective, ["request"] = Guid.NewGuid().ToString("N") };
            if (budget.HasValue) self._goalOptions["tokenBudget"] = budget.Value;
            self._pending = objective; Status = "queued";
            Progress("Goal accepted.", "queued"); return;
        }
        if (self._answer != null)
        {
            self._answer.TrySetResult(text); self._answer = null;
            Status = "running"; return;
        }
        if (PvpDuel.Active && self._running?.IsCompleted == false)
        {
            self._combatMessages.Enqueue(text);
            Progress("Combat instruction queued. Use /tb stop to stop.", "combat instruction queued");
            return;
        }
        self.Cancel();
        self._goalOptions = null;
        self._pending = text;
        Status = "queued";
        Progress("Instruction accepted.", "queued");
    }

    static string SessionDirectory()
    {
        if (Main.netMode != Terraria.ID.NetmodeID.SinglePlayer || string.IsNullOrEmpty(Main.ActivePlayerFileData?.Path)) return null;
        string world = Main.ActiveWorldFileData is { UniqueId: var id } && id != Guid.Empty ? id.ToString("N") : Main.worldID.ToString();
        string player = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(Main.ActivePlayerFileData.Path)));
        return Path.Combine(Main.SavePath, "TerraBlindAgent", "conversations", world, player);
    }

    static JsonObject ReadSavedGoal()
    {
        string directory = SessionDirectory();
        if (directory == null) return null;
        string path = Path.Combine(directory, "goal.json");
        try { return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null; }
        catch (Exception error) when (error is IOException or System.Text.Json.JsonException)
        { DiagLog.Write("[agent] saved goal unreadable: " + error.Message); return null; }
    }

    internal static void Progress(string text, string status, bool visible = true)
    {
        Status = status;
        if (_instance != null) _instance._lastFeedback = DateTime.UtcNow;
        DiagLog.Write("[agent] " + status + " " + text);
        if (visible) AgentChat.Say(text);
    }

    void Cancel()
    {
        _pending = null;
        bool stopActions = _running != null && !_running.IsCompleted && _cancel?.IsCancellationRequested == false;
        _cancel?.Cancel();
        if (_finishPotion != null && ItemUseCoordinator.IsActive) ItemUseCoordinator.Stop();
        _finishPotion?.Invoke();
        _finishPotion = null;
        _parallel.Reset();
        _propose = null;
        if (_goalStopping.IsCompleted && _codex?.GoalState?["native"]?["status"]?.ToString() == "active")
        {
            var codex = _codex;
            _goalStopping = Task.Run(async () =>
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await codex.SuspendGoal("paused", timeout.Token); }
                catch (Exception error) { DiagLog.Write("[agent] goal pause failed: " + error.Message); }
            });
        }
        if (stopActions || PvpDuel.Active || AgentTools.Actions.HasActive || AgentTools.HasBossControlOverride) AgentTools.Stop();
        while (_combatMessages.TryDequeue(out _)) { }
        _answer?.TrySetCanceled(); _answer = null;
        Status = "idle";
    }

    public static void Stop() => _instance?.Cancel();

    void CloseCodex()
    {
        var codex = _codex; _codex = null;
        if (codex == null) return;
        if (_goalStopping.IsCompleted) codex.Dispose();
        else _ = _goalStopping.ContinueWith(_ => codex.Dispose(), TaskScheduler.Default);
    }

    public override void PostUpdateEverything()
        => UpdateAgent(true);

    static readonly string[] proposalQueries = { "get_state", "game_catalog", "find_tiles", "find_descent", "early_descent", "inspect_terrain", "recipe", "npc_find" };
    const string SuccessorInventory = " For live inventory use tool('get_state',{fields:['inventory_counts','inventory_slots']}). "
        + "Read item counts as state.inventory_counts[String(itemType)] ?? 0, including ammo slots, and main inventory slots from state.inventory_slots. "
        + "Use the same field names in the generated script. Player positions from get_state are pixels; nav_to coordinates are tiles.";
    static readonly JsonObject proposalTool = JsonNode.Parse("""
        {"type":"function","function":{"name":"submit_proposal","description":"Return a proposal to the game host without executing it.",
         "parameters":{"type":"object","properties":{"proposal":{"type":"object"}},"required":["proposal"]}}}
        """).AsObject();

    async Task<JsonObject> RequestProposal(string role, JsonObject facts, string executable, string cwd, string model, CancellationToken token)
    {
        var tools = new JsonArray(proposalTool.DeepClone());
        bool successor = role is "successor" or "action_followup";
        if (!successor)
        {
            using var fast = CancellationTokenSource.CreateLinkedTokenSource(token);
            fast.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                var decision = await JudgeInventory(role, facts, async (request, ct) =>
                    JsonNode.Parse(await AgentTools.Execute("jev_judge", request, ct)).AsObject(), fast.Token);
                if (decision != null) return decision;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        }
        if (successor)
            foreach (var schema in _schemas.Where(s => proposalQueries.Contains(s["function"]?["name"]?.ToString())))
                tools.Add(schema.DeepClone());
        string instructions = role switch
        {
            "equip" => "Evaluate the carried equipment against the equipped gear, current goal and progress. Submit {inventory_slot,equipment_slot} for one clear upgrade, or {}. Use only the supplied slots. Prefer survivability and practical mobility.",
            "cleanup" => "Review disposal candidates in the context of the current goal and inventory. Submit {trash_slots:[...]} with up to three stacks safe to discard entirely. Consider replacements, recipes, rarity and utility. Return an empty list when uncertain. Only use slots in candidates.",
            "potions" => "Choose one carried potion to drink now or keep all. Consider the current activity, goal, health, mana, and active effects. Submit {potion_slot:number} or {}. Prefer potions that help with current or imminent work. Save short or scarce buffs for that work. Use special effects such as teleportation only when specifically useful for the objective.",
            "action_followup" => "While this action runs, prepare the next useful actions for this DAG node. Submit {script:string,on_failure:string}. Either field may be omitted when the existing plan covers it. The success script runs after a clean handoff. The failure script receives the previous action as `failure` and runs for a blocked route or exhausted source. Check live state and choose a different reachable source, a safe retreat, or another useful part of the node. For an overhead boulder, inspect terrain before choosing an escape with clear footing. Preserve all user constraints and safety checks. Write executable TerraBlind JavaScript with direct tool calls. Use read-only tools during preparation.",
            _ => "Prepare the supplied DAG node as an executable TerraBlind JavaScript script. The current action continues. Query only facts needed to prepare this node. Submit {script:string} or {} if the facts do not support an executable script. Use tool(name,args), verify actual node conditions with tool('task_step',{action:'verify'}), and return a concrete blocked reason when needed. Preserve the goal, dependencies and permissions. Do not change the plan or call tools that mutate the game."
        };
        if (successor) instructions += " Prepare for the supplied world_phase. Recheck the phase when the script runs. When the primary action depends on a later phase, include concrete, useful preparations with their own movement that can start in the current phase. Return {} if you have no such verified action."
            + " The host runs synchronous JavaScript. tool() and skill() return directly. Write plain statements without await or async wrappers."
            + SuccessorInventory;
        JsonObject proposal = null;
        int queryCount = 0;
        using var worker = new CodexAgent();
        await worker.RunAsync(facts.ToJsonString(AgentTools.ModelJson), executable, cwd, model, instructions, tools,
            async (name, args, ct) =>
            {
                if (name == "submit_proposal" && args["proposal"] is JsonObject value && value.ToJsonString().Length <= 12000)
                {
                    if (successor && CheckProposalScript(value) is JsonObject error) return error.ToJsonString();
                    proposal ??= (JsonObject)value.DeepClone();
                    return "{\"received\":true}";
                }
                if (!successor || !proposalQueries.Contains(name) || ++queryCount > 6)
                    return "{\"error\":\"proposal_query_unavailable\"}";
                return await AgentTools.Execute(name, args, ct);
            }, _ => { }, token, proposalAccepted: () => proposal != null);
        return proposal;
    }

    internal static JsonObject CheckProposalScript(JsonObject proposal)
    {
        foreach (string field in new[] { "script", "on_failure" })
        {
            if (proposal[field] == null) continue;
            if (proposal[field] is not JsonValue value || !value.TryGetValue<string>(out string code)
                || string.IsNullOrWhiteSpace(code) || code.Length > 10000)
                return new JsonObject { ["error"] = "invalid_proposal_script", ["field"] = field };
            try { new Acornima.Parser().ParseScript("(function(){\n" + code + "\n});"); }
            catch (Acornima.ParseErrorException error)
            {
                return new JsonObject { ["error"] = "invalid_proposal_script", ["field"] = field, ["detail"] = error.Message };
            }
            if (!AgentScript.ContainsInput(code))
                return new JsonObject { ["error"] = "proposal_has_no_action", ["field"] = field };
        }
        return null;
    }

    internal static async Task<JsonObject> JudgeInventory(string role, JsonObject facts,
        Func<JsonObject, CancellationToken, Task<JsonObject>> judge, CancellationToken token)
    {
        var bag = facts["bag"];
        var items = (bag?["inventory"] as JsonArray)?.OfType<JsonObject>().ToArray() ?? Array.Empty<JsonObject>();
        var questions = new JsonObject();
        var choices = new Dictionary<string, (int Source, int Equipment)>();
        void Ask(string key, string instruction, IEnumerable<(string Key, string Label)> options)
        {
            var criteria = new JsonObject { ["keep"] = "Keep current items and equipment; no clear improvement." };
            foreach (var (id, label) in options) criteria[id] = label;
            if (criteria.Count > 1) questions[key] = new JsonObject { ["type"] = "choice", ["instructions"] = instruction, ["criteria"] = criteria };
        }
        if (role == "equip")
        {
            var equipment = bag["equipment"].AsArray().OfType<JsonObject>()
                .Where(slot => slot["unlocked"]?.GetValue<bool>() == true && slot["favorited"]?.GetValue<bool>() != true).ToArray();
            var options = new List<(string, string)>();
            foreach (var item in items.Where(i => i["favorited"]?.GetValue<bool>() != true))
            {
                int armorSlot = ReadNumber(item["armor_slot"]), from = ReadNumber(item["slot"]);
                var targets = equipment.Where(slot => armorSlot >= 0 ? ReadNumber(slot["slot"]) == armorSlot
                    : item["accessory"]?.GetValue<bool>() == true && ReadNumber(slot["slot"]) is >= 3 and < 10).ToArray();
                var empty = targets.FirstOrDefault(slot => ReadNumber(slot["type"]) == 0);
                foreach (var target in (empty != null ? new[] { empty } : targets))
                {
                    if (JsonNode.DeepEquals(item["type"], target["type"]) && JsonNode.DeepEquals(item["prefix"], target["prefix"])) continue;
                    if (options.Count == 48) break;
                    int to = ReadNumber(target["slot"]);
                    string id = $"equip_{from}_{to}";
                    choices[id] = (from, to);
                    options.Add((id, $"Equip carried slot {from} ({item["name"]}), replacing slot {to} ({target["name"]})."));
                }
            }
            Ask("equip", "Which option clearly improves survivability or mobility for the current goal? Read the carried and equipped item facts; preserve unusual or goal-critical effects.", options);
        }
        else if (role == "potions")
        {
            Ask("potion", "Which potion should the player drink now? Choose keep if its effect will be more useful later. Use healing and mana potions when the restored amount is useful. Movement, mining, building, exploration and combat buffs should match the current work. For special effects, check the objective.",
                facts["potions"].AsArray().Select(item => ($"slot_{item["slot"]}",
                    $"Drink slot {item["slot"]}: {item["name"]} (effect {item["buff"]}, heal {item["heal_life"]}, mana {item["heal_mana"]}).")));
        }
        else
        {
            var slots = facts["candidates"].AsArray().Select(ReadNumber).ToHashSet();
            var candidates = items.Where(i => slots.Contains(ReadNumber(i["slot"]))).ToArray();
            foreach (var (part, index) in candidates.Chunk(16).Select((part, index) => (part, index)))
                Ask("trash_" + index, "Which single stack in these options is safe to discard entirely, given the goal, substitutes and the rest of the backpack? Preserve useful materials and rare items. Choose keep when uncertain.",
                    part.Select(item => ($"slot_{item["slot"]}", $"Discard slot {item["slot"]}: {item["stack"]} × {item["name"]}.")));
        }
        if (questions.Count == 0) return new JsonObject();
        var result = await judge(new JsonObject { ["state"] = facts.DeepClone(), ["questions"] = questions }, token);
        if (result?["answers"] is not JsonObject answers) return null;
        string Pick(JsonNode answer)
        {
            string choice = answer?["choice"]?.ToString();
            return choice != null && answer["probabilities"]?[choice] is JsonValue probability
                && probability.TryGetValue<double>(out double score) && score >= 0.9 ? choice : null;
        }
        if (role == "equip")
        {
            string choice = Pick(answers["equip"]);
            return choice == null ? null : choices.TryGetValue(choice, out var selected)
                ? new JsonObject { ["inventory_slot"] = selected.Source, ["equipment_slot"] = selected.Equipment } : new JsonObject();
        }
        if (role == "potions")
        {
            string choice = Pick(answers["potion"]);
            return choice == null ? null : choice.StartsWith("slot_", StringComparison.Ordinal)
                && questions["potion"]?["criteria"]?[choice] != null && int.TryParse(choice[5..], out int slot)
                ? new JsonObject { ["potion_slot"] = slot } : new JsonObject();
        }
        var discarded = new JsonArray();
        foreach (var (key, answer) in answers)
        {
            string choice = Pick(answer);
            if (choice != null && choice.StartsWith("slot_", StringComparison.Ordinal)
                && questions[key]?["criteria"]?[choice] != null && int.TryParse(choice[5..], out int slot)) discarded.Add(slot);
        }
        return discarded.Count > 0 ? new JsonObject { ["trash_slots"] = discarded }
            : answers.Any(pair => Pick(pair.Value) == null) ? null : new JsonObject();
    }

    void UpdateParallelWork()
    {
        if (_propose == null || !Processing || Manual.On || !Config.I.AgentEnabled || !AgentNetwork.CanAutomate
            || Main.LocalPlayer.dead || PvpDuel.Active || _codex?.HasActiveManagedGoal != true) return;
        var token = _cancel.Token;
        var propose = _propose;
        _parallel.Poll(token);
        ulong now = Main.GameUpdateCount;
        PrepareParallelWork(now, propose, token);
        if (now >= _potionReviewTick)
        {
            _potionReviewTick = now + 60;
            var p = Main.LocalPlayer;
            var potions = PotionCandidates(p);
            if (potions.Count > 0)
            {
                string potionNode = AgentTools.Actions.ActiveNode;
                string activity = AgentTools.ControlSession.Tool;
                var facts = new JsonObject { ["objective"] = _goalOptions?["objective"]?.DeepClone(),
                    ["step"] = AgentTools.Plan.CurrentDescription, ["activity"] = activity,
                    ["hp"] = p.statLife, ["max_hp"] = p.statLifeMax2, ["mana"] = p.statMana, ["max_mana"] = p.statManaMax2,
                    ["wet"] = p.wet, ["held_weapon"] = p.HeldItem.Name, ["held_mana_cost"] = p.HeldItem.mana,
                    ["mining"] = MineCoordinator.IsActive || p.itemAnimation > 0 && p.HeldItem.pick > 0,
                    ["placing"] = PlaceAnywhere.IsRunning || p.itemAnimation > 0 && p.HeldItem.createTile >= 0,
                    ["buffs"] = new JsonArray(p.buffType.Select((type, i) => (Type: type, Time: p.buffTime[i]))
                        .Where(buff => buff.Type > 0 && buff.Time > 0)
                        .Select(buff => (JsonNode)new JsonObject { ["buff"] = Lang.GetBuffName(buff.Type), ["ticks"] = buff.Time }).ToArray()),
                    ["potions"] = potions };
                string revision = potionNode + ":" + activity + ":" + p.statLife * 4 / Math.Max(1, p.statLifeMax2)
                    + ":" + p.statMana * 4 / Math.Max(1, p.statManaMax2) + ":" + p.wet + ":" + p.HeldItem.type + ":" + potions.ToJsonString();
                _parallel.Offer("inventory:potions", revision, ct => propose("potions", facts, ct),
                    DrinkPotion(potions, potionNode, activity, finish => _finishPotion = finish), token);
            }
        }
        ulong chest = AgentTaskEvidence.ChestRevision;
        int free = Main.LocalPlayer.inventory.Take(50).Count(i => i.IsAir);
        if (chest != _chestReviewRevision || now >= _inventoryReviewTick)
        {
            _chestReviewRevision = chest;
            _inventoryReviewTick = now + (free == 0 ? 120UL : 1800UL);
            var bag = PvpInventory.Review();
            string revision = bag.ToJsonString();
            var items = bag["inventory"].AsArray().OfType<JsonObject>();
            var equipment = bag["equipment"].AsArray().OfType<JsonObject>();
            var scene = new JsonObject { ["objective"] = _goalOptions?["objective"]?.DeepClone(),
                ["step"] = AgentTools.Plan.CurrentDescription, ["bag"] = bag,
                ["hardmode"] = Main.hardMode, ["life"] = Main.LocalPlayer.statLifeMax2 };
            if (items.Any(i => i["accessory"]?.GetValue<bool>() == true || i["armor_slot"] != null)
                && equipment.Any(i => i["unlocked"]?.GetValue<bool>() == true))
                _parallel.Offer("inventory:equip", revision, ct => propose("equip", scene, ct), answer => ApplyEquipment(bag, answer), token);
            if (free <= CleanupFreeSlots)
            {
                var protectedTypes = ProtectedItems(AgentTools.Plan.Snapshot());
                var candidates = items.Where(i => DiscardCandidate(i, protectedTypes)).ToArray();
                if (candidates.Length > 0)
                {
                    var cleanup = (JsonObject)scene.DeepClone();
                    cleanup["candidates"] = new JsonArray(candidates.Select(i => i["slot"].DeepClone()).ToArray());
                    _parallel.Offer("inventory:cleanup", revision, ct => propose("cleanup", cleanup, ct),
                        answer => ApplyCleanup(bag, candidates, answer), token);
                }
            }
        }
    }

    void PrepareParallelWork(ulong now, Func<string, JsonObject, CancellationToken, Task<JsonObject>> propose, CancellationToken token)
    {
        if (now < _preparationTick || !AgentTools.Plan.RequiresNodeExecution || !AgentTools.ControlSession.PrimaryActive) return;
        var player = Main.LocalPlayer;
        if (Math.Abs(player.velocity.X) + Math.Abs(player.velocity.Y) < .15f) return;
        _preparationTick = now + 15;
        string owner = AgentTools.Actions.ActiveNode;
        var opportunities = AgentTools.Plan.PreparationNodes(owner, AgentTaskEvidence.Measure, prefetchFollowing: true);
        if (opportunities.Count == 0 && owner == null) return;
        var graph = AgentTools.Plan.Snapshot();
        var ready = graph["ready_steps"].AsArray().Select(step => step.ToString()).ToHashSet();
        OfferActionFollowup(_parallel, graph, _goalOptions?["objective"], propose, token);
        foreach (var node in opportunities)
        {
            string id = node.GetValue<string>();
            if (id == owner) continue;
            var step = graph["steps"].AsArray().OfType<JsonObject>().First(s => s["id"].ToString() == id);
            string stamp = step["workflow"] + ":" + AgentTools.Plan.ExecutionRevision(id);
            bool dependenciesVerified = ready.Contains(id);
            string offer = stamp + (dependenciesVerified ? ":ready" : ":waiting_dependencies");
            if (!_parallel.CanOffer("dag:" + id, offer)) continue;
            var facts = new JsonObject { ["goal"] = graph["goal"]?.DeepClone(), ["active_node"] = owner,
                ["node"] = step.DeepClone(), ["inventory"] = PvpInventory.Snapshot(),
                ["dependencies_verified"] = dependenciesVerified, ["world_phase"] = Main.dayTime ? "day" : "night",
                ["position_tiles"] = new JsonArray(Main.LocalPlayer.Center.X / 16, Main.LocalPlayer.Center.Y / 16),
                ["tools"] = "Use find_tiles and other read-only tools now. Scripts can use nav_to, gather, collect_treasure, build_house, craft_to_slot, equip_item, task_step and existing skills with verified arguments." };
            _parallel.Offer("dag:" + id, offer, ct => propose("successor", facts, ct),
                answer => ApplySuccessor(id, stamp, answer), token);
            DiagLog.Write($"[decision_prefetch] role=successor node={id} tick={now} active_node={owner}" +
                $" movement={(Math.Abs(player.velocity.X) + Math.Abs(player.velocity.Y)):0.00}");
        }
    }

    internal static void OfferActionFollowup(ParallelAgentWork jobs, JsonObject graph, JsonNode objective,
        Func<string, JsonObject, CancellationToken, Task<JsonObject>> propose, CancellationToken token)
    {
        if (AgentTools.Actions.HasQueuedSuccessor) return;
        var running = AgentTools.Actions.RunningPrimary();
        string owner = running?["task_node"]?.ToString();
        if (string.IsNullOrEmpty(owner)) return;
        var steps = graph["steps"].AsArray();
        var step = steps.FirstOrDefault(n => n["id"].ToString() == owner);
        if (step == null || step["status"]?.ToString() == "completed") return;
        string actionKey = AgentTools.FollowupKey(running), version = AgentTools.StepVersion(step);
        const string key = "dag:action_followup";
        if (!jobs.CanOffer(key, actionKey + ":" + version)) return;
        AgentTools.Plan.Observe(AgentTaskEvidence.Measure);
        step = AgentTools.Plan.Snapshot()["steps"].AsArray().First(n => n["id"].ToString() == owner);
        running["live_tool"] = AgentTools.ControlSession.Tool;
        var facts = new JsonObject { ["goal"] = graph["goal"]?.DeepClone(), ["objective"] = objective?.DeepClone(),
            ["node"] = step.DeepClone(), ["active_action"] = running, ["inventory"] = PvpInventory.Snapshot(),
            ["world_phase"] = Main.dayTime ? "day" : "night",
            ["downstream"] = new JsonArray(steps.Where(n => n["depends_on"].AsArray().Any(id => id.ToString() == owner))
                .Select(n => new JsonObject { ["id"] = n["id"].DeepClone(), ["description"] = n["description"].DeepClone() }).ToArray<JsonNode>()),
            ["position_tiles"] = new JsonArray(Main.LocalPlayer.Center.X / 16, Main.LocalPlayer.Center.Y / 16),
            ["tools"] = "Use action_query and get_state inside the script to check current results. Scripts can use nav_to, gather, collect_treasure, build_house, craft_to_slot, equip_item, task_step and existing skills with verified arguments." };
        jobs.Offer(key, actionKey + ":" + version, ct => propose("action_followup", facts, ct),
            answer => AgentTools.StageActionFollowup(actionKey, owner, version, answer), token);
        var p = Main.LocalPlayer;
        DiagLog.Write($"[decision_prefetch] role=action_followup node={owner} tick={Main.GameUpdateCount} handle={running["handle"]}" +
            $" movement={(Math.Abs(p.velocity.X) + Math.Abs(p.velocity.Y)):0.00}");
    }

    static HashSet<int> ProtectedItems(JsonObject plan)
    {
        var steps = plan["steps"].AsArray().OfType<JsonObject>().ToArray();
        var pending = steps.Where(step => step["status"]?.ToString() != "completed").ToArray();
        if (pending.Length == 0) return new HashSet<int>();
        return steps.SelectMany(step => step["conditions"].AsArray().OfType<JsonObject>())
            .Where(condition => condition["kind"]?.ToString() is "item" or "equipped")
            .Select(condition => condition["type"].GetValue<int>())
            .Concat(pending.SelectMany(step => AgentTools.MaterialsFor(step["id"].ToString()))).ToHashSet();
    }

    static int ReadNumber(JsonNode value) => value is JsonValue v && v.TryGetValue<int>(out int number) ? number : -1;

    internal static JsonObject LavaProtectionWarning(Player p)
    {
        if (p.dead) return null;
        int buff = p.FindBuffIndex(BuffID.ObsidianSkin);
        if (buff < 0 || p.buffTime[buff] is <= 0 or > LavaProtectionNoticeTicks) return null;
        const int margin = 8 * 16;
        if (!p.lavaWet && !Collision.LavaCollision(new Microsoft.Xna.Framework.Vector2(p.position.X - margin, p.position.Y - margin),
            p.width + 2 * margin, p.height + 2 * margin)) return null;
        return new JsonObject { ["expires_in_ticks"] = p.buffTime[buff], ["in_lava"] = p.lavaWet,
            ["x"] = (int)p.Center.X / 16, ["y"] = (int)p.Center.Y / 16,
            ["replacement_available"] = p.inventory.Take(50).Any(i => !i.IsAir && i.type == ItemID.ObsidianSkinPotion) };
    }

    static bool DiscardCandidate(JsonObject item, HashSet<int> protectedTypes)
        => ReadNumber(item["slot"]) >= 10 && item["favorited"]?.GetValue<bool>() != true && item["accessory"]?.GetValue<bool>() != true
            && item["armor_slot"] == null && ReadNumber(item["heal_life"]) <= 0
            && ReadNumber(item["damage"]) <= 0 && ReadNumber(item["pick"]) <= 0 && ReadNumber(item["axe"]) <= 0 && ReadNumber(item["hammer"]) <= 0
            && ReadNumber(item["create_tile"]) is not (TileID.Painting3X3 or TileID.Painting4X3 or TileID.Painting6X4 or TileID.Painting2X3 or TileID.Painting3X2)
            && !protectedTypes.Contains(ReadNumber(item["type"])) && ReadNumber(item["type"]) is not (>= 71 and <= 74)
            && !IsPotion(Main.LocalPlayer.inventory[ReadNumber(item["slot"])]);

    static bool IsPotion(Item item) => !item.IsAir && item.consumable
        && (item.potion || item.healLife > 0 || item.healMana > 0 || item.buffType > 0
            || item.useStyle == ItemUseStyleID.DrinkLiquid || AgentTools.IsRecallItem(item.type));

    static bool CanDrink(Player p, Item item) => IsPotion(item) && (!item.potion || p.potionDelay == 0)
        && (item.buffType <= 0 || !p.HasBuff(item.buffType))
        && (item.healLife <= 0 && item.healMana <= 0 || item.healLife > 0 && p.statLife < p.statLifeMax2
            || item.healMana > 0 && p.statMana < p.statManaMax2)
        && (AgentTools.AllowTaskRecall || !AgentTools.IsRecallItem(item.type));

    internal static JsonArray PotionCandidates(Player p) => new(Enumerable.Range(0, 50)
        .Where(slot => CanDrink(p, p.inventory[slot]))
        .GroupBy(slot => p.inventory[slot].type)
        .Select(group =>
        {
            int slot = group.First();
            var item = p.inventory[slot];
            return (JsonNode)new JsonObject { ["slot"] = slot, ["type"] = item.type, ["prefix"] = item.prefix,
                ["name"] = item.Name, ["count"] = group.Sum(s => p.inventory[s].stack),
                ["heal_life"] = item.healLife, ["heal_mana"] = item.healMana,
                ["buff"] = item.buffType > 0 ? Lang.GetBuffName(item.buffType) : null, ["buff_ticks"] = item.buffTime };
        }).ToArray());

    internal static Func<JsonObject, JsonObject> DrinkPotion(JsonArray candidates, string owner, string activity, Action<Action> setFinish = null)
    {
        int source = -1, hotbar = -1, selected = -1, type = 0, count = 0, buff = 0;
        Item displaced = null;
        void Restore(Player player)
        {
            var held = player.inventory[hotbar];
            if (displaced != null && ReferenceEquals(player.inventory[source], displaced) && (held.IsAir || held.type == type))
                PvpInventory.SwapSlots(hotbar, source);
            if (player.selectedItem == hotbar) player.selectedItem = selected;
        }
        return answer =>
        {
            var p = Main.LocalPlayer;
            if (hotbar >= 0)
            {
                if (ItemUseCoordinator.IsActive || p.itemAnimation > 0 || p.itemTime > 0) return null;
                var held = p.inventory[hotbar];
                bool consumed = held.IsAir || held.type == type && held.stack < count;
                Restore(p);
                setFinish?.Invoke(null);
                bool activeBuff = buff > 0 && p.HasBuff(buff);
                return new JsonObject { ["potion_type"] = type, ["consumed"] = consumed, ["buff_active"] = activeBuff,
                    ["outcome"] = consumed && (buff <= 0 || activeBuff) ? "used" : "potion_use_unconfirmed" };
            }
            source = ReadNumber(answer?["potion_slot"]);
            var expected = candidates.FirstOrDefault(item => ReadNumber(item?["slot"]) == source);
            if (expected == null || source is < 0 or >= 50 || AgentTools.Actions.ActiveNode != owner
                || AgentTools.ControlSession.Tool != activity || p.dead) return new JsonObject();
            var potion = p.inventory[source];
            if (potion.type != ReadNumber(expected["type"]) || potion.prefix != ReadNumber(expected["prefix"]) || !CanDrink(p, potion))
                return new JsonObject();
            if (InventoryBusy() || MineCoordinator.IsActive) return null;
            selected = p.selectedItem;
            hotbar = source < 10 ? source : Enumerable.Range(0, 10).FirstOrDefault(i => p.inventory[i].IsAir, selected);
            if (hotbar is < 0 or >= 10) { hotbar = -1; return new JsonObject(); }
            type = potion.type; count = potion.stack; buff = potion.buffType;
            if (source != hotbar)
            {
                var swap = PvpInventory.SwapSlots(source, hotbar);
                if (swap["ok"]?.GetValue<bool>() != true) { hotbar = -1; return new JsonObject(); }
                displaced = p.inventory[source];
            }
            ItemUseCoordinator.Start(new ItemUseRequest { Slot = hotbar, TargetWx = (int)(p.Center.X / 16),
                TargetWy = (int)(p.Center.Y / 16), DurationTicks = Math.Max(24, potion.useAnimation + 6) });
            setFinish?.Invoke(() => Restore(p));
            return null;
        };
    }

    static bool InventoryBusy()
    {
        var p = Main.LocalPlayer;
        return p.itemAnimation != 0 || p.itemTime != 0 || p.chest != -1 || !Main.mouseItem.IsAir
            || AgentTools.ContactEscapeActive || ItemUseCoordinator.IsActive || PlaceAnywhere.IsRunning
            || StateSpacePlanner.ReservesItemUse || !AgentTools.ControlAllows(ControlBehavior.Workflow, ControlChannels.ItemUse, delegated: true)
            || AgentTools.ControlSession.Tool is "act" or "mine" or "mine_vein" or "use_item" or "build_house" or "build_bridge" or "place_at" or "craft_recipe" or "slot_click" or "npc_chat";
    }

    internal static JsonObject ApplyEquipment(JsonObject bag, JsonObject answer)
    {
        int source = ReadNumber(answer?["inventory_slot"]), target = ReadNumber(answer?["equipment_slot"]);
        if (source is < 0 or >= 50 || target is < 0 or >= 10) return new JsonObject();
        if (InventoryBusy()) return null;
        var item = bag["inventory"].AsArray().FirstOrDefault(i => ReadNumber(i["slot"]) == source);
        var equipped = bag["equipment"].AsArray().FirstOrDefault(i => ReadNumber(i["slot"]) == target);
        var p = Main.LocalPlayer;
        if (item == null || equipped == null || p.inventory[source].favorited || p.armor[target].favorited || source == p.selectedItem)
            return new JsonObject();
        var result = Equipment.Swap(new JsonObject { ["inventory_slot"] = source, ["equipment_slot"] = target,
            ["expected_inventory"] = item.DeepClone(), ["expected_equipment"] = equipped.DeepClone() });
        return result["ok"]?.GetValue<bool>() == true ? new JsonObject { ["equipped"] = result["after"]["equipment"]["name"]?.DeepClone() }
            : new JsonObject();
    }

    internal JsonObject ApplyCleanup(JsonObject bag, JsonObject[] candidates, JsonObject answer)
    {
        if (answer?["trash_slots"] is not JsonArray slots || slots.Count == 0) return new JsonObject();
        if (InventoryBusy()) return null;
        var now = PvpInventory.Review();
        if (now["empty_slots"].AsArray().Count > CleanupFreeSlots) return new JsonObject();
        var trash = now["trash"];
        if (!JsonNode.DeepEquals(trash, bag["trash"]) || ReadNumber(trash["type"]) > 0 && !JsonNode.DeepEquals(trash, _lastAutomaticTrash))
            return new JsonObject();
        var protectedTypes = ProtectedItems(AgentTools.Plan.Snapshot());
        var removed = new JsonArray();
        foreach (int slot in slots.Select(ReadNumber).Distinct().Take(3))
        {
            var original = candidates.FirstOrDefault(i => ReadNumber(i["slot"]) == slot);
            var fresh = now["inventory"].AsArray().OfType<JsonObject>().FirstOrDefault(i => ReadNumber(i["slot"]) == slot);
            if (original == null || fresh == null || !DiscardCandidate(fresh, protectedTypes)) continue;
            var result = PvpInventory.Trash(new JsonObject { ["slot"] = slot,
                ["expected"] = original.DeepClone(), ["expected_trash"] = trash.DeepClone() });
            if (result["ok"]?.GetValue<bool>() != true) continue;
            _lastAutomaticTrash = trash = result["trash"].DeepClone();
            removed.Add(new JsonObject { ["name"] = original["name"]?.DeepClone(), ["stack"] = original["stack"].DeepClone() });
        }
        return removed.Count > 0 ? new JsonObject { ["trashed"] = removed, ["slots_freed"] = removed.Count } : new JsonObject();
    }

    internal static JsonObject ApplySuccessor(string id, string stamp, JsonObject answer)
    {
        if (answer?["script"] is not JsonValue value || !value.TryGetValue<string>(out var code)
            || string.IsNullOrWhiteSpace(code) || code.Length > 10000 || AgentScript.PreparationOnly(code)) return new JsonObject();
        var plan = AgentTools.Plan.Snapshot();
        var step = plan["steps"].AsArray().OfType<JsonObject>().FirstOrDefault(s => s["id"].ToString() == id);
        if (step == null || step["status"]?.ToString() == "completed" || step["preparation_only"]?.GetValue<bool>() != true
            || stamp != step["workflow"] + ":" + AgentTools.Plan.ExecutionRevision(id)) return new JsonObject();
        new Acornima.Parser().ParseScript("(function(){\n" + code + "\n});");
        AgentTools.Plan.Revise(new JsonObject { ["expected_revision"] = plan["revision"].DeepClone(),
            ["reason"] = "Prepared by parallel game agent", ["nodes"] = new JsonArray(new JsonObject { ["id"] = id, ["script"] = code }) }, AgentTaskEvidence.Measure);
        return new JsonObject { ["prepared_node"] = id };
    }

    internal static JsonObject UpcomingBoss(Player player)
    {
        if (Main.dayTime || Main.netMode == Terraria.ID.NetmodeID.MultiplayerClient) return null;
        int type = WorldGen.spawnEye ? Terraria.ID.NPCID.EyeofCthulhu : WorldGen.spawnHardBoss switch
        {
            1 => Terraria.ID.NPCID.TheDestroyer,
            2 => Terraria.ID.NPCID.Retinazer,
            3 => Terraria.ID.NPCID.SkeletronPrime,
            _ => 0
        };
        if (type == 0) return null;
        return new JsonObject { ["type"] = type, ["boss"] = WorldGen.spawnHardBoss == 2 && !WorldGen.spawnEye ? "The Twins" : Lang.GetNPCNameValue(type),
            ["surface_spawn_eligible"] = player.position.Y < Main.worldSurface * 16 || Main.spawnTileY > Main.worldSurface,
            ["game_ticks_until_earliest_spawn"] = Math.Max(0, 4861 - (int)Main.time),
            ["x"] = player.Center.X / 16, ["y"] = player.Center.Y / 16,
            ["hp"] = player.statLife, ["max_hp"] = player.statLifeMax2, ["held_item_type"] = player.HeldItem.type,
            ["mana"] = player.statMana };
    }

    public override void UpdateUI(Microsoft.Xna.Framework.GameTime gameTime)
    {
        if (Main.gamePaused) UpdateAgent(false);
    }

    void UpdateAgent(bool worldAdvanced)
    {
        if (Main.dedServ) return;
        if (!Config.I.AgentEnabled || !AgentNetwork.CanAutomate)
        {
            Cancel();
            CloseCodex();
            return;
        }
        int frameWaiters = FrameQueue.Count;
        for (int i = 0; i < frameWaiters && FrameQueue.TryDequeue(out var waiter); i++) waiter();
        if (worldAdvanced)
        {
            int worldWaiters = WorldFrameQueue.Count;
            for (int i = 0; i < worldWaiters && WorldFrameQueue.TryDequeue(out var waiter); i++) waiter();
        }
        while (MainQueue.TryDequeue(out var action)) action();
        bool runtimeActive = _cancel?.IsCancellationRequested == false
            && Status is not ("needs_repair" or "budgetLimited" or "failed");
        if (!Main.gameMenu && runtimeActive)
        {
            AgentTools.ContinuePreparedNode(_cancel.Token);
            AgentTools.ObserveContinuity();
            if (worldAdvanced)
            {
                AgentTools.ObserveBossControl(Processing);
                if (GoalState?["native"]?["status"]?.ToString() is string goalStatus ? goalStatus == "active" : Processing)
                    AgentTools.ContinueIdleAction(_cancel.Token);
                AgentTools.LootPassingChest();
                UpdateParallelWork();
            }
        }
        if (worldAdvanced && !Main.gameMenu && (AgentTools.Watches.LocalDelivery || AgentTools.Watches.HasScriptHandlers
                || runtimeActive)
            && AgentTools.Watches.Armed && DateTime.UtcNow - _lastWatch >= TimeSpan.FromMilliseconds(250))
        {
            _lastWatch = DateTime.UtcNow;
            var p = Main.LocalPlayer;
            var chests = new JsonArray();
            foreach (var chest in Main.chest)
                if (chest != null && chest.item.Any(i => !i.IsAir)
                    && Math.Abs(chest.x - p.Center.X / 16) + Math.Abs(chest.y - p.Center.Y / 16) <= AgentTools.Watches.Radius)
                    chests.Add(new JsonObject { ["x"] = chest.x, ["y"] = chest.y });
            AgentTools.Watches.Observe(new JsonObject { ["hp"] = p.statLife, ["max_hp"] = p.statLifeMax2,
                ["dead"] = p.dead, ["suffocating"] = p.suffocating,
                ["invasion"] = AgentTools.InvasionStatus(targets: false),
                ["upcoming_boss"] = UpcomingBoss(p),
                ["lava_protection_warning"] = LavaProtectionWarning(p),
                ["ammo_supply"] = !p.dead && AgentPermissions.CanAttack(delegated: true) ? CombatLoadout.AmmoSupply(p) : null,
                ["tick"] = (long)Main.GameUpdateCount, ["honey_slowed"] = p.honeyWet && !p.ignoreWater,
                ["last_damage"] = AgentTools.LastDamage?.DeepClone(), ["last_death"] = AgentTools.LastDeath?.DeepClone(),
                ["item_counts"] = new JsonObject(p.inventory.Where(item => !item.IsAir).GroupBy(item => item.type)
                    .Select(group => new System.Collections.Generic.KeyValuePair<string, JsonNode>(group.Key.ToString(), JsonValue.Create(group.Sum(item => item.stack))))),
                ["pos"] = new JsonObject { ["x"] = p.position.X, ["y"] = p.position.Y },
                ["inventory_full"] = !p.inventory.Take(50).Any(i => i.IsAir), ["chests"] = chests,
                ["bosses"] = new JsonArray(Main.npc.Where(n => n != null && n.active && n.boss && n.life > 0
                    && Math.Abs(n.Center.X - p.Center.X) + Math.Abs(n.Center.Y - p.Center.Y) <= 200 * 16)
                    .Select(n => (JsonNode)new JsonObject { ["who"] = n.whoAmI, ["type"] = n.type,
                        ["name"] = Terraria.Lang.GetNPCNameValue(n.type), ["hp"] = n.life, ["max_hp"] = n.lifeMax,
                        ["x"] = n.Center.X / 16f, ["y"] = n.Center.Y / 16f, ["tick"] = Main.GameUpdateCount }).ToArray()),
                ["tiles"] = ObserveTiles(p),
                ["drops"] = AgentTools.Watches.Subscribed("nearby_drop") ? NearbyDrops.Read(AgentTools.Watches.Radius) : null,
                ["actions"] = AgentTools.Actions.Summary() });
            if (AgentTools.Watches.Samples % 20 == 0)
                DiagLog.Write("[agent_observe] " + AgentTools.Watches.LastObservation.ToJsonString());
            if (_codex != null && Processing && _dangerDelivery.IsCompleted)
            {
                var danger = AgentTools.Watches.Pending()["events"].AsArray().LastOrDefault(e =>
                    e["id"].GetValue<int>() > _lastDangerNotice
                    && (e["kind"].ToString() is "low_health" or "honey_slowdown" or "invasion_started" or "lava_protection_expiring" or "ammo_low" || !AgentTools.Watches.LocalDelivery
                        && e["kind"].ToString() is "suffocating" or "player_died" or "boss_appeared"));
                if (danger != null)
                {
                    _lastDangerNotice = danger["id"].GetValue<int>();
                    var observation = new JsonObject { ["event_id"] = _lastDangerNotice, ["kind"] = danger["kind"].DeepClone(),
                        ["hp"] = p.statLife, ["max_hp"] = p.statLifeMax2, ["dead"] = p.dead,
                        ["potion_delay_ticks"] = p.potionDelay, ["active_handle"] = AgentTools.Actions.ActiveHandle,
                        ["last_damage"] = AgentTools.LastDamage?.DeepClone() };
                    bool bossAppeared = danger["kind"].ToString() == "boss_appeared";
                    bool lavaExpiring = danger["kind"].ToString() == "lava_protection_expiring";
                    bool ammoLow = danger["kind"].ToString() == "ammo_low";
                    if (bossAppeared) observation["boss"] = danger["evidence"]?.DeepClone();
                    if (lavaExpiring) observation["lava_protection"] = danger["evidence"]?.DeepClone();
                    if (ammoLow) observation["ammo_supply"] = danger["evidence"]?.DeepClone();
                    if (danger["kind"].ToString() == "honey_slowdown") observation["honey_slowed"] = true;
                    string instruction = bossAppeared
                        ? "宿主观察到附近Boss，原目标及进度保留。依据任务、战斗许可与现场，决定交接战斗、调整路线或继续当前行动。调整前核对当前动作句柄。现场："
                        : lavaExpiring ? "Obsidian Skin is expiring near lava. Reach dry footing or renew protection before it expires. Keep the goal and verified progress. Live evidence: "
                        : ammoLow ? "Compatible ammunition is low. Keep the current action running. Prepare a craft or purchase with verified inputs and a reachable station or merchant, or use an approved backup. Live evidence: "
                        : "宿主实时危险观察，原目标和进度保留。低血量仅通知，当前动作继续；不得单凭低血量暂停、取消、原地回血或等待审阅。仅任务明确约束死亡时按该约束处理。请在行动期间调整后续方案：";
                    _dangerDelivery = _codex.Steer(instruction + observation.ToJsonString(AgentTools.ModelJson), _cancel.Token);
                }
                else _dangerDelivery = AgentTools.NotifyExecution(_codex, _cancel.Token);
            }
        }
        if (!Main.gameMenu && runtimeActive && AgentTools.Watches.HasScriptHandlers && _idleReaction.IsCompleted)
            _idleReaction = AgentTools.ReactWhileIdle(_cancel.Token);
        if (Status == "background action running" && !AgentTools.Actions.HasActive) Status = "idle";
        if (worldAdvanced && PvpDuel.Active) PvpDuel.Update(Main.LocalPlayer);
        if (_running != null && !_running.IsCompleted && _cancel?.IsCancellationRequested == false
            && _answer == null && DateTime.UtcNow - _lastFeedback >= TimeSpan.FromSeconds(30))
            Progress("Still working: " + Status + ". Use /tb stop to stop.", Status, visible: false);
        if (!Main.gameMenu && Main.LocalPlayer.active && Main.LocalPlayer.dead)
        {
            return;
        }
        if (_pending == null || _running != null && !_running.IsCompleted) return;
        if (Main.gameMenu || !Main.LocalPlayer.active) return;
        string goal = _pending; _pending = null;
        string key = Config.I.AgentApiKey?.Trim();
        string model = Config.I.AgentModel?.Trim();
        string url = Config.I.AgentBaseUrl?.Trim();
        bool useCodex = Config.I.AgentUseCodex;
        string codexPath = Config.I.AgentCodexPath;
        string codexModel = Config.I.AgentCodexModel?.Trim();
        if (string.IsNullOrEmpty(codexModel)) codexModel = "gpt-6-luna";
        string cwd = System.IO.Path.Combine(Main.SavePath, "TerraBlindAgent");
        var goalOptions = _goalOptions?.DeepClone() as JsonObject;
        if (!useCodex && (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(model) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
        { Progress("Set the Agent API key, base URL, and model in mod settings.", "failed"); return; }
        if (!useCodex) CloseCodex();
        var codex = useCodex ? _codex ??= new CodexAgent() : null;
        if (codex != null)
        {
            codex.SessionDirectory = SessionDirectory();
            if (goalOptions != null && codex.SessionDirectory != null)
            {
                try { CodexAgent.WriteSessionFile(Path.Combine(codex.SessionDirectory, "goal.json"), goalOptions.ToJsonString()); }
                catch (IOException error) { DiagLog.Write("[agent] could not save goal: " + error.Message); }
            }
        }
        _cancel?.Dispose();
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        AgentTools.NewTask(goal, allowPassingChestLoot: goalOptions != null);
        AgentTools.Plan.SetObjective(goalOptions?["objective"]?.ToString(), AgentCatalog.BossNames);
        _inventoryReviewTick = _chestReviewRevision = _potionReviewTick = _preparationTick = 0;
        _lastAutomaticTrash = null;
        if (goalOptions != null && goalOptions["tokenBudget"] == null && useCodex)
            _propose = (role, facts, ct) => RequestProposal(role, facts, codexPath, cwd, codexModel, ct);
        string safetyPolicy = AgentTools.AllowTaskRecall
            ? "skill('safety_reactions',0);"
            : "skill('safety_reactions',0,{allow_recall:false});";
        _lastDangerNotice = 0;
        string system = AgentScript.Instructions(_system + "\n回复语言风格：" + Config.I.AgentPersona, _schemas);
        var entrySchemas = AgentScript.EntryTools(_schemas);
        Progress(useCodex ? "Connecting to Codex." : "Connecting to the model.", "waiting for model", visible: false);
        DiagLog.Write($"[agent] backend={(useCodex ? "codex" : "api")} model={(useCodex ? codexModel : model)} api_revision={AgentTools.ApiRevision} assembly={typeof(NativeAgent).Assembly.ManifestModule.ModuleVersionId}");
        _running = Task.Run(async () =>
        {
            try
            {
                await _goalStopping.WaitAsync(token);
                await AgentTools.Execute("task_policy", new JsonObject { ["operation"] = "set", ["expected_revision"] = 0,
                    ["code"] = safetyPolicy }, token);
                var modelContext = new AgentContext();
                int contextRevision = 0;
                bool ActionCoversCompaction() => (AgentTools.ControlSession.PrimaryActive || AgentTools.Actions.HasPrimaryControl)
                    && !Main.LocalPlayer.dead && !AgentTools.Watches.HasModelInterrupting;
                Task<bool> Covered() => OnMain(ActionCoversCompaction, token);
                Task<bool> ReadyToCompact() => OnMain(() => ActionCoversCompaction()
                    && AgentTools.Actions.HasQueuedSuccessor, token);
                async Task<string> Context()
                {
                    if (useCodex)
                    {
                        bool executing = await ReadyToCompact();
                        await codex.PrepareContext(codexPath, cwd, codexModel, system, entrySchemas, goalOptions != null, token,
                            compact: executing, keepExecuting: Covered);
                    }
                    if (useCodex && contextRevision != codex.ContextRevision)
                    { modelContext.Reset(); contextRevision = codex.ContextRevision; }
                    string result = modelContext.Capture(JsonNode.Parse(await AgentTools.State(token)).AsObject()).ToJsonString(AgentTools.ModelJson);
                    DiagLog.Write($"[agent-context] chars={result.Length} revision={contextRevision}");
                    return result;
                }
                string state = await Context();
                void Say(string text) => MainQueue.Enqueue(() => { if (!token.IsCancellationRequested) AgentChat.Say(text); });
                string input = goal + (goalOptions?["resume_previous"]?.GetValue<bool>() == true
                    ? "\n已重新进入存档。先用当前游戏状态核对以前的进度，按实际缺口继续原目标。" : "")
                    + "\n现状：" + state;
                long recoverySession = -1;
                int noProgressContinuations = 0;
                int preparationContinuations = 0;
                string preparedAttempt = null;
                bool preparedDuringRespawn = false;
                while (true)
                {
                    int progressBefore = await OnMain(() => AgentTools.Plan.Progress, token);
                    int arrivalsBefore = AgentTools.Actions.NavigationArrivals;
                    int discoveriesBefore = await OnMain(() => AgentTools.DiscoveryRevision, token);
                    var workflowBefore = await OnMain(() => AgentTools.Workflows.ToDictionary(w => w.Key, w => w.Value.Segment), token);
                    if (useCodex)
                        await codex.RunAsync(input, codexPath, cwd, codexModel, system, entrySchemas, AgentTools.Execute, Say, token, goalOptions, Context,
                            Covered, ReadyToCompact);
                    else
                        await _conversation.RunAsync(input, url, key, model, system, entrySchemas, AgentTools.Execute, Say, token);
                    bool scriptYielded = await OnMain(() => AgentTools.Workflows.Any(w => w.Value.Yielded
                        && w.Value.Segment > (workflowBefore.TryGetValue(w.Key, out int previous) ? previous : 0)), token);
                    bool reviewRequested = await OnMain(() => AgentTools.Workflows.Any(w => w.Value.ReviewRequested
                        && w.Value.Segment > (workflowBefore.TryGetValue(w.Key, out int previous) ? previous : 0)), token);
                    var backgroundReview = System.Diagnostics.Stopwatch.StartNew();
                    bool historyChanged = useCodex && contextRevision != codex.ContextRevision;
                    string preparingAttempt = await OnMain(() => AgentTools.PreparationContext(
                        AgentTools.Actions.ActiveAttempt, AgentTools.Actions.ActiveNode), token);
                    var preparation = await OnMain(() => (Delay: AgentTools.Plan.RequiresNodeExecution ? 0 : 15,
                        Nodes: AgentTools.Plan.PreparationNodes(AgentTools.Actions.ActiveNode, AgentTaskEvidence.Measure)), token);
                    bool prepareNext = historyChanged || preparingAttempt != null && preparingAttempt != preparedAttempt
                        && (preparingAttempt != AgentTools.PreparationNotifiedContext || preparation.Nodes.Count > 0);
                    double reviewDelay = preparation.Delay;
                    while (!reviewRequested && (!prepareNext || backgroundReview.Elapsed.TotalSeconds < reviewDelay)
                        && await OnMain(() => AgentTools.Actions.HasActive && (goalOptions != null || AgentTools.Watches.Armed)
                        && !AgentTools.Watches.HasModelInterrupting && !AgentTools.Watches.HasHealthNotice && !AgentTools.Watches.HasAdvisories, token))
                    {
                        await Task.Delay(100, token);
                        preparingAttempt = await OnMain(() => AgentTools.PreparationContext(
                            AgentTools.Actions.ActiveAttempt, AgentTools.Actions.ActiveNode), token);
                        prepareNext = historyChanged || preparingAttempt != null && preparingAttempt != preparedAttempt
                            && (preparingAttempt != AgentTools.PreparationNotifiedContext || preparation.Nodes.Count > 0);
                    }
                    if (!reviewRequested && (prepareNext
                        || await OnMain(() => AgentTools.Watches.HasHealthNotice || AgentTools.Watches.HasAdvisories, token))
                        && await OnMain(() => AgentTools.Actions.HasActive && !AgentTools.Watches.HasModelInterrupting, token))
                    {
                        preparedAttempt = preparingAttempt;
                        var healthNotices = await OnMain(AgentTools.Watches.TakeHealthNotices, token);
                        var opportunities = await OnMain(AgentTools.Watches.Advisories, token);
                        var handoff = await OnMain(() => AgentTools.LastContinuation?["mode"]?.ToString() == "prepared_followup"
                            && AgentTools.LastContinuation["started_handle"]?.ToString() == AgentTools.Actions.PrimaryHandle
                            ? AgentTools.LastContinuation.DeepClone() : null, token);
                        input = (handoff != null
                            ? "并行观察：上一段动作已结束，提前写好的后续脚本正在执行。核对上一段实际结果与当前行动，发现问题时再调整。交接：" + handoff.ToJsonString()
                            : "并行观察：后台动作继续。补齐当前节点on_failure和后继script；独立动作的父脚本已退出时用action_queue衔接。")
                            + "\n待补齐节点：" + preparation.Nodes.ToJsonString()
                            + "\n健康通知：" + healthNotices.ToJsonString()
                            + "\n沿途机会：" + opportunities.ToJsonString(AgentTools.ModelJson)
                            + "\n现状：" + await Context();
                        await OnMain(() => { DiagLog.Write("[agent] background_review active_handle=" + AgentTools.Actions.ActiveHandle
                            + " prepare=" + prepareNext + " health=" + healthNotices.Count
                            + " opportunities=" + string.Join(",", opportunities.Select(e => e["kind"]?.ToString()))); return true; }, token);
                        continue;
                    }
                    if (await OnMain(() => AgentTools.Watches.HasModelInterrupting, token))
                    {
                        if (AgentTools.Actions.NavigationArrivals > arrivalsBefore) noProgressContinuations = 0;
                        string handle = AgentTools.Actions.ActiveHandle;
                        if (handle != null) await AgentTools.Actions.Control(handle, "pause", null, token);
                        if (goalOptions == null && await OnMain(() => AgentTools.Recovery.Exhausted, token)) break;
                        var events = await OnMain(() => AgentTools.Watches.TakeModelInterrupts(8), token);
                        input = "宿主事件已暂停原后台动作并释放输入。根据事件调整剩余代码；已完成step保留，uncertain先核对。"
                            + "临时参数仅属于该动作，不能放宽玩家权限；需要继续时恢复同一handle。\n事件：" + events.ToJsonString()
                            + "\n现状：" + await Context();
                        continue;
                    }
                    bool died = await OnMain(() => Main.LocalPlayer.dead, token);
                    if (died && !await OnMain(() => PvpDuel.Active, token))
                    {
                        if (preparedDuringRespawn)
                            while (await OnMain(() => Main.LocalPlayer.dead, token)) await Task.Delay(250, token);
                        preparedDuringRespawn = await OnMain(() => Main.LocalPlayer.dead, token);
                        input = preparedDuringRespawn
                            ? "继续原目标。角色正在等待复活，现在查询资源、配方和现场证据，修订未完成节点的script，准备恢复方案。实际动作在复活后核对新位置再执行。\n现状：" + await Context()
                            : "继续原目标。刚才死亡，现已复活。依据实际现状执行已准备的恢复方案。\n现状：" + await Context();
                        continue;
                    }
                    preparedDuringRespawn = false;
                    bool canContinue = await OnMain(() => AgentTools.Plan.Incomplete
                        && AgentTools.Plan.Snapshot()["status"]?.ToString() != "blocked"
                        && !PvpDuel.Active, token);
                    if (goalOptions != null && !await OnMain(() => AgentTools.Plan.VerifyFinal(AgentTaskEvidence.Measure)["verified"].GetValue<bool>(), token))
                        canContinue = await OnMain(() => AgentTools.Plan.Snapshot()["status"]?.ToString() != "blocked", token);
                    bool progressed = await OnMain(() => AgentTools.Plan.Progress > progressBefore, token)
                        || AgentTools.Actions.NavigationArrivals > arrivalsBefore;
                    if (AgentTools.Actions.NavigationArrivals > arrivalsBefore)
                        await OnMain(() => { DiagLog.Write("[agent_progress] navigation_arrivals=" + AgentTools.Actions.NavigationArrivals); return true; }, token);
                    if (scriptYielded)
                    {
                        canContinue = true;
                    }
                    if (progressed) { noProgressContinuations = 0; preparationContinuations = 0; }
                    if (await OnMain(() => AgentTools.Recovery.Exhausted, token)) canContinue = false;
                    if (canContinue && await OnMain(() => AgentTools.Recovery.Offer(), token))
                    {
                        input = "工具失败：按证据修正参数、来源或局部方案，继续原任务。"
                            + "修复最多3轮，同一失败参数最多3次。\n失败证据："
                            + (await OnMain(() => AgentTools.Recovery.Snapshot(), token)).ToJsonString(AgentTools.ModelJson) + "\n现状：" + await Context();
                        continue;
                    }
                    bool discovered = await OnMain(() => AgentTools.DiscoveryRevision > discoveriesBefore, token);
                    if (canContinue && (progressed || discovered && preparationContinuations++ < 4 || noProgressContinuations++ < 1))
                    {
                        input = "计划未完成或脚本yield：结合当前workflow续写并执行剩余步骤，uncertain先核对。"
                            + (goalOptions == null ? "新查证资料仅允许最多4轮准备续接；没有新资料或实际进展时仅额外续接一次。"
                                : "长期goal持续续接，不因普通指令的轮数限制结束；利用recovery.failure_history改变失败方案，不只查询和空yield。")
                            + "请执行采集、移动或制作；缺少可执行办法时保留真实阻塞并调查替代方案。\n现状："
                            + await Context();
                        continue;
                    }
                    if (goalOptions != null && !await OnMain(() => AgentTools.Plan.VerifyFinal(AgentTaskEvidence.Measure)["verified"].GetValue<bool>(), token))
                    {
                        await OnMain(() => { Progress("Continuing the goal.", "continuing goal", visible: false); return true; }, token);
                        input = "目标尚未通过宿主验收，按当前阻塞调整并继续原计划。"
                            + "已有后台动作保持执行；失败按新证据换方案。"
                            + "修复blocked节点并保留既有进度。"
                            + "\n现状：" + await Context();
                        continue;
                    }
                    JsonObject report;
                    while (true)
                    {
                        report = await OnMain(() =>
                        {
                            if (!PvpDuel.Active) return new JsonObject { ["ended"] = true };
                            Status = "waiting for combat instructions";
                            bool requested = CombatMessagesPending;
                            if (!requested && (!PvpDuel.RecoveryRequired || recoverySession == PvpDuel.Session)) return null;
                            if (!requested) recoverySession = PvpDuel.Session;
                            var current = PvpDuel.Report();
                            current["player_messages"] = TakeCombatMessages();
                            return current;
                        }, token);
                        if (report != null) break;
                        await Task.Delay(250, token);
                    }
                    if (report["ended"] != null) break;
                    input = "PvP新要求或本会话一次受阻求助：处理后结束本轮，Jev继续控制。\n战况：" + report.ToJsonString();
                }
                JsonObject final = null;
                if (goalOptions != null)
                {
                    final = await codex.CompleteGoal(ct => OnMain(() =>
                    {
                        var verified = AgentTools.Plan.VerifyFinal(AgentTaskEvidence.Measure);
                        if (AgentTools.Actions.HasActive) verified["verified"] = false;
                        return verified;
                    }, ct), token);
                    if (final?["verified"]?.GetValue<bool>() != true)
                    {
                        string handle = AgentTools.Actions.ActiveHandle;
                        if (handle != null) await AgentTools.Actions.Control(handle, "pause", null, token);
                        if (await OnMain(() => AgentTools.Recovery.Exhausted || AgentTools.Plan.Snapshot()["status"]?.ToString() == "blocked", token))
                            await codex.SuspendGoal("blocked", token);
                    }
                }
                await OnMain(() =>
                {
                    if (goalOptions != null) DiagLog.Write("[agent] goal result " + final?.ToJsonString());
                    if (final?["verified"]?.GetValue<bool>() == true) Progress("Goal complete. Verified in game.", "complete");
                    else if (AgentTools.Plan.Incomplete || AgentTools.Workflows.Values.Any(w => w.Yielded)
                        || goalOptions != null && codex.GoalState?["native"]?["status"]?.ToString() != "complete")
                    {
                        if (goalOptions != null)
                            Progress("Goal needs recovery. Progress saved. Use /tb /goal resume to continue.", "needs_repair");
                        else Progress("This run ended before verification. Progress saved.", "paused");
                    }
                    else Status = AgentTools.Actions.HasActive ? "background action running" : "idle";
                    if (!AgentTools.Actions.HasActive && !AgentTools.Plan.Incomplete)
                    { AgentTools.ClearTaskPolicy(); AgentTools.Watches.End(); }
                    return true;
                }, token);
            }
            catch (OperationCanceledException) { }
            catch (GoalBudgetExceededException error)
            {
                await OnMain(() => { AgentTools.Stop(); Progress(error.Message, "budgetLimited"); return true; }, default);
            }
            catch (Exception ex)
            {
                string error = string.IsNullOrEmpty(key) ? ex.Message : ex.Message.Replace(key, "[redacted]");
                MainQueue.Enqueue(() =>
                {
                    if (token.IsCancellationRequested) return;
                    AgentTools.Plan.Record("host_failure", error);
                    AgentTools.Stop(); Status = "failed";
                    DiagLog.Write("[agent] " + error);
                    AgentChat.Say("Action stopped: " + error);
                });
            }
        });
    }

    internal static Task<T> OnMain<T>(Func<T> action, CancellationToken token)
    {
        var result = new TaskCompletionSource<T>();
        var registration = token.Register(() => result.TrySetCanceled(token));
        MainQueue.Enqueue(() =>
        {
            try
            {
                if (!token.IsCancellationRequested) result.TrySetResult(action());
            }
            catch (Exception ex) { result.TrySetException(ex); }
            finally { registration.Dispose(); }
        });
        return result.Task;
    }

    internal static Task NextUpdate(CancellationToken token, bool worldFrame = false)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = token.Register(() => result.TrySetCanceled(token));
        (worldFrame ? WorldFrameQueue : FrameQueue).Enqueue(() => { result.TrySetResult(true); registration.Dispose(); });
        return result.Task;
    }

    internal static void ValidateTool(string name, JsonObject args)
    {
        var schema = _instance?._schemas?.FirstOrDefault(t => t["function"]?["name"]?.ToString() == name);
        if (schema == null) throw new ArgumentException("未知游戏工具：" + name);
        foreach (var field in schema["function"]?["parameters"]?["required"]?.AsArray() ?? new JsonArray())
            if (args[field.ToString()] == null)
            {
                var error = new ArgumentException("工具缺少必填参数：" + field);
                error.Data["tool_schema"] = schema["function"].DeepClone();
                throw error;
            }
    }

    internal static JsonObject DescribeApi(JsonObject args) => AgentScript.DescribeApi(_instance._schemas, args);

    internal static async Task<string> Ask(string question, CancellationToken token)
    {
        Task<string> answer = await OnMain(() =>
        {
            _instance._answer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            Status = "waiting for answer";
            AgentChat.Say(question);
            return _instance._answer.Task;
        }, token);
        return await answer.WaitAsync(token);
    }

    public override void OnWorldLoad()
    {
        if (!Main.dedServ) AgentTools.RefreshHousingProtection();
        if (Main.dedServ || !Config.I.AgentEnabled || _scriptWarmup != null) return;
        _scriptWarmup = new CancellationTokenSource();
        var token = _scriptWarmup.Token;
        _ = Task.Run(() => AgentScript.WarmUp(token), token);
    }

    public override void OnWorldUnload()
    {
        Cancel();
        AgentTools.ClearTaskPolicy(true);
        AgentTools.Plan.Reset();
        AgentTools.Workflows.Clear();
        AgentTools.Actions.Reset();
        AgentTools.Control.Reset();
        AgentTools.Control.ReplaceHousing(Array.Empty<(int, int, int, int)>());
        AgentTools.ClearHousingConstruction();
        AgentTools.ControlSession.Reset();
        AgentTools.LastDamage = AgentTools.LastDeath = AgentTools.LastContactEscape = AgentTools.LastAutomaticHeal = null;
        AgentTools.ContactEscapeActive = false;
        CombatLoadout.PreferredType = 0;
        CombatLoadout.AmmoFallbackType = 0;
        AgentTaskEvidence.Reset();
        PlayerCombat.Stop();
        PvpDuel.Stop();
        CloseCodex();
        _goalOptions = null;
        if (_http != null) _conversation = new AgentConversation(_http);
        AgentPermissions.Reset();
    }

    public override void Unload()
    {
        _scriptWarmup?.Cancel();
        _scriptWarmup?.Dispose();
        Cancel();
        CloseCodex();
        _http?.Dispose();
        _instance = null;
        while (MainQueue.TryDequeue(out _)) { }
    }
}

internal sealed class ParallelAgentWork
{
    const int Capacity = 5;
    sealed record Work(string Key, Task<JsonObject> Result, Func<JsonObject, JsonObject> Apply,
        long Started, CancellationTokenSource Lifetime);
    readonly List<Work> running = new();
    readonly Dictionary<string, string> attempted = new();
    readonly Queue<JsonObject> receipts = new();

    internal bool CanOffer(string key, string revision) => running.Count < Capacity
        && !running.Any(job => job.Key == key) && (!attempted.TryGetValue(key, out var previous) || previous != revision);

    internal void Offer(string key, string revision, Func<CancellationToken, Task<JsonObject>> propose,
        Func<JsonObject, JsonObject> apply, CancellationToken token)
    {
        if (token.IsCancellationRequested || !CanOffer(key, revision)) return;
        attempted[key] = revision;
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        lifetime.CancelAfter(TimeSpan.FromSeconds(30));
        var result = Task.Run(() => propose(lifetime.Token), lifetime.Token);
        running.Add(new Work(key, result, apply, System.Diagnostics.Stopwatch.GetTimestamp(), lifetime));
    }

    internal void Poll(CancellationToken token)
    {
        if (token.IsCancellationRequested) return;
        for (int i = 0; i < running.Count; i++)
        {
            var job = running[i];
            if (!job.Result.IsCompleted) continue;
            try
            {
                var proposal = job.Result.GetAwaiter().GetResult();
                if (proposal != null && !job.Lifetime.IsCancellationRequested)
                {
                    var receipt = job.Apply(proposal);
                    if (receipt == null) continue;
                    if (receipt.Count > 0)
                    {
                        receipt["worker"] = job.Key;
                        receipt["elapsed_ms"] = (long)System.Diagnostics.Stopwatch.GetElapsedTime(job.Started).TotalMilliseconds;
                        if (receipts.Count == 8) receipts.Dequeue();
                        receipts.Enqueue(receipt);
                        DiagLog.Write("[agent_parallel] " + receipt.ToJsonString());
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { DiagLog.Write($"[agent_parallel] worker={job.Key} failed={error.Message}"); }
            job.Lifetime.Dispose();
            running.RemoveAt(i--);
        }
    }

    internal JsonArray Receipts() => new(receipts.Select(r => r.DeepClone()).ToArray());

    internal void Reset()
    {
        foreach (var job in running)
        {
            job.Lifetime.Cancel();
            _ = job.Result.ContinueWith(task => { _ = task.Exception; job.Lifetime.Dispose(); }, TaskScheduler.Default);
        }
        running.Clear(); attempted.Clear(); receipts.Clear();
    }
}

public static class AgentPermissions
{
    static bool dodge = true, attack = true;
    public static bool CanDodge(bool delegated = false) => dodge && AgentTools.ControlAllows(ControlBehavior.Dodge, ControlChannels.Movement, delegated);
    public static bool CanAttack(bool delegated = false) => attack && AgentTools.ControlAllows(ControlBehavior.Attack, ControlChannels.ItemUse, delegated);
    public static bool Dodge { get => CanDodge(); set => dodge = value; }
    public static bool Attack { get => CanAttack(); set => attack = value; }
    public static void Reset() { dodge = attack = true; }
}
