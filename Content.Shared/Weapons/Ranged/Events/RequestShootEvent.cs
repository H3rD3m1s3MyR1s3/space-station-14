using Robust.Shared.Map;
using Robust.Shared.Serialization;
using Robust.Shared.Timing;

namespace Content.Shared.Weapons.Ranged.Events;

/// <summary>
/// Raised on the client to indicate it'd like to shoot.
/// </summary>
[Serializable, NetSerializable]
public sealed class RequestShootEvent : EntityEventArgs
{
    /// <summary>
    /// The gun shooting.
    /// </summary>
    public NetEntity Gun;

    /// <summary>
    /// The location the player is shooting at.
    /// </summary>
    public NetCoordinates Coordinates;

    /// <summary>
    /// The target the player is shooting at, if any.
    /// </summary>
    public NetEntity? Target;

    /// <summary>
    /// If the client wants to continuously shoot.
    /// If true, the gun will continue firing until a stop event is sent from the client.
    /// </summary>
    public bool Continuous;

    /// <summary>
    /// The last server state the client had applied when it fired.
    /// Other entities were drawn at roughly this tick, so the server uses it for lag compensation.
    /// </summary>
    public GameTick? ViewTick;
}
