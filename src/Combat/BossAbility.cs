using Godot;

namespace Wipebound.Combat;

public enum TargetingRule
{
    /// Anyone. The standard "spread out" pressure.
    RandomPlayer = 0,

    /// Whoever is closest -- stands in for a threat table until there is one.
    NearestPlayer = 1,

    /// Punishes hanging back at range.
    FarthestPlayer = 2,

    /// A fixed hazard that ignores where anyone is standing.
    ArenaCenter = 3,

    /// Centred on the boss: cleaves, ground slams, "get out of melee".
    BossPosition = 4,
}

/// <summary>
/// One boss mechanic, as data.
///
/// This is the file that decides whether boss number eight costs an afternoon or a
/// week. Everything specific to a mechanic -- its footprint, how long the warning
/// lasts, who it chases, what it does on landing -- lives here rather than in
/// Boss.cs, so the encounter loop never needs editing to add another one.
///
/// Marked [GlobalClass], so these can be authored as .tres files in the inspector
/// once you would rather tune numbers than rebuild. DefaultEncounter.cs builds the
/// starting set in code because reading it is a better introduction than a blob.
/// </summary>
[GlobalClass]
public partial class BossAbility : Resource
{
    [Export] public string DisplayName { get; set; } = "Ability";

    [ExportGroup("Footprint")]
    [Export] public TelegraphShape Shape { get; set; } = TelegraphShape.Circle;

    /// Outer radius, or the length of a cone or rectangle.
    [Export] public float Radius { get; set; } = 8f;

    /// Donuts only: the safe hole in the middle.
    [Export] public float InnerRadius { get; set; } = 4f;

    /// Cones only: total opening angle.
    [Export] public float ConeAngleDegrees { get; set; } = 70f;

    /// Rectangles only: half the width of the lane.
    [Export] public float RectHalfWidth { get; set; } = 3f;

    [ExportGroup("Timing")]
    /// How long players have to read it and react. This is the difficulty dial.
    [Export] public float TelegraphSeconds { get; set; } = 2.2f;

    [Export] public float Cooldown { get; set; } = 10f;

    [ExportGroup("Targeting")]
    [Export] public TargetingRule Targeting { get; set; } = TargetingRule.RandomPlayer;

    [ExportGroup("Presentation")]
    [Export] public Color TelegraphColor { get; set; } = new(0.95f, 0.32f, 0.26f);

    [ExportGroup("Consequences")]
    [Export] public Godot.Collections.Array<AbilityEffect> Effects { get; set; } = new();

    /// <summary>
    /// Freeze the footprint. Circles and donuts land ON the target; cones and
    /// rectangles start at the boss and point AT it. Called once, at cast start,
    /// and the result never changes again.
    /// </summary>
    public TelegraphArea BuildArea(Vector3 bossPosition, Vector3 targetPoint)
    {
        float halfAngle = Mathf.DegToRad(ConeAngleDegrees) * 0.5f;

        if (Shape is TelegraphShape.Circle or TelegraphShape.Donut)
            return new TelegraphArea(Shape, targetPoint, 0f, Radius, InnerRadius, halfAngle, RectHalfWidth);

        Vector3 toTarget = targetPoint - bossPosition;
        toTarget.Y = 0f;

        // Godot's forward is -Z, so this is the yaw whose forward points at the target.
        float facing = toTarget.LengthSquared() > 0.0001f
            ? Mathf.Atan2(-toTarget.X, -toTarget.Z)
            : 0f;

        return new TelegraphArea(Shape, bossPosition, facing, Radius, InnerRadius, halfAngle, RectHalfWidth);
    }
}
