using Godot;

namespace Wipebound.Combat;

/// <summary>
/// Shove everyone inside away from the centre.
///
/// Worth understanding what this costs. Movement is client-authoritative, so the
/// server cannot simply move a hero -- it computes the destination, adopts it as
/// the validated position, and ASKS the owning client to slide there. A modified
/// client could refuse. In PvE that buys a cheater nothing but a lost mechanic,
/// and it is the price of instant, prediction-free dodging everywhere else.
/// </summary>
[GlobalClass]
public partial class KnockbackEffect : AbilityEffect
{
    [Export] public float Distance = 9f;
    [Export] public float TravelSeconds = 0.35f;

    /// Keeps players on the map rather than launching them into the void.
    [Export] public float ArenaRadius = 44f;

    public override void Resolve(EffectContext context)
    {
        foreach (var hero in context.Inside)
        {
            Vector3 away = hero.ServerPosition - context.Area.Center;
            away.Y = 0f;

            // Dead centre has no "away", so pick something rather than a NaN.
            Vector3 direction = away.LengthSquared() > 0.0001f ? away.Normalized() : Vector3.Right;

            Vector3 destination = hero.ServerPosition + direction * Distance;
            if (destination.Length() > ArenaRadius)
                destination = destination.Normalized() * ArenaRadius;

            hero.ServerPush(destination, TravelSeconds);
        }
    }

    public override string Describe(EffectContext context)
        => $"Knock {context.Inside.Count} back {Distance}m";
}
