using System.Numerics;
using Content.Server.Movement.Components;
using Robust.Server.Player;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server.Movement.Systems;

/// <summary>
/// Stores a buffer of previous positions of the relevant entity.
/// Can be used to check the entity's position at a recent point in time.
/// </summary>
public sealed partial class LagCompensationSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;

    // I figured 500 ping is max, so 1.5 is 750.
    // Max ping I've had is 350ms from aus to spain.
    public static readonly TimeSpan BufferTime = TimeSpan.FromMilliseconds(750);

    /// <summary>
    /// Moves of at least this distance snap instead of being interpolated, matching MaxInterpolationDistance in the
    /// engine's client TransformSystem, which has no cvar.
    /// </summary>
    private const float MaxInterpolationDistance = 2f;

    public override void Initialize()
    {
        base.Initialize();
        Log.Level = LogLevel.Info;
        SubscribeLocalEvent<LagCompensationComponent, MoveEvent>(OnLagMove);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var curTime = _timing.CurTime;
        var earliestTime = curTime - BufferTime;

        // Cull any old ones from active updates
        // Probably fine to include ignored.
        var query = AllEntityQuery<LagCompensationComponent>();

        while (query.MoveNext(out var comp))
        {
            // Keep the newest sample that is older than the buffer, as that's where the entity was at the start of it.
            var stale = -1;
            foreach (var pos in comp.Positions)
            {
                if (pos.Time > earliestTime)
                    break;

                stale++;
            }

            for (var i = 0; i < stale; i++)
            {
                comp.Positions.Dequeue();
            }
        }
    }

    private void OnLagMove(EntityUid uid, LagCompensationComponent component, ref MoveEvent args)
    {
        if (!args.NewPosition.EntityId.IsValid())
            return; // probably being sent to nullspace for deletion.

        // If the entity was sitting still, record that it was still at its old position last tick.
        // Otherwise interpolating between samples would smear the move over the whole time it stood still.
        var curTime = _timing.CurTime;
        var lastTick = curTime - _timing.TickPeriod;

        if (args.OldPosition.EntityId.IsValid()
            && (component.Positions.Count == 0 || component.LastSampleTime < lastTick))
        {
            component.Positions.Enqueue((lastTick, args.OldPosition, args.OldRotation));
        }

        component.Positions.Enqueue((curTime, args.NewPosition, args.NewRotation));
        component.LastSampleTime = curTime;
    }

    /// <summary>
    /// Gets the server time at which the given tick was simulated.
    /// </summary>
    public TimeSpan GetTickTime(GameTick tick)
    {
        var curTick = _timing.CurTick;
        if (tick >= curTick)
            return _timing.CurTime;

        return _timing.CurTime - _timing.TickPeriod * (curTick.Value - tick.Value);
    }

    /// <summary>
    /// Gets where an entity was at the given server time, interpolating between recorded positions the same way
    /// clients interpolate between game states.
    /// </summary>
    /// <returns>False if the entity has no transform.</returns>
    public bool TryGetPastCoordinates(Entity<LagCompensationComponent?, TransformComponent?> ent,
        TimeSpan time,
        out EntityCoordinates coordinates,
        out Angle angle)
    {
        coordinates = EntityCoordinates.Invalid;
        angle = Angle.Zero;

        if (!Resolve(ent, ref ent.Comp2))
            return false;

        coordinates = ent.Comp2.Coordinates;
        angle = ent.Comp2.LocalRotation;

        // No history means it hasn't moved recently, so the current position is correct.
        if (!Resolve(ent, ref ent.Comp1, false) || ent.Comp1.Positions.Count == 0)
            return true;

        (TimeSpan Time, EntityCoordinates Coordinates, Angle Angle)? before = null;
        (TimeSpan Time, EntityCoordinates Coordinates, Angle Angle)? after = null;

        foreach (var pos in ent.Comp1.Positions)
        {
            if (pos.Time <= time)
            {
                before = pos;
                continue;
            }

            after = pos;
            break;
        }

        // Older than anything we have, so use the oldest sample.
        if (before is not { } from)
        {
            if (after is { } oldest)
            {
                coordinates = oldest.Coordinates;
                angle = oldest.Angle;
            }

            return true;
        }

        coordinates = from.Coordinates;
        angle = from.Angle;

        if (after is not { } to
            || to.Coordinates.EntityId != from.Coordinates.EntityId
            || (to.Coordinates.Position - from.Coordinates.Position).Length() >= MaxInterpolationDistance)
        {
            return true;
        }

        var t = (float) ((time - from.Time) / (to.Time - from.Time));
        coordinates = new EntityCoordinates(from.Coordinates.EntityId,
            Vector2.Lerp(from.Coordinates.Position, to.Coordinates.Position, t));
        angle = Angle.Lerp(from.Angle, to.Angle, t);
        return true;
    }

    public (EntityCoordinates Coordinates, Angle Angle) GetCoordinatesAngle(EntityUid uid, ICommonSession? pSession,
        TransformComponent? xform = null)
    {
        if (!Resolve(uid, ref xform))
            return (EntityCoordinates.Invalid, Angle.Zero);

        if (pSession == null || !TryComp<LagCompensationComponent>(uid, out var lag) || lag.Positions.Count == 0)
            return (xform.Coordinates, xform.LocalRotation);

        var angle = Angle.Zero;
        var coordinates = EntityCoordinates.Invalid;
        var ping = pSession.Ping;
        // Use 1.5 due to the trip buffer.
        var sentTime = _timing.CurTime - TimeSpan.FromMilliseconds(ping * 1.5);

        foreach (var pos in lag.Positions)
        {
            coordinates = pos.Coordinates;
            angle = pos.Angle;

            if (pos.Time >= sentTime)
                break;
        }

        if (coordinates == default)
        {
            Log.Debug($"No long comp coords found, using {xform.Coordinates}");
            coordinates = xform.Coordinates;
            angle = xform.LocalRotation;
        }
        else
        {
            Log.Debug($"Actual coords is {xform.Coordinates} and got {coordinates}");
        }

        return (coordinates, angle);
    }

    public Angle GetAngle(EntityUid uid, ICommonSession? session, TransformComponent? xform = null)
    {
        var (_, angle) = GetCoordinatesAngle(uid, session, xform);
        return angle;
    }

    public EntityCoordinates GetCoordinates(EntityUid uid, ICommonSession? session, TransformComponent? xform = null)
    {
        var (coordinates, _) = GetCoordinatesAngle(uid, session, xform);
        return coordinates;
    }
}
