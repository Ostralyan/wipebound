using Godot;

namespace Wipebound.Combat;

/// <summary>Puts a timed modifier on everything the ability caught.</summary>
[GlobalClass]
public partial class ApplyStatusEffect : AbilityEffect
{
    /// Looked up in StatusLibrary. Set Definition instead to use an inspector-authored one.
    [Export] public string StatusId { get; set; } = "";

    [Export] public StatusEffect Definition { get; set; }

    private StatusEffect Resolved => Definition ?? StatusLibrary.Get(StatusId);

    public override void Resolve(EffectContext context)
    {
        StatusEffect definition = Resolved;
        if (definition is null)
        {
            GD.PushWarning($"ApplyStatusEffect on {context.AbilityName}: no status '{StatusId}'");
            return;
        }

        foreach (ICombatant target in context.Targets)
        {
            // Targets was snapshotted before the FIRST effect ran, so by the time
            // a later effect in the same list gets here some of them may be
            // corpses -- Rupture is {damage, Sundered, Burning}, and its damage
            // can kill. Every ApplyDamage and Heal already refuses the dead; this
            // was the one consequence that did not, which meant a status could be
            // hung on a body. Detonation has an expiry effect, so that body would
            // go off later.
            if (target is null || !target.IsAlive) continue;
            target.Status.Apply(definition, context.Caster, context.Now);
        }
    }

    public override string Describe(EffectContext context)
        => $"Apply {Resolved?.DisplayName ?? StatusId} to {context.Targets.Count}";
}
