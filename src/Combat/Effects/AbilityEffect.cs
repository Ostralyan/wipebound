using System.Collections.Generic;
using Godot;

namespace Wipebound.Combat;

/// <summary>Everything an effect is allowed to know at resolve time.</summary>
public sealed class EffectContext
{
    public required string AbilityName { get; init; }

    /// Who cast it. Needed for damage attribution, for outgoing modifiers, and so
    /// effects can tell friend from foe without being told.
    public required ICombatant Caster { get; init; }

    public required TelegraphArea Area { get; init; }

    /// Living, affectable combatants whose server position fell inside the area.
    public required IReadOnlyList<ICombatant> Targets { get; init; }

    /// Every living, affectable combatant, inside the area or not. Soaks and wipes
    /// punish the ones who stayed out, so they need the wider set.
    public required IReadOnlyList<ICombatant> Candidates { get; init; }
}

/// <summary>
/// One consequence of an ability resolving.
///
/// An ability owns a LIST of these rather than a damage number, and that is the
/// difference between a dodge-em-up and a co-op game. Damage alone only ever asks
/// "did you move?". Soak and Stack ask players to agree with each other.
///
/// Effects run on the server only; they are reached exclusively from the cast
/// pipeline, which is itself server-side.
/// </summary>
[GlobalClass]
public partial class AbilityEffect : Resource
{
    public virtual void Resolve(EffectContext context) { }

    /// One line for the resolve log, so the server's reasoning is legible.
    public virtual string Describe(EffectContext context) => GetType().Name;
}
