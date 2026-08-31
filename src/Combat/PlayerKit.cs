using Godot;
using Godot.Collections;

namespace Wipebound.Combat;

/// <summary>
/// What a hero can do. Built in code for the same reason the encounter is: a kit
/// reads better as a short list of named abilities than as a directory of .tres.
/// Assign Hero.Kit in the inspector to override.
/// </summary>
public static class PlayerKit
{
    public static Array<Ability> Build() => new()
    {
        // Slot 0 (Q). Instant, no telegraph, no cost -- the thing you hold down.
        // A cone rather than a magic hitscan, so facing matters and so it goes
        // through exactly the same footprint machinery as a boss mechanic.
        new Ability
        {
            Id = "strike",
            DisplayName = "Strike",
            Shape = TelegraphShape.Cone,
            Origin = AbilityOrigin.FromCasterTowardAim,
            Radius = 6f,
            ConeAngleDegrees = 100f,
            CastSeconds = 0f,
            Cooldown = 0.9f,
            ManaCost = 0f,
            Affects = TargetFilter.Enemies,
            ShowTelegraph = false,
            Effects = new Array<AbilityEffect> { new DamageEffect { Amount = 14f } },
        },
    };
}
