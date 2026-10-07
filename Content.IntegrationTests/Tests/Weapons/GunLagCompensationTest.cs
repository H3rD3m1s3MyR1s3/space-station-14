using System.Numerics;
using Content.IntegrationTests.Tests.Interaction;
using Content.Shared.CCVar;
using Content.Shared.Damage.Systems;
using Content.Shared.FixedPoint;
using Content.Shared.Weapons.Ranged.Events;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests.Weapons;

/// <summary>
/// Checks that hitscan shots requested by a client hit where the client saw the target, not where it is now.
/// </summary>
public sealed class GunLagCompensationTest : InteractionTest
{
    protected override string PlayerPrototype => "MobHuman";
    private static readonly EntProtoId MobHuman = "MobHuman";
    private static readonly EntProtoId LaserGun = "WeaponLaserGun";

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
}
