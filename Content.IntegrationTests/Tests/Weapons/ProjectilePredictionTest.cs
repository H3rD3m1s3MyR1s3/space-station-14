using System.Numerics;
using System.Threading;
using Content.IntegrationTests.Tests.Interaction;
using Content.Shared.CombatMode;
using Content.Shared.Projectiles;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;
using Robust.Client.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests.Weapons;

/// <summary>
/// Checks that the shooter's client draws projectiles as soon as they fire, and hides the server's copies of them.
/// </summary>
public sealed class ProjectilePredictionTest : InteractionTest
{
    protected override string PlayerPrototype => "MobHuman";
    private const string Shotgun = "ProjectilePredictionTestShotgun";
    private const string Rifle = "ProjectilePredictionTestRifle";
    private const string PiercingBullet = "ProjectilePredictionTestPiercingBullet";
    private const string PiercingCartridge = "ProjectilePredictionTestPiercingCartridge";
    private const string PiercingRifle = "ProjectilePredictionTestPiercingRifle";
    private const string Reflector = "ProjectilePredictionTestReflector";
    private const string Fragile = "ProjectilePredictionTestFragile";
    private static readonly EntProtoId VisualPrototype = "ClientsideClone";

    // Guns that hold their ammo directly. Magazine guns warn when the client predicts chambering the next round.
    [TestPrototypes]
    private const string Prototypes = $@"
- type: entity
  parent: BaseWeaponShotgun
  id: {Shotgun}
  components:
  - type: Sprite
    sprite: Objects/Weapons/Guns/Shotguns/db_shotgun.rsi

- type: entity
  parent: {Shotgun}
  id: {Rifle}
  components:
  - type: BallisticAmmoProvider
    proto: CartridgeRifle

- type: entity
  parent: BaseBullet
  id: {PiercingBullet}
  components:
  - type: Projectile
    damage:
      types:
        Piercing: 5
    penetrationThreshold: 1000

- type: entity
  parent: CartridgeRifle
  id: {PiercingCartridge}
  components:
  - type: CartridgeAmmo
    proto: {PiercingBullet}

- type: entity
  parent: {Shotgun}
  id: {PiercingRifle}
  components:
  - type: BallisticAmmoProvider
    proto: {PiercingCartridge}

# Sends every bullet straight back.
- type: entity
  id: {Reflector}
  components:
  - type: Physics
    bodyType: Static
  - type: Fixtures
    fixtures:
      fix1:
        shape: !type:PhysShapeAabb
          bounds: ""-0.4,-0.4,0.4,0.4""
        hard: true
        layer:
        - Impassable
  - type: Reflect
    reflectProb: 1
    spread: 0

# Destroyed by any hit, so piercing bullets carry on through it.
- type: entity
  id: {Fragile}
  components:
  - type: Physics
    bodyType: Static
  - type: Fixtures
    fixtures:
      fix1:
        shape: !type:PhysShapeAabb
          bounds: ""-0.4,-0.4,0.4,0.4""
        hard: true
        layer:
        - Impassable
  - type: Damageable
  - type: Injurable
  - type: Destructible
    thresholds:
    - trigger:
        !type:DamageTrigger
        damage: 1
      behaviors:
      - !type:DoActsBehavior
        acts: [ ""Destruction"" ]
";

    // Shot ids count up on each client, and clients are reused between tests.
    private static int _lastShotId;

    [Test]
    [TestCase(Rifle, 1)]
    [TestCase(Shotgun, 6)]
    public async Task ShooterSeesProjectilesOnce(string gunPrototype, int projectiles)
    {
        await AddAtmosphere(); // prevent the Urist from suffocating

        var gun = await PlaceInHands(gunPrototype);
        await Pair.RunSeconds(2f); // Guns have a cooldown when picking them up.
        await EnterCombatMode();

        // Aim at nothing, so the projectiles are still flying when the server's copies arrive.
        var aimCoords = SEntMan.GetNetCoordinates(SEntMan.GetCoordinates(TargetCoords).Offset(new Vector2(10f, 0f)));

        var predicted = 0;
        await Client.WaitPost(() =>
        {
            CEntMan.RaisePredictiveEvent(new RequestShootEvent
            {
                Gun = gun,
                ShotId = Interlocked.Increment(ref _lastShotId),
                Coordinates = aimCoords,
                ViewTick = CTiming.LastRealTick,
            });

            // The server hasn't even received the request yet.
            predicted = CountPredictedVisuals();
        });

        Assert.That(predicted, Is.EqualTo(projectiles), "Shooter's client didn't draw every projectile when firing.");

        await RunTicks(10);

        var tagged = 0;
        await Server.WaitPost(() =>
        {
            var query = SEntMan.EntityQueryEnumerator<PredictedProjectileComponent>();
            while (query.MoveNext(out var comp))
            {
                if (comp.Shooter == SPlayer)
                    tagged++;
            }
        });

        Assert.That(tagged, Is.EqualTo(projectiles), "Server didn't mark the shooter's projectiles as predicted.");

        var arrived = 0;
        var visible = 0;
        await Client.WaitPost(() =>
        {
            var query = CEntMan.EntityQueryEnumerator<PredictedProjectileComponent, SpriteComponent>();
            while (query.MoveNext(out _, out var sprite))
            {
                arrived++;
                if (sprite.Visible)
                    visible++;
            }
        });

        Assert.Multiple(() =>
        {
            Assert.That(arrived, Is.EqualTo(projectiles), "Server's projectiles didn't reach the shooter's client.");
            Assert.That(visible, Is.Zero, "Shooter saw the server's copy of a projectile their client already drew.");
        });
    }

    /// <summary>
    /// After a lag spike the server can run a shot on a later tick than the client predicted it on. The shooter must
    /// still only see their own copy of the shot.
    /// </summary>
    [Test]
    [TestCase(Rifle, 1)]
    [TestCase(Shotgun, 6)]
    public async Task LateShotIsStillMatched(string gunPrototype, int projectiles)
    {
        await AddAtmosphere(); // prevent the Urist from suffocating

        var gun = await PlaceInHands(gunPrototype);
        await Pair.RunSeconds(2f); // Guns have a cooldown when picking them up.
        await EnterCombatMode();

        var aimCoords = SEntMan.GetNetCoordinates(SEntMan.GetCoordinates(TargetCoords).Offset(new Vector2(10f, 0f)));

        // Let the server get ahead, so the request arrives for a tick the server has already run.
        await Server.WaitRunTicks(5);

        var requestTick = GameTick.Zero;
        await Client.WaitPost(() =>
        {
            requestTick = CTiming.CurTick;
            CEntMan.RaisePredictiveEvent(new RequestShootEvent
            {
                Gun = gun,
                ShotId = Interlocked.Increment(ref _lastShotId),
                Coordinates = aimCoords,
                ViewTick = CTiming.LastRealTick,
            });
        });

        await RunTicks(10);

        var late = 0;
        await Server.WaitPost(() =>
        {
            var query = SEntMan.EntityQueryEnumerator<PredictedProjectileComponent, MetaDataComponent>();
            while (query.MoveNext(out var comp, out var meta))
            {
                if (comp.Shooter == SPlayer && meta.CreationTick > requestTick)
                    late++;
            }
        });

        var arrived = 0;
        var visible = 0;
        await Client.WaitPost(() =>
        {
            var query = CEntMan.EntityQueryEnumerator<ProjectileComponent, SpriteComponent, TransformComponent>();
            while (query.MoveNext(out _, out _, out var sprite, out var xform))
            {
                // Templates the client copies predicted projectiles from, not projectiles in the world.
                if (xform.MapID == MapId.Nullspace)
                    continue;

                arrived++;
                if (sprite.Visible)
                    visible++;
            }
        });

        Assert.Multiple(() =>
        {
            Assert.That(late, Is.EqualTo(projectiles), "Server didn't run the shot later than the client predicted it.");
            Assert.That(arrived, Is.EqualTo(projectiles), "Server's projectiles didn't reach the shooter's client.");
            Assert.That(visible, Is.Zero, "Shooter saw the server's copy of a projectile their client already drew.");
        });
    }

    [Test]
    public async Task UnpredictedShotsStayVisible()
    {
        await AddAtmosphere(); // prevent the Urist from suffocating

        await PlaceInHands(Rifle);
        await Pair.RunSeconds(2f); // Guns have a cooldown when picking them up.

        // Fired by the server alone, like a turret or an admin forcing a shot.
        await AttemptShoot(SEntMan.GetNetCoordinates(SEntMan.GetCoordinates(TargetCoords).Offset(new Vector2(10f, 0f))));
        await RunTicks(10);

        var projectiles = 0;
        var hidden = 0;
        await Client.WaitPost(() =>
        {
            var query = CEntMan.EntityQueryEnumerator<ProjectileComponent, SpriteComponent, TransformComponent>();
            while (query.MoveNext(out var uid, out _, out var sprite, out var xform))
            {
                // Templates the client copies predicted projectiles from, not projectiles in the world.
                if (xform.MapID == MapId.Nullspace)
                    continue;

                projectiles++;
                if (!sprite.Visible || CEntMan.HasComponent<PredictedProjectileComponent>(uid))
                    hidden++;
            }
        });

        Assert.Multiple(() =>
        {
            Assert.That(projectiles, Is.GreaterThan(0), "Server's projectile didn't reach the client.");
            Assert.That(hidden, Is.Zero, "A projectile the client never predicted was hidden.");
        });
    }

    /// <summary>
    /// The shooter's client stops drawing its bullet at the reflector, so it has to see the reflected one.
    /// </summary>
    [Test]
    public async Task ReflectedProjectileIsShown()
    {
        await AddAtmosphere(); // prevent the Urist from suffocating

        var reflectorCoords = SEntMan.GetNetCoordinates(SEntMan.GetCoordinates(TargetCoords).Offset(new Vector2(2f, 0f)));
        await Spawn(Reflector, reflectorCoords);

        Assert.That(await FireAndWatchForRevealedProjectile(Rifle, reflectorCoords),
            "Shooter never saw their bullet after it was reflected.");
    }

    /// <summary>
    /// The shooter's client stops drawing its bullet at the first thing it hits, so it has to see a bullet that passes
    /// through and carries on.
    /// </summary>
    [Test]
    public async Task PiercingProjectileIsShown()
    {
        await AddAtmosphere(); // prevent the Urist from suffocating

        var fragileCoords = SEntMan.GetNetCoordinates(SEntMan.GetCoordinates(TargetCoords).Offset(new Vector2(2f, 0f)));
        var fragile = await Spawn(Fragile, fragileCoords);

        var revealed = await FireAndWatchForRevealedProjectile(PiercingRifle, fragileCoords);

        Assert.Multiple(() =>
        {
            Assert.That(revealed, "Shooter never saw their bullet after it passed through what it hit.");
            Assert.That(SEntMan.Deleted(ToServer(fragile)), "Bullet didn't break what it hit, so it couldn't pass through.");
        });
    }

    /// <summary>
    /// Fires a gun from the client and watches for one of the server's projectiles to be shown to the shooter.
    /// </summary>
    private async Task<bool> FireAndWatchForRevealedProjectile(string gunPrototype, NetCoordinates aimCoords)
    {
        var gun = await PlaceInHands(gunPrototype);
        await Pair.RunSeconds(2f); // Guns have a cooldown when picking them up.
        await EnterCombatMode();

        await Client.WaitPost(() =>
        {
            CEntMan.RaisePredictiveEvent(new RequestShootEvent
            {
                Gun = gun,
                ShotId = Interlocked.Increment(ref _lastShotId),
                Coordinates = aimCoords,
                ViewTick = CTiming.LastRealTick,
            });
        });

        // Reflected bullets come straight back and hit the shooter, so look every tick rather than once at the end.
        var revealed = false;
        for (var i = 0; i < 20 && !revealed; i++)
        {
            await RunTicks(1);
            await Client.WaitPost(() =>
            {
                var query = CEntMan.EntityQueryEnumerator<ProjectileComponent, SpriteComponent, TransformComponent>();
                while (query.MoveNext(out var uid, out _, out var sprite, out var xform))
                {
                    // Templates the client copies predicted projectiles from, not projectiles in the world.
                    if (xform.MapID == MapId.Nullspace)
                        continue;

                    if (sprite.Visible && !CEntMan.HasComponent<PredictedProjectileComponent>(uid))
                        revealed = true;
                }
            });
        }

        return revealed;
    }

    private async Task EnterCombatMode()
    {
        await SetCombatMode(true);

        // The client has to know it's in combat mode too, or it won't predict the shot.
        for (var i = 0; i < 20 && !CEntMan.GetComponent<CombatModeComponent>(CPlayer).IsInCombatMode; i++)
        {
            await RunTicks(1);
        }
    }

    private int CountPredictedVisuals()
    {
        var count = 0;
        var query = CEntMan.EntityQueryEnumerator<MetaDataComponent>();

        while (query.MoveNext(out var meta))
        {
            if (meta.EntityPrototype?.ID == VisualPrototype)
                count++;
        }

        return count;
    }
}
