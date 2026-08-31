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
    [Export] public int RequiredSoakers { get; set; } = 1;
    [Export] public float DamagePerSoaker { get; set; } = 20f;
    [Export] public float UnsoakedDamage { get; set; } = 60f;

    public override void Resolve(EffectContext context)
    {
        if (context.Targets.Count >= RequiredSoakers)
        {
            foreach (ICombatant target in context.Targets)
                target.ApplyDamage(DamagePerSoaker, context.Caster, $"{context.AbilityName} (soaked)");
            return;
        }

        foreach (ICombatant candidate in context.Candidates)
            candidate.ApplyDamage(UnsoakedDamage, context.Caster, $"{context.AbilityName} (unsoaked)");
    }

    public override string Describe(EffectContext context)
        => context.Targets.Count >= RequiredSoakers
            ? $"Soaked by {context.Targets.Count}/{RequiredSoakers}: {DamagePerSoaker} each"
            : $"UNSOAKED ({context.Targets.Count}/{RequiredSoakers}): {UnsoakedDamage} to all {context.Candidates.Count}";
}
