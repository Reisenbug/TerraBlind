using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;

namespace TerraBlind.JevariaCombat;

public enum DodgeDirection { Up, UpRight, Right, DownRight, Down, DownLeft, Left, UpLeft, Stay }
public enum Magnitude { None, Small, Medium, Large }

public readonly record struct DodgeIntent(
    DodgeDirection Direction, Magnitude HorizontalSize, Magnitude VerticalSize, bool Dash)
{
    public static DodgeIntent Idle => new(DodgeDirection.Stay, Magnitude.None, Magnitude.None, false);

    public bool Valid => Direction switch
    {
        DodgeDirection.Stay => HorizontalSize == Magnitude.None && VerticalSize == Magnitude.None,
        DodgeDirection.Left or DodgeDirection.Right => HorizontalSize is Magnitude.Small or Magnitude.Medium && VerticalSize == Magnitude.None,
        DodgeDirection.Up or DodgeDirection.Down => HorizontalSize == Magnitude.None && VerticalSize != Magnitude.None,
        _ => HorizontalSize is Magnitude.Small or Magnitude.Medium && VerticalSize != Magnitude.None
    };
}

public readonly record struct CombatEntity(
    int Id, string Name, Vector2 Center, Vector2 Velocity,
    Vector2 Size, int Damage);

public readonly record struct BoundaryDistances(
    float Left, float Right, float Up, float Down);

public readonly record struct BossMotion(
    int Id, float BossSpeedCellsPerSecond, float BossFastestInTheLastSecond,
    int Health, int MaxHealth);

public readonly record struct DodgeOption(DodgeIntent Intent, float? HookDistanceCells,
    Vector2? HookTarget = null);

public sealed record CombatSnapshot(
    ulong Sequence, ulong Tick, CombatEntity Player, int Health, int MaxHealth,
    bool LosingHealthOverTime,
    CombatEntity Boss, IReadOnlyList<CombatEntity> Bosses,
    IReadOnlyList<BossMotion> BossMotion,
    IReadOnlyList<CombatEntity> Parts,
    IReadOnlyList<CombatEntity> Projectiles,
    BoundaryDistances SolidDistances, BoundaryDistances WorldDistances,
    float PlatformDistanceBelow,
    bool CanDash, bool CanDoubleJump, bool CanFly, bool HasHook,
    bool HasMount, float ShotSpeed, int ShotProjectileType, string WeaponName,
    IReadOnlyList<DodgeOption> AvailableActions,
    DodgeIntent PreviousIntent, long PreviousDurationMs)
{
    public int MountType { get; init; } = -1;
}

public sealed record DodgeDecision(
    DodgeIntent Intent, IReadOnlyDictionary<string, IReadOnlyDictionary<string, float>> Probabilities,
    float Confidence, long LatencyMs, ulong Sequence);

public readonly record struct ActionResult(DodgeIntent Requested, DodgeIntent Applied, string Reason);

public interface ICombatSensor
{
    bool Observe(Player player);
    CombatSnapshot? Capture(Player player, ulong sequence, DodgeIntent previous, long previousDurationMs);
}

public interface IMovementDriver
{
    ActionResult Apply(Player player, DodgeIntent intent, CombatSnapshot snapshot);
    void Release(Player player);
}

public interface IAttackDriver
{
    int? SelectFirstWeapon(Player player);
    void RestoreWeapon(Player player);
    bool TryAttack(Player player, CombatSnapshot snapshot, out Vector2 target);
    void Release(Player player);
}

public interface IDodgeBrain
{
    System.Threading.Tasks.Task<DodgeDecision> DecideAsync(CombatSnapshot snapshot,
        System.Threading.CancellationToken cancellationToken);
}

