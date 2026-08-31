using Godot;

namespace Wipebound.Combat;

public enum StackRule
{
    /// Reapplying resets the timer. The common case for buffs.
    Refresh = 0,

    /// Reapplying adds a stack up to MaxStacks and resets the timer.
    Stack = 1,

    /// Reapplying while already active does nothing at all.
    Ignore = 2,
}

/// <summary>
/// A timed modifier: buff, debuff, damage over time, crowd control.
///
/// Deliberately a flat bag of modifiers rather than a subclass hierarchy. Buffs
/// compose -- a haste and a slow and a shield are all live at once and their
/// numbers multiply together -- and modelling that with polymorphism produces a
/// combinatorial mess where every pair of buff types needs to know about the
/// other. A bag of numbers just multiplies.
///
/// The one thing a bag cannot express is behaviour over time, so periodic effects
/// reuse AbilityEffect: a damage-over-time is a DamageEffect on a one second
/// interval. That keeps one Strategy hierarchy in the codebase instead of two.
/// </summary>
[GlobalClass]
public partial class StatusEffect : Resource
{
    /// Identity for stacking and for replication -- the wire sends this, not the object.
    [Export] public string Id { get; set; } = "status";

    [Export] public string DisplayName { get; set; } = "Status";
    [Export] public float Duration { get; set; } = 5f;
    [Export] public StackRule Stacking { get; set; } = StackRule.Refresh;
    [Export] public int MaxStacks { get; set; } = 1;

    /// Drives the HUD colour. Players read good-vs-bad off this before they read
    /// the name, so keep it consistent with the telegraph palette.
    [Export] public Color Tint { get; set; } = Colors.White;
    [Export] public bool Beneficial { get; set; } = true;

    [ExportGroup("Modifiers (applied once per stack, multiplied together)")]
    [Export] public float MoveSpeedMultiplier { get; set; } = 1f;
    [Export] public float DamageTakenMultiplier { get; set; } = 1f;
    [Export] public float DamageDealtMultiplier { get; set; } = 1f;
    [Export] public float ManaRegenMultiplier { get; set; } = 1f;

    [ExportGroup("Control")]
    [Export] public bool Rooted { get; set; }
    [Export] public bool Silenced { get; set; }

    [ExportGroup("Periodic")]
    [Export] public float TickInterval { get; set; } = 1f;
    [Export] public Godot.Collections.Array<AbilityEffect> OnTick { get; set; } = new();
}
