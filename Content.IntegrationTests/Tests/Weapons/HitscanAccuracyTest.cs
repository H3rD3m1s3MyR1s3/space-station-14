using System.Numerics;
using Content.IntegrationTests.Tests.Interaction;
using Content.Shared.Damage.Systems;
using Content.Shared.FixedPoint;
using Content.Shared.Weapons.Hitscan.Events;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests.Weapons;

/// <summary>
/// Checks that hitscan shots hit the actual shape of a mob, not the box around it.
/// </summary>
public sealed class HitscanAccuracyTest : InteractionTest
{
    private static readonly EntProtoId MobHuman = "MobHuman";
    private static readonly EntProtoId RedLaser = "RedLaser";

    /// <summary>
    /// Fires diagonally past a human (fixture radius 0.35) at a sideways offset from its center.
    /// An offset of 0.45 is outside the circle but inside its AABB's corner, which used to count as a hit.
    /// </summary>
    [Test]
    [TestCase(0.25f, true)]
    [TestCase(0.45f, false)]
    public async Task DiagonalShotUsesShape(float offset, bool shouldHit)
    {
        var damageSystem = SEntMan.System<DamageableSystem>();
        var xformSystem = SEntMan.System<SharedTransformSystem>();

        await AddAtmosphere(); // prevent the Urist from suffocating

        var targetCoords = SEntMan.GetNetCoordinates(SEntMan.GetCoordinates(TargetCoords).Offset(new Vector2(2f, 2f)));
        var urist = await SpawnTarget(MobHuman, targetCoords);

        EntityUid? laser = null;
        await Server.WaitAssertion(() =>
        {
            var target = xformSystem.GetMapCoordinates(ToServer(urist));
            var direction = Vector2.Normalize(new Vector2(1f, 1f));
            var sideways = new Vector2(-direction.Y, direction.X);
            var from = target.Position - direction * 2f + sideways * offset;

            laser = SEntMan.Spawn(RedLaser);
            var ev = new HitscanTraceEvent
            {
                FromCoordinates = new EntityCoordinates(SEntMan.System<SharedMapSystem>().GetMap(target.MapId), from),
                ShotDirection = direction,
                Gun = SPlayer,
                Shooter = SPlayer,
            };
            SEntMan.EventBus.RaiseLocalEvent(laser.Value, ref ev);
        });
        await RunTicks(1);
        await Server.WaitPost(() => SEntMan.DeleteEntity(laser));

        var damage = damageSystem.GetTotalDamage(ToServer(urist));
        if (shouldHit)
            Assert.That(damage, Is.GreaterThan(FixedPoint2.Zero), "Shot through the mob's shape missed.");
        else
            Assert.That(damage, Is.EqualTo(FixedPoint2.Zero), "Shot outside the mob's shape hit its bounding box.");
    }
}
