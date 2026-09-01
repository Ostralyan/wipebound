using Godot;

namespace Wipebound.Combat;

/// <summary>
/// Leaves the ability's own footprint on the ground as a lasting hazard.
///
/// Taking the area from the resolving cast rather than declaring its own is what
/// makes "the fire lands where the fire was telegraphed" true by construction
/// instead of by two numbers agreeing.
/// </summary>
[GlobalClass]
public partial class SpawnHazardEffect : AbilityEffect
{
    [Export] public Hazard Definition { get; set; }

    public override void Resolve(EffectContext context)
    {
        if (Definition is null) return;
        CombatDirector.Instance?.SpawnHazard(context.Caster, Definition, context.Area, context.Now);
    }

    public override string Describe(EffectContext context)
        => $"Leave {Definition?.DisplayName ?? "hazard"} for {Definition?.Duration ?? 0f}s";
}
