using Godot;

namespace Wipebound.Combat;

/// <summary>
/// One big hit divided among whoever shares it, so the raid has to physically
/// gather. Nobody there means nobody splits it and everyone eats the whole thing.
/// </summary>
[GlobalClass]
public partial class StackEffect : AbilityEffect
{
    [Export] public float TotalDamage = 180f;

    public override void Resolve(EffectContext context)
    {
        if (context.Inside.Count == 0)
        {
            foreach (var hero in context.Everyone)
                hero.ApplyDamage(TotalDamage, $"{context.AbilityName} (nobody stacked)");
            return;
        }

        float share = TotalDamage / context.Inside.Count;
        foreach (var hero in context.Inside)
            hero.ApplyDamage(share, $"{context.AbilityName} (split {context.Inside.Count} ways)");
    }

    public override string Describe(EffectContext context)
        => context.Inside.Count == 0
            ? $"NOBODY STACKED: {TotalDamage} to all {context.Everyone.Count}"
            : $"Stack split {context.Inside.Count} ways: {TotalDamage / context.Inside.Count:0.#} each";
}
