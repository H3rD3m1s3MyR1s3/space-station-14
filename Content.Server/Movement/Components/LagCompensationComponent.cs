using Robust.Shared.Map;

namespace Content.Server.Movement.Components;

[RegisterComponent]
public sealed partial class LagCompensationComponent : Component
{
    [ViewVariables]
    public readonly Queue<(TimeSpan Time, EntityCoordinates Coordinates, Angle Angle)> Positions = new();

    /// <summary>
    /// Time of the newest entry in <see cref="Positions"/>.
    /// </summary>
    [ViewVariables]
    public TimeSpan LastSampleTime;
}
