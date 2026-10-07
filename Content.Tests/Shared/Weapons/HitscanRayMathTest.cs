using System;
using System.Numerics;
using Content.Shared.Weapons.Hitscan;
using NUnit.Framework;
using Robust.Shared.Maths;
using Robust.Shared.Physics.Collision.Shapes;
using PhysicsTransform = Robust.Shared.Physics.Transform;

namespace Content.Tests.Shared.Weapons;

[Parallelizable]
[TestFixture]
[TestOf(typeof(HitscanRayMath))]
public sealed class HitscanRayMathTest
{
    private const float Tolerance = 0.001f;

    private static PolygonShape Box(float halfWidth, float halfHeight, float angle = 0f)
    {
        var box = new PolygonShape();
        box.SetAsBox(halfWidth, halfHeight, Vector2.Zero, angle);
        return box;
    }

    [Test]
    public void RotatedBoxHitsShapeNotAabb()
    {
        // A square rotated 45 degrees is a diamond reaching 0.707 along each axis, with an AABB of the same size.
        var diamond = Box(0.5f, 0.5f, MathF.PI / 4f);

        // At y = 0.6 the diamond spans x = +-0.107.
        Assert.That(HitscanRayMath.TryIntersectShape(new Vector2(-5f, 0.6f), Vector2.UnitX, diamond, PhysicsTransform.Empty, out var distance));
        Assert.That(distance, Is.EqualTo(5f - (MathF.Sqrt(0.5f) - 0.6f)).Within(Tolerance));

        // Passes through (0.6, 0.6), inside the AABB's corner but outside the diamond.
        var direction = Vector2.Normalize(new Vector2(1f, -1f));
        var origin = new Vector2(0.6f, 0.6f) - direction * 5f;
        Assert.That(HitscanRayMath.TryIntersectShape(origin, direction, diamond, PhysicsTransform.Empty, out _), Is.False);
    }

    [Test]
    public void BoxUsesBodyTransform()
    {
        // Wide box turned upright by the body's rotation, then moved away from the ray origin.
        var transform = new PhysicsTransform(new Vector2(10f, 0f), MathF.PI / 2f);

        Assert.That(HitscanRayMath.TryIntersectShape(Vector2.Zero, Vector2.UnitX, Box(1f, 0.25f), transform, out var distance));
        Assert.That(distance, Is.EqualTo(9.75f).Within(Tolerance));

        // The rotation makes the box 2 tall, so this only hits if the transform is applied.
        Assert.That(HitscanRayMath.TryIntersectShape(new Vector2(0f, 0.5f), Vector2.UnitX, Box(1f, 0.25f), transform, out _));
        Assert.That(HitscanRayMath.TryIntersectShape(new Vector2(0f, 1.5f), Vector2.UnitX, Box(1f, 0.25f), transform, out _), Is.False);
    }

    [Test]
    public void RotatedAabbShapeHitsShapeNotWorldAabb()
    {
        // Like a wall on a grid turned 45 degrees, making the unit box a diamond in world space.
        var shape = new PhysShapeAabb();
        var transform = new PhysicsTransform(Vector2.Zero, MathF.PI / 4f);

        Assert.That(HitscanRayMath.TryIntersectShape(new Vector2(-5f, 0f), Vector2.UnitX, shape, transform, out var distance));
        Assert.That(distance, Is.EqualTo(5f - MathF.Sqrt(0.5f)).Within(Tolerance));

        // Passes through (0.6, 0.6), inside the world AABB's corner but outside the diamond.
        var direction = Vector2.Normalize(new Vector2(1f, -1f));
        var origin = new Vector2(0.6f, 0.6f) - direction * 5f;
        Assert.That(HitscanRayMath.TryIntersectShape(origin, direction, shape, transform, out _), Is.False);
    }

    [Test]
    public void StartingInsideHitsAtZero()
    {
        Assert.That(HitscanRayMath.TryIntersectShape(Vector2.Zero, Vector2.UnitX, Box(0.5f, 0.5f), PhysicsTransform.Empty, out var boxDistance));
        Assert.That(boxDistance, Is.EqualTo(0f));

        Assert.That(HitscanRayMath.TryIntersectShape(Vector2.Zero, Vector2.UnitX, new PhysShapeCircle(0.5f), PhysicsTransform.Empty, out var circleDistance));
        Assert.That(circleDistance, Is.EqualTo(0f));
    }

    [Test]
    [TestCase(0.3f, true)]
    [TestCase(0.45f, true)]
    [TestCase(0.55f, false)]
    public void CircleHitDistance(float offset, bool shouldHit)
    {
        var circle = new PhysShapeCircle(0.5f, new Vector2(1f, 0f));
        var transform = new PhysicsTransform(new Vector2(4f, 0f), 0f);

        var hit = HitscanRayMath.TryIntersectShape(new Vector2(0f, offset), Vector2.UnitX, circle, transform, out var distance);

        Assert.That(hit, Is.EqualTo(shouldHit));
        if (shouldHit)
            Assert.That(distance, Is.EqualTo(5f - MathF.Sqrt(0.25f - offset * offset)).Within(Tolerance));
    }
}
