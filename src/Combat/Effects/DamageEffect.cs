using Godot;

namespace Wipebound.Combat;

/// <summary>Hit everyone standing in it. The plain "get out of the fire" mechanic.</summary>
[GlobalClass]
public partial class DamageEffect : AbilityEffect
{
    [Export] public float Amount = 25f;

    public override void Resolve(EffectContext context)
    {
        foreach (var hero in context.Inside)
            hero.ApplyDamage(Amount, context.AbilityName);
    }

    public override string Describe(EffectContext context)
        => $"Damage {Amount} to {context.Inside.Count} inside";
}
