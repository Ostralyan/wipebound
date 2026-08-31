using Godot;

namespace Wipebound.Combat;

/// <summary>
/// Shove everyone inside away from the centre.
///
/// Worth understanding what this costs. Hero movement is client-authoritative, so
/// the server cannot simply move one -- it computes the destination, adopts it as
/// the validated position, and ASKS the owning client to slide there. A modified
/// client could refuse. In PvE that buys a cheater nothing but a lost mechanic,
/// and it is the price of instant, prediction-free dodging everywhere else.
///
/// Combatants that cannot be moved implement Displace as a no-op, so this is safe
/// to put on any ability.
/// </summary>
[GlobalClass]
public partial class KnockbackEffect : AbilityEffect
{
    [Export] public float Distance { get; set; } = 9f;
    [Export] public float TravelSeconds { get; set; } = 0.35f;

    /// Keeps targets on the map rather than launching them into the void.
    [Export] public float ArenaRadius { get; set; } = 44f;

    public override void Resolve(EffectContext context)
    {
        foreach (ICombatant target in context.Targets)
        {
            Vector3 away = target.CombatPosition - context.Area.Center;
            away.Y = 0f;

            // Dead centre has no "away", so pick something rather than a NaN.
            Vector3 direction = away.LengthSquared() > 0.0001f ? away.Normalized() : Vector3.Right;

            Vector3 destination = target.CombatPosition + direction * Distance;
            if (destination.Length() > ArenaRadius)
                destination = destination.Normalized() * ArenaRadius;

            target.Displace(destination, TravelSeconds);
        }
    }

    public override string Describe(EffectContext context)
        => $"Knock {context.Targets.Count} back {Distance}m";
}
