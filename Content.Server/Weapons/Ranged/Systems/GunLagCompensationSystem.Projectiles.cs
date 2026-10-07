using System.Numerics;
using Content.Server.Movement.Systems;
using Content.Server.Physics.Components;
using Content.Server.Weapons.Ranged.Components;
using Content.Shared.CCVar;
using Content.Shared.Projectiles;
using Content.Shared.Weapons.Hitscan;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Dynamics;
using Robust.Shared.Physics.Events;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server.Weapons.Ranged.Systems;

public sealed partial class GunLagCompensationSystem
{
    [Dependency] private SharedPhysicsSystem _physics = default!;

    /// <summary>
    /// Projectiles slower than this have stopped flying.
    /// </summary>
    private const float MinFlightSpeed = 0.1f;

    /// <summary>
    /// How long a projectile is lag compensated for. Bullets are well past the edge of the shooter's screen by then,
    /// and slow projectiles are left to physics rather than swept for their whole lifetime.
    /// </summary>
    private static readonly TimeSpan MaxCompensatedFlight = TimeSpan.FromSeconds(1);

    private bool _projectilesEnabled;

    /// <summary>
    /// Set while checking whether a projectile may collide with something, so our own filter stays out of the way.
    /// </summary>
    private bool _checkingCollision;

    private readonly List<RayCastResults> _sweepResults = new();

    private void InitializeProjectiles()
    {
        SubscribeLocalEvent<LagCompensatedProjectileComponent, PreventCollideEvent>(OnProjectilePreventCollide);

        Subs.CVar(_cfg, CCVars.CombatGunLagCompensationProjectiles, value => _projectilesEnabled = value, true);

        // Projectiles have to be moved onto what they hit before physics looks for collisions.
        UpdatesBefore.Add(typeof(SharedPhysicsSystem));
    }

    /// <summary>
    /// Makes a projectile hit lag compensated entities where the shooter saw them when firing.
    /// </summary>
    /// <param name="projectile">The projectile, already flying.</param>
    /// <param name="shooter">The player that fired it.</param>
    /// <param name="viewTick">The last server tick the shooter's client had applied when firing, if it said.</param>
    public void CompensateProjectile(EntityUid projectile, EntityUid shooter, GameTick? viewTick)
    {
        // Homing projectiles steer towards where their target is now, so they could never hit where the shooter saw it.
        // Without a projectile fixture there is nothing to sweep, while physics would still ignore lag compensated entities.
        if (!_enabled
            || !_projectilesEnabled
            || viewTick is not { } tick
            || HasComp<ChasingWalkComponent>(projectile)
            || !_fixturesQuery.TryComp(projectile, out var fixtures)
            || !fixtures.Fixtures.ContainsKey(SharedProjectileSystem.ProjectileFixture)
            || !TryComp<ActorComponent>(shooter, out var actor))
        {
            return;
        }

        var rewind = GetRewind(tick, actor.PlayerSession);
        if (rewind <= TimeSpan.Zero)
            return;

        var comp = EnsureComp<LagCompensatedProjectileComponent>(projectile);
        comp.Rewind = rewind;
        comp.Shooter = shooter;
        comp.SweptTo = _transform.GetMapCoordinates(projectile);
        comp.EndTime = _timing.CurTime + MaxCompensatedFlight;
    }

    /// <summary>
    /// Stops the projectile from hitting lag compensated entities where they are now. It hits them where the shooter
    /// saw them instead, see <see cref="Sweep"/>.
    /// </summary>
    private void OnProjectilePreventCollide(Entity<LagCompensatedProjectileComponent> ent, ref PreventCollideEvent args)
    {
        if (!_checkingCollision
            && args.OtherEntity != ent.Comp.Target
            && IsRewound(ent.Comp, args.OtherEntity)
            && args.OurFixture == _fixturesQuery.Comp(ent).Fixtures.GetValueOrDefault(SharedProjectileSystem.ProjectileFixture))
        {
            args.Cancelled = true;
        }
    }

    /// <summary>
    /// Whether the projectile hits an entity where the shooter saw it. The shooter sees themselves where they are now.
    /// </summary>
    private bool IsRewound(LagCompensatedProjectileComponent comp, EntityUid other)
    {
        return other != comp.Shooter && _lagQuery.HasComp(other);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<LagCompensatedProjectileComponent, ProjectileComponent, PhysicsComponent, FixturesComponent>();
        while (query.MoveNext(out var uid, out var comp, out var projectile, out var body, out var fixtures))
        {
            var velocity = _physics.GetMapLinearVelocity(uid, body);

            // Its flight is over, like an arrow stuck in a wall or lying on the floor. Anything that sends it flying
            // again, like being thrown, has to collide with mobs normally.
            if (projectile.ProjectileSpent || velocity.LengthSquared() < MinFlightSpeed * MinFlightSpeed)
            {
                RemCompDeferred<LagCompensatedProjectileComponent>(uid);
                continue;
            }

            var position = _transform.GetMapCoordinates(uid);

            // Moved to another map, so there's no path between the two positions to sweep.
            if (position.MapId != comp.SweptTo.MapId)
            {
                comp.SweptTo = position;
                comp.Offset = Vector2.Zero;
            }

            // Sweep from where the last sweep ended, so the tick the projectile was fired on is covered too.
            var start = comp.SweptTo.Position;
            var end = position.Position - comp.Offset + velocity * frameTime;
            comp.SweptTo = new MapCoordinates(end, position.MapId);

            Sweep((uid, comp, body, fixtures), position, start, end);

            // Physics keeps ignoring lag compensated entities until the component is actually removed, so this tick
            // still had to be swept.
            if (_timing.CurTime >= comp.EndTime)
                RemCompDeferred<LagCompensatedProjectileComponent>(uid);
        }
    }

    /// <summary>
    /// Checks whether the projectile hits a lag compensated entity between two points on the path the shooter sees it
    /// fly along, testing those entities at their past positions where they can be rewound and everything else at its
    /// present one.
    /// If it does, moves the projectile onto that entity so physics makes it collide this tick.
    /// </summary>
    private void Sweep(Entity<LagCompensatedProjectileComponent, PhysicsComponent, FixturesComponent> ent,
        MapCoordinates position,
        Vector2 start,
        Vector2 end)
    {
        var delta = end - start;
        var length = delta.Length();

        if (length <= 0f || !ent.Comp3.Fixtures.TryGetValue(SharedProjectileSystem.ProjectileFixture, out var fixture))
            return;

        var direction = delta / length;
        var ray = new CollisionRay(start, direction, fixture.CollisionMask);
        var time = _timing.CurTime - ent.Comp1.Rewind;

        // Physics collides with the whole fixture, so widen the ray by how far it sticks out to either side.
        var radius = HitscanRayMath.GetHalfWidth(fixture.Shape, _transform.GetWorldRotation(ent) - direction.ToAngle());

        // Look for hits on lag compensated entities first. Almost every tick there are none, and physics handles
        // everything else, so the raycast can usually be skipped.
        _sweepResults.Clear();
        RewindRay(ent.Comp1.Shooter, position.MapId, ray, length, time, _sweepResults, radius, out var unrewound);

        if (_sweepResults.Count == 0 && !unrewound)
            return;

        _sweepResults.Clear();
        foreach (var result in _physics.IntersectRay(position.MapId, ray, length, ent, returnOnFirstHit: false))
        {
            var other = result.HitEntity;

            // The raycast only tests AABBs, so check the actual shapes, which is what physics collides with.
            if (!_fixturesQuery.TryComp(other, out var otherFixtures)
                || !HitscanRayMath.TryIntersectFixtures(ray.Position,
                    ray.Direction,
                    length,
                    ray.CollisionMask,
                    otherFixtures,
                    _physics.GetPhysicsTransform(other),
                    out var distance,
                    radius))
            {
                continue;
            }

            _sweepResults.Add(new RayCastResults(distance, ray.Position + ray.Direction * distance, other));
        }

        _sweepResults.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        RewindRay(ent.Comp1.Shooter, position.MapId, ray, length, time, _sweepResults, radius, out _);

        foreach (var result in _sweepResults)
        {
            var other = result.HitEntity;

            if (ent.Comp1.Hit.Contains(other) || !CanCollide(ent, fixture, other))
                continue;

            // Something that isn't rewound is in the way first. Physics handles that one as usual.
            if (!IsRewound(ent.Comp1, other))
                return;

            // Teleported since, so the projectile can't be moved onto it without jumping across the station.
            var target = _transform.GetMapCoordinates(other);
            if (target.MapId != position.MapId || !MovedWithoutSnapping(other, time))
                continue;

            ent.Comp1.Hit.Add(other);
            ent.Comp1.Target = other;
            ent.Comp1.Offset += target.Position - position.Position;

            // Carry on from the hit next tick, in case the projectile passes through to hit something behind it.
            ent.Comp1.SweptTo = new MapCoordinates(result.HitPos, position.MapId);

            // Hit it through a normal collision, so everything that reacts to the projectile colliding still does.
            _transform.SetWorldPosition(ent.Owner, target.Position);
            return;
        }
    }

    /// <summary>
    /// Whether an entity got from where it was at <paramref name="time"/> to where it is now without any move big
    /// enough to snap rather than be interpolated, like a teleport.
    /// </summary>
    private bool MovedWithoutSnapping(EntityUid uid, TimeSpan time)
    {
        if (!_lagQuery.TryComp(uid, out var lag))
            return true;

        MapCoordinates? previous = null;

        foreach (var (sampleTime, coordinates, _) in lag.Positions)
        {
            var current = Exists(coordinates.EntityId)
                ? _transform.ToMapCoordinates(coordinates)
                : MapCoordinates.Nullspace;

            if (sampleTime > time
                && previous is { } from
                && (current.MapId != from.MapId
                    || (current.Position - from.Position).Length() >= LagCompensationSystem.MaxInterpolationDistance))
            {
                return false;
            }

            previous = current;
        }

        return true;
    }

    /// <summary>
    /// Asks everything that can stop a collision, like mobs lying down that bullets fly over, whether the projectile
    /// may hit an entity.
    /// </summary>
    private bool CanCollide(Entity<LagCompensatedProjectileComponent, PhysicsComponent, FixturesComponent> ent,
        Fixture fixture,
        EntityUid other)
    {
        if (!_physicsQuery.TryComp(other, out var otherBody) || !_fixturesQuery.TryComp(other, out var otherFixtures))
            return false;

        foreach (var otherFixture in otherFixtures.Fixtures.Values)
        {
            if (!otherFixture.Hard || (otherFixture.CollisionLayer & fixture.CollisionMask) == 0)
                continue;

            var ev = new PreventCollideEvent(ent, other, ent.Comp2, otherBody, fixture, otherFixture);
            _checkingCollision = true;

            try
            {
                RaiseLocalEvent(ent, ref ev);

                if (!ev.Cancelled)
                {
                    var otherEv = new PreventCollideEvent(other, ent, otherBody, ent.Comp2, otherFixture, fixture);
                    RaiseLocalEvent(other, ref otherEv);
                    ev.Cancelled = otherEv.Cancelled;
                }
            }
            finally
            {
                // Left set, our filter would stop working for every projectile.
                _checkingCollision = false;
            }

            return !ev.Cancelled;
        }

        return false;
    }
}
