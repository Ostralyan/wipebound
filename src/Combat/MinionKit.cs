using Godot;
using Godot.Collections;

namespace Wipebound.Combat;

/// <summary>What summoned things can do. Ordinary abilities, cast through the director.</summary>
public static class MinionKit
{
    /// A swing that lands where the minion is standing when it swings. No
    /// telegraph: nobody is meant to dodge a melee hit, they are meant to kill the
    /// thing or move away from it.
    public static Ability Claw() => new()
    {
        Id = "claw",
        DisplayName = "Claw",
        Shape = TelegraphShape.Circle,
        Origin = AbilityOrigin.AtAimPoint,
        Radius = 1.8f,
        CastSeconds = 0f,
        Cooldown = 1.6f,
        Affects = TargetFilter.Enemies,
        ShowTelegraph = false,
        Effects = new Array<AbilityEffect> { new DamageEffect { Amount = 9f } },
    };
}
