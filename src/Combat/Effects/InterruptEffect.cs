using Godot;

namespace Wipebound.Combat;

/// <summary>
/// Stops a cast that is already in flight, and locks the caster out briefly.
///
/// The director has been able to cancel a cast since casts became objects; this is
/// the first thing that asks it to. That is the payoff of reifying them: an
/// interrupt is a list removal, not a special case threaded through the boss.
/// </summary>
[GlobalClass]
public partial class InterruptEffect : AbilityEffect
{
    /// Applied to whoever was interrupted, so an interrupt buys a window rather
    /// than merely delaying one cast by an instant.
    [Export] public string LockoutStatusId { get; set; } = StatusLibrary.Silenced;

    public override void Resolve(EffectContext context)
    {
        foreach (ICombatant target in context.Targets)
        {
            if (CombatDirector.Instance is null || !CombatDirector.Instance.IsCasting(target)) continue;

            CombatDirector.Instance.CancelFor(target);
            GD.Print($"[combat] {context.Caster.CombatName} interrupted {target.CombatName}");

            StatusEffect lockout = StatusLibrary.Get(LockoutStatusId);
            if (lockout is not null) target.Status.Apply(lockout, context.Caster, context.Now);
        }
    }

    public override string Describe(EffectContext context)
    {
        int casting = 0;
        foreach (ICombatant target in context.Targets)
            if (CombatDirector.Instance?.IsCasting(target) == true) casting++;

        return casting > 0 ? $"Interrupt {casting} mid-cast" : "Interrupt (nothing was casting)";
    }
}
