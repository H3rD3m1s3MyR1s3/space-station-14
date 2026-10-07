using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Timing;

namespace Content.Shared.Weapons.Ranged.Systems;

/// <summary>
/// Lets shared code check shots against where the shooter saw other entities, rather than where they are now.
/// </summary>
/// <remarks>
/// Only the server keeps the history of where entities were, so on the client this does nothing.
/// </remarks>
public abstract class SharedGunLagCompensationSystem : EntitySystem
{
    /// <summary>
    /// Replaces any lag compensated entities in <paramref name="results"/> with hits against where they were while
    /// the shooter's client was showing <paramref name="viewTick"/>. The results stay sorted by distance.
    /// </summary>
    /// <param name="viewTick">The last server tick the shooter's client had applied when firing.</param>
    public virtual void RewindRay(EntityUid shooter,
        MapId mapId,
        CollisionRay ray,
        float maxDistance,
        GameTick viewTick,
        List<RayCastResults> results)
    {
    }
}
