using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Port = TerraBlind.JevariaCombat;
using V = System.Numerics.Vector2;

namespace TerraBlind;

public static class PvpDuel
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };
    static readonly Port.GameSensor Sensor = new();
    static Port.GameMovement Movement = new();
    static CancellationTokenSource lifetime;
    static Task<PvpTactics.Decision> pending;
    static Func<JsonObject, CancellationToken, Task<PvpTactics.Decision>> choose;
    static readonly Stopwatch requestClock = new();
    static readonly Dictionary<int, (int Identity, Vector2 Velocity, int Samples)> arrows = new();
    static Dictionary<string, Port.DodgeOption> options;
    static Port.CombatSnapshot requestSnapshot, appliedSnapshot;
    static Port.DodgeIntent intent = Port.DodgeIntent.Idle;
    static Vector2 requestSelf, requestTarget, lastPosition;
    static int frames, preferredSlot, activeWeapon, choiceAge, nextDecision, lastHp, lastTargetHp, observedType;
    static string key, strategy, mode, movementReason;
    static ulong sequence;
    static float speed, gravity, gravityDelay, range;
    public static int Target { get; private set; } = -1;
    public static bool Active => Target >= 0;
    public static string Outcome { get; private set; } = "idle";
    public static int Decisions { get; private set; }
    static V N(Vector2 value) => new(value.X, value.Y);

    static Dictionary<string, int> Weapons(Player self)
    {
        var result = new Dictionary<string, int>();
        for (int index = 0; index < 10; index++)
        {
            int slot = (preferredSlot + index) % 10;
            var item = self.inventory[slot];
            if (item.IsAir || item.damage <= 0 || item.useStyle <= 0 || item.ammo > 0
                || item.pick > 0 || item.axe > 0 || item.hammer > 0
                || item.useAmmo > 0 && !self.HasAmmo(item) || item.mana * self.manaCost > self.statMana) continue;
            if (!item.noMelee) result.TryAdd("melee", slot);
            if (item.shoot > 0) result.TryAdd("ranged", slot);
        }
        return result;
    }

    public static bool Start(int target, int seconds, int weaponSlot, float maxDistance, string instructions, out string reason,
        Func<JsonObject, CancellationToken, Task<PvpTactics.Decision>> decide = null)
    {
        Stop();
        var self = Main.LocalPlayer;
        reason = PlayerCombat.TargetReason(self, target);
        if (reason == "" && (!self.active || self.dead)) reason = "self_dead";
        if (reason == "" && (!AgentPermissions.Attack || !AgentPermissions.Dodge)) reason = "combat_permission_denied";
        if (reason == "" && string.IsNullOrWhiteSpace(Config.I.TypeSafeKey)) reason = "missing_jev_key";
        preferredSlot = Math.Clamp(weaponSlot, 0, 9);
        var weapons = Weapons(self);
        if (reason == "" && weapons.Count == 0) reason = "no_usable_weapon_in_hotbar";
        if (reason == "" && Vector2.Distance(self.Center, Main.player[target].Center) > maxDistance * 16) reason = "out_of_range";
        if (reason != "") { Outcome = reason; return false; }
        AgentTools.Stop();
        Movement = new Port.GameMovement();
        self.hostile = true;
        NetMessage.SendData(MessageID.TogglePVP, number: self.whoAmI);
        mode = weapons.ContainsKey("ranged") ? "ranged" : "melee";
        self.selectedItem = weapons[mode]; activeWeapon = -1;
        frames = Math.Clamp(seconds, 1, 120) * 60; range = maxDistance * 16;
        key = Config.I.TypeSafeKey.Trim(); strategy = instructions;
        lastHp = self.statLife; lastTargetHp = Main.player[target].statLife; lastPosition = self.Center;
        intent = Port.DodgeIntent.Idle; appliedSnapshot = requestSnapshot = null;
        choiceAge = 999; nextDecision = 0; Decisions = 0; sequence = 0; movementReason = "";
        choose = decide ?? ((scene, token) => PvpTactics.Choose(Http, key, scene, token));
        lifetime = new CancellationTokenSource(); Target = target; Outcome = "running";
        DiagLog.Write($"[pvp] start target={target} preferred_slot={preferredSlot} seconds={seconds} strategy={strategy}");
        return true;
    }

    static void ClearControls(Player self)
    {
        self.controlLeft = self.controlRight = self.controlUp = self.controlDown = false;
        self.controlJump = self.controlHook = self.controlUseItem = false;
    }

    public static void Stop(string outcome = "stopped")
    {
        if (Active)
        {
            Movement.Release(Main.LocalPlayer); ClearControls(Main.LocalPlayer);
            Outcome = outcome;
            DiagLog.Write($"[pvp] stop outcome={outcome} decisions={Decisions}");
        }
        Target = -1;
        lifetime?.Cancel(); lifetime?.Dispose(); lifetime = null;
        if (pending != null) _ = pending.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        pending = null; arrows.Clear(); appliedSnapshot = null;
    }

    static bool Clear(Vector2 from, Vector2 to) => Collision.CanHitLine(from, 4, 4, to, 4, 4);

    static void ObserveArrows(Player self)
    {
        foreach (var projectile in Main.projectile)
        {
            if (!projectile.active || projectile.owner != self.whoAmI || !projectile.arrow || projectile.damage <= 0) continue;
            float updates = projectile.extraUpdates + 1;
            if (!arrows.TryGetValue(projectile.whoAmI, out var previous) || previous.Identity != projectile.identity)
            {
                arrows[projectile.whoAmI] = (projectile.identity, projectile.velocity, 0);
                speed = projectile.velocity.Length() * updates;
                if (observedType != projectile.type)
                {
                    gravity = gravityDelay = 0; observedType = projectile.type;
                    DiagLog.Write($"[pvp] projectile type={observedType} speed={speed:0.###} updates={updates}");
                }
                continue;
            }
            if (projectile.type != observedType || previous.Samples >= 90) continue;
            Vector2 delta = projectile.velocity - previous.Velocity;
            if (Math.Abs(delta.X) < .1f && delta.Y >= 0 && delta.Y < 2)
            {
                if (gravity == 0 && delta.Y > .001f) gravityDelay = previous.Samples;
                if (delta.Y > .001f) gravity = delta.Y * updates;
            }
            arrows[projectile.whoAmI] = (projectile.identity, projectile.velocity, previous.Samples + 1);
        }
    }

    static bool Attack(Player self, Player target)
    {
        self.controlUseItem = false;
        var item = self.HeldItem;
        float shotSpeed = item.shootSpeed;
        int shotType = item.shoot;
        if (item.useAmmo > 0 && !self.PickAmmo(item, out shotType, out shotSpeed, out _, out _, out _, true)) return false;
        if (activeWeapon != item.type)
        {
            activeWeapon = item.type; speed = shotSpeed; gravity = gravityDelay = 0; observedType = 0; arrows.Clear();
            foreach (var projectile in Main.projectile)
                if (projectile.active) arrows[projectile.whoAmI] = (projectile.identity, projectile.velocity, int.MaxValue);
        }
        Vector2 point = target.Center;
        bool clear;
        if (mode == "melee")
        {
            float reach = Math.Max(32, Math.Max(item.width, item.height) * item.scale + self.width / 2f);
            clear = Vector2.Distance(self.Center, target.Center) <= reach + target.width / 2f && Clear(self.Center, target.Center);
        }
        else
        {
            ObserveArrows(self);
            if (observedType == 0)
            {
                speed = shotSpeed;
                if (ContentSamples.ProjectilesByType.TryGetValue(shotType, out var defaults)) speed *= defaults.extraUpdates + 1;
            }
            V? aim = PvpTactics.Aim(N(self.Center), N(target.Center), N(target.velocity), speed, gravity,
                (a, b) => Clear(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y)), gravityDelay);
            clear = aim.HasValue || observedType == 0 && Clear(self.Center, target.Center);
            if (aim.HasValue) point = new Vector2(aim.Value.X, aim.Value.Y);
        }
        if (!self.controlHook) Cursor.AimPx(point.X, point.Y);
        self.direction = target.Center.X < self.Center.X ? -1 : 1;
        self.controlUseItem = clear && !self.controlHook;
        if (self.controlUseItem && self.itemAnimation == 0) self.releaseUseItem = true;
        return clear;
    }

    public static void Tick(Player self)
    {
        if (!Active) return;
        string reason = PlayerCombat.TargetReason(self, Target);
        if (reason == "" && (!self.active || self.dead)) reason = "self_dead";
        if (reason == "" && (!AgentPermissions.Attack || !AgentPermissions.Dodge || !self.hostile)) reason = "combat_permission_denied";
        if (reason != "") { Stop(reason); return; }
        if (frames-- <= 0) { Stop("duration_complete"); return; }
        var target = Main.player[Target];
        if (Vector2.Distance(self.Center, target.Center) > range) { Stop("out_of_range"); return; }
        var weapons = Weapons(self);
        if (weapons.Count == 0) { Stop("no_usable_weapon_in_hotbar"); return; }
        if (pending?.IsCompleted == true)
        {
            try
            {
                var result = pending.GetAwaiter().GetResult();
                if (!options.TryGetValue(result.Move, out var selected) || !weapons.ContainsKey(result.WeaponMode))
                    throw new InvalidOperationException("invalid_choice");
                bool fresh = requestClock.Elapsed.TotalSeconds <= 1.5 && Vector2.Distance(self.Center, requestSelf) < 160
                    && Vector2.Distance(target.Center, requestTarget) < 240;
                if (fresh)
                {
                    intent = selected.Intent; appliedSnapshot = requestSnapshot;
                    mode = result.WeaponMode; choiceAge = 0; Decisions++;
                }
                DiagLog.Write($"[pvp] choice={result.Move} mode={result.WeaponMode} accepted={fresh} ms={requestClock.ElapsedMilliseconds}");
            }
            catch (Exception ex)
            {
                DiagLog.Write("[pvp] decision_failed " + ex.GetType().Name);
                Stop("jev_request_failed"); return;
            }
            pending = null;
        }
        if (!weapons.ContainsKey(mode)) { foreach (string available in weapons.Keys) { mode = available; break; } nextDecision = 0; }
        int slot = weapons[mode];
        if (self.selectedItem != slot && self.itemAnimation == 0 && self.itemTime == 0)
        {
            self.selectedItem = slot;
            DiagLog.Write($"[pvp] weapon mode={mode} slot={slot} name={self.HeldItem.Name}");
        }
        if (choiceAge++ <= 90 && appliedSnapshot != null)
        {
            var result = Movement.Apply(self, intent, appliedSnapshot with
            { CanDoubleJump = self.AnyExtraJumpUsable(), CanFly = self.wingTime > 0 });
            if (result.Reason != movementReason)
            {
                movementReason = result.Reason;
                DiagLog.Write($"[pvp] movement={result.Applied} reason={result.Reason} vel={self.velocity} mount={self.mount.Active}");
            }
        }
        else { Movement.Release(self); ClearControls(self); }
        bool aimClear = self.selectedItem == slot && Attack(self, target);
        if (self.selectedItem != slot) self.controlUseItem = false;
        if (pending == null && --nextDecision <= 0)
        {
            requestSnapshot = Sensor.CapturePlayer(self, target, ++sequence, intent, choiceAge * 1000 / 60);
            options = new Dictionary<string, Port.DodgeOption>();
            var criteria = new JsonObject();
            foreach (var option in requestSnapshot.AvailableActions)
            {
                string id = "m" + options.Count;
                options[id] = option; criteria[id] = Port.DecisionFacts.Describe(option, requestSnapshot);
            }
            var modes = new JsonObject();
            foreach (var pair in weapons)
            {
                var item = self.inventory[pair.Value];
                modes[pair.Key] = new JsonObject { ["slot"] = pair.Value, ["name"] = item.Name, ["damage"] = item.damage,
                    ["shoot_speed"] = item.shootSpeed, ["projectile_type"] = item.shoot,
                    ["description"] = pair.Key == "melee" ? "接触近战，需要靠近并直接够到目标。" : "发射武器，需射界；适合保持距离或封锁路线。" };
            }
            var scene = new JsonObject
            {
                ["strategy"] = strategy, ["movement_options"] = criteria, ["weapon_modes"] = modes, ["current_mode"] = mode,
                ["movement_state"] = JsonNode.Parse(Port.DecisionFacts.State(requestSnapshot, "")),
                ["dx"] = target.Center.X - self.Center.X, ["dy"] = target.Center.Y - self.Center.Y,
                ["self_hp"] = self.statLife, ["target_hp"] = target.statLife,
                ["hp_change"] = self.statLife - lastHp, ["target_hp_change"] = target.statLife - lastTargetHp,
                ["previous_move"] = intent.ToString(), ["movement_result"] = movementReason,
                ["moved_pixels"] = Vector2.Distance(self.Center, lastPosition), ["aim_clear"] = aimClear,
                ["trajectory_observed"] = observedType != 0
            };
            lastHp = self.statLife; lastTargetHp = target.statLife;
            lastPosition = requestSelf = self.Center; requestTarget = target.Center;
            DiagLog.Write("[pvp] scene " + scene.ToJsonString());
            nextDecision = 24; requestClock.Restart(); pending = choose(scene, lifetime.Token);
        }
    }
}
