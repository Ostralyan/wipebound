using Godot;

namespace Wipebound.Combat;

/// <summary>
/// Move the CASTER toward where they aimed.
///
/// Every other movement effect in the game pushes someone else; this is the one
/// that moves you, and a kit built around WASD needs it. It is what makes a
/// defensive slot an actual decision -- a shield is "I will survive this", a
/// dash is "I will not be there".
///
/// Two limits matter. It never travels further than the cursor, so a dash cannot
/// overshoot the spot you pointed at and drop you somewhere worse; and with no
/// aim at all it does nothing rather than picking an arbitrary direction.
///
/// It goes through Displace, the same server-computes-then-asks-the-client path
/// as knockback, so it inherits that trade honestly: a modified client can refuse
/// to move. Refusing your own escape is not an exploit worth closing.
/// </summary>
[GlobalClass]
public partial class DashEffect : AbilityEffect
{
    [Export] public float Distance { get; set; } = 12f;
    [Export] public float TravelSeconds { get; set; } = 0.18f;

    /// Keeps the dash on the map rather than launching it into the void.
    [Export] public float ArenaRadius { get; set; } = 44f;

    public override void Resolve(EffectContext context)
    {
        // Area is a struct and always present. Belongs on an ability, where the
        // cast pipeline fills it from the cursor -- on a status tick there is no
        // footprint and its centre is the world origin, which is not a dash.
        Vector3 origin = context.Caster.CombatPosition;
        Vector3 toward = context.Area.Center - origin;
        toward.Y = 0f;

        // Aimed at your own feet: there is no direction to read, so do nothing.
        if (toward.LengthSquared() < 0.0001f) return;

        float travel = Mathf.Min(Distance, toward.Length());
        Vector3 destination = origin + toward.Normalized() * travel;

        var flat = new Vector2(destination.X, destination.Z);
        if (flat.Length() > ArenaRadius)
        {
            flat = flat.Normalized() * ArenaRadius;
            destination = new Vector3(flat.X, destination.Y, flat.Y);
        }

        context.Caster.Displace(destination, TravelSeconds);
    }

    public override string Describe(EffectContext context)
        => $"Dash {Distance}m toward the cursor";
}
