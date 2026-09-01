using Godot;

namespace Wipebound.Combat;

/// <summary>
/// Puts enemies on the field.
///
/// Costs almost nothing because a minion is an ICombatant: every telegraph,
/// effect, status and hazard already applies to it, and it casts through the same
/// director as everything else.
/// </summary>
[GlobalClass]
public partial class SummonEffect : AbilityEffect
{
    [Export] public int Count { get; set; } = 3;

    /// Scatter radius around the ability's footprint, so they do not stack up.
    [Export] public float Spread { get; set; } = 4f;

    [Export] public float Health { get; set; } = 90f;

    /// What the summoned things come for. Different summons can behave differently
    /// in the same encounter, which is more interesting than one global rule.
    [Export] public TargetRule Targeting { get; set; } = TargetRule.Nearest;

    public override void Resolve(EffectContext context)
    {
        for (int i = 0; i < Count; i++)
        {
            float angle = Mathf.Tau * i / Mathf.Max(1, Count);
            Vector3 at = context.Area.Center + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * Spread;
            CombatDirector.Instance?.SpawnMinion(at, Health, Targeting);
        }
    }

    public override string Describe(EffectContext context) => $"Summon {Count}";
}
