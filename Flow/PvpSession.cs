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
    static JsonObject latestScene;
    static readonly PvpHistory history = new();
    public static long Session { get; private set; }
    public static long EventSequence => eventSequence;
    public static PvpDirective Directive { get; private set; }

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
        events.Clear(); recentEvents.Clear(); eventSequence = 0; latestScene = null; history.Reset();
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
        ["item_animation"] = player.itemAnimation, ["item_time"] = player.itemTime
    };

    static JsonObject Perception(Player self, Player target, bool detailed = false) => new()
    {
        ["captured_tick"] = Main.GameUpdateCount, ["units"] = "pixels; velocity=pixels/game_frame",
        ["self"] = Body(self), ["target"] = Body(target), ["self_mana"] = self.statMana, ["target_mana"] = null,
        ["history"] = history.ToJson(), ["terrain"] = PvpTerrain.Observe(self, target, detailed),
        ["inventory_weapons"] = InventoryWeapons(self)
    };

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
        ["scene"] = latestScene?.DeepClone(), ["last_event"] = eventSequence,
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
