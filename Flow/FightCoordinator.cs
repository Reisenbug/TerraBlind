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
        private static bool _moving;
        private static int _jumpHold;
        private static string _movement = "auto";

        private static void ReleaseMovement(Player p)
        {
            if (_moving) p.controlLeft = p.controlRight = p.controlJump = false;
            _moving = false; _jumpHold = 0;
        }

        internal static bool NeedsClearLine(Item item) => item.shoot <= 0
            || !ContentSamples.ProjectilesByType.TryGetValue(item.shoot, out var shot) || shot.tileCollide;

        internal static (int Direction, bool Jump) Evade(Player p, NPC target = null)
        {
            bool blocked = target != null && NeedsClearLine(p.HeldItem)
                && !Collision.CanHitLine(p.Center, 4, 4, target.Center, 4, 4);
            var shots = new List<Projectile>();
            var enemies = new List<NPC>();
            foreach (var shot in Main.projectile)
                if (shot.active && shot.hostile && shot.damage > 0) shots.Add(shot);
            foreach (var npc in Main.npc)
                if (npc.active && !npc.friendly && !npc.townNPC && npc.damage > 0
                    && Vector2.Distance(npc.Center, p.Center) < 600) enemies.Add(npc);
            if (!blocked && shots.Count == 0 && enemies.Count == 0) return (0, false);
            float Risk(int direction, bool jump)
            {
                Vector2 position = p.Center, velocity = p.velocity;
                if (jump) velocity.Y = -Player.jumpSpeed - p.jumpSpeedBoost;
                int hold = jump ? 12 : _jumpHold;
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

        public static void Start(float maxDistTiles, int[] types = null, string movement = "auto")
        {
            if (movement is not ("auto" or "left" or "right")) throw new ArgumentException("movement需要auto/left/right。");
            _movement = movement;
            _maxDistTiles = maxDistTiles;
            _framesLeft = TimeoutFrames;
            _active = true;
            _types = types == null ? null : new HashSet<int>(types);
        }

        public static void Stop()
        {
            ReleaseMovement(Main.LocalPlayer);
            _active = false;
            _framesLeft = 0;
            _types = null;
            _movement = "auto";
        }

        public static void Tick(Player p)
        {
            if (!AgentPermissions.CanAttack(delegated: true)) { Stop(); return; }
            if (!_active || _framesLeft <= 0)
            {
                ReleaseMovement(p);
                _active = false;
                return;
            }
            NPC target = FindNearest(p);
            if (target == null) { ReleaseMovement(p); _framesLeft--; return; }
            _framesLeft = TimeoutFrames;

            if (AgentPermissions.CanDodge(delegated: true))
            {
                var evade = _movement == "auto" ? Evade(p, target)
                    : (Direction: _movement == "left" ? -1 : 1, Jump: false);
                if (evade.Jump) _jumpHold = 12;
                if (_moving) p.controlLeft = p.controlRight = p.controlJump = false;
                p.controlLeft |= evade.Direction < 0;
                p.controlRight |= evade.Direction > 0;
                p.controlJump |= _jumpHold > 0;
                _moving = evade.Direction != 0 || _jumpHold > 0;
                if (_jumpHold > 0) _jumpHold--;
            }
            else ReleaseMovement(p);

            int weaponSlot = FindWeaponSlot(p);
            if (weaponSlot < 0) return;

            float ncx = target.position.X + target.width / 2f;
            float ncy = target.position.Y + target.height / 2f;
            Cursor.AimPx(ncx, ncy);

            p.selectedItem = weaponSlot;
            p.controlUseItem = !NeedsClearLine(p.HeldItem) || Collision.CanHitLine(p.Center, 4, 4, target.Center, 4, 4);
        }

        private static NPC FindNearest(Player p)
        {
            float pcx = p.position.X + p.width / 2f;
            float pcy = p.position.Y + p.height / 2f;
            float maxDist = _maxDistTiles * 16f;
            NPC best = null;
            float bestDist = float.MaxValue;
            for (int i = 0; i < Main.maxNPCs; i++)
            {
                NPC npc = Main.npc[i];
                if (npc == null || !npc.active) continue;
                if (_types != null && !_types.Contains(npc.type)) continue;
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
