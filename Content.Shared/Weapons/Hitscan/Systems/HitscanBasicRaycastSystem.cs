using System.Numerics;
using Content.Shared.Administration.Logs;
using Content.Shared.Damage.Components;
using Content.Shared.Database;
using Content.Shared.Weapons.Hitscan.Components;
using Content.Shared.Weapons.Hitscan.Events;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Network;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Player;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Shared.Weapons.Hitscan.Systems;

public sealed partial class HitscanBasicRaycastSystem : EntitySystem
{
    [Dependency] private SharedPhysicsSystem _physics = default!;
    [Dependency] private SharedContainerSystem _container = default!;
    [Dependency] private ISharedAdminLogManager _log = default!;
    [Dependency] private SharedGunLagCompensationSystem _lagCompensation = default!;
    [Dependency] private INetManager _net = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    [Dependency] private EntityQuery<HitscanBasicVisualsComponent> _visualsQuery = default!;
    [Dependency] private EntityQuery<FixturesComponent> _fixturesQuery = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<HitscanBasicRaycastComponent, HitscanTraceEvent>(OnHitscanFired);
    }

    private void OnHitscanFired(Entity<HitscanBasicRaycastComponent> ent, ref HitscanTraceEvent args)
    {
        var shooter = args.Shooter ?? args.Gun;
        var result = FindHit(ent, args.FromCoordinates, args.ShotDirection, shooter, args.Target, args.LagCompensationTick);
        var distanceTried = result?.Distance ?? ent.Comp.MaxDistance;

        // Do visuals without an event. They should always happen and putting it on the attempt event is weird!
        // If more stuff gets added here, it should probably be turned into an event.
        FireEffects(args.FromCoordinates, distanceTried, args.ShotDirection.ToAngle(), ent.Owner, args.Shooter, args.ShotId);

        // Admin logging
        if (result?.HitEntity != null)
        {
            _log.Add(LogType.HitScanHit,
                $"{ToPrettyString(shooter):user} hit {ToPrettyString(result.Value.HitEntity):target}"
                + $" using {ToPrettyString(args.Gun):entity}.");
        }

        var data = new HitscanRaycastFiredData
        {
            ShotDirection = args.ShotDirection,
            Gun = args.Gun,
            Hitscan = ent.Owner,
            Shooter = args.Shooter,
            HitEntity = result?.HitEntity,
        };

        var attemptEvent = new AttemptHitscanRaycastFiredEvent { Data = data };
        RaiseLocalEvent(ent, ref attemptEvent);

        if (attemptEvent.Cancelled)
            return;

        var hitEvent = new HitscanRaycastFiredEvent { Data = data };
        RaiseLocalEvent(ent, ref hitEvent);

        if (data.HitEntity != null)
        {
            var strikeEvent = new HitscanRaycastStrikeEvent { Data = data };
            RaiseLocalEvent(data.HitEntity.Value, ref strikeEvent);
        }
    }

    /// <summary>
    /// Draws a hitscan shot on the client without waiting for the server.
    /// Only the effects are predicted, the server still decides what was hit.
    /// </summary>
    /// <returns>Whether anything was drawn.</returns>
    public bool PredictEffects(Entity<HitscanBasicRaycastComponent?> ent,
        EntityCoordinates fromCoordinates,
        Vector2 direction,
        EntityUid shooter,
        EntityUid? target)
    {
        if (!Resolve(ent, ref ent.Comp, false))
            return false;

        var result = FindHit((ent, ent.Comp), fromCoordinates, direction, shooter, target, null);
        var distanceTried = result?.Distance ?? ent.Comp.MaxDistance;
        return FireEffects(fromCoordinates, distanceTried, direction.ToAngle(), ent.Owner, null, null);
    }

    /// <summary>
    /// Casts the hitscan's ray and picks what it hits.
    /// </summary>
    private RayCastResults? FindHit(Entity<HitscanBasicRaycastComponent> ent,
        EntityCoordinates fromCoordinates,
        Vector2 direction,
        EntityUid shooter,
        EntityUid? target,
        GameTick? lagCompensationTick)
    {
        var mapCords = _transform.ToMapCoordinates(fromCoordinates);
        var ray = new CollisionRay(mapCords.Position, direction, (int) ent.Comp.CollisionMask);
        var rayCastResults = new List<RayCastResults>(_physics.IntersectRay(mapCords.MapId, ray, ent.Comp.MaxDistance, shooter, false));
        RefineHits(ray, ent.Comp.MaxDistance, rayCastResults);

        if (lagCompensationTick is { } tick)
            _lagCompensation.RewindRay(shooter, mapCords.MapId, ray, ent.Comp.MaxDistance, tick, rayCastResults);

        // If you are in a container, use the raycast result
        // Otherwise:
        //  1.) Hit the first entity that you targeted.
        //  2.) Hit the first entity that doesn't require you to aim at it specifically to be hit.
        return _container.IsEntityOrParentInContainer(shooter)
            ? rayCastResults.FirstOrNull()
            : rayCastResults.FirstOrNull(hit => hit.HitEntity == target
                                                || CompOrNull<RequireProjectileTargetComponent>(hit.HitEntity)?.Active != true);
    }

    /// <summary>
    /// The broadphase raycast only tests AABBs, so re-test each hit against the actual fixture shapes.
    /// Drops hits that only clipped a corner of the AABB and moves the rest to where the ray enters the shape.
    /// </summary>
    private void RefineHits(CollisionRay ray, float maxDistance, List<RayCastResults> results)
    {
        var direction = ray.Direction.Normalized();

        for (var i = results.Count - 1; i >= 0; i--)
        {
            var hit = results[i].HitEntity;

            if (!_fixturesQuery.TryComp(hit, out var fixtures))
                continue;

            var transform = _physics.GetPhysicsTransform(hit);

            if (!HitscanRayMath.TryIntersectFixtures(ray.Position, direction, maxDistance, ray.CollisionMask, fixtures, transform, out var distance))
            {
                results.RemoveAt(i);
                continue;
            }

            results[i] = new RayCastResults(distance, ray.Position + direction * distance, hit);
        }

        results.Sort((a, b) => a.Distance.CompareTo(b.Distance));
    }

    /// <summary>
    /// Create visual effects for the fired hitscan weapon.
    /// </summary>
    /// <param name="fromCoordinates">Location to start the effect.</param>
    /// <param name="distance">Distance of the hitscan shot.</param>
    /// <param name="shotAngle">Angle of the shot.</param>
    /// <param name="hitscanUid">The hitscan entity itself.</param>
    /// <param name="shooter">Who fired the hitscan.</param>
    /// <param name="shotId">The shooter's request for this shot, see <see cref="HitscanTraceEvent.ShotId"/>.</param>
    /// <returns>Whether there was anything to draw.</returns>
    private bool FireEffects(EntityCoordinates fromCoordinates,
        float distance,
        Angle shotAngle,
        EntityUid hitscanUid,
        EntityUid? shooter,
        int? shotId)
    {
        if (distance == 0 || !_visualsQuery.TryComp(hitscanUid, out var vizComp))
            return false;

        var sprites = new List<(NetCoordinates coordinates, Angle angle, SpriteSpecifier sprite, float scale)>();
        var fromXform = Transform(fromCoordinates.EntityId);

        // We'll get the effects relative to the grid / map of the firer
        // Look you could probably optimise this a bit with redundant transforms at this point.

        var gridUid = fromXform.GridUid;
        if (gridUid != fromCoordinates.EntityId && TryComp(gridUid, out TransformComponent? gridXform))
        {
            var (_, gridRot, gridInvMatrix) = _transform.GetWorldPositionRotationInvMatrix(gridXform);
            var map = _transform.ToMapCoordinates(fromCoordinates);
            fromCoordinates = new EntityCoordinates(gridUid.Value, Vector2.Transform(map.Position, gridInvMatrix));
            shotAngle -= gridRot;
        }
        else
        {
            shotAngle -= _transform.GetWorldRotation(fromXform);
        }

        if (distance >= 1f)
        {
            if (vizComp.MuzzleFlash != null)
            {
                var coords = fromCoordinates.Offset(shotAngle.ToVec().Normalized() / 2);
                var netCoords = GetNetCoordinates(coords);

                sprites.Add((netCoords, shotAngle, vizComp.MuzzleFlash, 1f));
            }

            if (vizComp.TravelFlash != null)
            {
                var coords = fromCoordinates.Offset(shotAngle.ToVec() * (distance + 0.5f) / 2);
                var netCoords = GetNetCoordinates(coords);

                sprites.Add((netCoords, shotAngle, vizComp.TravelFlash, distance - 1.5f));
            }
        }

        if (vizComp.ImpactFlash != null)
        {
            var coords = fromCoordinates.Offset(shotAngle.ToVec() * distance);
            var netCoords = GetNetCoordinates(coords);

            sprites.Add((netCoords, shotAngle.FlipPositive(), vizComp.ImpactFlash, 1f));
        }

        if (sprites.Count == 0)
            return false;

        var ev = new SharedGunSystem.HitscanEvent { Sprites = sprites };

        // Only reached on the client when predicting, so just draw it locally.
        if (_net.IsClient)
        {
            RaiseLocalEvent(ev);
            return true;
        }

        var filter = Filter.Pvs(fromCoordinates, entityMan: EntityManager);

        // The shooter's client may have drawn this shot already, so tell it which shot this is.
        if (shotId != null && TryComp<ActorComponent>(shooter, out var actor))
        {
            filter.RemovePlayer(actor.PlayerSession);
            RaiseNetworkEvent(new SharedGunSystem.HitscanEvent { Sprites = sprites, ShotId = shotId }, actor.PlayerSession);
        }

        RaiseNetworkEvent(ev, filter);
        return true;
    }
}
