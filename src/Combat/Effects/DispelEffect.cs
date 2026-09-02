using Godot;

namespace Wipebound.Combat;

/// <summary>
/// Strip statuses. Cleansing a debuff from an ally, or stealing the initiative
/// back by removing something the enemy relies on.
///
/// Dispelling deliberately does not fire expiry effects: a bomb that detonated
/// when cleansed would make cleansing it pointless, and "this one cannot be
/// removed" is what Dispellable is for.
/// </summary>
[GlobalClass]
public partial class DispelEffect : AbilityEffect
{
    [Export] public int Count { get; set; } = 1;

    /// True strips buffs (used on enemies); false strips debuffs (used on allies).
    [Export] public bool StripBeneficial { get; set; }

    public override void Resolve(EffectContext context)
    {
        foreach (ICombatant target in context.Targets)
        {
            int before = target.Status.Active.Count;
            // The removals themselves come from StatusTracker; this records who
            // did it and how much, which is a fact about the caster rather than
            // about any one status.
            target.Status.Dispel(StripBeneficial, Count, context.Now);

            Session.RunRecorder.Instance?.Log.Dispel(
                context.Now, context.Caster, target, context.AbilityName,
                before - target.Status.Active.Count);
        }
    }

    public override string Describe(EffectContext context)
        => $"Dispel {Count} {(StripBeneficial ? "buff" : "debuff")} from {context.Targets.Count}";
}
