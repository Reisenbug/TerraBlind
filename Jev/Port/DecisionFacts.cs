using System;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria.ID;

namespace TerraBlind.JevariaCombat;

public static class DecisionFacts
{
    public static string State(CombatSnapshot snapshot, string bossNotes)
    {
        bool pillion = snapshot.MountType == MountID.QueenSlime;
        string mountName = pillion ? "Gelatinous Pillion" : "Slimy Saddle";
        string mountSteering = pillion
            ? "It has faster horizontal steering and a short flight time"
            : "It has poor horizontal steering";
        var state = new
        {
            hp_percent = snapshot.Health * 100 / Math.Max(1, snapshot.MaxHealth),
            i_am_losing_health_over_time = snapshot.LosingHealthOverTime,
            my_speed_to_the_right = (int)(snapshot.Player.Velocity.X * 60f / 16f),
            my_speed_upward = (int)(-snapshot.Player.Velocity.Y * 60f / 16f),
            movement = new
            {
                can_double_jump = snapshot.CanDoubleJump,
                can_fly = snapshot.CanFly,
                has_hook = snapshot.HasHook,
                vertical_mount = snapshot.HasMount ? mountName : "none"
            },
            threats = snapshot.Bosses.Concat(snapshot.Parts)
                .GroupBy(entity => entity.Name)
                .Select(group => group.OrderBy(entity => Vector2.DistanceSquared(entity.Center, snapshot.Player.Center)).First())
                .Where(entity => (Math.Abs(entity.Center.X - snapshot.Player.Center.X) +
                    Math.Abs(entity.Center.Y - snapshot.Player.Center.Y)) / 16f <= 200f)
                .Select(entity => BossThreat(entity, snapshot)).ToArray(),
            incoming_projectiles = snapshot.Projectiles
                .Select(entity => (Entity: entity, Frames: FramesUntilContact(entity, snapshot.Player)))
                .Where(item => item.Frames >= 0 || Vector2.DistanceSquared(item.Entity.Center, snapshot.Player.Center) <= 30f * 30f * 16f * 16f)
                .Where(item => item.Frames == 0 || Vector2.DistanceSquared(item.Entity.Center, snapshot.Player.Center) <= 96f * 96f ||
                    (item.Entity.Center.X < snapshot.Player.Center.X && item.Entity.Velocity.X > 0.1f) ||
                    (item.Entity.Center.X > snapshot.Player.Center.X && item.Entity.Velocity.X < -0.1f) ||
                    (item.Entity.Center.Y < snapshot.Player.Center.Y && item.Entity.Velocity.Y > 0.1f) ||
                    (item.Entity.Center.Y > snapshot.Player.Center.Y && item.Entity.Velocity.Y < -0.1f))
                .OrderBy(item => item.Frames < 0 ? int.MaxValue : item.Frames)
                .Take(12)
                .Select(item => ProjectileThreat(item.Entity, snapshot)).ToArray(),
            room = new
            {
                left = Room(snapshot.SolidDistances.Left, snapshot.WorldDistances.Left),
                right = Room(snapshot.SolidDistances.Right, snapshot.WorldDistances.Right),
                above = Room(snapshot.SolidDistances.Up, snapshot.WorldDistances.Up),
                below = Room(snapshot.SolidDistances.Down, snapshot.WorldDistances.Down)
            },
            boss_notes = bossNotes
        };
        return JsonSerializer.Serialize(state);
    }

    public static string Describe(DodgeOption option, CombatSnapshot snapshot, bool pvp = false)
    {
        if (pvp && option.Intent.Direction == DodgeDirection.Stay)
            return "Hold position if it offers a useful firing angle or melee reach; otherwise reposition.";
        bool pillion = snapshot.MountType == MountID.QueenSlime;
        return RouteDescription(option.Intent.Direction, snapshot, option.Intent.HorizontalSize,
            option.Intent.VerticalSize == Magnitude.Large, pvp) + " " + ActionDescription(option,
            pillion ? "Gelatinous Pillion" : "Slimy Saddle",
            pillion ? "It has faster horizontal steering and a short flight time" : "It has poor horizontal steering");
    }

    private static string ActionName(DodgeIntent intent)
    {
        if (intent.Direction == DodgeDirection.Stay) return "Stay";
        (int x, int y) = Components(intent.Direction);
        string horizontal = x == 0 ? "" : $"{(x < 0 ? "Left" : "Right")}{intent.HorizontalSize}";
        string vertical = y == 0 ? "" : $"{(y < 0 ? "Up" : "Down")}{intent.VerticalSize}";
        return horizontal + vertical;
    }

    private static string DirectionDescription(DodgeDirection direction)
    {
        if (direction == DodgeDirection.Stay)
            return "Stay only if no approaching threat requires movement and I have room to escape later.";
        (int x, int y) = Components(direction);
        string horizontal = x == 0 ? "" : x < 0 ? "left" : "right";
        string vertical = y == 0 ? "" : y < 0 ? "up" : "down";
        return x == 0 ? $"Move {vertical}." : y == 0 ? $"Move {horizontal}." :
            $"Move {vertical} and {horizontal}.";
    }

    private static string RouteDescription(DodgeDirection direction, CombatSnapshot snapshot,
        Magnitude horizontalSize, bool burst, bool pvp)
    {
        if (direction == DodgeDirection.Stay) return DirectionDescription(direction);
        (int x, int y) = Components(direction);
        float horizontal = x == 0 ? 0f : (horizontalSize == Magnitude.Medium ? 20f : 12f) * x;
        float horizontalRoom = x < 0
            ? Room(snapshot.SolidDistances.Left, snapshot.WorldDistances.Left) - GameMovement.HorizontalReserveCells * snapshot.MovementReserveScale
            : Room(snapshot.SolidDistances.Right, snapshot.WorldDistances.Right) - GameMovement.HorizontalReserveCells * snapshot.MovementReserveScale;
        float verticalRoom = y < 0
            ? Room(snapshot.SolidDistances.Up, snapshot.WorldDistances.Up) - GameMovement.UpperReserveCells * snapshot.MovementReserveScale
            : Room(snapshot.SolidDistances.Down, snapshot.WorldDistances.Down) - GameMovement.LowerReserveCells * snapshot.MovementReserveScale;
        float travelEstimate = burst ? (y < 0 ? 18f : 22f) : (y < 0 ? 10f : 18f);
        float vertical = y * Math.Max(0f, Math.Min(travelEstimate, verticalRoom));
        Vector2 displacement = new(horizontal * 16f, vertical * 16f);
        Vector2 playerAfter = snapshot.Player.Center + displacement;
        string bossClearance = string.Join("; ", snapshot.Bosses.Concat(snapshot.Parts)
            .Where(boss => Vector2.Distance(boss.Center, snapshot.Player.Center) < 70f * 16f)
            .Select(boss =>
            {
                float current = Vector2.Distance(boss.Center, snapshot.Player.Center);
                float after = Vector2.Distance(boss.Center + boss.Velocity * 36f, playerAfter);
                if (pvp) return $"{boss.Name}: approximate distance {current:0} -> {after:0} pixels; an estimate, not a guaranteed position";
                return $"{boss.Name}: {(after - current > 5f * 16f ? "opens space" :
                    after - current < -5f * 16f ? "closes space" : "little separation change")}";
            }));
        int exposed = 0;
        foreach (CombatEntity projectile in snapshot.Projectiles)
        {
            Vector2 relative = projectile.Center - snapshot.Player.Center;
            Vector2 relativeStep = projectile.Velocity - displacement / 36f;
            float speedSquared = relativeStep.LengthSquared();
            float frame = speedSquared < 0.01f ? 36f :
                Math.Clamp(-Vector2.Dot(relative, relativeStep) / speedSquared, 4f, 36f);
            if ((relative + relativeStep * frame).LengthSquared() < 10f * 16f * 10f * 16f)
                exposed++;
        }
        return $"{DirectionDescription(direction)} Approximate displacement over 0.6 seconds: " +
            $"{horizontal:0} cells right, {vertical:0} cells down. " +
            (burst ? "Mount for faster vertical travel while holding W or S. " :
                y == 0 ? "Ordinary horizontal travel. " : "Ordinary vertical travel. ") +
            $"{(pvp ? "Opponent distance" : "Boss clearance trend if current motion continues")}: {(bossClearance.Length == 0 ? "no nearby boss" : bossClearance)}. " +
            $"Projectile corridor: {(exposed == 0 ? "clear" : exposed < 3 ? "exposed" : "crowded")}. " +
            $"Available room beyond reserve: horizontal {(x == 0 ? 0f : Math.Max(0f, horizontalRoom)):0}, " +
            $"vertical {(y == 0 ? 0f : Math.Max(0f, verticalRoom)):0} cells.";
    }

    private static string ActionDescription(DodgeOption option, string mountName,
        string mountSteering)
    {
        DodgeIntent intent = option.Intent;
        if (intent.Direction == DodgeDirection.Stay)
            return "Stay only if no approaching threat requires movement and I have room to escape later.";
        (int x, int y) = Components(intent.Direction);
        string horizontal = x == 0 ? "" : $"Move {(x < 0 ? "left" : "right")}. ";
        string hook = option.HookDistanceCells is float distance
            ? $"Grapple toward a surface {distance:0.#} cells away, then jump free one or two frames after latching. " : "";
        string vertical = y switch
        {
            < 0 when intent.VerticalSize == Magnitude.Large =>
                $"Jump while holding W and mount {mountName} during the rise. {mountSteering} and may hit a ceiling; dismount at the apex. ",
            < 0 => "Jump or use an extra jump while holding W. ",
            > 0 when intent.VerticalSize == Magnitude.Large =>
                $"Mount {mountName} and hold S for a fast descent. {mountSteering}; dismount when the descent ends. ",
            > 0 => "Hold S to descend or pass through a platform. ",
            _ => ""
        };
        return horizontal + hook + vertical;
    }

    private static (int X, int Y) Components(DodgeDirection direction) => direction switch
    {
        DodgeDirection.Up => (0, -1), DodgeDirection.UpRight => (1, -1),
        DodgeDirection.Right => (1, 0), DodgeDirection.DownRight => (1, 1),
        DodgeDirection.Down => (0, 1), DodgeDirection.DownLeft => (-1, 1),
        DodgeDirection.Left => (-1, 0), DodgeDirection.UpLeft => (-1, -1),
        _ => (0, 0)
    };

    private static int Room(float solid, float world)
        => (int)Math.Clamp(Math.Min(solid, world - GameSensor.WorldEdgeCells * 16f) / 16f, 0f, 60f);

    private static object BossThreat(CombatEntity entity, CombatSnapshot snapshot)
        => new
        {
            id = entity.Name, name = entity.Name,
            health_percent = snapshot.BossMotion.FirstOrDefault(motion => motion.Id == entity.Id) is { MaxHealth: > 0 } motion
                ? motion.Health * 100 / motion.MaxHealth : 100,
            contact_damage_percent_of_my_hp = entity.Damage * 100 / Math.Max(1, snapshot.Health),
            cells_to_my_right = (int)((entity.Center.X - snapshot.Player.Center.X) / 16f),
            cells_above_me = (int)((snapshot.Player.Center.Y - entity.Center.Y) / 16f),
            speed_to_the_right = (int)(entity.Velocity.X * 60f / 16f),
            speed_upward = (int)(-entity.Velocity.Y * 60f / 16f),
            speed_cells_per_second = (int)((Math.Abs(entity.Velocity.X) + Math.Abs(entity.Velocity.Y)) * 60f / 16f),
            top_speed_in_the_last_second = (int)snapshot.BossMotion.FirstOrDefault(motion => motion.Id == entity.Id).BossFastestInTheLastSecond,
            frames_until_it_hits_me = ContactTime(entity, snapshot.Player)
        };

    private static object ProjectileThreat(CombatEntity entity, CombatSnapshot snapshot) => new
    {
        name = entity.Name,
        damage_percent_of_my_hp = entity.Damage * 100 / Math.Max(1, snapshot.Health),
        cells_to_my_right = (int)((entity.Center.X - snapshot.Player.Center.X) / 16f),
        cells_above_me = (int)((snapshot.Player.Center.Y - entity.Center.Y) / 16f),
        speed_to_the_right = (int)(entity.Velocity.X * 60f / 16f),
        speed_upward = (int)(-entity.Velocity.Y * 60f / 16f),
        frames_until_it_hits_me = ContactTime(entity, snapshot.Player)
    };

    private static object ContactTime(CombatEntity entity, CombatEntity player)
    {
        int frames = FramesUntilContact(entity, player);
        return frames >= 0 ? frames : "not heading at me";
    }

    private static int FramesUntilContact(CombatEntity entity, CombatEntity player)
    {
        float dx = entity.Center.X - player.Center.X;
        float dy = entity.Center.Y - player.Center.Y;
        float gapX = Math.Abs(dx) - (entity.Size.X + player.Size.X) * 0.5f;
        float gapY = Math.Abs(dy) - (entity.Size.Y + player.Size.Y) * 0.5f;
        if (gapX <= 0f && gapY <= 0f) return 0;

        float relativeX = entity.Velocity.X - player.Velocity.X;
        float relativeY = entity.Velocity.Y - player.Velocity.Y;
        float closeX = dx > 0f ? -relativeX : relativeX;
        float closeY = dy > 0f ? -relativeY : relativeY;
        float framesX = gapX <= 0f ? 0f : closeX > 0.1f ? gapX / closeX : -1f;
        float framesY = gapY <= 0f ? 0f : closeY > 0.1f ? gapY / closeY : -1f;
        if (framesX < 0f || framesY < 0f) return -1;
        float frames = Math.Max(framesX, framesY);
        return frames > 600f ? -1 : (int)frames;
    }

}

