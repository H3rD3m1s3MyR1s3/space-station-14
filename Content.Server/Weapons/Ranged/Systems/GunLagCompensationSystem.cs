using System.Numerics;
using Content.Server.Movement.Components;
using Content.Server.Movement.Systems;
using Content.Shared.CCVar;
using Content.Shared.Weapons.Hitscan;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.Configuration;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Components;
using Robust.Shared.Player;
using Robust.Shared.Timing;
using PhysicsTransform = Robust.Shared.Physics.Transform;

namespace Content.Server.Weapons.Ranged.Systems;

/// <summary>
/// Rewinds lag compensated entities to where a shooter saw them when checking what a hitscan shot hit.
/// Without this, players have to lead moving targets by their ping plus the client's interpolation delay.
/// </summary>
/// <remarks>
/// Entities are never actually moved. Their fixtures are tested against the ray at their past position instead,
/// so walls and other static geometry always use the present state.
/// </remarks>
public sealed partial class GunLagCompensationSystem : SharedGunLagCompensationSystem
{
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private LagCompensationSystem _lag = default!;
    [Dependency] private SharedContainerSystem _container = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    [Dependency] private EntityQuery<FixturesComponent> _fixturesQuery = default!;
    [Dependency] private EntityQuery<MapGridComponent> _gridQuery = default!;
    [Dependency] private EntityQuery<MapComponent> _mapQuery = default!;
    [Dependency] private EntityQuery<PhysicsComponent> _physicsQuery = default!;

    private bool _enabled;
    private TimeSpan _maxRewind;
    private TimeSpan _slack;

    /// <summary>
    /// Every lag compensated entity, where it was when <see cref="_candidatesTick"/> started, and how far from there
    /// its fixtures can have reached at any time a shot may be rewound to.
    /// </summary>
    /// <remarks>
    /// Rays are checked against this instead of an entity lookup. There are far fewer lag compensated entities than
    /// entities in a lookup's area, which are mostly items, walls and the contents of containers.
    /// </remarks>
    private readonly List<(Entity<LagCompensationComponent> Entity, MapId MapId, Vector2 Position, float Reach)> _candidates = new();

    private GameTick? _candidatesTick;

    public override void Initialize()
    {
        base.Initialize();

        Subs.CVar(_cfg, CCVars.CombatGunLagCompensation, value => _enabled = value, true);
        Subs.CVar(_cfg, CCVars.CombatGunLagCompensationMax, value => _maxRewind = TimeSpan.FromSeconds(value), true);
        Subs.CVar(_cfg, CCVars.CombatGunLagCompensationSlack, value => _slack = TimeSpan.FromSeconds(value), true);
    }

    public override void RewindRay(EntityUid shooter,
        MapId mapId,
        CollisionRay ray,
        float maxDistance,
        GameTick viewTick,
        List<RayCastResults> results)
    {
        if (!_enabled || !TryComp<ActorComponent>(shooter, out var actor))
            return;

        var rewind = GetRewind(viewTick, actor.PlayerSession);
        if (rewind <= TimeSpan.Zero)
            return;

        RewindRay(shooter, mapId, ray, maxDistance, _timing.CurTime - rewind, results);
    }

    /// <summary>
    /// Works out how far back to rewind for a shot from a client that was seeing the given tick.
    /// </summary>
    public TimeSpan GetRewind(GameTick viewTick, ICommonSession session)
    {
        // Clients draw other entities interpolated between the previous state and this one, so aim for the middle.
        var rewind = _timing.CurTime - _lag.GetTickTime(viewTick) + _timing.TickPeriod / 2;

        // Don't trust the client to tell us an arbitrarily old tick.
        var allowed = TimeSpan.FromMilliseconds(session.Ping) + _slack;
        var cap = TimeSpan.FromTicks(Math.Min(_maxRewind.Ticks, LagCompensationSystem.BufferTime.Ticks));

        // Negative cvars mean no rewind, rather than Math.Clamp throwing.
        var max = Math.Max(0, Math.Min(allowed.Ticks, cap.Ticks));

        return TimeSpan.FromTicks(Math.Clamp(rewind.Ticks, 0, max));
    }

    /// <summary>
    /// Replaces any lag compensated entities in <paramref name="results"/> with hits against where they were at
    /// <paramref name="time"/>. Entities that can't be rewound keep their present hits.
    /// The results stay sorted by distance.
    /// </summary>
    public void RewindRay(EntityUid? ignored,
        MapId mapId,
        CollisionRay ray,
        float maxDistance,
        TimeSpan time,
        List<RayCastResults> results)
    {
        UpdateCandidates();

        var direction = ray.Direction.Normalized();
        var end = ray.Position + direction * maxDistance;

        var added = false;

        foreach (var (candidate, candidateMap, position, reach) in _candidates)
        {
            var uid = candidate.Owner;

            // Can't have been in the ray's path within the rewind window, so skip the expensive checks.
            if (candidateMap != mapId
                || uid == ignored
                || DistanceToSegmentSquared(position, ray.Position, end) > reach * reach
                || !_physicsQuery.TryComp(uid, out var physics)
                || !physics.CanCollide
                || !_fixturesQuery.TryComp(uid, out var fixtures))
            {
                continue;
            }

            // A past position inside a container can't be checked against the present container state.
            if (!_lag.TryGetPastCoordinates((uid, candidate.Comp, null), time, out var coordinates, out var localAngle)
                || (!_mapQuery.HasComp(coordinates.EntityId) && !_gridQuery.HasComp(coordinates.EntityId)))
            {
                continue;
            }

            var mapCoordinates = _transform.ToMapCoordinates(coordinates);
            if (mapCoordinates.MapId != mapId)
                continue;

            for (var i = results.Count - 1; i >= 0; i--)
            {
                if (results[i].HitEntity == uid)
                    results.RemoveAt(i);
            }

            var worldAngle = _transform.GetWorldRotation(coordinates.EntityId) + localAngle;
            var transform = new PhysicsTransform(mapCoordinates.Position, worldAngle);

            // Checked last as it's the slowest check and most candidates miss anyway.
            if (!HitscanRayMath.TryIntersectFixtures(ray.Position, direction, maxDistance, ray.CollisionMask, fixtures, transform, out var distance)
                || _container.IsEntityOrParentInContainer(uid))
            {
                continue;
            }

            results.Add(new RayCastResults(distance, ray.Position + direction * distance, uid));
            added = true;
        }

        // Removing entries kept the rest in order.
        if (added)
            results.Sort((a, b) => a.Distance.CompareTo(b.Distance));
    }

    /// <summary>
    /// Gathers every lag compensated entity once per tick, the first time a ray needs them.
    /// </summary>
    private void UpdateCandidates()
    {
        if (_candidatesTick == _timing.CurTick)
            return;

        _candidatesTick = _timing.CurTick;
        _candidates.Clear();

        // Shots are never rewound further than this.
        var oldest = _timing.CurTime - TimeSpan.FromTicks(Math.Min(_maxRewind.Ticks, LagCompensationSystem.BufferTime.Ticks));

        var query = EntityQueryEnumerator<LagCompensationComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var comp, out var xform))
        {
            if (xform.MapID == MapId.Nullspace)
                continue;

            // Movement since the candidates were gathered, as rays can come later in the tick.
            var reach = GetFixtureReach(uid) + GetHistoryReach(comp, xform, oldest);
            if (_physicsQuery.TryComp(uid, out var physics))
                reach += physics.LinearVelocity.Length() * (float) _timing.TickPeriod.TotalSeconds;

            _candidates.Add(((uid, comp), xform.MapID, _transform.GetWorldPosition(xform), reach));
        }
    }

    /// <summary>
    /// How far an entity's hard fixtures reach from its position, at any rotation.
    /// </summary>
    private float GetFixtureReach(EntityUid uid)
    {
        if (!_fixturesQuery.TryComp(uid, out var fixtures))
            return 0f;

        var reach = 0f;

        foreach (var fixture in fixtures.Fixtures.Values)
        {
            if (!fixture.Hard)
                continue;

            if (fixture.Shape is PhysShapeCircle circle)
            {
                var offset = circle.Position;
                reach = MathF.Max(reach, offset.Length() + circle.Radius);
                continue;
            }

            for (var i = 0; i < fixture.Shape.ChildCount; i++)
            {
                var bounds = fixture.Shape.ComputeAABB(PhysicsTransform.Empty, i);
                var x = MathF.Max(MathF.Abs(bounds.Left), MathF.Abs(bounds.Right));
                var y = MathF.Max(MathF.Abs(bounds.Bottom), MathF.Abs(bounds.Top));
                reach = MathF.Max(reach, MathF.Sqrt(x * x + y * y));
            }
        }

        return reach;
    }

    /// <summary>
    /// How far from its current position an entity was at any time since <paramref name="oldest"/>.
    /// </summary>
    private static float GetHistoryReach(LagCompensationComponent comp, TransformComponent xform, TimeSpan oldest)
    {
        var reachSquared = 0f;
        (TimeSpan Time, EntityCoordinates Coordinates, Angle Angle)? older = null;

        foreach (var sample in comp.Positions)
        {
            // Rewinding to just after this sample interpolates from it, so only the newest one before the window counts.
            if (sample.Time < oldest)
            {
                older = sample;
                continue;
            }

            if (!TryGetReachSquared(sample.Coordinates, xform, ref reachSquared))
                return float.PositiveInfinity;
        }

        if (older is { } first && !TryGetReachSquared(first.Coordinates, xform, ref reachSquared))
            return float.PositiveInfinity;

        return MathF.Sqrt(reachSquared);
    }

    /// <returns>False if the sample is relative to a different parent, so it can't be compared cheaply.</returns>
    private static bool TryGetReachSquared(EntityCoordinates sample, TransformComponent xform, ref float reachSquared)
    {
        if (sample.EntityId != xform.ParentUid)
            return false;

        reachSquared = MathF.Max(reachSquared, (sample.Position - xform.LocalPosition).LengthSquared());
        return true;
    }

    private static float DistanceToSegmentSquared(Vector2 point, Vector2 start, Vector2 end)
    {
        var segment = end - start;
        var lengthSquared = segment.LengthSquared();
        if (lengthSquared <= 0f)
            return (point - start).LengthSquared();

        var t = Math.Clamp(Vector2.Dot(point - start, segment) / lengthSquared, 0f, 1f);
        return (start + segment * t - point).LengthSquared();
    }
}
