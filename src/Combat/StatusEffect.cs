using Godot;

namespace Wipebound.Combat;

/// <summary>Whether two different casters share one instance of a status, or hold their own.</summary>
public enum StatusScope
{
    /// One instance on the target no matter who applied it. Reapplying refreshes
    /// whatever is already there. Right for a target's own state -- a stun, a slow.
    Shared = 0,

    /// Every caster holds their own instance. Right for anything a caster OWNS: a
    /// damage-over-time that should credit them, or a mark that is theirs.
    ///
    /// This is the general case, which is why it is the substrate rather than the
    /// option. Per-source instances can express shared behaviour by declaring a
    /// status Shared; shared instances can never express per-caster anything.
    PerSource = 1,
}

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
    [Export] public StatusScope Scope { get; set; } = StatusScope.Shared;

    /// Whether a cleanse can strip this. Some things are meant to be endured.
    [Export] public bool Dispellable { get; set; } = true;

    /// Drives the HUD colour. Players read good-vs-bad off this before they read
    /// the name, so keep it consistent with the telegraph palette.
    [Export] public Color Tint { get; set; } = Colors.White;
    [Export] public bool Beneficial { get; set; } = true;

    [ExportGroup("Modifiers (applied once per stack, multiplied together)")]
    [Export] public float MoveSpeedMultiplier { get; set; } = 1f;
    [Export] public float DamageTakenMultiplier { get; set; } = 1f;
    [Export] public float DamageDealtMultiplier { get; set; } = 1f;
    [Export] public float ManaRegenMultiplier { get; set; } = 1f;

    [ExportGroup("Shield")]
    /// Damage this soaks before it breaks. A shield is a POOL with its own state,
    /// which is why it cannot be expressed as a damage-taken multiplier: mitigation
    /// scales every hit forever, absorption spends itself and disappears.
    [Export] public float AbsorbAmount { get; set; }

    [ExportGroup("Control")]
    [Export] public bool Rooted { get; set; }
    [Export] public bool Silenced { get; set; }

    [ExportGroup("Periodic and expiry")]
    [Export] public float TickInterval { get; set; } = 1f;
    [Export] public Godot.Collections.Array<AbilityEffect> OnTick { get; set; } = new();

    /// <summary>
    /// Runs once, when the status ends by running out of time.
    ///
    /// This is what turns a debuff from a number into a decision: "when this falls
    /// off it detonates" makes players do something before a deadline rather than
    /// simply endure a subtraction. Not run when a status is dispelled or cleared
    /// on death -- removing it early is precisely the point of removing it.
    /// </summary>
    [Export] public Godot.Collections.Array<AbilityEffect> OnExpire { get; set; } = new();

    /// Radius around the bearer that ticks and expiry reach. Zero means the bearer
    /// alone, which is the ordinary case for a damage-over-time.
    [Export] public float TickRadius { get; set; }
    [Export] public float ExpireRadius { get; set; }

    /// Who a radius picks up, relative to whoever applied the status.
    [Export] public TargetFilter AreaAffects { get; set; } = TargetFilter.Enemies;
}
