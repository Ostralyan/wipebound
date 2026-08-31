using Godot;

namespace Wipebound.Combat;

/// <summary>Restores health to everything the ability caught.</summary>
[GlobalClass]
public partial class HealEffect : AbilityEffect
{
    [Export] public float Amount { get; set; } = 25f;

    public override void Resolve(EffectContext context)
    {
        foreach (ICombatant target in context.Targets)
            target.Heal(Amount, context.Caster, context.AbilityName);
    }

    public override string Describe(EffectContext context)
        => $"Heal {Amount} to {context.Targets.Count}";
}
