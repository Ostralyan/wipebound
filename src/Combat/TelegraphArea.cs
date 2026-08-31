using Godot;

namespace Wipebound.Combat;

public enum TelegraphShape
{
    Circle = 0,
    Donut = 1,
    Cone = 2,
    Rectangle = 3,
}

/// <summary>
/// The frozen footprint of one telegraph: where it is, how big, which way it points.
///
/// TWO THINGS MAKE THIS FILE LOAD-BEARING.
///
/// First, it is frozen. The area is computed once when the cast begins and never
/// updated. A telegraph parented to a moving boss renders somewhere different from
/// where the server resolves it, because the boss's position on a client is both a
/// ping late and interpolated between updates. Even a frontal cone snapshots the
/// boss's position and facing at cast start and then stops listening.
///
/// Second, the shape is defined ONCE, as the signed field in <see cref="Field"/>,
/// and the shader in TelegraphView.cs evaluates the identical expression. If the
/// drawn edge and the tested edge disagree by even a little, players standing on
/// the boundary get hit by nothing and learn to distrust every telegraph in the
/// game. Change one, change the other, and check the pair against each other.
/// </summary>
public readonly struct TelegraphArea
{
    public readonly TelegraphShape Shape;

    /// Circles and donuts sit ON the centre. Cones and rectangles START at it and
    /// extend along Facing.
    public readonly Vector3 Center;

    /// Yaw in radians. Ignored by circles and donuts.
    public readonly float Facing;

    /// Outer radius, or the length of a cone/rectangle.
    public readonly float Radius;

    public readonly float InnerRadius;
    public readonly float HalfAngle;
    public readonly float HalfWidth;

    public TelegraphArea(TelegraphShape shape, Vector3 center, float facing, float radius,
                         float innerRadius = 0f, float halfAngle = 0f, float halfWidth = 0f)
    {
        Shape = shape;
        Center = new Vector3(center.X, 0f, center.Z);
        Facing = facing;
        Radius = radius;
        InnerRadius = innerRadius;
        HalfAngle = halfAngle;
        HalfWidth = halfWidth;
    }

    /// Godot's forward is -Z, so a node at yaw t points along this.
    public static Vector3 ForwardFor(float yaw) => new(-Mathf.Sin(yaw), 0f, -Mathf.Cos(yaw));
    public static Vector3 RightFor(float yaw) => new(Mathf.Cos(yaw), 0f, -Mathf.Sin(yaw));

    /// <summary>
    /// Negative inside, zero on the boundary, positive outside, in world units.
    /// Composite shapes are intersections of half-spaces, so they compose with max().
    /// </summary>
    public float Field(Vector3 point)
    {
        Vector3 offset = new(point.X - Center.X, 0f, point.Z - Center.Z);
        float distance = offset.Length();

        switch (Shape)
        {
            case TelegraphShape.Circle:
                return distance - Radius;

            case TelegraphShape.Donut:
                return Mathf.Max(distance - Radius, InnerRadius - distance);

            case TelegraphShape.Cone:
            {
                // Everything within a hair of the apex counts as inside, which also
                // keeps the angle term from dividing by a vanishing length.
                if (distance < 0.02f) return -Radius;

                Vector3 forward = ForwardFor(Facing);
                float cosAngle = Mathf.Clamp(offset.Dot(forward) / distance, -1f, 1f);
                float angleExcess = Mathf.Acos(cosAngle) - HalfAngle;

                // Scale the angular error by distance so it lands in world units and
                // the outline stays a constant thickness on screen.
                return Mathf.Max(distance - Radius, angleExcess * distance);
            }

            case TelegraphShape.Rectangle:
            {
                float along = offset.Dot(ForwardFor(Facing));
                float across = offset.Dot(RightFor(Facing));
                return Mathf.Max(along - Radius, Mathf.Max(-along, Mathf.Abs(across) - HalfWidth));
            }

            default:
                return 1f;
        }
    }

    public bool Contains(Vector3 point) => Field(point) <= 0f;

    /// Radius of a circle centred on Center that covers the whole shape. Sizes the
    /// quad the shader draws on.
    public float BoundingRadius => Shape == TelegraphShape.Rectangle
        ? Mathf.Sqrt(Radius * Radius + HalfWidth * HalfWidth)
        : Radius;

    // -- wire format ------------------------------------------------------
    // RPCs cannot carry a custom struct, and eleven positional floats would be
    // unreadable at both ends, so it travels as a small named dictionary.

    public Godot.Collections.Dictionary ToDictionary() => new()
    {
        ["shape"] = (int)Shape,
        ["cx"] = Center.X,
        ["cz"] = Center.Z,
        ["facing"] = Facing,
        ["radius"] = Radius,
        ["inner"] = InnerRadius,
        ["half_angle"] = HalfAngle,
        ["half_width"] = HalfWidth,
    };

    public static TelegraphArea FromDictionary(Godot.Collections.Dictionary data) => new(
        (TelegraphShape)(int)data["shape"],
        new Vector3((float)data["cx"], 0f, (float)data["cz"]),
        (float)data["facing"],
        (float)data["radius"],
        (float)data["inner"],
        (float)data["half_angle"],
        (float)data["half_width"]);
}
