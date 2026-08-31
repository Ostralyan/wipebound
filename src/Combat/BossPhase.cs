using Godot;

namespace Wipebound.Combat;

/// <summary>
/// A stage of a fight: which mechanics are live, and how much breathing room sits
/// between them. Phases are what stop an encounter being one rotation on loop --
/// crossing a health threshold changes the question players are answering.
/// </summary>
[GlobalClass]
public partial class BossPhase : Resource
{
    [Export] public string Name { get; set; } = "Phase";

    /// The boss enters this phase when its health drops to or below this share of
    /// maximum. Phases are evaluated in order, so list them highest first.
    [Export(PropertyHint.Range, "0,100,1")]
    public float EntersAtHealthPercent { get; set; } = 100f;

    /// Quiet time after a mechanic resolves before the next may begin. Shrinking
    /// this in later phases is the cheapest way to raise pressure.
    [Export] public float RecoverySeconds { get; set; } = 2.0f;

    [Export] public Godot.Collections.Array<BossAbility> Abilities { get; set; } = new();
}
