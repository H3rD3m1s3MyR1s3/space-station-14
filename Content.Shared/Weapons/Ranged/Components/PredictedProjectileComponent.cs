using Content.Shared.Weapons.Ranged.Events;
using Robust.Shared.GameStates;

namespace Content.Shared.Weapons.Ranged.Components;

/// <summary>
/// Marks a projectile whose flight the shooter's client already drew when they fired.
/// The shooter's client hides this copy so they don't see the same bullet twice.
/// </summary>
/// <remarks>
/// Removed once the projectile does something the client couldn't have predicted, like being reflected or
/// passing through what it hit, so the shooter sees the real projectile from then on.
/// </remarks>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
public sealed partial class PredictedProjectileComponent : Component
{
    /// <summary>
    /// The player entity whose client predicted this projectile.
    /// Not networked, as only that player is sent this component.
    /// </summary>
    public EntityUid Shooter;

    /// <summary>
    /// The <see cref="RequestShootEvent.ShotId"/> of the request that fired it.
    /// </summary>
    [AutoNetworkedField]
    public int ShotId;

    /// <summary>
    /// Which projectile of the shot this is, counting every projectile the shot fired in order.
    /// </summary>
    [AutoNetworkedField]
    public int Index;

    public override bool SessionSpecific => true;
}
