using System.Numerics;
using Content.IntegrationTests.Tests.Interaction;
using Content.Shared.CombatMode;
using Content.Shared.Weapons.Ranged.Events;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests.Weapons;

/// <summary>
/// Checks that the shooter's client draws hitscan beams as soon as they fire, and only once.
/// </summary>
public sealed class HitscanPredictionTest : InteractionTest
{
    protected override string PlayerPrototype => "MobHuman";
    private static readonly EntProtoId MobHuman = "MobHuman";
    private static readonly EntProtoId LaserGun = "WeaponLaserGun";
    private static readonly EntProtoId HitscanEffect = "HitscanEffect";

    [Test]
    public async Task ShooterSeesBeamOnce()
    {
        await AddAtmosphere(); // prevent the Urist from suffocating

        var aimCoords = SEntMan.GetNetCoordinates(SEntMan.GetCoordinates(TargetCoords).Offset(new Vector2(2f, 0f)));
        await SpawnTarget(MobHuman, aimCoords);
        var gun = await PlaceInHands(LaserGun);

        await Pair.RunSeconds(2f); // Guns have a cooldown when picking them up.
        await SetCombatMode(true);

        // The client has to know it's in combat mode too, or it won't predict the shot.
        for (var i = 0; i < 20 && !CEntMan.GetComponent<CombatModeComponent>(CPlayer).IsInCombatMode; i++)
        {
            await RunTicks(1);
        }

        var predicted = 0;
        await Client.WaitPost(() =>
        {
            CEntMan.RaisePredictiveEvent(new RequestShootEvent
            {
                Gun = gun,
                ShotId = 1,
                Coordinates = aimCoords,
                ViewTick = CTiming.LastRealTick,
            });

            // The server hasn't even received the request yet.
            predicted = CountBeamEffects();
        });

        Assert.That(predicted, Is.GreaterThan(0), "Shooter's client didn't draw the beam when firing.");

        // Plenty of time for the server to process the shot and send effects, but less than the effects' lifetime.
        await RunTicks(10);

        Assert.That(CountBeamEffects(), Is.EqualTo(predicted), "Server sent the shooter a second copy of their own beam.");
    }

    private int CountBeamEffects()
    {
        var count = 0;
        var query = CEntMan.EntityQueryEnumerator<MetaDataComponent>();

        while (query.MoveNext(out var meta))
        {
            if (meta.EntityPrototype?.ID == HitscanEffect)
                count++;
        }

        return count;
    }
}
