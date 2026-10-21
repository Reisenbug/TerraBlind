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
using V = System.Numerics.Vector2;

namespace TerraBlind;

public static class PvpDuel
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };
    static CancellationTokenSource lifetime;
    static Task<string> pending;
    static Func<JsonObject, CancellationToken, Task<string>> choose;
    static readonly Stopwatch requestClock = new();
    static readonly Dictionary<int, (int Identity, Vector2 Velocity, int Samples)> arrows = new();
    static Vector2 requestSelf, requestTarget, lastPosition;
    static int frames, slot, weaponType, weaponPrefix, choiceAge, nextDecision, lastHp, lastTargetHp, observedType;
    static string key, strategy, choice;
    static float speed, gravity, gravityDelay, range;
    public static int Target { get; private set; } = -1;
    public static bool Active => Target >= 0;
    public static string Outcome { get; private set; } = "idle";
    public static int Decisions { get; private set; }
    static V N(Vector2 value) => new(value.X, value.Y);

    public static bool Start(int target, int seconds, int weaponSlot, float maxDistance, string instructions, out string reason,
        Func<JsonObject, CancellationToken, Task<string>> decide = null)
    {
        Stop();
        var self = Main.LocalPlayer;
        reason = PlayerCombat.TargetReason(self, target);
        if (reason == "" && (!self.active || self.dead)) reason = "self_dead";
        if (reason == "" && (!AgentPermissions.Attack || !AgentPermissions.Dodge)) reason = "combat_permission_denied";
        if (reason == "" && string.IsNullOrWhiteSpace(Config.I.TypeSafeKey)) reason = "missing_jev_key";
        if (reason == "" && (weaponSlot < 0 || weaponSlot > 9 || self.inventory[weaponSlot].IsAir
            || self.inventory[weaponSlot].useAmmo != AmmoID.Arrow)) reason = "prototype_requires_bow_in_hotbar";
        if (reason == "" && Vector2.Distance(self.Center, Main.player[target].Center) > maxDistance * 16) reason = "out_of_range";
        if (reason != "") { Outcome = reason; return false; }
        AgentTools.Stop();
        self.hostile = true;
        NetMessage.SendData(MessageID.TogglePVP, number: self.whoAmI);
        slot = weaponSlot; weaponType = self.inventory[slot].type; weaponPrefix = self.inventory[slot].prefix;
        frames = Math.Clamp(seconds, 1, 120) * 60; range = maxDistance * 16;
        key = Config.I.TypeSafeKey.Trim(); strategy = instructions;
        speed = Math.Max(1, self.inventory[slot].shootSpeed); gravity = gravityDelay = 0; observedType = 0;
        lastHp = self.statLife; lastTargetHp = Main.player[target].statLife; lastPosition = self.Center;
        choice = "hold"; choiceAge = 999; nextDecision = 0; Decisions = 0; arrows.Clear();
        foreach (var projectile in Main.projectile)
            if (projectile.active) arrows[projectile.whoAmI] = (projectile.identity, projectile.velocity, int.MaxValue);
        choose = decide ?? ((scene, token) => PvpTactics.Choose(Http, key, scene, token));
        lifetime = new CancellationTokenSource(); Target = target; Outcome = "running";
        DiagLog.Write($"[pvp] start target={target} slot={slot} weapon={weaponType} seconds={seconds} strategy={strategy}");
        return true;
    }

    public static void Stop(string outcome = "stopped")
    {
        if (Active)
        {
            var self = Main.LocalPlayer;
            self.controlLeft = self.controlRight = self.controlJump = self.controlUseItem = false;
            Outcome = outcome;
            DiagLog.Write($"[pvp] stop outcome={outcome} decisions={Decisions}");
        }
        Target = -1;
        lifetime?.Cancel(); lifetime?.Dispose(); lifetime = null;
        if (pending != null) _ = pending.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        pending = null; arrows.Clear();
    }

    static bool Clear(Vector2 from, Vector2 to) => Collision.CanHitLine(from, 4, 4, to, 4, 4);

    static bool CanMove(Player self, int direction, bool jump)
    {
        if (direction == 0) return true;
        Vector2 destination = new(self.position.X + direction * 24, self.position.Y);
        if (destination.X < 32 || destination.X + self.width > Main.maxTilesX * 16 - 32) return false;
        if (Collision.SolidCollision(destination, self.width, self.height) && !jump) return false;
        if (jump && Collision.SolidCollision(new Vector2(self.position.X, self.position.Y - 24), self.width, self.height)) return false;
        if (self.velocity.Y != 0) return true;
        for (int y = 0; y < 5; y++)
        {
            int tx = (int)((self.Center.X + direction * 32) / 16), ty = (int)((self.Bottom.Y + y * 16) / 16);
            if (WorldGen.InWorld(tx, ty) && Main.tile[tx, ty].HasUnactuatedTile
                && (Main.tileSolid[Main.tile[tx, ty].TileType] || Main.tileSolidTop[Main.tile[tx, ty].TileType])) return true;
        }
        return false;
    }

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

    public static void Tick(Player self)
    {
        if (!Active) return;
        string reason = PlayerCombat.TargetReason(self, Target);
        if (reason == "" && (!self.active || self.dead)) reason = "self_dead";
        if (reason == "" && (!AgentPermissions.Attack || !AgentPermissions.Dodge || !self.hostile)) reason = "combat_permission_denied";
        if (reason == "" && (self.inventory[slot].type != weaponType || self.inventory[slot].prefix != weaponPrefix)) reason = "weapon_changed";
        if (reason != "") { Stop(reason); return; }
        if (frames-- <= 0) { Stop("duration_complete"); return; }
        var target = Main.player[Target];
        if (Vector2.Distance(self.Center, target.Center) > range) { Stop("out_of_range"); return; }
        if (!self.HasAmmo(self.inventory[slot])) { Stop("no_ammo"); return; }
        self.selectedItem = slot;
        ObserveArrows(self);
        V? aim = PvpTactics.Aim(N(self.Center), N(target.Center), N(target.velocity), speed, gravity,
            (a, b) => Clear(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y)), gravityDelay);
        bool aimClear = aim.HasValue;
        Vector2 point = aim.HasValue ? new Vector2(aim.Value.X, aim.Value.Y) : target.Center;
        Cursor.AimPx(point.X, point.Y);
        self.direction = target.Center.X < self.Center.X ? -1 : 1;
        bool probe = observedType == 0 && Clear(self.Center, target.Center);
        self.controlUseItem = aimClear || probe;
        if (self.controlUseItem && self.itemAnimation == 0) self.releaseUseItem = true;

        if (pending?.IsCompleted == true)
        {
            try
            {
                string result = pending.GetAwaiter().GetResult();
                if (Array.IndexOf(PvpTactics.Choices, result) < 0) throw new InvalidOperationException("invalid_choice");
                if (requestClock.Elapsed.TotalSeconds <= 1.5 && Vector2.Distance(self.Center, requestSelf) < 160
                    && Vector2.Distance(target.Center, requestTarget) < 240)
                { choice = result; choiceAge = 0; Decisions++; }
                DiagLog.Write($"[pvp] choice={result} accepted={choiceAge == 0} ms={requestClock.ElapsedMilliseconds} self={self.Center} target={target.Center} speed={speed:0.##} gravity={gravity:0.###} aim_clear={aimClear}");
            }
            catch (Exception ex)
            {
                DiagLog.Write("[pvp] decision_failed " + ex.GetType().Name);
                Stop("jev_request_failed"); return;
            }
            pending = null;
        }
        if (pending == null && --nextDecision <= 0)
        {
            var allowed = new JsonArray();
            foreach (string candidate in PvpTactics.Choices)
                if (CanMove(self, PvpTactics.Direction(candidate, target.Center.X - self.Center.X), candidate.StartsWith("jump"))) allowed.Add(candidate);
            var threats = new JsonArray();
            foreach (var projectile in Main.projectile)
            {
                if (!projectile.active || projectile.damage <= 0 || projectile.owner == self.whoAmI) continue;
                bool enemyPlayer = projectile.friendly && projectile.owner >= 0 && projectile.owner < Main.maxPlayers
                    && Main.player[projectile.owner].active && Main.player[projectile.owner].hostile
                    && (self.team == 0 || self.team != Main.player[projectile.owner].team);
                if ((!projectile.hostile && !enemyPlayer) || Vector2.Distance(projectile.Center, self.Center) > 500) continue;
                threats.Add(new JsonObject { ["dx"] = projectile.Center.X - self.Center.X, ["dy"] = projectile.Center.Y - self.Center.Y,
                    ["vx"] = projectile.velocity.X * (projectile.extraUpdates + 1), ["vy"] = projectile.velocity.Y * (projectile.extraUpdates + 1) });
                if (threats.Count >= 12) break;
            }
            var scene = new JsonObject
            {
                ["strategy"] = strategy, ["allowed"] = allowed, ["dx"] = target.Center.X - self.Center.X,
                ["dy"] = target.Center.Y - self.Center.Y, ["target_vx"] = target.velocity.X, ["target_vy"] = target.velocity.Y,
                ["self_vx"] = self.velocity.X, ["self_vy"] = self.velocity.Y,
                ["self_hp"] = self.statLife, ["target_hp"] = target.statLife,
                ["hp_change"] = self.statLife - lastHp, ["target_hp_change"] = target.statLife - lastTargetHp,
                ["previous_choice"] = choice, ["moved_pixels"] = Vector2.Distance(self.Center, lastPosition),
                ["aim_clear"] = aimClear, ["nearby_projectiles"] = threats,
                ["weapon"] = self.inventory[slot].Name, ["trajectory_observed"] = observedType != 0
            };
            lastHp = self.statLife; lastTargetHp = target.statLife; lastPosition = requestSelf = self.Center; requestTarget = target.Center;
            DiagLog.Write("[pvp] scene " + scene.ToJsonString());
            nextDecision = 24; requestClock.Restart();
            pending = choose(scene, lifetime.Token);
        }
        choiceAge++;
        int horizontal = choiceAge <= 60 ? PvpTactics.Direction(choice, target.Center.X - self.Center.X) : 0;
        bool jump = choiceAge <= 60 && choice.StartsWith("jump") && self.velocity.Y == 0 && !self.controlJump;
        if (!CanMove(self, horizontal, jump)) { horizontal = 0; nextDecision = 0; }
        self.controlLeft = horizontal < 0; self.controlRight = horizontal > 0;
        self.controlJump = jump;
    }
}
