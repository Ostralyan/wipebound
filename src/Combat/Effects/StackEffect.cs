using Godot;

namespace Wipebound.Combat;

/// <summary>
/// One big hit divided among whoever shares it, so the raid has to physically
/// gather. Nobody there means nobody splits it and everyone eats the whole thing.
/// </summary>
[GlobalClass]
public partial class StackEffect : AbilityEffect
{
    [Export] public float TotalDamage { get; set; } = 180f;

    public override void Resolve(EffectContext context)
    {
        if (context.Targets.Count == 0)
        {
            foreach (ICombatant candidate in context.Candidates)
                candidate.ApplyDamage(TotalDamage, context.Caster, $"{context.AbilityName} (nobody stacked)");
            return;
        }

        float share = TotalDamage / context.Targets.Count;
        foreach (ICombatant target in context.Targets)
            target.ApplyDamage(share, context.Caster, $"{context.AbilityName} (split {context.Targets.Count} ways)");
    }

    public override string Describe(EffectContext context)
        => context.Targets.Count == 0
            ? $"NOBODY STACKED: {TotalDamage} to all {context.Candidates.Count}"
            : $"Stack split {context.Targets.Count} ways: {TotalDamage / context.Targets.Count:0.#} each";
}
