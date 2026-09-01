using Godot;

namespace Wipebound.Combat;

/// <summary>
/// Ground that stays dangerous.
///
/// Every mechanic before this was instantaneous: a warning, a deadline, a
/// resolution. That can only ever ask "were you standing there at one particular
/// moment". A hazard asks a different and complementary question -- where can you
/// stand at all -- and answering it repeatedly is what turns an arena into a
/// shrinking one.
///
/// It reuses the footprint of whatever ability spawned it, so a cone hazard and a
/// donut hazard are free, and its consequences are ordinary AbilityEffects.
/// </summary>
[GlobalClass]
public partial class Hazard : Resource
{
    [Export] public string Id { get; set; } = "hazard";
    [Export] public string DisplayName { get; set; } = "Hazard";
    [Export] public float Duration { get; set; } = 10f;
    [Export] public float TickInterval { get; set; } = 1f;
    [Export] public TargetFilter Affects { get; set; } = TargetFilter.Enemies;
    [Export] public Color Tint { get; set; } = new(0.98f, 0.45f, 0.15f);
    [Export] public Godot.Collections.Array<AbilityEffect> OnTick { get; set; } = new();
}
