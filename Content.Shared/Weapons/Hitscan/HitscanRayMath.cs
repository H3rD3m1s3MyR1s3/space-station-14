using System.Numerics;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Collision.Shapes;
using PhysicsTransform = Robust.Shared.Physics.Transform;

namespace Content.Shared.Weapons.Hitscan;

/// <summary>
/// Exact ray tests against fixture shapes.
/// </summary>
/// <remarks>
/// The broadphase raycast used for hitscan only tests fixture AABBs, which lets shots that visibly pass beside a
/// round mob still hit it, especially on diagonals. These narrow a hit down to the actual shape.
/// </remarks>
public static class HitscanRayMath
{
    /// <summary>
    /// Finds the closest point along a ray where it enters any hard fixture matching the mask.
    /// </summary>
    /// <param name="origin">World position the ray starts at.</param>
    /// <param name="direction">Normalized world direction of the ray.</param>
    /// <param name="maxDistance">Ignore hits further away than this.</param>
    /// <param name="mask">Collision mask of the ray, compared against each fixture's layer.</param>
    /// <param name="fixtures">The fixtures to test.</param>
    /// <param name="transform">World transform of the fixtures' owner.</param>
    /// <param name="distance">Distance from <paramref name="origin"/> to the hit.</param>
    public static bool TryIntersectFixtures(Vector2 origin,
        Vector2 direction,
        float maxDistance,
        int mask,
        FixturesComponent fixtures,
        PhysicsTransform transform,
        out float distance)
    {
        distance = float.MaxValue;

        foreach (var fixture in fixtures.Fixtures.Values)
        {
            // Same filter as the broadphase raycast.
            if (!fixture.Hard || (fixture.CollisionLayer & mask) == 0)
                continue;

            if (TryIntersectShape(origin, direction, fixture.Shape, transform, out var hit)
                && hit <= maxDistance
                && hit < distance)
            {
                distance = hit;
            }
        }

        return distance != float.MaxValue;
    }

    /// <summary>
    /// Finds where a ray enters a shape. Unsupported shapes fall back to their AABB.
    /// </summary>
    public static bool TryIntersectShape(Vector2 origin,
        Vector2 direction,
        IPhysShape shape,
        PhysicsTransform transform,
        out float distance)
    {
        switch (shape)
        {
            case PhysShapeCircle circle:
                return TryIntersectCircle(origin,
                    direction,
                    PhysicsTransform.Mul(transform, circle.Position),
                    circle.Radius,
                    out distance);
            case PolygonShape polygon:
                return TryIntersectPolygon(origin, direction, polygon, transform, out distance);
            case PhysShapeAabb aabb:
                // Only axis-aligned in the body's local space, which is rotated along with its grid.
                return TryIntersectAabb(PhysicsTransform.MulT(transform, origin),
                    PhysicsTransform.MulT(transform.Quaternion2D, direction),
                    aabb.LocalBounds,
                    out distance);
        }

        distance = float.MaxValue;
        var found = false;

        for (var i = 0; i < shape.ChildCount; i++)
        {
            if (TryIntersectAabb(origin, direction, shape.ComputeAABB(transform, i), out var hit) && hit < distance)
            {
                distance = hit;
                found = true;
            }
        }

        return found;
    }

    /// <summary>
    /// Ray against a circle. A ray starting inside the circle hits at distance 0.
    /// </summary>
    private static bool TryIntersectCircle(Vector2 origin, Vector2 direction, Vector2 center, float radius, out float distance)
    {
        distance = 0f;

        var offset = origin - center;
        var b = Vector2.Dot(offset, direction);
        var c = offset.LengthSquared() - radius * radius;

        // Outside the circle and pointing away from it.
        if (c > 0f && b > 0f)
            return false;

        var discriminant = b * b - c;
        if (discriminant < 0f)
            return false;

        distance = MathF.Max(0f, -b - MathF.Sqrt(discriminant));
        return true;
    }

    /// <summary>
    /// Ray against a convex polygon, clipping the ray against each edge.
    /// A ray starting inside the polygon hits at distance 0.
    /// </summary>
    private static bool TryIntersectPolygon(Vector2 origin,
        Vector2 direction,
        PolygonShape polygon,
        PhysicsTransform transform,
        out float distance)
    {
        distance = 0f;

        if (polygon.VertexCount < 3)
            return false;

        // Work in the polygon's local space so its outward edge normals can be used as they are.
        var localOrigin = PhysicsTransform.MulT(transform, origin);
        var localDirection = PhysicsTransform.MulT(transform.Quaternion2D, direction);

        var enter = 0f;
        var exit = float.MaxValue;

        for (var i = 0; i < polygon.VertexCount; i++)
        {
            var normal = polygon.Normals[i];

            // Positive while the origin is on the inner side of this edge.
            var numerator = Vector2.Dot(normal, polygon.Vertices[i] - localOrigin);
            var denominator = Vector2.Dot(normal, localDirection);

            if (MathF.Abs(denominator) < 1e-8f)
            {
                // Parallel to this edge and outside of it.
                if (numerator < 0f)
                    return false;

                continue;
            }

            var t = numerator / denominator;

            if (denominator < 0f)
                enter = MathF.Max(enter, t);
            else
                exit = MathF.Min(exit, t);

            if (enter > exit)
                return false;
        }

        distance = enter;
        return true;
    }

    /// <summary>
    /// Slab test for a ray against an axis-aligned box.
    /// </summary>
    private static bool TryIntersectAabb(Vector2 origin, Vector2 direction, Box2 aabb, out float distance)
    {
        var tMin = 0f;
        var tMax = float.MaxValue;
        distance = 0f;

        for (var axis = 0; axis < 2; axis++)
        {
            var o = axis == 0 ? origin.X : origin.Y;
            var d = axis == 0 ? direction.X : direction.Y;
            var min = axis == 0 ? aabb.Left : aabb.Bottom;
            var max = axis == 0 ? aabb.Right : aabb.Top;

            if (MathF.Abs(d) < 1e-8f)
            {
                // Parallel to this slab, so we either always or never overlap it.
                if (o < min || o > max)
                    return false;

                continue;
            }

            var t1 = (min - o) / d;
            var t2 = (max - o) / d;

            if (t1 > t2)
                (t1, t2) = (t2, t1);

            tMin = MathF.Max(tMin, t1);
            tMax = MathF.Min(tMax, t2);

            if (tMin > tMax)
                return false;
        }

        distance = tMin;
        return true;
    }
}
