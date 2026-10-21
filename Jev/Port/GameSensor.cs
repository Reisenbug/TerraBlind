using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace TerraBlind.JevariaCombat;

public sealed class GameSensor : ICombatSensor
{
    public const int WorldEdgeCells = 40;
    private readonly Dictionary<int, Queue<(ulong Tick, float Speed)>> _speedHistory = new();

    public bool Observe(Player player)
    {
        ulong now = Main.GameUpdateCount;
        bool bossPresent = false;
        foreach (NPC npc in Main.npc)
        {
            if (!npc.active || !npc.boss && !BossPart(npc.type)) continue;
            if (npc.boss) bossPresent = true;
            if (!_speedHistory.TryGetValue(npc.whoAmI, out var history))
                _speedHistory[npc.whoAmI] = history = new Queue<(ulong, float)>();
            history.Enqueue((now, (Math.Abs(npc.velocity.X) + Math.Abs(npc.velocity.Y)) * 60f / 16f));
            while (history.Count > 0 && now - history.Peek().Tick > 60)
                history.Dequeue();
        }
        return bossPresent;
    }

    public CombatSnapshot? Capture(Player player, ulong sequence, DodgeIntent previous, long previousDurationMs)
    {
        var activeBosses = new List<NPC>();
        NPC? boss = null;
        float closest = float.MaxValue;
        foreach (NPC npc in Main.npc)
        {
            if (!npc.active || !npc.boss) continue;
            activeBosses.Add(npc);
            if (npc.type == NPCID.Spazmatism) boss = npc;
            float distance = Vector2.DistanceSquared(player.Center, npc.Center);
            if (boss?.type == NPCID.Spazmatism || distance >= closest) continue;
            closest = distance;
            boss = npc;
        }

        if (boss is null) return null;

        var bosses = new List<CombatEntity>();
        var motion = new List<BossMotion>();
        var bossIds = new HashSet<int>();
        foreach (NPC npc in activeBosses)
        {
            bossIds.Add(npc.whoAmI);
            bosses.Add(Entity(npc.whoAmI, BossName(npc), npc.Center, npc.velocity,
                npc.width, npc.height, npc.damage));
            float speed = (Math.Abs(npc.velocity.X) + Math.Abs(npc.velocity.Y)) * 60f / 16f;
            float fastest = speed;
            if (_speedHistory.TryGetValue(npc.whoAmI, out var history))
                foreach (var sample in history) fastest = Math.Max(fastest, sample.Speed);
            motion.Add(new BossMotion(npc.whoAmI, speed, fastest, npc.life, npc.lifeMax));
        }
        var parts = new List<CombatEntity>();
        foreach (NPC npc in Main.npc)
        {
            if (!npc.active || npc.friendly || bossIds.Contains(npc.whoAmI)) continue;
            if (bossIds.Contains(npc.realLife) || BossPart(npc.type))
            {
                parts.Add(Entity(npc.whoAmI, npc.FullName, npc.Center, npc.velocity,
                    npc.width, npc.height, npc.damage));
                float speed = (Math.Abs(npc.velocity.X) + Math.Abs(npc.velocity.Y)) * 60f / 16f;
                float fastest = speed;
                if (_speedHistory.TryGetValue(npc.whoAmI, out var history))
                    foreach (var sample in history) fastest = Math.Max(fastest, sample.Speed);
                motion.Add(new BossMotion(npc.whoAmI, speed, fastest, npc.life, npc.lifeMax));
            }
        }

        return CaptureScene(player, sequence, previous, previousDurationMs,
            Entity(boss.whoAmI, BossName(boss), boss.Center, boss.velocity, boss.width, boss.height, boss.damage),
            bosses, motion, parts, false, 1f);
    }

    public CombatSnapshot CapturePlayer(Player player, Player target, ulong sequence, DodgeIntent previous, long durationMs)
    {
        var opponent = Entity(target.whoAmI, target.name, target.Center, target.velocity, target.width, target.height, target.HeldItem.damage);
        return CaptureScene(player, sequence, previous, durationMs, opponent,
            new[] { opponent }, Array.Empty<BossMotion>(), Array.Empty<CombatEntity>(), true, .075f);
    }

    static CombatSnapshot CaptureScene(Player player, ulong sequence, DodgeIntent previous, long previousDurationMs,
        CombatEntity target, IReadOnlyList<CombatEntity> targets, IReadOnlyList<BossMotion> motion,
        IReadOnlyList<CombatEntity> parts, bool pvp, float reserveScale)
    {
        var projectiles = new List<CombatEntity>();
        foreach (Projectile projectile in Main.projectile)
        {
            if (!projectile.active || projectile.damage <= 0) continue;
            bool enemy = pvp && projectile.friendly && projectile.owner != player.whoAmI
                && projectile.owner >= 0 && projectile.owner < Main.maxPlayers
                && Main.player[projectile.owner].active && Main.player[projectile.owner].hostile
                && (player.team == 0 || player.team != Main.player[projectile.owner].team);
            if (!projectile.hostile && !enemy) continue;
            projectiles.Add(Entity(projectile.whoAmI, projectile.Name, projectile.Center,
                projectile.velocity * (projectile.extraUpdates + 1), projectile.width,
                projectile.height, projectile.damage));
        }

        Rectangle box = player.Hitbox;
        var world = new BoundaryDistances(box.Left, Main.maxTilesX * 16f - box.Right,
            box.Top, Main.maxTilesY * 16f - box.Bottom);
        BoundaryDistances solid = ScanSolids(box);
        int mountType = GameMovement.EquippedVerticalMount(player);
        bool hasMount = mountType != MountID.None;
        bool hasHook = player.miscEquips[4].shoot != ProjectileID.None && Main.projHook[player.miscEquips[4].shoot];
        for (int slot = 0; slot < 58 && !hasHook; slot++)
        {
            int candidate = player.inventory[slot].shoot;
            hasHook = candidate > 0 && Main.projHook[candidate];
        }
        float shotSpeed = player.HeldItem.shootSpeed;
        int shotType = player.HeldItem.shoot;
        if (player.HeldItem.useAmmo != AmmoID.None &&
            !player.PickAmmo(player.HeldItem, out shotType, out shotSpeed, out _, out _, out _, true))
            shotSpeed = 0f;
        if (ContentSamples.ProjectilesByType.TryGetValue(shotType, out Projectile? projectileDefaults) &&
            projectileDefaults is not null)
            shotSpeed *= projectileDefaults.extraUpdates + 1;

        return new CombatSnapshot(sequence, Main.GameUpdateCount,
            Entity(player.whoAmI, player.name, player.Center, player.velocity,
                player.width, player.height, 0), player.statLife,
            player.statLifeMax2, player.lifeRegen < 0,
            target, targets, motion, parts, projectiles,
            solid, world, ScanPlatformBelow(box),
            player.dashType > 0 && player.dashDelay == 0,
            player.AnyExtraJumpUsable(), player.wingTime > 0f,
            hasHook, hasMount,
            shotSpeed, shotType, player.HeldItem.Name,
            GameMovement.AvailableActions(player, solid, world, hasHook, hasMount, reserveScale),
            previous, previousDurationMs) { MountType = mountType, MovementReserveScale = reserveScale };
    }

    private static CombatEntity Entity(int id, string name, Vector2 center,
        Vector2 velocity, int width, int height, int damage)
        => new(id, name, center, velocity, new Vector2(width, height), damage);

    private static string BossName(NPC npc) => npc.type switch
    {
        NPCID.Spazmatism => "Spazmatism",
        NPCID.Retinazer => "Retinazer",
        _ => npc.FullName
    };

    private static bool BossPart(int type) => type is
        NPCID.SkeletronHand or NPCID.EaterofWorldsHead or NPCID.EaterofWorldsBody or
        NPCID.EaterofWorldsTail or NPCID.WallofFleshEye or NPCID.TheHungry or
        NPCID.TheHungryII or NPCID.PlanterasTentacle or NPCID.PlanterasHook or
        NPCID.PrimeCannon or NPCID.PrimeSaw or NPCID.PrimeVice or NPCID.PrimeLaser or
        NPCID.TheDestroyerBody or NPCID.TheDestroyerTail or NPCID.Probe;

    public static BoundaryDistances ScanSolids(Rectangle box)
    {
        float left = 1600f, right = 1600f, up = 1600f, down = 1600f;
        int x0 = Math.Max(0, box.Left / 16);
        int x1 = Math.Min(Main.maxTilesX - 1, (box.Right - 1) / 16);
        int y0 = Math.Max(0, box.Top / 16);
        int y1 = Math.Min(Main.maxTilesY - 1, (box.Bottom - 1) / 16);

        for (int step = 1; step <= 100; step++)
        {
            int lx = x0 - step, rx = x1 + step;
            if (left == 1600f && lx >= 0 && SolidColumn(lx, y0, y1))
                left = box.Left - (lx + 1) * 16f;
            if (right == 1600f && rx < Main.maxTilesX && SolidColumn(rx, y0, y1))
                right = rx * 16f - box.Right;
            int uy = y0 - step, dy = y1 + step;
            if (up == 1600f && uy >= 0 && SolidRow(uy, x0, x1))
                up = box.Top - (uy + 1) * 16f;
            if (down == 1600f && dy < Main.maxTilesY && SolidRow(dy, x0, x1))
                down = dy * 16f - box.Bottom;
            if (left < 1600f && right < 1600f && up < 1600f && down < 1600f) break;
        }
        return new BoundaryDistances(left, right, up, down);
    }

    private static bool SolidColumn(int x, int first, int last)
    {
        for (int y = first; y <= last; y++) if (Solid(x, y)) return true;
        return false;
    }

    private static bool SolidRow(int y, int first, int last)
    {
        for (int x = first; x <= last; x++) if (Solid(x, y)) return true;
        return false;
    }

    private static bool Solid(int x, int y)
    {
        Tile tile = Main.tile[x, y];
        return tile.HasTile && !tile.IsActuated && Main.tileSolid[tile.TileType]
            && !Main.tileSolidTop[tile.TileType] && tile.Slope == 0 && !tile.IsHalfBlock;
    }

    private static float ScanPlatformBelow(Rectangle box)
    {
        int x0 = Math.Max(0, box.Left / 16);
        int x1 = Math.Min(Main.maxTilesX - 1, (box.Right - 1) / 16);
        int y0 = (box.Bottom - 1) / 16;
        for (int step = 1; step <= 100 && y0 + step < Main.maxTilesY; step++)
        {
            int y = y0 + step;
            for (int x = x0; x <= x1; x++)
            {
                Tile tile = Main.tile[x, y];
                if (tile.HasTile && !tile.IsActuated && Main.tileSolidTop[tile.TileType])
                    return y * 16f - box.Bottom;
            }
        }
        return 1600f;
    }
}

