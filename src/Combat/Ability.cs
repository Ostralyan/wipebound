using Godot;

namespace Wipebound.Combat;

/// <summary>Where the footprint sits, relative to the caster and the aim point.</summary>
public enum AbilityOrigin
{
    /// Lands on the aim point. Circles, donuts, ground-targeted spells.
    AtAimPoint = 0,

    /// Centred on the caster and ignoring the aim point. Slams, self buffs, auras.
    AtCaster = 1,

    /// Starts at the caster and points at the aim. Cones, lanes, beams.
    FromCasterTowardAim = 2,
}

/// <summary>How an NPC caster chooses its aim point. Players supply their own.</summary>
public enum AiTargeting
{
    RandomEnemy = 0,
    NearestEnemy = 1,
    FarthestEnemy = 2,
    ArenaCentre = 3,
    Self = 4,
}

/// <summary>
/// One ability, as data -- and the same class whether a boss or a player casts it.
///
/// That symmetry is the point. A boss mechanic and a player spell are the same
/// object: a footprint, a wind-up, a cost, and a list of consequences. The only
/// difference is where the aim point comes from, which is why <see cref="AiTargeting"/>
/// exists but is simply ignored when a human supplies one.
///
/// [GlobalClass], so these can be authored as .tres in the inspector. The starting
/// content is built in code (DefaultEncounter, PlayerKit) because a fight reads
/// better as a list of named mechanics than as a serialised blob.
/// </summary>
[GlobalClass]
public partial class Ability : Resource
{
    [Export] public string Id { get; set; } = "ability";
    [Export] public string DisplayName { get; set; } = "Ability";

    [ExportGroup("Cost and timing")]
    [Export] public float ManaCost { get; set; }
    [Export] public float Cooldown { get; set; } = 1f;

    /// Wind-up before it resolves. This is the telegraph duration, and the
    /// difficulty dial for boss mechanics. Zero means it resolves immediately.
    [Export] public float CastSeconds { get; set; } = 2f;

    /// Maximum distance from caster to aim point. Zero means unlimited.
    [Export] public float Range { get; set; }

    [ExportGroup("Footprint")]
    [Export] public TelegraphShape Shape { get; set; } = TelegraphShape.Circle;
    [Export] public AbilityOrigin Origin { get; set; } = AbilityOrigin.AtAimPoint;

    /// Outer radius, or the length of a cone or rectangle.
    [Export] public float Radius { get; set; } = 8f;

    /// Donuts only: the safe hole in the middle.
    [Export] public float InnerRadius { get; set; } = 4f;

    /// Cones only: total opening angle.
    [Export] public float ConeAngleDegrees { get; set; } = 70f;

    /// Rectangles only: half the width of the lane.
    [Export] public float RectHalfWidth { get; set; } = 3f;

    [ExportGroup("Targeting")]
    [Export] public TargetFilter Affects { get; set; } = TargetFilter.Enemies;
    [Export] public AiTargeting AiTargeting { get; set; } = AiTargeting.RandomEnemy;

    [ExportGroup("Presentation")]
    /// Instant self-buffs generally should not draw a warning circle.
    [Export] public bool ShowTelegraph { get; set; } = true;
    [Export] public Color TelegraphColor { get; set; } = new(0.95f, 0.32f, 0.26f);

    [ExportGroup("Consequences")]
    [Export] public Godot.Collections.Array<AbilityEffect> Effects { get; set; } = new();

    /// <summary>
    /// Freeze the footprint. Called once, when the cast begins, and the result never
    /// changes again -- a telegraph that keeps tracking a moving caster renders
    /// somewhere different from where the server resolves it.
    /// </summary>
    public TelegraphArea BuildArea(Vector3 casterPosition, Vector3 aimPoint)
    {
        float halfAngle = Mathf.DegToRad(ConeAngleDegrees) * 0.5f;
        Vector3 centre = Origin == AbilityOrigin.AtAimPoint ? aimPoint : casterPosition;
        float facing = 0f;

        if (Origin == AbilityOrigin.FromCasterTowardAim)
        {
            Vector3 toAim = aimPoint - casterPosition;
            toAim.Y = 0f;

            // Godot's forward is -Z, so this is the yaw whose forward points at the aim.
            facing = toAim.LengthSquared() > 0.0001f ? Mathf.Atan2(-toAim.X, -toAim.Z) : 0f;
        }

        return new TelegraphArea(Shape, centre, facing, Radius, InnerRadius, halfAngle, RectHalfWidth);
    }
}
