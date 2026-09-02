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

            // Read back, not assumed. A refreshing or stacking status ends up at
            // a count and an expiry the definition alone does not predict, and a
            // replay drawing "three stacks" when there is one is worse than
            // drawing nothing.
            int stacks = 1;
            double expires = context.Now + definition.Duration;

            foreach (ActiveStatus live in target.Status.Active)
            {
                if (live.Definition.Id != definition.Id) continue;
                if (live.ExpiresAt < expires && stacks > 1) continue;
                stacks = live.Stacks;
                expires = live.ExpiresAt;
            }

            Session.RunRecorder.Instance?.Log.Aura(
                context.Now, applied: true, context.Caster, target,
                definition.DisplayName, stacks, expires - context.Now);
        }
    }

    public override string Describe(EffectContext context)
        => $"Apply {Resolved?.DisplayName ?? StatusId} to {context.Targets.Count}";
}
