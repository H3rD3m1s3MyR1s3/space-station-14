using System.Numerics;
using Content.Client.Projectiles;
using Content.Shared.Damage.Components;
using Content.Shared.Projectiles;
using Content.Shared.Weapons.Hitscan;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Client.Animations;
using Robust.Client.GameObjects;
using Robust.Client.Timing;
using Robust.Shared.Animations;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Prototypes;
using Robust.Shared.Spawners;

namespace Content.Client.Weapons.Ranged.Systems;

/// <summary>
/// Draws the local player's projectiles as soon as they fire, instead of a round trip later when the server's
/// projectiles arrive. The server's copies are hidden from the shooter while the predicted ones stand in for them.
/// </summary>
/// <remarks>
/// The predicted projectiles are only visuals. They fly in a straight line and stop at the first thing the client
/// sees in their way, where they play their impact effect. Hits and damage still come from the server.
/// </remarks>
public sealed partial class ProjectilePredictionSystem : EntitySystem
{
    [Dependency] private IClientGameTiming _timing = default!;
    [Dependency] private AnimationPlayerSystem _animations = default!;
    [Dependency] private MetaDataSystem _metaData = default!;
    [Dependency] private ProjectileSystem _projectiles = default!;
    [Dependency] private SharedMapSystem _maps = default!;
    [Dependency] private SharedPhysicsSystem _physics = default!;
    [Dependency] private SharedPointLightSystem _lights = default!;
    [Dependency] private SpriteSystem _sprite = default!;
    [Dependency] private TransformSystem _transform = default!;

    [Dependency] private EntityQuery<FixturesComponent> _fixturesQuery = default!;

    private static readonly EntProtoId VisualPrototype = "ClientsideClone";
    private const string AnimationKey = "predicted-projectile";

    /// <summary>
    /// Upper bound on how far a predicted projectile is drawn, well past the edge of the screen.
    /// </summary>
    private const float MaxRange = 32f;

    /// <summary>
    /// Extra time the server's copy stays hidden after the predicted one would have reached the same point.
    /// </summary>
    private static readonly TimeSpan HideSlack = TimeSpan.FromSeconds(0.15);

    /// <summary>
    /// How far the server's impact can be from the one this client drew and still be the same impact.
    /// </summary>
    private const float ImpactMatchRange = 0.5f;

    /// <summary>
    /// Shots this client drew, mapped to when the server's copy may be shown, and where and on what this client drew
    /// an impact for it that the server's impact effect would duplicate.
    /// </summary>
    private readonly Dictionary<(int ShotId, int Index), (TimeSpan ShowAt, EntityCoordinates? Impact, EntityUid? Hit)> _drawn = new();

    /// <summary>
    /// Impact effects to play once a predicted projectile reaches what it hit.
    /// </summary>
    private readonly List<(float At, EntityCoordinates Coordinates, EntProtoId Prototype)> _pendingImpacts = new();

    /// <summary>
    /// Seconds of frame time passed, the same clock the predicted projectiles' animations run on.
    /// </summary>
    private float _frameClock;

    /// <summary>
    /// Server projectiles hidden from the local player, mapped to when they should be shown again and whether their
    /// light was on before they were hidden.
    /// </summary>
    private readonly Dictionary<EntityUid, (TimeSpan ShowAt, bool LightWasOn)> _hidden = new();

    /// <summary>
    /// One entity of each projectile prototype fired, kept paused in nullspace to read and copy from.
    /// Spawning a fresh one for every projectile costs more than everything else about predicting it.
    /// </summary>
    private readonly Dictionary<EntProtoId, EntityUid> _templates = new();

    private readonly List<EntityUid> _toShow = new();
    private readonly List<(int, int)> _toForget = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PredictedProjectileComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<PredictedProjectileComponent, AfterAutoHandleStateEvent>(OnHandleState);
        SubscribeLocalEvent<PredictedProjectileComponent, ComponentShutdown>(OnShutdown);
    }

    public override void Shutdown()
    {
        base.Shutdown();

        _drawn.Clear();
        _hidden.Clear();
        _pendingImpacts.Clear();
        _templates.Clear();
    }

    /// <summary>
    /// Gets an entity of a projectile prototype to pass to <see cref="PredictProjectile"/>. Don't change it.
    /// </summary>
    public EntityUid GetTemplate(EntProtoId prototype)
    {
        if (_templates.TryGetValue(prototype, out var template) && !TerminatingOrDeleted(template))
            return template;

        template = Spawn(prototype, MapCoordinates.Nullspace);

        // Otherwise it would despawn like a projectile that was fired.
        _metaData.SetEntityPaused(template, true);

        _templates[prototype] = template;
        return template;
    }

    /// <summary>
    /// Draws a projectile the local player just fired.
    /// </summary>
    /// <param name="projectile">An entity of the projectile's prototype to copy the look of, see <see cref="GetTemplate"/>.</param>
    /// <param name="gun">The gun that fired it.</param>
    /// <param name="user">The local player's entity.</param>
    /// <param name="from">Where the server spawns the projectile.</param>
    /// <param name="direction">Map direction the projectile flies in.</param>
    /// <param name="speed">How fast the projectile flies.</param>
    /// <param name="shotId">The shot's <see cref="PredictedProjectileComponent.ShotId"/>.</param>
    /// <param name="index">Which projectile of the shot it is, see <see cref="PredictedProjectileComponent.Index"/>.</param>
    /// <param name="target">Entity the shooter aimed at, which projectiles hit even when they'd otherwise fly over it.</param>
    public void PredictProjectile(Entity<ProjectileComponent?, SpriteComponent?> projectile,
        EntityUid gun,
        EntityUid user,
        EntityCoordinates from,
        Angle direction,
        float speed,
        int shotId,
        int index,
        EntityUid? target)
    {
        if (!_timing.IsFirstTimePredicted
            || speed <= 0f
            || !Resolve(projectile, ref projectile.Comp1, ref projectile.Comp2, false))
        {
            return;
        }

        var fromMap = _transform.ToMapCoordinates(from);
        if (fromMap.MapId == MapId.Nullspace)
            return;

        var lifetime = CompOrNull<TimedDespawnComponent>(projectile)?.Lifetime ?? float.MaxValue;
        var range = MathF.Min(speed * lifetime, MaxRange);
        var hit = TryGetFlightDistance((projectile.Owner, projectile.Comp1),
            user,
            gun,
            target,
            fromMap,
            direction.ToVec(),
            range,
            out var distance,
            out var hitEntity);
        var duration = distance / speed;

        // Same parenting as the server, so the projectile moves with the grid it was fired on.
        var coordinates = _maps.TryFindGridAt(fromMap, out var gridUid, out _)
            ? _transform.WithEntityId(from, gridUid)
            : new EntityCoordinates(_maps.GetMapOrInvalid(fromMap.MapId), fromMap.Position);

        var localDirection = (-_transform.GetWorldRotation(coordinates.EntityId)).RotateVec(direction.ToVec());
        var end = coordinates.Offset(localDirection * distance);

        EntityCoordinates? impact = null;
        if (hit && projectile.Comp1.ImpactEffect is { } impactEffect)
        {
            impact = end;
            _pendingImpacts.Add((_frameClock + duration, end, impactEffect));
        }

        // The server's copy is drawn behind the predicted one by however far ahead of the last server state we are.
        var behind = _timing.TickPeriod * Math.Max(0, (long) _timing.CurTick.Value - _timing.LastRealTick.Value);
        _drawn[(shotId, index)] = (_timing.RealTime + TimeSpan.FromSeconds(duration) + behind + HideSlack, impact, hitEntity);

        // Fired point blank into something, so there's no flight to draw.
        if (duration < _timing.TickPeriod.TotalSeconds)
            return;

        var visual = SpawnAttachedTo(VisualPrototype, coordinates);
        var sprite = Comp<SpriteComponent>(visual);
        _sprite.CopySprite((projectile.Owner, projectile.Comp2), (visual, sprite));
        _sprite.SetVisible((visual, sprite), true);

        if (_lights.TryGetLight(projectile, out var projectileLight) && projectileLight.Enabled)
        {
            var light = _lights.EnsureLight(visual);
            _lights.SetColor(visual, projectileLight.Color, light);
            _lights.SetRadius(visual, projectileLight.Radius, light);
            _lights.SetEnergy(visual, projectileLight.Energy, light);
        }

        _transform.SetLocalPositionNoLerp(visual, coordinates.Position);
        _transform.SetWorldRotationNoLerp(visual, direction.ToVec().ToWorldAngle() + projectile.Comp1.Angle);

        _animations.Play(visual,
            new Animation
            {
                Length = TimeSpan.FromSeconds(duration),
                AnimationTracks =
                {
                    new AnimationTrackComponentProperty
                    {
                        ComponentType = typeof(TransformComponent),
                        Property = nameof(TransformComponent.LocalPosition),
                        InterpolationMode = AnimationInterpolationMode.Linear,
                        KeyFrames =
                        {
                            new AnimationTrackProperty.KeyFrame(coordinates.Position, 0f),
                            new AnimationTrackProperty.KeyFrame(end.Position, (float) duration),
                        },
                    },
                },
            },
            AnimationKey);

        EnsureComp<TimedDespawnComponent>(visual).Lifetime = duration;
    }

    /// <summary>
    /// How far a projectile flies before hitting something, as far as this client can tell.
    /// </summary>
    /// <param name="hitEntity">What it hits.</param>
    /// <returns>Whether it hits something before flying its full <paramref name="range"/>.</returns>
    private bool TryGetFlightDistance(Entity<ProjectileComponent> projectile,
        EntityUid user,
        EntityUid gun,
        EntityUid? target,
        MapCoordinates from,
        Vector2 direction,
        float range,
        out float distance,
        out EntityUid? hitEntity)
    {
        distance = range;
        hitEntity = null;

        if (!_fixturesQuery.TryComp(projectile, out var projectileFixtures)
            || !projectileFixtures.Fixtures.TryGetValue(SharedProjectileSystem.ProjectileFixture, out var fixture))
        {
            return false;
        }

        var ray = new CollisionRay(from.Position, direction, fixture.CollisionMask);

        // Physics collides with the whole fixture, so widen the ray by how far it sticks out to either side.
        // Rotated the way the server rotates projectiles when shooting them.
        var rotation = direction.ToWorldAngle() + projectile.Comp.Angle - direction.ToAngle();
        var radius = HitscanRayMath.GetHalfWidth(fixture.Shape, rotation);

        foreach (var result in _physics.IntersectRay(from.MapId, ray, range, user, returnOnFirstHit: false))
        {
            if (result.HitEntity == gun
                || result.HitEntity != target && CompOrNull<RequireProjectileTargetComponent>(result.HitEntity)?.Active == true
                || !_fixturesQuery.TryComp(result.HitEntity, out var fixtures))
            {
                continue;
            }

            // Projectiles collide with the real shapes, not the AABBs the broadphase raycast tests.
            if (HitscanRayMath.TryIntersectFixtures(ray.Position,
                    direction,
                    distance,
                    ray.CollisionMask,
                    fixtures,
                    _physics.GetPhysicsTransform(result.HitEntity),
                    out var hitDistance,
                    radius))
            {
                distance = hitDistance;
                hitEntity = result.HitEntity;
            }
        }

        return hitEntity != null;
    }

    /// <summary>
    /// Checks whether this client already drew the impact of a shot on the same thing or at about the same place, so
    /// the server's impact effect would be a duplicate. Only the first impact of each shot is predicted, so this is
    /// only checked once.
    /// </summary>
    /// <param name="hit">What the server's projectile hit.</param>
    public bool ConsumePredictedImpact(PredictedShot shot, EntityCoordinates coordinates, EntityUid? hit)
    {
        var key = (shot.ShotId, shot.Index);
        if (!_drawn.TryGetValue(key, out var drawn) || drawn.Impact is not { } impact)
            return false;

        _drawn[key] = drawn with { Impact = null };
        return (hit != null && hit == drawn.Hit) || _transform.InRange(impact, coordinates, ImpactMatchRange);
    }

    private void OnStartup(Entity<PredictedProjectileComponent> ent, ref ComponentStartup args)
    {
        TryHide(ent);
    }

    private void OnHandleState(Entity<PredictedProjectileComponent> ent, ref AfterAutoHandleStateEvent args)
    {
        TryHide(ent);
    }

    private void OnShutdown(Entity<PredictedProjectileComponent> ent, ref ComponentShutdown args)
    {
        // Removed by the server because the projectile went somewhere the predicted one didn't.
        if (_hidden.Remove(ent, out var hidden))
            Show(ent, hidden.LightWasOn);
    }

    private void TryHide(Entity<PredictedProjectileComponent> ent)
    {
        if (_hidden.ContainsKey(ent)
            || !_drawn.TryGetValue((ent.Comp.ShotId, ent.Comp.Index), out var drawn))
        {
            return;
        }

        _sprite.SetVisible(ent.Owner, false);

        var lightWasOn = _lights.TryGetLight(ent, out var light) && light.Enabled;
        if (lightWasOn)
            _lights.SetEnabled(ent, false, light);

        _hidden[ent] = (drawn.ShowAt, lightWasOn);
    }

    private void Show(EntityUid uid, bool lightWasOn)
    {
        if (TerminatingOrDeleted(uid))
            return;

        _sprite.SetVisible(uid, true);

        if (lightWasOn)
            _lights.SetEnabled(uid, true);
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);

        var now = _timing.RealTime;
        _frameClock += frameTime;

        for (var i = _pendingImpacts.Count - 1; i >= 0; i--)
        {
            var (at, coordinates, prototype) = _pendingImpacts[i];
            if (_frameClock < at)
                continue;

            _pendingImpacts.RemoveAt(i);
            _projectiles.SpawnImpactEffect(prototype, coordinates);
        }

        // Anything still around after the predicted projectile would have stopped went somewhere else, so show it.
        foreach (var (uid, hidden) in _hidden)
        {
            if (TerminatingOrDeleted(uid) || now >= hidden.ShowAt)
                _toShow.Add(uid);
        }

        foreach (var uid in _toShow)
        {
            if (_hidden.Remove(uid, out var hidden))
                Show(uid, hidden.LightWasOn);
        }

        foreach (var (key, drawn) in _drawn)
        {
            if (now >= drawn.ShowAt)
                _toForget.Add(key);
        }

        foreach (var key in _toForget)
        {
            _drawn.Remove(key);
        }

        _toShow.Clear();
        _toForget.Clear();
    }
}
