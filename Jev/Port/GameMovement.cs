using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace TerraBlind.JevariaCombat;

public sealed class GameMovement : IMovementDriver
{
    private const float MountEntrySpeed = 4f;
    public static int EquippedVerticalMount(Player player) => player.miscEquips[3].type switch
    {
        ItemID.SlimySaddle => MountID.Slime,
        ItemID.QueenSlimeMountSaddle => MountID.QueenSlime,
        _ => MountID.None
    };
    public const int HorizontalReserveCells = 35;
    public const int UpperReserveCells = 18;
    public const int LowerReserveCells = 4;

    public static bool Blocked(int horizontal, int vertical, BoundaryDistances solid,
        BoundaryDistances world, Vector2 velocity)
    {
        float horizontalReserve = Math.Max(HorizontalReserveCells * 16f,
            Math.Abs(velocity.X) * 60f * 1.2f) +
            Math.Max(0f, horizontal * velocity.X) * 18f;
        float upperReserve = Math.Max(UpperReserveCells * 16f,
            Math.Max(0f, -velocity.Y) * 60f * 0.6f) +
            5f * 16f + Math.Max(0f, -velocity.Y) * 18f;
        return horizontal < 0 && (solid.Left <= horizontalReserve ||
                world.Left <= horizontalReserve + GameSensor.WorldEdgeCells * 16f) ||
            horizontal > 0 && (solid.Right <= horizontalReserve ||
                world.Right <= horizontalReserve + GameSensor.WorldEdgeCells * 16f) ||
            vertical < 0 && (solid.Up <= upperReserve ||
                world.Up <= upperReserve + GameSensor.WorldEdgeCells * 16f) ||
            vertical > 0 && (solid.Down <= LowerReserveCells * 16f ||
                world.Down <= (LowerReserveCells + GameSensor.WorldEdgeCells) * 16f);
    }

    public static IReadOnlyList<DodgeOption> AvailableActions(Player player,
        BoundaryDistances solid, BoundaryDistances world, bool hasHook, bool hasMount)
    {
        var options = new List<DodgeOption> { new(DodgeIntent.Idle, null) };
        (int X, Magnitude Size)[] horizontal =
        {
            (0, Magnitude.None), (-1, Magnitude.Small), (-1, Magnitude.Medium),
            (1, Magnitude.Small), (1, Magnitude.Medium)
        };
        (int Y, Magnitude Size)[] vertical =
        {
            (0, Magnitude.None), (-1, Magnitude.Small), (-1, Magnitude.Medium),
            (-1, Magnitude.Large), (1, Magnitude.Small), (1, Magnitude.Large)
        };
        var hookTargets = new Dictionary<(int X, int Y, bool NearHorizontal), Vector2?>();
        foreach (var (x, horizontalSize) in horizontal)
        foreach (var (y, verticalSize) in vertical)
        {
            if (x == 0 && y == 0) continue;
            if (Blocked(x, y, solid, world, player.velocity)) continue;
            bool horizontalHook = horizontalSize == Magnitude.Medium;
            bool verticalHook = y < 0 && verticalSize >= Magnitude.Medium;
            bool upMount = y < 0 && verticalSize == Magnitude.Large;
            bool downMount = y > 0 && verticalSize == Magnitude.Large;
            if ((upMount || downMount) && !hasMount || downMount && horizontalHook) continue;
            Vector2? hookTarget = null;
            if (horizontalHook || verticalHook)
            {
                var direction = (horizontalHook ? x : 0, verticalHook ? y : 0,
                    horizontalHook && !verticalHook);
                if (!hookTargets.TryGetValue(direction, out hookTarget))
                {
                    if (hasHook && TryHookPoint(player, direction.Item1, direction.Item2,
                        direction.Item3, out Vector2 target))
                        hookTarget = target;
                    hookTargets[direction] = hookTarget;
                }
                if (hookTarget is null && !upMount) continue;
                if (hookTarget is null && horizontalHook) continue;
            }
            options.Add(new DodgeOption(new DodgeIntent(Compose(x, y), horizontalSize,
                verticalSize, false), hookTarget is Vector2 point
                    ? Vector2.Distance(player.Center, point) / 16f : null, hookTarget));
        }
        return options;
    }

    private ulong _hookSequence;
    private ulong _jumpSequence;
    private bool _autoHookActive;
    private bool _hookHeld;
    private bool _jumpHeld;
    private int _jumpTicks;
    private ulong _hookIssuedTick;
    private ulong _hookLatchedTick;
    private ulong _hookCooldownUntil;
    private Vector2 _hookTarget;
    private bool _autoMountActive;
    private int _autoMountType = MountID.None;
    private bool _autoMountForDown;
    private bool _mountRiseSeen;
    private bool _mountFallSeen;
    private ulong _mountStartTick;
    private ulong _upMountSequence;

    public ActionResult Apply(Player player, DodgeIntent intent, CombatSnapshot snapshot)
    {
        if (!intent.Valid) return new ActionResult(intent, DodgeIntent.Idle, "invalid intent");

        Vector2? plannedHook = null;
        foreach (DodgeOption option in snapshot.AvailableActions)
            if (option.Intent == intent) { plannedHook = option.HookTarget; break; }
        (int horizontal, int vertical) = Components(intent.Direction);
        Magnitude horizontalSize = intent.HorizontalSize;
        Magnitude verticalSize = intent.VerticalSize;
        string reason = "";
        Rectangle box = player.Hitbox;
        BoundaryDistances solid = GameSensor.ScanSolids(box);
        BoundaryDistances world = new(box.Left, Main.maxTilesX * 16f - box.Right,
            box.Top, Main.maxTilesY * 16f - box.Bottom);

        if (Blocked(-1, 0, solid, world, player.velocity))
            if (horizontal < 0) { horizontal = 0; reason = "left blocked"; }
        if (Blocked(1, 0, solid, world, player.velocity))
            if (horizontal > 0) { horizontal = 0; reason = "right blocked"; }

        if (Blocked(0, -1, solid, world, player.velocity))
        {
            if (vertical < 0) { vertical = 0; reason = "upper boundary"; }
        }
        if (Blocked(0, 1, solid, world, player.velocity))
        {
            if (vertical > 0) { vertical = 0; reason = "lower boundary"; }
        }

        int mountType = EquippedVerticalMount(player);
        if (verticalSize == Magnitude.Large && mountType == MountID.None)
        {
            verticalSize = vertical < 0 ? Magnitude.Medium : Magnitude.Small;
            reason = "vertical mount unavailable";
        }
        bool upMount = mountType != MountID.None && vertical < 0 && verticalSize == Magnitude.Large;
        bool downMount = mountType != MountID.None && vertical > 0 && verticalSize == Magnitude.Large;
        if (downMount && horizontalSize == Magnitude.Medium)
        {
            horizontalSize = Magnitude.Small;
            reason = "horizontal grapple unavailable while mounted";
        }
        if (_autoMountActive && (!player.mount.Active || player.mount.Type != _autoMountType ||
            mountType != _autoMountType))
            DismountAuto(player);
        if (_autoMountActive && ((!upMount && !downMount) || upMount && _autoMountForDown ||
            downMount && !_autoMountForDown ||
            _autoMountForDown && _mountFallSeen && player.velocity.Y <= 0f ||
            !_autoMountForDown && (_mountRiseSeen && player.velocity.Y >= 0f ||
            !_mountRiseSeen && Main.GameUpdateCount - _mountStartTick > 6)))
        {
            DismountAuto(player);
            reason = "vertical mount dismount";
        }

        if (horizontal == 0) horizontalSize = Magnitude.None;
        if (vertical == 0) verticalSize = Magnitude.None;
        bool horizontalHook = horizontal != 0 && horizontalSize == Magnitude.Medium;
        if (_autoMountActive && horizontalHook)
        {
            horizontalSize = Magnitude.Small;
            horizontalHook = false;
            reason = "hook deferred while mounted";
        }
        bool verticalHook = vertical < 0 && verticalSize >= Magnitude.Medium &&
            !_autoMountActive && (_autoHookActive || player.velocity.Y > -MountEntrySpeed);
        bool wantHook = !downMount && (horizontalHook || verticalHook);
        bool hookJump = _autoHookActive && _hookLatchedTick > 0 &&
            player.grapCount > 0 && Main.GameUpdateCount > _hookLatchedTick;
        bool hookFinished = _autoHookActive && _hookLatchedTick > 0 && player.grapCount == 0;
        if (_autoHookActive && player.grapCount > 0 && _hookLatchedTick == 0)
        {
            _hookLatchedTick = Main.GameUpdateCount;
            _jumpHeld = false;
            reason = "grapple latched";
        }
        if (hookFinished) FinishAutoHook();
        else if (_autoHookActive && _hookLatchedTick == 0 &&
            (!wantHook || Main.GameUpdateCount - _hookIssuedTick >= 45))
        {
            Release(player);
            reason = "grapple missed";
        }
        if (player.grapCount > 0 && !_autoHookActive)
            return new ActionResult(intent, DodgeIntent.Idle, "manual grapple");

        if (wantHook && !_autoHookActive && !hookFinished && player.grapCount == 0 &&
            Main.GameUpdateCount >= _hookCooldownUntil && _hookSequence != snapshot.Sequence)
        {
            _hookSequence = snapshot.Sequence;
            if (snapshot.HasHook && TrySelectedHookPoint(player, plannedHook,
                horizontalHook ? horizontal : 0, verticalHook ? vertical : 0,
                horizontalHook && !verticalHook, out Vector2 hook))
            {
                _autoHookActive = true;
                _hookIssuedTick = Main.GameUpdateCount;
                _hookLatchedTick = 0;
                _hookTarget = hook;
                Main.mouseX = (int)(hook.X - Main.screenPosition.X);
                Main.mouseY = (int)(hook.Y - Main.screenPosition.Y);
                player.releaseHook = true;
                player.controlHook = true;
                _hookHeld = true;
                reason = "grapple";
            }
            else
            {
                if (horizontalHook) horizontalSize = Magnitude.Small;
                if (verticalSize >= Magnitude.Medium && !upMount) verticalSize = Magnitude.Small;
                reason = "grapple target lost";
            }
        }
        else if (_autoHookActive && player.grapCount == 0)
        {
            reason = "grapple";
            _hookHeld = !_hookHeld;
            player.controlHook = _hookHeld;
            if (_hookHeld)
            {
                Main.mouseX = (int)(_hookTarget.X - Main.screenPosition.X);
                Main.mouseY = (int)(_hookTarget.Y - Main.screenPosition.Y);
            }
        }
        else player.controlHook = false;
        if (hookJump)
        {
            player.controlHook = false;
            player.releaseJump = true;
            reason = "grapple jump cancel";
        }
        if (wantHook && !_autoHookActive && !hookFinished &&
            (horizontalSize == Magnitude.Medium || verticalSize >= Magnitude.Medium))
        {
            if (horizontalHook) horizontalSize = Magnitude.Small;
            if (verticalSize >= Magnitude.Medium && !upMount) verticalSize = Magnitude.Small;
            reason = _hookSequence == snapshot.Sequence ? "grapple completed" :
                Main.GameUpdateCount < _hookCooldownUntil ? "grapple cooling down" :
                "grapple target lost";
        }

        bool canJump = !_autoHookActive || player.grapCount == 0;
        player.controlLeft = horizontal < 0;
        player.controlRight = horizontal > 0;
        player.controlUp = player.grapCount == 0 && vertical < 0;
        player.controlDown = player.grapCount == 0 && vertical > 0;
        bool useExtraJump = canJump && vertical < 0 &&
            snapshot.CanDoubleJump && _jumpSequence != snapshot.Sequence;
        player.controlJump = hookJump || ApplyJump(player, canJump && vertical < 0,
            useExtraJump, snapshot.Sequence);
        if (upMount && !_autoMountActive && _upMountSequence != snapshot.Sequence &&
            player.velocity.Y <= -MountEntrySpeed && !_autoHookActive && player.grapCount == 0)
        {
            if (MountAuto(player, mountType, false))
            {
                _upMountSequence = snapshot.Sequence;
                reason = "vertical mount jump";
            }
            else
            {
                verticalSize = Magnitude.Small;
                reason = "vertical mount unavailable";
            }
        }
        else if (upMount && !_autoMountActive && !_autoHookActive && reason.Length == 0)
            reason = "vertical mount waiting for upward speed";
        if (downMount && !_autoMountActive && !_autoHookActive && player.grapCount == 0)
        {
            if (MountAuto(player, mountType, true)) reason = "vertical mount descent";
            else
            {
                verticalSize = Magnitude.Small;
                reason = "vertical mount unavailable";
            }
        }
        if (_autoMountActive && !_autoMountForDown && player.velocity.Y < -0.1f)
            _mountRiseSeen = true;
        if (_autoMountActive && _autoMountForDown && player.velocity.Y > 0.1f)
            _mountFallSeen = true;
        var applied = new DodgeIntent(Compose(horizontal, vertical), horizontalSize, verticalSize, false);
        return new ActionResult(intent, applied, reason);
    }

    private static (int Horizontal, int Vertical) Components(DodgeDirection direction) => direction switch
    {
        DodgeDirection.Up => (0, -1),
        DodgeDirection.UpRight => (1, -1),
        DodgeDirection.Right => (1, 0),
        DodgeDirection.DownRight => (1, 1),
        DodgeDirection.Down => (0, 1),
        DodgeDirection.DownLeft => (-1, 1),
        DodgeDirection.Left => (-1, 0),
        DodgeDirection.UpLeft => (-1, -1),
        _ => (0, 0)
    };

    private static DodgeDirection Compose(int horizontal, int vertical) => (horizontal, vertical) switch
    {
        (0, -1) => DodgeDirection.Up,
        (1, -1) => DodgeDirection.UpRight,
        (1, 0) => DodgeDirection.Right,
        (1, 1) => DodgeDirection.DownRight,
        (0, 1) => DodgeDirection.Down,
        (-1, 1) => DodgeDirection.DownLeft,
        (-1, 0) => DodgeDirection.Left,
        (-1, -1) => DodgeDirection.UpLeft,
        _ => DodgeDirection.Stay
    };

    public void Release(Player player)
    {
        _jumpHeld = false;
        _jumpTicks = 0;
        DismountAuto(player);
        if (!_autoHookActive) return;
        if (_hookLatchedTick > 0 && player.grapCount > 0)
        {
            player.releaseJump = true;
            player.controlJump = true;
        }
        else player.RemoveAllGrapplingHooks();
        FinishAutoHook();
    }

    private void FinishAutoHook()
    {
        _autoHookActive = false;
        _hookHeld = false;
        _hookLatchedTick = 0;
        _hookCooldownUntil = Main.GameUpdateCount + 2;
    }

    private bool MountAuto(Player player, int mountType, bool down)
    {
        if (player.mount.Active || !player.mount.CanMount(mountType, player)) return false;
        float entrySpeed = player.velocity.Y;
        player.mount.SetMount(mountType, player);
        if (!player.mount.Active || player.mount.Type != mountType) return false;
        _autoMountActive = true;
        _autoMountType = mountType;
        _autoMountForDown = down;
        _mountRiseSeen = false;
        _mountFallSeen = false;
        _mountStartTick = Main.GameUpdateCount;
        global::TerraBlind.DiagLog.Write(
            $"vertical mount: type={mountType}; tick={Main.GameUpdateCount}; direction={(down ? "down" : "up")}; " +
            $"velocity_before={entrySpeed:0.00}; velocity_after={player.velocity.Y:0.00}");
        return true;
    }

    private void DismountAuto(Player player)
    {
        if (!_autoMountActive) return;
        if (player.mount.Active && player.mount.Type == _autoMountType)
            player.mount.Dismount(player);
        _autoMountActive = false;
        _autoMountType = MountID.None;
        _autoMountForDown = false;
        _mountRiseSeen = false;
        _mountFallSeen = false;
    }

    private bool ApplyJump(Player player, bool rise, bool hop, ulong sequence)
    {
        if (_jumpHeld)
        {
            if (hop)
            {
                _jumpHeld = false;
                return false;
            }
            _jumpTicks++;
            bool landed = player.velocity.Y == 0f && _jumpTicks > 2;
            if (rise && !landed) return true;
            if (player.velocity.Y < 0f && _jumpTicks < 30 && !landed) return true;
            _jumpHeld = false;
            return false;
        }

        if (!rise && !hop) return false;
        if (hop) _jumpSequence = sequence;
        _jumpHeld = true;
        _jumpTicks = 0;
        return true;
    }

    private static bool TryHookPoint(Player player, int horizontal,
        int vertical, bool nearHorizontal, out Vector2 target)
    {
        target = default;
        float range = HookRange(player);
        if (range <= 0f) return false;
        foreach (float tilt in nearHorizontal ? new[] { 0f, -0.35f, 0.35f } : new[] { 0f })
        {
            Vector2 step = Vector2.Normalize(new Vector2(horizontal, nearHorizontal ? tilt : vertical)) * 4f;
            Vector2 point = player.Center;
            for (float distance = 0f; distance <= range; distance += 4f, point += step)
            {
                int x = (int)(point.X / 16f);
                int y = (int)(point.Y / 16f);
                if (!WorldGen.InWorld(x, y, 2)) break;
                Tile tile = Main.tile[x, y];
                if (!tile.HasTile || tile.IsActuated ||
                    !Main.tileSolid[tile.TileType] && tile.TileType != TileID.MinecartTrack) continue;
                Vector2 found = new(x * 16f + 8f, y * 16f + 8f);
                if (Vector2.DistanceSquared(player.Center, found) <= 6f * 16f * 6f * 16f) break;
                target = found;
                return true;
            }
        }
        return false;
    }

    private static bool TrySelectedHookPoint(Player player, Vector2? planned,
        int horizontal, int vertical, bool nearHorizontal, out Vector2 target)
    {
        if (planned is Vector2 saved && ValidHookPoint(player, saved, horizontal, vertical))
        {
            target = saved;
            return true;
        }
        return TryHookPoint(player, horizontal, vertical, nearHorizontal, out target);
    }

    private static bool ValidHookPoint(Player player, Vector2 target, int horizontal, int vertical)
    {
        Vector2 offset = target - player.Center;
        float distance = offset.Length();
        if (distance <= 6f * 16f || distance > HookRange(player) ||
            horizontal != 0 && offset.X * horizontal <= 0f ||
            vertical != 0 && offset.Y * vertical <= 0f) return false;
        int x = (int)(target.X / 16f);
        int y = (int)(target.Y / 16f);
        if (!WorldGen.InWorld(x, y, 2)) return false;
        Tile tile = Main.tile[x, y];
        return tile.HasTile && !tile.IsActuated &&
            (Main.tileSolid[tile.TileType] || tile.TileType == TileID.MinecartTrack);
    }

    private static float HookRange(Player player)
    {
        int type = player.miscEquips[4].shoot;
        if (type <= 0 || !Main.projHook[type])
        {
            type = 0;
            for (int slot = 0; slot < 58 && type == 0; slot++)
            {
                int candidate = player.inventory[slot].shoot;
                if (candidate > 0 && Main.projHook[candidate]) type = candidate;
            }
            if (type == 0) return 0f;
        }
        if (type >= ProjectileID.Count)
            return Terraria.ModLoader.ProjectileLoader.GetProjectile(type)?.GrappleRange() ?? 0f;
        return type switch
        {
            13 or 396 or 865 => 300f,
            32 or 331 or 372 => 400f,
            73 or 74 => 440f,
            165 => 375f,
            256 => 350f,
            315 or 446 or 935 => 500f,
            322 or 332 => 550f,
            >= 646 and <= 649 => 550f,
            652 => 600f,
            >= 486 and <= 489 => 480f,
            >= 230 and <= 235 => 300f + (type - 230) * 30f,
            753 => 420f,
            _ => 2500f
        };
    }
}

