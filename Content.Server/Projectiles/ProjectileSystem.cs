using System.Linq;
using Content.Server.Administration.Logs;
using Content.Server.Destructible;
using Content.Server.Effects;
using Content.Server.Weapons.Ranged.Systems;
using Content.Shared.Camera;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.Database;
using Content.Shared.FixedPoint;
using Content.Shared.Projectiles;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Shared.Physics.Events;
using Robust.Shared.Player;

namespace Content.Server.Projectiles;

public sealed partial class ProjectileSystem : SharedProjectileSystem
{
    [Dependency] private IAdminLogManager _adminLogger = default!;
    [Dependency] private ColorFlashEffectSystem _color = default!;
    [Dependency] private DamageableSystem _damageableSystem = default!;
    [Dependency] private DestructibleSystem _destructibleSystem = default!;
    [Dependency] private GunSystem _guns = default!;
    [Dependency] private SharedCameraRecoilSystem _sharedCameraRecoil = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ProjectileComponent, StartCollideEvent>(OnStartCollide);
    }

    private void OnStartCollide(EntityUid uid, ProjectileComponent component, ref StartCollideEvent args)
    {
        // This is so entities that shouldn't get a collision are ignored.
        if (args.OurFixtureId != ProjectileFixture || !args.OtherFixture.Hard
            || component.ProjectileSpent || component is { Weapon: null, OnlyCollideWhenShot: true })
            return;

        var target = args.OtherEntity;
        // it's here so this check is only done once before possible hit
        var attemptEv = new ProjectileReflectAttemptEvent(uid, component, false);
        RaiseLocalEvent(target, ref attemptEv);
        if (attemptEv.Cancelled)
        {
            SetShooter(uid, component, target);
            // The shooter's client stopped drawing it at the reflector.
            RemComp<PredictedProjectileComponent>(uid);
            return;
        }

        var damageEv = new BeforeProjectileHitEvent(component.Damage, target, component.Shooter);
        RaiseLocalEvent(uid, ref damageEv);
        var ev = new ProjectileHitEvent(damageEv.Damage * _damageableSystem.UniversalProjectileDamageModifier, target, component.Shooter);
        RaiseLocalEvent(uid, ref ev);

        var otherName = ToPrettyString(target);
        var damageRequired = _destructibleSystem.DestroyedAt(target);
        if (TryComp<DamageableComponent>(target, out var damageableComponent))
        {
            damageRequired -= _damageableSystem.GetTotalDamage((target, damageableComponent));
            damageRequired = FixedPoint2.Max(damageRequired, FixedPoint2.Zero);
        }

        if (_damageableSystem.TryChangeDamage((target, damageableComponent), ev.Damage, out var damage, component.IgnoreResistances, origin: component.Shooter))
        {
            if (!Deleted(target))
            {
                _color.RaiseEffect(Color.Red, new List<EntityUid> { target }, Filter.Pvs(target, entityManager: EntityManager));
            }

            var shotByString = Exists(component.Shooter)
                ? $"{ToPrettyString(component.Shooter!.Value):user}"
                : "a now deleted entity (grenade?)";

            _adminLogger.Add(LogType.BulletHit,
                LogImpact.Medium,
                $"Projectile {ToPrettyString(uid):projectile} shot by {shotByString} hit {otherName:target} and dealt {damage:damage} damage");


            component.ProjectileSpent = !TryPenetrate((uid, component), damage, damageRequired);
        }
        else
        {
            component.ProjectileSpent = true;
        }

        if (!Deleted(target))
        {
            _guns.PlayImpactSound(target, damage, component.SoundHit, component.ForceSound);

            if (!args.OurBody.LinearVelocity.IsLengthZero())
                _sharedCameraRecoil.KickCamera(target, args.OurBody.LinearVelocity.Normalized());
        }

        // The shooter's client predicted this first hit, so it drew the impact itself.
        PredictedShot? shot = null;
        NetEntity? hit = null;
        ICommonSession? shooterSession = null;
        if (TryComp<PredictedProjectileComponent>(uid, out var predicted)
            && TryComp<ActorComponent>(predicted.Shooter, out var actor))
        {
            shot = new PredictedShot(predicted.ShotId, predicted.Index);
            shooterSession = actor.PlayerSession;

            // Not there anymore if the hit destroyed it.
            TryGetNetEntity(target, out hit);
        }

        if (component.DeleteOnCollide && component.ProjectileSpent)
        {
            QueueDel(uid);
        }
        else
        {
            // The shooter's client stopped drawing it at the first hit.
            RemComp<PredictedProjectileComponent>(uid);
        }

        if (component.ImpactEffect != null && TryComp(uid, out TransformComponent? xform))
        {
            var coordinates = GetNetCoordinates(xform.Coordinates);
            var filter = Filter.Pvs(xform.Coordinates, entityMan: EntityManager);

            // Only the shooter is told which of their shots this was. Shot ids are counted per client, so another
            // client could have a shot with the same id and skip this impact thinking it already drew it.
            if (shooterSession != null && filter.Recipients.Contains(shooterSession))
            {
                filter.RemovePlayer(shooterSession);
                RaiseNetworkEvent(new ImpactEffectEvent(component.ImpactEffect, coordinates, shot, hit), shooterSession);
            }

            RaiseNetworkEvent(new ImpactEffectEvent(component.ImpactEffect, coordinates), filter);
        }
    }

    private bool TryPenetrate(Entity<ProjectileComponent> projectile, DamageSpecifier damage, FixedPoint2 damageRequired)
    {
        // If penetration is to be considered, we need to do some checks to see if the projectile should stop.
        if (projectile.Comp.PenetrationThreshold == 0)
            return false;

        // If a damage type is required, stop the bullet if the hit entity doesn't have that type.
        if (projectile.Comp.PenetrationDamageTypeRequirement != null)
        {
            foreach (var requiredDamageType in projectile.Comp.PenetrationDamageTypeRequirement)
            {
                if (damage.DamageDict.Keys.Contains(requiredDamageType))
                    continue;

                return false;
            }
        }

        // If the object won't be destroyed, it "tanks" the penetration hit.
        if (damage.GetTotal() < damageRequired)
        {
            return false;
        }

        if (!projectile.Comp.ProjectileSpent)
        {
            projectile.Comp.PenetrationAmount += damageRequired;
            // The projectile has dealt enough damage to be spent.
            if (projectile.Comp.PenetrationAmount >= projectile.Comp.PenetrationThreshold)
            {
                return false;
            }
        }

        return true;
    }
}
