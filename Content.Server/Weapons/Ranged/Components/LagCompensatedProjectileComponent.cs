using System.Numerics;
using Content.Server.Weapons.Ranged.Systems;
using Robust.Shared.Map;

namespace Content.Server.Weapons.Ranged.Components;

/// <summary>
/// A projectile that hits lag compensated entities where its shooter saw them, rather than where they are now.
/// </summary>
/// <remarks>
/// Physics ignores lag compensated entities for this projectile. Instead, <see cref="GunLagCompensationSystem"/>
/// sweeps its path every tick against their past positions, and moves it onto any entity it hits so the hit happens
/// through a normal collision.
/// </remarks>
[RegisterComponent, AutoGenerateComponentPause, Access(typeof(GunLagCompensationSystem))]
public sealed partial class LagCompensatedProjectileComponent : Component
{
    /// <summary>
    /// How far behind the server the shooter saw other entities.
    /// </summary>
    [ViewVariables]
    public TimeSpan Rewind;

    /// <summary>
    /// The shooter, whose view this compensates for.
    /// </summary>
    [ViewVariables]
    public EntityUid Shooter;

    /// <summary>
    /// Where the swept path continues from on the next tick.
    /// </summary>
    [ViewVariables]
    public MapCoordinates SweptTo;

    /// <summary>
    /// How far hits moved the projectile away from the path the shooter sees it fly along.
    /// </summary>
    [ViewVariables]
    public Vector2 Offset;

    /// <summary>
    /// When the projectile stops being lag compensated.
    /// </summary>
    [ViewVariables, AutoPausedField]
    public TimeSpan EndTime;

    /// <summary>
    /// The lag compensated entity this projectile was moved onto, which physics lets it collide with.
    /// </summary>
    [ViewVariables]
    public EntityUid? Target;

    /// <summary>
    /// Entities already hit, so a projectile that passes through something can't hit it twice.
    /// </summary>
    [ViewVariables]
    public HashSet<EntityUid> Hit = new();
}
