using System.Numerics;
using Content.IntegrationTests.Tests.Interaction;
using Content.Shared.CCVar;
using Content.Shared.Damage.Systems;
using Content.Shared.FixedPoint;
using Content.Shared.Standing;
using Content.Shared.Weapons.Ranged.Events;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests.Weapons;

/// <summary>
/// Checks that hitscan shots and projectiles hit where the shooting client saw the target, not where it is now.
/// </summary>
public sealed class GunLagCompensationTest : InteractionTest
{
    protected override string PlayerPrototype => "MobHuman";
    private static readonly EntProtoId MobHuman = "MobHuman";
    private static readonly EntProtoId LaserGun = "WeaponLaserGun";
    private const string Shotgun = "GunLagCompensationTestShotgun";
    private static readonly EntProtoId Wall = "WallSolid";

    // Holds its ammo directly. Magazine guns need chambering first.
    [TestPrototypes]
    private const string Prototypes = $@"
- type: entity
  parent: BaseWeaponShotgun
  id: {Shotgun}
  components:
  - type: Sprite
    sprite: Objects/Weapons/Guns/Shotguns/db_shotgun.rsi
  - type: BallisticAmmoProvider
    proto: CartridgeRifle
";

    [Test]
    [TestCase(true, 2f)]
    [TestCase(false, 2f)]
    // A teleport rather than a walk, covering the reach prefilter and the snap for moves over 2 tiles.
    [TestCase(true, 5f)]
    public async Task HitscanUsesShooterView(bool lagCompensation, float moveDistance)
    {
        var damageSystem = SEntMan.System<DamageableSystem>();
        var xformSystem = SEntMan.System<SharedTransformSystem>();

        await AddAtmosphere(); // prevent the Urist from suffocating

        // Put the target a few tiles away in a straight line, so spread can't make us miss.
        var aimCoords = SEntMan.GetNetCoordinates(SEntMan.GetCoordinates(TargetCoords).Offset(new Vector2(2f, 0f)));
        var urist = await SpawnTarget(MobHuman, aimCoords);
        var gun = await PlaceInHands(LaserGun);

        await Pair.RunSeconds(2f); // Guns have a cooldown when picking them up.
        await SetCombatMode(true);

        await Server.WaitPost(() =>
        {
            Server.CfgMan.SetCVar(CCVars.CombatGunLagCompensation, lagCompensation);
            // Test clients have no ping, so allow for however many ticks the test itself takes.
            Server.CfgMan.SetCVar(CCVars.CombatGunLagCompensationSlack, 0.5f);
        });

        try
        {
            // The client was looking at a tick where the target stood still in the line of fire.
            var viewTick = GameTick.Zero;
            await Server.WaitPost(() => viewTick = STiming.CurTick);
            await RunTicks(2);

            await Server.WaitPost(() =>
            {
                var moved = SEntMan.GetCoordinates(aimCoords).Offset(new Vector2(0f, moveDistance));
                xformSystem.SetCoordinates(ToServer(urist), moved);
            });
            await RunTicks(1);

            // The client fires at where it last saw the target, like a lagging player would.
            await Client.WaitPost(() =>
            {
                CEntMan.RaisePredictiveEvent(new RequestShootEvent
                {
                    Gun = gun,
                    Coordinates = aimCoords,
                    ViewTick = viewTick,
                });
            });
            await RunTicks(10);

            var damage = damageSystem.GetTotalDamage(ToServer(urist));

            if (lagCompensation)
                Assert.That(damage, Is.GreaterThan(FixedPoint2.Zero), "Lag compensated shot missed where the shooter saw the target.");
            else
                Assert.That(damage, Is.EqualTo(FixedPoint2.Zero), "Shot without lag compensation hit a target that had already moved away.");
        }
        finally
        {
            await Server.WaitPost(() =>
            {
                Server.CfgMan.SetCVar(CCVars.CombatGunLagCompensation, CCVars.CombatGunLagCompensation.DefaultValue);
                Server.CfgMan.SetCVar(CCVars.CombatGunLagCompensationSlack, CCVars.CombatGunLagCompensationSlack.DefaultValue);
            });
        }
    }

    /// <summary>
    /// A client claiming to see a much older state than its ping allows must not be able to hit where the target was back then.
    /// </summary>
    [Test]
    public async Task RewindIsCappedByPing()
    {
        var damageSystem = SEntMan.System<DamageableSystem>();
        var xformSystem = SEntMan.System<SharedTransformSystem>();

        await AddAtmosphere();

        var aimCoords = SEntMan.GetNetCoordinates(SEntMan.GetCoordinates(TargetCoords).Offset(new Vector2(2f, 0f)));
        var urist = await SpawnTarget(MobHuman, aimCoords);
        var gun = await PlaceInHands(LaserGun);

        await Pair.RunSeconds(2f);
        await SetCombatMode(true);

        var viewTick = GameTick.Zero;
        await Server.WaitPost(() => viewTick = STiming.CurTick);
        await RunTicks(2);

        await Server.WaitPost(() =>
        {
            var moved = SEntMan.GetCoordinates(aimCoords).Offset(new Vector2(0f, 2f));
            xformSystem.SetCoordinates(ToServer(urist), moved);
        });

        // Past the default slack for a test client with no ping, but within the hard cap.
        await Pair.RunSeconds(0.3f);

        await Client.WaitPost(() =>
        {
            CEntMan.RaisePredictiveEvent(new RequestShootEvent
            {
                Gun = gun,
                Coordinates = aimCoords,
                ViewTick = viewTick,
            });
        });
        await RunTicks(10);

        Assert.That(damageSystem.GetTotalDamage(ToServer(urist)),
            Is.EqualTo(FixedPoint2.Zero),
            "Client was able to rewind further than its ping allows.");
    }

    /// <summary>
    /// A bullet that hits stops at the target, one that misses carries on along its path to the mob behind.
    /// </summary>
    [Test]
    [TestCase(true, 0f, 1.5f, true)]
    [TestCase(false, 0f, 1.5f, false)]
    // Bullets are 0.2 wide, so physics would hit a 0.35 radius mob up to 0.45 off the line of fire.
    [TestCase(true, 0.4f, 1.5f, true)]
    [TestCase(true, 0.55f, 1.5f, false)]
    // Teleported, which the bullet can't follow.
    [TestCase(true, 0f, 10f, false)]
    public async Task ProjectileUsesShooterView(bool lagCompensation, float graze, float moveDistance, bool hits)
    {
        var (damage, behindDamage) = await FireRifleAtMovedTarget(lagCompensation, lyingDown: false, graze: graze, moveDistance: moveDistance);

        Assert.Multiple(() =>
        {
            Assert.That(damage > FixedPoint2.Zero, Is.EqualTo(hits), "Bullet hit or missed the target wrongly.");
            Assert.That(behindDamage > FixedPoint2.Zero, Is.EqualTo(!hits), "Bullet didn't stop at the target or carry on past it.");
        });
    }

    /// <summary>
    /// Walls stay where they are now. A wall between the shooter and where they saw the target still stops the bullet.
    /// </summary>
    [Test]
    public async Task ProjectileStoppedByWallInFront()
    {
        var (damage, _) = await FireRifleAtMovedTarget(lagCompensation: true, lyingDown: false, wallInFront: true);

        Assert.That(damage, Is.EqualTo(FixedPoint2.Zero), "Lag compensated bullet went through a wall to hit the target.");
    }

    /// <summary>
    /// Bullets fly over mobs lying down unless aimed at them. That has to hold where the shooter saw the mob too, and
    /// the bullet has to carry on along its path to whatever is behind.
    /// </summary>
    [Test]
    public async Task ProjectileFliesOverLyingTarget()
    {
        var (damage, behindDamage) = await FireRifleAtMovedTarget(lagCompensation: true, lyingDown: true);

        Assert.Multiple(() =>
        {
            Assert.That(damage, Is.EqualTo(FixedPoint2.Zero), "Lag compensated bullet hit a mob lying down.");
            Assert.That(behindDamage, Is.GreaterThan(FixedPoint2.Zero), "Bullet didn't carry on past the mob lying down.");
        });
    }

    /// <summary>
    /// Fires a rifle from a client that last saw a target in the line of fire, after the target moved out of it.
    /// Another mob stands further along the line of fire.
    /// </summary>
    /// <param name="graze">How far off the line of fire the target was.</param>
    /// <param name="moveDistance">How far the target moved. Moves over 2 tiles snap on clients, like teleports.</param>
    /// <returns>Damage the target and the mob behind it took.</returns>
    private async Task<(FixedPoint2 Target, FixedPoint2 Behind)> FireRifleAtMovedTarget(bool lagCompensation,
        bool lyingDown,
        bool wallInFront = false,
        float graze = 0f,
        float moveDistance = 1.5f)
    {
        var damageSystem = SEntMan.System<DamageableSystem>();
        var xformSystem = SEntMan.System<SharedTransformSystem>();

        await AddAtmosphere(); // prevent the Urists from suffocating

        var aimCoords = SEntMan.GetNetCoordinates(SEntMan.GetCoordinates(TargetCoords).Offset(new Vector2(2f, 0f)));
        var targetCoords = SEntMan.GetCoordinates(aimCoords).Offset(new Vector2(0f, graze));
        var behindCoords = SEntMan.GetNetCoordinates(SEntMan.GetCoordinates(aimCoords).Offset(new Vector2(2f, 0f)));
        var urist = await SpawnTarget(MobHuman, SEntMan.GetNetCoordinates(targetCoords));
        var behind = await Spawn(MobHuman, behindCoords);

        if (wallInFront)
            await Spawn(Wall, SEntMan.GetNetCoordinates(SEntMan.GetCoordinates(aimCoords).Offset(new Vector2(-1f, 0f))));

        var gun = await PlaceInHands(Shotgun);

        await Pair.RunSeconds(2f); // Guns have a cooldown when picking them up.
        await SetCombatMode(true);

        if (lyingDown)
            await Server.WaitPost(() => SEntMan.System<StandingStateSystem>().Down(ToServer(urist)));

        await Server.WaitPost(() =>
        {
            Server.CfgMan.SetCVar(CCVars.CombatGunLagCompensation, lagCompensation);
            // Test clients have no ping, so allow for however many ticks the test itself takes.
            Server.CfgMan.SetCVar(CCVars.CombatGunLagCompensationSlack, 0.5f);
        });

        try
        {
            // The client was looking at a tick where the target stood still in the line of fire.
            var viewTick = GameTick.Zero;
            await Server.WaitPost(() => viewTick = STiming.CurTick);

            // Long enough for the bullet to reach the target before the shooter sees it move.
            await RunTicks(5);

            await Server.WaitPost(() =>
            {
                var moved = targetCoords.Offset(new Vector2(0f, moveDistance));
                xformSystem.SetCoordinates(ToServer(urist), moved);
            });
            await RunTicks(1);

            // The client fires at where it last saw the target, like a lagging player would.
            await Client.WaitPost(() =>
            {
                CEntMan.RaisePredictiveEvent(new RequestShootEvent
                {
                    Gun = gun,
                    Coordinates = aimCoords,
                    ViewTick = viewTick,
                });
            });

            // Long enough for the bullet to get well past the target.
            await RunTicks(15);

            return (damageSystem.GetTotalDamage(ToServer(urist)), damageSystem.GetTotalDamage(ToServer(behind)));
        }
        finally
        {
            await Server.WaitPost(() =>
            {
                Server.CfgMan.SetCVar(CCVars.CombatGunLagCompensation, CCVars.CombatGunLagCompensation.DefaultValue);
                Server.CfgMan.SetCVar(CCVars.CombatGunLagCompensationSlack, CCVars.CombatGunLagCompensationSlack.DefaultValue);
            });
        }
    }
}
