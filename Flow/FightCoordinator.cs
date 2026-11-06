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
        private static bool _idleDefense;
        private static bool _usingItem;
        private static bool _evading;
        private static int _animationSlot = -1;
        private static Vector2 _animationAim;
        private static bool _animationAttack, _animationSeen, _attackNext;
        public static Vector2? AimTarget { get; private set; }
        public static bool AttackAnimationActive => _animationSlot >= 0 && _animationAttack && Main.LocalPlayer.itemAnimation > 0;

        static void RememberAnimation(Player p, bool attack)
        {
            if (!p.controlUseItem || p.itemAnimation != 0 || p.itemTime != 0) return;
            _animationSlot = p.selectedItem;
            _animationAim = new Vector2(Main.mouseX + Main.screenPosition.X, Main.mouseY + Main.screenPosition.Y);
            _animationAttack = attack;
            _animationSeen = false;
        }

        internal static Vector2 PredictAim(Player p, NPC target, Item weapon)
        {
            int shot = weapon.shoot;
            float speed = weapon.shootSpeed;
            if (weapon.useAmmo > 0 && !p.PickAmmo(weapon, out shot, out speed, out _, out _, out _, true)) return target.Center;
            if (shot <= 0 || speed <= 0) return target.Center;
            if (ContentSamples.ProjectilesByType.TryGetValue(shot, out var sample)) speed *= sample.extraUpdates + 1;
            var point = PvpTactics.Aim(new(p.Center.X, p.Center.Y), new(target.Center.X, target.Center.Y),
                new(target.velocity.X, target.velocity.Y), speed, 0,
                (a, b) => !NeedsClearLine(weapon) || Collision.CanHitLine(new Vector2(a.X, a.Y), 2, 2, new Vector2(b.X, b.Y), 2, 2));
            return point.HasValue ? new Vector2(point.Value.X, point.Value.Y) : target.Center;
        }

        internal static bool NeedsClearLine(Item item) => item.shoot <= 0
            || !ContentSamples.ProjectilesByType.TryGetValue(item.shoot, out var shot) || shot.tileCollide;

        internal static (int Direction, bool Jump) Evade(Player p, NPC target = null, bool projectilesOnly = false)
        {
            bool blocked = target != null && NeedsClearLine(p.HeldItem)
                && !Collision.CanHitLine(p.Center, 4, 4, target.Center, 4, 4);
            var shots = new List<Projectile>();
            var enemies = new List<NPC>();
            foreach (var shot in Main.projectile)
                if (shot.active && shot.hostile && shot.damage > 0) shots.Add(shot);
            foreach (var npc in projectilesOnly ? Array.Empty<NPC>() : Main.npc)
                if (npc.active && !npc.friendly && !npc.townNPC && npc.damage > 0
                    && Vector2.Distance(npc.Center, p.Center) < 600) enemies.Add(npc);
            if (!blocked && shots.Count == 0 && enemies.Count == 0) return (0, false);
            float Risk(int direction, bool jump)
            {
                Vector2 position = p.Center, velocity = p.velocity;
                if (jump) velocity.Y = -Player.jumpSpeed - p.jumpSpeedBoost;
                int hold = jump ? 12 : 0;
                float score = direction == 0 ? 0 : .01f;
                if (jump) score += .02f;
                for (int frame = 1; frame <= 24; frame++)
                {
                    float desired = direction * Math.Max(p.maxRunSpeed, p.accRunSpeed);
                    velocity.X += Math.Clamp(desired - velocity.X, -p.runAcceleration, p.runAcceleration);
                    var next = position + new Vector2(velocity.X, 0);
                    if (!Collision.SolidCollision(next - new Vector2(p.width / 2f, p.height / 2f), p.width, p.height)) position = next;
                    else velocity.X = 0;
                    if (hold > 0 && velocity.Y < 0)
                    { velocity.Y = -Player.jumpSpeed - p.jumpSpeedBoost; hold--; }
                    velocity.Y += p.gravity;
                    next = position + new Vector2(0, velocity.Y);
                    if (!Collision.SolidCollision(next - new Vector2(p.width / 2f, p.height / 2f), p.width, p.height)) position = next;
                    else velocity.Y = 0;
                    if (blocked && !Collision.CanHitLine(position, 4, 4, target.Center + target.velocity * frame, 4, 4)) score++;
                    foreach (var shot in shots)
                    {
                        Vector2 point = shot.Center + shot.velocity * ((shot.extraUpdates + 1) * frame);
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
                return score;
            }
            (int Direction, bool Jump) best = (0, false);
            float bestRisk = Risk(0, false);
            foreach (int direction in new[] { -1, 0, 1 })
            foreach (bool jump in new[] { false, true })
            {
                if (jump && p.velocity.Y != 0) continue;
                float risk = Risk(direction, jump);
                if (risk < bestRisk) { bestRisk = risk; best = (direction, jump); }
            }
            return best;
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
        }

        public static void Stop()
        {
            StopIdleDefense();
            if (_usingItem && !ItemUseCoordinator.IsActive) Main.LocalPlayer.controlUseItem = false;
            _usingItem = false;
            _active = false;
            _framesLeft = 0;
            _types = null;
            _animationSlot = -1;
            _attackNext = false;
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

        public static void Tick(Player p) => Tick(p, NativeAgent.Processing);

        internal static void Tick(Player p, bool processing, bool primaryItemUse = false)
        {
            AimTarget = null;
            bool evading = false;
            if ((processing || IsActive) && !p.dead && !Manual.On && AgentNetwork.CanAutomate
                && !PvpDuel.Active && !PlayerCombat.Active && !AgentTools.ContactEscapeActive
                && AgentPermissions.CanDodge(delegated: true))
            {
                var evade = Evade(p, projectilesOnly: true);
                if (evade.Direction != 0 || evade.Jump)
                {
                    p.controlLeft = evade.Direction < 0;
                    p.controlRight = evade.Direction > 0;
                    p.controlJump = evade.Jump;
                    evading = true;
                }
            }
            if (evading != _evading)
                DiagLog.Write($"[agent_evade] active={evading} tick={Main.GameUpdateCount} x={p.position.X} y={p.position.Y} hp={p.statLife}");
            _evading = evading;
            if (!AgentPermissions.CanAttack(delegated: true)) { Stop(); return; }
            if (_animationSlot >= 0 && p.itemAnimation > 0 && !p.dead && !Manual.On)
            {
                _animationSeen = true;
                p.selectedItem = _animationSlot;
                Cursor.AimPx(_animationAim.X, _animationAim.Y);
                p.controlUseItem = false;
                _usingItem = _animationAttack;
                if (_animationAttack) AimTarget = _animationAim;
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
            bool mining = primaryItemUse && (p.HeldItem.pick > 0 || p.HeldItem.axe > 0 || p.HeldItem.hammer > 0);
            bool placing = PlaceAction.IsRunning || PlaceCoordinator.IsActive || PlaceWalls.IsRunning
                || primaryItemUse && p.HeldItem.createTile >= 0;
            bool itemBusy = placing || mining && !_attackNext || !mining && (primaryItemUse || ItemUseCoordinator.IsActive
                || AgentTools.ControlSession.Active && AgentTools.ControlSession.Tool is not ("nav_to" or "fight" or "gather" or "collect_treasure"));
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
            NPC target = FindNearest(p, idle);

            if (target == null)
            {
                if (!mining) p.controlUseItem = false;
                else RememberAnimation(p, false);
                _idleDefense = false;
                _usingItem = false;
                if (!idle) _framesLeft--;
                return;
            }
            if (idle && !_idleDefense) DiagLog.Write($"[agent_defense] started target={target.whoAmI} hp={p.statLife}");
            _idleDefense = idle;
            if (!idle) _framesLeft = TimeoutFrames;

            int weaponSlot = FindWeaponSlot(p);
            if (weaponSlot < 0)
            {
                if (mining) RememberAnimation(p, false);
                return;
            }

            var aim = PredictAim(p, target, p.inventory[weaponSlot]);
            AimTarget = aim;
            Cursor.AimPx(aim.X, aim.Y);

            p.selectedItem = weaponSlot;
            if (idle) p.delayUseItem = p.mouseInterface = false;
            p.controlUseItem = !NeedsClearLine(p.HeldItem) || Collision.CanHitLine(p.Center, 4, 4, target.Center, 4, 4);
            _usingItem = p.controlUseItem;
            RememberAnimation(p, true);
        }

        private static NPC FindNearest(Player p, bool idle = false)
        {
            float pcx = p.position.X + p.width / 2f;
            float pcy = p.position.Y + p.height / 2f;
            float maxDist = (idle ? 20f : _maxDistTiles) * 16f;
            NPC best = null;
            float bestDist = float.MaxValue;
            for (int i = 0; i < Main.maxNPCs; i++)
            {
                NPC npc = Main.npc[i];
                if (npc == null || !npc.active) continue;
                if (idle ? npc.damage <= 0 : _types != null && !_types.Contains(npc.type)) continue;
                if (npc.townNPC || npc.friendly) continue;
                if (npc.lifeMax <= 5 && npc.damage == 0) continue;
                float ncx = npc.position.X + npc.width / 2f;
                float ncy = npc.position.Y + npc.height / 2f;
                float dx = ncx - pcx;
                float dy = ncy - pcy;
                float dist = dx * dx + dy * dy;
                if (dist < bestDist && System.MathF.Sqrt(dist) <= maxDist)
                {
                    bestDist = dist;
                    best = npc;
                }
            }
            return best;
        }

        private static int FindWeaponSlot(Player p)
            => CombatLoadout.Select(p);
    }
}
