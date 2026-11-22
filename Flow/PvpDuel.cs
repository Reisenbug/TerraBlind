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
    static Port.GameMovement Movement = NewMovement();
    static Port.GameMovement NewMovement() => new() { CommitGrapple = true };
    static CancellationTokenSource lifetime;
    static Task<PvpTactics.Decision> pending;
    static Func<JsonObject, CancellationToken, Task<PvpTactics.Decision>> choose;
    static readonly Stopwatch requestClock = new();
    static Dictionary<string, Port.DodgeOption> options;
    static Dictionary<string, (int Slot, int Type, int Prefix)> requestItems;
    static Port.CombatSnapshot requestSnapshot, appliedSnapshot;
    static Port.DodgeIntent intent = Port.DodgeIntent.Idle;
    static Vector2 requestSelf, requestTarget, lastPosition;
    static int frames, preferredSlot, choiceAge, lastHp, lastTargetHp;
    static bool lastAimClear, fireIntent = true;
    static string aimKind = "velocity";
    static int plannedType, plannedPrefix;
    static int observedHp;
    static string key, strategy, mode, movementReason, attackReason;
    static ulong sequence;
    static float range;
    static bool waitingForRespawn;
    readonly record struct CoverCell(int X, int Y, int Slot, int Item, int Tile);
    static CoverCell? offeredCover, queuedCover, activeCover;
    static long coverLease, coverRevision;
    static ulong coverDeadline, coverRetryTick, coverQueueDeadline;
    static ulong moveSequence;
    static Vector2 moveFrom;
    public static bool UntilStopped { get; private set; }
    public static int Target { get; private set; } = -1;
    public static bool Active => Target >= 0;
    public static string Outcome { get; private set; } = "idle";
    public static int Decisions { get; private set; }
    static V N(Vector2 value) => new(value.X, value.Y);
    static bool IsCombatWeapon(Item item) => !item.IsAir && item.damage > 0 && item.useStyle > 0
        && item.ammo <= 0 && item.pick <= 0 && item.axe <= 0 && item.hammer <= 0;

    static Dictionary<string, int> Weapons(Player self)
    {
        var result = new Dictionary<string, int>();
        for (int index = 0; index < 10; index++)
        {
            int slot = (preferredSlot + index) % 10;
            var item = self.inventory[slot];
            if (Directive != null && !Directive.AllowsWeapon(item.type)) continue;
            if (!IsCombatWeapon(item)
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
        Movement = NewMovement();
        self.hostile = true;
        NetMessage.SendData(MessageID.TogglePVP, number: self.whoAmI);
        mode = weapons.ContainsKey("ranged") ? "ranged" : "melee";
        self.selectedItem = weapons[mode];
        aimKind = "velocity"; fireIntent = true; plannedType = self.HeldItem.type; plannedPrefix = self.HeldItem.prefix;
        UntilStopped = untilStopped;
        frames = Math.Clamp(seconds, 1, 120) * 60; range = maxDistance * 16;
        key = Config.I.TypeSafeKey.Trim(); strategy = instructions;
        lastHp = self.statLife; lastTargetHp = Main.player[target].statLife; lastPosition = self.Center;
        observedHp = self.statLife;
        coverRetryTick = 0;
        moveSequence = 0;
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
        if (coverLease != 0) ItemUseCoordinator.Stop();
        coverLease = 0; activeCover = offeredCover = queuedCover = null;
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
        pending = null; appliedSnapshot = null;
    }

    static bool Clear(Vector2 from, Vector2 to) => Collision.CanHitLine(from, 4, 4, to, 4, 4);

    static float MeleeReach(Player self, Player target, Item item)
        => Math.Max(32, Math.Max(item.width, item.height) * item.scale + self.width / 2f) + target.width / 2f;

    readonly record struct ShotWeapon(int Projectile, PvpTactics.ShotFlight Flight, int Width, int Height, bool ThroughTiles, int Ammo);

    static ShotWeapon? ReadShot(Player self, Item item)
    {
        float shotSpeed = item.shootSpeed;
        int shotType = item.shoot;
        int ammo = 0;
        if (item.useAmmo > 0 && !self.PickAmmo(item, out shotType, out shotSpeed, out _, out _, out ammo, true)) return null;
        if (shotType == ProjectileID.WoodenArrowFriendly && item.type == ItemID.MoltenFury)
            shotType = ProjectileID.FireArrow;
        if (shotType == ProjectileID.Bullet && item.type is ItemID.Uzi or ItemID.SniperRifle or ItemID.VenusMagnum)
            shotType = ProjectileID.BulletHighVelocity;
        if (shotType <= 0 || shotSpeed <= 0) return null;
        if (shotType is ProjectileID.DeathSickle or ProjectileID.IceSickle && !ProjectileID.Sets.NoMeleeSpeedVelocityScaling[shotType])
            shotSpeed *= self.GetTotalAttackSpeed(Terraria.ModLoader.DamageClass.Melee);
        ContentSamples.ProjectilesByType.TryGetValue(shotType, out var defaults);
        int updates = 1 + (defaults?.extraUpdates ?? 0);
        int steps = Math.Min(90 * updates, defaults?.timeLeft ?? int.MaxValue);
        var flight = new PvpTactics.ShotFlight(shotSpeed, updates, steps);
        flight = shotType switch
        {
            ProjectileID.WoodenArrowFriendly or ProjectileID.FireArrow or ProjectileID.UnholyArrow or ProjectileID.HellfireArrow
                or ProjectileID.CursedArrow or ProjectileID.IchorArrow or ProjectileID.VenomArrow => flight with { GravityStart = 15, Gravity = .1f },
            ProjectileID.FrostburnArrow => flight with { GravityStart = 17, Gravity = .085f },
            ProjectileID.BoneArrow => flight with { GravityStart = 35, Gravity = .06f },
            ProjectileID.ThrowingKnife or ProjectileID.Shuriken => flight with { GravityStart = 20, Gravity = .4f, DragX = .97f, FallLimit = 16 },
            ProjectileID.DeathSickle => flight with { DragX = .96f, DragY = .96f },
            ProjectileID.IceSickle => flight with { DragX = .95f, DragY = .95f },
            _ => flight
        };
        bool through = !item.channel && defaults is { tileCollide: false, ownerHitCheck: false }
            && (shotType == ProjectileID.DeathSickle || defaults.aiStyle is 0 or 1);
        return new(shotType, flight, defaults?.width ?? 4, defaults?.height ?? 4, through, ammo);
    }

    static PvpTactics.ShotSolution? SolveShot(Player self, Player target, ShotWeapon shot, string kind)
    {
        Vector2 origin = self.RotatedRelativePoint(self.MountedCenter);
        V center = N(target.Center), velocity = N(target.velocity);
        V reach = new((target.width + shot.Width) * .5f, (target.height + shot.Height) * .5f);
        Func<V, V, bool> clear = shot.ThroughTiles ? null
            : (from, to) => PvpTerrain.ProjectileClear(new(from.X, from.Y), new(to.X, to.Y), shot.Width, shot.Height);
        var landing = (kind is "landing" or "coverage") ? PvpTerrain.Landing(target, history.FallingAcceleration()) : null;
        V Future(float frames, string motion) => motion switch
        {
            "body" => center,
            "brake" => center + velocity * Math.Min(frames, 6),
            "landing" when landing.HasValue && frames >= landing.Value.Frames => N(landing.Value.Point),
            "blend" => center + velocity * ((frames + Math.Min(frames, 6)) * .5f),
            _ => center + velocity * frames
        };
        if (kind == "exit")
        {
            var exit = PvpTerrain.PlatformExit(target);
            if (!exit.HasValue || Math.Abs(velocity.X) < .5f) return null;
            float crossing = Math.Abs(exit.Value.X - center.X) / Math.Abs(velocity.X);
            var shotAtExit = PvpTactics.SolveFlight(N(origin), shot.Flight, reach, _ => N(exit.Value), clear, Math.Max(0, crossing - 4));
            return shotAtExit.HasValue && shotAtExit.Value.FlightFrames <= crossing + 4 ? shotAtExit : null;
        }
        if (kind == "landing" && !landing.HasValue) return null;
        PvpTactics.ShotSolution? Solve(string motion) => PvpTactics.SolveFlight(N(origin), shot.Flight, reach,
            frames => Future(frames, motion), clear, motion == "landing" ? landing.Value.Frames : 0);
        if (kind != "coverage") return Solve(kind);
        var motions = landing.HasValue ? new[] { "velocity", "brake", "landing" } : new[] { "velocity", "brake" };
        PvpTactics.ShotSolution? best = null;
        int bestCoverage = 0;
        foreach (string motion in motions.Append("blend"))
        {
            var solution = Solve(motion);
            if (!solution.HasValue) continue;
            int coverage = motions.Count(alternative =>
            {
                V miss = solution.Value.Impact - Future(solution.Value.FlightFrames, alternative);
                return Math.Abs(miss.X) <= reach.X && Math.Abs(miss.Y) <= reach.Y;
            });
            if (coverage <= bestCoverage) continue;
            best = solution;
            bestCoverage = coverage;
            if (coverage == motions.Length) break;
        }
        return best;
    }

    static bool ContactMode(string value) => value.StartsWith("melee", StringComparison.Ordinal);

    static CoverCell? FindCover(Player self, Player target, Dictionary<string, int> weapons)
    {
        if (Main.GameUpdateCount < coverRetryTick || self.grapCount > 0 || Movement.GrappleActive
            || Main.mouseItem?.IsAir == false) return null;
        int slot = -1;
        for (int i = 0; i < 10; i++)
        {
            var block = self.inventory[i];
            int type = block.createTile;
            if (!block.IsAir && type >= 0 && Main.tileSolid[type]
                && !Main.tileSolidTop[type] && !TileID.Sets.Falling[type])
            { slot = i; break; }
        }
        if (slot < 0 || !weapons.Values.Any(weapon => ReadShot(self, self.inventory[weapon])?.ThroughTiles == true)) return null;
        int enemyShot = target.HeldItem.shoot;
        if (enemyShot <= 0 || !ContentSamples.ProjectilesByType.TryGetValue(enemyShot, out var incoming)
            || !incoming.tileCollide || incoming.aiStyle is not (0 or 1))
            return null;
        var cell = PvpTerrain.FindCoverCell(self, target, (x, y) => CanPlaceCover(self, x, y));
        return cell.HasValue ? new CoverCell(cell.Value.X, cell.Value.Y, slot, self.inventory[slot].type,
            self.inventory[slot].createTile) : null;
    }

    static bool CanPlaceCover(Player self, int x, int y) => Directive.AllowsTerrain(true, x, y)
        && AgentTools.ControlAllows(ControlBehavior.Placement, ControlChannels.ItemUse, delegated: true)
        && !AgentTools.Control.ForbiddenAt(ControlBehavior.Placement, x, y) && Reach.CanPlace(self, x, y);

    static bool TryPlaceCover(Player self, Player target, CoverCell cell)
    {
        if (Movement.GrappleActive || self.grapCount > 0
            || self.itemAnimation > 0 || self.itemTime > 0 || ItemUseCoordinator.IsActive
            || Main.mouseItem?.IsAir == false || self.inventory[cell.Slot].IsAir || self.inventory[cell.Slot].type != cell.Item
            || !CanPlaceCover(self, cell.X, cell.Y) || !PvpTerrain.CanCoverCell(self, target, cell.X, cell.Y)
            || !PvpTerrain.WalkProbe(self, Math.Sign(self.Center.X - target.Center.X)).HasValue
            || !PvpTerrain.DirectShotClear(self.Center, target.Center)
            || !Weapons(self).Values.Any(slot => ReadShot(self, self.inventory[slot])?.ThroughTiles == true)) return false;
        coverLease = BeginAction("pvp_cover");
        activeCover = cell;
        coverDeadline = Main.GameUpdateCount + (ulong)Math.Clamp(self.inventory[cell.Slot].useTime + 12, 20, 60);
        ItemUseCoordinator.Start(new ItemUseRequest { TargetWx = cell.X, TargetWy = cell.Y, Slot = cell.Slot,
            DurationTicks = (int)(coverDeadline - Main.GameUpdateCount), Strict = true });
        DiagLog.Write($"[pvp-cover] session={Session} action={coverLease} state=started x={cell.X} y={cell.Y} tile={cell.Tile}");
        return true;
    }

    static void CheckCover(Player self, Player target)
    {
        if (activeCover is not CoverCell cell) return;
        var tile = Main.tile[cell.X, cell.Y];
        bool placed = tile.HasUnactuatedTile && tile.TileType == cell.Tile;
        if (ItemUseCoordinator.IsActive && !placed && !self.dead && !target.dead && Main.GameUpdateCount < coverDeadline
            && CanPlaceCover(self, cell.X, cell.Y)) return;
        ItemUseCoordinator.Stop();
        string result = placed ? "cover_present" : "cover_missing";
        DiagLog.Write($"[pvp-cover] session={Session} action={coverLease} state={result} " +
            $"x={cell.X} y={cell.Y} direct_line={PvpTerrain.DirectShotClear(self.Center, target.Center)}");
        activeCover = null;
        coverRetryTick = Main.GameUpdateCount + 90;
        long action = coverLease;
        coverLease = 0;
        var snapshot = appliedSnapshot;
        bool resumeFire = fireIntent;
        EndAction(action);
        appliedSnapshot = snapshot;
        fireIntent = resumeFire;
    }

    static bool Attack(Player self, Player target)
    {
        self.controlUseItem = false;
        var item = self.HeldItem;
        Vector2 point = target.Center;
        PvpTactics.ShotSolution? solution = null;
        bool clear;
        if (ContactMode(mode))
        {
            clear = Vector2.Distance(self.Center, target.Center) <= MeleeReach(self, target, item) && Clear(self.Center, target.Center);
        }
        else
        {
            if (ReadShot(self, item) is ShotWeapon shot) solution = SolveShot(self, target, shot, aimKind);
            clear = solution.HasValue;
            if (solution.HasValue) point = new(solution.Value.Point.X, solution.Value.Point.Y);
        }
        if (!self.controlHook) Cursor.AimPx(point.X, point.Y);
        self.direction = target.Center.X < self.Center.X ? -1 : 1;
        self.controlUseItem = clear && !self.controlHook && fireIntent && queuedCover == null
            && item.type == plannedType && item.prefix == plannedPrefix;
        if (self.controlUseItem) history.Aim(item, aimKind, point, solution);
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
        if (coverLease != 0) CheckCover(self, Main.player[Target]);
        if (self.dead || Main.player[Target].dead)
        {
            queuedCover = null;
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
            Movement = NewMovement(); choiceAge = 999;
            attackReason = "";
            lastHp = self.statLife; lastTargetHp = Main.player[Target].statLife; lastPosition = self.Center;
            NativeAgent.Progress("复活了，继续对战。", "执行 duel_player");
            DiagLog.Write($"[pvp] resumed target={Target} remaining_frames={frames}");
        }
        if (ExternalAction) return;
        if (!UntilStopped && frames-- <= 0) { Stop("duration_complete"); return; }
        var target = Main.player[Target];
        if (queuedCover is CoverCell queued)
        {
            if (coverRevision != Directive.Revision || Main.GameUpdateCount >= coverQueueDeadline) queuedCover = null;
            else if (self.itemAnimation == 0 && self.itemTime == 0)
            {
                queuedCover = null;
                if (TryPlaceCover(self, target, queued)) return;
                coverRetryTick = Main.GameUpdateCount + 30;
            }
        }
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
        if (pending == null || pending.IsCompleted) RefreshLoadout(self);
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
                    if (result.Cover && offeredCover is CoverCell cover)
                    {
                        if (TryPlaceCover(self, target, cover))
                        {
                            Decisions++;
                            return;
                        }
                        if (self.itemAnimation > 0 || self.itemTime > 0)
                        {
                            queuedCover = cover;
                            coverRevision = Directive.Revision;
                            coverQueueDeadline = Main.GameUpdateCount + (ulong)Math.Clamp(Math.Max(self.itemAnimation, self.itemTime) + 3, 4, 60);
                            pending = null;
                            Decisions++;
                            return;
                        }
                        coverRetryTick = Main.GameUpdateCount + 30;
                    }
                    if (moveSequence != 0)
                        DiagLog.Write($"[pvp-move] session={Session} sequence={moveSequence} state=observed " +
                            $"from={moveFrom} to={self.Center} reason={movementReason} shot_clear={lastAimClear}");
                    intent = selected.Intent; appliedSnapshot = requestSnapshot;
                    moveSequence = requestSnapshot.Sequence;
                    moveFrom = self.Center;
                    DiagLog.Write($"[pvp-move] session={Session} sequence={moveSequence} state=issued " +
                        $"at={moveFrom} move={intent} anchor={selected.HookTarget}");
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
        if (pending == null && queuedCover == null)
        {
            requestSnapshot = Sensor.CapturePlayer(self, target, ++sequence, intent, choiceAge * 1000 / 60);
            options = new Dictionary<string, Port.DodgeOption>();
            var criteria = new JsonObject();
            var left = PvpTerrain.WalkProbe(self, -1);
            var right = PvpTerrain.WalkProbe(self, 1);
            foreach (var option in requestSnapshot.AvailableActions)
            {
                string id = "m" + options.Count;
                options[id] = option;
                string description = Port.DecisionFacts.Describe(option, requestSnapshot, true);
                Vector2? step = option.Intent is { HorizontalSize: Port.Magnitude.Small, VerticalSize: Port.Magnitude.None }
                    ? option.Intent.Direction == Port.DodgeDirection.Left ? left : right : null;
                if (step.HasValue)
                    description += $" Verified 3-tile ground walk to ({step.Value.X:0},{step.Value.Y:0}); direct line there: " +
                        $"{(PvpTerrain.DirectShotClear(step.Value, target.Center) ? "open" : "blocked")}.";
                criteria[id] = description;
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
                    if (target.velocity.LengthSquared() > 1f) aimKinds.Add("coverage");
                    if (PvpTerrain.Landing(target, history.FallingAcceleration()).HasValue) aimKinds.Add("landing");
                    if (PvpTerrain.PlatformExit(target).HasValue) aimKinds.Add("exit");
                    var shot = ReadShot(self, item);
                    foreach (string kind in aimKinds)
                    {
                        var solution = shot.HasValue ? SolveShot(self, target, shot.Value, kind) : null;
                        aimChoices[kind] = new JsonObject { ["clear"] = solution.HasValue, ["flight_frames"] = solution?.FlightFrames,
                            ["through_tiles"] = shot?.ThroughTiles ?? false,
                            ["assumption"] = kind switch { "body" => "Target stays at current position.",
                                "coverage" => "Compare continuing, braking and any supported landing. Prefer aim covering multiple possibilities.",
                                "landing" => "Extrapolate measured falling acceleration to sampled support within 30 frames; no future jump/dash/mod physics is known.",
                                "exit" => "Cover a real nearby platform edge in the current travel direction; target may choose not to cross it.",
                                _ => "Target continues current velocity; a dash reversal can defeat this." },
                            ["projectile_type"] = shot?.Projectile };
                    }
                }
                modes[pair.Key] = new JsonObject { ["slot"] = pair.Value, ["name"] = item.Name, ["damage"] = self.GetWeaponDamage(item),
                    ["type"] = item.type, ["prefix"] = item.prefix, ["aims"] = aimChoices,
                    ["shoot_speed"] = item.shootSpeed, ["projectile_type"] = item.shoot,
                    ["can_attack_now"] = aimChoices.Any(aim => aim.Value["clear"]?.GetValue<bool>() == true),
                    ["contact_reach_pixels"] = contact ? MeleeReach(self, target, item) : (float?)null,
                    ["description"] = contact ? "接触近战，需要靠近并直接够到目标。" : "发射武器，需射界；追近/后退/跳跃都可以同时射击。" };
            }
            var movementState = JsonNode.Parse(Port.DecisionFacts.State(requestSnapshot, "", AgentTools.Control.Snapshot())).AsObject();
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
                ["trajectory_observed"] = history.HasObservedShot
            };
            offeredCover = FindCover(self, target, weapons);
            if (offeredCover is CoverCell cover)
                scene["cover_option"] = new JsonObject { ["x"] = cover.X, ["y"] = cover.Y,
                    ["item"] = self.inventory[cover.Slot].Name,
                    ["fact"] = "Blocks the current direct line; a three-tile walk away remains clear. A compatible through-wall weapon is available." };
            lastHp = self.statLife; lastTargetHp = target.statLife;
            lastPosition = requestSelf = self.Center; requestTarget = target.Center;
            requestRevision = Directive.Revision;
            latestScene = (JsonObject)scene.DeepClone();
            if (DiagLog.Trace) DiagLog.Trc("[pvp] scene " + scene.ToJsonString());
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
