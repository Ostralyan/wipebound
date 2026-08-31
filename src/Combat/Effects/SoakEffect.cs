using Godot;

namespace Wipebound.Combat;

/// <summary>
/// Inverted danger: the circle punishes the raid for being EMPTY.
///
/// Somebody has to volunteer to stand in it, which turns a dodge into a decision
/// somebody has to make out loud. If enough people soak, only they take the hit.
/// If not, everybody pays.
/// </summary>
[GlobalClass]
public partial class SoakEffect : AbilityEffect
{
    [Export] public int RequiredSoakers = 1;
    [Export] public float DamagePerSoaker = 20f;
    [Export] public float UnsoakedDamage = 60f;

    public override void Resolve(EffectContext context)
    {
        if (context.Inside.Count >= RequiredSoakers)
        {
            foreach (var hero in context.Inside)
                hero.ApplyDamage(DamagePerSoaker, $"{context.AbilityName} (soaked)");
            return;
        }

        foreach (var hero in context.Everyone)
            hero.ApplyDamage(UnsoakedDamage, $"{context.AbilityName} (unsoaked)");
    }

    public override string Describe(EffectContext context)
        => context.Inside.Count >= RequiredSoakers
            ? $"Soaked by {context.Inside.Count}/{RequiredSoakers}: {DamagePerSoaker} each"
            : $"UNSOAKED ({context.Inside.Count}/{RequiredSoakers}): {UnsoakedDamage} to all {context.Everyone.Count}";
}
