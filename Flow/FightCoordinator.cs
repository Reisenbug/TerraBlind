using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using System.Collections.Generic;
using System;
using Microsoft.Xna.Framework;

namespace TerraBlind
{
    public static class FightCoordinator
    {
        private static volatile bool _active;
        private static volatile int _framesLeft;
        private static float _maxDistTiles = 20f;
        private const int TimeoutFrames = 360;
        private static HashSet<int> _types;
        private static NPC _actionTarget;
        private static bool _endWithoutTarget;
        internal static bool EndedWithoutTarget { get; private set; }
        private static bool _idleDefense;
        internal static bool NavigationSnapshotPending;
        private static bool _usingItem;
        private static bool _evading;
        private static bool _evadeJumpHeld;
        private static int _animationSlot = -1;
        private static Vector2 _animationAim;
        private static NPC _animationTarget;
        private static bool _animationAttack, _animationSeen, _attackNext;
        private static readonly Dictionary<int, Vector2> _selfBlastSizes = new();
        public static Vector2? AimTarget { get; private set; }
        internal static bool OwnsUseInput => _usingItem;
        public static bool AttackAnimationActive => _animationSlot >= 0 && _animationAttack && Main.LocalPlayer.itemAnimation > 0;
        static bool ApproachingHellSite => AgentTools.ControlSession.Tool == "hell_run" && RecedingNav.Active;

        static void RememberAnimation(Player p, bool attack)
        {
            if (!p.controlUseItem || p.itemAnimation != 0 || p.itemTime != 0) return;
            _animationSlot = p.selectedItem;
            _animationAim = new Vector2(Main.mouseX + Main.screenPosition.X, Main.mouseY + Main.screenPosition.Y);
            _animationAttack = attack;
            _animationSeen = false;
        }

        internal static bool NeedsClearLine(Item item) => item.shoot <= 0
            || !ContentSamples.ProjectilesByType.TryGetValue(item.shoot, out var shot) || shot.tileCollide || shot.ownerHitCheck;

        internal static bool CanHitTarget(Player player, Item weapon, Vector2 target, float targetRadius = 0)
            => CanHitTargetFrom(player, weapon, player.Center, target, targetRadius);

        internal static Vector2 SelfBlastSize(int type)
        {
            if (!ContentSamples.ProjectilesByType.TryGetValue(type, out var shot)
                || shot.aiStyle != ProjAIStyleID.Explosive && !ProjectileID.Sets.Explosive[type]
                || ProjectileID.Sets.RocketsSkipDamageForPlayers[type]) return Vector2.Zero;
            if (_selfBlastSizes.TryGetValue(type, out var size)) return size;
            var blast = new Projectile(); blast.SetDefaults(type); blast.owner = Main.myPlayer;
            blast.PrepareBombToBlow();
            return _selfBlastSizes[type] = new Vector2(blast.width, blast.height);
        }

        internal static bool CanHitTargetFrom(Player player, Item weapon, Vector2 source, Vector2 target, float targetRadius = 0)
        {
            if (weapon.shoot <= 0)
                return !weapon.noMelee && Vector2.Distance(source, target)
                    <= Math.Max(32, Math.Max(weapon.width, weapon.height) * weapon.scale + player.width / 2f) + targetRadius
                    && Collision.CanHitLine(source, 4, 4, target, 4, 4);
            int shotType = weapon.shoot;
            if (weapon.useAmmo > 0 && !player.PickAmmo(weapon, out shotType, out _, out _, out _, out _, true)) return false;
            var blast = SelfBlastSize(shotType);
            if (blast != Vector2.Zero && Math.Abs(source.X - target.X) <= (blast.X + player.width) / 2 + targetRadius
                && Math.Abs(source.Y - target.Y) <= (blast.Y + player.height) / 2 + targetRadius) return false;
            if (ContentSamples.ProjectilesByType.TryGetValue(shotType, out var shot))
            {
                if (shot.aiStyle == ProjAIStyleID.ShortSword && Vector2.Distance(source, target)
                    > weapon.shootSpeed * 14 + new Vector2(shot.width, shot.height).Length() * weapon.scale / 2 + targetRadius)
                    return false;
                if (shot.aiStyle == ProjAIStyleID.Spear && Vector2.Distance(source, target)
                    > SpearReach(player, weapon, shot, shotType) + targetRadius) return false;
                if (!shot.tileCollide && !shot.ownerHitCheck) return true;
            }
            return Collision.CanHitLine(source, 4, 4, target, 4, 4);
        }

        static float SpearReach(Player player, Item weapon, Projectile shot, int type)
        {
            var (initial, extension) = type switch
            {
                46 => (3f, 1.4f), 105 => (3f, 2.1f), 367 => (3f, 1.5f), 368 => (3f, 1.4f),
                47 => (4f, .9f), 153 => (4f, 1.3f), 49 => (4f, .85f),
                64 or 215 => (3f, 1.7f), 66 or 97 or 212 or 218 => (3f, 1.9f),
                877 or 878 or 879 => (28f, .4f),
                _ => (float.PositiveInfinity, 0f)
            };
            int duration = CombinedHooks.TotalAnimationTime(weapon.useAnimation, player, weapon);
            float speed = player.GetTotalAttackSpeed(DamageClass.Melee);
            float velocity = weapon.shootSpeed * (ProjectileID.Sets.NoMeleeSpeedVelocityScaling[type] ? 1 : speed);
            float body = new Vector2(shot.width, shot.height).Length() / 2;
            float tip = type switch
            {
                46 => 10 + 50 * speed + 25 * MathF.Sqrt(2) / 2,
                105 => 10 + 50 * speed + 30 * MathF.Sqrt(2) / 2,
                153 => 10 + 40 * speed + 20 * MathF.Sqrt(2) / 2,
                _ => 0
            };
            return velocity * (initial + extension * (duration - duration / 3)) + Math.Max(body, tip);
        }

        internal static bool TempleTargetAllowed(Player player, NPC npc)
            => player.ZoneLihzhardTemple || npc.type is not (NPCID.Lihzahrd or NPCID.LihzahrdCrawler or NPCID.FlyingSnake);

        internal static bool HoldingFallProtection(Player player)
            => player.velocity.Y * player.gravDir > 0
                && (player.HeldItem.type is ItemID.Umbrella or ItemID.TragicUmbrella
                    || (IsActive || NativeAgent.Processing || RecedingNav.Active) && !player.AnyExtraJumpUsable()
                        && FallSafety.LandingDamage(player, player.position, player.fallStart) > 0);

        internal static (int Direction, bool Jump, bool Apply) Evade(Player p, NPC target = null, bool projectilesOnly = false)
        {
            bool blocked = target != null && NeedsClearLine(p.HeldItem)
                && !Collision.CanHitLine(p.Center, 4, 4, target.Center, 4, 4);
            var shots = new List<(Projectile Shot, Vector2[] Path)>();
            var enemies = new List<NPC>();
            foreach (var shot in Main.projectile)
                if (shot.active && shot.hostile && shot.damage > 0)
                    shots.Add((shot, shot.type == ProjectileID.Boulder ? BoulderPath(shot) : null));
            foreach (var npc in Main.npc)
                if (npc.active && !npc.friendly && !npc.townNPC && npc.damage > 0
                    && Vector2.Distance(npc.Center, p.Center) < 600)
                {
                    bool imminent = !projectilesOnly;
                    for (int frame = 0; !imminent && frame <= 18; frame++)
                    {
                        var relative = npc.Center - p.Center + (npc.velocity - p.velocity) * frame;
                        imminent = Math.Abs(relative.X) < (p.width + npc.width) / 2f + 4
                            && Math.Abs(relative.Y) < (p.height + npc.height) / 2f + 4;
                    }
                    if (imminent) enemies.Add(npc);
                }
            int currentDirection = p.controlRight ? 1 : p.controlLeft ? -1 : 0;
            bool canStartJump = p.velocity.Y == 0 && p.releaseJump;
            bool canHoldJump = p.jump > 0 && p.velocity.Y * p.gravDir < 0;
            bool holdEvade = _evadeJumpHeld && canHoldJump;
            bool currentJump = holdEvade || p.controlJump && (canStartJump || canHoldJump);
            if (!blocked && shots.Count == 0 && enemies.Count == 0) return (currentDirection, holdEvade, holdEvade);
            bool checkFall = RecedingNav.Active && !FallSafety.Immune(p);
            bool checkLava = !p.lavaImmune && p.lavaTime <= 0;
            bool escapingLava = checkLava && Collision.LavaCollision(p.position, p.width, p.height);
            float Risk(int direction, bool jump)
            {
                Vector2 position = p.Center, velocity = p.velocity;
                var halfSize = new Vector2(p.width / 2f, p.height / 2f);
                int fallStart = p.velocity.Y * p.gravDir > 0 ? p.fallStart : (int)(p.position.Y / 16);
                bool clearingContact = Collision.HurtTiles(p.position, p.width, p.height, p).type >= 0;
                if (jump && canStartJump) velocity.Y = (-Player.jumpSpeed - p.jumpSpeedBoost) * p.gravDir;
                int hold = !jump ? 0 : canStartJump ? Player.jumpHeight : p.jump;
                bool landed = false;
                bool inLava = escapingLava;
                float score = direction == currentDirection ? 0 : .01f;
                if (jump != currentJump) score += .02f;
                for (int frame = 1; frame <= (checkFall ? 96 : 24); frame++)
                {
                    if (frame > 24 && landed) break;
                    float desired = direction * Math.Max(p.maxRunSpeed, p.accRunSpeed);
                    velocity.X += Math.Clamp(desired - velocity.X, -p.runAcceleration, p.runAcceleration);
                    if (hold > 0 && velocity.Y * p.gravDir < 0)
                    { velocity.Y = (-Player.jumpSpeed - p.jumpSpeedBoost) * p.gravDir; hold--; }
                    velocity.Y += p.gravity * p.gravDir;
                    if (velocity.Y * p.gravDir > p.maxFallSpeed) velocity.Y = p.maxFallSpeed * p.gravDir;
                    bool up = Collision.up, down = Collision.down;
                    Vector2 movement;
                    try { movement = Collision.TileCollision(position - halfSize, velocity, p.width, p.height,
                        p.controlDown, p.controlDown, (int)p.gravDir); }
                    finally { Collision.up = up; Collision.down = down; }
                    position += movement;
                    if (checkLava)
                    {
                        bool touchesLava = Collision.LavaCollision(position - halfSize, p.width, p.height);
                        if (touchesLava && !inLava) return float.PositiveInfinity;
                        if (touchesLava) score += 25000;
                        inLava = touchesLava;
                    }
                    if (movement.X != velocity.X) velocity.X = 0;
                    if (checkFall)
                    {
                        landed = movement.Y * p.gravDir < velocity.Y * p.gravDir;
                        if (landed && FallSafety.LandingDamage(p, position - halfSize, fallStart) >= p.statLife)
                            return float.PositiveInfinity;
                        fallStart = FallSafety.ResetDistance(p, position - halfSize, fallStart, landed ? 0 : movement.Y, frame);
                    }
                    if (movement.Y != velocity.Y) velocity.Y = 0;
                    if (frame > 24) continue;
                    bool contact = Collision.HurtTiles(position - halfSize, p.width, p.height, p).type >= 0;
                    if (contact && !clearingContact) return float.PositiveInfinity;
                    if (contact) score += (25 - frame) * 1000;
                    else clearingContact = false;
                    if (!AgentTools.Control.BoundsAllowed(ControlBehavior.Dodge,
                        (position.X - p.width / 2f) / 16, (position.Y - p.height / 2f) / 16, p.width / 16f, p.height / 16f))
                        return float.PositiveInfinity;
                    if (blocked && !Collision.CanHitLine(position, 4, 4, target.Center + target.velocity * frame, 4, 4)) score++;
                    foreach (var (shot, path) in shots)
                    {
                        Vector2 point = path == null ? shot.Center + shot.velocity * ((shot.extraUpdates + 1) * frame) : path[frame - 1];
                        if (Math.Abs(point.X - position.X) < (p.width + shot.width) / 2f + 6
                            && Math.Abs(point.Y - position.Y) < (p.height + shot.height) / 2f + 6)
                            score += (25 - frame) * 100;
                    }
                    foreach (var npc in enemies)
                    {
                        Vector2 point = npc.Center + npc.velocity * frame;
                        if (Math.Abs(point.X - position.X) < (p.width + npc.width) / 2f + 4
                            && Math.Abs(point.Y - position.Y) < (p.height + npc.height) / 2f + 4)
                            score += (25 - frame) * 100;
                    }
                }
                if (checkFall && velocity.Y * p.gravDir > 0
                    && FallSafety.LandingDamage(p, position - halfSize, fallStart) >= p.statLife)
                    return float.PositiveInfinity;
                return score;
            }
            (int Direction, bool Jump, bool Apply) best = (currentDirection, currentJump, holdEvade);
            float bestRisk = Risk(currentDirection, currentJump);
            foreach (int direction in new[] { -1, 0, 1 })
            foreach (bool jump in new[] { false, true })
            {
                if (jump && !canStartJump && !canHoldJump) continue;
                float risk = Risk(direction, jump);
                if (risk < bestRisk) { bestRisk = risk; best = (direction, jump, true); }
            }
            return best;
        }

        static Vector2[] BoulderPath(Projectile shot)
        {
            var path = new Vector2[24];
            var center = shot.Center;
            var velocity = shot.velocity;
            var halfSize = new Vector2(shot.width / 2f, shot.height / 2f);
            bool up = Collision.up, down = Collision.down;
            try
            {
                for (int frame = 0; frame < path.Length; frame++)
                {
                    if (velocity.Y <= 6f && Math.Abs(velocity.X) < 7f)
                        velocity.X += Math.Sign(velocity.X) * .05f;
                    velocity.Y = Math.Min(velocity.Y, 16f) + .3f;
                    var movement = shot.tileCollide
                        ? Collision.TileCollision(center - halfSize, velocity, shot.width, shot.height)
                        : velocity;
                    center += movement;
                    if (movement.X != velocity.X) velocity.X = 0;
                    if (movement.Y != velocity.Y) velocity.Y = 0;
                    path[frame] = center;
                }
            }
            finally { Collision.up = up; Collision.down = down; }
            return path;
        }

        public static bool IsActive => _active && _framesLeft > 0;
        public static bool IsIdleDefending => _idleDefense;

        public static void Start(float maxDistTiles, int[] types = null, string movement = "auto")
        {
            if (movement is not ("auto" or "left" or "right")) throw new ArgumentException("movement需要auto/left/right。");
            StopIdleDefense();
            _maxDistTiles = maxDistTiles;
            _framesLeft = TimeoutFrames;
            _active = true;
            _types = types == null ? null : new HashSet<int>(types);
            _actionTarget = null;
            _endWithoutTarget = EndedWithoutTarget = false;
        }

        internal static void StartAction(float maxDistTiles, int[] types = null, string movement = "auto", NPC target = null)
        {
            Start(maxDistTiles, types, movement);
            _actionTarget = target;
            _endWithoutTarget = true;
        }

        public static void Stop()
        {
            StopIdleDefense();
            if (_usingItem && !ItemUseCoordinator.IsActive) Main.LocalPlayer.controlUseItem = false;
            _usingItem = false;
            _active = false;
            _framesLeft = 0;
            _types = null;
            _actionTarget = null;
            _animationSlot = -1;
            _animationTarget = null;
            _attackNext = false;
            _evadeJumpHeld = false;
            AimTarget = null;
        }

        internal static void StopIdleDefense()
        {
            if (!_idleDefense) return;
            if (_usingItem && !ItemUseCoordinator.IsActive) Main.LocalPlayer.controlUseItem = false;
            _usingItem = false;
            _idleDefense = false;
            DiagLog.Write("[agent_defense] released");
        }

        public static void Tick(Player p) => Tick(p, AgentTools.AutonomyActive);

        internal static void Tick(Player p, bool processing, bool primaryItemUse = false)
        {
            AimTarget = null;
            if (HoldingFallProtection(p))
            {
                _animationSlot = -1; _animationTarget = null;
                _evadeJumpHeld = false;
                _usingItem = _idleDefense = _evading = false;
                return;
            }
            bool evading = false;
            bool holdingJump = false;
            if ((processing || IsActive) && !p.dead && !Manual.On && AgentNetwork.CanAutomate
                && !PvpDuel.Active && !PlayerCombat.Active && !AgentTools.ContactEscapeActive
                && !AgentTools.ControlSession.OwnsDirectMovement
                && !BridgeStart.ReservesMovement
                && !FeedbackControl.IsApplying
                && !CombatForecast.ReservesRoutePrefix
                && AgentPermissions.CanDodge(delegated: true))
            {
                bool bossMovement = false;
                if (Dodge.Enabled)
                    foreach (var npc in Main.npc)
                        if (npc is { active: true, boss: true } && Vector2.Distance(npc.Center, p.Center) < 3200)
                        { bossMovement = true; break; }
                var evade = Evade(p, projectilesOnly: bossMovement);
                if (evade.Apply)
                {
                    p.controlLeft = evade.Direction < 0;
                    p.controlRight = evade.Direction > 0;
                    p.controlJump = evade.Jump;
                    evading = true;
                    holdingJump = evade.Jump;
                }
            }
            _evadeJumpHeld = holdingJump;
            if (evading != _evading)
                DiagLog.Write($"[agent_evade] active={evading} tick={Main.GameUpdateCount} x={p.position.X} y={p.position.Y} hp={p.statLife}");
            _evading = evading;
            if (!AgentPermissions.CanAttack(delegated: true)) { Stop(); return; }
            if (_animationSlot >= 0 && p.itemAnimation > 0 && !p.dead && !Manual.On)
            {
                _animationSeen = true;
                bool continuingPrimary = !_animationAttack && primaryItemUse && p.selectedItem == _animationSlot;
                if (continuingPrimary)
                    _animationAim = new Vector2(Main.mouseX + Main.screenPosition.X, Main.mouseY + Main.screenPosition.Y);
                p.selectedItem = _animationSlot;
                bool attack = _animationAttack && (_animationTarget == null || TempleTargetAllowed(p, _animationTarget));
                if (attack && _animationTarget is { active: true, friendly: false })
                    _animationAim = Combat.Aim(p, _animationTarget, p.HeldItem);
                Cursor.AimPx(_animationAim.X, _animationAim.Y);
                p.controlUseItem = continuingPrimary;
                _usingItem = attack;
                if (attack) AimTarget = _animationAim;
                return;
            }
            if (_animationSlot >= 0)
            {
                if (_animationSeen)
                {
                    _attackNext = !_animationAttack;
                    DiagLog.Write($"[agent_item_turn] completed={(_animationAttack ? "attack" : "primary")} slot={_animationSlot} tick={Main.GameUpdateCount}");
                }
                _animationSlot = -1;
            }
            if (NavigationSnapshotPending || CombatForecast.ReservesRoutePrefix || StateSpacePlanner.ReservesItemUse
                || RecedingNav.Active && (VanillaNavBridge.Enabled && !RecedingNav.FieldReady
                    || VanillaNavBridge.Stage is "Approaching" or "Braking" or "Preparing"))
            {
                _usingItem = _idleDefense = false;
                return;
            }
            bool mining = primaryItemUse && (p.HeldItem.pick > 0 || p.HeldItem.axe > 0 || p.HeldItem.hammer > 0);
            bool placing = PlaceAction.IsRunning || PlaceCoordinator.IsActive || PlaceWalls.IsRunning
                || p.HeldItem.createTile >= 0 && (primaryItemUse || p.itemTime > 0);
            int closeWeapon = -1;
            NPC closeThreat = mining && !placing ? FindNearest(p, !IsActive, out closeWeapon, closeOnly: true) : null;
            bool miningAction = AgentTools.ControlSession.Active
                && AgentControlSession.Behavior(AgentTools.ControlSession.Tool) == ControlBehavior.Mining;
            bool itemBusy = placing || mining && !_attackNext && closeThreat == null || !mining && (primaryItemUse || ItemUseCoordinator.IsActive
                || AgentTools.ControlSession.OwnsItemUse && !miningAction && !ApproachingHellSite
                    && AgentTools.ControlSession.Tool is not ("nav_to" or "fight" or "gather" or "collect_treasure"));
            if (itemBusy)
            {
                _idleDefense = false;
                _usingItem = false;
                RememberAnimation(p, false);
                return;
            }
            bool idle = !IsActive;
            if (idle && (!processing || !Config.I.AgentEnabled || !AgentNetwork.CanAutomate || Manual.On
                || p.dead
                || AgentTools.ContactEscapeActive || PvpDuel.Active || PlayerCombat.Active
                || !AgentTools.ControlAllows(ControlBehavior.Survival, ControlChannels.ItemUse, delegated: true)))
            {
                StopIdleDefense();
                _active = false;
                return;
            }
            int weaponSlot = closeWeapon;
            NPC target = closeThreat ?? FindNearest(p, idle, out weaponSlot);
            if (target != null) weaponSlot = CombatLoadout.ReadySlot(p, weaponSlot);

            if (target == null || weaponSlot < 0)
            {
                if (!mining) p.controlUseItem = false;
                else RememberAnimation(p, false);
                _idleDefense = false;
                _usingItem = false;
                if (!idle && _endWithoutTarget)
                {
                    Stop();
                    EndedWithoutTarget = true;
                    DiagLog.Write($"[agent_fight] stopped=no_actionable_target tick={Main.GameUpdateCount}");
                }
                else if (!idle) _framesLeft--;
                return;
            }
            if (idle && !_idleDefense) DiagLog.Write($"[agent_defense] started target={target.whoAmI} hp={p.statLife}");
            _idleDefense = idle;
            if (!idle) _framesLeft = TimeoutFrames;

            var aim = Combat.Aim(p, target, p.inventory[weaponSlot]);
            _animationTarget = target;
            AimTarget = aim;
            Cursor.AimPx(aim.X, aim.Y);

            p.selectedItem = weaponSlot;
            p.delayUseItem = p.mouseInterface = false;
            p.controlUseItem = CanHitTarget(p, p.HeldItem, target.Center, target.width / 2f);
            _usingItem = p.controlUseItem;
            RememberAnimation(p, true);
        }

        private static NPC FindNearest(Player p, bool idle, out int weaponSlot, bool closeOnly = false)
        {
            weaponSlot = -1;
            float pcx = p.position.X + p.width / 2f;
            float pcy = p.position.Y + p.height / 2f;
            float maxDist = (idle ? 20f : _maxDistTiles) * 16f;
            NPC best = null;
            float bestDist = float.MaxValue;
            bool bestAttacker = false;
            var damage = AgentTools.LastDamage;
            var attacker = damage?["attacker"];
            bool attackerOnly = ApproachingHellSite;
            long age = (long)Main.GameUpdateCount - (damage?["tick"]?.GetValue<long>() ?? -1000);
            for (int i = 0; i < Main.maxNPCs; i++)
            {
                NPC npc = Main.npc[i];
                if (npc == null || !npc.CanBeChasedBy(p)) continue;
                if (!idle && _actionTarget != null && !ReferenceEquals(npc, _actionTarget)) continue;
                if (idle ? npc.damage <= 0 : _types != null && !_types.Contains(npc.type)) continue;
                if (npc.townNPC || npc.friendly) continue;
                if (!TempleTargetAllowed(p, npc)) continue;
                if (npc.lifeMax <= 5 && npc.damage == 0) continue;
                float ncx = npc.position.X + npc.width / 2f;
                float ncy = npc.position.Y + npc.height / 2f;
                float dx = ncx - pcx;
                float dy = ncy - pcy;
                float dist = dx * dx + dy * dy;
                if (dist > maxDist * maxDist) continue;
                if (closeOnly)
                {
                    float gapX = Math.Max(0, Math.Abs(dx) - (p.width + npc.width) / 2f);
                    float gapY = Math.Max(0, Math.Abs(dy) - (p.height + npc.height) / 2f);
                    if (npc.damage <= 0 || gapX * gapX + gapY * gapY > 48 * 48) continue;
                }
                bool recentAttacker = age >= 0 && age <= 300 && attacker?["npc_index"]?.GetValue<int>() == i
                    && attacker?["npc_type"]?.GetValue<int>() == npc.type;
                if (attackerOnly && !recentAttacker) continue;
                int slot = CombatLoadout.Select(p, npc, allowPickaxeFallback: true,
                    allowAlternative: idle && recentAttacker && age <= 60 && attacker?["via"]?.ToString() == "contact");
                if (slot < 0) continue;
                if (best == null || recentAttacker && !bestAttacker || recentAttacker == bestAttacker && dist < bestDist)
                {
                    bestDist = dist;
                    best = npc;
                    bestAttacker = recentAttacker;
                    weaponSlot = slot;
                }
            }
            return best;
        }

    }
}
