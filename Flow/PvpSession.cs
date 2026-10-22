using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Terraria;

namespace TerraBlind;

public static partial class PvpDuel
{
    static readonly Queue<JsonObject> events = new();
    static readonly Dictionary<string, (ulong Tick, JsonObject Entry)> recentEvents = new();
    static long eventSequence, requestRevision;
    static int sessionTarget;
    static long actionCounter, actionLease, actionSession;
    public static bool ExternalAction => actionLease != 0;
    static JsonObject latestScene;
    static readonly PvpHistory history = new();
    static int obstructedFrames;
    public static long Session { get; private set; }
    public static long EventSequence => eventSequence;
    public static bool RecoveryRequired => obstructedFrames >= 180;
    public static PvpDirective Directive { get; private set; }

    public static long BeginAction(string kind)
    {
        if (!Active) return 0;
        if (ExternalAction) throw new InvalidOperationException("combat_action_busy");
        actionLease = ++actionCounter; actionSession = Session;
        Movement.Release(Main.LocalPlayer); ClearControls(Main.LocalPlayer);
        lifetime.Cancel(); lifetime.Dispose(); lifetime = new CancellationTokenSource();
        if (pending != null) _ = pending.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        pending = null; Outcome = "paused_for_action";
        Event("action_started", kind);
        return actionLease;
    }

    public static void EndAction(long lease)
    {
        if (lease == 0 || lease != actionLease || !Active || actionSession != Session) return;
        Halt.All(); actionLease = 0; ClearControls(Main.LocalPlayer);
        Movement = new JevariaCombat.GameMovement(); appliedSnapshot = null; fireIntent = false;
        Outcome = Main.LocalPlayer.dead ? "waiting_for_respawn" : "running";
        Event("action_finished", "Resampling the changed world before resuming combat.");
    }

    public static void InventoryChanged()
    {
        if (!Active) return;
        lifetime.Cancel(); lifetime.Dispose(); lifetime = new CancellationTokenSource();
        if (pending != null) _ = pending.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        pending = null; fireIntent = false; Main.LocalPlayer.controlUseItem = false;
        Event("inventory_changed", "Weapon references invalidated; resampling before the next attack.");
    }

    static void BeginSession(int target)
    {
        Session++; sessionTarget = target;
        events.Clear(); recentEvents.Clear(); eventSequence = 0; latestScene = null; history.Reset(); obstructedFrames = 0;
        Event("started", "running");
    }

    static void Event(string kind, string detail)
    {
        ulong tick = Main.GameUpdateCount;
        string key = kind + ":" + detail;
        if (recentEvents.TryGetValue(key, out var previous) && tick - previous.Tick < 60)
        {
            previous.Entry["count"] = previous.Entry["count"].GetValue<int>() + 1;
            previous.Entry["last_tick"] = tick;
            return;
        }
        var entry = new JsonObject { ["seq"] = ++eventSequence, ["tick"] = tick,
            ["kind"] = kind, ["detail"] = detail, ["count"] = 1 };
        if (events.Count == 128) events.Dequeue();
        events.Enqueue(entry); recentEvents[key] = (tick, entry);
        foreach (string old in recentEvents.Where(pair => tick - pair.Value.Tick > 60).Select(pair => pair.Key).ToArray()) recentEvents.Remove(old);
        DiagLog.Write($"[pvp-event] session={Session} seq={eventSequence} kind={kind} detail={detail}");
    }

    static JsonObject Body(Player player) => new()
    {
        ["id"] = player.whoAmI, ["name"] = player.name, ["hp"] = player.statLife, ["max_hp"] = player.statLifeMax2,
        ["x"] = player.Center.X, ["y"] = player.Center.Y, ["vx"] = player.velocity.X, ["vy"] = player.velocity.Y,
        ["dead"] = player.dead, ["held_type"] = player.HeldItem.type, ["held_name"] = player.HeldItem.Name,
        ["width"] = player.width, ["height"] = player.height, ["pvp"] = player.hostile, ["team"] = player.team,
        ["item_animation"] = player.itemAnimation, ["item_time"] = player.itemTime
    };

    static JsonObject Perception(Player self, Player target, bool detailed = false, JevariaCombat.CombatSnapshot snapshot = null)
    {
        snapshot ??= Sensor.CapturePlayer(self, target, sequence, intent, choiceAge * 1000 / 60);
        var movement = JsonNode.Parse(JevariaCombat.DecisionFacts.State(snapshot, "")).AsObject();
        movement.Remove("threats"); movement.Remove("boss_notes");
        return new JsonObject
        {
            ["captured_tick"] = Main.GameUpdateCount, ["units"] = "pixels; velocity=pixels/game_frame",
            ["self"] = Body(self), ["target"] = Body(target), ["self_mana"] = self.statMana, ["target_mana"] = null,
            ["movement_state"] = movement, ["history"] = history.ToJson(), ["terrain"] = PvpTerrain.Observe(self, target, detailed),
            ["inventory_weapons"] = InventoryWeapons(self)
        };
    }

    static void TrackObstruction(Player self, Player target)
    {
        if (Clear(self.Center, target.Center) || lastAimClear) obstructedFrames = 0;
        else if (++obstructedFrames == 180)
            Event("recovery_required", "No firing line for 180 active frames. Execute and verify a concrete recovery action; instruction edits are not movement.");
    }

    static JsonArray InventoryWeapons(Player self)
    {
        var result = new JsonArray();
        for (int slot = 0; slot < 50; slot++)
        {
            var item = self.inventory[slot];
            if (item.IsAir || item.damage <= 0 || item.ammo > 0 || item.pick > 0 || item.axe > 0 || item.hammer > 0) continue;
            result.Add(new JsonObject { ["slot"] = slot, ["type"] = item.type, ["prefix"] = item.prefix,
                ["stack"] = item.stack,
                ["name"] = item.Name, ["damage_before_target_defense"] = self.GetWeaponDamage(item),
                ["hotbar"] = slot < 10, ["allowed"] = Directive?.AllowsWeapon(item.type) ?? true,
                ["has_ammo"] = item.useAmmo == 0 || self.HasAmmo(item), ["mana_cost_estimate"] = item.mana * self.manaCost,
                ["use_time"] = item.useTime, ["use_animation"] = item.useAnimation, ["channel"] = item.channel,
                ["projectile_type"] = item.shoot, ["base_shoot_speed"] = item.shootSpeed,
                ["trajectory_support"] = item.shoot == 0 ? "contact_reach_estimate" : "simple_trajectory_estimate_not_all_mod_behaviors" });
        }
        return result;
    }

    public static JsonObject Report(long afterEvent = 0, bool detailed = false) => new()
    {
        ["session"] = Session, ["tick"] = Main.GameUpdateCount, ["active"] = Active, ["outcome"] = Outcome,
        ["directive"] = Directive?.ToJson(), ["remaining_active_frames"] = frames, ["jev_decisions"] = Decisions,
        ["self"] = Body(Main.LocalPlayer),
        ["perception"] = Active ? Perception(Main.LocalPlayer, Main.player[sessionTarget], detailed) : null,
        ["target"] = Session == 0 ? null : Body(Main.player[sessionTarget]),
        ["current_mode"] = mode, ["movement_reason"] = movementReason, ["attack_reason"] = attackReason,
        ["action_owner"] = ExternalAction ? "agent_tool" : "combat",
        ["recovery"] = new JsonObject { ["required"] = obstructedFrames >= 180,
            ["blocked_active_frames"] = obstructedFrames,
            ["next_step"] = "Choose a fresh stance probe for combat_reposition, or inspect inventory/tools and execute an allowed alternative. No clear probes does not prove enclosure. Do not repeat instruction-only updates as recovery." },
        ["scene"] = detailed ? latestScene?.DeepClone() : null, ["last_event"] = eventSequence,
        ["selected_aim"] = aimKind, ["fire_intent"] = fireIntent,
        ["request_tick"] = latestScene?["captured_tick"]?.DeepClone(),
        ["weapon_options_at_request"] = latestScene?["weapon_modes"]?.DeepClone(),
        ["events_truncated"] = events.Count > 0 && afterEvent < events.Peek()["seq"].GetValue<long>() - 1,
        ["events"] = new JsonArray(events.Where(entry => entry["seq"].GetValue<long>() > afterEvent).Select(entry => entry.DeepClone()).ToArray())
    };

    public static JsonObject UpdateDirective(JsonObject patch, bool fromPlayer = false)
    {
        if (!Active || patch["session"]?.GetValue<long>() != Session) return new JsonObject { ["error"] = "session_mismatch" };
        var result = Directive.Update(patch, fromPlayer);
        if (result["error"] != null) return result;
        lifetime.Cancel(); lifetime.Dispose(); lifetime = new CancellationTokenSource();
        if (pending != null) _ = pending.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        pending = null;
        if (!Directive.AllowsWeapon(Main.LocalPlayer.HeldItem.type)) Main.LocalPlayer.controlUseItem = false;
        Event("directive_updated", "revision=" + Directive.Revision);
        result["session"] = Session;
        return result;
    }
}
