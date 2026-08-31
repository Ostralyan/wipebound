using Godot;
using System.Collections.Generic;
using Wipebound.Player;

namespace Wipebound.Combat;

/// <summary>Everything an effect is allowed to know at resolve time.</summary>
public sealed class EffectContext
{
    public required string AbilityName { get; init; }
    public required TelegraphArea Area { get; init; }

    /// Living heroes whose validated position fell inside the area.
    public required IReadOnlyList<Hero> Inside { get; init; }

    /// Every living hero, inside or not. Soaks and wipes need this.
    public required IReadOnlyList<Hero> Everyone { get; init; }
}

/// <summary>
/// One consequence of a telegraph resolving.
///
/// An ability owns a LIST of these rather than a damage number, and that is the
/// difference between a dodge-em-up and a co-op game. Damage alone only ever asks
/// "did you move?". Soak and Stack ask players to agree with each other, which is
/// the reason this genre is played with friends.
///
/// Effects run on the server only; they are reached exclusively from Boss.Resolve.
/// </summary>
[GlobalClass]
public partial class AbilityEffect : Resource
{
    public virtual void Resolve(EffectContext context) { }

    /// One line for the resolve log, so the server's reasoning is legible.
    public virtual string Describe(EffectContext context) => GetType().Name;
}
