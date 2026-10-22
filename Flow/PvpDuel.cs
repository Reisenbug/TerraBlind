using System;
using System.Collections.Generic;
using System.Linq;
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

public static partial class PvpDuel
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
    static Dictionary<string, (int Slot, int Type, int Prefix)> requestItems;
    static Port.CombatSnapshot requestSnapshot, appliedSnapshot;
    static Port.DodgeIntent intent = Port.DodgeIntent.Idle;
    static Vector2 requestSelf, requestTarget, lastPosition;
    static int frames, preferredSlot, activeWeapon, choiceAge, lastHp, lastTargetHp, observedType;
    static bool lastAimClear, fireIntent = true;
    static string aimKind = "velocity";
    static int plannedType, plannedPrefix, activePrefix;
    static int observedHp;
    static string key, strategy, mode, movementReason, attackReason;
    static ulong sequence;
    static float speed, gravity, gravityDelay, range;
    static bool waitingForRespawn;
    public static bool UntilStopped { get; private set; }
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
            if (Directive != null && !Directive.AllowsWeapon(item.type)) continue;
            if (item.IsAir || item.damage <= 0 || item.useStyle <= 0 || item.ammo > 0
                || item.pick > 0 || item.axe > 0 || item.hammer > 0
                || item.useAmmo > 0 && !self.HasAmmo(item) || item.mana * self.manaCost > self.statMana) continue;
            void Add(string category)
            {
                if (!result.TryAdd(category, slot)) result[category + "_" + slot + "_" + item.type + "_" + item.prefix] = slot;
            }
            if (!item.noMelee) Add("melee");
            if (item.shoot > 0) Add("ranged");
        }
        return result;
    }

    public static bool Start(int target, int seconds, int weaponSlot, float maxDistance, string instructions, out string reason,
        Func<JsonObject, CancellationToken, Task<PvpTactics.Decision>> decide = null, bool untilStopped = false)
    {
        Stop();
        Directive = new PvpDirective(instructions);
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
        aimKind = "velocity"; fireIntent = true; plannedType = self.HeldItem.type; plannedPrefix = self.HeldItem.prefix;
        UntilStopped = untilStopped;
        frames = Math.Clamp(seconds, 1, 120) * 60; range = maxDistance * 16;
        key = Config.I.TypeSafeKey.Trim(); strategy = instructions;
        lastHp = self.statLife; lastTargetHp = Main.player[target].statLife; lastPosition = self.Center;
        observedHp = self.statLife;
        intent = Port.DodgeIntent.Idle; appliedSnapshot = requestSnapshot = null;
        choiceAge = 999; Decisions = 0; sequence = 0; movementReason = attackReason = "";
        choose = decide ?? ((scene, token) => PvpTactics.Choose(Http, key, scene, token));
        lifetime = new CancellationTokenSource(); waitingForRespawn = false; Target = target; Outcome = "running";
        BeginSession(target);
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
            Event("stopped", outcome);
            DiagLog.Write($"[pvp] stop outcome={outcome} decisions={Decisions}");
        }
        Target = -1; waitingForRespawn = false; actionLease = 0;
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

    static float MeleeReach(Player self, Player target, Item item)
        => Math.Max(32, Math.Max(item.width, item.height) * item.scale + self.width / 2f) + target.width / 2f;

    static PvpTactics.ShotSolution? SolveShot(Player self, Player target, Item item, string kind)
    {
        float shotSpeed = item.shootSpeed;
        int shotType = item.shoot;
        if (item.useAmmo > 0 && !self.PickAmmo(item, out shotType, out shotSpeed, out _, out _, out _, true)) return null;
        bool observed = activeWeapon == item.type && activePrefix == item.prefix && observedType != 0;
        if (ContentSamples.ProjectilesByType.TryGetValue(shotType, out var defaults)) shotSpeed *= defaults.extraUpdates + 1;
        Vector2 point = target.Center, velocity = target.velocity;
        if (kind == "landing")
        {
            var landing = PvpTerrain.Landing(target, history.FallingAcceleration());
            if (!landing.HasValue) return null;
            point = landing.Value.Point; velocity = Vector2.Zero;
        }
        if (kind == "exit")
        {
            var exit = PvpTerrain.PlatformExit(target);
            if (!exit.HasValue) return null;
            point = exit.Value; velocity = Vector2.Zero;
        }
        return PvpTactics.Solve(N(self.Center), N(point), N(velocity), observed ? speed : shotSpeed,
            observed ? gravity : 0, (a, b) => Clear(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y)), observed ? gravityDelay : 0, kind);
    }

    static Vector2? ShotAim(Player self, Player target, Item item, string kind = null)
    {
        var aim = SolveShot(self, target, item, kind ?? aimKind);
        return aim.HasValue ? new Vector2(aim.Value.Point.X, aim.Value.Point.Y) : null;
    }

    static bool ContactMode(string value) => value.StartsWith("melee", StringComparison.Ordinal);

    static bool Attack(Player self, Player target)
    {
        self.controlUseItem = false;
        var item = self.HeldItem;
        if (activeWeapon != item.type || activePrefix != item.prefix)
        {
            activeWeapon = item.type; activePrefix = item.prefix; speed = item.shootSpeed; gravity = gravityDelay = 0; observedType = 0; arrows.Clear();
            foreach (var projectile in Main.projectile)
                if (projectile.active) arrows[projectile.whoAmI] = (projectile.identity, projectile.velocity, int.MaxValue);
        }
        Vector2 point = target.Center;
        bool clear;
        if (ContactMode(mode))
        {
            clear = Vector2.Distance(self.Center, target.Center) <= MeleeReach(self, target, item) && Clear(self.Center, target.Center);
        }
        else
        {
            ObserveArrows(self);
            Vector2? aim = ShotAim(self, target, item);
            clear = aim.HasValue;
            if (aim.HasValue) point = aim.Value;
        }
        if (!self.controlHook) Cursor.AimPx(point.X, point.Y);
        self.direction = target.Center.X < self.Center.X ? -1 : 1;
        self.controlUseItem = clear && !self.controlHook && fireIntent && item.type == plannedType && item.prefix == plannedPrefix;
        if (self.controlUseItem && self.itemAnimation == 0) self.releaseUseItem = true;
        string reason = self.controlHook ? "grappling" : !fireIntent ? "plan_reposition_without_firing"
            : item.type != plannedType || item.prefix != plannedPrefix ? "weapon_reference_changed"
            : clear ? "attacking" : ContactMode(mode) ? "melee_out_of_reach_or_blocked" : "no_clear_shot";
        if (reason != attackReason)
        {
            attackReason = reason;
            Event("attack_state", reason);
            DiagLog.Write($"[pvp] attack={reason} mode={mode} weapon={item.Name} distance={Vector2.Distance(self.Center, target.Center):0} animation={self.itemAnimation}");
        }
        return clear;
    }

    public static void Tick(Player self)
    {
        Update(self);
        ApplyInputs(self);
    }

    public static void Update(Player self)
    {
        if (!Active) return;
        string reason = PlayerCombat.TargetReason(self, Target);
        if (reason == "target_dead") reason = "";
        if (reason == "" && !self.active) reason = "self_inactive";
        if (reason == "" && (!AgentPermissions.Attack || !AgentPermissions.Dodge || !self.hostile)) reason = "combat_permission_denied";
        if (reason != "") { Stop(reason); return; }
        if (self.dead || Main.player[Target].dead)
        {
            if (!waitingForRespawn)
            {
                Movement.Release(self); ClearControls(self);
                lifetime.Cancel(); lifetime.Dispose(); lifetime = new CancellationTokenSource();
                if (pending != null) _ = pending.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                pending = null; appliedSnapshot = null; intent = Port.DodgeIntent.Idle;
                waitingForRespawn = true; Outcome = self.dead ? "waiting_for_respawn" : "waiting_for_target_respawn";
                Event("death", Outcome);
                NativeAgent.Progress("有人倒下了，双方复活后继续对战。", "等待复活");
                DiagLog.Write($"[pvp] paused target={Target} remaining_frames={frames}");
            }
            return;
        }
        if (waitingForRespawn)
        {
            waitingForRespawn = false; Outcome = "running";
            Event("respawn", Outcome);
            Movement = new Port.GameMovement(); choiceAge = 999;
            activeWeapon = -1; arrows.Clear(); attackReason = "";
            lastHp = self.statLife; lastTargetHp = Main.player[Target].statLife; lastPosition = self.Center;
            NativeAgent.Progress("复活了，继续对战。", "执行 duel_player");
            DiagLog.Write($"[pvp] resumed target={Target} remaining_frames={frames}");
        }
        if (ExternalAction) return;
        if (!UntilStopped && frames-- <= 0) { Stop("duration_complete"); return; }
        var target = Main.player[Target];
        if (self.statLife < observedHp) Event("self_damaged", "HP decreased; inspect current threats and recent outcomes.");
        observedHp = self.statLife;
        history.Record(self, target, lastAimClear);
        TrackObstruction(self, target);
        if (history.StationaryFrames >= 60 && intent.Direction != Port.DodgeDirection.Stay)
            Event("movement_stalled", "Movement was requested but position barely changed for 60 sampled frames.");
        if (Vector2.Distance(self.Center, target.Center) > range) { Stop("out_of_range"); return; }
        var weapons = Weapons(self);
        if (weapons.Count == 0)
        {
            Event("no_usable_weapon", "Check restrictions, ammunition and mana.");
        }
        if (pending?.IsCompleted == true)
        {
            try
            {
                var result = pending.GetAwaiter().GetResult();
                if (!options.TryGetValue(result.Move, out var selected))
                    throw new InvalidOperationException("invalid_choice");
                bool fresh = requestRevision == Directive.Revision;
                if (fresh)
                {
                    intent = selected.Intent; appliedSnapshot = requestSnapshot;
                    fireIntent = result.Fire && requestItems.TryGetValue(result.WeaponMode, out var itemRef)
                        && weapons.TryGetValue(result.WeaponMode, out int currentSlot) && currentSlot == itemRef.Slot
                        && self.inventory[currentSlot].type == itemRef.Type && self.inventory[currentSlot].prefix == itemRef.Prefix;
                    if (fireIntent)
                    {
                        mode = result.WeaponMode; plannedType = self.inventory[weapons[mode]].type;
                        plannedPrefix = self.inventory[weapons[mode]].prefix;
                    }
                    aimKind = result.Aim;
                    choiceAge = 0; Decisions++;
                }
                DiagLog.Write($"[pvp] choice={result.Move} mode={result.WeaponMode} accepted={fresh} ms={requestClock.ElapsedMilliseconds} self_delta={Vector2.Distance(self.Center, requestSelf):0} target_delta={Vector2.Distance(target.Center, requestTarget):0} attack_available={weapons.ContainsKey(result.WeaponMode)}");
            }
            catch (Exception ex)
            {
                DiagLog.Write("[pvp] decision_failed " + ex.GetType().Name);
                Stop("jev_request_failed"); return;
            }
            pending = null;
        }
        choiceAge++;
        if (pending == null)
        {
            requestSnapshot = Sensor.CapturePlayer(self, target, ++sequence, intent, choiceAge * 1000 / 60);
            options = new Dictionary<string, Port.DodgeOption>();
            var criteria = new JsonObject();
            foreach (var option in requestSnapshot.AvailableActions)
            {
                string id = "m" + options.Count;
                options[id] = option; criteria[id] = Port.DecisionFacts.Describe(option, requestSnapshot, true);
            }
            var modes = new JsonObject();
            requestItems = new();
            foreach (var pair in weapons)
            {
                var item = self.inventory[pair.Value];
                requestItems[pair.Key] = (pair.Value, item.type, item.prefix);
                var aimChoices = new JsonObject();
                bool contact = ContactMode(pair.Key);
                if (contact)
                    aimChoices["body"] = new JsonObject { ["clear"] = Vector2.Distance(self.Center, target.Center) <= MeleeReach(self, target, item) && Clear(self.Center, target.Center),
                        ["flight_frames"] = 0, ["assumption"] = "Current contact reach only; pursuing does not require selecting this weapon." };
                else
                {
                    var aimKinds = new List<string> { "body", "velocity" };
                    if (history.ToJson()["horizontal_reversals"]?.GetValue<int>() > 0) aimKinds.Add("brake");
                    if (PvpTerrain.Landing(target, history.FallingAcceleration()).HasValue) aimKinds.Add("landing");
                    if (PvpTerrain.PlatformExit(target).HasValue) aimKinds.Add("exit");
                    foreach (string kind in aimKinds)
                    {
                        var solution = SolveShot(self, target, item, kind);
                        aimChoices[kind] = new JsonObject { ["clear"] = solution.HasValue, ["flight_frames"] = solution?.FlightFrames,
                            ["assumption"] = kind switch { "body" => "Target stays at current position.",
                                "brake" => "Target stops after 6 frames; hypothetical reversal coverage.",
                                "landing" => "Extrapolate measured falling acceleration to sampled support within 30 frames; no future jump/dash/mod physics is known.",
                                "exit" => "Cover a real nearby platform edge in the current travel direction; target may choose not to cross it.",
                                _ => "Target continues current velocity; a dash reversal can defeat this." },
                            ["trajectory_source"] = activeWeapon == item.type && observedType != 0 ? "observed_arrow_model" : "estimated_weapon_ammo_speed" };
                    }
                }
                modes[pair.Key] = new JsonObject { ["slot"] = pair.Value, ["name"] = item.Name, ["damage"] = self.GetWeaponDamage(item),
                    ["type"] = item.type, ["prefix"] = item.prefix, ["aims"] = aimChoices,
                    ["shoot_speed"] = item.shootSpeed, ["projectile_type"] = item.shoot,
                    ["can_attack_now"] = aimChoices.Any(aim => aim.Value["clear"]?.GetValue<bool>() == true),
                    ["contact_reach_pixels"] = contact ? MeleeReach(self, target, item) : (float?)null,
                    ["description"] = contact ? "接触近战，需要靠近并直接够到目标。" : "发射武器，需射界；追近/后退/跳跃都可以同时射击。" };
            }
            var movementState = JsonNode.Parse(Port.DecisionFacts.State(requestSnapshot, "")).AsObject();
            movementState.Remove("threats");
            movementState.Remove("boss_notes");
            var scene = new JsonObject
            {
                ["strategy"] = strategy, ["movement_options"] = criteria, ["weapon_modes"] = modes, ["current_mode"] = mode,
                ["directive"] = Directive.ToJson(), ["session"] = Session,
                ["candidate_rotation"] = sequence,
                ["perception"] = Perception(self, target, false, requestSnapshot), ["captured_tick"] = Main.GameUpdateCount,
                ["movement_state"] = movementState,
                ["dx"] = target.Center.X - self.Center.X, ["dy"] = target.Center.Y - self.Center.Y,
                ["target_vx"] = target.velocity.X, ["target_vy"] = target.velocity.Y,
                ["self_hp"] = self.statLife, ["target_hp"] = target.statLife,
                ["hp_change"] = self.statLife - lastHp, ["target_hp_change"] = target.statLife - lastTargetHp,
                ["previous_move"] = intent.ToString(), ["movement_result"] = movementReason,
                ["moved_pixels"] = Vector2.Distance(self.Center, lastPosition), ["aim_clear"] = lastAimClear,
                ["trajectory_observed"] = observedType != 0
            };
            lastHp = self.statLife; lastTargetHp = target.statLife;
            lastPosition = requestSelf = self.Center; requestTarget = target.Center;
            requestRevision = Directive.Revision;
            latestScene = (JsonObject)scene.DeepClone();
            DiagLog.Write("[pvp] scene " + scene.ToJsonString());
            requestClock.Restart(); pending = choose(scene, lifetime.Token);
        }
    }

    public static void ApplyInputs(Player self)
    {
        if (ExternalAction) return;
        if (!Active || self.dead || Main.player[Target].dead || waitingForRespawn || !AgentPermissions.Attack || !AgentPermissions.Dodge)
        { ClearControls(self); return; }
        if (appliedSnapshot != null)
        {
            var result = Movement.Apply(self, intent, appliedSnapshot with
            { CanDoubleJump = self.AnyExtraJumpUsable(), CanFly = self.wingTime > 0 });
            if (result.Reason != movementReason)
            {
                movementReason = result.Reason;
                if (result.Reason.Contains("blocked") || result.Reason.Contains("boundary")) Event("movement_blocked", result.Reason);
                DiagLog.Write($"[pvp] movement={result.Applied} reason={result.Reason} vel={self.velocity} mount={self.mount.Active}");
            }
        }
        else { Movement.Release(self); ClearControls(self); }
        var weapons = Weapons(self);
        if (weapons.Count == 0) { self.controlUseItem = false; lastAimClear = false; return; }
        if (!weapons.ContainsKey(mode)) foreach (string available in weapons.Keys) { mode = available; break; }
        int slot = weapons[mode];
        if (self.selectedItem != slot && self.itemAnimation == 0 && self.itemTime == 0)
        {
            self.selectedItem = slot;
            DiagLog.Write($"[pvp] weapon mode={mode} slot={slot} name={self.HeldItem.Name}");
        }
        lastAimClear = self.selectedItem == slot && Attack(self, Main.player[Target]);
        if (self.selectedItem != slot) self.controlUseItem = false;
    }
}
