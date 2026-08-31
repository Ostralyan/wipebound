using Godot;

namespace Wipebound.Combat;

/// <summary>Hit everyone standing in it. The plain "get out of the fire" mechanic.</summary>
[GlobalClass]
public partial class DamageEffect : AbilityEffect
{
    [Export] public float Amount { get; set; } = 25f;

    public override void Resolve(EffectContext context)
    {
        foreach (ICombatant target in context.Targets)
            target.ApplyDamage(Amount, context.Caster, context.AbilityName);
    }

    public override string Describe(EffectContext context)
        => $"Damage {Amount} to {context.Targets.Count} inside";
}
